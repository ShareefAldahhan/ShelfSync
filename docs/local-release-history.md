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
pwsh -File scripts/Verify-Http.ps1 -ProductionHttpChecks
dotnet test tests/ShelfSync.Tests
```

The HTTP verification runs a separate API on port 5088 with temporary generated operator credentials, checks access protection and real database reads, then stops it. It does not modify inventory.

Database integration tests require `SHELFSYNC_TEST_CONNECTION` to point to **shelfsync_test**, with schema already applied. Use a dedicated test owner account: the rollback failure-injection test creates/drops a trigger and needs table ownership. Never grant that permission to the production application role. The GitHub Actions job provisions its own disposable PostgreSQL database and runs the complete suite plus frontend checks and Docker build.

## Security

All business API reads and writes require a signed operator session. Passwords are checked using ASP.NET Identity password hashing. Session cookies are HttpOnly, SameSite Strict, last four hours, and require Secure transport outside Development. Mutating requests require ASP.NET antiforgery tokens, including login/logout. Login allows five attempts per client address per minute. Health endpoints are intentionally anonymous. The browser receives no password hash or database credentials.

This MVP has one trusted operator role for the fictional store; it does not implement customer accounts, registration, password reset, MFA, or per-user order ownership. Each authenticated request revalidates the operator username, role, and credential fingerprint. Changing the configured password hash or username invalidates previous sessions on the next request after the running server loads the new configuration (restart after changing environment variables or local user secrets). Never expose the Development server on a public interface.

Implementation references: [cookie authentication](https://learn.microsoft.com/aspnet/core/security/authentication/cookie?view=aspnetcore-10.0), [antiforgery](https://learn.microsoft.com/aspnet/core/security/anti-request-forgery?view=aspnetcore-10.0), [rate limiting](https://learn.microsoft.com/aspnet/core/performance/rate-limit?view=aspnetcore-10.0).

## Deployment package

`scripts/Publish.ps1` produces `artifacts/publish/` with the API and compiled dashboard. `Dockerfile` builds the same application in stages and runs as a non-root user on port 8080. Docker is not installed on the original machine, so container execution is verified by CI when pushed, not claimed as locally verified.

For future HTTPS hosting, supply `ConnectionStrings__ShelfSync`, `Operator__Username`, and `Operator__PasswordHash` from the host's secret manager. Do not put a raw password in configuration. Terminate TLS at a trusted reverse proxy, correctly configure trusted forwarded headers before enabling proxy-dependent behavior, persist ASP.NET data-protection keys outside disposable containers, set database backups, and limit database network access. No cloud hosting or paid resources are created for this local release.

## Scope decisions

The early design notes proposed external sales that could invalidate reservations, negative discrepancy counts, stale-feed gating, picked-order restocking, and reconciliation. The implemented MVP instead conservatively rejects POS sales that would consume reserved stock; its schema disallows negative inventory. Cancellation is for unexpired RESERVED orders. Those early proposals are future extensions, not implemented acceptance criteria. Current source and tests are authoritative.

Known verification limit on the original machine: 41 of 42 database tests passed with the application-role test connection; the rollback trigger test requires a test-owner connection. The separate HTTP security checks pass. The CI database is owned by its test account and includes the required permissions.

## Verified release checkpoint â€” 22 October 2026

- Frontend dependency audit: zero reported vulnerabilities; production build and lint passed.
- Backend and test projects compile without warnings.
- Published dashboard responds correctly from the backend.
- HTTP checks passed: anonymous access denied, CSRF rejection, operator login/session/logout, catalog/orders reads, missing idempotency-key rejection.
- Published API exercised against a disposable product in `shelfsync_test`: reservation replay returns the same order; cancel; pick and fulfill; shortage and resolution; POS replay; final stock matches exactly one fulfillment plus one sale. Fixture rows and operation results were removed afterward.
- Full database suite: 41 passed, one rollback-trigger test blocked by the local test account's table ownership permissions. This is an environment prerequisite, not recorded as a passing test. CI is configured with a test-owner database; that remote run has not been executed here.
- Container execution was not run locally. Phase 7 browser verification is recorded below. The artifact is a local portfolio release, not a publicly hosted service.


## Phase 7 browser checkpoint â€” 22 October 2026

Verified against the running published app using temporary local test credentials: sign-in, catalog rendering, SKU search, reservation submission with pending button state, new order visibility, state filtering, cancellation, active-order summary update, order-ID search, POS missing-product validation, and no captured browser console errors. A 390 px responsive check found no page-wide horizontal overflow; wide tables scroll inside their containers.

A demo reservation for SS-002 was created and cancelled during this check; its audit record remains in the development database. No stock was consumed by this reservation/cancellation exercise.

Phase 7 refinements: order search/state filters; effective EXPIRED display before the background sweep; expired holds excluded from the active summary; pending reservation, order-action and POS operation identifiers saved in tab session storage before sending, preserving retries across filtering and reloads. Browser evidence is available locally at `artifacts/phase7-orders.png` (ignored by Git).

POS stock mutation and remaining lifecycle actions were already verified against the published HTTP API at the previous checkpoint. Full visual automation of each lifecycle branch and deliberate browser network-disconnect testing have not been completed; do not interpret this checkpoint as that coverage.


## Phase 8 â€” single-operator security checkpoint

Historical Phase 8 checkpoint: the original release used one Operator. Superseded on 24 October 2026 by individual Customer, Associate, Manager and Admin accounts; see [accounts and access](accounts.md).

Implemented and verified: credential hashing; four-hour signed HttpOnly/SameSite session cookie; wrong-password denial; tampered-cookie denial; antiforgery on writes including login/logout; five login attempts per minute per direct client address; explicit operator-role checks; credential fingerprint revalidation on each authenticated request; old session denial after credential changes; production API rejection over plain HTTP; secure antiforgery cookies outside Development; no credential configuration values included in validation error messages. Startup checks the configured hash format before accepting traffic.

Verification on 22 October 2026: backend/test build passed without warnings; frontend build and lint passed; expanded HTTP security checks passed; production HTTP checks passed. Temporary credential changes used only child-process environment variables and did not change saved operator credentials or inventory.

HTTPS hosting, trusted proxy configuration and data-protection key persistence must be configured and verified on the actual hosting platform in Phase 9. Do not forge forwarded headers to bypass HTTPS enforcement. Local start remains loopback-only Development. Role separation, customer accounts, MFA, distributed login throttling and server-side per-session revocation are future extensions. Logout removes the current browser's cookie; credential rotation revokes all earlier cookies, including copies.

## Phase 9 â€” verified local deployment

Phase 9 is complete for the explicitly selected local deployment scope. No public host or paid resources were provisioned.

Run the repeatable release gate from the repository root:

```powershell
pwsh -File scripts/Verify-Release.ps1
```

This builds the release, initializes a separate password-protected PostgreSQL 18 cluster under ignored `artifacts/verification-*`, binds it only to 127.0.0.1:55439, creates `shelfsync_test` with its own table-owning test role, applies the schema, runs all 42 integration tests, exercises the published lifecycle and POS routes with a fictional fixture, runs security checks and production HTTP rejection, and runs frontend lint. It stops that cluster and restores connection environment variables in `finally`. Your existing PostgreSQL service, databases, and permissions are not changed. Bootstrap password files are removed after initialization. Stopped test data/logs remain under ignored artifacts for diagnostics. Use `-PostgresBin` or `-Port` for a different local PostgreSQL installation or unused port; use `-SkipPublish` only when a current publish package already exists.

Verified on 22 October 2026: **42 passed, zero failed, zero skipped**, plus published reservation/replay/cancel, pick/fulfill, shortage/resolve/cancel, POS/replay, final stock invariants, wrong-password rejection, tampered-session rejection, CSRF, login throttling, logout, credential-rotation invalidation, production HTTP rejection and lint. This resolves the earlier 41/42 local-account ownership limitation without granting extra application permissions.

Create a distributable package:

```powershell
pwsh -File scripts/Publish.ps1 -Archive
```

Outputs: `artifacts/publish/`, `artifacts/publish.zip`, and `artifacts/publish.zip.sha256`. `release.json` records source revision, local-change status and UTC build time. The ZIP includes the compiled dashboard and backend, not database passwords or operator credentials. Install .NET 10 runtime and configure the database/operator secrets on the destination machine. For the original machine use `scripts/Set-Operator.ps1` once, then `scripts/Start-Local.ps1`.

The supplied Dockerfile and CI workflow remain available for future hosting; Docker execution and remote CI were not run locally. Public HTTPS/proxy configuration remains deferred because public hosting is outside the selected scope. Phase 10 portfolio polish is separate.

## Phase 10 â€” documentation and portfolio handoff

The README now presents the implemented local MVP with a verified dashboard screenshot, setup/startup commands, architecture overview, verification evidence and explicit scope. Current setup, architecture, API and acceptance documents replace contradictory draft claims. Older design/learning documents remain preserved and clearly marked historical.

`docs/portfolio.md` supplies a three-minute demo script, project description, interview prompts and honest claim boundaries. `docs/package-readme.md` is copied into the distributable so an extracted package has its own startup instructions. Local documentation links and screenshot targets were checked. A recorded video, public hosting, remote CI execution and the later comprehensive teaching course are not claimed as delivered.

The selected local functional MVP and its release/documentation work are complete. Source changes are committed locally. Publishing/pushing to an external destination remains a separate action.


## Account release — 24 October 2026

70 tests passed with no skips; published role/ownership, account lifecycle, password auditing, lockout, inventory workflows and transport checks passed. Migration 002 and restricted table/sequence grants were explicitly approved and applied on the original local database. The existing credential bootstrapped one recovery Admin successfully. The upgraded release serves localhost:5000; readiness and anonymous API denial were checked. Browser preview inspection was blocked by browser security policy; new account screens have build/lint verification but no completed visual check.


## Store workflow release — 24 October 2026

The approved migration 003 and SELECT/INSERT grants on inventory_returns and mail_preview were applied locally. Existing shelf/picked counts were retained, with backroom/damaged initialized to zero. The upgraded release is active at localhost:5000; readiness, published stockroom/cart/mail controls and anonymous stock denial were checked. 82 tests and the expanded published workflow/security checks passed; frontend build and lint passed. See store-operations.md for behavior, permissions and limitations. No external email was sent. Phone-camera capture, physical scanner hardware and completed visual review are not claimed.


## Payment release prepared — 25 October 2026

Stripe-hosted test checkout, signed event verification, server-owned totals, idempotent attempts, asynchronous confirmation and refund reconciliation are implemented. All 100 tests and published regression/access checks passed; frontend build and lint passed without warnings. Fake-provider tests and real SDK signature checks do not claim an actual Stripe transaction. The release is staged at artifacts/payments-build. Migration 004 is approved, but the administrator prompt had not completed at the last check; the running app remains the store-workflow release. Stripe credentials and webhook forwarding are still required. No real payments were enabled or initiated.


Payment activation update — 25 October 2026: the approved payment migration and restricted grants have completed. The payment release is now active at localhost:5000 and readiness/test-mode controls were checked. Stripe CLI 1.55.1 is installed. A private Connect-StripeTest prompt is open for the user to supply the sandbox API key; provider authentication, webhook forwarding and an actual test checkout remain unverified until that step is completed.


Associate picking update — 25 October 2026: Associates now require an active reservation belonging to a Customer account. Staff-owned and ownerless legacy reservations are not pickable by Associates; Managers/Admins retain legacy handling. UI eligibility is returned with orders and rechecked under the server picking lock. 103 tests, published access/workflow checks, build and lint passed. The release is active locally; no schema migration was required.


Associate shopping UI update — 25 October 2026: cart/reservation controls and personal shopping navigation are removed for Associates. Reservation, cart-reservation and checkout endpoints enforce the same role boundary. Customer and Manager/Admin reservation support is preserved. 107 tests and published checks passed; build/lint passed. The active Associate session was refreshed and visually verified to show View item details instead of shopping controls.


Checkout-first release — 25 October 2026: migration 005 and restricted link/view grants were approved and applied. Customer flow is now Add to cart → Checkout → one full-cart Stripe test payment → paid-only staff picking → pickup. New confirmation, whole-cart receipt, expiry, cancellation, outage/retry and queue tests bring the suite to 115 passing tests; build/lint and published regression checks passed. The live customer session was refreshed and visually checked to show only Add to cart and the new Checkout action. No payment was initiated during this verification. Existing records and Stripe test configuration were retained.


Full verification — 25 October 2026: 116 tests and two final full release gates passed after fixing an expiry candidate deletion race. Migration replay, production dependency advisories, customer desktop/mobile navigation, API protection and read-only Stripe session amount/currency matching were checked. The expiry fix is active locally. See test-report-2026-10-25.md for scope and the remaining manual hosted Checkout test.
