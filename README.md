# ShelfSync

A local portfolio application for inventory reservations, fulfillment, and store sales, using ASP.NET Core 10, PostgreSQL, Npgsql, React, TypeScript, and Vite.

See [the local release guide](docs/local-release.md) for one-command startup, operator setup, verification, security details and deployment instructions.

## Implemented

- Operator sign-in, protected API access, CSRF protection, and login rate limiting.
- Product dashboard with shelf, picked, buffer and quarantine visibility.
- Reservations with quantity validation, expiration, and idempotency.
- Latest 100 orders, cancellation, picking, shortage reporting/resolution, and fulfillment.
- POS sales with event-based deduplication.
- Transactional stock updates, row locks, audit events, and background expiry.
- UI errors, pending states, refresh, and safe retries for uncertain operations.

Shelf counts are physical inventory, not a promise of available stock. The backend rechecks active reservations under locks on every mutation.

## Run locally (Windows PowerShell)

Prerequisites: .NET 10 SDK, Node.js compatible with Vite 8, PostgreSQL, and a development database provisioned using `database/` scripts. Review scripts before applying them; do not recreate an existing database.

Run `pwsh -File scripts/Set-Operator.ps1` to configure operator sign-in. Configure `ConnectionStrings:ShelfSync` using .NET user secrets for `backend/ShelfSync.Api`, or the `ConnectionStrings__ShelfSync` environment variable. Keep passwords out of source control and command history.

From the repository directory:

```powershell
dotnet run --project backend/ShelfSync.Api --urls http://localhost:5000
```

In another terminal:

```powershell
cd frontend
npm ci
npm run dev
```

Open http://localhost:5173. The Vite proxy sends `/api` and `/health` to the API on port 5000.

## Verification

```powershell
npm --prefix frontend run build
npm --prefix frontend run lint
dotnet build tests/ShelfSync.Tests
dotnet test tests/ShelfSync.Tests
```

Integration tests require `SHELFSYNC_TEST_CONNECTION` configured securely in the current process. The test helper refuses any database name except `shelfsync_test`. Provision that separate database with the current schema and suitable test-role permissions before testing.

Manual walkthrough: reserve one unit, find the order, pick and fulfill it. Repeat with cancellation and shortage/resolution. Record a POS sale. Refresh to inspect stock. A retry after an uncertain response reuses its original key/event; keep the page open and resolve the operation before closing or reloading. Pending retry state is held in memory, not persisted across reloads.

## API

- `GET /api/products`, `GET /api/products/{id}`
- `GET /api/orders` (latest 100, newest first)
- `POST /api/reservations` with `{productId, quantity}`
- `POST /api/reservations/{id}/cancel`
- `POST /api/orders/{id}/pick`, `/shortage`, `/resolve-shortage`, `/fulfill`
- `POST /api/pos/sales` with `{eventId, productId, quantity}`
- `GET /health/live`, `GET /health/ready`

Reservation and order mutations require a UUID `Idempotency-Key` header. POS uses the UUID `eventId`. Quantities must be whole numbers from 1 to 100.

## Release limits

This is a loopback-only local demonstration using fictional data. A single operator role is implemented. Public hosting, customer accounts, and browser automation are outside this local release. Use the loopback startup script for this release. Public hosting requires HTTPS and the production configuration described in the release guide. No hosting resources are provisioned.

Existing design notes in `docs/` describe broader intentions; actual source and verified tests determine implemented behavior.
