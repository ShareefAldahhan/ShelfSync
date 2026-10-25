using Npgsql;
using ShelfSync.Api;

namespace ShelfSync.Tests;

public sealed class DatabaseReadinessTests
{
    [Fact]
    public async Task FullyMigratedDatabaseIsReady()
    {
        await using var scope = await PostgresTestScope.CreateAsync();
        Assert.True(await DatabaseReadiness.CheckAsync(scope.Db, CancellationToken.None));
    }

    [Fact]
    public async Task ConnectionWithoutApplicationSchemaIsNotReady()
    {
        await using var scope = await PostgresTestScope.CreateAsync();
        var settings = new NpgsqlConnectionStringBuilder(scope.Db.ConnectionString)
        {
            SearchPath = "pg_catalog"
        };
        await using var database = NpgsqlDataSource.Create(settings.ConnectionString);
        Assert.False(await DatabaseReadiness.CheckAsync(database, CancellationToken.None));
    }
}
