
using System.Text.Json;
using Npgsql;
using ShelfSync.Api.Models;
using ShelfSync.Api.Services;

namespace ShelfSync.Tests;

public sealed class ReservationIntegrationTests
{

	[Fact]
	public async Task ExpiredReservation_TransitionsOnce()
	{
		var connectionString = Environment.GetEnvironmentVariable(
			"SHELFSYNC_TEST_CONNECTION");

		Assert.False(
			string.IsNullOrWhiteSpace(connectionString),
			"SHELFSYNC_TEST_CONNECTION must be configured.");

		var builder = new NpgsqlConnectionStringBuilder(connectionString);
		Assert.Equal("shelfsync_test", builder.Database);

		await using var db = NpgsqlDataSource.Create(builder.ConnectionString);

		var productId = Guid.NewGuid();
		var requestKey = Guid.NewGuid();

		try
		{
			// Create a dedicated test product and inventory.
			await using (var connection = await db.OpenConnectionAsync())
			{
				const string productSql = """
                INSERT INTO products (id, sku, name, price_cents)
                VALUES ($1, $2, 'Expiration Test Product', 1000);
                """;

				await using (var command =
					new NpgsqlCommand(productSql, connection))
				{
					command.Parameters.AddWithValue(productId);
					command.Parameters.AddWithValue($"TEST-{productId:N}");
					await command.ExecuteNonQueryAsync();
				}

				const string inventorySql = """
                INSERT INTO inventory
                    (product_id, shelf_units, picked_units,
                     safety_buffer, quarantined)
                VALUES ($1, 10, 0, 0, FALSE);
                """;

				await using (var command =
					new NpgsqlCommand(inventorySql, connection))
				{
					command.Parameters.AddWithValue(productId);
					await command.ExecuteNonQueryAsync();
				}
			}

			var reservationService = new ReservationService(db);

			var result = await reservationService.CreateAsync(
				new ReservationRequest(productId, 2),
				requestKey);

			Assert.Equal(201, result.StatusCode);

			using var document = JsonDocument.Parse(result.Json);
			var orderId = document.RootElement.GetProperty("orderId").GetGuid();

			// Make the reservation overdue.
			await using (var connection = await db.OpenConnectionAsync())
			{
				const string expireSql = """
                UPDATE orders
                SET expires_at = clock_timestamp() - INTERVAL '1 second'
                WHERE id = $1
                  AND state = 'RESERVED'
                  AND created_at < clock_timestamp() - INTERVAL '2 seconds';
                """;

				// Ensure created_at remains earlier than expires_at.
				await Task.Delay(TimeSpan.FromSeconds(3));

				await using var command =
					new NpgsqlCommand(expireSql, connection);

				command.Parameters.AddWithValue(orderId);
				Assert.Equal(1, await command.ExecuteNonQueryAsync());
			}

			var expirationService = new ReservationExpirationService(db);

			// First sweep should expire our reservation.
			var firstCount = await expirationService.ExpireBatchAsync();

			// Second sweep must not expire it again.
			var secondCount = await expirationService.ExpireBatchAsync();

			Assert.True(firstCount >= 1);
			Assert.Equal(0, secondCount);

			await using (var connection = await db.OpenConnectionAsync())
			{
				const string verifySql = """
                SELECT
                    o.state,
                    (SELECT COUNT(*)
                     FROM inventory_events e
                     WHERE e.order_id = o.id AND e.kind = 'EXPIRE')
                FROM orders o
                WHERE o.id = $1;
                """;

				await using var command =
					new NpgsqlCommand(verifySql, connection);

				command.Parameters.AddWithValue(orderId);

				await using var reader = await command.ExecuteReaderAsync();

				Assert.True(await reader.ReadAsync());
				Assert.Equal("EXPIRED", reader.GetString(0));
				Assert.Equal(1L, reader.GetInt64(1));
			}
		}
		finally
		{
			await using var connection = await db.OpenConnectionAsync();
			await using var transaction =
				await connection.BeginTransactionAsync();

			string[] cleanupStatements =
			[
				"DELETE FROM operation_requests WHERE operation = 'RESERVE' AND request_key = $1;",
			"DELETE FROM inventory_events WHERE product_id = $1;",
			"DELETE FROM orders WHERE product_id = $1;",
			"DELETE FROM inventory WHERE product_id = $1;",
			"DELETE FROM products WHERE id = $1;"
			];

			for (var i = 0; i < cleanupStatements.Length; i++)
			{
				await using var command = new NpgsqlCommand(
					cleanupStatements[i], connection, transaction);

				command.Parameters.AddWithValue(
					i == 0 ? requestKey : productId);

				await command.ExecuteNonQueryAsync();
			}

			await transaction.CommitAsync();
		}
	}

	[Fact]
	public async Task ConcurrentReservations_NeverOversell()
	{
		var connectionString = Environment.GetEnvironmentVariable(
			"SHELFSYNC_TEST_CONNECTION");

		Assert.False(string.IsNullOrWhiteSpace(connectionString));

		var builder = new NpgsqlConnectionStringBuilder(connectionString);
		Assert.Equal("shelfsync_test", builder.Database);

		await using var db = NpgsqlDataSource.Create(builder.ConnectionString);

		var productId = Guid.NewGuid();
		var requestKeys = Enumerable.Range(0, 10)
			.Select(_ => Guid.NewGuid())
			.ToArray();

		try
		{
			await using (var connection = await db.OpenConnectionAsync())
			{
				const string productSql = """
                INSERT INTO products (id, sku, name, price_cents)
                VALUES ($1, $2, 'Concurrency Test Product', 1000);
                """;

				await using (var command =
					new NpgsqlCommand(productSql, connection))
				{
					command.Parameters.AddWithValue(productId);
					command.Parameters.AddWithValue($"TEST-{productId:N}");
					await command.ExecuteNonQueryAsync();
				}

				const string inventorySql = """
                INSERT INTO inventory
                    (product_id, shelf_units, picked_units,
                     safety_buffer, quarantined)
                VALUES ($1, 6, 0, 0, FALSE);
                """;

				await using (var command =
					new NpgsqlCommand(inventorySql, connection))
				{
					command.Parameters.AddWithValue(productId);
					await command.ExecuteNonQueryAsync();
				}
			}

			var service = new ReservationService(db);
			var request = new ReservationRequest(productId, 1);

			// Launch ten independent reservation attempts.
			var tasks = requestKeys.Select(
				key => service.CreateAsync(request, key));

			var results = await Task.WhenAll(tasks);

			Assert.Equal(6, results.Count(r => r.StatusCode == 201));
			Assert.Equal(4, results.Count(r => r.StatusCode == 409));

			await using var verifyConnection = await db.OpenConnectionAsync();

			const string verifySql = """
            SELECT
                (SELECT COALESCE(SUM(quantity), 0)
                 FROM orders
                 WHERE product_id = $1 AND state = 'RESERVED'),
                (SELECT COUNT(*)
                 FROM inventory_events
                 WHERE product_id = $1 AND kind = 'RESERVE');
            """;

			await using var verifyCommand =
				new NpgsqlCommand(verifySql, verifyConnection);

			verifyCommand.Parameters.AddWithValue(productId);

			await using var reader =
				await verifyCommand.ExecuteReaderAsync();

			Assert.True(await reader.ReadAsync());
			Assert.Equal(6L, reader.GetInt64(0));
			Assert.Equal(6L, reader.GetInt64(1));
		}
		finally
		{
			await using var connection = await db.OpenConnectionAsync();
			await using var transaction =
				await connection.BeginTransactionAsync();

			// Remove all ten idempotency records first.
			const string keysSql = """
            DELETE FROM operation_requests
            WHERE operation = 'RESERVE'
              AND request_key = ANY($1);
            """;

			await using (var command =
				new NpgsqlCommand(keysSql, connection, transaction))
			{
				command.Parameters.AddWithValue(requestKeys);
				await command.ExecuteNonQueryAsync();
			}

			string[] cleanupStatements =
			[
				"DELETE FROM inventory_events WHERE product_id = $1;",
			"DELETE FROM orders WHERE product_id = $1;",
			"DELETE FROM inventory WHERE product_id = $1;",
			"DELETE FROM products WHERE id = $1;"
			];

			foreach (var sql in cleanupStatements)
			{
				await using var command =
					new NpgsqlCommand(sql, connection, transaction);

				command.Parameters.AddWithValue(productId);
				await command.ExecuteNonQueryAsync();
			}

			await transaction.CommitAsync();
		}
	}


	[Fact]
	public async Task ReusingKeyWithDifferentQuantity_Returns409()
	{
		var connectionString = Environment.GetEnvironmentVariable(
			"SHELFSYNC_TEST_CONNECTION");

		Assert.False(string.IsNullOrWhiteSpace(connectionString));

		var builder = new NpgsqlConnectionStringBuilder(connectionString);
		Assert.Equal("shelfsync_test", builder.Database);

		await using var db = NpgsqlDataSource.Create(builder.ConnectionString);

		var productId = Guid.NewGuid();
		var requestKey = Guid.NewGuid();

		try
		{
			await using (var connection = await db.OpenConnectionAsync())
			{
				const string productSql = """
                INSERT INTO products (id, sku, name, price_cents)
                VALUES ($1, $2, 'Idempotency Conflict Test', 1000);
                """;

				await using (var command = new NpgsqlCommand(productSql, connection))
				{
					command.Parameters.AddWithValue(productId);
					command.Parameters.AddWithValue($"TEST-{productId:N}");
					await command.ExecuteNonQueryAsync();
				}

				const string inventorySql = """
                INSERT INTO inventory
                    (product_id, shelf_units, picked_units,
                     safety_buffer, quarantined)
                VALUES ($1, 10, 0, 0, FALSE);
                """;

				await using (var command = new NpgsqlCommand(inventorySql, connection))
				{
					command.Parameters.AddWithValue(productId);
					await command.ExecuteNonQueryAsync();
				}
			}

			var service = new ReservationService(db);

			var first = await service.CreateAsync(
				new ReservationRequest(productId, 2),
				requestKey);

			var second = await service.CreateAsync(
				new ReservationRequest(productId, 3),
				requestKey);

			Assert.Equal(201, first.StatusCode);
			Assert.Equal(409, second.StatusCode);
			Assert.Contains(
				"Idempotency key reused with different data.",
				second.Json);

			await using var verifyConnection = await db.OpenConnectionAsync();

			const string countSql = """
            SELECT COUNT(*)
            FROM orders
            WHERE product_id = $1;
            """;

			await using var countCommand =
				new NpgsqlCommand(countSql, verifyConnection);

			countCommand.Parameters.AddWithValue(productId);

			var orderCount = (long)(await countCommand.ExecuteScalarAsync())!;

			Assert.Equal(1L, orderCount);
		}
		finally
		{
			await using var connection = await db.OpenConnectionAsync();
			await using var transaction =
				await connection.BeginTransactionAsync();

			string[] cleanupStatements =
			[
				"DELETE FROM operation_requests WHERE operation = 'RESERVE' AND request_key = $1;",
			"DELETE FROM inventory_events WHERE product_id = $1;",
			"DELETE FROM orders WHERE product_id = $1;",
			"DELETE FROM inventory WHERE product_id = $1;",
			"DELETE FROM products WHERE id = $1;"
			];

			for (var i = 0; i < cleanupStatements.Length; i++)
			{
				await using var command = new NpgsqlCommand(
					cleanupStatements[i], connection, transaction);

				command.Parameters.AddWithValue(
					i == 0 ? requestKey : productId);

				await command.ExecuteNonQueryAsync();
			}

			await transaction.CommitAsync();
		}
	}

	[Fact]
	public async Task RetryingReservation_ReturnsSameOrder()
	{
		var connectionString = Environment.GetEnvironmentVariable(
			"SHELFSYNC_TEST_CONNECTION");

		Assert.False(
			string.IsNullOrWhiteSpace(connectionString),
			"SHELFSYNC_TEST_CONNECTION must be configured.");

		var builder = new NpgsqlConnectionStringBuilder(connectionString);

		Assert.Equal("shelfsync_test", builder.Database);

		await using var db = NpgsqlDataSource.Create(
			builder.ConnectionString);

		var productId = Guid.NewGuid();
		var requestKey = Guid.NewGuid();

		try
		{
			// ARRANGE: Create a test product.
			await using (var connection = await db.OpenConnectionAsync())
			{
				const string productSql = """
                    INSERT INTO products (id, sku, name, price_cents)
                    VALUES ($1, $2, 'Integration Test Product', 1000);
                    """;

				await using (var command =
					new NpgsqlCommand(productSql, connection))
				{
					command.Parameters.AddWithValue(productId);
					command.Parameters.AddWithValue($"TEST-{productId:N}");

					await command.ExecuteNonQueryAsync();
				}

				// Create inventory with 10 available units.
				const string inventorySql = """
                    INSERT INTO inventory
                        (product_id, shelf_units, picked_units,
                         safety_buffer, quarantined)
                    VALUES ($1, 10, 0, 0, FALSE);
                    """;

				await using (var command =
					new NpgsqlCommand(inventorySql, connection))
				{
					command.Parameters.AddWithValue(productId);

					await command.ExecuteNonQueryAsync();
				}
			}

			var service = new ReservationService(db);
			var request = new ReservationRequest(productId, 2);

			// ACT: Submit the same request twice.
			var first = await service.CreateAsync(request, requestKey);
			var second = await service.CreateAsync(request, requestKey);

			// ASSERT: Both responses must be identical.
			Assert.Equal(201, first.StatusCode);
			Assert.Equal(first.StatusCode, second.StatusCode);

			using var firstDocument = JsonDocument.Parse(first.Json);
			using var secondDocument = JsonDocument.Parse(second.Json);

			Assert.True(
				JsonElement.DeepEquals(
					firstDocument.RootElement,
					secondDocument.RootElement),
				"The retried reservation must return the same JSON data.");

			using var document = JsonDocument.Parse(first.Json);

			var orderId = document.RootElement
				.GetProperty("orderId")
				.GetGuid();

			// Verify PostgreSQL contains exactly one order
			// and one RESERVE audit event.
			await using (var connection = await db.OpenConnectionAsync())
			{
				const string verifySql = """
                    SELECT
                        (SELECT COUNT(*) FROM orders
                         WHERE product_id = $1),
                        (SELECT COUNT(*) FROM inventory_events
                         WHERE order_id = $2 AND kind = 'RESERVE');
                    """;

				await using var command =
					new NpgsqlCommand(verifySql, connection);

				command.Parameters.AddWithValue(productId);
				command.Parameters.AddWithValue(orderId);

				await using var reader =
					await command.ExecuteReaderAsync();

				Assert.True(await reader.ReadAsync());
				Assert.Equal(1L, reader.GetInt64(0));
				Assert.Equal(1L, reader.GetInt64(1));
			}
		}
		finally
		{
			// CLEANUP: Delete only records belonging to this test.
			// A transaction ensures cleanup is atomic.
			await using var connection = await db.OpenConnectionAsync();
			await using var transaction =
				await connection.BeginTransactionAsync();

			string[] cleanupStatements =
			[
				"""
                DELETE FROM operation_requests
                WHERE operation = 'RESERVE'
                  AND request_key = $1;
                """,
				"""
                DELETE FROM inventory_events
                WHERE product_id = $1;
                """,
				"""
                DELETE FROM orders
                WHERE product_id = $1;
                """,
				"""
                DELETE FROM inventory
                WHERE product_id = $1;
                """,
				"""
                DELETE FROM products
                WHERE id = $1;
                """
			];

			for (var i = 0; i < cleanupStatements.Length; i++)
			{
				await using var command = new NpgsqlCommand(
					cleanupStatements[i],
					connection,
					transaction);

				command.Parameters.AddWithValue(
					i == 0 ? requestKey : productId);

				await command.ExecuteNonQueryAsync();
			}

			await transaction.CommitAsync();
		}
	}
}
