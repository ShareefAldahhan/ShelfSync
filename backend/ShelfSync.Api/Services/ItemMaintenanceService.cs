using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace ShelfSync.Api.Services;

public sealed record ItemUpdateRequest(string? Aisle, string? Bay, string? Shelf, string? Bin, int? ShelfUnits, int? ExpectedShelfUnits, string? Reason);
public sealed class ItemMaintenanceService(NpgsqlDataSource db)
{
    public async Task<ReservationResult> UpdateAsync(Guid productId, Guid requestKey, ItemUpdateRequest request, string actor, bool correctStock, CancellationToken ct=default)
    {
        ReservationResult Error(int status,string error)=>new(status,JsonSerializer.Serialize(new {error}));
        string? Clean(string? text)=>string.IsNullOrWhiteSpace(text)?null:text.Trim();
        var reason=Clean(request.Reason);var aisle=Clean(request.Aisle);var bay=Clean(request.Bay);var shelf=Clean(request.Shelf);var bin=Clean(request.Bin);
        if(productId==Guid.Empty || requestKey==Guid.Empty || reason is null || reason.Length<3 || reason.Length>500) return Error(400,"A reason of 3 to 500 characters is required.");
        if(correctStock && (request.ShelfUnits is null or <0 or >1000000 || request.ExpectedShelfUnits is null or <0)) return Error(400,"Enter a nonnegative shelf count (maximum 1,000,000) and the previously displayed count.");
        if(!correctStock && new[]{aisle,bay,shelf,bin}.Any(v=>v?.Length>40)) return Error(400,"Location fields must be at most 40 characters.");
        var operation=correctStock?"INVENTORY_CORRECTION":"LOCATION_UPDATE";
        var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {productId,request.ShelfUnits,request.ExpectedShelfUnits,aisle,bay,shelf,bin,reason,actor}))));
        await using var connection=await db.OpenConnectionAsync(ct);
        await using var tx=await connection.BeginTransactionAsync(ct);
        await using(var claim=new NpgsqlCommand("INSERT INTO operation_requests(operation,request_key,payload_hash) VALUES($1,$2,$3) ON CONFLICT DO NOTHING",connection,tx))
        {
            claim.Parameters.AddWithValue(operation);claim.Parameters.AddWithValue(requestKey);claim.Parameters.AddWithValue(hash);
            if(await claim.ExecuteNonQueryAsync(ct)==0)
            {
                await using var replay=new NpgsqlCommand("SELECT payload_hash,response_status,response_body::text FROM operation_requests WHERE operation=$1 AND request_key=$2",connection,tx);
                replay.Parameters.AddWithValue(operation);replay.Parameters.AddWithValue(requestKey);await using var reader=await replay.ExecuteReaderAsync(ct);await reader.ReadAsync(ct);
                if(reader.GetString(0)!=hash) return Error(409,"This request key belongs to a different item update.");
                return new(reader.GetInt32(1),reader.GetString(2));
            }
        }
        int oldShelf;
        await using(var inventory=new NpgsqlCommand("SELECT shelf_units FROM inventory WHERE product_id=$1 FOR UPDATE",connection,tx))
        {
            inventory.Parameters.AddWithValue(productId);var count=await inventory.ExecuteScalarAsync(ct);
            if(count is null) return Error(404,"Product inventory was not found.");oldShelf=(int)count;
        }
        object before;object after;int delta=0;
        if(correctStock)
        {
            if(oldShelf!=request.ExpectedShelfUnits) return Error(409,"Stock changed since this page was loaded. Refresh and recount before submitting a new correction.");
            await using var reservations=new NpgsqlCommand("SELECT COALESCE(SUM(quantity),0) FROM orders WHERE product_id=$1 AND state='RESERVED' AND expires_at>clock_timestamp()",connection,tx);
            reservations.Parameters.AddWithValue(productId);var reserved=(long)(await reservations.ExecuteScalarAsync(ct))!;
            if(request.ShelfUnits<reserved) return Error(409,"This count would leave active reservations uncovered. Report shortages for affected orders first, then refresh and correct stock.");
            delta=request.ShelfUnits!.Value-oldShelf;before=new {shelfUnits=oldShelf};after=new {shelfUnits=request.ShelfUnits};
            await using var update=new NpgsqlCommand("UPDATE inventory SET shelf_units=$2,updated_at=clock_timestamp() WHERE product_id=$1",connection,tx);
            update.Parameters.AddWithValue(productId);update.Parameters.AddWithValue(request.ShelfUnits.Value);await update.ExecuteNonQueryAsync(ct);
        }
        else
        {
            await using(var lookup=new NpgsqlCommand("SELECT aisle,bay,shelf,bin FROM product_profiles WHERE product_id=$1",connection,tx))
            {
                lookup.Parameters.AddWithValue(productId);await using var r=await lookup.ExecuteReaderAsync(ct);
                before=await r.ReadAsync(ct)?new {aisle=r.IsDBNull(0)?null:r.GetString(0),bay=r.IsDBNull(1)?null:r.GetString(1),shelf=r.IsDBNull(2)?null:r.GetString(2),bin=r.IsDBNull(3)?null:r.GetString(3)}:new {aisle=(string?)null,bay=(string?)null,shelf=(string?)null,bin=(string?)null};
            }
            after=new {aisle,bay,shelf,bin};
            await using var update=new NpgsqlCommand("INSERT INTO product_profiles(product_id,aisle,bay,shelf,bin) VALUES($1,$2,$3,$4,$5) ON CONFLICT(product_id) DO UPDATE SET aisle=EXCLUDED.aisle,bay=EXCLUDED.bay,shelf=EXCLUDED.shelf,bin=EXCLUDED.bin,updated_at=clock_timestamp()",connection,tx);
            update.Parameters.AddWithValue(productId);
            foreach(var field in new[]{aisle,bay,shelf,bin})update.Parameters.Add(new NpgsqlParameter {NpgsqlDbType=NpgsqlDbType.Text,Value=(object?)field??DBNull.Value});
            await update.ExecuteNonQueryAsync(ct);
        }
        await using(var audit=new NpgsqlCommand("INSERT INTO inventory_events(product_id,kind,shelf_delta,picked_delta,metadata) VALUES($1,'RECONCILE',$2,0,$3::jsonb)",connection,tx))
        {
            audit.Parameters.AddWithValue(productId);audit.Parameters.AddWithValue(delta);audit.Parameters.AddWithValue(JsonSerializer.Serialize(new {operation,actor,reason,before,after,requestKey}));await audit.ExecuteNonQueryAsync(ct);
        }
        var result=new ReservationResult(200,JsonSerializer.Serialize(new {productId,operation,updated=true}));
        await using(var save=new NpgsqlCommand("UPDATE operation_requests SET response_status=$3,response_body=$4::jsonb WHERE operation=$1 AND request_key=$2",connection,tx))
        {
            save.Parameters.AddWithValue(operation);save.Parameters.AddWithValue(requestKey);save.Parameters.AddWithValue(result.StatusCode);save.Parameters.AddWithValue(result.Json);await save.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);return result;
    }
}
