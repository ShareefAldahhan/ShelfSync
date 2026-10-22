> Historical planning/learning document. Some proposals differ from the completed local MVP. See [current architecture](architecture.md), [API contract](api-contract.md), and [release guide](local-release.md) for implemented behavior and verification.

# Seven-day roadmap

## Time budget and outcome

### Platform and catalog refinement (October 9, 2026)

Build a responsive web application first: customer and picker screens optimized for phones, operations optimized for a desktop/tablet. One React/TypeScript frontend uses the same backend and authoritative stock state across devices. Native iOS/Android apps are outside week one. Installability/PWA is optional later work; caching must never imply fresh inventory or allow offline reservation confirmation.

Add a bounded catalog preparation task before backend feature work. Reserve 45-60 minutes from day one's existing setup/scaffolding time for creating and validating a CSV of 10-20 products. Extend day one into the optional fourth hour if needed. Suggested fields: sku, name, price_cents, currency, source_url, observed_at, source_type. Keep initial demo stock in a separate seed file. Prices are dated snapshots, not current-price guarantees. Product metadata imports must never overwrite inventory counts or reservation state.

Do not make scraping the entire Walmart website a release dependency. It does not supply authoritative shelf counts, physical cart contents, reservation history, or store checkout events. Begin with fictional products or a dataset whose reuse conditions have been verified. A catalog-source adapter can later support permitted collection/API input while keeping the reservation engine unchanged.

Walmart.ca robots.txt was read on October 9: it disallows search and several other paths, while allowing certain product URL patterns. Robots directives are not a grant of reuse permission. Walmart.ca's general terms could not be retrieved in the research check; the US Walmart.com terms explicitly restrict scraping without prior written consent and must not be assumed to be Canada's terms. Walmart Marketplace inventory APIs are seller-oriented, not access to all physical Walmart store stock.

If permitted collection is added, cap it at 10-20 approved product pages, cache results, record provenance, validate required fields, deduplicate by source ID, and stop on access denial/CAPTCHA or rate limiting. Do not bypass access challenges. Stage imports for preview; a malformed or empty response must not delete the existing catalog, change price to zero, or set inventory to zero. No product images are required for week one; use original placeholders.

References:
- https://www.walmart.ca/robots.txt
- https://www.walmart.com/help/article/walmart-com-terms-of-use/3b75080af40340d6bbd596f116fae5a0/
- https://developer.walmart.com/global-marketplace/docs/inventory-api-overview

Budget 24.5 hours: 3.5 hours per day. Starting Wednesday October 7, 2026 would make day seven Tuesday October 13; shift these dates if starting later. The outcome is a narrow local working release, not a production retail system. If C#/React/PostgreSQL are unfamiliar, this is an aggressive estimate. Protect inventory correctness and cut cosmetic features first.

The daily allocation includes learning, coding, and checks. The optional fourth hour is a buffer for setup or debugging. At only three hours/day, use the reduced scope described below.

## Tools and preparation

| Tool | Purpose | Current evidence / next action |
| --- | --- | --- |
| .NET 10 SDK | Backend and expiry worker | Installed: 10.0.401 |
| Node.js and npm | React/Vite frontend | Node 24.18.0 installed; verify npm on day 1 |
| Git | Version control | Installed |
| PostgreSQL | Durable shared inventory and transactions | Not found on PATH; check for existing installation before installing |
| VS Code or preferred editor | Code editing | Choose existing editor; no paid IDE required |
| Npgsql | PostgreSQL access from C# | Restore package during backend setup |
| xUnit | Integration tests | Restore during test setup |
| Playwright | One browser workflow | Install only needed browser during day 6 |
| GitHub Actions | Build/test workflow | Configure after local tests work |

Use a currently supported PostgreSQL release. A native installation is sufficient; Docker is optional. Create separate application and test databases with restricted application users. Store connection strings in .NET user secrets or untracked local environment configuration; commit `.env.example` with placeholders only. Never use real credentials or stock records in the demo.

Suggested final layout:

```text
inventory-reservation/
  backend/ShelfSync.Api/
  frontend/
  tests/ShelfSync.IntegrationTests/
  tests/browser/
  database/schema.sql
  database/seed.sql
  docs/
  README.md
```

## Day 1: foundation and one readable inventory screen (3.5 h)

- 45 min: install/verify PostgreSQL, create databases and local configuration.
- 45 min: scaffold backend and React/TypeScript frontend, configure dev proxy.
- 60 min: apply schema, seed products, implement health and product-list endpoints.
- 30 min: render cards with shelf stock, reserved, picked, available, and status.
- 30 min: explain the stock formula; verify counts against SQL; commit work.

Done: restart the API and still see seeded stock. If setup exceeds its budget, simplify styling and omit filters. Do not replace PostgreSQL with an in-memory database for concurrency tests.

## Day 2: reservations and repeat-safe requests (3.5 h)

- 30 min: implement request validation and the idempotency record.
- 90 min: implement transaction, stock row locking, availability check, order and reservation creation, and ledger event.
- 45 min: implement customer reserve/status screens and ambiguous-result recovery.
- 45 min: test final-unit competition, insufficient quantity, duplicate request, and same-key/different-payload rejection.

Done: two concurrent requests for one unit create exactly one successful reservation. A retry returns the original order rather than creating another.

## Day 3: store sales and shortage recovery (3.5 h)

- 60 min: implement POS-event deduplication and shelf-stock changes atomically.
- 45 min: detect shortages, choose affected reservations deterministically, and quarantine suspect inventory.
- 45 min: implement operations checkout simulator and exception banner.
- 30 min: implement count reconciliation with operator reason and ledger entry.
- 30 min: run the three-bag scenario and duplicate-sale test.

Done: sell all three before picking; online order becomes shortage pending, online availability is zero, and reconciliation is explicit. An impossible count is preserved as a discrepancy rather than silently clamped.

## Day 4: picking, expiry, cancellation, physical return (3.5 h)

- 60 min: implement pick/fulfill/cancel/restock transitions.
- 45 min: implement expiry worker plus lazy expiration inside relevant stock transactions.
- 45 min: add picker screen and expiry clock using server-provided timestamps.
- 60 min: test pick-versus-expiry race, worker restart, and cancellation of picked stock.

Done: expiry cannot undo a completed pick. Canceling picked stock does not make it sellable until restocking is confirmed.

## Day 5: safe failures and usable screens (3.5 h)

- 45 min: central error responses, request IDs, and user-facing recovery messages.
- 45 min: enforce persisted POS feed freshness and per-SKU quarantine gate.
- 45 min: add development-only controlled fault points before commit and after commit/before response.
- 30 min: add operations history, status polling, and clear loading/error states.
- 45 min: test rollback, ambiguous outcome, stale feed, and bounded lock-timeout behavior.

Done: database failure never produces success; retry after a dropped response recovers the committed result. Failures are shown as unknown, pending, or unavailable as appropriate.

## Day 6: release verification and automated checks (3.5 h)

- 90 min: complete integration scenarios using PostgreSQL and isolated fixtures.
- 45 min: one Playwright flow for reserve -> shortage -> cancel/reconcile.
- 45 min: GitHub Actions build and integration tests with a PostgreSQL service.
- 30 min: startup/configuration checks and review of logs for secrets.

Done: checks pass once on a clean setup. Fix failures before adding features. Report any check not actually run.

## Day 7: portfolio presentation (3.5 h)

- 60 min: close defects and verify a fresh database setup.
- 45 min: write README, architecture diagram, trade-offs, and limitations.
- 45 min: record a three-minute demo with the normal path and two failures.
- 30 min: capture screenshots and measured test results.
- 30 min: practice explaining locking, idempotency, stock states, and physical uncertainty.

Done: reproducible local release and reviewable portfolio. Publish code only after checking repository contents for secrets. Hosting is a follow-up, not a day-seven dependency.

## Required vs later

Required: stock correctness, idempotent mutations, shortage/quarantine flow, expiration, physical restock confirmation, PostgreSQL tests, three basic screens, setup documentation.

At 21 hours, cut: search/filtering, charts, configurable TTL UI, and elaborate styles. Keep a fixed demo TTL and basic tables. Browser test automation can be replaced by a recorded manual check if setup blocks it; document that limitation, while retaining database integration tests.

Later: multi-item carts, multiple stores, authenticated roles, substitutions, payments/refunds, email/webhooks/outbox, mobile scanning, durable real POS integrations, optimization, production hosting, and load testing. Each needs its own scope and verification.

## Responsibilities

Codex can scaffold, implement, write checks, troubleshoot, and prepare documentation. Shareef should spend 20-30 minutes of each day's budget understanding the change and running the demo. Account sign-in and any required installation approval remain user actions. Do not describe generated code as independently authored; demonstrate ownership through comprehension and improvements.
