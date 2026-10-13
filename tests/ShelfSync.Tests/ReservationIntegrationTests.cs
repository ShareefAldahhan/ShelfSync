using System.Text.Json;
using Npgsql;
using ShelfSync.Api.Models;
using ShelfSync.Api.Services;

namespace ShelfSync.Tests;

public sealed class ReservationIntegrationTests
{
    [Fact]
    public async Task CompletionFailure_RollsBackReservationAndIdempotencyClaim()
    {
        await using var scope = await PostgresTestScope.CreateAsync();
        var requestKey = scope.NewRequestKey();
        await scope.InstallCompletionFailureAsync(requestKey);
        var service = new ReservationService(scope.Db);

        // The trigger checks that both order and RESERVE event exist inside the
        // transaction before raising this specific exception at response completion.
        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            service.CreateAsync(new ReservationRequest(scope.ProductId, 2), requestKey));
        Assert.Equal("P0001", exception.SqlState);
        Assert.Equal("ShelfSync test-only reservation completion failure", exception.MessageText);

        // A separate connection observes only committed state after service disposal.
        await using var connection = await scope.Db.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT
                (SELECT COUNT(*) FROM orders WHERE product_id = $1),
                (SELECT COUNT(*) FROM inventory_events
                 WHERE product_id = $1 AND kind = 'RESERVE'),
                (SELECT COUNT(*) FROM operation_requests
                 WHERE operation = 'RESERVE' AND request_key = $2),
                (SELECT shelf_units FROM inventory WHERE product_id = $1);
            """, connection);
        command.Parameters.AddWithValue(scope.ProductId);
        command.Parameters.AddWithValue(requestKey);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0L, reader.GetInt64(0));
        Assert.Equal(0L, reader.GetInt64(1));
        // Counts all claim rows, whether response fields are NULL or completed.
        Assert.Equal(0L, reader.GetInt64(2));
        Assert.Equal(10, reader.GetInt32(3));
        // Scope disposal drops its unique trigger/function even if assertions fail.
    }

    [Fact]
    public async Task ExpiredReservation_TransitionsOnce()
    {
        await using var scope = await PostgresTestScope.CreateAsync();
        var service = new ReservationService(scope.Db);
        var result = await service.CreateAsync(
            new ReservationRequest(scope.ProductId, 2), scope.NewRequestKey());
        Assert.Equal(201, result.StatusCode);
        using var document = JsonDocument.Parse(result.Json);
        var orderId = document.RootElement.GetProperty("orderId").GetGuid();

        await using (var connection = await scope.Db.OpenConnectionAsync())
        {
            // Shift BOTH timestamps using one decision time so the real schema's
            // expires_at > created_at and updated_at >= created_at checks hold.
            await using var command = new NpgsqlCommand("""
                WITH decision AS (SELECT clock_timestamp() AS now_at)
                UPDATE orders
                SET created_at = decision.now_at - INTERVAL '2 hours',
                    expires_at = decision.now_at - INTERVAL '1 hour',
                    updated_at = decision.now_at
                FROM decision
                WHERE id = $1 AND product_id = $2 AND state = 'RESERVED';
                """, connection);
            command.Parameters.AddWithValue(orderId);
            command.Parameters.AddWithValue(scope.ProductId);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        var expirationService = new ReservationExpirationService(scope.Db);
        // Batch counts may include unrelated records. Check only our order and
        // allow older test records to drain ahead of it, without wall-clock waits.
        var expired = false;
        for (var sweep = 0; sweep < 20 && !expired; sweep++)
        {
            await expirationService.ExpireBatchAsync();
            await using var connection = await scope.Db.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                "SELECT state FROM orders WHERE id = $1 AND product_id = $2;", connection);
            command.Parameters.AddWithValue(orderId);
            command.Parameters.AddWithValue(scope.ProductId);
            expired = (string?)await command.ExecuteScalarAsync() == "EXPIRED";
        }
        Assert.True(expired, "The test reservation must expire within the bounded sweep budget.");

        await expirationService.ExpireBatchAsync();
        await expirationService.ExpireBatchAsync();

        await using var verifyConnection = await scope.Db.OpenConnectionAsync();
        await using var verify = new NpgsqlCommand("""
            SELECT o.state,
                (SELECT COUNT(*) FROM inventory_events e
                 WHERE e.order_id = o.id AND e.product_id = o.product_id AND e.kind = 'EXPIRE')
            FROM orders o WHERE o.id = $1 AND o.product_id = $2;
            """, verifyConnection);
        verify.Parameters.AddWithValue(orderId);
        verify.Parameters.AddWithValue(scope.ProductId);
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("EXPIRED", reader.GetString(0));
        Assert.Equal(1L, reader.GetInt64(1));
    }

    [Fact]
    public async Task ConcurrentReservations_NeverOversell()
    {
        await using var scope = await PostgresTestScope.CreateAsync(shelfUnits: 6);
        var requestKeys = Enumerable.Range(0, 10).Select(_ => scope.NewRequestKey()).ToArray();
        var service = new ReservationService(scope.Db);
        var request = new ReservationRequest(scope.ProductId, 1);
        var results = await Task.WhenAll(requestKeys.Select(key => service.CreateAsync(request, key)));
        Assert.Equal(6, results.Count(r => r.StatusCode == 201));
        Assert.Equal(4, results.Count(r => r.StatusCode == 409));

        await using var connection = await scope.Db.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT
                (SELECT COALESCE(SUM(quantity), 0) FROM orders
                 WHERE product_id = $1 AND state = 'RESERVED'),
                (SELECT COUNT(*) FROM inventory_events
                 WHERE product_id = $1 AND kind = 'RESERVE');
            """, connection);
        command.Parameters.AddWithValue(scope.ProductId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(6L, reader.GetInt64(0));
        Assert.Equal(6L, reader.GetInt64(1));
    }

    [Fact]
    public async Task ReusingKeyWithDifferentQuantity_Returns409()
    {
        await using var scope = await PostgresTestScope.CreateAsync();
        var requestKey = scope.NewRequestKey();
        var service = new ReservationService(scope.Db);
        var first = await service.CreateAsync(new ReservationRequest(scope.ProductId, 2), requestKey);
        var second = await service.CreateAsync(new ReservationRequest(scope.ProductId, 3), requestKey);
        Assert.Equal(201, first.StatusCode);
        Assert.Equal(409, second.StatusCode);
        Assert.Contains("Idempotency key reused with different data.", second.Json);

        await using var connection = await scope.Db.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT COUNT(*) FROM orders WHERE product_id = $1;", connection);
        command.Parameters.AddWithValue(scope.ProductId);
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task RetryingReservation_ReturnsSameOrder()
    {
        await using var scope = await PostgresTestScope.CreateAsync();
        var requestKey = scope.NewRequestKey();
        var service = new ReservationService(scope.Db);
        var request = new ReservationRequest(scope.ProductId, 2);
        var first = await service.CreateAsync(request, requestKey);
        var second = await service.CreateAsync(request, requestKey);
        Assert.Equal(201, first.StatusCode);
        Assert.Equal(first.StatusCode, second.StatusCode);
        using var firstDocument = JsonDocument.Parse(first.Json);
        using var secondDocument = JsonDocument.Parse(second.Json);
        Assert.True(JsonElement.DeepEquals(firstDocument.RootElement, secondDocument.RootElement),
            "The retried reservation must return the same JSON data.");
        var orderId = firstDocument.RootElement.GetProperty("orderId").GetGuid();

        await using var connection = await scope.Db.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT
                (SELECT COUNT(*) FROM orders WHERE product_id = $1),
                (SELECT COUNT(*) FROM inventory_events
                 WHERE product_id = $1 AND order_id = $2 AND kind = 'RESERVE');
            """, connection);
        command.Parameters.AddWithValue(scope.ProductId);
        command.Parameters.AddWithValue(orderId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(1L, reader.GetInt64(1));
    }
}
