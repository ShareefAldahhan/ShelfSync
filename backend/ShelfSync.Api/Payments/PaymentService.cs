using Npgsql;
using ShelfSync.Api.Services;
using static ShelfSync.Api.Services.TransactionalOperation;
namespace ShelfSync.Api.Payments;

public sealed class PaymentService(NpgsqlDataSource db,IStripeGateway stripe,PaymentSettings settings)
{
    public async Task<ReservationResult> CheckoutAsync(Guid order,Guid owner,CancellationToken ct=default,bool returnPaidResult=false)
    {
        if(!settings.Enabled)return Error(503,"Online test payments are not configured.");
        Guid id,anchor;long amount=0;string? session=null;DateTime created;
        await using(var c=await db.OpenConnectionAsync(ct))
        await using(var tx=await c.BeginTransactionAsync(ct))
        {
            Guid? cart;
            await using(var cmd=new NpgsqlCommand("SELECT cart_id FROM orders WHERE id=$1 AND owner_user_id=$2",c,tx))
            {cmd.Parameters.AddWithValue(order);cmd.Parameters.AddWithValue(owner);await using var r=await cmd.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct))return Error(404,"Order not found.");cart=r.IsDBNull(0)?null:r.GetGuid(0);}
            // Keep pre-upgrade, individually charged cart lines separate; never merge an existing payment.
            if(cart is not null)
            {await using var cmd=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM order_payment_status p JOIN orders o ON o.id=p.linked_order_id WHERE o.cart_id=$1 AND p.cart_id IS NULL)",c,tx);cmd.Parameters.AddWithValue(cart.Value);if((bool)(await cmd.ExecuteScalarAsync(ct))!)cart=null;}
            var ids=new List<Guid>();
            await using(var cmd=new NpgsqlCommand("SELECT id FROM orders WHERE owner_user_id=$2 AND (id=$1 OR ($3::uuid IS NOT NULL AND cart_id=$3)) ORDER BY id",c,tx))
            {cmd.Parameters.AddWithValue(order);cmd.Parameters.AddWithValue(owner);cmd.Parameters.Add(new(){NpgsqlDbType=NpgsqlTypes.NpgsqlDbType.Uuid,Value=(object?)cart??DBNull.Value});await using var r=await cmd.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))ids.Add(r.GetGuid(0));}
            anchor=ids[0];
            await LockOrdersAsync(c,tx,ids.ToArray(),ct);
            await using var clock=new NpgsqlCommand("SELECT clock_timestamp()",c,tx);var now=(DateTime)(await clock.ExecuteScalarAsync(ct))!;
            await using(var cmd=new NpgsqlCommand("SELECT id,state,quantity::bigint*unit_price_cents,expires_at FROM orders WHERE id=ANY($1) ORDER BY id",c,tx))
            {cmd.Parameters.AddWithValue(ids.ToArray());await using var r=await cmd.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct)){var state=r.GetString(1);if(state is not ("RESERVED" or "PICKED"))return Error(409,"This order is no longer available for checkout.");if(state=="RESERVED"&&r.GetDateTime(3)<=now)return Error(409,"The checkout hold expired. Start a new cart checkout.");amount+=r.GetInt64(2);}}
            if(amount is <50 or >99999999)return Error(409,"Order total is outside the supported checkout range.");
            await using(var cmd=new NpgsqlCommand("SELECT id,stripe_session_id,state,created_at FROM payments WHERE order_id=$1 AND state IN ('READY','OPEN','PROCESSING','PAID','REVIEW','REFUNDED') ORDER BY created_at DESC LIMIT 1",c,tx))
            {
                cmd.Parameters.AddWithValue(anchor);await using var r=await cmd.ExecuteReaderAsync(ct);
                if(await r.ReadAsync(ct)){id=r.GetGuid(0);session=r.IsDBNull(1)?null:r.GetString(1);created=r.GetDateTime(3);if(r.GetString(2)=="PAID"&&returnPaidResult)return Result(200,new{paid=true,orderId=anchor});if(r.GetString(2) is "PAID" or "REFUNDED")return Error(409,"This order is already paid or refunded.");if(r.GetString(2) is "PROCESSING" or "REVIEW")return Error(409,"Payment confirmation is pending. Do not pay again.");}
                else{id=Guid.NewGuid();created=now;}
            }
            await using(var cmd=new NpgsqlCommand("INSERT INTO payments(id,order_id,owner_user_id,amount_cents,currency,state,created_at,cart_id) VALUES($1,$2,$3,$4,'usd','READY',$5,$6) ON CONFLICT(id) DO NOTHING",c,tx))
            {cmd.Parameters.AddWithValue(id);cmd.Parameters.AddWithValue(anchor);cmd.Parameters.AddWithValue(owner);cmd.Parameters.AddWithValue(amount);cmd.Parameters.AddWithValue(created);cmd.Parameters.Add(new(){NpgsqlDbType=NpgsqlTypes.NpgsqlDbType.Uuid,Value=(object?)cart??DBNull.Value});await cmd.ExecuteNonQueryAsync(ct);}
            foreach(var line in ids){await using var cmd=new NpgsqlCommand("INSERT INTO payment_orders(payment_id,order_id) VALUES($1,$2) ON CONFLICT DO NOTHING",c,tx);cmd.Parameters.AddWithValue(id);cmd.Parameters.AddWithValue(line);await cmd.ExecuteNonQueryAsync(ct);}
            // Once provider creation might have happened, only provider-confirmed expiry/failure may release these holds.
            await using(var cmd=new NpgsqlCommand("UPDATE orders SET expires_at='infinity',updated_at=clock_timestamp() WHERE id=ANY($1) AND state='RESERVED'",c,tx))
            {cmd.Parameters.AddWithValue(ids.ToArray());await cmd.ExecuteNonQueryAsync(ct);}
            await tx.CommitAsync(ct);
        }
        if(session is null&&created<DateTime.UtcNow.AddHours(-20))return Error(409,"This payment needs administrator reconciliation before retrying.");
        CheckoutSnapshot snapshot;
        try{snapshot=session is null?await stripe.CreateAsync(id,anchor,amount,created.AddHours(1),ct):await stripe.GetAsync(session,ct);}
        catch(Stripe.StripeException){return Error(503,"Payment provider is temporarily unavailable. Retry the saved checkout.");}
        if(!Match(snapshot,id,amount)||!await ApplyAsync(snapshot,null,ct))return Error(502,"Payment provider response could not be verified.");
        if(snapshot.PaymentStatus=="paid")return Result(200,new{paid=true,orderId=anchor});
        if(snapshot.Status!="open")return Error(409,"Checkout is no longer open. Refresh payment status.");
        if(!ValidCheckoutUrl(snapshot.Url))return Error(502,"Invalid hosted checkout destination.");
        return Result(200,new{url=snapshot.Url,testMode=true,orderId=anchor});
    }
    static async Task LockOrdersAsync(NpgsqlConnection c,NpgsqlTransaction tx,Guid[] ids,CancellationToken ct)
    {
        // Every multi-item flow locks inventory, then orders, in stable database order.
        await using(var cmd=new NpgsqlCommand("SELECT product_id FROM inventory WHERE product_id IN(SELECT product_id FROM orders WHERE id=ANY($1)) ORDER BY product_id FOR UPDATE",c,tx))
        {cmd.Parameters.AddWithValue(ids);await using var r=await cmd.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct)){} }
        await using(var cmd=new NpgsqlCommand("SELECT id FROM orders WHERE id=ANY($1) ORDER BY id FOR UPDATE",c,tx))
        {cmd.Parameters.AddWithValue(ids);await using var r=await cmd.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct)){} }
    }
    public static bool ValidCheckoutUrl(string? url)=>Uri.TryCreate(url,UriKind.Absolute,out var u)&&u.Scheme=="https"&&u.Host=="checkout.stripe.com"&&string.IsNullOrEmpty(u.UserInfo);
    static bool Match(CheckoutSnapshot s,Guid id,long amount)=>!s.Live&&s.PaymentId==id&&s.Currency=="usd"&&s.Amount==amount&&s.Id.StartsWith("cs_test_",StringComparison.Ordinal);
    public async Task<bool> ApplyAsync(CheckoutSnapshot snapshot,string? eventId,CancellationToken ct=default)
    {
        await using var c=await db.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
        var ids=new List<Guid>();
        await using(var lookup=new NpgsqlCommand("SELECT order_id FROM payment_orders WHERE payment_id=$1 ORDER BY order_id",c,tx))
        {lookup.Parameters.AddWithValue(snapshot.PaymentId);await using var r=await lookup.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))ids.Add(r.GetGuid(0));}
        if(ids.Count==0)return false;
        await LockOrdersAsync(c,tx,ids.ToArray(),ct);
        var states=new List<string>();
        await using(var cmd=new NpgsqlCommand("SELECT state FROM orders WHERE id=ANY($1)",c,tx))
        {cmd.Parameters.AddWithValue(ids.ToArray());await using var r=await cmd.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))states.Add(r.GetString(0));}
        string prior;string? session;long amount;
        await using(var cmd=new NpgsqlCommand("SELECT amount_cents,state,stripe_session_id FROM payments WHERE id=$1 FOR UPDATE",c,tx))
        {cmd.Parameters.AddWithValue(snapshot.PaymentId);await using var r=await cmd.ExecuteReaderAsync(ct);await r.ReadAsync(ct);amount=r.GetInt64(0);prior=r.GetString(1);session=r.IsDBNull(2)?null:r.GetString(2);}
        if(!Match(snapshot,snapshot.PaymentId,amount)||(session is not null&&session!=snapshot.Id))return false;
        if(eventId is not null)
        {
            await using var cmd=new NpgsqlCommand("INSERT INTO payment_events(event_id,payment_id,kind) VALUES($1,$2,'CHECKOUT_SYNC') ON CONFLICT DO NOTHING",c,tx);
            cmd.Parameters.AddWithValue(eventId);cmd.Parameters.AddWithValue(snapshot.PaymentId);if(await cmd.ExecuteNonQueryAsync(ct)==0)return true;
        }
        var state=snapshot.Refunded>=amount?"REFUNDED":snapshot.Refunded>0?"REVIEW":snapshot.PaymentStatus=="paid"?(states.All(s=>s is "RESERVED" or "PICKED" or "FULFILLED")?"PAID":"REVIEW")
            :snapshot.Status=="complete"&&snapshot.IntentStatus is ("canceled" or "requires_payment_method")?"FAILED":snapshot.Status=="expired"?"EXPIRED":snapshot.Status=="complete"?"PROCESSING":"OPEN";
        // Terminal payment success cannot be downgraded by reordered/duplicate unpaid notifications.
        if(prior=="REFUNDED")state=prior;
        else if(prior is "PAID" or "REVIEW" && snapshot.Refunded==0)state=prior;
        await using(var cmd=new NpgsqlCommand("UPDATE payments SET stripe_session_id=$2,checkout_url=$3,state=$4,updated_at=clock_timestamp() WHERE id=$1",c,tx))
        {cmd.Parameters.AddWithValue(snapshot.PaymentId);cmd.Parameters.AddWithValue(snapshot.Id);cmd.Parameters.Add(new(){NpgsqlDbType=NpgsqlTypes.NpgsqlDbType.Text,Value=(object?)snapshot.Url??DBNull.Value});cmd.Parameters.AddWithValue(state);await cmd.ExecuteNonQueryAsync(ct);}
        if(state is "EXPIRED" or "FAILED" or "REFUNDED")
        {
            var target=state=="REFUNDED"?"CANCELLED":"EXPIRED";
            await using var release=new NpgsqlCommand("WITH released AS (UPDATE orders SET state=$2,reason=$3,updated_at=clock_timestamp() WHERE id=ANY($1) AND state='RESERVED' RETURNING id,product_id) INSERT INTO inventory_events(product_id,order_id,kind,metadata) SELECT product_id,id,CASE WHEN $2='CANCELLED' THEN 'CANCEL' ELSE 'EXPIRE' END,jsonb_build_object('reason',$3::text,'paymentId',$4::text) FROM released",c,tx);
            release.Parameters.AddWithValue(ids.ToArray());release.Parameters.AddWithValue(target);release.Parameters.AddWithValue("Checkout "+state.ToLowerInvariant());release.Parameters.AddWithValue(snapshot.PaymentId.ToString());await release.ExecuteNonQueryAsync(ct);
        }
        if(state=="PAID"&&prior!="PAID")
        {
            await using var mail=new NpgsqlCommand("""INSERT INTO mail_preview(id,user_id,recipient,subject,body) SELECT $1,u."Id",COALESCE(u."Email",u."UserName"),'Payment confirmed — preparing pickup',$2 FROM payments p JOIN app_users u ON u."Id"=p.owner_user_id WHERE p.id=$3""",c,tx);
            mail.Parameters.AddWithValue(Guid.NewGuid());mail.Parameters.AddWithValue("Test payment confirmed. Your paid pickup order is now ready for staff to pick. No real money was collected.");mail.Parameters.AddWithValue(snapshot.PaymentId);await mail.ExecuteNonQueryAsync(ct);
        }
        if(state!=prior)
        {await using var cmd=new NpgsqlCommand("""INSERT INTO access_audit("Id","TargetUserId","Action","Detail","CreatedAt") SELECT $1,owner_user_id,'PAYMENT_STATUS',$2,clock_timestamp() FROM payments WHERE id=$3""",c,tx);cmd.Parameters.AddWithValue(Guid.NewGuid());cmd.Parameters.AddWithValue($"Payment {snapshot.PaymentId}: {state} (Stripe test mode)");cmd.Parameters.AddWithValue(snapshot.PaymentId);await cmd.ExecuteNonQueryAsync(ct);}
        await tx.CommitAsync(ct);return true;
    }
    public async Task<ReservationResult> CancelCheckoutAsync(Guid order,Guid owner,CancellationToken ct=default,bool returnPaidResult=false)
    {
        if(!settings.Enabled)return Error(503,"Online test payments are not configured.");
        string? session;
        await using(var cmd=db.CreateCommand("SELECT p.stripe_session_id FROM order_payment_status p JOIN orders o ON o.id=p.linked_order_id WHERE o.id=$1 AND o.owner_user_id=$2 ORDER BY p.created_at DESC LIMIT 1"))
        {cmd.Parameters.AddWithValue(order);cmd.Parameters.AddWithValue(owner);session=(string?)await cmd.ExecuteScalarAsync(ct);}
        if(session is null)return Error(409,"Checkout must be reconciled before cancellation. Retry checkout to recover its provider session.");
        try
        {
            var current=await stripe.GetAsync(session,ct);
            if(current.Status=="open")current=await stripe.ExpireAsync(session,ct);
            if(!await ApplyAsync(current,null,ct))return Error(502,"Checkout status could not be verified.");
            if(current.Status!="expired")return Error(409,"Payment is already processing or confirmed; checkout cannot be cancelled.");
            return Result(200,new{cancelled=true});
        }
        catch(Stripe.StripeException){return Error(503,"Unable to confirm cancellation. Your hold remains protected; retry safely.");}
    }
    public async Task ReconcilePendingAsync(CancellationToken ct)
    {
        if(!settings.Enabled)return;
        var pending=new List<(Guid Id,Guid Order,long Amount,string? Session,DateTime Created)>();
        await using(var cmd=db.CreateCommand("SELECT id,order_id,amount_cents,stripe_session_id,created_at FROM payments WHERE state IN ('READY','OPEN','PROCESSING') AND updated_at<clock_timestamp()-INTERVAL '45 seconds' ORDER BY updated_at LIMIT 20"))
        {await using var r=await cmd.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))pending.Add((r.GetGuid(0),r.GetGuid(1),r.GetInt64(2),r.IsDBNull(3)?null:r.GetString(3),r.GetDateTime(4)));}
        foreach(var p in pending)
        {
            if(p.Session is null&&p.Created<DateTime.UtcNow.AddHours(-20))continue;
            try{var snapshot=p.Session is null?await stripe.CreateAsync(p.Id,p.Order,p.Amount,p.Created.AddHours(1),ct):await stripe.GetAsync(p.Session,ct);await ApplyAsync(snapshot,null,ct);}
            catch(Stripe.StripeException){/* Provider outage: retain the hold and retry on the next sweep. */}
        }
    }
    public async Task<ReservationResult> ReconcileAsync(Guid payment,string session,CancellationToken ct=default)
    {
        if(!settings.Enabled)return Error(503,"Stripe test checkout is not configured.");
        if(string.IsNullOrWhiteSpace(session)||!session.StartsWith("cs_test_",StringComparison.Ordinal)||session.Length>255)return Error(400,"A Stripe test Checkout Session ID is required.");
        try
        {
            var snapshot=await stripe.GetAsync(session,ct);
            if(snapshot.PaymentId!=payment||!await ApplyAsync(snapshot,null,ct))return Error(409,"Session does not match the recorded payment.");
            return Result(200,new{updated=true});
        }
        catch(Stripe.StripeException){return Error(503,"Payment provider could not be queried.");}
    }
    public async Task<ReservationResult> RefreshAsync(Guid order,Guid user,bool staff,CancellationToken ct=default)
    {
        if(!settings.Enabled)return Error(503,"Stripe test checkout is not configured.");
        string? session;
        await using(var cmd=db.CreateCommand("SELECT p.stripe_session_id FROM order_payment_status p JOIN orders o ON o.id=p.linked_order_id WHERE o.id=$1 AND (o.owner_user_id=$2 OR $3) ORDER BY p.created_at DESC LIMIT 1"))
        {cmd.Parameters.AddWithValue(order);cmd.Parameters.AddWithValue(user);cmd.Parameters.AddWithValue(staff);session=(string?)await cmd.ExecuteScalarAsync(ct);}
        if(session is null)return Error(409,"No provider session exists yet. Retry checkout or contact the administrator.");
        try{if(!await ApplyAsync(await stripe.GetAsync(session,ct),null,ct))return Error(502,"Payment could not be verified.");}
        catch(Stripe.StripeException){return Error(503,"Payment provider is temporarily unavailable.");}
        return await StatusAsync(order,user,staff,ct);
    }
    public async Task<ReservationResult> StatusAsync(Guid order,Guid user,bool staff,CancellationToken ct=default)
    {
        await using var cmd=db.CreateCommand("SELECT p.id,p.state,p.amount_cents,p.currency,p.stripe_session_id,o.owner_user_id=$2 FROM orders o LEFT JOIN LATERAL(SELECT * FROM order_payment_status WHERE linked_order_id=o.id ORDER BY created_at DESC LIMIT 1)p ON true WHERE o.id=$1 AND (o.owner_user_id=$2 OR $3)");
        cmd.Parameters.AddWithValue(order);cmd.Parameters.AddWithValue(user);cmd.Parameters.AddWithValue(staff);await using var r=await cmd.ExecuteReaderAsync(ct);
        if(!await r.ReadAsync(ct))return Error(404,"Order not found.");
        return r.IsDBNull(0)?Result(200,new{state="NOT_STARTED",testMode=true,enabled=settings.Enabled,canPay=!r.IsDBNull(5)&&r.GetBoolean(5)}):Result(200,new{state=r.GetString(1),amountCents=r.GetInt64(2),currency=r.GetString(3),testMode=true,enabled=settings.Enabled,canPay=!r.IsDBNull(5)&&r.GetBoolean(5)});
    }
}
