using System.Text.Json;
using Npgsql;
using ShelfSync.Api.Services;
namespace ShelfSync.Tests;
public sealed class ExpirationRaceTests
{
 [Fact] public async Task DeletedCandidate_IsSkippedAfterWaitingForInventoryLock()
 {
  await using var f=await PostgresTestScope.CreateAsync();
  var result=await new ReservationService(f.Db).CreateAsync(new(f.ProductId,1),f.NewRequestKey());
  var order=JsonDocument.Parse(result.Json).RootElement.GetProperty("orderId").GetGuid();
  await using(var age=f.Db.CreateCommand("UPDATE orders SET created_at=clock_timestamp()-INTERVAL '2 minutes',expires_at=clock_timestamp()-INTERVAL '1 minute' WHERE id=$1")){age.Parameters.AddWithValue(order);await age.ExecuteNonQueryAsync();}
  var name="expiry-race-"+Guid.NewGuid().ToString("N");
  var connection=new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SHELFSYNC_TEST_CONNECTION")){ApplicationName=name};
  await using var workerDb=NpgsqlDataSource.Create(connection.ConnectionString);
  await using var held=await f.Db.OpenConnectionAsync();await using var tx=await held.BeginTransactionAsync();
  await using(var take=new NpgsqlCommand("SELECT product_id FROM inventory WHERE product_id=$1 FOR UPDATE",held,tx)){take.Parameters.AddWithValue(f.ProductId);await take.ExecuteScalarAsync();}
  using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
  var sweep=new ReservationExpirationService(workerDb).ExpireBatchAsync(timeout.Token,f.ProductId);
  var blocked=false;
  for(var i=0;i<300;i++)
  {await using var activity=f.Db.CreateCommand("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE application_name=$1 AND wait_event_type='Lock')");activity.Parameters.AddWithValue(name);if((bool)(await activity.ExecuteScalarAsync())!){blocked=true;break;}await Task.Delay(20);}
  if(!blocked){await tx.RollbackAsync();await sweep;Assert.Fail("Expiry worker did not wait for the fixture's inventory lock.");}
  foreach(var sql in new[]{"DELETE FROM inventory_events WHERE order_id=$1","DELETE FROM orders WHERE id=$1"})
  {await using var remove=new NpgsqlCommand(sql,held,tx);remove.Parameters.AddWithValue(order);await remove.ExecuteNonQueryAsync();}
  await using(var remove=new NpgsqlCommand("DELETE FROM inventory WHERE product_id=$1",held,tx)){remove.Parameters.AddWithValue(f.ProductId);await remove.ExecuteNonQueryAsync();}
  await tx.CommitAsync();Assert.Equal(0,await sweep);
 }
}
