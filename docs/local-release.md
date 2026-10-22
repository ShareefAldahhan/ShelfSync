# ShelfSync local release

## Start

Prerequisites: PowerShell 7, .NET 10 SDK, Node.js 24, PostgreSQL 18. An existing development database must contain `database/schema.sql` and optional fictional `database/seed.sql`. Do not reapply schema to an existing populated database.

1. Configure `ConnectionStrings:ShelfSync` in .NET user secrets for `backend/ShelfSync.Api`. The connection is already configured on the original development machine. Use an application role with only required read/write permissions.
2. Run `pwsh -File scripts/Set-Operator.ps1`. Choose a password with at least 12 characters. Only its ASP.NET Identity password hash is saved to user secrets; nothing is committed. Default username: `operator`.
3. Run `pwsh -File scripts/Start-Local.ps1 -Build`.
4. Open **http://localhost:5000** and sign in. The published backend serves the dashboard; a separate frontend process is unnecessary.

Normal restart: `pwsh -File scripts/Start-Local.ps1`. Rebuild after editing source. Stop with Ctrl+C. The script binds only to localhost and uses Development so local HTTP session cookies work. Production requires HTTPS.

## Demo (three minutes)

1. Inventory: reserve one unit of a product with stock. The returned receipt identifies the order and its expiry.
2. Orders: pick, then fulfill. Shelf stock moves to picked, then leaves picked stock.
3. Reserve another unit and cancel it. Repeat with report shortage and resolve shortage when stock permits.
4. POS Sales: record one unit sold; inspect the refreshed shelf quantity.
5. If a response is uncertain, use the displayed Retry action before reloading. The same request identifier is reused, preventing double stock changes. Unresolved requests are saved in browser session storage and survive search filtering and reloads in the same tab. Resolve them before closing the tab.

Counts shown in Inventory are physical shelf/picked/buffer values, not calculated sellable availability. Every mutation rechecks stock under database locks. Orders shows the most recent 100 records. Expired reservations become EXPIRED via the background worker; invalid actions are also rejected by the backend.

## Verification

```powershell
npm --prefix frontend run build
npm --prefix frontend run lint
dotnet build tests/ShelfSync.Tests
pwsh -File scripts/Verify-Http.ps1
dotnet test tests/ShelfSync.Tests
```

The HTTP verification runs a separate API on port 5088 with temporary generated operator credentials, checks access protection and real database reads, then stops it. It does not modify inventory.

Database integration tests require `SHELFSYNC_TEST_CONNECTION` to point to **shelfsync_test**, with schema already applied. Use a dedicated test owner account: the rollback failure-injection test creates/drops a trigger and needs table ownership. Never grant that permission to the production application role. The GitHub Actions job provisions its own disposable PostgreSQL database and runs the complete suite plus frontend checks and Docker build.

## Security

All business API reads and writes require a signed operator session. Passwords are checked using ASP.NET Identity password hashing. Session cookies are HttpOnly, SameSite Strict, last four hours, and require Secure transport outside Development. Mutating requests require ASP.NET antiforgery tokens, including login/logout. Login allows five attempts per client address per minute. Health endpoints are intentionally anonymous. The browser receives no password hash or database credentials.

This MVP has one trusted operator role for the fictional store; it does not implement customer accounts, registration, password reset, MFA, or per-user order ownership. Changing the configured password does not invalidate already issued session cookies; stop the server and rotate the persisted data-protection key ring if immediate revocation is needed. Never expose the Development server on a public interface.

Implementation references: [cookie authentication](https://learn.microsoft.com/aspnet/core/security/authentication/cookie?view=aspnetcore-10.0), [antiforgery](https://learn.microsoft.com/aspnet/core/security/anti-request-forgery?view=aspnetcore-10.0), [rate limiting](https://learn.microsoft.com/aspnet/core/performance/rate-limit?view=aspnetcore-10.0).

## Deployment package

`scripts/Publish.ps1` produces `artifacts/publish/` with the API and compiled dashboard. `Dockerfile` builds the same application in stages and runs as a non-root user on port 8080. Docker is not installed on the original machine, so container execution is verified by CI when pushed, not claimed as locally verified.

For future HTTPS hosting, supply `ConnectionStrings__ShelfSync`, `Operator__Username`, and `Operator__PasswordHash` from the host's secret manager. Do not put a raw password in configuration. Terminate TLS at a trusted reverse proxy, correctly configure trusted forwarded headers before enabling proxy-dependent behavior, persist ASP.NET data-protection keys outside disposable containers, set database backups, and limit database network access. No cloud hosting or paid resources are created for this local release.

## Scope decisions

The early design notes proposed external sales that could invalidate reservations, negative discrepancy counts, stale-feed gating, picked-order restocking, and reconciliation. The implemented MVP instead conservatively rejects POS sales that would consume reserved stock; its schema disallows negative inventory. Cancellation is for unexpired RESERVED orders. Those early proposals are future extensions, not implemented acceptance criteria. Current source and tests are authoritative.

Known verification limit on the original machine: 41 of 42 database tests passed with the application-role test connection; the rollback trigger test requires a test-owner connection. The separate HTTP security checks pass. The CI database is owned by its test account and includes the required permissions.

## Verified release checkpoint — 22 October 2026

- Frontend dependency audit: zero reported vulnerabilities; production build and lint passed.
- Backend and test projects compile without warnings.
- Published dashboard responds correctly from the backend.
- HTTP checks passed: anonymous access denied, CSRF rejection, operator login/session/logout, catalog/orders reads, missing idempotency-key rejection.
- Published API exercised against a disposable product in `shelfsync_test`: reservation replay returns the same order; cancel; pick and fulfill; shortage and resolution; POS replay; final stock matches exactly one fulfillment plus one sale. Fixture rows and operation results were removed afterward.
- Full database suite: 41 passed, one rollback-trigger test blocked by the local test account's table ownership permissions. This is an environment prerequisite, not recorded as a passing test. CI is configured with a test-owner database; that remote run has not been executed here.
- Container execution was not run locally. Phase 7 browser verification is recorded below. The artifact is a local portfolio release, not a publicly hosted service.


## Phase 7 browser checkpoint — 22 October 2026

Verified against the running published app using temporary local test credentials: sign-in, catalog rendering, SKU search, reservation submission with pending button state, new order visibility, state filtering, cancellation, active-order summary update, order-ID search, POS missing-product validation, and no captured browser console errors. A 390 px responsive check found no page-wide horizontal overflow; wide tables scroll inside their containers.

A demo reservation for SS-002 was created and cancelled during this check; its audit record remains in the development database. No stock was consumed by this reservation/cancellation exercise.

Phase 7 refinements: order search/state filters; effective EXPIRED display before the background sweep; expired holds excluded from the active summary; pending reservation, order-action and POS operation identifiers saved in tab session storage before sending, preserving retries across filtering and reloads. Browser evidence is available locally at `artifacts/phase7-orders.png` (ignored by Git).

POS stock mutation and remaining lifecycle actions were already verified against the published HTTP API at the previous checkpoint. Full visual automation of each lifecycle branch and deliberate browser network-disconnect testing have not been completed; do not interpret this checkpoint as that coverage.
