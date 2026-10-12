
-- ShelfSync v1 — PostgreSQL 18
-- Initial schema for a dedicated development database.
-- Execute through a controlled schema-management session,
-- never using the shelfsync_app runtime role.
--
-- All inventory writers must:
-- 1. Acquire product inventory row locks consistently.
-- 2. Recheck availability and order state after locking.
-- 3. Use database decision time captured after locking.
-- 4. Commit inventory, orders, events and replayable results atomically.

BEGIN;

CREATE TABLE products (
    id uuid PRIMARY KEY,
    sku text NOT NULL UNIQUE,
    name text NOT NULL,
    price_cents integer NOT NULL CHECK (price_cents >= 0),

    CHECK (length(btrim(sku)) > 0),
    CHECK (length(btrim(name)) > 0)
);

CREATE TABLE inventory (
    product_id uuid PRIMARY KEY REFERENCES products(id),
    shelf_units integer NOT NULL DEFAULT 0
        CHECK (shelf_units >= 0),
    picked_units integer NOT NULL DEFAULT 0
        CHECK (picked_units >= 0),
    safety_buffer integer NOT NULL DEFAULT 0
        CHECK (safety_buffer >= 0),

    quarantined boolean NOT NULL DEFAULT false,
    quarantine_reason text,
    updated_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,

    CHECK (
        (quarantined = false AND quarantine_reason IS NULL)
        OR
        (quarantined = true AND
         quarantine_reason IS NOT NULL AND
         length(btrim(quarantine_reason)) > 0)
    )
);

CREATE TABLE feed_status (
    id integer PRIMARY KEY CHECK (id = 1),
    last_heartbeat_at timestamptz
);

INSERT INTO feed_status (id) VALUES (1);

CREATE TABLE orders (
    id uuid PRIMARY KEY,
    product_id uuid NOT NULL REFERENCES products(id),

    quantity integer NOT NULL
        CHECK (quantity BETWEEN 1 AND 100),

    unit_price_cents integer NOT NULL
        CHECK (unit_price_cents >= 0),

    state text NOT NULL CHECK (
        state IN (
            'RESERVED',
            'PICKED',
            'SHORTAGE_PENDING',
            'RESTOCK_PENDING',
            'CANCELLED',
            'EXPIRED',
            'FULFILLED'
        )
    ),

    expires_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    reason text,

    CHECK (expires_at > created_at),
    CHECK (updated_at >= created_at)
);

CREATE INDEX orders_active_by_product
    ON orders (product_id, expires_at)
    WHERE state = 'RESERVED';

CREATE INDEX orders_queue
    ON orders (state, created_at);

CREATE TABLE operation_requests (
    operation text NOT NULL,
    request_key uuid NOT NULL,
    payload_hash text NOT NULL,

    response_status integer
        CHECK (response_status BETWEEN 100 AND 599),

    response_body jsonb,

    created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,

    PRIMARY KEY (operation, request_key),

    CHECK (length(btrim(operation)) > 0),
    CHECK (length(btrim(payload_hash)) > 0),

    CHECK (
        (response_status IS NULL AND response_body IS NULL)
        OR
        (response_status IS NOT NULL AND response_body IS NOT NULL)
    )
);

CREATE TABLE pos_events (
    event_id uuid PRIMARY KEY,
    product_id uuid NOT NULL REFERENCES products(id),

    quantity integer NOT NULL
        CHECK (quantity BETWEEN 1 AND 100),

    payload_hash text NOT NULL,
    response_body jsonb NOT NULL,
    committed_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,

    CHECK (length(btrim(payload_hash)) > 0)
);

CREATE TABLE inventory_events (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,

    product_id uuid NOT NULL REFERENCES products(id),
    order_id uuid REFERENCES orders(id),
    pos_event_id uuid REFERENCES pos_events(event_id),

    kind text NOT NULL CHECK (
        kind IN (
            'SEED',
            'RESERVE',
            'EXPIRE',
            'CANCEL',
            'SALE',
            'PICK',
            'MISSING',
            'SHORTAGE',
            'RESTOCK',
            'FULFILL',
            'RECEIVE',
            'RECONCILE'
        )
    ),

    shelf_delta integer NOT NULL DEFAULT 0,
    picked_delta integer NOT NULL DEFAULT 0,

    metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,

    CHECK (jsonb_typeof(metadata) = 'object')
);

CREATE INDEX inventory_events_history
    ON inventory_events (product_id, id);

COMMIT;
