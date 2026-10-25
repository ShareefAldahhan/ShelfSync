using ShelfSync.Api.Payments;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace ShelfSync.Api.Services;

public sealed class ReservationCancellationService(
	NpgsqlDataSource db)
{
	public async Task<ReservationResult> CancelAsync(
		Guid orderId,
		Guid requestKey,
		CancellationToken ct = default, Guid? customerId = null)
	{
		if (orderId == Guid.Empty || requestKey == Guid.Empty)
		{
			return Error(400, "Invalid order ID or idempotency key.");
		}

		var payload = orderId.ToString("D") + (customerId is null ? "" : $":{customerId:D}");

		var hash = Convert.ToHexString(
			SHA256.HashData(
				Encoding.UTF8.GetBytes(payload)));

		await using var connection =
			await db.OpenConnectionAsync(ct);

		await using var transaction =
			await connection.BeginTransactionAsync(ct);

        if (customerId is not null)
        {
            await using var ownership = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM orders WHERE id=$1 AND owner_user_id=$2)", connection, transaction);
            ownership.Parameters.AddWithValue(orderId); ownership.Parameters.AddWithValue(customerId.Value);
            if (!(bool)(await ownership.ExecuteScalarAsync(ct))!) return Error(404, "Reservation was not found.");
        }

		const string claimSql = """
    INSERT INTO operation_requests
        (operation, request_key, payload_hash)
    VALUES ('CANCEL', $1, $2)
    ON CONFLICT DO NOTHING;
    """;

		await using var claim = new NpgsqlCommand(
			claimSql, connection, transaction);

		claim.Parameters.AddWithValue(requestKey);
		claim.Parameters.AddWithValue(hash);

		var inserted = await claim.ExecuteNonQueryAsync(ct);

		if (inserted == 0)
		{
			const string replaySql = """
        SELECT payload_hash,
               response_status,
               response_body::text
        FROM operation_requests
        WHERE operation = 'CANCEL'
          AND request_key = $1;
        """;

			await using var replay = new NpgsqlCommand(
				replaySql, connection, transaction);

			replay.Parameters.AddWithValue(requestKey);

			await using var reader =
				await replay.ExecuteReaderAsync(ct);

			if (!await reader.ReadAsync(ct))
			{
				throw new InvalidOperationException(
					"Idempotency record was not found.");
			}

			var previousHash = reader.GetString(0);

			if (reader.IsDBNull(1) || reader.IsDBNull(2))
			{
				throw new InvalidOperationException(
					"Incomplete idempotency record.");
			}

			var previousStatus = reader.GetInt32(1);
			var previousJson = reader.GetString(2);

			if (previousHash != hash)
			{
				return Error(
					409,
					"Idempotency key reused with different data.");
			}

			return new ReservationResult(
				previousStatus,
				previousJson);
		}

		// 1. Find which product belongs to this order.
		const string findOrderSql = """
    SELECT product_id
    FROM orders
    WHERE id = $1;
    """;

		Guid productId;

		await using (var findOrder = new NpgsqlCommand(
			findOrderSql, connection, transaction))
		{
			findOrder.Parameters.AddWithValue(orderId);

			var value = await findOrder.ExecuteScalarAsync(ct);

			if (value is not Guid foundProductId)
			{
				var notFound = Error(404, "Reservation not found.");

				await SaveResponseAsync(
					connection,
					transaction,
					requestKey,
					notFound,
					ct);

				await transaction.CommitAsync(ct);

				return notFound;
			}

			productId = foundProductId;
		}

		// 2. Lock the inventory row for this product.
		const string lockInventorySql = """
    SELECT product_id
    FROM inventory
    WHERE product_id = $1
    FOR UPDATE;
    """;

		await using (var lockInventory = new NpgsqlCommand(
			lockInventorySql, connection, transaction))
		{
			lockInventory.Parameters.AddWithValue(productId);

			var lockedProductId =
				await lockInventory.ExecuteScalarAsync(ct);

			if (lockedProductId is null)
			{
				throw new InvalidOperationException(
					"Inventory row was not found for the order.");
			}
		}

		// 3. Lock and re-read the order after acquiring inventory lock.
		const string lockOrderSql = """
    SELECT state, expires_at
    FROM orders
    WHERE id = $1
      AND product_id = $2
    FOR UPDATE;
    """;

		string state;
		DateTime expiresAt;

		await using (var lockOrder = new NpgsqlCommand(
			lockOrderSql, connection, transaction))
		{
			lockOrder.Parameters.AddWithValue(orderId);
			lockOrder.Parameters.AddWithValue(productId);

			await using var reader =
				await lockOrder.ExecuteReaderAsync(ct);

			if (!await reader.ReadAsync(ct))
			{
				throw new InvalidOperationException(
					"Order disappeared while acquiring its lock.");
			}

			state = reader.GetString(0);
			expiresAt = reader.GetDateTime(1);
		}

		// 4. Get the decision time AFTER acquiring both locks.
		const string timeSql = "SELECT clock_timestamp();";

		DateTime decisionTime;

		await using (var timeCommand = new NpgsqlCommand(
			timeSql, connection, transaction))
		{
			decisionTime = (DateTime)
				(await timeCommand.ExecuteScalarAsync(ct))!;
		}

        var paymentError=await PaymentGuard.CancellationError(connection,transaction,orderId,ct);
        if(paymentError is not null)return Error(409,paymentError);
		ReservationResult result;

		if (state != "RESERVED" || expiresAt <= decisionTime)
		{
			result = Error(
				409,
				"Reservation cannot be cancelled.");
		}
		else
		{
			const string cancelSql = """
        UPDATE orders
        SET state = 'CANCELLED',
            updated_at = $1
        WHERE id = $2
          AND state = 'RESERVED';
        """;

			await using (var cancel = new NpgsqlCommand(
				cancelSql, connection, transaction))
			{
				cancel.Parameters.AddWithValue(decisionTime);
				cancel.Parameters.AddWithValue(orderId);

				var updated = await cancel.ExecuteNonQueryAsync(ct);

				if (updated != 1)
				{
					throw new InvalidOperationException(
						"Failed to cancel the reservation.");
				}
			}

			const string eventSql = """
        INSERT INTO inventory_events
            (product_id, order_id, kind,
             shelf_delta, picked_delta)
        VALUES ($1, $2, 'CANCEL', 0, 0);
        """;

			await using (var eventCommand = new NpgsqlCommand(
				eventSql, connection, transaction))
			{
				eventCommand.Parameters.AddWithValue(productId);
				eventCommand.Parameters.AddWithValue(orderId);

				await eventCommand.ExecuteNonQueryAsync(ct);
			}

			result = new ReservationResult(
				200,
				System.Text.Json.JsonSerializer.Serialize(
					new
					{
						orderId,
						state = "CANCELLED"
					}));
		}

		await SaveResponseAsync(
			connection,
			transaction,
			requestKey,
			result,
			ct);

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
		const string sql = """
        UPDATE operation_requests
        SET response_status = $1,
            response_body = $2::jsonb
        WHERE operation = 'CANCEL'
          AND request_key = $3;
        """;

		await using var command =
			new NpgsqlCommand(sql, connection, transaction);

		command.Parameters.AddWithValue(result.StatusCode);
		command.Parameters.AddWithValue(result.Json);
		command.Parameters.AddWithValue(requestKey);

		if (await command.ExecuteNonQueryAsync(ct) != 1)
		{
			throw new InvalidOperationException(
				"Failed to finalize cancellation response.");
		}
	}

	private static ReservationResult Error(
		int statusCode,
		string message)
	{
		return new ReservationResult(
			statusCode,
			System.Text.Json.JsonSerializer.Serialize(
				new { error = message }));
	}

}
