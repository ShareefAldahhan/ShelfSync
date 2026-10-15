
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using ShelfSync.Api.Models;

namespace ShelfSync.Api.Services;

public sealed class PosSaleService(NpgsqlDataSource db)
{
	public async Task<ReservationResult> RecordAsync(
		PosSaleRequest request,
		CancellationToken ct = default)
	{
		if (request.EventId == Guid.Empty ||
			request.ProductId == Guid.Empty ||
			request.Quantity is < 1 or > 100)
		{
			return Error(400, "Valid event ID, product ID, and quantity (1-100) are required.");
		}

		var payload = $"{request.ProductId:D}:{request.Quantity}";
		var payloadHash = Convert.ToHexString(
			SHA256.HashData(Encoding.UTF8.GetBytes(payload)));

		await using var connection = await db.OpenConnectionAsync(ct);
		await using var transaction = await connection.BeginTransactionAsync(ct);

		// Claim this event ID, preventing duplicate processing.
		int inserted;

		await using (var claim = new NpgsqlCommand("""
            INSERT INTO pos_events
                (event_id, product_id, quantity, payload_hash, response_body)
            VALUES ($1, $2, $3, $4, '{}'::jsonb)
            ON CONFLICT (event_id) DO NOTHING;
            """, connection, transaction))
		{
			claim.Parameters.AddWithValue(request.EventId);
			claim.Parameters.AddWithValue(request.ProductId);
			claim.Parameters.AddWithValue(request.Quantity);
			claim.Parameters.AddWithValue(payloadHash);

			inserted = await claim.ExecuteNonQueryAsync(ct);
		}

		if (inserted == 0)
		{
			await using var replay = new NpgsqlCommand("""
                SELECT payload_hash, response_body::text
                FROM pos_events
                WHERE event_id = $1;
                """, connection, transaction);

			replay.Parameters.AddWithValue(request.EventId);

			await using var reader = await replay.ExecuteReaderAsync(ct);

			if (!await reader.ReadAsync(ct))
				throw new InvalidOperationException("POS event disappeared.");

			if (reader.GetString(0) != payloadHash)
				return Error(409, "POS event ID belongs to a different sale.");

			return new ReservationResult(200, reader.GetString(1));
		}

		int shelfUnits;
		int safetyBuffer;
		bool quarantined;

		// Lock inventory using the same ordering as reservations.
		await using (var inventory = new NpgsqlCommand("""
            SELECT shelf_units, safety_buffer, quarantined
            FROM inventory
            WHERE product_id = $1
            FOR UPDATE;
            """, connection, transaction))
		{
			inventory.Parameters.AddWithValue(request.ProductId);

			await using var reader = await inventory.ExecuteReaderAsync(ct);

			if (!await reader.ReadAsync(ct))
			{
				await transaction.RollbackAsync(ct);
				return Error(404, "Product inventory not found.");
			}

			shelfUnits = reader.GetInt32(0);
			safetyBuffer = reader.GetInt32(1);
			quarantined = reader.GetBoolean(2);
		}

		if (quarantined)
		{
			await transaction.RollbackAsync(ct);
			return Error(409, "Inventory is quarantined.");
		}

		// Calculate available stock after acquiring the inventory lock.
		long activeReserved;

		await using (var reserved = new NpgsqlCommand("""
            WITH decision AS (
                SELECT clock_timestamp() AS now_at
            )
            SELECT COALESCE(SUM(o.quantity), 0)
            FROM orders o, decision
            WHERE o.product_id = $1
              AND o.state = 'RESERVED'
              AND o.expires_at > decision.now_at;
            """, connection, transaction))
		{
			reserved.Parameters.AddWithValue(request.ProductId);
			activeReserved = (long)(await reserved.ExecuteScalarAsync(ct))!;
		}

		long available = (long)shelfUnits - safetyBuffer - activeReserved;

		if (available < request.Quantity)
		{
			await transaction.RollbackAsync(ct);
			return Error(409, "Insufficient unreserved shelf inventory.");
		}

		DateTime decisionTime;

		await using (var clock = new NpgsqlCommand(
			"SELECT clock_timestamp();", connection, transaction))
		{
			decisionTime = (DateTime)(await clock.ExecuteScalarAsync(ct))!;
		}

		await using (var update = new NpgsqlCommand("""
            UPDATE inventory
            SET shelf_units = shelf_units - $1,
                updated_at = $2
            WHERE product_id = $3;
            """, connection, transaction))
		{
			update.Parameters.AddWithValue(request.Quantity);
			update.Parameters.AddWithValue(decisionTime);
			update.Parameters.AddWithValue(request.ProductId);

			if (await update.ExecuteNonQueryAsync(ct) != 1)
				throw new InvalidOperationException("Failed to update shelf inventory.");
		}

		var response = new
		{
			eventId = request.EventId,
			productId = request.ProductId,
			quantity = request.Quantity,
			state = "COMMITTED"
		};

		var json = JsonSerializer.Serialize(response);

		await using (var audit = new NpgsqlCommand("""
            INSERT INTO inventory_events
                (product_id, pos_event_id, kind, shelf_delta, picked_delta)
            VALUES ($1, $2, 'SALE', $3, 0);
            """, connection, transaction))
		{
			audit.Parameters.AddWithValue(request.ProductId);
			audit.Parameters.AddWithValue(request.EventId);
			audit.Parameters.AddWithValue(-request.Quantity);

			await audit.ExecuteNonQueryAsync(ct);
		}

		await using (var save = new NpgsqlCommand("""
            UPDATE pos_events
            SET response_body = $1::jsonb
            WHERE event_id = $2;
            """, connection, transaction))
		{
			save.Parameters.AddWithValue(json);
			save.Parameters.AddWithValue(request.EventId);

			if (await save.ExecuteNonQueryAsync(ct) != 1)
				throw new InvalidOperationException("Failed to save POS response.");
		}

		await transaction.CommitAsync(ct);
		return new ReservationResult(200, json);
	}

	private static ReservationResult Error(int status, string message) =>
		new(status, JsonSerializer.Serialize(new { error = message }));
}
