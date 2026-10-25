using Npgsql;
using ShelfSync.Api.Models;
using static ShelfSync.Api.Services.TransactionalOperation;
namespace ShelfSync.Api.Services;
public sealed record CartRequest(ReservationRequest[]? Items);
public sealed class CartReservationService(TransactionalOperation operations)
{
    public Task<ReservationResult> ReserveAsync(CartRequest request,Guid key,Guid owner,CancellationToken ct=default)
    {
        if(request.Items is null||request.Items.Length is <1 or >30||request.Items.Any(i=>i is null||i.ProductId==Guid.Empty||i.Quantity is <1 or >100)||request.Items.Select(i=>i.ProductId).Distinct().Count()!=request.Items.Length)
            return Task.FromResult(Error(400,"A cart needs 1–30 distinct products with quantities of 1–100."));
        var items=request.Items.OrderBy(i=>i.ProductId).ToArray();
        return operations.RunAsync("RESERVE_CART",key,new{owner,items},async(c,tx)=>
        {
            // Deterministic inventory lock ordering prevents competing carts from deadlocking.
            var prices=new Dictionary<Guid,int>();
            foreach(var item in items)
            {
                await using var cmd=new NpgsqlCommand("SELECT p.price_cents FROM inventory i JOIN products p ON p.id=i.product_id WHERE i.product_id=$1 FOR UPDATE OF i",c,tx);
                cmd.Parameters.AddWithValue(item.ProductId);var price=await cmd.ExecuteScalarAsync(ct);if(price is null)return Error(404,"A cart product was not found.");prices[item.ProductId]=(int)price;
            }
            await using var clock=new NpgsqlCommand("SELECT clock_timestamp()",c,tx);var now=(DateTime)(await clock.ExecuteScalarAsync(ct))!;
            var cartId=Guid.NewGuid();var receipts=new List<object>();long totalCents=0;
            foreach(var item in items)
            {
                await using(var available=new NpgsqlCommand("SELECT i.quarantined, GREATEST(0,i.shelf_units::bigint-i.safety_buffer-COALESCE((SELECT SUM(quantity) FROM orders WHERE product_id=$1 AND state='RESERVED' AND expires_at>$2),0)) FROM inventory i WHERE product_id=$1",c,tx))
                {available.Parameters.AddWithValue(item.ProductId);available.Parameters.AddWithValue(now);await using var r=await available.ExecuteReaderAsync(ct);await r.ReadAsync(ct);if(r.GetBoolean(0)||r.GetInt64(1)<item.Quantity)return Error(409,"A cart item is unavailable. No items were reserved.");}
                var id=Guid.NewGuid();var expiry=now.AddMinutes(15);
                await using(var cmd=new NpgsqlCommand("INSERT INTO orders(id,product_id,quantity,unit_price_cents,state,expires_at,created_at,updated_at,owner_user_id,cart_id) VALUES($1,$2,$3,$4,'RESERVED',$5,$6,$6,$7,$8)",c,tx))
                {cmd.Parameters.AddWithValue(id);cmd.Parameters.AddWithValue(item.ProductId);cmd.Parameters.AddWithValue(item.Quantity);cmd.Parameters.AddWithValue(prices[item.ProductId]);cmd.Parameters.AddWithValue(expiry);cmd.Parameters.AddWithValue(now);cmd.Parameters.AddWithValue(owner);cmd.Parameters.AddWithValue(cartId);await cmd.ExecuteNonQueryAsync(ct);}
                await using(var cmd=new NpgsqlCommand("INSERT INTO inventory_events(product_id,order_id,kind,metadata) VALUES($1,$2,'RESERVE',jsonb_build_object('cartId',$3::text))",c,tx))
                {cmd.Parameters.AddWithValue(item.ProductId);cmd.Parameters.AddWithValue(id);cmd.Parameters.AddWithValue(cartId.ToString());await cmd.ExecuteNonQueryAsync(ct);}
                totalCents+=(long)prices[item.ProductId]*item.Quantity;
                receipts.Add(new{orderId=id,productId=item.ProductId,quantity=item.Quantity,unitPriceCents=prices[item.ProductId],state="RESERVED",expiresAt=expiry});
            }
            // A preview message is committed with the reservation; it never sends external email.
            await using(var mail=new NpgsqlCommand("""INSERT INTO mail_preview(id,user_id,recipient,subject,body) SELECT $1,"Id",COALESCE("Email","UserName"),'Reservation confirmed',$3 FROM app_users WHERE "Id"=$2""",c,tx))
            {mail.Parameters.AddWithValue(Guid.NewGuid());mail.Parameters.AddWithValue(owner);mail.Parameters.AddWithValue($"Reservation {cartId}: {items.Length} product lines, total {totalCents/100m:0.00} USD. Held for 15 minutes. This is a reservation, not a payment receipt.");await mail.ExecuteNonQueryAsync(ct);}
            return Result(201,new{cartId,totalCents,orders=receipts});
        },ct);
    }
}
