using ShelfSync.Api.Repositories;

namespace ShelfSync.Tests;

public sealed class CatalogTests
{
    [Fact]
    public async Task SearchSortAndPagesUseTheWholeMatchingCatalog()
    {
        await using var first = await PostgresTestScope.CreateAsync();
        await using var second = await PostgresTestScope.CreateAsync();
        await using var third = await PostgresTestScope.CreateAsync();
        var prefix = Guid.NewGuid().ToString("N");
        foreach (var (scope, name, price) in new[] { (first, "chips", 300), (second, "milk", 100), (third, "beans", 200) })
        {
            await using var update = scope.Db.CreateCommand("UPDATE products SET sku=$2,name=$3,price_cents=$4 WHERE id=$1");
            update.Parameters.AddWithValue(scope.ProductId);
            update.Parameters.AddWithValue(prefix + name);
            update.Parameters.AddWithValue(name);
            update.Parameters.AddWithValue(price);
            await update.ExecuteNonQueryAsync();
        }
        var repository = new ProductRepository(first.Db);
        var page1 = await repository.SearchAsync(prefix.ToUpperInvariant(), "All products", "price-low", 1, 2);
        var page2 = await repository.SearchAsync(prefix, "All products", "price-low", 2, 2);
        Assert.Equal(3, page1.Total);
        Assert.Equal(new[] { second.ProductId, third.ProductId }, page1.Items.Select(p => p.Id));
        Assert.Equal(first.ProductId, Assert.Single(page2.Items).Id);
        var pantry = await repository.SearchAsync(prefix, "Pantry", "price-high", 1, 24);
        Assert.Equal(new[] { first.ProductId, third.ProductId }, pantry.Items.Select(p => p.Id));
        var outside = await repository.SearchAsync(prefix, "All products", "featured", 10, 2);
        Assert.Equal(3, outside.Total);
        Assert.Empty(outside.Items);
    }

    [Theory]
    [InlineData("%")]
    [InlineData("_")]
    [InlineData("' OR 1=1 --")]
    public async Task SearchTreatsWildcardAndSqlTextAsLiteralInput(string suffix)
    {
        await using var fixture = await PostgresTestScope.CreateAsync();
        var unique = Guid.NewGuid().ToString("N");
        var result = await new ProductRepository(fixture.Db).SearchAsync(unique + suffix, "All products", "featured", 1, 24);
        Assert.Equal(0, result.Total);
        Assert.Empty(result.Items);
    }
}
