BEGIN;
SELECT pg_advisory_xact_lock(78314025);
ALTER TABLE payments ADD COLUMN IF NOT EXISTS cart_id uuid;
CREATE TABLE IF NOT EXISTS payment_orders(
 payment_id uuid NOT NULL REFERENCES payments(id) ON DELETE CASCADE,
 order_id uuid NOT NULL REFERENCES orders(id) ON DELETE CASCADE,
 PRIMARY KEY(payment_id,order_id));
CREATE INDEX IF NOT EXISTS payment_orders_order ON payment_orders(order_id);
INSERT INTO payment_orders(payment_id,order_id) SELECT id,order_id FROM payments ON CONFLICT DO NOTHING;
CREATE OR REPLACE FUNCTION link_payment_anchor() RETURNS trigger LANGUAGE plpgsql AS $fn$
BEGIN
 INSERT INTO payment_orders(payment_id,order_id) VALUES(NEW.id,NEW.order_id) ON CONFLICT DO NOTHING;
 RETURN NEW;
END $fn$;
DROP TRIGGER IF EXISTS payment_anchor_link ON payments;
CREATE TRIGGER payment_anchor_link AFTER INSERT ON payments FOR EACH ROW EXECUTE FUNCTION link_payment_anchor();
CREATE OR REPLACE VIEW order_payment_status AS
 SELECT p.*,l.order_id AS linked_order_id FROM payment_orders l JOIN payments p ON p.id=l.payment_id;
INSERT INTO schema_migrations(version) VALUES('005_checkout_first') ON CONFLICT DO NOTHING;
COMMIT;
