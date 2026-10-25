BEGIN;
SELECT pg_advisory_xact_lock(78314024);
CREATE TABLE IF NOT EXISTS payments (
 id uuid PRIMARY KEY, order_id uuid NOT NULL REFERENCES orders(id) ON DELETE CASCADE,
 owner_user_id uuid NOT NULL REFERENCES app_users("Id"), amount_cents bigint NOT NULL CHECK(amount_cents>0),
 currency text NOT NULL CHECK(currency='usd'), state text NOT NULL CHECK(state IN ('READY','OPEN','PROCESSING','PAID','EXPIRED','FAILED','REVIEW','REFUNDED')),
 stripe_session_id text UNIQUE, checkout_url text, created_at timestamptz NOT NULL DEFAULT clock_timestamp(), updated_at timestamptz NOT NULL DEFAULT clock_timestamp());
CREATE UNIQUE INDEX IF NOT EXISTS payments_active_order ON payments(order_id) WHERE state IN ('READY','OPEN','PROCESSING','PAID','REVIEW');
CREATE TABLE IF NOT EXISTS payment_events(event_id text PRIMARY KEY,payment_id uuid NOT NULL REFERENCES payments(id) ON DELETE CASCADE,kind text NOT NULL,created_at timestamptz NOT NULL DEFAULT clock_timestamp());
-- Stock returns record disposition only. Payment refunds remain a separate provider operation.
INSERT INTO schema_migrations(version) VALUES('004_payments') ON CONFLICT DO NOTHING;
COMMIT;
