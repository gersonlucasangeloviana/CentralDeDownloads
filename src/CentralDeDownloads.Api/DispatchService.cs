using CentralDeDownloads.Core;

public sealed class DispatchService(IExportStore store, IJobQueue queue,
    ILogger<DispatchService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var job in await store.FindClosingAsync(stoppingToken))
                    await store.FinalizeCloseAsync(job.Id, stoppingToken);
                foreach (var job in await store.FindQueuedForDispatchAsync(stoppingToken))
                {
                    await queue.PublishAsync(job.Id, stoppingToken);
                    await store.MarkEnqueuedAsync(job.Id, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Falha ao despachar arquivos"); }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}
