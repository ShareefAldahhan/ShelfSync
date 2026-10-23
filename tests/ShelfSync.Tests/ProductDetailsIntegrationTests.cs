using Npgsql;
using ShelfSync.Api.Models;
using ShelfSync.Api.Repositories;
using ShelfSync.Api.Services;
using System.Text.Json;

namespace ShelfSync.Tests;

public sealed class ProductDetailsIntegrationTests
{
    [Fact]
    public async Task UnknownProduct_ReturnsNull()
    {
        await using var scope=await PostgresTestScope.CreateAsync();
        Assert.Null(await new ProductDetailsRepository(scope.Db).GetAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Details_UsesLiveHoldsAndSafetyBuffer_AndReturnsStoredMetadata()
    {
        await using var scope=await PostgresTestScope.CreateAsync();
        var reserved=await new ReservationService(scope.Db).CreateAsync(new(scope.ProductId,3),scope.NewRequestKey());
        Assert.Equal(201,reserved.StatusCode);
        await using var connection=await scope.Db.OpenConnectionAsync();
        await using var sql=new NpgsqlCommand("""
            WITH adjusted AS (UPDATE inventory SET safety_buffer=2 WHERE product_id=$1 RETURNING product_id), profile AS (
            INSERT INTO product_profiles(product_id,barcode,aisle,bay,shelf,case_pack,reorder_point)
            VALUES ($1,'INTERNAL-TEST','A01','03','02',12,4) RETURNING product_id)
            INSERT INTO orders(id,product_id,quantity,unit_price_cents,state,created_at,expires_at)
            VALUES ($2,$1,4,1000,'RESERVED',CURRENT_TIMESTAMP-INTERVAL '30 minutes',CURRENT_TIMESTAMP-INTERVAL '15 minutes');
            """,connection);
        sql.Parameters.AddWithValue(scope.ProductId);sql.Parameters.AddWithValue(Guid.NewGuid());await sql.ExecuteNonQueryAsync();
        var repository=new ProductDetailsRepository(scope.Db);
        var result=Assert.IsType<AssociateDetails>(await repository.GetAsync(scope.ProductId));
        Assert.Equal(3,result.Stock.Reserved);Assert.Equal(5,result.Stock.Available);Assert.Equal("INTERNAL-TEST",result.Profile.Barcode);
        Assert.Equal("A01",result.Profile.Aisle);Assert.Equal(12,result.Profile.CasePack);Assert.Equal(4,result.Profile.ReorderPoint);
        await using var quarantine=new NpgsqlCommand("UPDATE inventory SET quarantined=true,quarantine_reason='Physical count required' WHERE product_id=$1",connection);
        quarantine.Parameters.AddWithValue(scope.ProductId);await quarantine.ExecuteNonQueryAsync();
        var blocked=(await repository.GetAsync(scope.ProductId))!;Assert.Equal(0,blocked.Stock.Available);Assert.Equal("Physical count required",blocked.Stock.QuarantineReason);
    }

    [Fact]
    public async Task SaleReplay_IsCountedOnce_AndHistoryIsProductScoped()
    {
        await using var scope=await PostgresTestScope.CreateAsync();
        var service=new PosSaleService(scope.Db);var request=new PosSaleRequest(Guid.NewGuid(),scope.ProductId,2);
        Assert.Equal(200,(await service.RecordAsync(request)).StatusCode);Assert.Equal(200,(await service.RecordAsync(request)).StatusCode);
        var result=(await new ProductDetailsRepository(scope.Db).GetAsync(scope.ProductId))!;
        Assert.Equal(2,result.PosUnits30Days);Assert.Equal(8,result.Stock.Shelf);Assert.Single(result.RecentSales);
        Assert.Equal(request.EventId,result.RecentSales[0].EventId);Assert.Single(result.Activity,a=>a.Kind=="SALE");
        Assert.Equal(result.Sku,result.Profile.Barcode);Assert.Null(result.Profile.Aisle);Assert.Null(result.Profile.Supplier);
        await using var connection=await scope.Db.OpenConnectionAsync();
        await using var age=new NpgsqlCommand("UPDATE pos_events SET committed_at=CURRENT_TIMESTAMP-INTERVAL '31 days' WHERE product_id=$1",connection);
        age.Parameters.AddWithValue(scope.ProductId);await age.ExecuteNonQueryAsync();
        var old=(await new ProductDetailsRepository(scope.Db).GetAsync(scope.ProductId))!;Assert.Equal(0,old.PosUnits30Days);Assert.Single(old.RecentSales);
    }

    [Fact]
    public async Task Fulfillment_IsSeparateFromPos_AndAudited()
    {
        await using var scope=await PostgresTestScope.CreateAsync();
        var reserved=await new ReservationService(scope.Db).CreateAsync(new(scope.ProductId,2),scope.NewRequestKey());
        using var json=JsonDocument.Parse(reserved.Json);var order=json.RootElement.GetProperty("orderId").GetGuid();
        Assert.Equal(200,(await new OrderPickingService(scope.Db).PickAsync(order,scope.NewRequestKey())).StatusCode);
        Assert.Equal(200,(await new OrderFulfillmentService(scope.Db).FulfillAsync(order,scope.NewRequestKey())).StatusCode);
        var result=(await new ProductDetailsRepository(scope.Db).GetAsync(scope.ProductId))!;
        Assert.Equal(2,result.FulfilledUnits30Days);Assert.Equal(0,result.PosUnits30Days);Assert.Equal(0,result.Stock.Picked);
        Assert.Contains(result.Activity,a=>a.Kind=="PICK" && a.ShelfDelta==-2 && a.PickedDelta==2);
        Assert.Contains(result.Activity,a=>a.Kind=="FULFILL" && a.PickedDelta==-2);
    }
}
