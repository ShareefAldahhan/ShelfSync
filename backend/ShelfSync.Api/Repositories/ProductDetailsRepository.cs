using System.Data;
using Npgsql;

namespace ShelfSync.Api.Repositories;

public sealed record StockSnapshot(int Shelf, int Picked, long Reserved, int SafetyBuffer, long Available, bool Quarantined, string? QuarantineReason, DateTime UpdatedAt);
public sealed record ProductProfile(string? Barcode, string? Aisle, string? Bay, string? Shelf, string? Bin, string? Brand, string? Supplier, int? CasePack, int? ReorderPoint, string? Notes);
public sealed record SalesEntry(Guid EventId, int Quantity, DateTime CommittedAt);
public sealed record ActivityEntry(long Id, string Kind, int ShelfDelta, int PickedDelta, Guid? OrderId, Guid? PosEventId, DateTime CreatedAt, string? Reason, string? Actor);
public sealed record AssociateDetails(Guid Id, string Sku, string Name, int PriceCents, ProductProfile Profile, StockSnapshot Stock, long PosUnits30Days, long FulfilledUnits30Days, List<SalesEntry> RecentSales, List<ActivityEntry> Activity, DateTime AsOf);

public sealed class ProductDetailsRepository(NpgsqlDataSource db)
{
    public async Task<AssociateDetails?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await using var command = new NpgsqlCommand("""
            SELECT p.sku,p.name,p.price_cents,i.shelf_units,i.picked_units,i.safety_buffer,
                   i.quarantined,i.quarantine_reason,i.updated_at,
                   d.barcode,d.aisle,d.bay,d.shelf,d.bin,d.brand,d.supplier,d.case_pack,d.reorder_point,d.notes,
                   COALESCE((SELECT SUM(quantity) FROM orders WHERE product_id=p.id AND state='RESERVED' AND expires_at>CURRENT_TIMESTAMP),0),
                   COALESCE((SELECT SUM(quantity) FROM pos_events WHERE product_id=p.id AND committed_at>=CURRENT_TIMESTAMP-INTERVAL '30 days'),0),
                   COALESCE((SELECT SUM(-picked_delta) FROM inventory_events WHERE product_id=p.id AND kind='FULFILL' AND created_at>=CURRENT_TIMESTAMP-INTERVAL '30 days'),0), CURRENT_TIMESTAMP
            FROM products p JOIN inventory i ON i.product_id=p.id
            LEFT JOIN product_profiles d ON d.product_id=p.id WHERE p.id=$1
            """, connection, tx);
        command.Parameters.AddWithValue(id);
        string? Text(NpgsqlDataReader r, int n) => r.IsDBNull(n) ? null : r.GetString(n);
        string sku, name; int price; ProductProfile profile; StockSnapshot stock; long pos, fulfilled; DateTime asOf;
        await using (var r = await command.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return null;
            sku=r.GetString(0); name=r.GetString(1); price=r.GetInt32(2);
            var reserved=r.GetInt64(19);
            stock=new(r.GetInt32(3),r.GetInt32(4),reserved,r.GetInt32(5),r.GetBoolean(6) ? 0 : Math.Max(0,(long)r.GetInt32(3)-reserved-r.GetInt32(5)),r.GetBoolean(6),Text(r,7),r.GetDateTime(8));
            profile=new(Text(r,9) ?? sku,Text(r,10),Text(r,11),Text(r,12),Text(r,13),Text(r,14),Text(r,15),r.IsDBNull(16)?null:r.GetInt32(16),r.IsDBNull(17)?null:r.GetInt32(17),Text(r,18));
            pos=r.GetInt64(20);fulfilled=r.GetInt64(21);asOf=r.GetDateTime(22);
        }
        var sales=new List<SalesEntry>();
        await using (var q=new NpgsqlCommand("SELECT event_id,quantity,committed_at FROM pos_events WHERE product_id=$1 ORDER BY committed_at DESC,event_id LIMIT 100",connection,tx))
        {
            q.Parameters.AddWithValue(id); await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct)) sales.Add(new(r.GetGuid(0),r.GetInt32(1),r.GetDateTime(2)));
        }
        var activity=new List<ActivityEntry>();
        await using (var q=new NpgsqlCommand("SELECT id,kind,shelf_delta,picked_delta,order_id,pos_event_id,created_at,metadata->>'reason',metadata->>'actor' FROM inventory_events WHERE product_id=$1 ORDER BY id DESC LIMIT 100",connection,tx))
        {
            q.Parameters.AddWithValue(id); await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct)) activity.Add(new(r.GetInt64(0),r.GetString(1),r.GetInt32(2),r.GetInt32(3),r.IsDBNull(4)?null:r.GetGuid(4),r.IsDBNull(5)?null:r.GetGuid(5),r.GetDateTime(6),Text(r,7),Text(r,8)));
        }
        await tx.CommitAsync(ct);
        return new(id,sku,name,price,profile,stock,pos,fulfilled,sales,activity,asOf);
    }
}
