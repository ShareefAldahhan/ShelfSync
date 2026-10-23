# Associate item details

Click a product image or title in the storefront to open `#product/<uuid>`. Links survive browser reloads and support browser back/forward. The view remains protected by the existing Operator session.

The page shows a scannable internal Code 128 barcode, SKU and product ID; current retail price; aisle/bay/shelf/bin; total on-hand, shelf, picked, active reserved, safety buffer and available quantities; quarantine status/reason; last inventory update and snapshot time; case pack, supplier, brand and reorder threshold when recorded; a reservation action; POS sales history; and the inventory/order activity ledger.

## Data integrity

`GET /api/products/{id}/details` uses a repeatable-read database transaction so quantities and history come from a consistent snapshot. Live reserved units count only RESERVED orders whose expiry is later than the snapshot time. Available units are `max(0, shelf - liveReserved - safetyBuffer)`, or zero when quarantined. Picked stock is already off the shelf and is not subtracted twice.

Sales history is the latest 100 committed POS events, not fabricated chart data. The 30-day POS unit total is calculated over all matching events, independently of that 100-row display cap. Online fulfilled units are counted separately from FULFILL ledger events. Inventory activity is the latest 100 events. Historical POS prices are not stored, so the page does not calculate revenue using today's price. Unknown products return 404.

Missing operational fields display as Not recorded/Not assigned. Existing SS demo products receive fictional locations and their own SKU as an internal barcode; no manufacturer UPC or real store location is invented. Other products use SKU barcode fallback until their profile is configured. Barcode rendering uses [JsBarcode](https://github.com/lindell/JsBarcode).

## Database upgrade

Apply `database/migrations/001_product_profiles.sql` after `database/schema.sql`. The additive migration creates product_profiles and a POS history index; it does not alter stock quantities or orders. SS demo locations are inserted only where a profile does not already exist. Fresh verification databases and CI apply migrations automatically in filename order.

On an existing local development database, a schema administrator must apply it and grant the existing application role SELECT on product_profiles. The runtime app account deliberately lacks the required schema/reference privileges. Do not grant it administrator privileges.

`scripts/Apply-Migrations.ps1` prepares this upgrade for the named local shelfsync_dev or shelfsync_test database, prompts privately for the PostgreSQL administrator password, applies migrations and grants only SELECT on the new profile table. It must be run only after user approval of the database/access change. No administrator password is stored by the script. Existing development data is preserved.

The upgrade is not applied automatically at API startup. Publish the updated API and restart it after the migration; copying a new frontend alone cannot activate a new backend endpoint. The implementation is published in artifacts/publish and enabled on localhost:5000 after the approved migration.

## Verification

Four additional integration tests cover unknown products, metadata, expired-hold exclusion, safety buffer and quarantine, missing-profile fallback, replay-safe product-scoped POS history, 30-day filtering and fulfilled unit/activity totals. The suite now contains 46 tests. Published HTTP checks also assert the details endpoint's quantities, history and 404 handling.

Run `pwsh -File scripts/Verify-Release.ps1` with the current published package, or `-SkipPublish -PublishDirectory artifacts/item-preview` for the independently built preview. An optional `-Preview` starts a temporary loopback browser demo after verification using only the isolated test database. Stop it with Ctrl+C; its temporary credentials do not change saved operator credentials.

## Verified preview — 23 October 2026

All 46 integration tests passed in the isolated cluster. Published HTTP checks confirmed barcode/history data, available stock, replay-safe POS and online fulfillment totals and unknown-item 404 responses. Browser checks covered clicking through from a product, the overview/barcode/location display, sales and activity tabs, back navigation, and mobile page width; no captured console errors. The saved screenshot uses a clearly identified isolated test product, not fabricated production history.

![Associate details verified against an isolated test product](images/associate-details.png)

After explicit user approval, the administrator migration was completed on the local development database. The app account can read all 12 demo profiles. The tested backend/frontend were activated and the app restarted; readiness and authenticated product-details/barcode/security checks passed. Existing stock and orders were preserved.
