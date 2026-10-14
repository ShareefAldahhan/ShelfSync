
using System.Text.Json;
using Npgsql;
using ShelfSync.Api.Models;
using ShelfSync.Api.Services;

namespace ShelfSync.Tests;

public sealed class ReservationCancellationIntegrationTests
{
	[Fact]
	public async Task CancellingReservation_UpdatesStateAndCreatesOneEvent()
	{
		// Arrange: create an actual reservation in PostgreSQL.
		await using var scope = await PostgresTestScope.CreateAsync();

		var reservationService = new ReservationService(scope.Db);
		var cancellationService = new ReservationCancellationService(scope.Db);

		var reservation = await reservationService.CreateAsync(
			new ReservationRequest(scope.ProductId, 2),
			scope.NewRequestKey());

		Assert.Equal(201, reservation.StatusCode);

		using var document = JsonDocument.Parse(reservation.Json);
		var orderId = document.RootElement.GetProperty("orderId").GetGuid();

		// Act: cancel the reservation.
		var cancellation = await cancellationService.CancelAsync(
			orderId,
			scope.NewRequestKey());

		Assert.Equal(200, cancellation.StatusCode);

		using var cancellationDocument = JsonDocument.Parse(cancellation.Json);
		Assert.Equal(
			"CANCELLED",
			cancellationDocument.RootElement.GetProperty("state").GetString());

		// Assert: inspect committed database state.
		await using var connection = await scope.Db.OpenConnectionAsync();

		await using var command = new NpgsqlCommand("""
            SELECT
                o.state,
                i.shelf_units,
                (SELECT COUNT(*)
                 FROM inventory_events e
                 WHERE e.order_id = o.id
                   AND e.kind = 'CANCEL')
            FROM orders o
            JOIN inventory i ON i.product_id = o.product_id
            WHERE o.id = $1;
            """, connection);

		command.Parameters.AddWithValue(orderId);

		await using var reader = await command.ExecuteReaderAsync();

		Assert.True(await reader.ReadAsync());
		Assert.Equal("CANCELLED", reader.GetString(0));
		Assert.Equal(10, reader.GetInt32(1));
		Assert.Equal(1L, reader.GetInt64(2));
	}


	[Fact]
	public async Task RetryingCancellation_ReturnsSameResponseWithoutDuplicateEvents()
	{
		await using var scope = await PostgresTestScope.CreateAsync();

		var reservationService = new ReservationService(scope.Db);
		var cancellationService = new ReservationCancellationService(scope.Db);

		var reservation = await reservationService.CreateAsync(
			new ReservationRequest(scope.ProductId, 2),
			scope.NewRequestKey());

		Assert.Equal(201, reservation.StatusCode);

		using var document = JsonDocument.Parse(reservation.Json);
		var orderId = document.RootElement.GetProperty("orderId").GetGuid();

		// Same key represents the same logical cancellation attempt.
		var cancellationKey = scope.NewRequestKey();

		var first = await cancellationService.CancelAsync(
			orderId, cancellationKey);

		var second = await cancellationService.CancelAsync(
			orderId, cancellationKey);

		Assert.Equal(200, first.StatusCode);
		Assert.Equal(first.StatusCode, second.StatusCode);

		using var firstDocument = JsonDocument.Parse(first.Json);
		using var secondDocument = JsonDocument.Parse(second.Json);

		Assert.True(JsonElement.DeepEquals(
			firstDocument.RootElement,
			secondDocument.RootElement));

		await using var connection = await scope.Db.OpenConnectionAsync();

		await using var command = new NpgsqlCommand("""
        SELECT COUNT(*)
        FROM inventory_events
        WHERE order_id = $1
          AND kind = 'CANCEL';
        """, connection);

		command.Parameters.AddWithValue(orderId);

		Assert.Equal(
			1L,
			(long)(await command.ExecuteScalarAsync())!);
	}

	[Fact]
	public async Task ReusingCancellationKeyForDifferentOrder_Returns409()
	{
		await using var scope = await PostgresTestScope.CreateAsync();

		var reservationService = new ReservationService(scope.Db);
		var cancellationService = new ReservationCancellationService(scope.Db);

		// Create two different reservations for the same product.
		var firstReservation = await reservationService.CreateAsync(
			new ReservationRequest(scope.ProductId, 2),
			scope.NewRequestKey());

		var secondReservation = await reservationService.CreateAsync(
			new ReservationRequest(scope.ProductId, 3),
			scope.NewRequestKey());

		Assert.Equal(201, firstReservation.StatusCode);
		Assert.Equal(201, secondReservation.StatusCode);

		using var firstDocument = JsonDocument.Parse(firstReservation.Json);
		using var secondDocument = JsonDocument.Parse(secondReservation.Json);

		var firstOrderId =
			firstDocument.RootElement.GetProperty("orderId").GetGuid();

		var secondOrderId =
			secondDocument.RootElement.GetProperty("orderId").GetGuid();

		var cancellationKey = scope.NewRequestKey();

		// First cancellation succeeds.
		var first = await cancellationService.CancelAsync(
			firstOrderId, cancellationKey);

		// Same key, different order: reject.
		var second = await cancellationService.CancelAsync(
			secondOrderId, cancellationKey);

		Assert.Equal(200, first.StatusCode);
		Assert.Equal(409, second.StatusCode);
		Assert.Contains(
			"Idempotency key reused with different data.",
			second.Json);

		// Confirm that only the first order was cancelled.
		await using var connection = await scope.Db.OpenConnectionAsync();

		await using var command = new NpgsqlCommand("""
        SELECT
            (SELECT state FROM orders WHERE id = $1),
            (SELECT state FROM orders WHERE id = $2),
            (SELECT COUNT(*) FROM inventory_events
             WHERE order_id = $2 AND kind = 'CANCEL');
        """, connection);

		command.Parameters.AddWithValue(firstOrderId);
		command.Parameters.AddWithValue(secondOrderId);

		await using var reader = await command.ExecuteReaderAsync();

		Assert.True(await reader.ReadAsync());
		Assert.Equal("CANCELLED", reader.GetString(0));
		Assert.Equal("RESERVED", reader.GetString(1));
		Assert.Equal(0L, reader.GetInt64(2));
	}

	[Fact]
	public async Task ConcurrentCancellations_OnlyOneSucceeds()
	{
		await using var scope = await PostgresTestScope.CreateAsync();

		var reservationService = new ReservationService(scope.Db);
		var cancellationService = new ReservationCancellationService(scope.Db);

		var reservation = await reservationService.CreateAsync(
			new ReservationRequest(scope.ProductId, 2),
			scope.NewRequestKey());

		Assert.Equal(201, reservation.StatusCode);

		using var document = JsonDocument.Parse(reservation.Json);
		var orderId = document.RootElement.GetProperty("orderId").GetGuid();

		// Different keys represent two independent cancellation attempts.
		var keys = Enumerable.Range(0, 10)
			.Select(_ => scope.NewRequestKey())
			.ToArray();

		// Launch all ten attempts concurrently.
		var results = await Task.WhenAll(
			keys.Select(key =>
				cancellationService.CancelAsync(orderId, key)));

		Assert.Equal(1, results.Count(r => r.StatusCode == 200));
		Assert.Equal(9, results.Count(r => r.StatusCode == 409));

		// Verify the actual committed database state.
		await using var connection = await scope.Db.OpenConnectionAsync();

		await using var command = new NpgsqlCommand("""
        SELECT
            o.state,
            (SELECT COUNT(*)
             FROM inventory_events e
             WHERE e.order_id = o.id
               AND e.kind = 'CANCEL')
        FROM orders o
        WHERE o.id = $1;
        """, connection);

		command.Parameters.AddWithValue(orderId);

		await using var reader = await command.ExecuteReaderAsync();

		Assert.True(await reader.ReadAsync());
		Assert.Equal("CANCELLED", reader.GetString(0));
		Assert.Equal(1L, reader.GetInt64(1));
	}

}
