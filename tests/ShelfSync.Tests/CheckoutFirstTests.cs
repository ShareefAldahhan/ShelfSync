using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Npgsql;
using ShelfSync.Api.Payments;
using ShelfSync.Api.Repositories;
using ShelfSync.Api.Services;
namespace ShelfSync.Tests;
public sealed class CheckoutFirstTests
{
 static IConfiguration Config()=>new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Stripe:Enabled","true"},{"Stripe:SecretKey","sk_test_fixture"},{"Stripe:WebhookSecret","whsec_fixture"}}).Build();
 sealed class Gateway:IStripeGateway
 {
  public ConcurrentDictionary<Guid,CheckoutSnapshot> Sessions=new();public bool Fail;
  public Task<CheckoutSnapshot> CreateAsync(Guid payment,Guid order,long amount,DateTime expiry,CancellationToken ct)
  {if(Fail)throw new Stripe.StripeException("Fixture outage");return Task.FromResult(Sessions.GetOrAdd(payment,p=>new("cs_test_"+p.ToString("N"),"https://checkout.stripe.com/test","open","unpaid",amount,"usd",false,p)));}
  public Task<CheckoutSnapshot> GetAsync(string session,CancellationToken ct)=>Task.FromResult(Sessions.Values.Single(s=>s.Id==session));
  public Task<CheckoutSnapshot> ExpireAsync(string session,CancellationToken ct){var current=Sessions.Values.Single(s=>s.Id==session);var expired=current with{Status="expired"};Sessions[current.PaymentId]=expired;return Task.FromResult(expired);}
 }
 static PaymentService Service(PostgresTestScope f,Gateway g)=>new(f.Db,g,new(Config()));
 static async Task<Guid> Customer(PostgresTestScope f)
 {
  var user=await f.NewAccountAsync();
  await using(var cmd=f.Db.CreateCommand("""INSERT INTO app_roles("Id","Name","NormalizedName") VALUES($1,'Customer','CUSTOMER') ON CONFLICT("NormalizedName") DO NOTHING""")){cmd.Parameters.AddWithValue(Guid.NewGuid());await cmd.ExecuteNonQueryAsync();}
  await using(var cmd=f.Db.CreateCommand("""INSERT INTO app_user_roles("UserId","RoleId") SELECT $1,"Id" FROM app_roles WHERE "Name"='Customer'""")){cmd.Parameters.AddWithValue(user);await cmd.ExecuteNonQueryAsync();}
  return user;
 }
 static async Task<Guid[]> Cart(PostgresTestScope a,PostgresTestScope b,Guid owner)
 {var result=await new CartReservationService(new(a.Db)).ReserveAsync(new([new(a.ProductId,2),new(b.ProductId,1)]),a.NewRequestKey(),owner);Assert.Equal(201,result.StatusCode);return JsonDocument.Parse(result.Json).RootElement.GetProperty("orders").EnumerateArray().Select(o=>o.GetProperty("orderId").GetGuid()).ToArray();}
 static async Task<string> State(PostgresTestScope f,Guid order){await using var cmd=f.Db.CreateCommand("SELECT state FROM orders WHERE id=$1");cmd.Parameters.AddWithValue(order);return (string)(await cmd.ExecuteScalarAsync())!;}
 [Fact] public async Task OneCartPayment_ConfirmsEveryLine_AndOnlyThenAllowsPicking()
 {
  await using var a=await PostgresTestScope.CreateAsync();await using var b=await PostgresTestScope.CreateAsync();var owner=await Customer(a);var ids=await Cart(a,b,owner);var g=new Gateway();var service=Service(a,g);
  Assert.Equal(409,(await new OrderPickingService(a.Db,Config()).PickAsync(ids[0],a.NewRequestKey(),requireCustomerReservation:true)).StatusCode);
  Assert.Equal(200,(await service.CheckoutAsync(ids[0],owner)).StatusCode);Assert.Equal(200,(await service.CheckoutAsync(ids[1],owner)).StatusCode);Assert.Single(g.Sessions);var snapshot=g.Sessions.Values.Single();Assert.Equal(3000,snapshot.Amount);
  foreach(var id in ids)Assert.Equal("RESERVED",await State(a,id));
  await service.ApplyAsync(snapshot with{Status="complete",PaymentStatus="paid"},"evt_cart_paid");
  foreach(var id in ids){Assert.Equal("PAID",JsonDocument.Parse((await service.StatusAsync(id,owner,false)).Json).RootElement.GetProperty("state").GetString());Assert.Equal(200,(await new OrderPickingService(a.Db,Config()).PickAsync(id,a.NewRequestKey(),requireCustomerReservation:true)).StatusCode);}
  var receipt=JsonDocument.Parse(await new StoreOperationsRepository(a.Db).ReceiptAsync(ids[1],owner));Assert.Equal(3000,receipt.RootElement.GetProperty("checkoutTotalCents").GetInt64());Assert.Equal(2,receipt.RootElement.GetProperty("checkoutItems").GetArrayLength());
 }
 [Fact] public async Task AssociateQueue_HidesUnpaidOrders_AndShowsEveryPaidCartLine()
 {await using var a=await PostgresTestScope.CreateAsync();await using var b=await PostgresTestScope.CreateAsync();var owner=await Customer(a);var ids=await Cart(a,b,owner);var repository=new OrderRepository(a.Db);Assert.DoesNotContain(await repository.GetRecentAsync(requirePaid:true,paidQueue:true),o=>ids.Contains(o.Id));var g=new Gateway();var service=Service(a,g);await service.CheckoutAsync(ids[0],owner);await service.ApplyAsync(g.Sessions.Values.Single() with{Status="complete",PaymentStatus="paid"},"evt_queue_paid");var queue=await repository.GetRecentAsync(requirePaid:true,paidQueue:true);foreach(var id in ids)Assert.Contains(queue,o=>o.Id==id&&o.CanPick);}
 [Fact] public async Task CancelCheckout_ExpiresProviderFirst_AndReleasesEveryCartHold()
 {await using var a=await PostgresTestScope.CreateAsync();await using var b=await PostgresTestScope.CreateAsync();var owner=await Customer(a);var ids=await Cart(a,b,owner);var service=Service(a,new Gateway());await service.CheckoutAsync(ids[0],owner);Assert.Equal(409,(await new ReservationCancellationService(a.Db).CancelAsync(ids[1],a.NewRequestKey(),customerId:owner)).StatusCode);Assert.Equal(200,(await service.CancelCheckoutAsync(ids[1],owner)).StatusCode);foreach(var id in ids)Assert.Equal("EXPIRED",await State(a,id));Assert.Equal(201,(await new ReservationService(a.Db).CreateAsync(new(a.ProductId,10),a.NewRequestKey())).StatusCode);}
 [Fact] public async Task ProviderExpiry_ReleasesHold_AndLateSuccessRequiresReview()
 {await using var a=await PostgresTestScope.CreateAsync();await using var b=await PostgresTestScope.CreateAsync();var owner=await Customer(a);var ids=await Cart(a,b,owner);var g=new Gateway();var service=Service(a,g);await service.CheckoutAsync(ids[0],owner);var session=g.Sessions.Values.Single();await service.ApplyAsync(session with{Status="expired"},"evt_expiry");foreach(var id in ids)Assert.Equal("EXPIRED",await State(a,id));await service.ApplyAsync(session with{Status="complete",PaymentStatus="paid"},"evt_late_paid");Assert.Equal("REVIEW",JsonDocument.Parse((await service.StatusAsync(ids[0],owner,false)).Json).RootElement.GetProperty("state").GetString());}
 [Fact] public async Task FailedAsyncPayment_ReleasesHold_WhileProcessingDoesNot()
 {await using var a=await PostgresTestScope.CreateAsync();await using var b=await PostgresTestScope.CreateAsync();var owner=await Customer(a);var ids=await Cart(a,b,owner);var g=new Gateway();var service=Service(a,g);await service.CheckoutAsync(ids[0],owner);var session=g.Sessions.Values.Single();await service.ApplyAsync(session with{Status="complete",IntentStatus="processing"},"evt_processing");await new ReservationExpirationService(a.Db).ExpireBatchAsync(productId:a.ProductId);foreach(var id in ids)Assert.Equal("RESERVED",await State(a,id));await service.ApplyAsync(session with{Status="complete",IntentStatus="requires_payment_method"},"evt_failed");foreach(var id in ids)Assert.Equal("EXPIRED",await State(a,id));}
 [Fact] public async Task ProviderOutage_RetryReusesCartAndPaymentIntent()
 {await using var a=await PostgresTestScope.CreateAsync();await using var b=await PostgresTestScope.CreateAsync();var owner=await Customer(a);var ids=await Cart(a,b,owner);var g=new Gateway{Fail=true};var service=Service(a,g);Assert.Equal(503,(await service.CheckoutAsync(ids[0],owner)).StatusCode);await new ReservationExpirationService(a.Db).ExpireBatchAsync(productId:a.ProductId);foreach(var id in ids)Assert.Equal("RESERVED",await State(a,id));g.Fail=false;Assert.Equal(200,(await service.CheckoutAsync(ids[1],owner)).StatusCode);Assert.Single(g.Sessions);}
 [Fact] public async Task ExpiredReservation_CannotCreateProviderCheckout()
 {await using var a=await PostgresTestScope.CreateAsync();var owner=await Customer(a);var result=await new ReservationService(a.Db).CreateAsync(new(a.ProductId,1),a.NewRequestKey(),ownerUserId:owner);var id=JsonDocument.Parse(result.Json).RootElement.GetProperty("orderId").GetGuid();await using(var cmd=a.Db.CreateCommand("UPDATE orders SET created_at=clock_timestamp()-INTERVAL '2 minutes',expires_at=clock_timestamp()-INTERVAL '1 minute' WHERE id=$1")){cmd.Parameters.AddWithValue(id);await cmd.ExecuteNonQueryAsync();}var g=new Gateway();Assert.Equal(409,(await Service(a,g).CheckoutAsync(id,owner)).StatusCode);Assert.Empty(g.Sessions);}
 [Fact] public async Task PendingPayment_CannotBeReportedShort_OrCancelledByAnotherCustomer()
 {await using var a=await PostgresTestScope.CreateAsync();await using var b=await PostgresTestScope.CreateAsync();var owner=await Customer(a);var ids=await Cart(a,b,owner);var service=Service(a,new Gateway());await service.CheckoutAsync(ids[0],owner);Assert.Equal(409,(await new OrderShortageService(a.Db).ReportAsync(ids[0],a.NewRequestKey())).StatusCode);Assert.Equal(409,(await service.CancelCheckoutAsync(ids[0],Guid.NewGuid())).StatusCode);foreach(var id in ids)Assert.Equal("RESERVED",await State(a,id));}
}
