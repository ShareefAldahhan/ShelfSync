
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace ShelfSync.Api.Services;

public sealed class OrderFulfillmentService(NpgsqlDataSource db)
{
	public async Task<ReservationResult> FulfillAsync(
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

		int inserted;

		await using (var claim = new NpgsqlCommand("""
            INSERT INTO operation_requests
                (operation, request_key, payload_hash)
            VALUES ('FULFILL', $1, $2)
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
                WHERE operation = 'FULFILL' AND request_key = $1;
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

		Guid productId;

		await using (var lookup = new NpgsqlCommand("""
            SELECT product_id FROM orders WHERE id = $1;
            """, connection, transaction))
		{
			lookup.Parameters.AddWithValue(orderId);

			if (await lookup.ExecuteScalarAsync(ct) is not Guid id)
			{
				var missing = Error(404, "Order not found.");
				await SaveResponseAsync(connection, transaction, requestKey, missing, ct);
				await transaction.CommitAsync(ct);
				return missing;
			}

			productId = id;
		}

		int pickedUnits;

		// All inventory writers acquire this lock first.
		await using (var inventory = new NpgsqlCommand("""
            SELECT picked_units
            FROM inventory
            WHERE product_id = $1
            FOR UPDATE;
            """, connection, transaction))
		{
			inventory.Parameters.AddWithValue(productId);

			var value = await inventory.ExecuteScalarAsync(ct);

			if (value is not int count)
			{
				var missing = Error(409, "Inventory record is missing.");
				await SaveResponseAsync(connection, transaction, requestKey, missing, ct);
				await transaction.CommitAsync(ct);
				return missing;
			}

			pickedUnits = count;
		}

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
				throw new InvalidOperationException("Order disappeared during fulfillment.");

			state = reader.GetString(0);
			quantity = reader.GetInt32(1);
		}

		if (state != "PICKED")
		{
			var invalid = Error(409, "Only PICKED orders can be fulfilled.");
			await SaveResponseAsync(connection, transaction, requestKey, invalid, ct);
			await transaction.CommitAsync(ct);
			return invalid;
		}

		if (pickedUnits < quantity)
		{
			var invalid = Error(409, "Insufficient picked inventory.");
			await SaveResponseAsync(connection, transaction, requestKey, invalid, ct);
			await transaction.CommitAsync(ct);
			return invalid;
		}

		DateTime decisionTime;

		await using (var clock = new NpgsqlCommand(
			"SELECT clock_timestamp();", connection, transaction))
		{
			decisionTime = (DateTime)(await clock.ExecuteScalarAsync(ct))!;
		}

		await using (var updateInventory = new NpgsqlCommand("""
            UPDATE inventory
            SET picked_units = picked_units - $1,
                updated_at = $2
            WHERE product_id = $3;
            """, connection, transaction))
		{
			updateInventory.Parameters.AddWithValue(quantity);
			updateInventory.Parameters.AddWithValue(decisionTime);
			updateInventory.Parameters.AddWithValue(productId);

			if (await updateInventory.ExecuteNonQueryAsync(ct) != 1)
				throw new InvalidOperationException("Failed to update picked inventory.");
		}

		await using (var updateOrder = new NpgsqlCommand("""
            UPDATE orders
            SET state = 'FULFILLED',
                updated_at = $1
            WHERE id = $2 AND state = 'PICKED';
            """, connection, transaction))
		{
			updateOrder.Parameters.AddWithValue(decisionTime);
			updateOrder.Parameters.AddWithValue(orderId);

			if (await updateOrder.ExecuteNonQueryAsync(ct) != 1)
				throw new InvalidOperationException("Failed to fulfill order.");
		}

		await using (var audit = new NpgsqlCommand("""
            INSERT INTO inventory_events
                (product_id, order_id, kind, shelf_delta, picked_delta)
            VALUES ($1, $2, 'FULFILL', 0, $3);
            """, connection, transaction))
		{
			audit.Parameters.AddWithValue(productId);
			audit.Parameters.AddWithValue(orderId);
			audit.Parameters.AddWithValue(-quantity);
			await audit.ExecuteNonQueryAsync(ct);
		}

		var result = new ReservationResult(
			200,
			JsonSerializer.Serialize(new
			{
				orderId,
				state = "FULFILLED"
			}));

		await SaveResponseAsync(connection, transaction, requestKey, result, ct);
		await transaction.CommitAsync(ct);

		return result;
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
            WHERE operation = 'FULFILL'
              AND request_key = $3;
            """, connection, transaction);

		command.Parameters.AddWithValue(result.StatusCode);
		command.Parameters.AddWithValue(result.Json);
		command.Parameters.AddWithValue(requestKey);

		if (await command.ExecuteNonQueryAsync(ct) != 1)
			throw new InvalidOperationException("Failed to save fulfillment response.");
	}

	private static ReservationResult Error(int status, string message) =>
		new(status, JsonSerializer.Serialize(new { error = message }));
}
