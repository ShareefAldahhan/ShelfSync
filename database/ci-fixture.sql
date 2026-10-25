-- Disposable integration-test fixture only. Never load into a store database.
BEGIN;
DO $$ BEGIN
    IF current_database() <> 'shelfsync_test' THEN
        RAISE EXCEPTION 'CI fixture requires the isolated shelfsync_test database';
    END IF;
END $$;
INSERT INTO products(id,sku,name,price_cents)
VALUES ('f0000000-0000-4000-8000-000000000001','CI-HTTP','CI workflow product',100);
INSERT INTO inventory(product_id,shelf_units,picked_units,safety_buffer,quarantined)
VALUES ('f0000000-0000-4000-8000-000000000001',10,0,0,false);
INSERT INTO product_profiles(product_id,barcode,aisle,bay,shelf,notes)
VALUES ('f0000000-0000-4000-8000-000000000001','CI-HTTP','A01','03','02','Disposable CI fixture.');
COMMIT;
