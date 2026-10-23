# Implemented API contract

This describes the local single-operator MVP. The API is served on localhost:5000 by the local startup script; Vite development proxies `/api` and `/health` to it.

## Session and request security

1. `GET /api/auth/session` returns `{authenticated, username, token}` and sets the antiforgery cookie.
2. `POST /api/auth/login` accepts `{username,password}` and the `X-CSRF-TOKEN` header. Successful sign-in sets a signed HttpOnly session cookie.
3. Fetch `/api/auth/session` again after login for a token associated with the signed-in identity.
4. Send the session cookie on API requests and `X-CSRF-TOKEN` on writes, including logout.
5. `POST /api/auth/logout` removes the browser session cookie.

Business reads and writes require an authenticated Operator. Invalid/absent sessions return 401; insufficient role returns 403. Missing/invalid antiforgery tokens return 400. Login attempts are limited to five per direct client address per minute, then 429. Outside Development, plain HTTP API requests return 400; use HTTPS.

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

There is no GET /api/inventory route: inventory fields are already returned with products. There are no receiving, reconciliation, picked-restock, customer account, registration, reset-password, or external-feed ingestion routes. RESTOCK_PENDING is a schema value, not an exposed operation.

## Associate details extension

`GET /api/products/{id}/details` returns identity and current price; profile/barcode/location metadata; consistent stock buckets; 30-day POS and online fulfilled unit totals; the latest 100 POS sales; and the latest 100 inventory events. It requires the Operator session and the product_profiles migration. Unknown product IDs return 404. See [associate details](associate-details.md) for field semantics, provenance and upgrade instructions.

## Operator item maintenance

POST `/api/products/{id}/location`: `{aisle,bay,shelf,bin,reason}`. Fields are nullable strings up to 40 characters; blank clears. POST `/api/products/{id}/stock-correction`: `{shelfUnits,expectedShelfUnits,reason}`. Shelf units are an absolute count, not an increment. Count must be 0–1,000,000 and expectedShelfUnits must match current shelf count. Reason is required (3–500 characters). Both use Operator authentication, CSRF and UUID Idempotency-Key and return `{productId,operation,updated:true}` on success. Invalid input returns 400; stale counts or uncovered reservations return 409; unknown inventory returns 404. Writes and audit/replay results commit atomically. Picked quantities are not editable through this endpoint.
