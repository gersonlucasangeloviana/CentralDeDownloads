using GeradorExcel.Core;

public sealed class JobCleanupService(IExportStore store, S3ExportStorage storage,
    ILogger<JobCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromHours(1);
            try
            {
                var now = DateTime.UtcNow;
                while (true)
                {
                    var jobs = await store.FindStaleReceivingAsync(now.AddMinutes(-30), stoppingToken);
                    if (jobs.Count == 0) break;
                    foreach (var job in jobs)
                        if (await store.FailAsync(job.Id, "Solicitação inativa há mais de 30 minutos.", stoppingToken))
                            await store.DeleteBatchesAsync(job.Id, stoppingToken);
                }
                while (true)
                {
                    var jobs = await store.FindOverdueAsync(now.AddMinutes(-30), stoppingToken);
                    if (jobs.Count == 0) break;
                    foreach (var job in jobs)
                        if (await store.FailAsync(job.Id, "Prazo de processamento de 30 minutos excedido.", stoppingToken))
                            await store.DeleteBatchesAsync(job.Id, stoppingToken);
                }
                while (true)
                {
                    var jobs = await store.FindTerminalForBatchCleanupAsync(stoppingToken);
                    if (jobs.Count == 0) break;
                    foreach (var job in jobs)
                        await store.DeleteBatchesAsync(job.Id, stoppingToken);
                }
                while (true)
                {
                    var jobs = await store.FindFailedWithFileAsync(stoppingToken);
                    if (jobs.Count == 0) break;
                    foreach (var job in jobs)
                    {
                        await storage.DeleteObjectAsync(job.S3Key!, stoppingToken);
                        await store.ClearFailedFileAsync(job.Id, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Falha na limpeza horária dos trabalhos");
                delay = TimeSpan.FromSeconds(10);
            }
            await Task.Delay(delay, stoppingToken);
        }
    }
}
