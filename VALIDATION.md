# Validation record — 9 October 2026

Executed with .NET SDK 10.0.401 / runtime 10.0.12 on Linux.

| Check | Result |
| --- | --- |
| Solution build | Passed, 0 warnings and 0 errors |
| Release domain tests | 28 passed |
| Release HTTP/application tests using fake persistence | 19 passed |
| Real SQL integration tests | 8 compiled, skipped; not validated against a running engine |
| Release API publish | Passed |
| Published API startup and HTTP smoke check | Passed: liveness endpoint and OpenAPI, including the activation `If-Match` parameter |

The final HTTP suite includes the activation endpoint's explicit `If-Match` header binding. Tests verify request validation, accepted terms, eligibility, create/activate responses, stale versions, repeat activation and read contracts. These do not validate SQL execution or database locking.

The local SQL Server engine could not start in this execution environment, including an attempt with a checksum-verified official SQL Server 2025 package. No successful real SQL integration run is claimed. Run the SQL Express instructions in README.md before considering database behavior verified. The supplied CI workflow requires those eight tests, but has not been executed on GitHub.

No deployment, production database changes, or public hosting were performed. PowerShell wrappers and the GitHub workflow were reviewed but not executed in PowerShell/GitHub here. The API requires .NET 10; it was not opened in a Windows IDE during these checks.
