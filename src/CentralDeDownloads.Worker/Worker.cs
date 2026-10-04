using CentralDeDownloads.Core;
using System.Diagnostics;

namespace CentralDeDownloads.Worker;

public class Worker(IExportStore store, S3ExportStorage storage, IJobQueue queue,
    IConfiguration configuration, ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var indexesReady = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!indexesReady)
                {
                    await store.EnsureIndexesAsync(stoppingToken);
                    indexesReady = true;
                }
                var message = await queue.ReceiveAsync(stoppingToken);
                if (message is not null)
                {
                    try
                    {
                        await ProcessAsync(message.Body, stoppingToken);
                        await message.AcknowledgeAsync(CancellationToken.None);
                    }
                    catch
                    {
                        try { await message.ReleaseAsync(CancellationToken.None); }
                        catch (Exception releaseError)
                        { logger.LogError(releaseError, "Falha ao liberar mensagem da fila"); }
                        throw;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Falha no consumo da fila");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task ProcessAsync(string id, CancellationToken stoppingToken)
    {
        var job = await store.ClaimAsync(id, stoppingToken);
        if (job is null) return; // mensagem duplicada ou estado final
        logger.LogInformation("Gerando {JobId} em {Format}", job.Id, job.Format);
        var deadline = (job.ClosedAt ?? DateTime.UtcNow).AddMinutes(30);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(deadline - DateTime.UtcNow > TimeSpan.Zero
            ? deadline - DateTime.UtcNow : TimeSpan.FromMilliseconds(1));
        var key = S3ExportStorage.ObjectKey(job.Id);
        try
        {
            if (job.Columns.Count == 0) throw new InvalidOperationException("Arquivo sem colunas.");
            var partMiB = configuration.GetValue("Aws:UploadPartMiB", 64);
            if (partMiB is < 5 or > 512)
                throw new InvalidOperationException("Aws:UploadPartMiB deve ficar entre 5 e 512.");
            var started = Stopwatch.GetTimestamp();
            var upload = await storage.UploadGeneratedAsync(job,
                store.ReadBatchesAsync(job.Id, timeout.Token), key, ContentType(job.Format),
                timeout.Token, partMiB * 1024 * 1024);
            var generationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (!await store.MarkReadyAsync(job.Id, key, upload.Bytes, timeout.Token))
            {
                await storage.DeleteObjectAsync(key, CancellationToken.None);
                throw new InvalidOperationException("O trabalho mudou de estado durante a geração.");
            }
            try { await store.DeleteBatchesAsync(job.Id, CancellationToken.None); }
            catch (Exception cleanupError) { logger.LogError(cleanupError, "Falha na limpeza dos lotes de {JobId}", job.Id); }
            logger.LogInformation("Arquivo {JobId} pronto com {Bytes} bytes em {Parts} partes; geracao_ms={GenerationMs:F0}; upload_s3_ms={UploadMs}",
                job.Id, upload.Bytes, upload.Parts, generationMilliseconds, upload.S3UploadMilliseconds);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha ao gerar {JobId}", job.Id);
            try { await storage.DeleteObjectAsync(key, CancellationToken.None); }
            catch (Exception cleanupError) { logger.LogError(cleanupError, "Falha ao apagar arquivo parcial {JobId}", job.Id); }
            await store.FailAsync(job.Id,
                stoppingToken.IsCancellationRequested ? "Processamento interrompido pelo encerramento do worker." :
                timeout.IsCancellationRequested ? "Prazo de 30 minutos excedido." : "Falha interna na geração.",
                CancellationToken.None);
            await store.DeleteBatchesAsync(job.Id, CancellationToken.None);
        }
    }

    private static string ContentType(string format) => format switch
    {
        "csv" => "text/csv; charset=utf-8",
        "json" => "application/json",
        _ => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
    };
}
