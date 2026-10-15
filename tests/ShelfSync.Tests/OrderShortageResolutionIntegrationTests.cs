
using System.Text.Json;
using Npgsql;
using ShelfSync.Api.Models;
using ShelfSync.Api.Services;
using Xunit;

namespace ShelfSync.Tests;

public sealed class OrderShortageResolutionIntegrationTests
{
	private static async Task<Guid> CreateShortageAsync(
		PostgresTestScope scope,
		int quantity = 2)
	{
		var reservation = await new ReservationService(scope.Db).CreateAsync(
			new ReservationRequest(scope.ProductId, quantity),
			scope.NewRequestKey());

		Assert.Equal(201, reservation.StatusCode);

		using var document = JsonDocument.Parse(reservation.Json);
		var orderId = document.RootElement.GetProperty("orderId").GetGuid();

		var shortage = await new OrderShortageService(scope.Db).ReportAsync(
			orderId, scope.NewRequestKey());

		Assert.Equal(200, shortage.StatusCode);

		return orderId;
	}

	[Fact]
	public async Task ResolvingShortage_RestoresReservationAndAllowsPicking()
	{
		await using var scope = await PostgresTestScope.CreateAsync();
		var orderId = await CreateShortageAsync(scope, 3);

		var result = await new OrderShortageResolutionService(scope.Db)
			.ResolveAsync(orderId, scope.NewRequestKey());

		Assert.Equal(200, result.StatusCode);

		using var document = JsonDocument.Parse(result.Json);
		Assert.Equal("RESERVED",
			document.RootElement.GetProperty("state").GetString());

		await using var connection = await scope.Db.OpenConnectionAsync();
		await using (var command = new NpgsqlCommand("""
            SELECT state, reason, expires_at,
                   (SELECT COUNT(*) FROM inventory_events
                    WHERE order_id = $1 AND kind = 'RECONCILE')
            FROM orders WHERE id = $1;
            """, connection))
		{
			command.Parameters.AddWithValue(orderId);

			await using var reader = await command.ExecuteReaderAsync();
			Assert.True(await reader.ReadAsync());
			Assert.Equal("RESERVED", reader.GetString(0));
			Assert.True(reader.IsDBNull(1));
			Assert.True(reader.GetDateTime(2) > DateTime.UtcNow);
			Assert.Equal(1L, reader.GetInt64(3));
		}

		var pick = await new OrderPickingService(scope.Db).PickAsync(
			orderId, scope.NewRequestKey());

		Assert.Equal(200, pick.StatusCode);
	}

	[Fact]
	public async Task InsufficientStock_CannotResolveShortage()
	{
		await using var scope = await PostgresTestScope.CreateAsync(shelfUnits: 5);
		var orderId = await CreateShortageAsync(scope, 4);

		// Another order claims the stock released by the shortage hold.
		var competing = await new ReservationService(scope.Db).CreateAsync(
			new ReservationRequest(scope.ProductId, 3),
			scope.NewRequestKey());

		Assert.Equal(201, competing.StatusCode);

		var result = await new OrderShortageResolutionService(scope.Db)
			.ResolveAsync(orderId, scope.NewRequestKey());

		Assert.Equal(409, result.StatusCode);

		await using var connection = await scope.Db.OpenConnectionAsync();
		await using var command = new NpgsqlCommand("""
            SELECT state FROM orders WHERE id = $1;
            """, connection);

		command.Parameters.AddWithValue(orderId);
		Assert.Equal("SHORTAGE_PENDING",
			(string?)await command.ExecuteScalarAsync());
	}

	[Fact]
	public async Task QuarantinedInventory_CannotResolveShortage()
	{
		await using var scope = await PostgresTestScope.CreateAsync();
		var orderId = await CreateShortageAsync(scope);

		await using (var connection = await scope.Db.OpenConnectionAsync())
		{
			await using var command = new NpgsqlCommand("""
				UPDATE inventory
				SET quarantined = TRUE,
					quarantine_reason = 'PHYSICAL_STOCK_DISCREPANCY'
				WHERE product_id = $1;
				""", connection);

			command.Parameters.AddWithValue(scope.ProductId);
			await command.ExecuteNonQueryAsync();
		}

		var result = await new OrderShortageResolutionService(scope.Db)
			.ResolveAsync(orderId, scope.NewRequestKey());

		Assert.Equal(409, result.StatusCode);
	}

	[Fact]
	public async Task RetryingResolution_DoesNotCreateDuplicateEvents()
	{
		await using var scope = await PostgresTestScope.CreateAsync();
		var orderId = await CreateShortageAsync(scope);

		var service = new OrderShortageResolutionService(scope.Db);
		var key = scope.NewRequestKey();

		var first = await service.ResolveAsync(orderId, key);
		var second = await service.ResolveAsync(orderId, key);

		Assert.Equal(200, first.StatusCode);
		Assert.Equal(200, second.StatusCode);

		using var firstJson = JsonDocument.Parse(first.Json);
		using var secondJson = JsonDocument.Parse(second.Json);

		Assert.Equal(
			firstJson.RootElement.GetProperty("orderId").GetGuid(),
			secondJson.RootElement.GetProperty("orderId").GetGuid());

		Assert.Equal(
			firstJson.RootElement.GetProperty("expiresAt").GetDateTime(),
			secondJson.RootElement.GetProperty("expiresAt").GetDateTime());

		await using var connection = await scope.Db.OpenConnectionAsync();
		await using var command = new NpgsqlCommand("""
            SELECT COUNT(*) FROM inventory_events
            WHERE order_id = $1 AND kind = 'RECONCILE';
            """, connection);

		command.Parameters.AddWithValue(orderId);
		Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
	}

	[Fact]
	public async Task ConcurrentResolutions_OnlyOneSucceeds()
	{
		await using var scope = await PostgresTestScope.CreateAsync();
		var orderId = await CreateShortageAsync(scope);

		var service = new OrderShortageResolutionService(scope.Db);
		var keys = Enumerable.Range(0, 10)
			.Select(_ => scope.NewRequestKey())
			.ToArray();

		var results = await Task.WhenAll(
			keys.Select(key => service.ResolveAsync(orderId, key)));

		Assert.Equal(1, results.Count(r => r.StatusCode == 200));
		Assert.Equal(9, results.Count(r => r.StatusCode == 409));

		await using var connection = await scope.Db.OpenConnectionAsync();
		await using var command = new NpgsqlCommand("""
            SELECT COUNT(*) FROM inventory_events
            WHERE order_id = $1 AND kind = 'RECONCILE';
            """, connection);

		command.Parameters.AddWithValue(orderId);
		Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
	}
}
