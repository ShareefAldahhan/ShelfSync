using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
namespace ShelfSync.Api.Services;

// Shared replay boundary for the new stock workflows; business rules stay in their services.
public sealed class TransactionalOperation(NpgsqlDataSource db)
{
    public static ReservationResult Result(int status, object body) => new(status, JsonSerializer.Serialize(body));
    public static ReservationResult Error(int status, string error) => Result(status, new { error });
    public async Task<ReservationResult> RunAsync(string operation, Guid key, object payload,
        Func<NpgsqlConnection,NpgsqlTransaction,Task<ReservationResult>> action, CancellationToken ct)
    {
        if(key==Guid.Empty)return Error(400,"A request key is required.");
        var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload))));
        await using var c=await db.OpenConnectionAsync(ct);
        await using var tx=await c.BeginTransactionAsync(ct);
        await using(var claim=new NpgsqlCommand("INSERT INTO operation_requests(operation,request_key,payload_hash) VALUES($1,$2,$3) ON CONFLICT DO NOTHING",c,tx))
        {
            claim.Parameters.AddWithValue(operation);claim.Parameters.AddWithValue(key);claim.Parameters.AddWithValue(hash);
            if(await claim.ExecuteNonQueryAsync(ct)==0)
            {
                await using var replay=new NpgsqlCommand("SELECT payload_hash,response_status,response_body::text FROM operation_requests WHERE operation=$1 AND request_key=$2",c,tx);
                replay.Parameters.AddWithValue(operation);replay.Parameters.AddWithValue(key);
                await using var r=await replay.ExecuteReaderAsync(ct);await r.ReadAsync(ct);
                return r.GetString(0)!=hash?Error(409,"Request key reused with different data."):new(r.GetInt32(1),r.GetString(2));
            }
        }
        var result=await action(c,tx);
        // Business rejection rolls back every preceding line of an atomic cart or movement.
        if(result.StatusCode>=400)return result;
        await using(var save=new NpgsqlCommand("UPDATE operation_requests SET response_status=$3,response_body=$4::jsonb WHERE operation=$1 AND request_key=$2",c,tx))
        {
            save.Parameters.AddWithValue(operation);save.Parameters.AddWithValue(key);save.Parameters.AddWithValue(result.StatusCode);save.Parameters.AddWithValue(result.Json);await save.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);return result;
    }
}
