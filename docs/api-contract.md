# Implemented API contract

This describes the local multi-role application. The API is served on localhost:5000 by the local startup script; Vite development proxies `/api` and `/health` to it.

## Session and request security

1. `GET /api/auth/session` returns `{authenticated, username, userId, roles, token}` and sets the antiforgery cookie.
2. `POST /api/auth/login` accepts `{username,password}` and the `X-CSRF-TOKEN` header. Successful sign-in sets a signed HttpOnly session cookie.
3. Fetch `/api/auth/session` again after login for a token associated with the signed-in identity.
4. Send the session cookie on API requests and `X-CSRF-TOKEN` on writes, including logout.
5. `POST /api/auth/logout` removes the browser session cookie.

Business reads and writes require a known application role. See [accounts and access](accounts.md) for the server permission matrix. Customer orders are filtered by authenticated user ID; cross-customer cancellation returns 404. Invalid/absent sessions return 401; insufficient role returns 403. Missing/invalid antiforgery tokens return 400. Login attempts are limited to five per direct client address per minute, then 429. Outside Development, plain HTTP API requests return 400; use HTTPS.

## Routes

| Method | Route | Body / result |
| --- | --- | --- |
| GET | /api/products | Array of product/inventory DTOs |
| GET | /api/products/{id} | One product, or 404 |
| GET | /api/orders | Latest 100 orders, newest first |
| POST | /api/reservations | `{productId,quantity}`; 201 reservation receipt |
| POST | /api/reservations/{id}/cancel | Cancel a live RESERVED order |
| POST | /api/orders/{id}/pick | RESERVED to PICKED |
| POST | /api/orders/{id}/shortage | RESERVED to SHORTAGE_PENDING |
| POST | /api/orders/{id}/resolve-shortage | Recheck and restore eligible shortage order to RESERVED |
| POST | /api/orders/{id}/fulfill | PICKED to FULFILLED |
| POST | /api/pos/sales | `{eventId,productId,quantity}`; committed sale response |
| GET | /health/live | Anonymous process liveness |
| GET | /health/ready | Anonymous database readiness; 503 if unavailable |

Reservation/order mutation routes require one nonempty UUID `Idempotency-Key` header. POS uses its nonempty UUID `eventId` instead. Product IDs and quantities are validated; quantity must be an integer from 1 through 100.

## DTOs

Product: `id`, `sku`, `name`, `priceCents`, `shelfUnits`, `pickedUnits`, `safetyBuffer`, `quarantined`.

Order summary: `id`, `productId`, `sku`, `name`, `quantity`, `state`, `expiresAt`, `createdAt`, `updatedAt`, nullable `reason`.

Reservation receipt: `orderId`, `productId`, `quantity`, `state`, `expiresAt`. New reservations last 15 minutes. Shortage resolution may issue a new expiry.

Successful lifecycle responses include `orderId` and the resulting state. POS returns the committed event/product/quantity/state. Read actual service responses rather than assuming every mutation has an identical response DTO.

## Retry and errors

Generate a new identifier for a new intentional operation. On an uncertain network/server outcome, retry the exact payload using the same identifier. Successful replays return the stored outcome without repeating inventory movement. Reusing an identifier with a changed payload returns conflict.

Business-service errors generally use `{ "error": "..." }`, with validation/conflict/not-found status as appropriate. Framework binding errors and unhandled failures may use ASP.NET error/problem responses; unauthorized responses may have no JSON body. Never treat a failed HTTP response as success merely because the server returned JSON.

A stale product screen is not a stock guarantee. The service rechecks reservations, stock and state within its transaction. The frontend retains unresolved operation identifiers in tab session storage until resolution; this is not a durable cross-device request history.

## Deliberate omissions

There is no GET /api/inventory route: inventory fields are already returned with products. There is no external-feed ingestion or payment/refund route. Receiving and picked cancellation are supplied by the store extension. RESTOCK_PENDING is a schema value, not an exposed operation.

## Associate details extension

`GET /api/products/{id}/details` returns identity and current price; profile/barcode/location metadata; consistent stock buckets; 30-day POS and online fulfilled unit totals; the latest 100 POS sales; and the latest 100 inventory events. It requires a Staff policy session and the product_profiles migration. Unknown product IDs return 404. See [associate details](associate-details.md) for field semantics, provenance and upgrade instructions.

## Operator item maintenance

POST `/api/products/{id}/location`: `{aisle,bay,shelf,bin,reason}`. Fields are nullable strings up to 40 characters; blank clears. POST `/api/products/{id}/stock-correction`: `{shelfUnits,expectedShelfUnits,reason}`. Shelf units are an absolute count, not an increment. Count must be 0–1,000,000 and expectedShelfUnits must match current shelf count. Reason is required (3–500 characters). Location changes require Staff; stock corrections require Management. Both use authentication, CSRF and UUID Idempotency-Key and return `{productId,operation,updated:true}` on success. Invalid input returns 400; stale counts or uncovered reservations return 409; unknown inventory returns 404. Writes and audit/replay results commit atomically. Picked quantities are not editable through this endpoint.


## Accounts

POST `/api/auth/register` takes `{username,password}` and always assigns Customer, regardless of extra fields. POST `/api/account/password` takes `{currentPassword,newPassword}`. Both require CSRF and use login throttling. Own password changes revoke existing sessions. Recovery Admin uses the local setup script instead.

Admin-only routes: GET `/api/admin/users`, POST `/api/admin/users` with `{username,password,role}`, POST `/api/admin/users/{id}/access` with `{role,enabled,reason}`, POST `/api/admin/users/{id}/password` with `{password,reason}`, GET `/api/admin/audit`. Account/access/password changes are transactionally audited. The recovery account is protected; an administrator cannot change their own access through this endpoint. Role changes, disablement and password resets revoke existing sessions.


Password recovery: Admin POST `/api/admin/users/{id}/recovery-code` accepts `{reason}` and returns `{username,code,expiresInMinutes:30}` after identity verification. Anonymous POST `/api/auth/reset-password` accepts `{username,code,newPassword}` with CSRF and login rate limits. Invalid, expired, replaced and consumed codes receive the same 400 message. Success revokes sessions, clears lockout and audits the recovery without secrets. See accounts.md for the local delivery process.


## Store workflows

Staff: GET `/api/stock`; GET `/api/inventory-history?search=&page=1` (50 rows); POST `/api/stock/receive` and `/api/stock/replenish` with `{productId,quantity,reason,reference?}`. Management: POST `/api/returns` with `{saleId,isPos,quantity,destination,reason}`; POST `/api/orders/{id}/cancel-picked` with `{destination,reason}`. Destinations are backroom or damaged. All writes require CSRF and UUID Idempotency-Key.

Authenticated: POST `/api/cart/reserve` with `{items:[{productId,quantity}]}` (UUID Idempotency-Key); GET `/api/orders?page=1` (100 rows); GET `/api/orders/{id}/receipt` (owner or staff). Cart response contains pickup group cartId, totalCents and product order receipts. Each line keeps its lifecycle.

Anonymous CSRF/rate-limited: POST `/api/auth/forgot-password` with `{username}` always gives the same 200 message and queues an eligible account's reset code in the local inbox. Admin: GET `/api/admin/mail-preview` returns latest 100 local messages. No external email delivery is enabled.


## Stripe test payments

Authenticated: GET `/api/payments/config`; GET `/api/payments/orders/{id}` (owner or staff); POST `/api/payments/orders/{id}/checkout` (owner, PICKED order, server price); POST `/api/payments/orders/{id}/refresh` (owner or staff). Writes use CSRF and per-account payment throttling. Checkout has one durable attempt per active order; it does not trust an amount in the request body.

Admin: GET `/api/payments/admin/recent`; POST `/api/payments/{paymentId}/reconcile` with `{sessionId}`. Anonymous provider endpoint: POST `/api/payments/stripe-webhook`, raw JSON with Stripe-Signature. Only this write bypasses CSRF; official signature verification, timestamp checks, test-mode checks and authoritative session retrieval replace browser authentication for that endpoint. See payments.md.


Picking restriction: Associate-only callers must pick an active customer-owned reservation. Staff-created or ownerless reservations return 409 without moving stock. Unknown orders return 404; cancelled/non-RESERVED/expired orders remain rejected. Order summaries expose customerReservation for rendering the eligible actions. Managers/Admins retain legacy picking support.


Checkout-first extension: POST `/api/cart/checkout` takes the same cart lines and UUID retry key, creates temporary holds and starts a single payment for the full cart. It returns the hosted URL and anchor orderId. Payment checkout accepts active RESERVED orders as well as pre-upgrade PICKED orders. POST `/api/payments/orders/{id}/cancel-checkout` expires an open provider session before releasing its whole cart. Order/receipt payment state comes from the payment-to-order links; receipts include checkoutItems and checkoutTotalCents. Associate picking queues exclude unpaid orders when payments are enabled. See checkout-first.md.
