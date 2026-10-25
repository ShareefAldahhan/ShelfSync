using ShelfSync.Api.Accounts;
using ShelfSync.Api.Payments;
using ShelfSync.Api.Models;
using ShelfSync.Api.Repositories;
using ShelfSync.Api.Services;

namespace ShelfSync.Api.Endpoints;

public static class BusinessEndpoints
{
    public static void MapBusinessEndpoints(this WebApplication app)
    {
        var api=app.MapGroup("/api").RequireAuthorization("ApplicationUser");
        api.MapGet("/products",async(ProductRepository r,CancellationToken ct)=>Results.Ok(await r.GetAllAsync(ct)));
        api.MapGet("/products/{id:guid}",async(Guid id,ProductRepository r,CancellationToken ct)=>
        {var product=await r.GetByIdAsync(id,ct);return product is null?Results.NotFound():Results.Ok(product);});
        api.MapGet("/products/{id:guid}/details",async(Guid id,ProductDetailsRepository r,CancellationToken ct)=>
        {var details=await r.GetAsync(id,ct);return details is null?Results.NotFound():Results.Ok(details);}).RequireAuthorization("Staff");
        api.MapGet("/orders",async(HttpContext c,OrderRepository r,int? page,IConfiguration config,CancellationToken ct)=>page is <1 or >100000?Results.BadRequest():Results.Ok(await r.GetRecentAsync(ct,CustomerId(c),page??1,config.GetValue<bool>("Stripe:Enabled"),config.GetValue<bool>("Stripe:Enabled")&&c.User.IsInRole(AppRoles.Associate)&&!c.User.IsInRole(AppRoles.Admin)&&!c.User.IsInRole(AppRoles.Manager))));
        api.MapPost("/reservations",async(ReservationRequest request,HttpContext c,ReservationService s,CancellationToken ct)=>
            await WithKey(c,key=>s.CreateAsync(request,key,ct,AccountEndpoints.UserId(c.User)))).RequireAuthorization("ReserveInventory");
        api.MapPost("/reservations/{orderId:guid}/cancel",async(Guid orderId,HttpContext c,ReservationCancellationService s,CancellationToken ct)=>
            await WithKey(c,key=>s.CancelAsync(orderId,key,ct,CustomerId(c))));
        api.MapPost("/orders/{orderId:guid}/pick",async(Guid orderId,HttpContext c,OrderPickingService s,CancellationToken ct)=>
            await WithKey(c,key=>s.PickAsync(orderId,key,ct,requireCustomerReservation:c.User.IsInRole(AppRoles.Associate)&&!c.User.IsInRole(AppRoles.Manager)&&!c.User.IsInRole(AppRoles.Admin)))).RequireAuthorization("Staff");
        api.MapPost("/orders/{orderId:guid}/shortage",async(Guid orderId,HttpContext c,OrderShortageService s,CancellationToken ct)=>
            await WithKey(c,key=>s.ReportAsync(orderId,key,ct))).RequireAuthorization("Staff");
        api.MapPost("/orders/{orderId:guid}/resolve-shortage",async(Guid orderId,HttpContext c,OrderShortageResolutionService s,CancellationToken ct)=>
            await WithKey(c,key=>s.ResolveAsync(orderId,key,ct))).RequireAuthorization("Management");
        api.MapPost("/orders/{orderId:guid}/fulfill",async(Guid orderId,HttpContext c,OrderFulfillmentService s,CancellationToken ct)=>
            await WithKey(c,key=>s.FulfillAsync(orderId,key,ct))).RequireAuthorization("Staff");
        api.MapPost("/pos/sales",async(PosSaleRequest request,PosSaleService s,CancellationToken ct)=>ToHttp(await s.RecordAsync(request,ct))).RequireAuthorization("Staff");
        api.MapPost("/cart/checkout",async(CartRequest request,HttpContext c,CartReservationService carts,PaymentService payments,PaymentSettings settings,CancellationToken ct)=>
        {
            if(!settings.Enabled)return Results.Json(new{error="Online test checkout is not configured."},statusCode:503);
            return await WithKey(c,async key=>
            {
                var reserved=await carts.ReserveAsync(request,key,AccountEndpoints.UserId(c.User),ct);
                if(reserved.StatusCode!=201)return reserved;
                using var doc=System.Text.Json.JsonDocument.Parse(reserved.Json);
                var order=doc.RootElement.GetProperty("orders")[0].GetProperty("orderId").GetGuid();
                return await payments.CheckoutAsync(order,AccountEndpoints.UserId(c.User),ct,returnPaidResult:true);
            });
        }).RequireAuthorization("ReserveInventory").RequireRateLimiting("payments");
        api.MapPost("/cart/reserve",async(CartRequest request,HttpContext c,CartReservationService service,CancellationToken ct)=>
            await WithKey(c,key=>service.ReserveAsync(request,key,AccountEndpoints.UserId(c.User),ct))).RequireAuthorization("ReserveInventory");
        api.MapGet("/stock",async(StoreOperationsRepository r,CancellationToken ct)=>Results.Content(await r.StockAsync(ct),"application/json")).RequireAuthorization("Staff");
        api.MapGet("/inventory-history",async(string? search,int? page,StoreOperationsRepository r,CancellationToken ct)=>
            page is <1 or >100000||search?.Length>200?Results.BadRequest():Results.Content(await r.HistoryAsync(search,page??1,ct),"application/json")).RequireAuthorization("Staff");
        api.MapGet("/orders/{id:guid}/receipt",async(Guid id,HttpContext c,StoreOperationsRepository r,CancellationToken ct)=>
        {var receipt=await r.ReceiptAsync(id,CustomerId(c),ct);return receipt=="null"?Results.NotFound():Results.Content(receipt,"application/json");});
        api.MapPost("/stock/receive",async(StockMovementRequest request,HttpContext c,StoreOperationsService service,CancellationToken ct)=>
            await WithKey(c,key=>service.MoveAsync("RECEIVE_BACKROOM",request,key,c.User.Identity!.Name!,ct))).RequireAuthorization("Staff");
        api.MapPost("/stock/replenish",async(StockMovementRequest request,HttpContext c,StoreOperationsService service,CancellationToken ct)=>
            await WithKey(c,key=>service.MoveAsync("REPLENISH_SHELF",request,key,c.User.Identity!.Name!,ct))).RequireAuthorization("Staff");
        api.MapPost("/returns",async(ReturnRequest request,HttpContext c,StoreOperationsService service,CancellationToken ct)=>
            await WithKey(c,key=>service.ReturnAsync(request,key,c.User.Identity!.Name!,ct))).RequireAuthorization("Management");
        api.MapPost("/orders/{id:guid}/cancel-picked",async(Guid id,PickedCancellationRequest request,HttpContext c,StoreOperationsService service,CancellationToken ct)=>
            await WithKey(c,key=>service.CancelPickedAsync(id,request,key,c.User.Identity!.Name!,ct))).RequireAuthorization("Management");
        foreach(var action in new[]{"location","stock-correction"})
        {
            var correct=action=="stock-correction";
            api.MapPost("/products/{id:guid}/"+action,async(Guid id,ItemUpdateRequest request,HttpContext c,ItemMaintenanceService s,CancellationToken ct)=>
                await WithKey(c,key=>s.UpdateAsync(id,key,request,c.User.Identity!.Name!,correct,ct))).RequireAuthorization(correct?"Management":"Staff");
        }
    }
    private static Guid? CustomerId(HttpContext c)=>c.User.IsInRole(AppRoles.Customer)?AccountEndpoints.UserId(c.User):null;
    private static IResult ToHttp(ReservationResult result)=>Results.Content(result.Json,"application/json",statusCode:result.StatusCode);
    private static async Task<IResult> WithKey(HttpContext context,Func<Guid,Task<ReservationResult>> operation)
    {
        if(!context.Request.Headers.TryGetValue("Idempotency-Key",out var values)||values.Count!=1||!Guid.TryParse(values[0],out var key)||key==Guid.Empty)
            return Results.BadRequest(new{error="A valid Idempotency-Key UUID header is required."});
        return ToHttp(await operation(key));
    }
}

