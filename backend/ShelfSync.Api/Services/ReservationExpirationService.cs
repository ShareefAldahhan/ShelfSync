
using Npgsql;

namespace ShelfSync.Api.Services;

public sealed class ReservationExpirationService(NpgsqlDataSource db)
{
	private const int BatchSize = 100;

	public async Task<int> ExpireBatchAsync(CancellationToken ct = default)
	{
		const string candidatesSql = """
            SELECT id, product_id
            FROM orders
            WHERE state = 'RESERVED'
              AND expires_at <= clock_timestamp()
            ORDER BY expires_at, id
            LIMIT $1;
            """;

		var candidates = new List<(Guid OrderId, Guid ProductId)>();

		await using (var connection = await db.OpenConnectionAsync(ct))
		{
			await using var command =
				new NpgsqlCommand(candidatesSql, connection);

			command.Parameters.AddWithValue(BatchSize);

			await using var reader =
				await command.ExecuteReaderAsync(ct);

			while (await reader.ReadAsync(ct))
			{
				candidates.Add((
					reader.GetGuid(0),
					reader.GetGuid(1)
				));
			}
		}

		var expiredCount = 0;

		foreach (var candidate in candidates)
		{
			if (await ExpireOneAsync(
				candidate.OrderId,
				candidate.ProductId,
				ct))
			{
				expiredCount++;
			}
		}

		return expiredCount;
	}

	private async Task<bool> ExpireOneAsync(
		Guid orderId,
		Guid productId,
		CancellationToken ct)
	{
		await using var connection =
			await db.OpenConnectionAsync(ct);

		await using var transaction =
			await connection.BeginTransactionAsync(ct);

		// Lock inventory first to preserve our global lock order.
		const string inventorySql = """
            SELECT product_id
            FROM inventory
            WHERE product_id = $1
            FOR UPDATE;
            """;

		await using (var command =
			new NpgsqlCommand(inventorySql, connection, transaction))
		{
			command.Parameters.AddWithValue(productId);

			var result = await command.ExecuteScalarAsync(ct);

			if (result is null)
			{
				throw new InvalidOperationException(
					"Inventory row missing for reservation.");
			}
		}

		// Lock and recheck the order after obtaining the inventory lock.
		const string orderSql = """
            SELECT state, expires_at
            FROM orders
            WHERE id = $1 AND product_id = $2
            FOR UPDATE;
            """;

		string state;
		DateTime expiresAt;

		await using (var command =
			new NpgsqlCommand(orderSql, connection, transaction))
		{
			command.Parameters.AddWithValue(orderId);
			command.Parameters.AddWithValue(productId);

			await using var reader =
				await command.ExecuteReaderAsync(ct);

			if (!await reader.ReadAsync(ct))
			{
				return false;
			}

			state = reader.GetString(0);
			expiresAt = reader.GetDateTime(1);
		}

		// Capture the decision time after acquiring both locks.
		const string nowSql = "SELECT clock_timestamp();";

		DateTime now;

		await using (var command =
			new NpgsqlCommand(nowSql, connection, transaction))
		{
			now = (DateTime)(await command.ExecuteScalarAsync(ct))!;
		}

		// Another operation may already have changed the order.
		if (state != "RESERVED" || expiresAt > now)
		{
			return false;
		}

		const string updateSql = """
            UPDATE orders
            SET state = 'EXPIRED',
                updated_at = $1
            WHERE id = $2
              AND state = 'RESERVED';
            """;

		await using (var command =
			new NpgsqlCommand(updateSql, connection, transaction))
		{
			command.Parameters.AddWithValue(now);
			command.Parameters.AddWithValue(orderId);

			if (await command.ExecuteNonQueryAsync(ct) != 1)
			{
				throw new InvalidOperationException(
					"Failed to expire reservation.");
			}
		}

		// Expiration releases the reservation without moving stock.
		const string eventSql = """
            INSERT INTO inventory_events
                (product_id, order_id, kind,
                 shelf_delta, picked_delta)
            VALUES ($1, $2, 'EXPIRE', 0, 0);
            """;

		await using (var command =
			new NpgsqlCommand(eventSql, connection, transaction))
		{
			command.Parameters.AddWithValue(productId);
			command.Parameters.AddWithValue(orderId);

			await command.ExecuteNonQueryAsync(ct);
		}

		// Commit the state change and audit event together.
		await transaction.CommitAsync(ct);

		return true;
	}
}
