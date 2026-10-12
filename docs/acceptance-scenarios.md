# Release acceptance scenarios

Use PostgreSQL, not mocks, for stock/concurrency cases. Each test starts from an isolated fixture. Do not run reset/seed against an unspecified or production database. Assertions include orders, request results, stock, and ledger entries.

| # | Scenario | Expected result |
| --- | --- | --- |
| 1 | Shelf 3; reserve 1; buffer 0; feed fresh | Shelf 3, active reserved 1, picked 0, available 2 |
| 2 | Shelf 1; two distinct requests concurrently reserve 1 | One reserved order, one 409, available 0; no leaked order |
| 3 | Repeat successful request key and payload | Same order/status; one reservation event |
| 4 | Same key, changed quantity | 409; original result unchanged |
| 5 | Inject error after creating order but before commit | No order/movement/key-result remains; retry can succeed |
| 6 | Commit then drop response | Retry recovers original order; no additional hold |
| 7 | Shelf 3, reserved 1; POS sells 3 | Shelf 0, order SHORTAGE_PENDING, quarantine true, available 0 |
| 8 | Replay sale event ID with same payload | No additional deduction or shortage event |
| 9 | Replay sale event ID with conflicting quantity | 409; recorded stock unchanged |
| 10 | POS reports sale exceeding recorded shelf stock | Negative discrepancy preserved; quarantined; online unavailable |
| 11 | Reserve, then confirm pick before deadline | Shelf decreases, picked increases, reservation ends; total stock unchanged |
| 12 | Pick races with expiry | Exactly one valid transition based on locked state/database time; no double movement |
| 13 | Stop expiry worker; wait past TTL; reserve again | Expired hold no longer counts; old order cannot be picked |
| 14 | Restart API with expired reservations | Sweep converges to EXPIRED once; no duplicate expiry event |
| 15 | Cancel RESERVED twice | Logical hold releases once; shelf unchanged |
| 16 | Cancel PICKED, then confirm restock twice | Stock unavailable while RESTOCK_PENDING; physical return counted once |
| 17 | Fulfill PICKED twice | Picked quantity decremented once; shelf unchanged |
| 18 | Feed heartbeat older than threshold | New reservation rejected even if stored count is positive |
| 19 | Restore heartbeat while SKU quarantined | SKU remains unavailable until reconciliation |
| 20 | Reconcile shelf count with a reason | Adjustment recorded; uncovered holds resolved; availability resumes only if healthy |
| 21 | DB unavailable | No success messages or new mutations; UI explains unavailable status |
| 22 | Lock contention exceeds limit | Transaction aborts; bounded retry or 503; no partial effects |
| 23 | Blank reason, zero/negative/fractional quantity, unknown SKU | Input rejected without mutation |
| 24 | Report missing item during a hold | Shortage pending; quarantine; no invented shelf count |

Browser workflow: reserve chips -> observe available decrease -> simulate shelf sale -> observe shortage -> cancel -> reconcile -> see online availability recover. Add a restock flow if time permits.

Manual verification: fresh clone/setup, process restart, small-screen layout, failure messages, failure controls disabled outside Development, no secrets in repository, README statements match actual tests.

Three-minute demo outline: 30 seconds problem/stock buckets; 45 seconds reservation and picking; 45 seconds in-store shortage; 30 seconds lost-response retry; 30 seconds tests and limitations.
