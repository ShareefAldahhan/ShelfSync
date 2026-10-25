# Setup

## Prerequisites

Use PowerShell 7, .NET 10 SDK, Node.js 24 and PostgreSQL 18. PostgreSQL tools are expected at `C:\Program Files\PostgreSQL\18\bin`; `Verify-Release.ps1 -PostgresBin <path>` supports another installation. The verification port defaults to 55439 and can be changed with `-Port`.

Clone the repository and open its root in PowerShell. Do not apply schema creation scripts to an existing populated database.

## New development database

With a database administrator, provision a dedicated `shelfsync_dev` database. Apply `database/schema.sql` once, then optionally `database/seed.sql` for fictional demo products. The schema script creates tables, constraints and indexes; it does not provision database roles or passwords.

Create a dedicated application login with a securely chosen password. Grant CONNECT on the development database, USAGE on its schema, SELECT on products, SELECT/UPDATE on inventory, SELECT/INSERT/UPDATE on orders, pos_events and operation_requests, SELECT/INSERT on inventory_events, SELECT/UPDATE on feed_status, and USAGE/SELECT on sequences. Use a separate non-login owner role for schema ownership; do not run the application as a database superuser or grant it test-trigger ownership permissions.

Adapt these grants to your chosen role and schema names in an administrative database session. Database and role creation should be intentional and specific to the local environment; the release scripts never reset the existing development database.

## Configuration

The backend reads `ConnectionStrings:ShelfSync` from .NET user secrets in Development, or `ConnectionStrings__ShelfSync` from environment configuration. The connection must identify the host, port, database, application username and password. Configure secrets privately using your editor's user-secrets support or a secure process environment; do not paste passwords into chat, source code or committed configuration files.

Run `pwsh -File scripts/Set-Operator.ps1` to set the operator username/password hash in local user secrets. It prompts for the password without echoing it and requires at least 12 characters. `-Username <name>` changes the default `operator` name.

No database schema migration runs automatically at API startup. Startup rejects a missing required schema with an actionable message. `/health/ready` also checks required tables, views and columns. Diagnose failures through local logs; never repair them by blindly recreating a populated database.

## Startup

Run `pwsh -File scripts/Start-Local.ps1 -Build` once. Subsequent starts use `pwsh -File scripts/Start-Local.ps1`. The script sets Development and binds to loopback localhost:5000. Stop with Ctrl+C.

For separate development servers, run `dotnet run --project backend/ShelfSync.Api --urls http://localhost:5000`, then `npm ci` and `npm run dev` in `frontend/`. Browse localhost:5173.

## Tests without touching existing databases

Run `pwsh -File scripts/Verify-Release.ps1`. It creates its own isolated test cluster, database and credentials; ownership-dependent tests work without increasing application privileges. The test schema and temporary fixture data stay inside that cluster, which is stopped at the end. Diagnostic data remains in ignored artifacts.

For an independently maintained test database, set `SHELFSYNC_TEST_CONNECTION` privately and run `dotnet test tests/ShelfSync.Tests`. Its database name must be exactly `shelfsync_test`; the rollback trigger test needs a test account owning the tables.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| Startup reports missing operator hash | Run Set-Operator.ps1 for this backend project; restart |
| Sign-in fails | Check configured username; account lockout lasts 15 minutes after five failed passwords; the IP limit resets after one minute |
| Readiness returns 503 | Check PostgreSQL service, host, credentials and schema |
| API rejects HTTP outside Development | Use the loopback startup script locally; production needs HTTPS |
| Port already used | Stop the instance using that port, or choose a different test port |
| Old dashboard after source changes | Rebuild with Start-Local.ps1 -Build, then refresh the browser |
| Uncertain mutation | Retry the saved operation in the same tab before closing it |

## Additive migrations

After creating a fresh database with schema.sql, apply SQL files from database/migrations in filename order, then optionally seed fictional products. For existing databases, apply migrations using a schema-owner session; do not rerun schema.sql. The product-profile migration also seeds locations for existing SS products. Grant the app SELECT on product_profiles after applying that migration; it does not need ownership. See associate-details.md for the approval-based local helper and rollout steps.

For operator location editing, run the approved local helper with `-EnableProfileEditing` to additionally grant INSERT/UPDATE on product_profiles. This grant was explicitly approved and applied on the original development machine. Stock corrections use existing inventory/ledger/operation permissions. Never grant the application schema ownership or administrator access for this feature.


Individual accounts require migration `002_accounts.sql` and the table/sequence grants described in [accounts and access](accounts.md). Run the approved local helper with `-EnableAccounts -EnableProfileEditing`. This is an explicit administrator step, never an automatic startup migration.


Stockroom/cart/local-preview features require migration 003 and the `-EnableStoreOperations` flag on the explicitly approved migration helper. See [store operations](store-operations.md).

## Shipping package

The current release requires every migration through `005_checkout_first.sql` and its restricted grants, including payments and checkout links even when Stripe is disabled. See [payments](payments.md) and [checkout-first](checkout-first.md). `Set-Operator.ps1` and `Set-Payments.ps1` work from an extracted package using the fixed user-secrets ID; they do not require the source project directory. They still require the .NET SDK.

For an extracted verified release, use `scripts/Start-Package.ps1`, which checks package integrity. See [the release guide](local-release.md) for backup, restore, upgrade and rollback procedures.
