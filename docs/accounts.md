# Accounts and access

ShelfSync web accounts are stored using ASP.NET Core Identity. The backend connects to PostgreSQL through one restricted application login. Web roles control API actions; database roles control what the backend process can read and write. A customer account is never a PostgreSQL login.

| Capability | Customer | Associate | Manager | Admin |
| --- | --- | --- | --- | --- |
| Browse catalog | Yes | Yes | Yes | Yes |
| Create reservations, pickup carts and own-order checkout | Yes | No | Yes | Yes |
| View/cancel orders | Own only | All | All | All |
| View barcode, location, inventory and sales history | No | Yes | Yes | Yes |
| Edit locations, pick, report shortage, fulfill, POS sale | No | Yes | Yes | Yes |
| Correct shelf count, resolve shortage | No | No | Yes | Yes |
| Create accounts, change roles, disable, reset passwords, view access audit | No | No | No | Yes |

Public registration always creates a Customer. Staff accounts are created by an Admin through Manage accounts. All ordinary accounts may change their own password. Passwords require 12–1024 characters; five failed password attempts lock an account for 15 minutes. An additional login IP limit permits five requests per minute. Account disablement, role changes and credential rotation invalidate existing sessions on their next request. Password hashes and security stamps are never returned by account APIs.

The existing configured Operator becomes the protected recovery Admin on first startup after migration, using the existing username and password hash. Rotate that account through `scripts/Set-Operator.ps1`, then restart. Its role and password cannot be changed through the website. Other accounts survive restarts. Access changes and password changes are audited without credential values.

## Existing local database upgrade

Back up the database before deployment using your normal PostgreSQL backup procedure. With explicit local administrator approval, run:

```powershell
pwsh -File scripts/Apply-Migrations.ps1 -EnableAccounts -EnableProfileEditing
```

The helper prompts privately for the PostgreSQL administrator password. Migration 002 adds the Identity tables, append-only application access audit, a nullable orders.owner_user_id foreign key and a customer order index. It does not change inventory or assign existing orders to a customer. Legacy orders remain visible only to staff. Migrations can be rerun safely; do not rerun schema.sql against an existing database.

The application role receives SELECT/INSERT/UPDATE/DELETE on Identity tables, SELECT/INSERT on access_audit and USAGE/SELECT on the two Identity claim sequences. It receives no schema ownership, superuser access or permission to alter existing roles. The schema owner retains DDL responsibility. Start the new binary only after these grants succeed.

The migration helper requires the connection already configured in development user secrets. It supports only the named local shelfsync_dev and shelfsync_test databases.

## Verification and release scope

Use `scripts/Verify-Release.ps1` to build and verify against an isolated PostgreSQL instance. Its checks cover permission denials, owner spoofing, cross-customer cancellation, customer-only registration, session invalidation, recovery protection, resets and lockout, alongside transactional inventory workflows. HTTP verification scripts intentionally reject development databases because their test accounts are disposable.

The supported release is a local deployable application. Hosting, production TLS, backups, monitoring and environment-specific recovery procedures remain deployment responsibilities. The tests establish the checked behavior; they do not promise absence of all defects.


## Forgot password

Select **Forgot password?** on the sign-in page. This local release uses administrator-assisted recovery because no verified email or mail provider is configured. After verifying the account holder's identity, an Admin selects the account in Manage accounts, enters the verification reason, and clicks **Issue recovery code**. Share the code privately with that person. They enter their username, code and new password on the recovery screen. Never share codes in public chat or screenshots.

Codes use ASP.NET Core Identity's protected reset tokens, expire after 30 minutes and become invalid when used or replaced. Issuing a replacement also revokes active sessions. Successful recovery clears password lockout and invalidates sessions. Disabled accounts cannot recover; the protected recovery Admin continues to use Set-Operator.ps1. Codes are returned only to the authenticated Admin, held in browser memory and excluded from the audit. This feature requires no schema change.

Automatic email recovery requires a verified email enrollment and delivery service; it is not enabled in this local release.


The store extension adds administrator-only local email previews for requested recovery messages and order updates. No messages leave the machine; see [store operations](store-operations.md).


Associate picking: the picking API requires a live RESERVED order whose owner has the Customer role. Ownerless legacy orders and reservations made by staff accounts cannot be picked by an Associate, and their Pick control is hidden. Inventory and order state are locked and expiry is rechecked before stock movement. Restricted picking uses a distinct idempotency payload so an Associate cannot replay a manager-only legacy pick. Managers/Admins retain the legacy-order handling path.


Associate shopping controls: associates have item lookup, customer reservation handling, stockroom/history and POS operations. Personal cart/reservation buttons and cart/payment-shopping navigation are hidden; direct reservation, cart-reservation and payment-checkout API requests return 403. Customer shopping and Manager/Admin legacy/demo reservation access remain available. Associates can still view payment status for customer fulfillment.
