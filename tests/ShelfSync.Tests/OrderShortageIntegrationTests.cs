
using System.Text.Json;
using Npgsql;
using ShelfSync.Api.Models;
using ShelfSync.Api.Services;
using Xunit;

namespace ShelfSync.Tests;

public sealed class OrderShortageIntegrationTests
{
	[Fact]
	public async Task ReportingShortage_HoldsOrderWithoutMovingStock()
	{
		await using var scope = await PostgresTestScope.CreateAsync();

		var reservation = await new ReservationService(scope.Db).CreateAsync(
			new ReservationRequest(scope.ProductId, 3),
			scope.NewRequestKey());

		Assert.Equal(201, reservation.StatusCode);

		using var document = JsonDocument.Parse(reservation.Json);
		var orderId = document.RootElement.GetProperty("orderId").GetGuid();

		var shortage = await new OrderShortageService(scope.Db).ReportAsync(
			orderId, scope.NewRequestKey());

		Assert.Equal(200, shortage.StatusCode);

		using var response = JsonDocument.Parse(shortage.Json);
		Assert.Equal("SHORTAGE_PENDING",
			response.RootElement.GetProperty("state").GetString());

		await using var connection = await scope.Db.OpenConnectionAsync();
		await using var command = new NpgsqlCommand("""
            SELECT o.state, o.reason, i.shelf_units, i.picked_units,
                   (SELECT COUNT(*) FROM inventory_events e
                    WHERE e.order_id = o.id AND e.kind = 'SHORTAGE')
            FROM orders o
            JOIN inventory i ON i.product_id = o.product_id
            WHERE o.id = $1;
            """, connection);

		command.Parameters.AddWithValue(orderId);

		await using var reader = await command.ExecuteReaderAsync();

		Assert.True(await reader.ReadAsync());
		Assert.Equal("SHORTAGE_PENDING", reader.GetString(0));
		Assert.Equal("PHYSICAL_STOCK_SHORTAGE", reader.GetString(1));
		Assert.Equal(10, reader.GetInt32(2));
		Assert.Equal(0, reader.GetInt32(3));
		Assert.Equal(1L, reader.GetInt64(4));
	}

	[Fact]
	public async Task RetryingShortage_DoesNotCreateDuplicateEvents()
	{
		await using var scope = await PostgresTestScope.CreateAsync();

		var reservation = await new ReservationService(scope.Db).CreateAsync(
			new ReservationRequest(scope.ProductId, 2),
			scope.NewRequestKey());

		Assert.Equal(201, reservation.StatusCode);

		using var document = JsonDocument.Parse(reservation.Json);
		var orderId = document.RootElement.GetProperty("orderId").GetGuid();

		var service = new OrderShortageService(scope.Db);
		var key = scope.NewRequestKey();

		var first = await service.ReportAsync(orderId, key);
		var second = await service.ReportAsync(orderId, key);

		Assert.Equal(200, first.StatusCode);
		Assert.Equal(200, second.StatusCode);

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
            SELECT COUNT(*) FROM inventory_events
            WHERE order_id = $1 AND kind = 'SHORTAGE';
            """, connection);

		command.Parameters.AddWithValue(orderId);

		Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
	}

	[Fact]
	public async Task ConcurrentShortageReports_OnlyOneSucceeds()
	{
		await using var scope = await PostgresTestScope.CreateAsync();

		var reservation = await new ReservationService(scope.Db).CreateAsync(
			new ReservationRequest(scope.ProductId, 2),
			scope.NewRequestKey());

		Assert.Equal(201, reservation.StatusCode);

		using var document = JsonDocument.Parse(reservation.Json);
		var orderId = document.RootElement.GetProperty("orderId").GetGuid();

		var service = new OrderShortageService(scope.Db);
		var keys = Enumerable.Range(0, 10)
			.Select(_ => scope.NewRequestKey())
			.ToArray();

		var results = await Task.WhenAll(
			keys.Select(key => service.ReportAsync(orderId, key)));

		Assert.Equal(1, results.Count(r => r.StatusCode == 200));
		Assert.Equal(9, results.Count(r => r.StatusCode == 409));

		await using var connection = await scope.Db.OpenConnectionAsync();
		await using var command = new NpgsqlCommand("""
            SELECT COUNT(*) FROM inventory_events
            WHERE order_id = $1 AND kind = 'SHORTAGE';
            """, connection);

		command.Parameters.AddWithValue(orderId);

		Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
	}

	[Fact]
	public async Task PickingShortageOrder_Returns409()
	{
		await using var scope = await PostgresTestScope.CreateAsync();

		var reservation = await new ReservationService(scope.Db).CreateAsync(
			new ReservationRequest(scope.ProductId, 2),
			scope.NewRequestKey());

		Assert.Equal(201, reservation.StatusCode);

		using var document = JsonDocument.Parse(reservation.Json);
		var orderId = document.RootElement.GetProperty("orderId").GetGuid();

		var shortage = await new OrderShortageService(scope.Db).ReportAsync(
			orderId, scope.NewRequestKey());

		Assert.Equal(200, shortage.StatusCode);

		var pick = await new OrderPickingService(scope.Db).PickAsync(
			orderId, scope.NewRequestKey());

		Assert.Equal(409, pick.StatusCode);
	}

	[Fact]
	public async Task ExpiredReservation_CannotReportShortage()
	{
		await using var scope = await PostgresTestScope.CreateAsync();

		var reservation = await new ReservationService(scope.Db).CreateAsync(
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

		var shortage = await new OrderShortageService(scope.Db).ReportAsync(
			orderId, scope.NewRequestKey());

		Assert.Equal(409, shortage.StatusCode);
	}
}
