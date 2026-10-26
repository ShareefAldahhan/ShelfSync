# ShelfSync: portfolio and interview guide

## Positioning

ShelfSync is a full-stack store operations and pickup application built with .NET 10, React/TypeScript, PostgreSQL and Stripe test Checkout. Its strongest engineering story is keeping inventory and payment state consistent through concurrent requests, lost responses, payment delays and retries. It includes four server-enforced account roles, customer-owned orders, associate workflows, an audit trail and a verified local release pipeline.

The repository and README are the project presentation. The application runs locally with fictional products and Stripe test payments. No separate explanatory website is required.

## Resume-ready project entry

**ShelfSync | .NET 10, React, TypeScript, PostgreSQL, Stripe, Docker**

Choose three bullets to fit the resume:

- Engineered an inventory and pickup platform with transactional row locking, idempotent operations and audit trails to protect stock across concurrent reservations, POS sales and order fulfillment.
- Implemented four-role authorization, customer-owned orders, CSRF protection and Stripe sandbox checkout with server-calculated totals, signed-webhook verification and payment reconciliation before associate picking.
- Diagnosed PostgreSQL connection exhaustion under load and consolidated connection pools; verified zero errors across 274,265 requests, including approximately 3,700 API requests/sec in a local browsing benchmark with 200 concurrent workers.
- Built a repeatable release gate with 135 backend tests, frontend checks, published API permission tests, migration replay, backup/restore verification and checksummed deployment artifacts.

Use https://github.com/ShareefAldahhan/ShelfSync as the project link. Do not describe the local load test as production throughput, 200 real shoppers, or an SLA. Docker configuration exists; container runtime validation and remote CI results must be verified separately before claiming them.

## Five-minute recruiter walkthrough

| Time | Demonstration | Engineering point |
|---|---|---|
| 0:00-0:30 | Show the storefront and explain the customer/associate problem | A clear product and domain model |
| 0:30-1:30 | Customer adds items, checks out with Stripe test details and views confirmation/receipt | Server-owned totals and payment-confirmed workflow |
| 1:30-2:15 | In a separate prepared associate session, inspect barcode/bin and pick the paid customer order | Server-enforced roles and operational UI |
| 2:15-3:00 | Show stock movement and audit history | Traceability and transaction boundaries |
| 3:00-4:00 | Show the benchmark table and explain the connection-pool failure/fix | Measured diagnosis rather than guessed scalability |
| 4:00-5:00 | Show test/release evidence and one architecture diagram | Reproducibility, recovery and honest limitations |

Use a separate demo database. Do not publish Admin credentials or give public visitors management privileges. Use test payment details only; do not enter a real card. For a local demonstration, use separate Customer and Associate sessions. A full application recording is not included.

## Interview deep dives

1. Trace two requests racing for the final unit. Explain the inventory row lock, availability recheck and transaction commit.
2. Explain why reservation state and payment state are separate, and why paid orders can still be RESERVED before picking.
3. Explain the unknown-result case: commit succeeds but the response is lost. Show the saved idempotency key and payload-conflict checks.
4. Explain checkout safety holds and why provider-confirmed expiry is required before releasing stock.
5. Walk through the connection-pool failure: independent pools exceeded PostgreSQL capacity; a shared bounded pool fixed request errors, and a configuration regression test protects maintenance headroom.
6. Explain the trusted reverse-proxy boundary and why accepting arbitrary forwarded HTTPS/client-IP headers is unsafe.
7. Describe what the restore test proves and what it does not: fixture data round-trips, while real operational backups and external payment records need a separate recovery plan.

## Evidence

- [Benchmark results](benchmark-results-2026-10-25.md): workload, machine, concurrency, p95/p99, error counts and limitations.
- [Shipping verification](shipping-report-2026-10-25.md): local release tests and checks.
- [Architecture](architecture.md), [API contract](api-contract.md), [checkout state model](checkout-first.md).
- [Hosting configuration reference](public-deployment.md): optional future deployment settings.

The earlier Stripe test payment was confirmed through provider reconciliation. Real SDK signature tests pass, but successful delivery of that payment's signed webhook was not observed. Demonstrate an actual hosted webhook before claiming that complete path has been verified publicly.
