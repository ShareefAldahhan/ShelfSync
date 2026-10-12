# ShelfSync: inventory reservations and fulfillment

Design package for a seven-day, local portfolio release. This folder currently contains the roadmap, architecture, API contract, database schema, and acceptance scenarios. The application is not implemented yet.

## Goal

Demonstrate that simultaneous online orders cannot reserve the same unit, that recorded store sales can invalidate unpicked reservations, and that shortages and outages produce honest, recoverable outcomes.

## Start here

1. Read `docs/roadmap.md` for the daily work plan and required tools.
2. Read `docs/software-design.md` for stock definitions and transaction rules.
3. Use `docs/api-contract.md` when implementing the backend and UI.
4. Use `docs/acceptance-scenarios.md` as the release checklist.
5. Use `database/schema.sql` as the initial PostgreSQL schema. It has not been applied or validated against PostgreSQL yet; it is not a substitute for implementing the transaction rules.

Stack: React/TypeScript, ASP.NET Core on .NET 10, PostgreSQL, Npgsql, xUnit, Playwright, GitHub Actions. Prefer direct SQL through Npgsql for the small number of stock transactions, making the locking behavior easy to inspect. Do not mix multiple write paths for stock updates.

Free local development. No paid hosting, payment provider, AI API, custom domain, or Walmart integration is required. One process hosts the API and expiry worker; production builds can serve the React assets through the backend.

## Scope

One fictional store, 6-10 seeded products, integer quantities, single-item orders, shared inventory per SKU, receipt/sale/count events, reservations, picking, cancellation, collection/dispatch confirmation, an event history, and controlled failure exercises.

Customer, picker, and operations are separate screens, not an authentication system. Week-one build runs locally on loopback with fictional data. Do not expose it publicly as a secured application. Authentication and authorization must be added and tested before a public write-enabled deployment.

An associate scan confirms picking and represents physical separation into a fulfillment area. Software cannot guarantee that physical separation actually happened or see a shopper's cart before a checkout event.

## Completion standard

The three-bag scenario, final-unit concurrency, idempotent retry, worker restart, expiry/pick race, quarantine/reconciliation, and picked-item restocking must pass against PostgreSQL. A reviewer gets reproducible instructions, a short video, and clearly identified limitations.
