> Historical planning/learning document. Some proposals differ from the completed local MVP. See [current architecture](architecture.md), [API contract](api-contract.md), and [release guide](local-release.md) for implemented behavior and verification.

# ShelfSync implementation guide

This is the implementation order and release gate. It supplements the roadmap, API contract, schema, and acceptance scenarios. It does not claim implemented features or verified security. All work stays inside inventory-reservation.

## Delivery boundaries

21-28 hours is a target for a small local demonstrator. Complete public deployment, account security, disaster recovery, and independently verified security cannot responsibly be promised within that same budget. Implement the core in week one; complete the public-release gates before exposing write endpoints. A longer schedule is preferable to silently omitting safeguards.

Use OWASP ASVS 5.0 as a checklist, targeting applicable Level 2 requirements for the eventual authenticated public app. Maintain a requirement ID, applicable/not applicable decision, implementation reference, verification evidence, and outstanding issue. A checklist or scanner is not certification.

## 1. Establish repository and environment

Create backend, frontend, tests, deployment, and configuration folders under this project. Keep project-local GitHub workflows under .github/workflows when this project becomes its own repository; do not write workflows into the parent resume workspace. Decide repository boundaries before publishing.

Verify .NET 10, Node/npm, Git and PostgreSQL. Scaffold ASP.NET Core, React/TypeScript with Vite, xUnit, and Npgsql. Commit dependency lock files, document versions, and use npm ci in CI. Separate dev and test databases. Add .gitignore and .env.example; actual connection secrets go in local user secrets/environment settings. Do not embed credentials in scripts or frontend builds.

Deliverable: clean setup can build both projects, connect to a dedicated demo database, and run a health check. The local runner was unavailable when this guide was prepared; scaffolding and tool/package checks remain to be done.

## 2. Define threats, invariants, and policies

Write threat-model.md: assets, actors, trust boundaries, attack scenarios, mitigations, and unresolved risks. Include stolen staff sessions, customer access to another customer's order, spoofed POS events, duplicate events, malicious imports, stale stock, and privileged stock adjustments.

Document invariants: no two online holds consume the same availability; picked units are excluded from shelf units; state transitions move stock once; committed operations and ledger entries agree; retries do not duplicate effects; stale/quarantined inventory cannot receive new online holds. Negative externally reported shelf stock is preserved as a quarantined discrepancy.

Define reservation TTL, feed freshness, buffers, shortage priority policy, and cancellation/restock behavior as explicit demo assumptions. Document that shoppers' unscanned carts and true physical stock are not observable from the website.

## 3. Create schema and migrations

Apply versioned migrations to an empty demo DB and then seed.sql. Do not reset an existing database to get migrations to pass. Use one schema-management mechanism. Migration role can alter schema; runtime role can access required tables only and cannot alter/drop schema. Separate test credentials.

Add foreign keys, checks, unique keys, appropriate indexes, and immutable ledger semantics. Treat catalog import as metadata only. Validate asset paths; SVGs in this dataset are trusted checked-in assets, not user uploads. Do not serve untrusted SVG/HTML inline.

For authenticated release, add customer ownership and authenticated actor identifiers to orders and audit records. Scope idempotency uniqueness and lookups to principal, operation and key; include target resource and semantic request inputs in the hash. Role membership is server-managed.

Deliverable: fresh schema/seed works; repeat seed preserves stock; data constraints reject malformed records. Verify ledger reconstruction against stock after representative workflows.

## 4. Implement the transactional inventory core before UI mutations

Create InventoryService with small explicit operations: Reserve, ApplySale, Pick, ReportMissing, Cancel, Restock, Fulfill, Reconcile, Expire. Controllers validate and call services; SQL lives in the persistence layer. Avoid a sprawling generic repository or microservices.

Each mutation claims an idempotency key and locks inventory using SELECT FOR UPDATE inside a PostgreSQL transaction. Every writer follows the same lock order. Capture clock_timestamp() after acquiring locks for TTL/freshness decisions; transaction-start time can be stale after contention. Recheck availability and order state inside the lock, not only before it.

Write the order/stock changes, movement events, and replayable response atomically. Roll back before handling a PostgreSQL unique-key exception. Same key/different request is 409. A lost response after commit is recovered by replaying the same key. A 404 lookup alone is not proof that an in-flight operation failed. Bound lock wait, command timeout, and deadlock retries; do not retry irreversible side effects outside the transaction.

Expire only unpicked holds. Lazy expiry plus a restart-safe worker prevents stopped workers from indefinitely hiding stock. Canceling picked goods creates RESTOCK_PENDING; only physical return confirmation makes them sellable. External sales are deduplicated by authenticated source/event ID and may create shortages. Quarantine then reconcile rather than fabricate inventory.

Deliverable: PostgreSQL integration tests pass for concurrency, rollback, replay, shortage, and expiry races before building the complete frontend.

## 5. Implement API boundaries

Follow api-contract.md. Use parameterized SQL, positive bounded integer quantities, server-calculated prices, UUID validation, request-size limits and structured errors with correlation IDs. Never trust client stock, role, price, actor, or order ownership.

Use pagination for queues/history, cancellation tokens, bounded database pool and request timeouts. Avoid displaying internal SQL/stack traces. Separate liveness from readiness: readiness checks dependencies; liveness does not trigger restart storms just because the database is temporarily unavailable.

Document OpenAPI and test the contract. Catalog/status reads can be polled; stock changes require a committed authoritative response. Protect request-result lookup like the underlying order.

## 6. Build the responsive frontend

Create customer, picker, and operations routes with common API types. Product gallery uses our local images, descriptions, and demo prices. Customer flow is quantity -> reserve -> confirmed reference/deadline -> status/cancel. Picker has explicit physical confirmations. Operations shows shelf/reserved/picked/available, feed age, quarantine, adjustments and event history.

For each screen handle loading, empty, success, business rejection, dependency failure, and uncertain result states. Generate one request key per intentional action and reuse it on retries. Preserve only minimal pending-operation references, scoped to the signed-in account; clear them on sign-out. Do not put access tokens in localStorage. Recover status on reload; never treat cached counts as current.

Use keyboard navigation, visible focus, labels, screen-reader status messages, adequate contrast, touch-sized controls, and mobile layouts. Do not make reservation success an optimistic UI update. Duplicate-click prevention is helpful UX, not concurrency protection. Avoid rendering raw HTML from catalog text.

Deliverable: browser flow is usable on phone and desktop and gives correct recovery instructions. Offline order confirmation is disabled; PWA installability is later scope.

## 7. Add account security before public write access

Use ASP.NET Core Identity or a maintained OIDC identity provider; do not invent passwords, hashing, JWT signing, or reset protocols. For same-origin deployment prefer framework-managed sessions in Secure, HttpOnly cookies with appropriate SameSite policy. Apply antiforgery protection to unsafe cookie-authenticated requests, including login where applicable. SameSite alone is not the entire CSRF defense.

Implement customer/picker/operator policies on the server. Customer reads and mutations require order ownership; staff scopes are explicit. Test cross-user UUID substitution on every resource endpoint. Use generic login/reset responses, rate limiting, lockout controls, expiring single-use reset tokens, session expiry/revocation, and MFA for staff. Do not publicly expose a shared operator demo account that can perform privileged changes.

Persist and protect ASP.NET data-protection keys across restarts and instances. Never hard-code staff accounts or reuse production secrets in staging. POS integrations use a separate authenticated service principal with narrow permissions. Production events need durable upstream storage/replay; a heartbeat alone does not prove all events arrived.

Enforce HTTPS and appropriate HSTS after TLS is configured. Allow-list trusted reverse proxies/forwarded headers. Prefer same origin to minimize CORS; otherwise allow explicit origins, never wildcard credentialed access. Add CSP, nosniff and framing restrictions. Rate-limit login and mutations by account and relevant network context; evaluate bypass and denial-of-service risks.

Deliverable: authorization, ownership, CSRF, expired-session, secret-leak, and abuse tests pass. Anonymous mutation demo remains loopback-only until this gate is complete.

## 8. Make asynchronous effects recoverable

Week one needs only the database-backed expiry worker and polling. If adding emails/webhooks later, write an outbox record in the same transaction as the business change, dispatch separately, and deduplicate consumers. Retry with backoff and a bound; exhausted messages become visible for investigation/replay.

No external network call while inventory locks are held. No claim of exactly-once network delivery: aim for at-least-once delivery with idempotent effects. Two workers must not process the same job concurrently without coordination. Persist work across restart and expose failed/pending age metrics.

## 9. Verification and CI pipeline

Test domain rules, real PostgreSQL transactions, API authorization, and a small number of meaningful Playwright journeys. Use distinct connections and a synchronization barrier for competing final-unit requests. Assert database/ledger state, not only status codes. Inject faults before commit and after commit-before-response. Exercise duplicate sale, worker restart, stale feed, shortage, and pick/cancel/expiry races.

Test both migration from empty DB and upgrade from the previous schema. Run a modest documented load test later: hardware, dataset, concurrency, latency/error rate, pool usage, and lock contention. Do not invent production-scale performance claims.

PR CI order: locked dependency restore -> formatting/lint/type check -> backend/frontend build -> unit/integration tests with disposable PostgreSQL -> browser checks -> dependency/secret/static analysis -> package artifact. Triage security findings; do not suppress failures simply to get a green build.

GitHub workflow permissions default to read-only. Pin third-party actions to reviewed full commit SHAs, avoid evaluating untrusted PR text as shell code, and never expose deployment secrets to fork PRs. Build once, label by commit, record dependency inventory/SBOM where supported, and promote the same artifact to staging/production. Prefer provider-supported OIDC for short-lived deployment credentials with repository/environment-bound trust.

Deliverable: branch protection requires checks; CI failures block release. Configure protections according to available repository plan/features and record any unavailable control.

## 10. Host through a staged release

Recommended architecture: one container/process serving React assets and API at the same origin, plus persistent managed PostgreSQL on a private connection. Container runs non-root, contains no build secrets, has bounded resources and patched dependencies. Database must not be publicly writable. TLS terminates at a trusted managed ingress. Set runtime secrets through the provider secret store.

Choose provider after reviewing persistence, idle sleep, database expiry, backups, logs, secret support, cost limits and region. Local development can be free; durable hosted database and reliable recovery may cost money. Do not promise a production-quality free tier. Do not replace PostgreSQL with ephemeral container storage.

Create reproducible configuration/infrastructure definitions when a provider is chosen. Separate staging from production databases, secrets, identities, and origin. Release job runs a reviewed migration once; every application instance must not compete to migrate at startup. Use backward-compatible expand/contract changes and backups. Test staging smoke flows before promotion.

Rollback application artifact if needed, but do not blindly roll back schema or erase ledger events. Define compatible rollback and forward-fix paths before destructive migrations. Disable all development fault/reset/seed endpoints and debug details in production.

Deliverable: deployed smoke tests, TLS, sessions, secrets, readiness, migration process, and rollback rehearsal pass before inviting users.

## 11. Operate and recover

Structured logs contain correlation IDs, operation outcomes and safe actor references, not passwords, tokens, cookies or full sensitive payloads. Separate security/business audit events from disposable debug logs. Define retention, access, export and deletion policies; gather no unnecessary personal data.

Track API errors/latency, database availability, lock timeouts, stale feed, quarantine count, shortage rate, oldest outbox job, worker lag and backup age. Alert only on actionable thresholds; write runbooks for database outage, suspected compromised staff account, inventory mismatch and failed migration.

Set a documented recovery objective and data-loss tolerance appropriate to the demo. Enable backups and restore them into a separate environment; verify orders, stock and ledger consistency. A successful backup job is not proof of a successful restore. Document key rotation, patch cadence and incident steps.

Deliverable: someone can restart, troubleshoot, restore and reconcile the application without guessing.

## 12. Public-release gate

- Stock invariants and all critical concurrency/replay/failure cases verified on PostgreSQL.
- Server authentication, role policies, ownership, CSRF and session controls verified.
- No secrets in source/history, browser bundle, logs, or artifacts; rotate any exposed secret.
- CI checks and reviewed artifact promotion work; staging remains isolated.
- Persistent hosting, HTTPS, restrictive access, production-safe configuration verified.
- Migrations, backup restore and compatible rollback tested.
- Monitoring and failure runbooks present.
- Known limitations explicitly published; no real checkout/payment or Walmart integration claimed.

Track each gate as planned/implemented/verified/blocked. Record commands, test output and screenshots when run. Features are complete only with evidence, not because a document lists them.

## How to work through this with Codex

Start with steps 1-3, then one vertical slice: list products -> reserve -> show order -> prove final-unit concurrency. Next implement shortage and recovery, then picking/expiry. Add security before public hosting. Each work session ends with what changed, what was verified, and the next checkpoint. The one-week schedule remains the local core milestone; later gates extend it.

## References

- OWASP ASVS 5.0: https://github.com/OWASP/ASVS
- ASP.NET Core antiforgery: https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery
- PostgreSQL locking: https://www.postgresql.org/docs/18/explicit-locking.html
- GitHub Actions security: https://docs.github.com/en/actions/how-tos/secure-your-work
- Deployment OIDC: https://docs.github.com/en/actions/concepts/security/openid-connect
