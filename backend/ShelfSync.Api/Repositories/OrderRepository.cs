using Npgsql;

namespace ShelfSync.Api.Repositories;

public sealed record OrderSummary(Guid Id, Guid ProductId, string Sku, string Name,
    int Quantity, string State, DateTime ExpiresAt, DateTime CreatedAt, DateTime UpdatedAt, string? Reason);

public sealed class OrderRepository(NpgsqlDataSource db)
{
    public async Task<List<OrderSummary>> GetRecentAsync(CancellationToken ct = default)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT o.id, o.product_id, p.sku, p.name, o.quantity, o.state,
                   o.expires_at, o.created_at, o.updated_at, o.reason
            FROM orders o JOIN products p ON p.id = o.product_id
            ORDER BY o.created_at DESC, o.id DESC LIMIT 100
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var orders = new List<OrderSummary>();
        while (await reader.ReadAsync(ct))
            orders.Add(new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3),
                reader.GetInt32(4), reader.GetString(5), reader.GetDateTime(6), reader.GetDateTime(7),
                reader.GetDateTime(8), reader.IsDBNull(9) ? null : reader.GetString(9)));
        return orders;
    }
}
