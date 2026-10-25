namespace ShelfSync.Api.Payments;
public sealed class PaymentReconciliationWorker(IServiceScopeFactory scopes,ILogger<PaymentReconciliationWorker> logger):BackgroundService
{
 protected override async Task ExecuteAsync(CancellationToken ct)
 {
  using var timer=new PeriodicTimer(TimeSpan.FromSeconds(60));
  try{while(await timer.WaitForNextTickAsync(ct))
  {try{await using var scope=scopes.CreateAsyncScope();await scope.ServiceProvider.GetRequiredService<PaymentService>().ReconcilePendingAsync(ct);}
   catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
   catch{logger.LogWarning("Payment reconciliation was unavailable. Holds remain protected; the next sweep will retry.");}}}
  catch(OperationCanceledException)when(ct.IsCancellationRequested){}
 }
}
