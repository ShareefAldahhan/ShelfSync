using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Npgsql;
using ShelfSync.Api.Payments;
using ShelfSync.Api.Services;
namespace ShelfSync.Tests;
public sealed class PaymentTests
{
 static IConfiguration Config(bool live=false)=>new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Stripe:Enabled","true"},{"Stripe:SecretKey",live?"sk_live_forbidden":"sk_test_fixture"},{"Stripe:WebhookSecret","whsec_fixture"}}).Build();
 sealed class FakeStripe:IStripeGateway
 {
    public ConcurrentDictionary<Guid,CheckoutSnapshot> Sessions=new();
    public ConcurrentBag<DateTime> Expirations=new();
    public Task<CheckoutSnapshot> CreateAsync(Guid payment,Guid order,long amount,DateTime expiry,CancellationToken ct)
    {Expirations.Add(expiry);return Task.FromResult(Sessions.GetOrAdd(payment,id=>new("cs_test_"+id.ToString("N"),"https://checkout.stripe.com/c/pay/test","open","unpaid",amount,"usd",false,id)));}
    public Task<CheckoutSnapshot> GetAsync(string session,CancellationToken ct)=>Task.FromResult(Sessions.Values.Single(s=>s.Id==session));
 }
 static PaymentService Service(PostgresTestScope f,FakeStripe fake)=>new(f.Db,fake,new(Config()));
 static async Task<(Guid Owner,Guid Order)> Pick(PostgresTestScope f,int quantity=2)
 {
    var owner=await f.NewAccountAsync();var r=await new ReservationService(f.Db).CreateAsync(new(f.ProductId,quantity),f.NewRequestKey(),ownerUserId:owner);
    Assert.Equal(201,r.StatusCode);var order=JsonDocument.Parse(r.Json).RootElement.GetProperty("orderId").GetGuid();
    Assert.Equal(200,(await new OrderPickingService(f.Db).PickAsync(order,f.NewRequestKey())).StatusCode);return(owner,order);
 }
 [Fact] public void LiveKeys_AreDisabled(){Assert.False(new PaymentSettings(Config(true)).Enabled);Assert.True(new PaymentSettings(Config()).Enabled);}
 [Theory][InlineData("https://checkout.stripe.com/c/pay/test",true)][InlineData("http://checkout.stripe.com/test",false)][InlineData("https://checkout.stripe.com.evil.example/test",false)][InlineData("javascript:alert(1)",false)][InlineData("https://evil.example/",false)]
 public void CheckoutRedirect_IsStrictlyAllowlisted(string value,bool valid)=>Assert.Equal(valid,PaymentService.ValidCheckoutUrl(value));
 [Fact] public async Task Checkout_RequiresOwner_AndAcceptsActiveReservation()
 {await using var f=await PostgresTestScope.CreateAsync();var owner=await f.NewAccountAsync();var fake=new FakeStripe();var s=Service(f,fake);var r=await new ReservationService(f.Db).CreateAsync(new(f.ProductId,1),f.NewRequestKey(),ownerUserId:owner);var order=JsonDocument.Parse(r.Json).RootElement.GetProperty("orderId").GetGuid();Assert.Equal(200,(await s.CheckoutAsync(order,owner)).StatusCode);Assert.Equal(404,(await s.CheckoutAsync(order,Guid.NewGuid())).StatusCode);Assert.Single(fake.Sessions);}
 [Fact] public async Task Checkout_UsesStoredPrice_AndReusesSingleIntentConcurrently()
 {await using var f=await PostgresTestScope.CreateAsync();var(owner,order)=await Pick(f);var fake=new FakeStripe();var s=Service(f,fake);var results=await Task.WhenAll(s.CheckoutAsync(order,owner),s.CheckoutAsync(order,owner));Assert.All(results,r=>Assert.Equal(200,r.StatusCode));Assert.Single(fake.Sessions);Assert.Equal(2000,fake.Sessions.Values.Single().Amount);Assert.Single(fake.Expirations.Select(e=>new DateTimeOffset(e).ToUnixTimeSeconds()).Distinct());}
 [Fact] public async Task PaidConfirmation_IsAmountBound_Deduplicated_AndCannotBeDowngraded()
 {await using var f=await PostgresTestScope.CreateAsync();var(owner,order)=await Pick(f);var fake=new FakeStripe();var s=Service(f,fake);await s.CheckoutAsync(order,owner);var session=fake.Sessions.Values.Single();Assert.False(await s.ApplyAsync(session with{Amount=1,PaymentStatus="paid"},"evt_wrong"));Assert.False(await s.ApplyAsync(session with{Currency="cad",PaymentStatus="paid"},"evt_currency"));Assert.False(await s.ApplyAsync(session with{Live=true,PaymentStatus="paid"},"evt_live"));Assert.True(await s.ApplyAsync(session with{PaymentStatus="paid",Status="complete"},"evt_paid"));Assert.True(await s.ApplyAsync(session with{PaymentStatus="paid",Status="complete"},"evt_paid"));Assert.True(await s.ApplyAsync(session,"evt_late"));var state=JsonDocument.Parse((await s.StatusAsync(order,owner,false)).Json).RootElement.GetProperty("state").GetString();Assert.Equal("PAID",state);Assert.Equal(409,(await s.CheckoutAsync(order,owner)).StatusCode);}
 [Fact] public async Task ActivePayment_BlocksPickedCancellation_AndUnpaidFulfillment()
 {await using var f=await PostgresTestScope.CreateAsync();var(owner,order)=await Pick(f);var fake=new FakeStripe();await Service(f,fake).CheckoutAsync(order,owner);Assert.Equal(409,(await new StoreOperationsService(new(f.Db)).CancelPickedAsync(order,new("backroom","Customer changed mind"),f.NewRequestKey(),"manager")).StatusCode);Assert.Equal(409,(await new OrderFulfillmentService(f.Db).FulfillAsync(order,f.NewRequestKey())).StatusCode);}
 [Fact] public async Task PaidPayment_AllowsFulfillment_AndProtectsOtherCustomersStatus()
 {await using var f=await PostgresTestScope.CreateAsync();var(owner,order)=await Pick(f);var fake=new FakeStripe();var s=Service(f,fake);await s.CheckoutAsync(order,owner);var session=fake.Sessions.Values.Single();await s.ApplyAsync(session with{PaymentStatus="paid",Status="complete"},"evt_paid");Assert.Equal(404,(await s.StatusAsync(order,Guid.NewGuid(),false)).StatusCode);Assert.Equal(200,(await new OrderFulfillmentService(f.Db).FulfillAsync(order,f.NewRequestKey())).StatusCode);}
 [Fact] public async Task DelayedPayment_MustRemainUnfulfilledUntilConfirmed()
 {await using var f=await PostgresTestScope.CreateAsync();var(owner,order)=await Pick(f);var fake=new FakeStripe();var s=Service(f,fake);await s.CheckoutAsync(order,owner);var session=fake.Sessions.Values.Single();await s.ApplyAsync(session with{Status="complete",IntentStatus="processing"},"evt_pending");Assert.Equal("PROCESSING",JsonDocument.Parse((await s.StatusAsync(order,owner,false)).Json).RootElement.GetProperty("state").GetString());Assert.Equal(409,(await new OrderFulfillmentService(f.Db).FulfillAsync(order,f.NewRequestKey())).StatusCode);}
 [Fact] public async Task ExpiredSession_AllowsCancellationWithoutReturningMoney()
 {await using var f=await PostgresTestScope.CreateAsync();var(owner,order)=await Pick(f);var fake=new FakeStripe();var s=Service(f,fake);await s.CheckoutAsync(order,owner);var session=fake.Sessions.Values.Single();await s.ApplyAsync(session with{Status="expired"},"evt_expired");Assert.Equal(200,(await new StoreOperationsService(new(f.Db)).CancelPickedAsync(order,new("backroom","Abandoned test checkout"),f.NewRequestKey(),"manager")).StatusCode);}
 [Fact] public async Task EnabledPayments_BlockOwnedOrdersWithoutPayment()
 {await using var f=await PostgresTestScope.CreateAsync();var(_,order)=await Pick(f);Assert.Equal(409,(await new OrderFulfillmentService(f.Db,Config()).FulfillAsync(order,f.NewRequestKey())).StatusCode);}
 [Fact] public async Task ConfirmedFullRefund_BlocksRepayment_AndAllowsStockCancellation()
 {await using var f=await PostgresTestScope.CreateAsync();var(owner,order)=await Pick(f);var fake=new FakeStripe();var service=Service(f,fake);await service.CheckoutAsync(order,owner);var snapshot=fake.Sessions.Values.Single() with{Status="complete",PaymentStatus="paid"};await service.ApplyAsync(snapshot,"evt_paid");await service.ApplyAsync(snapshot with{Refunded=snapshot.Amount},"evt_refund");Assert.Equal("REFUNDED",JsonDocument.Parse((await service.StatusAsync(order,owner,false)).Json).RootElement.GetProperty("state").GetString());Assert.Equal(409,(await service.CheckoutAsync(order,owner)).StatusCode);Assert.Equal(200,(await new StoreOperationsService(new(f.Db)).CancelPickedAsync(order,new("backroom","Refund confirmed by provider"),f.NewRequestKey(),"manager")).StatusCode);}
 [Fact] public async Task PartialRefund_RequiresReview_AndCannotFulfill()
 {await using var f=await PostgresTestScope.CreateAsync();var(owner,order)=await Pick(f);var fake=new FakeStripe();var service=Service(f,fake);await service.CheckoutAsync(order,owner);await service.ApplyAsync(fake.Sessions.Values.Single() with{Status="complete",PaymentStatus="paid",Refunded=100},"evt_partial");Assert.Equal("REVIEW",JsonDocument.Parse((await service.StatusAsync(order,owner,false)).Json).RootElement.GetProperty("state").GetString());Assert.Equal(409,(await new OrderFulfillmentService(f.Db).FulfillAsync(order,f.NewRequestKey())).StatusCode);}
 const string Secret="whsec_testing_only";
 static string Sign(string raw,long timestamp)=>$"t={timestamp},v1={Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret),Encoding.UTF8.GetBytes(timestamp+"."+raw))).ToLowerInvariant()}";
 static string Payload(bool live=false)=>JsonSerializer.Serialize(new{id="evt_test",type="checkout.session.completed",livemode=live,data=new{@object=new{id="cs_test_fixture",@object="checkout.session"}}});
 [Fact] public void Webhook_RequiresValidSignature_AndRejectsTamperingAndOldTimestamp()
 {var raw=Payload();var now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();Assert.NotNull(PaymentWebhookVerifier.Verify(raw,Sign(raw,now),Secret));Assert.ThrowsAny<Exception>(()=>PaymentWebhookVerifier.Verify(raw+" ",Sign(raw,now),Secret));Assert.ThrowsAny<Exception>(()=>PaymentWebhookVerifier.Verify(raw,Sign(raw,now-600),Secret));Assert.ThrowsAny<Exception>(()=>PaymentWebhookVerifier.Verify(raw,"bad",Secret));}
 [Fact] public void Webhook_RejectsSignedLiveEvents()
 {var raw=Payload(true);Assert.Throws<FormatException>(()=>PaymentWebhookVerifier.Verify(raw,Sign(raw,DateTimeOffset.UtcNow.ToUnixTimeSeconds()),Secret));}
}
