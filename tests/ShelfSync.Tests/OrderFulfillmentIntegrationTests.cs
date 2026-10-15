
using System.Text.Json;
using Npgsql;
using ShelfSync.Api.Models;
using ShelfSync.Api.Services;
using Xunit;

namespace ShelfSync.Tests;

public sealed class OrderFulfillmentIntegrationTests
{
	private static async Task<Guid> CreateOrderAsync(
		PostgresTestScope scope,
		int quantity = 3,
		bool pick = true)
	{
		var reservation = await new ReservationService(scope.Db).CreateAsync(
			new ReservationRequest(scope.ProductId, quantity),
			scope.NewRequestKey());

		Assert.Equal(201, reservation.StatusCode);

		using var document = JsonDocument.Parse(reservation.Json);
		var orderId = document.RootElement.GetProperty("orderId").GetGuid();

		if (pick)
		{
			var picked = await new OrderPickingService(scope.Db).PickAsync(
				orderId, scope.NewRequestKey());

			Assert.Equal(200, picked.StatusCode);
		}

		return orderId;
	}

	[Fact]
	public async Task FulfillingPickedOrder_UpdatesInventoryAndAudit()
	{
		await using var scope = await PostgresTestScope.CreateAsync(shelfUnits: 10);
		var orderId = await CreateOrderAsync(scope, quantity: 3);

		var result = await new OrderFulfillmentService(scope.Db)
			.FulfillAsync(orderId, scope.NewRequestKey());

		Assert.Equal(200, result.StatusCode);

		using var document = JsonDocument.Parse(result.Json);
		Assert.Equal("FULFILLED",
			document.RootElement.GetProperty("state").GetString());

		await using var connection = await scope.Db.OpenConnectionAsync();
		await using var command = new NpgsqlCommand("""
            SELECT o.state, i.shelf_units, i.picked_units,
                   (SELECT COUNT(*)
                    FROM inventory_events e
                    WHERE e.order_id = o.id AND e.kind = 'FULFILL'),
                   (SELECT picked_delta
                    FROM inventory_events e
                    WHERE e.order_id = o.id AND e.kind = 'FULFILL')
            FROM orders o
            JOIN inventory i ON i.product_id = o.product_id
            WHERE o.id = $1;
            """, connection);

		command.Parameters.AddWithValue(orderId);

		await using var reader = await command.ExecuteReaderAsync();

		Assert.True(await reader.ReadAsync());
		Assert.Equal("FULFILLED", reader.GetString(0));
		Assert.Equal(7, reader.GetInt32(1));
		Assert.Equal(0, reader.GetInt32(2));
		Assert.Equal(1L, reader.GetInt64(3));
		Assert.Equal(-3, reader.GetInt32(4));
	}

	[Fact]
	public async Task RetryingFulfillment_DoesNotDeductInventoryTwice()
	{
		await using var scope = await PostgresTestScope.CreateAsync();
		var orderId = await CreateOrderAsync(scope, quantity: 2);

		var service = new OrderFulfillmentService(scope.Db);
		var key = scope.NewRequestKey();

		var first = await service.FulfillAsync(orderId, key);
		var second = await service.FulfillAsync(orderId, key);

		Assert.Equal(200, first.StatusCode);
		Assert.Equal(200, second.StatusCode);

		using var firstJson = JsonDocument.Parse(first.Json);
		using var secondJson = JsonDocument.Parse(second.Json);

		Assert.Equal(
			firstJson.RootElement.GetProperty("orderId").GetGuid(),
			secondJson.RootElement.GetProperty("orderId").GetGuid());

		await using var connection = await scope.Db.OpenConnectionAsync();
		await using var command = new NpgsqlCommand("""
            SELECT i.picked_units,
                   (SELECT COUNT(*) FROM inventory_events
                    WHERE order_id = $1 AND kind = 'FULFILL')
            FROM inventory i
            WHERE i.product_id = $2;
            """, connection);

		command.Parameters.AddWithValue(orderId);
		command.Parameters.AddWithValue(scope.ProductId);

		await using var reader = await command.ExecuteReaderAsync();
		Assert.True(await reader.ReadAsync());
		Assert.Equal(0, reader.GetInt32(0));
		Assert.Equal(1L, reader.GetInt64(1));
	}

	[Fact]
	public async Task ReservedOrder_CannotBeFulfilled()
	{
		await using var scope = await PostgresTestScope.CreateAsync();
		var orderId = await CreateOrderAsync(scope, pick: false);

		var result = await new OrderFulfillmentService(scope.Db)
			.FulfillAsync(orderId, scope.NewRequestKey());

		Assert.Equal(409, result.StatusCode);
	}

	[Fact]
	public async Task FulfillmentKeyReusedForDifferentOrder_Returns409()
	{
		await using var scope = await PostgresTestScope.CreateAsync();
		var firstOrder = await CreateOrderAsync(scope, quantity: 2);
		var secondOrder = await CreateOrderAsync(scope, quantity: 2);

		var service = new OrderFulfillmentService(scope.Db);
		var key = scope.NewRequestKey();

		var first = await service.FulfillAsync(firstOrder, key);
		var second = await service.FulfillAsync(secondOrder, key);

		Assert.Equal(200, first.StatusCode);
		Assert.Equal(409, second.StatusCode);
	}

	[Fact]
	public async Task ConcurrentFulfillments_OnlyOneSucceeds()
	{
		await using var scope = await PostgresTestScope.CreateAsync();
		var orderId = await CreateOrderAsync(scope, quantity: 3);

		var service = new OrderFulfillmentService(scope.Db);

		var keys = Enumerable.Range(0, 10)
			.Select(_ => scope.NewRequestKey())
			.ToArray();

		var results = await Task.WhenAll(
			keys.Select(key => service.FulfillAsync(orderId, key)));

		Assert.Equal(1, results.Count(r => r.StatusCode == 200));
		Assert.Equal(9, results.Count(r => r.StatusCode == 409));

		await using var connection = await scope.Db.OpenConnectionAsync();
		await using var command = new NpgsqlCommand("""
            SELECT i.shelf_units, i.picked_units,
                   (SELECT COUNT(*) FROM inventory_events
                    WHERE order_id = $1 AND kind = 'FULFILL')
            FROM inventory i
            WHERE product_id = $2;
            """, connection);

		command.Parameters.AddWithValue(orderId);
		command.Parameters.AddWithValue(scope.ProductId);

		await using var reader = await command.ExecuteReaderAsync();

		Assert.True(await reader.ReadAsync());
		Assert.Equal(7, reader.GetInt32(0));
		Assert.Equal(0, reader.GetInt32(1));
		Assert.Equal(1L, reader.GetInt64(2));
	}
}
