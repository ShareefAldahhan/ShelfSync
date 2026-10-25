using ShelfSync.Api.Payments;
﻿
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace ShelfSync.Api.Services;

public sealed class OrderShortageService(NpgsqlDataSource db)
{
	public async Task<ReservationResult> ReportAsync(
		Guid orderId,
		Guid requestKey,
		CancellationToken ct = default)
	{
		if (orderId == Guid.Empty || requestKey == Guid.Empty)
		{
			return Error(400, "A valid order ID and idempotency key are required.");
		}

		var payloadHash = Convert.ToHexString(
			SHA256.HashData(Encoding.UTF8.GetBytes(orderId.ToString("D"))));

		await using var connection = await db.OpenConnectionAsync(ct);
		await using var transaction = await connection.BeginTransactionAsync(ct);

		int inserted;

		await using (var claim = new NpgsqlCommand("""
            INSERT INTO operation_requests
                (operation, request_key, payload_hash)
            VALUES ('SHORTAGE', $1, $2)
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
                WHERE operation = 'SHORTAGE' AND request_key = $1;
                """, connection, transaction);

			replay.Parameters.AddWithValue(requestKey);

			await using var reader = await replay.ExecuteReaderAsync(ct);

			if (!await reader.ReadAsync(ct))
				throw new InvalidOperationException("Idempotency record not found.");

			if (reader.GetString(0) != payloadHash)
				return Error(409, "Idempotency key belongs to a different order.");

			if (reader.IsDBNull(1) || reader.IsDBNull(2))
				return Error(409, "Request is still being processed.");

			return new ReservationResult(reader.GetInt32(1), reader.GetString(2));
		}

		Guid productId;

		await using (var lookup = new NpgsqlCommand("""
            SELECT product_id FROM orders WHERE id = $1;
            """, connection, transaction))
		{
			lookup.Parameters.AddWithValue(orderId);
			var value = await lookup.ExecuteScalarAsync(ct);

			if (value is not Guid id)
			{
				var missing = Error(404, "Order not found.");
				await SaveResponseAsync(connection, transaction, requestKey, missing, ct);
				await transaction.CommitAsync(ct);
				return missing;
			}

			productId = id;
		}

		// Inventory is always locked before the order.
		await using (var inventory = new NpgsqlCommand("""
            SELECT product_id FROM inventory
            WHERE product_id = $1 FOR UPDATE;
            """, connection, transaction))
		{
			inventory.Parameters.AddWithValue(productId);
			var locked = await inventory.ExecuteScalarAsync(ct);

			if (locked is null)
			{
				var missing = Error(409, "Inventory record is missing.");
				await SaveResponseAsync(connection, transaction, requestKey, missing, ct);
				await transaction.CommitAsync(ct);
				return missing;
			}
		}

		string state;
		DateTime expiresAt;

		await using (var order = new NpgsqlCommand("""
            SELECT state, expires_at FROM orders
            WHERE id = $1 AND product_id = $2 FOR UPDATE;
            """, connection, transaction))
		{
			order.Parameters.AddWithValue(orderId);
			order.Parameters.AddWithValue(productId);

			await using var reader = await order.ExecuteReaderAsync(ct);

			if (!await reader.ReadAsync(ct))
				throw new InvalidOperationException("Order disappeared during shortage report.");

			state = reader.GetString(0);
			expiresAt = reader.GetDateTime(1);
		}

		DateTime decisionTime;

		await using (var clock = new NpgsqlCommand(
			"SELECT clock_timestamp();", connection, transaction))
		{
			decisionTime = (DateTime)(await clock.ExecuteScalarAsync(ct))!;
		}

        var paymentError=await PaymentGuard.FulfillmentError(connection,transaction,orderId,false,ct);
        if(paymentError is not null)return Error(409,"Payment must be confirmed before shortage handling.");
		if (state != "RESERVED" || expiresAt <= decisionTime)
		{
			var conflict = Error(409, "Order is not eligible for shortage reporting.");
			await SaveResponseAsync(connection, transaction, requestKey, conflict, ct);
			await transaction.CommitAsync(ct);
			return conflict;
		}

		await using (var update = new NpgsqlCommand("""
            UPDATE orders
            SET state = 'SHORTAGE_PENDING',
                reason = 'PHYSICAL_STOCK_SHORTAGE',
                updated_at = $1
            WHERE id = $2 AND state = 'RESERVED';
            """, connection, transaction))
		{
			update.Parameters.AddWithValue(decisionTime);
			update.Parameters.AddWithValue(orderId);

			if (await update.ExecuteNonQueryAsync(ct) != 1)
				throw new InvalidOperationException("Failed to update shortage state.");
		}

		await using (var audit = new NpgsqlCommand("""
            INSERT INTO inventory_events
                (product_id, order_id, kind, shelf_delta, picked_delta)
            VALUES ($1, $2, 'SHORTAGE', 0, 0);
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
				state = "SHORTAGE_PENDING"
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
            WHERE operation = 'SHORTAGE' AND request_key = $3;
            """, connection, transaction);

		command.Parameters.AddWithValue(result.StatusCode);
		command.Parameters.AddWithValue(result.Json);
		command.Parameters.AddWithValue(requestKey);

		if (await command.ExecuteNonQueryAsync(ct) != 1)
			throw new InvalidOperationException("Failed to save shortage response.");
	}

	private static ReservationResult Error(int status, string message) =>
		new(status, JsonSerializer.Serialize(new { error = message }));
}
