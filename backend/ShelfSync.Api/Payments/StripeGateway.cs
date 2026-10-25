using Stripe;
using Stripe.Checkout;
namespace ShelfSync.Api.Payments;

public sealed class PaymentSettings(IConfiguration configuration)
{
    public string SecretKey => configuration["Stripe:SecretKey"] ?? "";
    public string WebhookSecret => configuration["Stripe:WebhookSecret"] ?? "";
    public string BaseUrl => (configuration["Stripe:BaseUrl"] ?? "http://localhost:5000").TrimEnd('/');
    public bool Enabled => configuration.GetValue<bool>("Stripe:Enabled") && SecretKey.StartsWith("sk_test_",StringComparison.Ordinal)
        && WebhookSecret.StartsWith("whsec_",StringComparison.Ordinal) && ValidBaseUrl(BaseUrl);
    public static bool ValidBaseUrl(string value) => Uri.TryCreate(value,UriKind.Absolute,out var u)
        && string.IsNullOrEmpty(u.UserInfo) && string.IsNullOrEmpty(u.Query) && string.IsNullOrEmpty(u.Fragment)
        && u.AbsolutePath=="/" && (u.Scheme=="https" || (u.Scheme=="http"&&u.IsLoopback));
}
public sealed record CheckoutSnapshot(string Id,string? Url,string Status,string PaymentStatus,long Amount,string Currency,bool Live,Guid PaymentId,string? IntentStatus=null,long Refunded=0);
public interface IStripeGateway
{
    Task<CheckoutSnapshot> CreateAsync(Guid payment,Guid order,long amount,DateTime expiresAt,CancellationToken ct);
    Task<CheckoutSnapshot> GetAsync(string session,CancellationToken ct);
    Task<CheckoutSnapshot> ExpireAsync(string session,CancellationToken ct)=>throw new NotSupportedException();
}
public sealed class StripeGateway(PaymentSettings settings):IStripeGateway
{
    SessionService Sessions()=>new(new StripeClient(settings.SecretKey));
    public async Task<CheckoutSnapshot> CreateAsync(Guid payment,Guid order,long amount,DateTime expiresAt,CancellationToken ct)
    {
        // Omit payment_method_types: Stripe shows methods enabled in the merchant dashboard and eligible for this transaction.
        var options=new SessionCreateOptions{
            Mode="payment",ClientReferenceId=payment.ToString(),
            Metadata=new(){{"payment_id",payment.ToString()},{"order_id",order.ToString()}},
            SuccessUrl=settings.BaseUrl+"/?payment=returned#payment/"+order,
            CancelUrl=settings.BaseUrl+"/?payment=cancelled#payment/"+order,
            ExpiresAt=expiresAt,
            LineItems=[new(){Quantity=1,PriceData=new(){Currency="usd",UnitAmount=amount,ProductData=new(){Name="ShelfSync pickup order "+order}}}]
        };
        return Snapshot(await Sessions().CreateAsync(options,new RequestOptions{IdempotencyKey="shelfsync-payment-"+payment},ct));
    }
    public async Task<CheckoutSnapshot> GetAsync(string session,CancellationToken ct)=>Snapshot(await Sessions().GetAsync(session,new SessionGetOptions{Expand=["payment_intent.latest_charge"]},cancellationToken:ct));
    public async Task<CheckoutSnapshot> ExpireAsync(string session,CancellationToken ct)=>Snapshot(await Sessions().ExpireAsync(session,cancellationToken:ct));
    static CheckoutSnapshot Snapshot(Session s)=>new(s.Id,s.Url,s.Status,s.PaymentStatus,s.AmountTotal??-1,s.Currency,s.Livemode,
        s.Metadata is not null&&s.Metadata.TryGetValue("payment_id",out var id)&&Guid.TryParse(id,out var guid)?guid:Guid.Empty,s.PaymentIntent?.Status,s.PaymentIntent?.LatestCharge?.AmountRefunded??0);
}
