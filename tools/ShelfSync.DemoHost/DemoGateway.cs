using System.Collections.Concurrent;
using ShelfSync.Api.Payments;

namespace ShelfSync.DemoHost;

// This implementation lives only in the demo host, never the release API.
public sealed class DemoGateway : IStripeGateway
{
    public sealed record Session(Guid Order, CheckoutSnapshot Snapshot);
    public ConcurrentDictionary<string, Session> Sessions { get; } = new();
    public Task<CheckoutSnapshot> CreateAsync(Guid payment, Guid order, long amount, DateTime expiresAt, CancellationToken ct)
    {
        var id = "cs_test_demo_" + payment.ToString("N");
        return Task.FromResult(Sessions.GetOrAdd(id, _ => new(order,
            new(id, "https://checkout.stripe.com/c/pay/" + id, "open", "unpaid", amount, "usd", false, payment))).Snapshot);
    }
    public Task<CheckoutSnapshot> GetAsync(string session, CancellationToken ct) => Task.FromResult(Sessions[session].Snapshot);
    public Task<CheckoutSnapshot> ExpireAsync(string session, CancellationToken ct)
    {
        var changed = Sessions.AddOrUpdate(session, _ => throw new InvalidOperationException(), (_, existing) =>
            existing.Snapshot.PaymentStatus == "paid" ? existing : existing with { Snapshot = existing.Snapshot with { Status = "expired" } });
        return Task.FromResult(changed.Snapshot);
    }
    public Session Confirm(string session) => Sessions.AddOrUpdate(session, _ => throw new InvalidOperationException(),
        (_, existing) => existing with { Snapshot = existing.Snapshot with { Status = "complete", PaymentStatus = "paid" } });
}
