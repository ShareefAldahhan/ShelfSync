# API contract draft

All endpoints are local demo endpoints. Authentication is deferred; labels such as picker/operations do not confer authorization. Bind to loopback and use fictional data.

API prefix `/api`. JSON errors: `{ "code": "inventory_unavailable", "message": "Online ordering is temporarily paused.", "requestId": "...", "retryable": true }`. Do not return SQL exceptions or connection strings.

All inventory/order mutations require `Idempotency-Key` (UUID generated once per intentional action). Heartbeats are naturally repeat-safe timestamps and do not need a stored operation key. POS events additionally have a durable external `eventId`; both uniqueness rules apply. Reuse keys on retries, store payload hashes including the target resource ID and operation, and reject changed payloads with 409. Keys are retained for the lifetime of the demo dataset, not deleted when a hold expires.

| Method/path | Request | Result |
| --- | --- | --- |
| GET /health | None | 200 if API ready; 503 if DB unavailable |
| GET /products | None | Product display, stock buckets, online status, server time; 503 rather than fabricated zero if DB unavailable |
| POST /orders | productId, quantity | 201 reserved order with id, state, expiresAt; 409 insufficient stock; 503 stale/unavailable inventory |
| GET /orders/{id} | None | Persisted state, effective state, expiry and actions; effective expiry reflected even before worker cleanup |
| GET /requests/{key}?operation=... | None | Completed operation result or 404 not found; not found is not proof an in-flight request failed |
| POST /orders/{id}/pick | confirmedQuantity | 200 picked order; 409 invalid state, expired, or stock requires reconciliation |
| POST /orders/{id}/missing | reason | 200 shortage pending and quarantine; only valid from unexpired RESERVED |
| POST /orders/{id}/cancel | reason optional | 200 canceled or restock pending; expired remains expired |
| POST /orders/{id}/restock | confirmedQuantity, reason | 200 canceled with stock physically returned; only RESTOCK_PENDING |
| POST /orders/{id}/fulfill | None | 200 fulfilled; only PICKED |
| POST /pos/sales | eventId, productId, quantity | 200 committed sale plus affected orders/quarantine status |
| POST /inventory/{id}/receive | quantity, reason | 200 stock receipt; does not silently clear quarantine |
| POST /inventory/{id}/reconcile | observedShelfUnits, reason | 200 updated inventory and affected orders |
| GET /operations/orders?state=... | None | Picker/exception queues |
| GET /inventory/{id}/events | None | Ordered ledger history |
| POST /operations/feed/heartbeat | None | Persisted server heartbeat; simulator only |

400 invalid input, 404 missing resource, 409 business conflict, 503 temporary dependency failure. Preserve the original status and response for completed idempotent replay. Use UTC ISO-8601 instants, database time captured after inventory lock acquisition for deadline decisions, and local formatting for display.

Limits: quantity integer 1-100 per action; reason 1-500 characters where required; product must exist; no multi-item orders. Demo threshold/TTL are server configuration, not browser-authoritative values.

Idempotency lookup is safe only in this fictional local build. Add ownership checks before any authenticated/public deployment; request keys are not authentication credentials.
