
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace ShelfSync.Api.Services;

public sealed class OrderShortageResolutionService(NpgsqlDataSource db)
{
	public async Task<ReservationResult> ResolveAsync(
		Guid orderId,
		Guid requestKey,
		CancellationToken ct = default)
	{
		if (orderId == Guid.Empty || requestKey == Guid.Empty)
			return Error(400, "A valid order ID and idempotency key are required.");

		var payloadHash = Convert.ToHexString(
			SHA256.HashData(Encoding.UTF8.GetBytes(orderId.ToString("D"))));

		await using var connection = await db.OpenConnectionAsync(ct);
		await using var transaction = await connection.BeginTransactionAsync(ct);

		// Claim the idempotency key.
		int inserted;

		await using (var claim = new NpgsqlCommand("""
            INSERT INTO operation_requests
                (operation, request_key, payload_hash)
            VALUES ('RESOLVE_SHORTAGE', $1, $2)
            ON CONFLICT DO NOTHING;
            """, connection, transaction))
		{
			claim.Parameters.AddWithValue(requestKey);
			claim.Parameters.AddWithValue(payloadHash);
			inserted = await claim.ExecuteNonQueryAsync(ct);
		}

		if (inserted == 0)
		{
			await using var replay = new NpgsqlCommand("""
                SELECT payload_hash, response_status, response_body::text
                FROM operation_requests
                WHERE operation = 'RESOLVE_SHORTAGE'
                  AND request_key = $1;
                """, connection, transaction);

			replay.Parameters.AddWithValue(requestKey);

			await using var reader = await replay.ExecuteReaderAsync(ct);

			if (!await reader.ReadAsync(ct))
				throw new InvalidOperationException("Idempotency record not found.");

			if (reader.GetString(0) != payloadHash)
				return Error(409, "Idempotency key belongs to another order.");

			if (reader.IsDBNull(1) || reader.IsDBNull(2))
				return Error(409, "Request is still being processed.");

			return new ReservationResult(reader.GetInt32(1), reader.GetString(2));
		}

		// Find the product associated with the order.
		Guid productId;

		await using (var lookup = new NpgsqlCommand("""
            SELECT product_id FROM orders WHERE id = $1;
            """, connection, transaction))
		{
			lookup.Parameters.AddWithValue(orderId);
			var value = await lookup.ExecuteScalarAsync(ct);

			if (value is not Guid id)
			{
				var result = Error(404, "Order not found.");
				await SaveResponseAsync(connection, transaction, requestKey, result, ct);
				await transaction.CommitAsync(ct);
				return result;
			}

			productId = id;
		}

		// Lock inventory first.
		int shelfUnits;
		int safetyBuffer;
		bool quarantined;

		await using (var inventory = new NpgsqlCommand("""
            SELECT shelf_units, safety_buffer, quarantined
            FROM inventory
            WHERE product_id = $1
            FOR UPDATE;
            """, connection, transaction))
		{
			inventory.Parameters.AddWithValue(productId);

			await using var reader = await inventory.ExecuteReaderAsync(ct);

			if (!await reader.ReadAsync(ct))
			{
				await reader.DisposeAsync();

				var result = Error(409, "Inventory record is missing.");
				await SaveResponseAsync(connection, transaction, requestKey, result, ct);
				await transaction.CommitAsync(ct);
				return result;
			}

			shelfUnits = reader.GetInt32(0);
			safetyBuffer = reader.GetInt32(1);
			quarantined = reader.GetBoolean(2);
		}

		// Lock the order after inventory.
		string state;
		int quantity;

		await using (var order = new NpgsqlCommand("""
            SELECT state, quantity
            FROM orders
            WHERE id = $1 AND product_id = $2
            FOR UPDATE;
            """, connection, transaction))
		{
			order.Parameters.AddWithValue(orderId);
			order.Parameters.AddWithValue(productId);

			await using var reader = await order.ExecuteReaderAsync(ct);

			if (!await reader.ReadAsync(ct))
				throw new InvalidOperationException("Order disappeared during resolution.");

			state = reader.GetString(0);
			quantity = reader.GetInt32(1);
		}

		// Database time is obtained after the locks.
		DateTime decisionTime;

		await using (var clock = new NpgsqlCommand(
			"SELECT clock_timestamp();", connection, transaction))
		{
			decisionTime = (DateTime)(await clock.ExecuteScalarAsync(ct))!;
		}

		if (state != "SHORTAGE_PENDING")
		{
			var result = Error(409, "Order is not awaiting shortage resolution.");
			await SaveResponseAsync(connection, transaction, requestKey, result, ct);
			await transaction.CommitAsync(ct);
			return result;
		}

		if (quarantined)
		{
			var result = Error(409, "Product inventory is quarantined.");
			await SaveResponseAsync(connection, transaction, requestKey, result, ct);
			await transaction.CommitAsync(ct);
			return result;
		}

		// Calculate allocations held by other active reservations.
		long activeReserved;

		await using (var reservations = new NpgsqlCommand("""
            SELECT COALESCE(SUM(quantity), 0)
            FROM orders
            WHERE product_id = $1
              AND id <> $2
              AND state = 'RESERVED'
              AND expires_at > $3;
            """, connection, transaction))
		{
			reservations.Parameters.AddWithValue(productId);
			reservations.Parameters.AddWithValue(orderId);
			reservations.Parameters.AddWithValue(decisionTime);

			activeReserved = (long)(await reservations.ExecuteScalarAsync(ct))!;
		}

		long available = (long)shelfUnits - safetyBuffer - activeReserved;

		if (available < quantity)
		{
			var result = Error(409, "Insufficient available stock to release shortage hold.");
			await SaveResponseAsync(connection, transaction, requestKey, result, ct);
			await transaction.CommitAsync(ct);
			return result;
		}

		// Restore the reservation with a new 15-minute deadline.
		var newExpiry = decisionTime.AddMinutes(15);
        await using(var payment=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM order_payment_status WHERE linked_order_id=$1 AND state='PAID')",connection,transaction))
        {payment.Parameters.AddWithValue(orderId);if((bool)(await payment.ExecuteScalarAsync(ct))!)newExpiry=DateTime.SpecifyKind(DateTime.MaxValue,DateTimeKind.Utc);}


		await using (var update = new NpgsqlCommand("""
            UPDATE orders
            SET state = 'RESERVED',
                reason = NULL,
                expires_at = $1,
                updated_at = $2
            WHERE id = $3 AND state = 'SHORTAGE_PENDING';
            """, connection, transaction))
		{
			update.Parameters.AddWithValue(newExpiry);
			update.Parameters.AddWithValue(decisionTime);
			update.Parameters.AddWithValue(orderId);

			if (await update.ExecuteNonQueryAsync(ct) != 1)
				throw new InvalidOperationException("Failed to resolve shortage.");
		}

		// Resolution is an audit event, not a stock movement.
		await using (var audit = new NpgsqlCommand("""
            INSERT INTO inventory_events
                (product_id, order_id, kind, shelf_delta, picked_delta)
            VALUES ($1, $2, 'RECONCILE', 0, 0);
            """, connection, transaction))
		{
			audit.Parameters.AddWithValue(productId);
			audit.Parameters.AddWithValue(orderId);
			await audit.ExecuteNonQueryAsync(ct);
		}

		var response = new ReservationResult(
			200,
			JsonSerializer.Serialize(new
			{
				orderId,
				state = "RESERVED",
				expiresAt = newExpiry
			}));

		await SaveResponseAsync(connection, transaction, requestKey, response, ct);
		await transaction.CommitAsync(ct);

		return response;
	}

	private static async Task SaveResponseAsync(
		NpgsqlConnection connection,
		NpgsqlTransaction transaction,
		Guid requestKey,
		ReservationResult result,
		CancellationToken ct)
	{
		await using var command = new NpgsqlCommand("""
            UPDATE operation_requests
            SET response_status = $1,
                response_body = $2::jsonb
            WHERE operation = 'RESOLVE_SHORTAGE'
              AND request_key = $3;
            """, connection, transaction);

		command.Parameters.AddWithValue(result.StatusCode);
		command.Parameters.AddWithValue(result.Json);
		command.Parameters.AddWithValue(requestKey);

		if (await command.ExecuteNonQueryAsync(ct) != 1)
			throw new InvalidOperationException("Failed to save resolution response.");
	}

	private static ReservationResult Error(int status, string message) =>
		new(status, JsonSerializer.Serialize(new { error = message }));
}
