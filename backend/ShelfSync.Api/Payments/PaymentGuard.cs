using Npgsql;
namespace ShelfSync.Api.Payments;
public static class PaymentGuard
{
    // Call only while holding the order row lock. Payment updates take the same lock.
    public static async Task<string?> CancellationError(NpgsqlConnection c,NpgsqlTransaction tx,Guid order,CancellationToken ct)
    {
        await using var cmd=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM order_payment_status WHERE linked_order_id=$1 AND state IN ('READY','OPEN','PROCESSING','PAID','REVIEW'))",c,tx);
        cmd.Parameters.AddWithValue(order);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!?"An active or paid checkout exists. Reconcile/expire checkout or refund through Stripe before cancelling. Stock returns do not issue refunds.":null;
    }
    public static async Task<string?> FulfillmentError(NpgsqlConnection c,NpgsqlTransaction tx,Guid order,bool requirePayment,CancellationToken ct)
    {
        await using var cmd=new NpgsqlCommand("SELECT o.owner_user_id,p.state FROM orders o LEFT JOIN LATERAL(SELECT state FROM order_payment_status WHERE linked_order_id=o.id ORDER BY created_at DESC LIMIT 1)p ON true WHERE o.id=$1",c,tx);
        cmd.Parameters.AddWithValue(order);await using var r=await cmd.ExecuteReaderAsync(ct);await r.ReadAsync(ct);
        var state=r.IsDBNull(1)?null:r.GetString(1);
        if(state is not null&&state!="PAID")return "Payment is not confirmed. Do not hand over this order.";
        if(requirePayment&&!r.IsDBNull(0)&&state!="PAID")return "Customer payment is required before fulfillment.";
        return null;
    }
}
