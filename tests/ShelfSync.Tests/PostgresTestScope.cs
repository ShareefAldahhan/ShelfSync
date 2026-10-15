using Npgsql;

namespace ShelfSync.Tests;

/// <summary>Owns only the product, request keys and failure objects created by one test.</summary>
internal sealed class PostgresTestScope : IAsyncDisposable
{
    private readonly List<Guid> requestKeys = [];
    private string? failureObjectName;

    private PostgresTestScope(NpgsqlDataSource db)
    {
        Db = db;
    }

    public NpgsqlDataSource Db { get; }
    public Guid ProductId { get; } = Guid.NewGuid();

    public static async Task<PostgresTestScope> CreateAsync(int shelfUnits = 10)
    {
        var connectionString = Environment.GetEnvironmentVariable("SHELFSYNC_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("SHELFSYNC_TEST_CONNECTION must be configured for database integration tests.");

        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            // Do not attach the original exception: malformed credentials must not reach test logs.
            throw new InvalidOperationException("SHELFSYNC_TEST_CONNECTION is not a valid PostgreSQL connection configuration.");
        }

        if (!string.Equals(builder.Database, "shelfsync_test", StringComparison.Ordinal))
            throw new InvalidOperationException("Database integration tests require the database name to be exactly shelfsync_test.");

        builder.IncludeErrorDetail = false;
        builder.LogParameters = false;
        var scope = new PostgresTestScope(NpgsqlDataSource.Create(builder.ConnectionString));
        try
        {
            await using var connection = await scope.Db.OpenConnectionAsync();
            // Confirm the actual connection too, before any test data or DDL is written.
            await using (var check = new NpgsqlCommand("SELECT current_database();", connection))
            {
                if (!string.Equals((string?)await check.ExecuteScalarAsync(), "shelfsync_test", StringComparison.Ordinal))
                    throw new InvalidOperationException("Connected database must be exactly shelfsync_test.");
            }

            await using var transaction = await connection.BeginTransactionAsync();
            await using (var product = new NpgsqlCommand("""
                INSERT INTO products (id, sku, name, price_cents)
                VALUES ($1, $2, 'Reservation Integration Test Product', 1000);
                """, connection, transaction))
            {
                product.Parameters.AddWithValue(scope.ProductId);
                product.Parameters.AddWithValue($"TEST-{scope.ProductId:N}");
                await product.ExecuteNonQueryAsync();
            }

            await using (var inventory = new NpgsqlCommand("""
                INSERT INTO inventory (product_id, shelf_units, picked_units, safety_buffer, quarantined)
                VALUES ($1, $2, 0, 0, FALSE);
                """, connection, transaction))
            {
                inventory.Parameters.AddWithValue(scope.ProductId);
                inventory.Parameters.AddWithValue(shelfUnits);
                await inventory.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
            return scope;
        }
        catch
        {
            // Seeding is atomic; a failure before commit has no rows to clean up.
            await scope.Db.DisposeAsync();
            throw;
        }
    }

    public Guid NewRequestKey()
    {
        var key = Guid.NewGuid();
        requestKeys.Add(key);
        return key;
    }

    public async Task InstallCompletionFailureAsync(Guid requestKey)
    {
        if (!requestKeys.Contains(requestKey))
            throw new ArgumentException("Failure trigger must target a request owned by this test.", nameof(requestKey));
        if (failureObjectName is not null)
            throw new InvalidOperationException("This test already owns a failure trigger.");

        // Identifiers and UUID literals are generated locally, never taken from input.
        failureObjectName = $"test_reserve_failure_{Guid.NewGuid():N}";
        await using var connection = await Db.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var sql = $$"""
            CREATE FUNCTION {{failureObjectName}}() RETURNS trigger LANGUAGE plpgsql AS $body$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM orders WHERE product_id = '{{ProductId:D}}'::uuid)
                   OR NOT EXISTS (SELECT 1 FROM inventory_events
                                  WHERE product_id = '{{ProductId:D}}'::uuid AND kind = 'RESERVE') THEN
                    RAISE EXCEPTION USING ERRCODE = 'P0002', MESSAGE = 'Rollback test did not reach the completed reservation writes';
                END IF;
                RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'ShelfSync test-only reservation completion failure';
            END;
            $body$;

            CREATE TRIGGER {{failureObjectName}}
            BEFORE UPDATE ON operation_requests
            FOR EACH ROW
            WHEN (NEW.operation = 'RESERVE' AND NEW.request_key = '{{requestKey:D}}'::uuid)
            EXECUTE FUNCTION {{failureObjectName}}();
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await using var connection = await Db.OpenConnectionAsync();
            if (failureObjectName is not null)
            {
                // Commit object removal independently, so a later row-cleanup
                // failure cannot roll back the trigger/function removal.
                await using var objectTransaction = await connection.BeginTransactionAsync();
                await using var drop = new NpgsqlCommand($"""
                    DROP TRIGGER IF EXISTS {failureObjectName} ON operation_requests;
                    DROP FUNCTION IF EXISTS {failureObjectName}();
                    """, connection, objectTransaction);
                await drop.ExecuteNonQueryAsync();
                await objectTransaction.CommitAsync();
            }

            await using var transaction = await connection.BeginTransactionAsync();
            await using (var keys = new NpgsqlCommand("""
                DELETE FROM operation_requests
                WHERE operation IN ('RESERVE', 'CANCEL', 'PICK', 'SHORTAGE', 'RESOLVE_SHORTAGE')
                  AND request_key = ANY($1);
                """, connection, transaction))
            {
                keys.Parameters.AddWithValue(requestKeys.ToArray());
                await keys.ExecuteNonQueryAsync();
            }

            // Audit events reference orders/POS events; both reference the product.
            string[] statements =
            [
                "DELETE FROM inventory_events WHERE product_id = $1;",
                "DELETE FROM orders WHERE product_id = $1;",
                "DELETE FROM pos_events WHERE product_id = $1;",
                "DELETE FROM inventory WHERE product_id = $1;",
                "DELETE FROM products WHERE id = $1;"
            ];
            foreach (var sql in statements)
            {
                await using var command = new NpgsqlCommand(sql, connection, transaction);
                command.Parameters.AddWithValue(ProductId);
                await command.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }
        finally
        {
            await Db.DisposeAsync();
        }
    }
}
