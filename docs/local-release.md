# ShelfSync 1.0.0 local release

This is the current release guide. [Earlier checkpoints](local-release-history.md) are historical and contain superseded behavior and test counts.

## Supported scope

A Windows local deployment with PostgreSQL 18, individual Customer/Associate/Manager/Admin accounts, inventory and associate workflows, checkout-first reservations, Stripe **test-only** hosted Checkout, paid receipts, and local email previews. Public signup creates Customers; Admin manages staff access. The configured operator is a protected recovery Admin.

Real payments, external email, public hosting and multi-instance operation are not enabled by this release. An actual Stripe test payment was confirmed; direct signed-webhook delivery for that payment was not observed, and provider reconciliation confirmed it instead. See [payment evidence](test-report-2026-10-25.md) and [checkout behavior](checkout-first.md).

## Produce a verified shipping package

Install PowerShell 7, .NET 10 SDK, Node.js 24 and PostgreSQL 18 tools, then run from the source root:

```powershell
pwsh -File scripts/Ship-Local.ps1
```

This creates a fresh versioned directory, runs the isolated release gate, checks dependency advisories, includes schema/migrations and documentation, verifies every packaged file, and creates a ZIP plus SHA-256 checksum under `artifacts/releases/`. No existing store database or running store process is modified. Credentials and backups are excluded. `release.json` records the version, Git revision, whether the source has uncommitted changes, required migrations and verified scope.

The gate checks:

- Backend tests, frontend tests, build and lint.
- All migrations applied twice to a disposable database.
- Published login, CSRF, session rotation, roles, customer ownership, order/inventory workflows, account management and production HTTP rejection.
- A database backup restored to another disposable database, with matching business/account data fingerprints.
- Production dependency advisories and package integrity.

The test cluster listens only on loopback and stops afterward. It has its own owner credentials; the store's application role needs no additional rights. Failed builds do not produce a verified archive. Diagnostic fixture data remains under ignored artifacts. Docker and remote CI execution are separate and are not claimed by a local pass.

## Install and start

1. Verify the ZIP checksum against the separately supplied `.sha256`, then extract into a new folder. Retain the previous release for rollback.
2. Follow [setup](setup.md): provision a dedicated PostgreSQL database and restricted application login; apply `schema.sql` only to a new database, then all migrations 001-005. Existing databases receive only missing additive migrations. Never recreate a populated database.
3. Configure the database connection privately in .NET user secrets (ID `f1743222-090c-4111-8a6c-12e064007dba`) or the process environment. Run the included `scripts/Set-Operator.ps1` to choose the recovery Admin password. Configuration scripts require the .NET SDK; running the package requires only the ASP.NET Core 10 runtime and PowerShell 7.
4. Optionally run `scripts/Set-Payments.ps1` with test credentials and arrange Stripe CLI forwarding to `/api/payments/stripe-webhook`. Checkout stays unavailable until configured. No external email is sent.
5. Run `pwsh -File scripts/Start-Package.ps1` from the extracted folder. It verifies the file manifest before starting on `http://localhost:5000`. `-Port` can select another loopback port; update the Stripe base URL and forwarding accordingly.
6. Confirm `/health/ready` returns ready, sign in, and inspect inventory and account access. Readiness checks required schema objects/columns, not only a database connection. Missing migrations fail startup with an actionable message; the application does not apply migrations automatically.

For source development, `Start-Local.ps1 -Build` still rebuilds the working publish directory. A verified shipping archive is produced by `Ship-Local.ps1`, not merely by a successful compile.

## Update, backup and rollback

Before updating a store with data, stop accepting new work, stop the application, and take a PostgreSQL custom-format backup using an authorized backup account. For example, `pg_dump -W -h 127.0.0.1 -U <backup-user> -d shelfsync_dev -Fc -f <private-backup-path>` prompts privately. Keep backups outside releases/source control with restricted access. Retain the same Windows service/user profile and its ASP.NET Data Protection keys, plus privately managed application configuration. A database backup alone does not back up those keys or external Stripe records.

Rehearse restore into a separate, newly created database using `pg_restore --no-owner --no-acl --exit-on-error`, then validate row contents, account access and grants. The automated restore test exercises fictional fixtures; it does not prove a real store backup is recoverable. Provision the restricted runtime role/grants deliberately on the destination.

Extract each upgrade into a new directory and verify its manifest. Apply only reviewed migrations after backup, then start the new release and check readiness. Roll back binaries only when the schema is compatible; do not automatically reverse migrations or overwrite the active database. Keep unresolved payment attempts visible for reconciliation, and never treat a database restore as reversing a Stripe transaction.

## Capacity and remaining public-launch work

See the [measured benchmark](benchmark-results-2026-10-25.md): 274,265 requests, zero final-run errors, and all tested workloads under 500 ms p95 at 100 concurrent API workers. This is not a real-shopper or production capacity guarantee. The application shares one pool with a default maximum of 80 connections; set explicit per-instance budgets if deploying elsewhere.

Public launch additionally requires a chosen host, HTTPS and trusted proxy verification, persistent protected data-protection keys, monitoring/alerts, tested real backups, production mail delivery, live-payment onboarding and a separate security/operational review. No public deployment, live charges, remote CI pass, or Docker runtime validation is claimed here.
