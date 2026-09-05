namespace BackBurner.Server;

public sealed class LeaseExpiryService(StateStore stateStore, ILogger<LeaseExpiryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var expired = await stateStore.ExpireLeasesAsync(stoppingToken);
                if (expired > 0)
                {
                    logger.LogWarning("Requeued {Count} job(s) after worker lease expiration.", expired);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Lease expiration sweep failed.");
            }
        }
    }
}
