using Stripe;
using System.Text.Json;
namespace ShelfSync.Api.Payments;
public static class PaymentWebhookVerifier
{
    public static (string EventId,string SessionId)? Verify(string raw,string signature,string secret)
    {
        EventUtility.ValidateSignature(raw,signature,secret,300);
        using var doc=JsonDocument.Parse(raw);var root=doc.RootElement;
        if(root.GetProperty("livemode").GetBoolean()||root.TryGetProperty("account",out _))throw new FormatException("Unsupported payment context.");
        var type=root.GetProperty("type").GetString();
        if(type is not ("checkout.session.completed" or "checkout.session.async_payment_succeeded" or "checkout.session.async_payment_failed" or "checkout.session.expired"))return null;
        var eventId=root.GetProperty("id").GetString();var obj=root.GetProperty("data").GetProperty("object");
        if(eventId is null||!eventId.StartsWith("evt_",StringComparison.Ordinal)||eventId.Length>255||obj.GetProperty("object").GetString()!="checkout.session")throw new FormatException("Invalid payment event.");
        var session=obj.GetProperty("id").GetString();if(session is null||!session.StartsWith("cs_test_",StringComparison.Ordinal)||session.Length>255)throw new FormatException("Invalid checkout session.");
        return (eventId,session);
    }
}
