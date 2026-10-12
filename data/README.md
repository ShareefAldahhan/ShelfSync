# ShelfSync demo catalog

Fictional retail products and simulated inventory. Product brands, prices, and barcodes are for this portfolio demo only and are not Walmart data.


## Included files

- catalog.html: responsive offline gallery, opened directly in a browser.
- products.json/products.csv: 12 products with stable UUIDs, brand, category, size, invented CAD price, and asset paths.
- inventory.json/inventory.csv: separate fictional stock for DEMO-001.
- images/: 12 original 512 x 512 SVG packaging illustrations.
- barcodes/: 12 Code 39 SVG barcodes encoding SS-001 through SS-012.
- scenarios.json: three-bag shortage, final-unit race, and zero-stock fixtures.
- ../database/seed.sql: catalog and stock seed compatible with schema.sql; not executed yet.

## Integration

Asset paths are relative to this folder. Treat SKU/barcode fields as strings. Prices are integer CAD cents. UUIDs match between catalog, stock, and SQL. The current database products table supports id/sku/name/price; richer metadata stays in JSON until schema expansion. Copy assets into frontend public/catalog or serve them explicitly. Barcode lookup is not implemented yet.

Seed adds events only for newly inserted inventory rows; it does not overwrite stock or reset existing orders. It leaves feed health unverified: a simulator heartbeat is required before online reservations. SS-001 starts with 3 bags, SS-002 with 1, and SS-011 with 0.

## Barcode format

Code 39 supports alphanumeric local IDs. Start/stop symbols, 3:1 wide/narrow ratio, and white quiet zones are included. Optional Mod-43 checksum is not added; configure a scanner accordingly, with start/stop stripped. These are not registered UPC/EAN/GTIN identifiers and must not be used at a real retail checkout. Print/display on white without stretching the aspect ratio. Physical scanning is unverified.

## Verification and limitations

Generation checks: 12 unique SKUs and UUIDs, matching inventory and asset references, integer nonnegative prices, and 9-element/3-wide-element barcode patterns. Local command runner is failing, so files have not been reread by a runtime, SQL has not been executed, images have not been visually rendered, and barcode decoding is not tested. Render the gallery and test scanning when tools recover. This is a dataset and static gallery, not a working storefront.

Original fictional artwork and descriptions may be reused in the portfolio. No Walmart assets were scraped. Fictional naming is not a trademark-clearance claim.
