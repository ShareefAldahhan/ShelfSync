# ShelfSync 1.0.0 - local release

Includes the compiled API/dashboard, schema and migrations 001-005, setup helpers and documentation. No credentials, user data or database backups are included.

This release supports Customer, Associate, Manager and Admin accounts, inventory workflows, Stripe TEST-mode checkout and receipts, and local email previews. Real payments and external email are not enabled.

## Start

Use PowerShell 7 and ASP.NET Core 10 runtime. PostgreSQL 18 must be provisioned and configured; see [setup](docs/setup.md) and the [release guide](docs/local-release.md). The .NET SDK is needed only for the included user-secrets setup helpers. Node.js is not needed to run this package.

Verify the archive's SHA-256 against the separately supplied checksum, extract into a new directory, configure the restricted database connection privately, and set the recovery Admin credentials with `scripts/Set-Operator.ps1`. Do not rerun `schema.sql` on an existing database. No migrations are applied automatically.

```powershell
pwsh -File scripts/Test-Package.ps1
pwsh -File scripts/Start-Package.ps1
```

Open http://localhost:5000. Stop with Ctrl+C. This is loopback-only Development HTTP; do not expose it publicly. The original Windows account can reuse its configured .NET user secrets. On another machine, configure the user-secrets ID `f1743222-090c-4111-8a6c-12e064007dba` or private environment settings described in the release guide.

`release.json` records build provenance and verified scope. `manifest.json` detects changed, missing and unexpected package files; it is not a digital signature. Keep the separately obtained ZIP checksum as the integrity reference. Upgrades should be extracted into new folders, with the old release and a verified private database backup retained.

The release gate covers backend/frontend tests, published HTTP/access/security checks, migration replay, isolated backup/restore and dependency advisories. Public hosting, live payments, external email and container execution are outside the verified local scope.
