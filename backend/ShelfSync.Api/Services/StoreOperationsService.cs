using ShelfSync.Api.Payments;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using static ShelfSync.Api.Services.TransactionalOperation;
namespace ShelfSync.Api.Services;
public sealed record StockMovementRequest(Guid ProductId,int Quantity,string Reason,string? Reference);
public sealed record ReturnRequest(Guid SaleId,bool IsPos,int Quantity,string Destination,string Reason);
public sealed record PickedCancellationRequest(string Destination,string Reason);
public sealed class StoreOperationsService(TransactionalOperation operations)
{
    static bool Reason(string? value)=>!string.IsNullOrWhiteSpace(value)&&value.Trim().Length is >=3 and <=500;
    public Task<ReservationResult> MoveAsync(string action,StockMovementRequest request,Guid key,string actor,CancellationToken ct=default)
    {
        if(request.ProductId==Guid.Empty||request.Quantity is <1 or >100000||!Reason(request.Reason)||request.Reference?.Length>120)
            return Task.FromResult(Error(400,"Enter a product, quantity (1–100,000), reason and reference up to 120 characters."));
        return operations.RunAsync(action,key,new{request,actor},async(c,tx)=>
        {
            int shelf,backroom;
            await using(var cmd=new NpgsqlCommand("SELECT shelf_units,backroom_units FROM inventory WHERE product_id=$1 FOR UPDATE",c,tx))
            {cmd.Parameters.AddWithValue(request.ProductId);await using var r=await cmd.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct))return Error(404,"Product not found.");shelf=r.GetInt32(0);backroom=r.GetInt32(1);}
            var receive=action=="RECEIVE_BACKROOM";
            if(!receive&&backroom<request.Quantity)return Error(409,"Not enough backroom stock. Refresh before retrying.");
            var nextBackroom=backroom+(receive?request.Quantity:-request.Quantity);
            if(nextBackroom>1000000||(!receive&&(long)shelf+request.Quantity>1000000))return Error(409,"Stock exceeds supported limits.");
            await using(var cmd=new NpgsqlCommand("UPDATE inventory SET backroom_units=$2,shelf_units=shelf_units+$3,updated_at=clock_timestamp() WHERE product_id=$1",c,tx))
            {cmd.Parameters.AddWithValue(request.ProductId);cmd.Parameters.AddWithValue(nextBackroom);cmd.Parameters.AddWithValue(receive?0:request.Quantity);await cmd.ExecuteNonQueryAsync(ct);}
            await Audit(c,tx,request.ProductId,receive?"RECEIVE":"RESTOCK",receive?0:request.Quantity,0,new{operation=action,actor,request.Reason,request.Reference,backroomDelta=receive?request.Quantity:-request.Quantity},ct);
            return Result(200,new{updated=true});
        },ct);
    }
    public Task<ReservationResult> ReturnAsync(ReturnRequest request,Guid key,string actor,CancellationToken ct=default)
    {
        if(request.SaleId==Guid.Empty||request.Quantity is <1 or >100||!Reason(request.Reason)||request.Destination is not ("backroom" or "damaged"))return Task.FromResult(Error(400,"Enter a sale, quantity, destination and reason."));
        return operations.RunAsync("RETURN_SALE",key,new{request,actor},async(c,tx)=>
        {
            Guid product;
            await using(var lookup=new NpgsqlCommand(request.IsPos?"SELECT product_id FROM pos_events WHERE event_id=$1":"SELECT product_id FROM orders WHERE id=$1",c,tx))
            {lookup.Parameters.AddWithValue(request.SaleId);var value=await lookup.ExecuteScalarAsync(ct);if(value is null)return Error(404,"Sale not found.");product=(Guid)value;}
            await LockInventory(c,tx,product,ct);
            int sold;
            await using(var cmd=new NpgsqlCommand(request.IsPos?"SELECT quantity FROM pos_events WHERE event_id=$1 FOR UPDATE":"SELECT quantity FROM orders WHERE id=$1 AND state='FULFILLED' FOR UPDATE",c,tx))
            {cmd.Parameters.AddWithValue(request.SaleId);var value=await cmd.ExecuteScalarAsync(ct);if(value is null)return Error(409,"Only fulfilled orders can be returned.");sold=(int)value;}
            await using(var cmd=new NpgsqlCommand(request.IsPos?"SELECT COALESCE(SUM(quantity),0) FROM inventory_returns WHERE pos_event_id=$1":"SELECT COALESCE(SUM(quantity),0) FROM inventory_returns WHERE order_id=$1",c,tx))
            {cmd.Parameters.AddWithValue(request.SaleId);if((long)(await cmd.ExecuteScalarAsync(ct))!+request.Quantity>sold)return Error(409,"Return exceeds the remaining sold quantity.");}
            if(!await AddDestination(c,tx,product,request.Destination,request.Quantity,ct))return Error(409,"Destination stock exceeds supported limits.");
            await using(var cmd=new NpgsqlCommand("INSERT INTO inventory_returns(id,product_id,order_id,pos_event_id,quantity,destination,reason,actor) VALUES($1,$2,$3,$4,$5,$6,$7,$8)",c,tx))
            {cmd.Parameters.AddWithValue(Guid.NewGuid());cmd.Parameters.AddWithValue(product);cmd.Parameters.Add(new NpgsqlParameter{NpgsqlDbType=NpgsqlDbType.Uuid,Value=request.IsPos?DBNull.Value:request.SaleId});cmd.Parameters.Add(new NpgsqlParameter{NpgsqlDbType=NpgsqlDbType.Uuid,Value=request.IsPos?request.SaleId:DBNull.Value});cmd.Parameters.AddWithValue(request.Quantity);cmd.Parameters.AddWithValue(request.Destination);cmd.Parameters.AddWithValue(request.Reason.Trim());cmd.Parameters.AddWithValue(actor);await cmd.ExecuteNonQueryAsync(ct);}
            await Audit(c,tx,product,"RESTOCK",0,0,new{operation="SALE_RETURN",actor,request.SaleId,request.IsPos,request.Quantity,request.Destination,request.Reason},ct);
            return Result(200,new{returned=true});
        },ct);
    }
    public Task<ReservationResult> CancelPickedAsync(Guid orderId,PickedCancellationRequest request,Guid key,string actor,CancellationToken ct=default)
    {
        if(!Reason(request.Reason)||request.Destination is not ("backroom" or "damaged"))return Task.FromResult(Error(400,"Choose a destination and enter a reason."));
        return operations.RunAsync("CANCEL_PICKED",key,new{orderId,request,actor},async(c,tx)=>
        {
            Guid product;
            await using(var lookup=new NpgsqlCommand("SELECT product_id FROM orders WHERE id=$1",c,tx))
            {lookup.Parameters.AddWithValue(orderId);var value=await lookup.ExecuteScalarAsync(ct);if(value is null)return Error(404,"Order not found.");product=(Guid)value;}
            await LockInventory(c,tx,product,ct);
            int quantity;
            await using(var cmd=new NpgsqlCommand("SELECT quantity FROM orders WHERE id=$1 AND state='PICKED' FOR UPDATE",c,tx))
            {cmd.Parameters.AddWithValue(orderId);var value=await cmd.ExecuteScalarAsync(ct);if(value is null)return Error(409,"Order is no longer picked.");quantity=(int)value;}
            var paymentError=await PaymentGuard.CancellationError(c,tx,orderId,ct);if(paymentError is not null)return Error(409,paymentError);
            if(!await AddDestination(c,tx,product,request.Destination,quantity,ct))return Error(409,"Destination stock exceeds supported limits.");
            await using(var cmd=new NpgsqlCommand("UPDATE inventory SET picked_units=picked_units-$2,updated_at=clock_timestamp() WHERE product_id=$1 AND picked_units>=$2",c,tx))
            {cmd.Parameters.AddWithValue(product);cmd.Parameters.AddWithValue(quantity);if(await cmd.ExecuteNonQueryAsync(ct)!=1)return Error(409,"Picked stock is inconsistent; manager investigation required.");}
            await using(var cmd=new NpgsqlCommand("UPDATE orders SET state='CANCELLED',reason=$2,updated_at=clock_timestamp() WHERE id=$1",c,tx))
            {cmd.Parameters.AddWithValue(orderId);cmd.Parameters.AddWithValue(request.Reason.Trim());await cmd.ExecuteNonQueryAsync(ct);}
            await Audit(c,tx,product,"RESTOCK",0,-quantity,new{operation="CANCEL_PICKED",actor,orderId,quantity,request.Destination,request.Reason},ct);
            return Result(200,new{cancelled=true});
        },ct);
    }
    static async Task LockInventory(NpgsqlConnection c,NpgsqlTransaction tx,Guid product,CancellationToken ct)
    {await using var cmd=new NpgsqlCommand("SELECT product_id FROM inventory WHERE product_id=$1 FOR UPDATE",c,tx);cmd.Parameters.AddWithValue(product);await cmd.ExecuteScalarAsync(ct);}
    static async Task<bool> AddDestination(NpgsqlConnection c,NpgsqlTransaction tx,Guid product,string destination,int quantity,CancellationToken ct)
    {
        var column=destination=="backroom"?"backroom_units":"damaged_units";
        await using var cmd=new NpgsqlCommand($"UPDATE inventory SET {column}={column}+$2,updated_at=clock_timestamp() WHERE product_id=$1 AND {column}<=1000000-$2",c,tx);
        cmd.Parameters.AddWithValue(product);cmd.Parameters.AddWithValue(quantity);return await cmd.ExecuteNonQueryAsync(ct)==1;
    }
    internal static async Task Audit(NpgsqlConnection c,NpgsqlTransaction tx,Guid product,string kind,int shelf,int picked,object metadata,CancellationToken ct)
    {await using var cmd=new NpgsqlCommand("INSERT INTO inventory_events(product_id,kind,shelf_delta,picked_delta,metadata) VALUES($1,$2,$3,$4,$5::jsonb)",c,tx);cmd.Parameters.AddWithValue(product);cmd.Parameters.AddWithValue(kind);cmd.Parameters.AddWithValue(shelf);cmd.Parameters.AddWithValue(picked);cmd.Parameters.AddWithValue(JsonSerializer.Serialize(metadata,new JsonSerializerOptions(JsonSerializerDefaults.Web)));await cmd.ExecuteNonQueryAsync(ct);}
}
