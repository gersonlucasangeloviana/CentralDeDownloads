using GeradorExcel.Core;

namespace GeradorExcel.Worker;

public class Worker(IExportStore store, S3ExportStorage storage, IJobQueue queue,
    ILogger<Worker> logger) : BackgroundService
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
        var path = Path.Combine(Path.GetTempPath(), $"gerador-{job.Id}-{Guid.NewGuid():N}.{job.Format}");
        var key = S3ExportStorage.ObjectKey(job.Id);
        var uploaded = false;
        try
        {
            if (job.Columns.Count == 0) throw new InvalidOperationException("Arquivo sem colunas.");
            var bytes = await ExportFileWriter.WriteAsync(job,
                store.ReadBatchesAsync(job.Id, timeout.Token), path, timeout.Token);
            await storage.UploadAsync(path, key, ContentType(job.Format), timeout.Token);
            uploaded = true;
            if (!await store.MarkReadyAsync(job.Id, key, bytes, timeout.Token))
            {
                await storage.DeleteObjectAsync(key, CancellationToken.None);
                throw new InvalidOperationException("O trabalho mudou de estado durante a geração.");
            }
            try { await store.DeleteBatchesAsync(job.Id, CancellationToken.None); }
            catch (Exception cleanupError) { logger.LogError(cleanupError, "Falha na limpeza dos lotes de {JobId}", job.Id); }
            logger.LogInformation("Arquivo {JobId} pronto com {Bytes} bytes", job.Id, bytes);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha ao gerar {JobId}", job.Id);
            if (uploaded)
            {
                try { await storage.DeleteObjectAsync(key, CancellationToken.None); }
                catch (Exception cleanupError) { logger.LogError(cleanupError, "Falha ao apagar arquivo parcial {JobId}", job.Id); }
            }
            await store.FailAsync(job.Id,
                stoppingToken.IsCancellationRequested ? "Processamento interrompido pelo encerramento do worker." :
                timeout.IsCancellationRequested ? "Prazo de 30 minutos excedido." : "Falha interna na geração.",
                CancellationToken.None);
            await store.DeleteBatchesAsync(job.Id, CancellationToken.None);
        }
        finally { try { File.Delete(path); } catch (IOException) { } }
    }

    private static string ContentType(string format) => format switch
    {
        "csv" => "text/csv; charset=utf-8",
        "json" => "application/json",
        _ => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
    };
}
