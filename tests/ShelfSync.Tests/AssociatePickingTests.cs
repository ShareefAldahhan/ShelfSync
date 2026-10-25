using System.Text.Json;
using Npgsql;
using ShelfSync.Api.Services;
namespace ShelfSync.Tests;
public sealed class AssociatePickingTests
{
 static async Task<Guid> Reserve(PostgresTestScope f,Guid? owner=null)
 {var r=await new ReservationService(f.Db).CreateAsync(new(f.ProductId,2),f.NewRequestKey(),ownerUserId:owner);Assert.Equal(201,r.StatusCode);return JsonDocument.Parse(r.Json).RootElement.GetProperty("orderId").GetGuid();}
 static async Task AddRole(PostgresTestScope f,Guid user,string role)
 {
  await using var c=await f.Db.OpenConnectionAsync();
  await using(var roleCommand=new NpgsqlCommand("""INSERT INTO app_roles("Id","Name","NormalizedName") VALUES($1,$2,upper($2)) ON CONFLICT("NormalizedName") DO NOTHING""",c))
  {roleCommand.Parameters.AddWithValue(Guid.NewGuid());roleCommand.Parameters.AddWithValue(role);await roleCommand.ExecuteNonQueryAsync();}
  await using(var assignment=new NpgsqlCommand("""INSERT INTO app_user_roles("UserId","RoleId") SELECT $1,"Id" FROM app_roles WHERE "Name"=$2""",c))
  {assignment.Parameters.AddWithValue(user);assignment.Parameters.AddWithValue(role);await assignment.ExecuteNonQueryAsync();}

 }
 [Fact] public async Task Associate_CannotPickOwnerlessReservation_AndStockDoesNotMove()
 {await using var f=await PostgresTestScope.CreateAsync();var order=await Reserve(f);var r=await new OrderPickingService(f.Db).PickAsync(order,f.NewRequestKey(),requireCustomerReservation:true);Assert.Equal(409,r.StatusCode);await using var cmd=f.Db.CreateCommand("SELECT shelf_units,picked_units FROM inventory WHERE product_id=$1");cmd.Parameters.AddWithValue(f.ProductId);await using var reader=await cmd.ExecuteReaderAsync();await reader.ReadAsync();Assert.Equal(10,reader.GetInt32(0));Assert.Equal(0,reader.GetInt32(1));}
 [Fact] public async Task Associate_CannotPickStaffOwnedReservation_OrReplayManagerKey()
 {await using var f=await PostgresTestScope.CreateAsync();var staff=await f.NewAccountAsync();await AddRole(f,staff,"Associate");var order=await Reserve(f,staff);var service=new OrderPickingService(f.Db);Assert.Equal(409,(await service.PickAsync(order,f.NewRequestKey(),requireCustomerReservation:true)).StatusCode);var adminKey=f.NewRequestKey();Assert.Equal(200,(await service.PickAsync(order,adminKey)).StatusCode);Assert.Equal(409,(await service.PickAsync(order,adminKey,requireCustomerReservation:true)).StatusCode);}
 [Fact] public async Task Associate_CanPickCustomerReservation_AndRetryWithoutMovingStockTwice()
 {await using var f=await PostgresTestScope.CreateAsync();var customer=await f.NewAccountAsync();await AddRole(f,customer,"Customer");var order=await Reserve(f,customer);var key=f.NewRequestKey();var service=new OrderPickingService(f.Db);Assert.Equal(200,(await service.PickAsync(order,key,requireCustomerReservation:true)).StatusCode);Assert.Equal(200,(await service.PickAsync(order,key,requireCustomerReservation:true)).StatusCode);await using var cmd=f.Db.CreateCommand("SELECT picked_units FROM inventory WHERE product_id=$1");cmd.Parameters.AddWithValue(f.ProductId);Assert.Equal(2,(int)(await cmd.ExecuteScalarAsync())!);}
}
