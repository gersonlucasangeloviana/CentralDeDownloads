using GeradorExcel.Core;

public sealed class FileCleanupService(IExportStore store, S3ExportStorage storage,
    ILogger<FileCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromDays(1);
            try
            {
                while (true)
                {
                    var jobs = await store.FindExpiredFilesAsync(DateTime.UtcNow, stoppingToken);
                    if (jobs.Count == 0) break;
                    foreach (var job in jobs)
                    {
                        if (job.S3Key is not null)
                            await storage.DeleteObjectAsync(job.S3Key, stoppingToken);
                        await store.MarkExpiredAsync(job.Id, stoppingToken);
                    }
                }
                await store.DeleteOldHistoryAsync(DateTime.UtcNow.AddDays(-30), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Falha na limpeza diária dos arquivos");
                delay = TimeSpan.FromSeconds(10);
            }
            await Task.Delay(delay, stoppingToken);
        }
    }
}
