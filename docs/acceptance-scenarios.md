# Release acceptance checklist

Current release scope: one fictional store, four web roles, software-controlled reservations and POS, local deployment. This replaces the early proposed checklist; deferred items are listed explicitly below.

| Criterion | Evidence |
| --- | --- |
| No duplicate allocation in reservation/POS concurrency cases | PostgreSQL integration suite |
| Same-key replay does not repeat stock changes | Reservation/POS integration tests and published HTTP checks |
| Conflicting payload for same identifier is rejected | Integration tests |
| Transaction failure rolls back reservation/order/event/request writes | Ownership-enabled isolated rollback test |
| Reservation lifecycle and expiry rules hold | Integration suite |
| Cancel, pick, shortage, resolve, fulfill work through published API | Isolated published HTTP workflow checks |
| POS replay preserves final inventory | Published HTTP stock-invariant assertion |
| Missing/invalid quantities are rejected | Validation and integration tests |
| Auth, CSRF, throttling, altered-cookie rejection and credential rotation | Verify-Http.ps1 |
| Production API refuses plain HTTP | Verify-Http.ps1 -ProductionHttpChecks |
| Receiving, transfers, return bounds, atomic carts, receipt ownership and local email preview | StoreOperationsTests and published Verify-Access checks |
| Four-role permissions, customer ownership, account lifecycle and lockout | Verify-Access.ps1 and authorization/ownership tests |
| Frontend compiles and lints | Publish and verification scripts |
| Dashboard navigation, search, reserve/cancel, filters, validation render | Recorded Phase 7 browser checks |
| Versioned package records source provenance and verifies file integrity | Ship-Local.ps1, manifest checks, ZIP checksum; local changes are recorded honestly |
| Missing database schema fails startup/readiness safely | DatabaseReadinessTests |
| Backup restores core business/account data | Isolated release-gate restore and fingerprint comparison |
| Repeatable local test environment without development DB changes | Verify-Release.ps1 temporary cluster |

Verification result: **122 backend tests and 3 frontend tests passed, zero failed/skipped**, plus published workflow/security checks, build and lint. See local-release.md for dates and detailed scope. Browser coverage is a recorded check, not a committed automated browser test suite. Docker/remote CI execution is not claimed.

## Deferred extensions

Public hosting; external POS feeds; negative discrepancy counts; stale-feed gating; quarantine reconciliation; backroom/damaged count reconciliation; browser network-fault automation; distributed sessions/rate limits; stable cursor pagination at large scale.

These are not hidden acceptance failures for the selected local MVP. Adding them requires explicit policies, implementation and new tests.
