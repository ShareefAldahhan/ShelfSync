using Npgsql;
using ShelfSync.Api.Repositories;
using ShelfSync.Api.Services;
using ShelfSync.Api.Models;

namespace ShelfSync.Tests;

public sealed class ItemMaintenanceIntegrationTests
{
    [Theory]
    [InlineData(-1,"Count checked")]
    [InlineData(1000001,"Count checked")]
    [InlineData(4,"")]
    public async Task InvalidCorrection_DoesNotChangeInventory(int quantity,string reason)
    {
        await using var scope=await PostgresTestScope.CreateAsync();
        var result=await new ItemMaintenanceService(scope.Db).UpdateAsync(scope.ProductId,scope.NewRequestKey(),new(null,null,null,null,quantity,10,reason),"operator",true);
        Assert.Equal(400,result.StatusCode);Assert.Equal(10,(await new ProductDetailsRepository(scope.Db).GetAsync(scope.ProductId))!.Stock.Shelf);
    }
    [Fact]
    public async Task Correction_ReplaysOnce_ConflictingPayloadIsRejected_AndActorIsAudited()
    {
        await using var scope=await PostgresTestScope.CreateAsync();var service=new ItemMaintenanceService(scope.Db);var key=scope.NewRequestKey();
        var request=new ItemUpdateRequest(null,null,null,null,7,10,"Physical shelf count");
        var first=await service.UpdateAsync(scope.ProductId,key,request,"operator",true);
        var retry=await service.UpdateAsync(scope.ProductId,key,request,"operator",true);
        Assert.Equal(200,first.StatusCode);Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(first.Json),System.Text.Json.Nodes.JsonNode.Parse(retry.Json)));
        Assert.Equal(409,(await service.UpdateAsync(scope.ProductId,key,request with{ShelfUnits=6},"operator",true)).StatusCode);
        var details=(await new ProductDetailsRepository(scope.Db).GetAsync(scope.ProductId))!;
        Assert.Equal(7,details.Stock.Shelf);Assert.Equal(0,details.Stock.Picked);
        var audit=Assert.Single(details.Activity);Assert.Equal(-3,audit.ShelfDelta);Assert.Equal("operator",audit.Actor);Assert.Equal("Physical shelf count",audit.Reason);
    }
    [Fact]
    public async Task StaleCountAndUncoveredReservations_AreRejected()
    {
        await using var scope=await PostgresTestScope.CreateAsync();var service=new ItemMaintenanceService(scope.Db);
        Assert.Equal(409,(await service.UpdateAsync(scope.ProductId,scope.NewRequestKey(),new(null,null,null,null,4,9,"Recounted"),"operator",true)).StatusCode);
        Assert.Equal(201,(await new ReservationService(scope.Db).CreateAsync(new(scope.ProductId,3),scope.NewRequestKey())).StatusCode);
        Assert.Equal(409,(await service.UpdateAsync(scope.ProductId,scope.NewRequestKey(),new(null,null,null,null,2,10,"Recounted"),"operator",true)).StatusCode);
        var details=(await new ProductDetailsRepository(scope.Db).GetAsync(scope.ProductId))!;Assert.Equal(10,details.Stock.Shelf);Assert.DoesNotContain(details.Activity,e=>e.Kind=="RECONCILE");
    }
    [Fact]
    public async Task LocationCanBeAddedChangedAndCleared_WithoutStockChanges()
    {
        await using var scope=await PostgresTestScope.CreateAsync();var service=new ItemMaintenanceService(scope.Db);
        var key=scope.NewRequestKey();var request=new ItemUpdateRequest(" A03 ","04","02","BIN-7",null,null,"Moved to new display");
        Assert.Equal(200,(await service.UpdateAsync(scope.ProductId,key,request,"associate",false)).StatusCode);
        Assert.Equal(200,(await service.UpdateAsync(scope.ProductId,key,request,"associate",false)).StatusCode);
        var repository=new ProductDetailsRepository(scope.Db);var details=(await repository.GetAsync(scope.ProductId))!;
        Assert.Equal("A03",details.Profile.Aisle);Assert.Equal("BIN-7",details.Profile.Bin);Assert.Equal(10,details.Stock.Shelf);Assert.Single(details.Activity);
        Assert.Equal(200,(await service.UpdateAsync(scope.ProductId,scope.NewRequestKey(),request with{Bin=" ",Reason="Removed temporary bin"},"associate",false)).StatusCode);
        var updated=(await repository.GetAsync(scope.ProductId))!;Assert.Null(updated.Profile.Bin);Assert.Equal(2,updated.Activity.Count);Assert.All(updated.Activity,e=>{Assert.Equal(0,e.ShelfDelta);Assert.Equal("associate",e.Actor);});
    }
    [Fact]
    public async Task ConcurrentPosAndCorrection_RespectStockLockAndExpectedCount()
    {
        await using var scope=await PostgresTestScope.CreateAsync();
        var correction=new ItemMaintenanceService(scope.Db).UpdateAsync(scope.ProductId,scope.NewRequestKey(),new(null,null,null,null,8,10,"Shelf recount"),"operator",true);
        var sale=new PosSaleService(scope.Db).RecordAsync(new PosSaleRequest(Guid.NewGuid(),scope.ProductId,1));
        await Task.WhenAll(correction,sale);var correctionResult=await correction;var saleResult=await sale;Assert.Equal(200,saleResult.StatusCode);Assert.Contains(correctionResult.StatusCode,new[]{200,409});
        var details=(await new ProductDetailsRepository(scope.Db).GetAsync(scope.ProductId))!;
        Assert.Equal(correctionResult.StatusCode==200?7:9,details.Stock.Shelf);Assert.Equal(1,details.PosUnits30Days);
    }
}
