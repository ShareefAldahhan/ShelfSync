using Npgsql;
using System.Text.Json;
using Microsoft.AspNetCore.RateLimiting;
using ShelfSync.Api.Accounts;
using ShelfSync.Api.Services;
using Stripe;
namespace ShelfSync.Api.Payments;
public sealed record PaymentReconciliationRequest(string SessionId);
public static class PaymentEndpoints
{
    public static void MapPaymentEndpoints(this WebApplication app)
    {
        static IResult Http(ReservationResult r)=>Results.Content(r.Json,"application/json",statusCode:r.StatusCode);
        var routes=app.MapGroup("/api/payments").RequireAuthorization("ApplicationUser");
        routes.MapGet("/config",(PaymentSettings s)=>Results.Ok(new{enabled=s.Enabled,testMode=true,currency="usd",payAfterPicking=false}));
        routes.MapPost("/orders/{id:guid}/checkout",async(Guid id,HttpContext c,PaymentService service,CancellationToken ct)=>Http(await service.CheckoutAsync(id,AccountEndpoints.UserId(c.User),ct))).RequireAuthorization("ReserveInventory").RequireRateLimiting("payments");
        routes.MapPost("/orders/{id:guid}/cancel-checkout",async(Guid id,HttpContext c,PaymentService service,CancellationToken ct)=>Http(await service.CancelCheckoutAsync(id,AccountEndpoints.UserId(c.User),ct))).RequireAuthorization("ReserveInventory").RequireRateLimiting("payments");
        routes.MapGet("/orders/{id:guid}",async(Guid id,HttpContext c,PaymentService service,CancellationToken ct)=>Http(await service.StatusAsync(id,AccountEndpoints.UserId(c.User),c.User.IsInRole(AppRoles.Associate)||c.User.IsInRole(AppRoles.Manager)||c.User.IsInRole(AppRoles.Admin),ct)));
        routes.MapPost("/orders/{id:guid}/refresh",async(Guid id,HttpContext c,PaymentService service,CancellationToken ct)=>Http(await service.RefreshAsync(id,AccountEndpoints.UserId(c.User),c.User.IsInRole(AppRoles.Associate)||c.User.IsInRole(AppRoles.Manager)||c.User.IsInRole(AppRoles.Admin),ct))).RequireRateLimiting("payments");
        routes.MapGet("/admin/recent",async(NpgsqlDataSource db,CancellationToken ct)=>
        {
            await using var cmd=db.CreateCommand("SELECT id,order_id,amount_cents,state,stripe_session_id,created_at FROM payments ORDER BY created_at DESC LIMIT 100");
            await using var r=await cmd.ExecuteReaderAsync(ct);var list=new List<object>();
            while(await r.ReadAsync(ct))list.Add(new{id=r.GetGuid(0),orderId=r.GetGuid(1),amountCents=r.GetInt64(2),state=r.GetString(3),sessionId=r.IsDBNull(4)?null:r.GetString(4),createdAt=r.GetDateTime(5)});
            return Results.Ok(list);
        }).RequireAuthorization("Admin");
        routes.MapPost("/{payment:guid}/reconcile",async(Guid payment,PaymentReconciliationRequest request,PaymentService service,CancellationToken ct)=>Http(await service.ReconcileAsync(payment,request.SessionId,ct))).RequireAuthorization("Admin").RequireRateLimiting("payments");
        app.MapPost("/api/payments/stripe-webhook",async(HttpContext c,PaymentSettings settings,PaymentService service,IStripeGateway gateway,CancellationToken ct)=>
        {
            if(!settings.Enabled)return Results.StatusCode(503);
            using var reader=new StreamReader(c.Request.Body);var raw=await reader.ReadToEndAsync(ct);
            try
            {
                var verified=PaymentWebhookVerifier.Verify(raw,c.Request.Headers["Stripe-Signature"].ToString(),settings.WebhookSecret);
                if(verified is null)return Results.Ok();
                var (eventId,session)=verified.Value;
                // Never trust an event's totals alone: retrieve the authoritative session with the server's own test key.
                var snapshot=await gateway.GetAsync(session,ct);
                return await service.ApplyAsync(snapshot,eventId,ct)?Results.Ok():Results.BadRequest();
            }
            catch(Exception ex)when(ex is StripeException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {return Results.BadRequest();}
        }).AllowAnonymous();
    }
}
