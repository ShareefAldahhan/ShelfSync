BEGIN;
SELECT pg_advisory_xact_lock(78314023);
ALTER TABLE inventory ADD COLUMN IF NOT EXISTS backroom_units integer NOT NULL DEFAULT 0 CHECK(backroom_units BETWEEN 0 AND 1000000);
ALTER TABLE inventory ADD COLUMN IF NOT EXISTS damaged_units integer NOT NULL DEFAULT 0 CHECK(damaged_units BETWEEN 0 AND 1000000);
ALTER TABLE inventory ADD COLUMN IF NOT EXISTS reorder_point integer NOT NULL DEFAULT 5 CHECK(reorder_point BETWEEN 0 AND 1000000);
ALTER TABLE orders ADD COLUMN IF NOT EXISTS cart_id uuid;
CREATE INDEX IF NOT EXISTS orders_cart ON orders(cart_id) WHERE cart_id IS NOT NULL;
CREATE TABLE IF NOT EXISTS inventory_returns(
 id uuid PRIMARY KEY, product_id uuid NOT NULL REFERENCES products(id) ON DELETE CASCADE,
 order_id uuid REFERENCES orders(id) ON DELETE CASCADE, pos_event_id uuid REFERENCES pos_events(event_id) ON DELETE CASCADE,
 quantity integer NOT NULL CHECK(quantity BETWEEN 1 AND 100), destination text NOT NULL CHECK(destination IN ('backroom','damaged')),
 reason text NOT NULL, actor text NOT NULL, created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 CHECK(num_nonnulls(order_id,pos_event_id)=1));
CREATE INDEX IF NOT EXISTS returns_order ON inventory_returns(order_id);
CREATE INDEX IF NOT EXISTS returns_pos ON inventory_returns(pos_event_id);
CREATE TABLE IF NOT EXISTS mail_preview(
 id uuid PRIMARY KEY, user_id uuid NOT NULL REFERENCES app_users("Id") ON DELETE CASCADE,
 recipient text NOT NULL, subject text NOT NULL, body text NOT NULL, created_at timestamptz NOT NULL DEFAULT clock_timestamp());
CREATE INDEX IF NOT EXISTS mail_preview_user ON mail_preview(user_id,created_at DESC);
CREATE OR REPLACE FUNCTION queue_order_preview() RETURNS trigger LANGUAGE plpgsql AS $fn$
BEGIN
 IF NEW.owner_user_id IS NOT NULL AND (TG_OP='INSERT' OR OLD.state IS DISTINCT FROM NEW.state) THEN
  INSERT INTO mail_preview(id,user_id,recipient,subject,body)
   SELECT gen_random_uuid(),"Id",COALESCE("Email","UserName"),'Order '||NEW.state,
    'Order '||NEW.id||': '||NEW.quantity||' unit(s), status '||NEW.state||'. Reservation expiry: '||NEW.expires_at||'. No payment was collected by ShelfSync.'
   FROM app_users WHERE "Id"=NEW.owner_user_id;
 END IF;
 RETURN NEW;
END $fn$;
DROP TRIGGER IF EXISTS order_mail_preview ON orders;
CREATE TRIGGER order_mail_preview AFTER INSERT OR UPDATE OF state ON orders FOR EACH ROW EXECUTE FUNCTION queue_order_preview();
INSERT INTO schema_migrations(version) VALUES('003_store_operations') ON CONFLICT DO NOTHING;
COMMIT;
