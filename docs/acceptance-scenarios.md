# Release acceptance checklist

Current release scope: one fictional store, single operator, software-controlled reservations and POS, local deployment. This replaces the early proposed checklist; deferred items are listed explicitly below.

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
| Frontend compiles and lints | Publish and verification scripts |
| Dashboard navigation, search, reserve/cancel, filters, validation render | Recorded Phase 7 browser checks |
| Package builds from committed source | Clean-source publish check |
| Repeatable local test environment without development DB changes | Verify-Release.ps1 temporary cluster |

Verification result: **42 tests passed, zero failed/skipped**, plus published workflow/security checks, build and lint. See local-release.md for dates and detailed scope. Browser coverage is a recorded check, not a committed automated browser test suite. Docker/remote CI execution is not claimed.

## Deferred extensions

Public hosting; multiple users/roles; customer-owned orders; external POS feeds; negative discrepancy counts; stale-feed gating; quarantine reconciliation; receipt/count APIs; picked-order cancellation and physical restocking; receiving UI; browser network-fault automation; distributed sessions/rate limits; pagination beyond the latest 100 orders.

These are not hidden acceptance failures for the selected local MVP. Adding them requires explicit policies, implementation and new tests.
