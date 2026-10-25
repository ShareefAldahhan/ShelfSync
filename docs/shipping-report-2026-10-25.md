# ShelfSync 1.0.0 local shipping verification

Release candidate: `ShelfSync-1.0.0-local-20261025-165829`.

- 122 backend tests passed; zero failed/skipped.
- 3 frontend tests passed; build and lint passed.
- Published HTTP workflows, four-role access/ownership, account recovery/revocation, CSRF, tampered-cookie rejection and production HTTP rejection passed.
- Migrations 001-005 applied twice successfully on an isolated database.
- Custom-format backup restored to a separate disposable database; products, inventory, orders, accounts, payments and payment-link fingerprints matched.
- Production frontend audit reported zero vulnerabilities; backend/transitive package advisory scan reported none.
- Package verifier accepted intact files and rejected modified, missing, unexpected and escaping paths.
- New schema-readiness checks prevent a connected but unmigrated database from appearing ready.
- Release test-process launches now handle absolute Windows paths containing spaces.
- Configuration helpers work without a source project directory; private values are passed to user-secrets on stdin.

The package includes the compiled dashboard/API, schema, migrations, current documentation, setup/start helpers, a SHA-256 file manifest and an external ZIP checksum. No actual store credentials or backups are included. Source is not committed by this task; release metadata records local changes.

The existing store database and running app were not changed by verification. The release is for local deployment, test-mode Stripe and local email previews. Public hosting, real-money payments, external email, remote CI, Docker runtime validation and an independent penetration test remain outside this verified scope. The earlier test payment was confirmed by provider reconciliation; actual signed-webhook delivery for that payment was not observed.
