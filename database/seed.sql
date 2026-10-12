-- Dedicated fictional demo database only; requires schema.sql.
-- Does not overwrite existing inventory or reset orders.
BEGIN;
INSERT INTO products (id,sku,name,price_cents) VALUES
('00000000-0000-4000-8000-000000000001','SS-001','Crisp Corner Original Potato Chips 200 g',297),
('00000000-0000-4000-8000-000000000002','SS-002','Crisp Corner Smoky BBQ Potato Chips 200 g',297),
('00000000-0000-4000-8000-000000000003','SS-003','Meadow Table 2% Milk 2 L',489),
('00000000-0000-4000-8000-000000000004','SS-004','Meadow Table Whole Wheat Bread 675 g',279),
('00000000-0000-4000-8000-000000000005','SS-005','Meadow Table Large Eggs 12 count',429),
('00000000-0000-4000-8000-000000000006','SS-006','Orchard Lane Gala Apples 1.36 kg',597),
('00000000-0000-4000-8000-000000000007','SS-007','Morning Field Honey Oat Cereal 500 g',447),
('00000000-0000-4000-8000-000000000008','SS-008','Pantry Grove Black Beans 540 mL',147),
('00000000-0000-4000-8000-000000000009','SS-009','Clear Home Lemon Dish Soap 740 mL',327),
('00000000-0000-4000-8000-000000000010','SS-010','Clear Home Fresh Laundry Detergent 1.47 L',897),
('00000000-0000-4000-8000-000000000011','SS-011','Clear Home Paper Towels 6 rolls',997),
('00000000-0000-4000-8000-000000000012','SS-012','Bright Day Mint Toothpaste 100 mL',247)
ON CONFLICT (id) DO NOTHING;
WITH seeded AS (
INSERT INTO inventory (product_id,shelf_units,picked_units,safety_buffer,quarantined) VALUES
('00000000-0000-4000-8000-000000000001',3,0,0,false),
('00000000-0000-4000-8000-000000000002',1,0,0,false),
('00000000-0000-4000-8000-000000000003',8,0,1,false),
('00000000-0000-4000-8000-000000000004',6,0,0,false),
('00000000-0000-4000-8000-000000000005',5,0,1,false),
('00000000-0000-4000-8000-000000000006',10,0,0,false),
('00000000-0000-4000-8000-000000000007',7,0,0,false),
('00000000-0000-4000-8000-000000000008',12,0,0,false),
('00000000-0000-4000-8000-000000000009',9,0,0,false),
('00000000-0000-4000-8000-000000000010',4,0,0,false),
('00000000-0000-4000-8000-000000000011',0,0,0,false),
('00000000-0000-4000-8000-000000000012',6,0,0,false)
ON CONFLICT (product_id) DO NOTHING RETURNING product_id,shelf_units,picked_units
)
INSERT INTO inventory_events (product_id,kind,shelf_delta,picked_delta,metadata)
SELECT product_id,'SEED',shelf_units,picked_units,'{"source":"fictional_demo_v1"}'::jsonb FROM seeded;
-- Send simulator heartbeat separately; seed does not assert live inventory.
COMMIT;

