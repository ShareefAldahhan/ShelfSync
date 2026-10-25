using Npgsql;

namespace ShelfSync.Api.Repositories;

public sealed record OrderSummary(Guid Id, Guid ProductId, string Sku, string Name,
    int Quantity, string State, DateTime ExpiresAt, DateTime CreatedAt, DateTime UpdatedAt, string? Reason, string? PaymentState=null,bool CustomerReservation=false,bool CanPick=false);

public sealed class OrderRepository(NpgsqlDataSource db)
{
    public async Task<List<OrderSummary>> GetRecentAsync(CancellationToken ct = default, Guid? customerId = null, int page = 1,bool requirePaid=false,bool paidQueue=false)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT o.id, o.product_id, p.sku, p.name, o.quantity, o.state,
                   o.expires_at, o.created_at, o.updated_at, o.reason,
                   (SELECT state FROM order_payment_status WHERE linked_order_id=o.id ORDER BY created_at DESC LIMIT 1),
                   EXISTS(SELECT 1 FROM app_user_roles ur JOIN app_roles r ON r."Id"=ur."RoleId"
                          WHERE ur."UserId"=o.owner_user_id AND r."Name"='Customer')
            FROM orders o JOIN products p ON p.id = o.product_id
            WHERE ($1::uuid IS NULL OR o.owner_user_id=$1)
              AND (NOT $3 OR EXISTS(SELECT 1 FROM order_payment_status ps WHERE ps.linked_order_id=o.id AND ps.state='PAID'))
            ORDER BY o.created_at DESC, o.id DESC LIMIT 100 OFFSET $2
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid, Value = (object?)customerId ?? DBNull.Value });
        command.Parameters.AddWithValue((page-1)*100);
        command.Parameters.AddWithValue(paidQueue);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var orders = new List<OrderSummary>();
        while (await reader.ReadAsync(ct))
            orders.Add(new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3),
                reader.GetInt32(4), reader.GetString(5), reader.GetDateTime(6), reader.GetDateTime(7),
                reader.GetDateTime(8), reader.IsDBNull(9) ? null : reader.GetString(9),reader.IsDBNull(10)?null:reader.GetString(10),reader.GetBoolean(11),reader.GetString(5)=="RESERVED"&&reader.GetDateTime(6)>DateTime.UtcNow&&(!requirePaid||!reader.GetBoolean(11)||(!reader.IsDBNull(10)&&reader.GetString(10)=="PAID"))));
        return orders;
    }
}
