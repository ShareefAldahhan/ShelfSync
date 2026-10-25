using System.Text.Json;
using ShelfSync.Api.Models;
using ShelfSync.Api.Repositories;
using ShelfSync.Api.Services;

namespace ShelfSync.Tests;

public sealed class OrderOwnershipIntegrationTests
{
    [Fact]
    public async Task CustomerListsOnlyOwnOrders_AndCannotCancelAnotherOrLegacyOrder()
    {
        await using var scope=await PostgresTestScope.CreateAsync();var a=await scope.NewAccountAsync();var b=await scope.NewAccountAsync();
        var reservations=new ReservationService(scope.Db);
        using var own=JsonDocument.Parse((await reservations.CreateAsync(new(scope.ProductId,1),scope.NewRequestKey(),ownerUserId:a)).Json);
        using var other=JsonDocument.Parse((await reservations.CreateAsync(new(scope.ProductId,1),scope.NewRequestKey(),ownerUserId:b)).Json);
        using var legacy=JsonDocument.Parse((await reservations.CreateAsync(new(scope.ProductId,1),scope.NewRequestKey())).Json);
        var ownId=own.RootElement.GetProperty("orderId").GetGuid();var otherId=other.RootElement.GetProperty("orderId").GetGuid();var legacyId=legacy.RootElement.GetProperty("orderId").GetGuid();
        var list=await new OrderRepository(scope.Db).GetRecentAsync(customerId:a);Assert.Single(list);Assert.Equal(ownId,list[0].Id);
        var cancel=new ReservationCancellationService(scope.Db);
        Assert.Equal(404,(await cancel.CancelAsync(otherId,scope.NewRequestKey(),customerId:a)).StatusCode);
        Assert.Equal(404,(await cancel.CancelAsync(legacyId,scope.NewRequestKey(),customerId:a)).StatusCode);
        Assert.Equal(200,(await cancel.CancelAsync(ownId,scope.NewRequestKey(),customerId:a)).StatusCode);
        Assert.Equal(200,(await cancel.CancelAsync(legacyId,scope.NewRequestKey())).StatusCode);
    }
    [Fact]
    public async Task CopiedReservationKey_CannotReplayAnotherCustomersResponse()
    {
        await using var scope=await PostgresTestScope.CreateAsync();var a=await scope.NewAccountAsync();var b=await scope.NewAccountAsync();var key=scope.NewRequestKey();var service=new ReservationService(scope.Db);
        Assert.Equal(201,(await service.CreateAsync(new(scope.ProductId,1),key,ownerUserId:a)).StatusCode);
        Assert.Equal(409,(await service.CreateAsync(new(scope.ProductId,1),key,ownerUserId:b)).StatusCode);
        Assert.Empty(await new OrderRepository(scope.Db).GetRecentAsync(customerId:b));
    }
}
