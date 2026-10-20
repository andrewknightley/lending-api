# Lending API

A small .NET 10 API over the existing `Lending` SQL Server database. This is the first agreement workflow: discover customers/accounts/products, create a pending credit agreement, then activate it using optimistic concurrency. It targets the `lending` schema recovered from the lending-sql-infrastructure source archive; compare that schema with any subsequent changes in your local SQL project before running it.

## Build and run on your machine

Install the .NET 10 SDK and use a .NET 10-compatible IDE or the CLI. Extract this folder beside `lending-sql-infrastructure`, for example `C:\Git\Repos\ak-lending-platform\lending-api`. Your existing SQL project continues to own database deployment; this API does not deploy or migrate your database.

From this folder in PowerShell:

```powershell
dotnet restore Lending.sln
dotnet build Lending.sln --configuration Release
dotnet user-secrets set "ConnectionStrings:Lending" 'Server=.\SQLEXPRESS;Database=Lending;Integrated Security=True;Encrypt=True;TrustServerCertificate=True' --project src/Lending.Api
dotnet run --project src/Lending.Api
```

The development launch profile listens at `http://localhost:5080`. Windows integrated authentication uses the identity running the API, which needs permission to read the four lending tables and insert/update agreements. The certificate exception above is for your local SQL Express instance; use a trusted certificate and normal certificate validation for hosted environments. Alternatively set `ConnectionStrings__Lending` as an environment variable. Do not commit credentials to appsettings.json.

- Liveness: `http://localhost:5080/health/live`
- SQL readiness: `http://localhost:5080/health/ready`
- Development OpenAPI JSON: `http://localhost:5080/openapi/v1.json`

Open `Lending.Api.http` in a compatible HTTP client, or use Postman. First list accounts and credit products, then copy their real IDs into the request. Your existing seed data is sufficient. `database/demo-data.sql` is optional illustrative data with fixed IDs used by the sample requests; run it only if wanted, against your selected Lending database. It inserts missing demo records and does not overwrite existing ones. Do not run `database/schema.sql` against an existing database: that file is for a new disposable test database only.

## Endpoints

| Method | Route | Purpose |
| --- | --- | --- |
| GET | `/api/customers` | List customer summaries |
| GET | `/api/customers/{id}` | Customer summary |
| GET | `/api/accounts?customerId={id}` | List accounts, optionally filtered |
| GET | `/api/accounts/{id}` | Account summary |
| GET | `/api/credit-products?available=true` | List products, optionally filtered |
| POST | `/api/accounts/{accountId}/credit-agreements` | Create a pending agreement |
| GET | `/api/credit-agreements/{id}` | Agreement and current ETag |
| GET | `/api/accounts/{accountId}/credit-agreements` | Account agreements |
| POST | `/api/credit-agreements/{id}/activate` | Activate with `If-Match` |

Lists accept `offset` (default 0) and `pageSize` (default 50, maximum 100), returning `items`, `offset`, `pageSize`, and `hasMore`. Reference responses are intentionally small summaries rather than every database field.

Example create body:

```json
{
  "agreementNumber": "AGR-2026-0001",
  "creditProductId": "<an existing product GUID>",
  "agreementType": "FixedLoan",
  "agreedAmount": 10000,
  "annualInterestRatePercent": 7.5,
  "termMonths": 36
}
```

Creation returns `201`, a `Location` header and an `ETag`. The body includes the agreement ID and a base64 `rowVersion`. Activation requires the exact quoted ETag in `If-Match`, for example `If-Match: "AAAAAAAAB9E="`. Treat it as an opaque value; fetch the current agreement to obtain it. Missing headers return 428, malformed headers 400, and stale versions 412. After a stale response, reload and decide whether activation is still appropriate; do not blindly retry.

Validation returns 400, missing references 404, and conflicting eligibility/status or duplicate agreement numbers 409. Selected SQL availability/deadlock failures return 503 without retrying writes automatically. Error bodies use Problem Details. Repeating activation with the *current* ETag on an active agreement returns the unchanged agreement; repeating with its old ETag returns 412.

## Business decisions in this first version

- Creation requires an Active customer, Open GBP account, and available product of the requested type.
- Amount is positive with at most four decimal places; annual interest percentage is non-negative with at most six. Both must fit the existing SQL decimal columns. No affordability, age, regulatory, or underwriting assessment is implemented: these eligibility checks are initial platform rules.
- FixedLoan requires a positive term; RevolvingCredit requires a null term. Agreement numbers are unique, trimmed printable ASCII strings of 1–30 characters.
- Accepted amount/rate/term are stored on the agreement. They are not recalculated from the product on activation. Activation rechecks account/customer eligibility but does not recheck product availability or reprice accepted terms.
- Pending agreements can become Active. Settling, cancelling, amending terms, customer/account writes, and interest calculation are outside this slice.

## Design and the future event store

`Lending.Domain` contains the agreement, immutable accepted terms, and business rules without SQL dependencies. `Lending.Application` coordinates the workflow through persistence interfaces. `Lending.Infrastructure` loads/saves SQL records and manages transactions. `Lending.Api` handles HTTP input, ETags and responses. Read endpoints use a separate query interface and SQL DTO queries; commands and queries currently share the same database.

Creation locks the eligibility rows for its transaction so their statuses cannot change between validation and insert. Activation locks the agreement, checks the supplied SQL rowversion, evaluates business rules, and updates with a rowversion predicate. Changes and eligibility checks commit together. The database constraints remain a second line of protection. Cross-row locking and transaction boundaries live in infrastructure, while domain decisions remain independently testable.

This separation allows us to retain much of the business behavior if writes later append `AgreementCreated` and `AgreementActivated` events. It is not an event store implementation or a drop-in persistence swap: we must introduce event-producing aggregates, event replay, stream-version concurrency, migration decisions and read-model projections. SQL rowversion would then protect SQL updates where needed; event-stream versions would protect event appends. There is currently no event publication, outbox, saga or event-sourced history. Unique agreement numbers prevent duplicate numbers, but do not provide a general HTTP idempotency-key contract.

## Tests

```powershell
dotnet test Lending.sln --configuration Release
```

This runs domain and in-process HTTP/application tests. Eight real SQL integration tests are skipped unless a test connection is configured. HTTP tests replace persistence with a fake; they do not prove that SQL statements execute correctly.

To run all tests against your local SQL Express, use a login allowed to create and drop test databases:

```powershell
$env:LENDING_TEST_SQL_CONNECTION_STRING = 'Server=.\SQLEXPRESS;Database=master;Integrated Security=True;Encrypt=True;TrustServerCertificate=True'
$env:LENDING_REQUIRE_SQL_TESTS = '1'
dotnet test Lending.sln --configuration Release
Remove-Item Env:LENDING_REQUIRE_SQL_TESTS
Remove-Item Env:LENDING_TEST_SQL_CONNECTION_STRING
```

The connection must explicitly target `master`. The fixture creates a unique `LendingApiTests_<guid>` database, loads the exact schema and synthetic data, then drops only that generated database. It does not use your Lending database. A forcibly interrupted test run can leave its generated database behind. Use an isolated test server in CI. Tests cover real HTTP/SQL round trips, decimal/UTC mapping, duplicate numbers, concurrent activation, rollback, term preservation, query/readiness behavior, database constraints, and eligibility locking.

PowerShell 7 wrappers are available as `scripts/Build.ps1` and `scripts/Test.ps1`; `Test.ps1 -RequireSql` makes missing SQL configuration a failure. Equivalent dotnet commands above work without PowerShell 7.

## CI and deployment

`.github/workflows/ci.yml` restores, builds and tests on pushes and pull requests. It starts a disposable SQL Server 2022 Developer service, requires the SQL tests, and uploads test results. Successful main-branch runs also publish and upload the API binaries. The hard-coded SQL password belongs only to the disposable test service and is not a deployment credential. The supplied workflow has not yet run in your GitHub repository.

If this API remains a subfolder of the platform repo, move the workflow to the repository's root `.github/workflows` folder and set `defaults.run.working-directory: lending-api`; update upload paths to `lending-api/artifacts/api/` and `lending-api/TestResults/*.trx`. As packaged, paths assume this folder is the GitHub repository root.

`dotnet publish src/Lending.Api --configuration Release --output artifacts/api` builds deployable API files; it does not deploy the SQL schema or host the API. A future release pipeline should deploy/review the SQL DACPAC separately, configure the API's database connection through environment/secrets, deploy the API artifact, and verify readiness. A GitHub-hosted runner cannot reach your desktop's SQL Express instance without additional connectivity; do not point hosted CI at it.

This deliverable is for local development. Authentication/authorization, deployment infrastructure and hosted access controls are not implemented. The default profile binds to loopback and no public deployment has been performed. Add access controls before exposing it beyond your machine. No production loan data or credentials are included.

## Validation and AI assistance

See `VALIDATION.md` for the checks actually executed and outstanding SQL validation. Source and tests were generated with assistance from OpenAI Codex and reviewed through builds and the recorded test runs.
