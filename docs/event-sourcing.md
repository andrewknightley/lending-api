# Credit agreement event sourcing

## Boundaries and storage contract

Only CreditAgreement is event sourced. Customers, accounts and products remain relational. Controllers still handle HTTP, command handlers orchestrate, domain aggregates decide and emit facts, and SQL sessions own locking, expected-version appends and transactions. Queries and health/docs endpoints are not commands.

Each agreement owns a stream identified by CreditAgreementId. AgreementStream reserves its unique business number and records the head version. AgreementEvent stores EventId, CreditAgreementId, StreamVersion (starting at 1), a stable EventType, SchemaVersion (currently 1), OccurredAtUtc and JSON Payload. Stable names are explicitly mapped, never resolved from arbitrary CLR type names. Payload enums are strings, timestamps are UTC and amounts/rates are decimal numbers. Unknown types/schema versions, gaps, mixed identities, invalid event order or a mismatched head fail closed.

- AgreementCreated: identity, agreement number, account/product IDs, agreement type, accepted amount/rate/term and creation time (OccurredAtUtc).
- AgreementActivated: agreement identity and activation time (OccurredAtUtc).
- AgreementImported: the same accepted terms plus known status, original creation/activation timestamps, and the separate import time. It records known state, not a reconstructed historical narrative.

Event schema version is independent of stream version. Adding/changing durable contracts requires an explicit schema migration and deserializer/upcaster design. Never edit historical payloads. Replay does not query current eligibility, use a clock, perform I/O or emit new events. Future business validation must remain on the command path, not be added to replay. Snapshots are deferred until stream length warrants them.

## Consistency and compatibility

A single SQL transaction covers current eligibility locks, event append and synchronous projection update. Stream head updates use an expected version predicate; event stream positions are unique. Two concurrent activation requests with the same ETag still yield one success and one 412. A current-token repeat activation appends nothing. Failure or disposal before commit rolls back head, events and projection. The existing API does not automatically retry writes.

HTTP ETags and the `rowVersion` response remain eight-byte SQL rowversion tokens from the projection. They do not encode stream versions. This deliberately preserves clients' If-Match contract. Command decisions come exclusively from replayed events; projection columns are only a read model and HTTP concurrency token. An existing row without a stream fails activation with a logged 500 diagnostic until the explicit migration is run. Missing/corrupt event history is never repaired from the projection automatically.

The SQL immutability trigger rejects UPDATE/DELETE of event rows; primary/unique/check constraints protect IDs, versions and JSON syntax. This is protection against accidental mutation, not cryptographic tamper evidence against a privileged database administrator. Runtime database access should be restricted to required tables; appends must go through the session transaction rather than arbitrary SQL. No external publication is performed, so there is no outbox or message broker in this slice.

## Coordinated rollout

1. Stop the old API and any scripts/jobs that write agreements. Back up the selected development database before migration.
2. Review and publish the companion lending-sql-infrastructure DACPAC. The change adds AgreementStream, AgreementEvent and an immutability trigger; CreditAgreement remains in place. Schema publication does not automatically run a data import.
3. Run `scripts/Import.LegacyAgreements.sql` from that repo against the selected Lending database. The script is transactional/repeatable, imports only rows without streams and leaves projections/rowversions unchanged. Existing events are not overwritten. Development seed scripts or demo SQL must run BEFORE import; any later direct inserts need import before use. A fresh empty database needs no import.
4. Verify every existing agreement has a stream and one AgreementImported baseline for migrated rows, with accepted terms/status/timestamps intact.
5. Start this API, verify readiness, then create/get/activate/repeat with the sample HTTP workflow. Run the real SQL integration suite against its disposable database.

Do not run old and new agreement writers side by side. Do not roll back to the old API after event-sourced writes: it would update only the projection and diverge from event history. Recovery after cutover needs an explicit, reviewed roll-forward or coordinated database restore, not an application-only downgrade. Structural changes remain owned by the SQL project; `database/schema.sql` is only a disposable test fixture. API CI checks the new SQL definitions/import script against the pinned companion infrastructure commit.

## Rebuilding one projection

Stop agreement writers for maintenance. With the API's normal local connection configuration, run:

```powershell
dotnet run --project src/Lending.Api -- --rebuild-agreement-projection <agreement-guid>
```

This maintenance command replays the stream, deletes/reinserts only that agreement's projection in one transaction and writes no events. It preserves stream heads and number reservations. It can restore a missing projection or correct corrupted projection values, provided referenced relational account/product rows still exist. It issues a new rowversion, so clients must GET again before their next write. No HTTP maintenance endpoint is exposed. An all-stream rebuild can enumerate AgreementStream IDs and invoke the same operation with writers paused; it is not an automatic startup action.

## Verification and scope

Domain tests cover event production, replay, invalid sequences, import states and no duplicate activation events. Real SQL tests cover persisted ordering, rollback, stream identity and number reservation, immutable history, projection rebuild, replay independent of altered projection terms, and explicit/repeatable baseline import. Existing HTTP/SQL tests retain ETag, precision, UTC, eligibility and concurrency checks.

Deferred: event sourcing other entities; cancellation/settlement commands; snapshots; asynchronous projections; event publication/outbox; generalized request idempotency; repayment, interest accrual and accounting ledger behaviour. There is no implied financial ledger or complete historical audit before the migration baseline. Existing data-retention/access controls still need design before production use.
