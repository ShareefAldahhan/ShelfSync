
namespace ShelfSync.Api.Services;

public sealed class ReservationExpirationWorker(
	ReservationExpirationService expirationService,
	ILogger<ReservationExpirationWorker> logger
) : BackgroundService
{
	private static readonly TimeSpan PollInterval =
		TimeSpan.FromSeconds(10);

	protected override async Task ExecuteAsync(
		CancellationToken stoppingToken)
	{
		logger.LogInformation(
			"Reservation expiration worker started.");

		// Process overdue reservations immediately on startup.
		await ProcessSafelyAsync(stoppingToken);

		using var timer = new PeriodicTimer(PollInterval);

		try
		{
			while (await timer.WaitForNextTickAsync(stoppingToken))
			{
				await ProcessSafelyAsync(stoppingToken);
			}
		}
		catch (OperationCanceledException)
			when (stoppingToken.IsCancellationRequested)
		{
			// Normal application shutdown.
		}
	}

	private async Task ProcessSafelyAsync(CancellationToken ct)
	{
		try
		{
			var expiredCount =
				await expirationService.ExpireBatchAsync(ct);

			if (expiredCount > 0)
			{
				logger.LogInformation(
					"Expired {Count} reservations.",
					expiredCount);
			}
		}
		catch (OperationCanceledException)
			when (ct.IsCancellationRequested)
		{
			// Normal application shutdown.
		}
		catch (Exception ex)
		{
			logger.LogError(
				ex,
				"Reservation expiration sweep failed.");
		}
	}
}
