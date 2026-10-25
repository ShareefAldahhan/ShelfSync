# Store workflow extension

The local release adds receiving, backroom replenishment, returns, cancellation of picked orders, low-stock views, keyboard barcode lookup, atomic multi-product reservations, printable order receipts, inventory-history search, paginated orders, responsive forms and local mail previews.

## Access and operation

All staff can use Stockroom to scan an internal SKU or barcode with a keyboard-mode USB/Bluetooth scanner, receive deliveries and replenish shelves. Enter quantity, reason and optional delivery/transfer reference. Receiving increases backroom stock only; replenishment transfers stock from backroom to shelf. Low-stock filtering uses available shelf units after live reservations and safety buffer, at or below the configured inventory reorder point (default 5), or quarantine. The threshold is a database setting in this release. Stockroom shows shelf, picked, backroom and damaged quantities separately. Phone camera capture is not implemented; typing the barcode is supported.

Managers/Admins can return fulfilled online orders or POS sales, up to the quantity sold minus all earlier returns. Select sellable backroom or damaged stock. Returns do not automatically release shelf inventory and do not process refunds. Stock disposition is separate from payment processing. Managers/Admins can cancel a PICKED order by specifying its physical destination and reason. This removes the picked units once and closes the order; canceled units cannot later be fulfilled.

Products can be added to a pickup cart. A cart supports 1–30 distinct products, quantities 1–100 each. Reserving locks inventory in product-ID order, captures current prices, checks live availability and creates all product orders in one transaction. Any rejected line rolls the whole cart back. Orders share a pickup group ID, but may be fulfilled independently. Existing one-product reservation remains supported. Retry identifiers are scoped to the logged-in account and saved before sending. Cart checkout preserves additions made after an uncertain request when the original request is later resolved.

Reservations provides 100-row pages and per-order summaries with captured prices and state; after verified payment, printable receipts include returned quantity. Receipt authorization follows order ownership. History provides server-side search across SKU, name, event type and metadata, in 50-row pages. Order filters apply to the currently loaded page. Offset pagination is intended for this local store; concurrent changes may shift page boundaries.

## Local email preview

No external mail is sent and no email provider is configured. Admins open Email preview to see the latest 100 order-status notifications and password-recovery messages. Notifications are committed with order creation/status changes using an invoker-rights database trigger; a transaction rollback also removes its messages. Cart reservation adds a pickup-group summary.

Forgot password can request a recovery message by username. It always gives the same public response for unknown, disabled, protected and eligible accounts. Recovery tokens are never returned to anonymous callers. An Admin verifies the person and shares the code privately from the protected preview inbox. Codes expire after 30 minutes and become unusable after password reset. This is an administrator-assisted local mailbox simulation, not proof of email ownership or production email delivery. The existing direct Admin code issuance remains available. The protected operator account still uses Set-Operator.ps1.

## Upgrade

Apply migration 003 only after explicit local administrator approval:

```powershell
pwsh -File scripts/Apply-Migrations.ps1 -EnableAccounts -EnableProfileEditing -EnableStoreOperations
```

The migration adds backroom/damaged/reorder counts (zero backroom/damaged for existing products), nullable cart IDs, return records, local mail previews and the order-notification trigger. Existing shelf/picked quantities and orders are retained. The app role receives SELECT/INSERT on inventory_returns and mail_preview; existing inventory/order write grants already cover new columns. No superuser or schema ownership is granted. Keep preview-message storage and backups private because recovery codes can appear there. Install the new binary after schema/grants succeed.

## Verification

82 database/policy tests cover receiving replay, transfers and overdraw, return bounds, concurrent returns, picked cancellation versus fulfillment, cart rollback, ownership and captured prices. Published HTTP checks cover stock permissions, carts, receipt ownership, cancellation, pagination and local previews in addition to the existing account and inventory checks. Build and lint cover the new UI; physical scanner hardware and phone-camera capture are not claimed as tested. New screen visual review is still pending if browser policy blocks inspection.
