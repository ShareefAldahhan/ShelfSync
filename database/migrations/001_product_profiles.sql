-- Additive migration: does not alter products, orders or stock values.
BEGIN;
CREATE TABLE IF NOT EXISTS product_profiles (
 product_id uuid PRIMARY KEY REFERENCES products(id),
 barcode text UNIQUE CHECK (barcode IS NULL OR length(btrim(barcode)) BETWEEN 1 AND 80),
 aisle text, bay text, shelf text, bin text,
 brand text, supplier text,
 case_pack integer CHECK (case_pack > 0),
 reorder_point integer CHECK (reorder_point >= 0),
 notes text,
 updated_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP
);
CREATE INDEX IF NOT EXISTS pos_events_product_history ON pos_events(product_id, committed_at DESC);
INSERT INTO product_profiles(product_id,barcode,aisle,bay,shelf,bin,notes)
SELECT id, sku, 'A' || lpad((((substring(sku from 4)::int)-1)/4+1)::text,2,'0'),
       lpad((((substring(sku from 4)::int)-1)%4+1)::text,2,'0'), '02', NULL,
       'Fictional demo shelf assignment. Barcode encodes the internal SKU, not a manufacturer UPC.'
FROM products WHERE sku ~ '^SS-[0-9]{3}$'
ON CONFLICT(product_id) DO NOTHING;
COMMIT;
