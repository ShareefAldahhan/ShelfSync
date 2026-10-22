using ShelfSync.Api.Models;
using ShelfSync.Api.Repositories;
using ShelfSync.Api.Services;
using System.Text.Json;

namespace ShelfSync.Tests;

public sealed class OrderListingIntegrationTests
{
    [Fact]
    public async Task RecentOrders_IncludesCreatedReservationAndCurrentState()
    {
        await using var scope = await PostgresTestScope.CreateAsync();
        var created = await new ReservationService(scope.Db).CreateAsync(
            new ReservationRequest(scope.ProductId, 2), scope.NewRequestKey());
        Assert.Equal(201, created.StatusCode);
        using var json = JsonDocument.Parse(created.Json);
        var id = json.RootElement.GetProperty("orderId").GetGuid();
        var repository = new OrderRepository(scope.Db);
        var orders = await repository.GetRecentAsync();
        var order = Assert.Single(orders, o => o.Id == id);
        Assert.Equal(scope.ProductId, order.ProductId);
        Assert.Equal(2, order.Quantity);
        Assert.Equal("RESERVED", order.State);
        Assert.True(order.ExpiresAt > order.CreatedAt);
        var cancelled = await new ReservationCancellationService(scope.Db).CancelAsync(id, scope.NewRequestKey());
        Assert.Equal(200, cancelled.StatusCode);
        Assert.Equal("CANCELLED", Assert.Single(await repository.GetRecentAsync(), o => o.Id == id).State);
        Assert.True(orders.Count <= 100);
    }
}
