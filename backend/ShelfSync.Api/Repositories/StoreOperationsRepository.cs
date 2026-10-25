using Npgsql;
using NpgsqlTypes;
namespace ShelfSync.Api.Repositories;
public sealed class StoreOperationsRepository(NpgsqlDataSource db)
{
    // JSON is constructed by PostgreSQL to keep the reporting query and its optional fields together.
    public async Task<string> StockAsync(CancellationToken ct=default)
    {
        await using var cmd=db.CreateCommand("""
        SELECT COALESCE(jsonb_agg(row_to_json(s) ORDER BY s.available,s.sku),'[]'::jsonb)::text FROM (
          SELECT p.id,p.sku,p.name,i.shelf_units AS "shelfUnits",i.backroom_units AS "backroomUnits",
            i.damaged_units AS "damagedUnits",i.picked_units AS "pickedUnits",COALESCE(pp.reorder_point,i.reorder_point) AS "reorderPoint",i.quarantined,
            CASE WHEN i.quarantined THEN 0 ELSE GREATEST(0,i.shelf_units-i.safety_buffer-COALESCE((SELECT SUM(quantity) FROM orders WHERE product_id=p.id AND state='RESERVED' AND expires_at>clock_timestamp()),0)) END AS available,
            pp.barcode,pp.aisle,pp.bay,pp.shelf,pp.bin
          FROM products p JOIN inventory i ON i.product_id=p.id LEFT JOIN product_profiles pp ON pp.product_id=p.id
        ) s
        """);return (string)(await cmd.ExecuteScalarAsync(ct))!;
    }
    public async Task<string> ReceiptAsync(Guid order,Guid? owner,CancellationToken ct=default)
    {
        await using var cmd=db.CreateCommand("""
        SELECT row_to_json(r)::text FROM (SELECT o.id,o.cart_id AS "cartId",p.sku,p.name,o.quantity,o.unit_price_cents AS "unitPriceCents",
          o.quantity::bigint*o.unit_price_cents AS "totalCents",o.state,(SELECT state FROM order_payment_status WHERE linked_order_id=o.id ORDER BY created_at DESC LIMIT 1) AS "paymentState",o.created_at AS "createdAt",o.expires_at AS "expiresAt",
          COALESCE((SELECT SUM(quantity) FROM inventory_returns WHERE order_id=o.id),0) AS "returnedQuantity",
          COALESCE((SELECT SUM(line.quantity::bigint*line.unit_price_cents) FROM payment_orders l JOIN orders line ON line.id=l.order_id
            WHERE l.payment_id=(SELECT id FROM order_payment_status WHERE linked_order_id=o.id ORDER BY created_at DESC LIMIT 1)),o.quantity::bigint*o.unit_price_cents) AS "checkoutTotalCents",
          COALESCE((SELECT jsonb_agg(jsonb_build_object('orderId',line.id,'name',item.name,'sku',item.sku,'quantity',line.quantity,'unitPriceCents',line.unit_price_cents,'totalCents',line.quantity::bigint*line.unit_price_cents) ORDER BY line.id)
            FROM payment_orders l JOIN orders line ON line.id=l.order_id JOIN products item ON item.id=line.product_id
            WHERE l.payment_id=(SELECT id FROM order_payment_status WHERE linked_order_id=o.id ORDER BY created_at DESC LIMIT 1)),
            jsonb_build_array(jsonb_build_object('orderId',o.id,'name',p.name,'sku',p.sku,'quantity',o.quantity,'unitPriceCents',o.unit_price_cents,'totalCents',o.quantity::bigint*o.unit_price_cents))) AS "checkoutItems"
          FROM orders o JOIN products p ON p.id=o.product_id WHERE o.id=$1 AND ($2::uuid IS NULL OR o.owner_user_id=$2)) r
        """);cmd.Parameters.AddWithValue(order);cmd.Parameters.Add(new NpgsqlParameter{NpgsqlDbType=NpgsqlDbType.Uuid,Value=(object?)owner??DBNull.Value});
        return (string?)await cmd.ExecuteScalarAsync(ct)??"null";
    }
    public async Task<string> HistoryAsync(string? search,int page,CancellationToken ct=default)
    {
        await using var cmd=db.CreateCommand("""
        SELECT COALESCE(jsonb_agg(row_to_json(r) ORDER BY r.id DESC),'[]'::jsonb)::text FROM (
          SELECT e.id,p.sku,p.name,e.kind,e.shelf_delta AS "shelfDelta",e.picked_delta AS "pickedDelta",e.metadata,e.created_at AS "createdAt"
          FROM inventory_events e JOIN products p ON p.id=e.product_id
          WHERE strpos(lower(p.sku||' '||p.name||' '||e.kind||' '||e.metadata::text),lower($1))>0
          ORDER BY e.id DESC LIMIT 50 OFFSET $2) r
        """);cmd.Parameters.AddWithValue(search??"");cmd.Parameters.AddWithValue((page-1)*50);return (string)(await cmd.ExecuteScalarAsync(ct))!;
    }
}
