using GeradorExcel.Core;

public sealed class NotificationService(IExportStore store, NotificationSender sender,
    ILogger<NotificationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var jobs = await store.FindUnnotifiedAsync(stoppingToken);
                await Parallel.ForEachAsync(jobs, new ParallelOptions
                { MaxDegreeOfParallelism = 4, CancellationToken = stoppingToken }, async (job, ct) =>
                {
                    var error = await sender.SendAsync(job, ct);
                    await store.MarkNotifiedAsync(job.Id, error, ct);
                });
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Falha ao enviar notificações"); }
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }
}
