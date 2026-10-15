
using System.Text.Json;
using Npgsql;
using ShelfSync.Api.Models;
using ShelfSync.Api.Services;
using Xunit;

namespace ShelfSync.Tests;

public sealed class OrderPickingIntegrationTests
{
	[Fact]
	public async Task PickingReservation_MovesStockAndCreatesOneEvent()
	{
		await using var scope = await PostgresTestScope.CreateAsync();

		var reservationService = new ReservationService(scope.Db);
		var pickingService = new OrderPickingService(scope.Db);

		var reservation = await reservationService.CreateAsync(
			new ReservationRequest(scope.ProductId, 3),
			scope.NewRequestKey());

		Assert.Equal(201, reservation.StatusCode);

		using var document = JsonDocument.Parse(reservation.Json);
		var orderId = document.RootElement.GetProperty("orderId").GetGuid();

		var result = await pickingService.PickAsync(
			orderId, scope.NewRequestKey());

		Assert.Equal(200, result.StatusCode);

		using var response = JsonDocument.Parse(result.Json);
		Assert.Equal(
			"PICKED",
			response.RootElement.GetProperty("state").GetString());

		await using var connection = await scope.Db.OpenConnectionAsync();
		await using var command = new NpgsqlCommand("""
            SELECT o.state, i.shelf_units, i.picked_units,
                   (SELECT COUNT(*) FROM inventory_events e
                    WHERE e.order_id = o.id AND e.kind = 'PICK'),
                   (SELECT COALESCE(SUM(e.shelf_delta), 0)
                    FROM inventory_events e
                    WHERE e.order_id = o.id AND e.kind = 'PICK'),
                   (SELECT COALESCE(SUM(e.picked_delta), 0)
                    FROM inventory_events e
                    WHERE e.order_id = o.id AND e.kind = 'PICK')
            FROM orders o
            JOIN inventory i ON i.product_id = o.product_id
            WHERE o.id = $1;
            """, connection);

		command.Parameters.AddWithValue(orderId);

		await using var reader = await command.ExecuteReaderAsync();

		Assert.True(await reader.ReadAsync());
		Assert.Equal("PICKED", reader.GetString(0));
		Assert.Equal(7, reader.GetInt32(1));
		Assert.Equal(3, reader.GetInt32(2));
		Assert.Equal(1L, reader.GetInt64(3));
		Assert.Equal(-3L, reader.GetInt64(4));
		Assert.Equal(3L, reader.GetInt64(5));
	}

	[Fact]
	public async Task RetryingPick_DoesNotMoveStockTwice()
	{
		await using var scope = await PostgresTestScope.CreateAsync();

		var reservationService = new ReservationService(scope.Db);
		var pickingService = new OrderPickingService(scope.Db);

		var reservation = await reservationService.CreateAsync(
			new ReservationRequest(scope.ProductId, 2),
			scope.NewRequestKey());

		Assert.Equal(201, reservation.StatusCode);

		using var document = JsonDocument.Parse(reservation.Json);
		var orderId = document.RootElement.GetProperty("orderId").GetGuid();

		var key = scope.NewRequestKey();

		var first = await pickingService.PickAsync(orderId, key);
		var second = await pickingService.PickAsync(orderId, key);

		Assert.Equal(200, first.StatusCode);
		Assert.Equal(first.StatusCode, second.StatusCode);
		using var firstJson = JsonDocument.Parse(first.Json);
		using var secondJson = JsonDocument.Parse(second.Json);

		Assert.Equal(
			firstJson.RootElement.GetProperty("orderId").GetGuid(),
			secondJson.RootElement.GetProperty("orderId").GetGuid());

		Assert.Equal(
			firstJson.RootElement.GetProperty("state").GetString(),
			secondJson.RootElement.GetProperty("state").GetString());

		await using var connection = await scope.Db.OpenConnectionAsync();
		await using var command = new NpgsqlCommand("""
            SELECT i.shelf_units, i.picked_units,
                   (SELECT COUNT(*) FROM inventory_events e
                    WHERE e.order_id = $1 AND e.kind = 'PICK')
            FROM inventory i
            WHERE i.product_id = $2;
            """, connection);

		command.Parameters.AddWithValue(orderId);
		command.Parameters.AddWithValue(scope.ProductId);

		await using var reader = await command.ExecuteReaderAsync();

		Assert.True(await reader.ReadAsync());
		Assert.Equal(8, reader.GetInt32(0));
		Assert.Equal(2, reader.GetInt32(1));
		Assert.Equal(1L, reader.GetInt64(2));
	}

	[Fact]
	public async Task ExpiredReservation_CannotBePicked()
	{
		await using var scope = await PostgresTestScope.CreateAsync();

		var reservationService = new ReservationService(scope.Db);
		var pickingService = new OrderPickingService(scope.Db);

		var reservation = await reservationService.CreateAsync(
			new ReservationRequest(scope.ProductId, 2),
			scope.NewRequestKey());

		Assert.Equal(201, reservation.StatusCode);

		using var document = JsonDocument.Parse(reservation.Json);
		var orderId = document.RootElement.GetProperty("orderId").GetGuid();

		await using (var connection = await scope.Db.OpenConnectionAsync())
		{
			await using var command = new NpgsqlCommand("""
                UPDATE orders
                SET created_at = clock_timestamp() - INTERVAL '20 minutes',
                    expires_at = clock_timestamp() - INTERVAL '5 minutes',
                    updated_at = clock_timestamp()
                WHERE id = $1;
                """, connection);

			command.Parameters.AddWithValue(orderId);
			await command.ExecuteNonQueryAsync();
		}

		var result = await pickingService.PickAsync(
			orderId, scope.NewRequestKey());

		Assert.Equal(409, result.StatusCode);

		await using var verifyConnection = await scope.Db.OpenConnectionAsync();
		await using var verify = new NpgsqlCommand("""
            SELECT i.shelf_units, i.picked_units,
                   (SELECT COUNT(*) FROM inventory_events e
                    WHERE e.order_id = $1 AND e.kind = 'PICK')
            FROM inventory i
            WHERE i.product_id = $2;
            """, verifyConnection);

		verify.Parameters.AddWithValue(orderId);
		verify.Parameters.AddWithValue(scope.ProductId);

		await using var reader = await verify.ExecuteReaderAsync();

		Assert.True(await reader.ReadAsync());
		Assert.Equal(10, reader.GetInt32(0));
		Assert.Equal(0, reader.GetInt32(1));
		Assert.Equal(0L, reader.GetInt64(2));
	}

	[Fact]
	public async Task ConcurrentPicks_OnlyOneSucceeds()
	{
		await using var scope = await PostgresTestScope.CreateAsync();

		var reservationService = new ReservationService(scope.Db);
		var pickingService = new OrderPickingService(scope.Db);

		var reservation = await reservationService.CreateAsync(
			new ReservationRequest(scope.ProductId, 4),
			scope.NewRequestKey());

		Assert.Equal(201, reservation.StatusCode);

		using var document = JsonDocument.Parse(reservation.Json);
		var orderId = document.RootElement.GetProperty("orderId").GetGuid();

		var keys = Enumerable.Range(0, 10)
			.Select(_ => scope.NewRequestKey())
			.ToArray();

		var results = await Task.WhenAll(
			keys.Select(key => pickingService.PickAsync(orderId, key)));

		Assert.Equal(1, results.Count(r => r.StatusCode == 200));
		Assert.Equal(9, results.Count(r => r.StatusCode == 409));

		await using var connection = await scope.Db.OpenConnectionAsync();
		await using var command = new NpgsqlCommand("""
            SELECT i.shelf_units, i.picked_units,
                   (SELECT COUNT(*) FROM inventory_events e
                    WHERE e.order_id = $1 AND e.kind = 'PICK')
            FROM inventory i
            WHERE i.product_id = $2;
            """, connection);

		command.Parameters.AddWithValue(orderId);
		command.Parameters.AddWithValue(scope.ProductId);

		await using var reader = await command.ExecuteReaderAsync();

		Assert.True(await reader.ReadAsync());
		Assert.Equal(6, reader.GetInt32(0));
		Assert.Equal(4, reader.GetInt32(1));
		Assert.Equal(1L, reader.GetInt64(2));
	}

	[Fact]
	public async Task ReusingPickKeyForDifferentOrder_Returns409()
	{
		await using var scope = await PostgresTestScope.CreateAsync();

		var reservationService = new ReservationService(scope.Db);
		var pickingService = new OrderPickingService(scope.Db);

		var firstReservation = await reservationService.CreateAsync(
			new ReservationRequest(scope.ProductId, 2),
			scope.NewRequestKey());

		var secondReservation = await reservationService.CreateAsync(
			new ReservationRequest(scope.ProductId, 2),
			scope.NewRequestKey());

		Assert.Equal(201, firstReservation.StatusCode);
		Assert.Equal(201, secondReservation.StatusCode);

		using var firstDocument = JsonDocument.Parse(firstReservation.Json);
		using var secondDocument = JsonDocument.Parse(secondReservation.Json);

		var firstOrderId =
			firstDocument.RootElement.GetProperty("orderId").GetGuid();

		var secondOrderId =
			secondDocument.RootElement.GetProperty("orderId").GetGuid();

		var key = scope.NewRequestKey();

		var first = await pickingService.PickAsync(firstOrderId, key);
		var second = await pickingService.PickAsync(secondOrderId, key);

		Assert.Equal(200, first.StatusCode);
		Assert.Equal(409, second.StatusCode);

		await using var connection = await scope.Db.OpenConnectionAsync();
		await using var command = new NpgsqlCommand("""
            SELECT state FROM orders WHERE id = $1;
            """, connection);

		command.Parameters.AddWithValue(secondOrderId);

		Assert.Equal("RESERVED", (string?)await command.ExecuteScalarAsync());
	}
}
