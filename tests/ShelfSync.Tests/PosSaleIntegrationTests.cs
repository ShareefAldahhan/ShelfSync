
using System.Text.Json;
using Npgsql;
using ShelfSync.Api.Models;
using ShelfSync.Api.Services;
using Xunit;

namespace ShelfSync.Tests;

public sealed class PosSaleIntegrationTests
{
	[Fact]
	public async Task Sale_DeductsShelfStockAndRecordsEvent()
	{
		await using var scope = await PostgresTestScope.CreateAsync(shelfUnits: 10);

		var eventId = Guid.NewGuid();
		var result = await new PosSaleService(scope.Db).RecordAsync(
			new PosSaleRequest(eventId, scope.ProductId, 3));

		Assert.Equal(200, result.StatusCode);

		using var json = JsonDocument.Parse(result.Json);
		Assert.Equal("COMMITTED",
			json.RootElement.GetProperty("state").GetString());

		await using var connection = await scope.Db.OpenConnectionAsync();
		await using var command = new NpgsqlCommand("""
            SELECT i.shelf_units,
                   (SELECT COUNT(*) FROM pos_events
                    WHERE event_id = $1),
                   (SELECT COUNT(*) FROM inventory_events
                    WHERE pos_event_id = $1 AND kind = 'SALE'),
                   (SELECT shelf_delta FROM inventory_events
                    WHERE pos_event_id = $1 AND kind = 'SALE')
            FROM inventory i
            WHERE i.product_id = $2;
            """, connection);

		command.Parameters.AddWithValue(eventId);
		command.Parameters.AddWithValue(scope.ProductId);

		await using var reader = await command.ExecuteReaderAsync();

		Assert.True(await reader.ReadAsync());
		Assert.Equal(7, reader.GetInt32(0));
		Assert.Equal(1L, reader.GetInt64(1));
		Assert.Equal(1L, reader.GetInt64(2));
		Assert.Equal(-3, reader.GetInt32(3));
	}

	[Fact]
	public async Task RetriedSale_DoesNotDeductStockTwice()
	{
		await using var scope = await PostgresTestScope.CreateAsync();

		var service = new PosSaleService(scope.Db);
		var request = new PosSaleRequest(
			Guid.NewGuid(), scope.ProductId, 2);

		var first = await service.RecordAsync(request);
		var second = await service.RecordAsync(request);

		Assert.Equal(200, first.StatusCode);
		Assert.Equal(200, second.StatusCode);

		using var firstJson = JsonDocument.Parse(first.Json);
		using var secondJson = JsonDocument.Parse(second.Json);

		Assert.Equal(
			firstJson.RootElement.GetProperty("eventId").GetGuid(),
			secondJson.RootElement.GetProperty("eventId").GetGuid());

		await using var connection = await scope.Db.OpenConnectionAsync();
		await using var command = new NpgsqlCommand("""
            SELECT shelf_units FROM inventory
            WHERE product_id = $1;
            """, connection);

		command.Parameters.AddWithValue(scope.ProductId);

		Assert.Equal(8,
			(int)(await command.ExecuteScalarAsync())!);
	}

	[Fact]
	public async Task ReusedEventIdWithDifferentPayload_Returns409()
	{
		await using var scope = await PostgresTestScope.CreateAsync();

		var service = new PosSaleService(scope.Db);
		var eventId = Guid.NewGuid();

		var first = await service.RecordAsync(
			new PosSaleRequest(eventId, scope.ProductId, 2));

		var second = await service.RecordAsync(
			new PosSaleRequest(eventId, scope.ProductId, 3));

		Assert.Equal(200, first.StatusCode);
		Assert.Equal(409, second.StatusCode);
	}

	[Fact]
	public async Task SaleCannotConsumeStockHeldByReservation()
	{
		await using var scope = await PostgresTestScope.CreateAsync(shelfUnits: 10);

		var reservation = await new ReservationService(scope.Db).CreateAsync(
			new ReservationRequest(scope.ProductId, 7),
			scope.NewRequestKey());

		Assert.Equal(201, reservation.StatusCode);

		var sale = await new PosSaleService(scope.Db).RecordAsync(
			new PosSaleRequest(Guid.NewGuid(), scope.ProductId, 4));

		Assert.Equal(409, sale.StatusCode);

		await using var connection = await scope.Db.OpenConnectionAsync();
		await using var command = new NpgsqlCommand("""
            SELECT shelf_units FROM inventory
            WHERE product_id = $1;
            """, connection);

		command.Parameters.AddWithValue(scope.ProductId);

		Assert.Equal(10,
			(int)(await command.ExecuteScalarAsync())!);
	}

	[Fact]
	public async Task ConcurrentSales_CannotOversell()
	{
		await using var scope = await PostgresTestScope.CreateAsync(shelfUnits: 10);

		var service = new PosSaleService(scope.Db);

		var results = await Task.WhenAll(
			Enumerable.Range(0, 10).Select(_ =>
				service.RecordAsync(new PosSaleRequest(
					Guid.NewGuid(), scope.ProductId, 3))));

		Assert.Equal(3, results.Count(r => r.StatusCode == 200));
		Assert.Equal(7, results.Count(r => r.StatusCode == 409));

		await using var connection = await scope.Db.OpenConnectionAsync();
		await using var command = new NpgsqlCommand("""
            SELECT shelf_units FROM inventory
            WHERE product_id = $1;
            """, connection);

		command.Parameters.AddWithValue(scope.ProductId);

		Assert.Equal(1,
			(int)(await command.ExecuteScalarAsync())!);
	}

	[Fact]
	public async Task ConcurrentSaleAndReservation_DoNotOversell()
	{
		await using var scope = await PostgresTestScope.CreateAsync(shelfUnits: 10);

		var saleService = new PosSaleService(scope.Db);
		var reservationService = new ReservationService(scope.Db);

		var saleTask = saleService.RecordAsync(
			new PosSaleRequest(Guid.NewGuid(), scope.ProductId, 7));

		var reserveTask = reservationService.CreateAsync(
			new ReservationRequest(scope.ProductId, 7),
			scope.NewRequestKey());

		var results = await Task.WhenAll(saleTask, reserveTask);

		var statuses = results
			.Select(result => result.StatusCode)
			.ToArray();

		Assert.Single(statuses, status => status is 200 or 201);
		Assert.Single(statuses, status => status == 409);
	}


	[Fact]
	public async Task ConcurrentDuplicatePosEvents_DeductStockOnlyOnce()
	{
		await using var scope =
			await PostgresTestScope.CreateAsync(shelfUnits: 10);

		var service = new PosSaleService(scope.Db);

		// Every request represents the SAME physical sale.
		var request = new PosSaleRequest(
			Guid.NewGuid(), scope.ProductId, 3);

		var results = await Task.WhenAll(
			Enumerable.Range(0, 10)
				.Select(_ => service.RecordAsync(request)));

		// All callers should receive the committed result.
		Assert.All(results, result =>
			Assert.Equal(200, result.StatusCode));

		await using var connection =
			await scope.Db.OpenConnectionAsync();

		await using var command = new NpgsqlCommand("""
        SELECT i.shelf_units,
               (SELECT COUNT(*)
                FROM pos_events
                WHERE event_id = $1),
               (SELECT COUNT(*)
                FROM inventory_events
                WHERE pos_event_id = $1 AND kind = 'SALE')
        FROM inventory i
        WHERE product_id = $2;
        """, connection);

		command.Parameters.AddWithValue(request.EventId);
		command.Parameters.AddWithValue(scope.ProductId);

		await using var reader = await command.ExecuteReaderAsync();

		Assert.True(await reader.ReadAsync());
		Assert.Equal(7, reader.GetInt32(0));
		Assert.Equal(1L, reader.GetInt64(1));
		Assert.Equal(1L, reader.GetInt64(2));
	}


	[Fact]
	public async Task RejectedSale_CanSucceedAfterStockBecomesAvailable()
	{
		await using var scope =
			await PostgresTestScope.CreateAsync(shelfUnits: 2);

		var service = new PosSaleService(scope.Db);
		var request = new PosSaleRequest(
			Guid.NewGuid(), scope.ProductId, 5);

		// First attempt: not enough stock.
		var first = await service.RecordAsync(request);
		Assert.Equal(409, first.StatusCode);

		// Simulate receiving additional physical inventory.
		await using (var connection = await scope.Db.OpenConnectionAsync())
		{
			await using var command = new NpgsqlCommand("""
            UPDATE inventory
            SET shelf_units = shelf_units + 5,
                updated_at = clock_timestamp()
            WHERE product_id = $1;
            """, connection);

			command.Parameters.AddWithValue(scope.ProductId);
			await command.ExecuteNonQueryAsync();
		}

		// Same event ID should now succeed.
		var second = await service.RecordAsync(request);
		Assert.Equal(200, second.StatusCode);

		// A further retry must not deduct stock again.
		var third = await service.RecordAsync(request);
		Assert.Equal(200, third.StatusCode);

		await using var verifyConnection =
			await scope.Db.OpenConnectionAsync();

		await using var verify = new NpgsqlCommand("""
        SELECT shelf_units
        FROM inventory
        WHERE product_id = $1;
        """, verifyConnection);

		verify.Parameters.AddWithValue(scope.ProductId);

		Assert.Equal(2,
			(int)(await verify.ExecuteScalarAsync())!);
	}

}
