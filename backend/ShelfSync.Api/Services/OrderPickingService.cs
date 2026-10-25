
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace ShelfSync.Api.Services;

public sealed class OrderPickingService(NpgsqlDataSource db,IConfiguration? configuration=null)
{
	public async Task<ReservationResult> PickAsync(
		Guid orderId,
		Guid requestKey,
		CancellationToken ct = default, bool requireCustomerReservation = false)
	{
		if (orderId == Guid.Empty || requestKey == Guid.Empty)
		{
			return Error(400, "A valid order ID and idempotency key are required.");
		}

		var payloadHash = Convert.ToHexString(
			SHA256.HashData(
				Encoding.UTF8.GetBytes((orderId.ToString("D") + (requireCustomerReservation ? ":customer-reservation" : "")))));

		await using var connection = await db.OpenConnectionAsync(ct);
		await using var transaction = await connection.BeginTransactionAsync(ct);

		// 1. Claim the idempotency key.
		int inserted;

		await using (var claim = new NpgsqlCommand("""
            INSERT INTO operation_requests
                (operation, request_key, payload_hash)
            VALUES ('PICK', $1, $2)
            ON CONFLICT DO NOTHING;
            """, connection, transaction))
		{
			claim.Parameters.AddWithValue(requestKey);
			claim.Parameters.AddWithValue(payloadHash);

			inserted = await claim.ExecuteNonQueryAsync(ct);
		}

		// 2. If the key already exists, replay its stored response.
		if (inserted == 0)
		{
			await using var replay = new NpgsqlCommand("""
                SELECT payload_hash, response_status, response_body::text
                FROM operation_requests
                WHERE operation = 'PICK'
                  AND request_key = $1;
                """, connection, transaction);

			replay.Parameters.AddWithValue(requestKey);

			await using var reader = await replay.ExecuteReaderAsync(ct);

			if (!await reader.ReadAsync(ct))
			{
				throw new InvalidOperationException(
					"Existing idempotency request could not be found.");
			}

			var storedHash = reader.GetString(0);

			if (storedHash != payloadHash)
			{
				return Error(409,
					"Idempotency key was already used for a different order.");
			}

			if (reader.IsDBNull(1) || reader.IsDBNull(2))
			{
				return Error(409, "Request is still being processed.");
			}

			var status = reader.GetInt32(1);
			var json = reader.GetString(2);

			return new ReservationResult(status, json);
		}

		// 3. Find the product associated with the order.
		Guid productId;

		await using (var lookup = new NpgsqlCommand("""
            SELECT product_id
            FROM orders
            WHERE id = $1;
            """, connection, transaction))
		{
			lookup.Parameters.AddWithValue(orderId);

			var value = await lookup.ExecuteScalarAsync(ct);

			if (value is not Guid id)
			{
				var result = Error(404, "Order not found.");

				await SaveResponseAsync(
					connection, transaction, requestKey, result, ct);

				await transaction.CommitAsync(ct);
				return result;
			}

			productId = id;
		}

		// 4. Lock inventory FIRST.
		int shelfUnits;
		int pickedUnits;

		await using (var inventory = new NpgsqlCommand("""
            SELECT shelf_units, picked_units
            FROM inventory
            WHERE product_id = $1
            FOR UPDATE;
            """, connection, transaction))
		{
			inventory.Parameters.AddWithValue(productId);

			await using var reader = await inventory.ExecuteReaderAsync(ct);

			if (!await reader.ReadAsync(ct))
			{
				var result = Error(409, "Inventory record is missing.");

				await reader.DisposeAsync();

				await SaveResponseAsync(
					connection, transaction, requestKey, result, ct);

				await transaction.CommitAsync(ct);
				return result;
			}

			shelfUnits = reader.GetInt32(0);
			pickedUnits = reader.GetInt32(1);
		}

		// 5. Lock the order AFTER inventory.
		string state;
		int quantity;
		DateTime expiresAt;
        bool customerReservation;
        bool paymentConfirmed;

		await using (var order = new NpgsqlCommand("""
            SELECT state, quantity, expires_at,
                   EXISTS(SELECT 1 FROM app_user_roles ur JOIN app_roles r ON r."Id"=ur."RoleId"
                          WHERE ur."UserId"=orders.owner_user_id AND r."Name"='Customer'),
                   EXISTS(SELECT 1 FROM order_payment_status ps WHERE ps.linked_order_id=orders.id AND ps.state='PAID')
            FROM orders
            WHERE id = $1 AND product_id = $2
            FOR UPDATE;
            """, connection, transaction))
		{
			order.Parameters.AddWithValue(orderId);
			order.Parameters.AddWithValue(productId);

			await using var reader = await order.ExecuteReaderAsync(ct);

			if (!await reader.ReadAsync(ct))
			{
				throw new InvalidOperationException(
					"Order disappeared while picking.");
			}

			state = reader.GetString(0);
			quantity = reader.GetInt32(1);
			expiresAt = reader.GetDateTime(2);
            customerReservation=reader.GetBoolean(3);
            paymentConfirmed=reader.GetBoolean(4);
		}

		// 6. Read database time AFTER acquiring both locks.
		DateTime decisionTime;

		await using (var clock = new NpgsqlCommand("""
            SELECT clock_timestamp();
            """, connection, transaction))
		{
			decisionTime = (DateTime)
				(await clock.ExecuteScalarAsync(ct))!;
		}

		ReservationResult? failure = null;

		if (state != "RESERVED")
		{
			failure = Error(409, "Order is not in RESERVED state.");
		}
		else if (expiresAt <= decisionTime)
		{
			failure = Error(409, "Reservation has expired.");
		}
		else if(customerReservation && configuration?.GetValue<bool>("Stripe:Enabled")==true && !paymentConfirmed)
        {
            failure=Error(409,"Only payment-confirmed customer orders can be picked.");
        }
        else if (requireCustomerReservation && !customerReservation)
        {
            failure=Error(409,"Associates can only pick active reservations belonging to a customer account.");
        }
		else if (shelfUnits < quantity)
		{
			failure = Error(409, "Insufficient shelf inventory to pick order.");
		}

		if (failure is not null)
		{
			await SaveResponseAsync(
				connection, transaction, requestKey, failure, ct);

			await transaction.CommitAsync(ct);
			return failure;
		}

		// 7. Move stock from shelf to picked.
		await using (var updateInventory = new NpgsqlCommand("""
            UPDATE inventory
            SET shelf_units = shelf_units - $1,
                picked_units = picked_units + $1,
                updated_at = $2
            WHERE product_id = $3;
            """, connection, transaction))
		{
			updateInventory.Parameters.AddWithValue(quantity);
			updateInventory.Parameters.AddWithValue(decisionTime);
			updateInventory.Parameters.AddWithValue(productId);

			if (await updateInventory.ExecuteNonQueryAsync(ct) != 1)
			{
				throw new InvalidOperationException(
					"Inventory update affected an unexpected number of rows.");
			}
		}

		// 8. Change the order state.
		await using (var updateOrder = new NpgsqlCommand("""
            UPDATE orders
            SET state = 'PICKED',
                updated_at = $1
            WHERE id = $2 AND state = 'RESERVED';
            """, connection, transaction))
		{
			updateOrder.Parameters.AddWithValue(decisionTime);
			updateOrder.Parameters.AddWithValue(orderId);

			if (await updateOrder.ExecuteNonQueryAsync(ct) != 1)
			{
				throw new InvalidOperationException(
					"Order update affected an unexpected number of rows.");
			}
		}

		// 9. Record the inventory movement.
		await using (var audit = new NpgsqlCommand("""
            INSERT INTO inventory_events
                (product_id, order_id, kind, shelf_delta, picked_delta)
            VALUES ($1, $2, 'PICK', $3, $4);
            """, connection, transaction))
		{
			audit.Parameters.AddWithValue(productId);
			audit.Parameters.AddWithValue(orderId);
			audit.Parameters.AddWithValue(-quantity);
			audit.Parameters.AddWithValue(quantity);

			await audit.ExecuteNonQueryAsync(ct);
		}

		// 10. Save the response in the SAME transaction.
		var response = new ReservationResult(
			200,
			JsonSerializer.Serialize(new
			{
				orderId,
				state = "PICKED"
			}));

		await SaveResponseAsync(
			connection, transaction, requestKey, response, ct);

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
            WHERE operation = 'PICK'
              AND request_key = $3;
            """, connection, transaction);

		command.Parameters.AddWithValue(result.StatusCode);
		command.Parameters.AddWithValue(result.Json);
		command.Parameters.AddWithValue(requestKey);

		if (await command.ExecuteNonQueryAsync(ct) != 1)
		{
			throw new InvalidOperationException(
				"Failed to save the idempotent response.");
		}
	}

	private static ReservationResult Error(int status, string message)
	{
		return new ReservationResult(
			status,
			JsonSerializer.Serialize(new { error = message }));
	}
}
