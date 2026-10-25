
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using ShelfSync.Api.Models;

namespace ShelfSync.Api.Services;

public sealed record ReservationResult(
    int StatusCode,
    string Json
);

public sealed class ReservationService(NpgsqlDataSource db)
{
    public async Task<ReservationResult> CreateAsync(
        ReservationRequest request,
        Guid requestKey,
        CancellationToken ct = default, Guid? ownerUserId = null)
    {
        // Validate before starting a database transaction.
        if (request.ProductId == Guid.Empty ||
            request.Quantity is < 1 or > 100)
        {
            return Error(400, "Invalid product or quantity.");
        }

        var payload = $"{request.ProductId:D}:{request.Quantity}";
        if (ownerUserId is not null) payload += $":{ownerUserId:D}";
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(payload)));

        await using var connection =
            await db.OpenConnectionAsync(ct);

        await using var transaction =
            await connection.BeginTransactionAsync(ct);

        // Claim the idempotency key.
        // A concurrent request using the same key waits for
        // the first transaction to finish.
        const string claimSql = """
            INSERT INTO operation_requests
                (operation, request_key, payload_hash)
            VALUES ('RESERVE', $1, $2)
            ON CONFLICT DO NOTHING;
            """;

        await using var claim =
            new NpgsqlCommand(claimSql, connection, transaction);

        claim.Parameters.AddWithValue(requestKey);
        claim.Parameters.AddWithValue(hash);

        var inserted = await claim.ExecuteNonQueryAsync(ct);

        if (inserted == 0)
        {
            const string replaySql = """
                SELECT payload_hash, response_status,
                       response_body::text
                FROM operation_requests
                WHERE operation = 'RESERVE'
                  AND request_key = $1;
                """;

            await using var replay =
                new NpgsqlCommand(replaySql, connection, transaction);

            replay.Parameters.AddWithValue(requestKey);

            string previousHash;
            int previousStatus;
            string previousJson;

            await using (var reader =
                await replay.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct))
                    throw new InvalidOperationException(
                        "Idempotency record was not found.");

                previousHash = reader.GetString(0);

                if (reader.IsDBNull(1) || reader.IsDBNull(2))
                    throw new InvalidOperationException(
                        "Incomplete idempotency record.");

                previousStatus = reader.GetInt32(1);
                previousJson = reader.GetString(2);
            }

            if (previousHash != hash)
            {
                return Error(409,
                    "Idempotency key reused with different data.");
            }

            return new ReservationResult(
                previousStatus, previousJson);
        }

        // Lock this product's inventory row.
        const string inventorySql = """
            SELECT i.shelf_units,
                   i.safety_buffer,
                   i.quarantined,
                   p.price_cents
            FROM inventory i
            JOIN products p ON p.id = i.product_id
            WHERE i.product_id = $1
            FOR UPDATE OF i;
            """;

        await using var inventoryCommand =
            new NpgsqlCommand(
                inventorySql, connection, transaction);

        inventoryCommand.Parameters.AddWithValue(request.ProductId);

        int shelfUnits = 0;
        int safetyBuffer = 0;
        int priceCents = 0;
        bool quarantined = false;
        bool found;

        await using (var reader =
            await inventoryCommand.ExecuteReaderAsync(ct))
        {
            found = await reader.ReadAsync(ct);

            if (found)
            {
                shelfUnits = reader.GetInt32(0);
                safetyBuffer = reader.GetInt32(1);
                quarantined = reader.GetBoolean(2);
                priceCents = reader.GetInt32(3);
            }
        }

        ReservationResult result;

        if (!found)
        {
            result = Error(404, "Product not found.");
        }
        else
        {
            // Obtain decision time AFTER acquiring the row lock.
            const string availabilitySql = """
                WITH decision AS (
                    SELECT clock_timestamp() AS now_at
                )
                SELECT decision.now_at,
                       COALESCE(SUM(o.quantity), 0)::bigint
                FROM decision
                LEFT JOIN orders o
                    ON o.product_id = $1
                   AND o.state = 'RESERVED'
                   AND o.expires_at > decision.now_at
                GROUP BY decision.now_at;
                """;

            await using var availabilityCommand =
                new NpgsqlCommand(
                    availabilitySql, connection, transaction);

            availabilityCommand.Parameters.AddWithValue(
                request.ProductId);

            DateTime decisionTime;
            long reservedUnits;

            await using (var reader =
                await availabilityCommand.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct))
                    throw new InvalidOperationException(
                        "Availability query returned no row.");

                decisionTime = reader.GetDateTime(0);
                reservedUnits = reader.GetInt64(1);
            }

            var available = Math.Max(
                0L,
                (long)shelfUnits - safetyBuffer - reservedUnits);

            if (quarantined || request.Quantity > available)
            {
                result = Error(409, "Insufficient available stock.");
            }
            else
            {
                var orderId = Guid.NewGuid();
                var expiresAt = decisionTime.AddMinutes(15);

                const string orderSql = """
                    INSERT INTO orders
                        (id, product_id, quantity,
                         unit_price_cents, state,
                         expires_at, created_at, updated_at, owner_user_id)
                    VALUES
                        ($1, $2, $3, $4, 'RESERVED',
                         $5, $6, $6, $7);
                    """;

                await using (var orderCommand =
                    new NpgsqlCommand(
                        orderSql, connection, transaction))
                {
                    orderCommand.Parameters.AddWithValue(orderId);
                    orderCommand.Parameters.AddWithValue(
                        request.ProductId);
                    orderCommand.Parameters.AddWithValue(
                        request.Quantity);
                    orderCommand.Parameters.AddWithValue(priceCents);
                    orderCommand.Parameters.AddWithValue(expiresAt);
                    orderCommand.Parameters.AddWithValue(decisionTime);
                    orderCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid, Value = (object?)ownerUserId ?? DBNull.Value });

                    await orderCommand.ExecuteNonQueryAsync(ct);
                }

                const string eventSql = """
                    INSERT INTO inventory_events
                        (product_id, order_id, kind,
                         shelf_delta, picked_delta)
                    VALUES ($1, $2, 'RESERVE', 0, 0);
                    """;

                await using (var eventCommand =
                    new NpgsqlCommand(
                        eventSql, connection, transaction))
                {
                    eventCommand.Parameters.AddWithValue(
                        request.ProductId);
                    eventCommand.Parameters.AddWithValue(orderId);

                    await eventCommand.ExecuteNonQueryAsync(ct);
                }

                var response = new ReservationResponse(
                    orderId,
                    request.ProductId,
                    request.Quantity,
                    "RESERVED",
                    expiresAt);

                result = new ReservationResult(
                    201,
                    JsonSerializer.Serialize(
                        response,
                        new JsonSerializerOptions(
                            JsonSerializerDefaults.Web)));
            }
        }

        // Persist the exact response for future retries.
        const string completeSql = """
            UPDATE operation_requests
            SET response_status = $1,
                response_body = $2::jsonb
            WHERE operation = 'RESERVE'
              AND request_key = $3;
            """;

        await using (var complete =
            new NpgsqlCommand(
                completeSql, connection, transaction))
        {
            complete.Parameters.AddWithValue(result.StatusCode);
            complete.Parameters.AddWithValue(result.Json);
            complete.Parameters.AddWithValue(requestKey);

            if (await complete.ExecuteNonQueryAsync(ct) != 1)
                throw new InvalidOperationException(
                    "Failed to finalize idempotency record.");
        }

        await transaction.CommitAsync(ct);
        return result;
    }

    private static ReservationResult Error(
        int statusCode, string message)
    {
        return new ReservationResult(
            statusCode,
            JsonSerializer.Serialize(new { error = message }));
    }
}
