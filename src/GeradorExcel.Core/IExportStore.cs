namespace GeradorExcel.Core;

public interface IExportStore
{
    Task EnsureIndexesAsync(CancellationToken ct);
    Task CreateAsync(ExportJob job, CancellationToken ct);
    Task<ExportJob?> GetAsync(string id, CancellationToken ct);
    Task<List<ExportJob>> ListAsync(int page, int size, string? status, CancellationToken ct);
    Task<(ExportBatch? Batch, string? Error)> AddBatchAsync(BatchInput input, CancellationToken ct);
    Task<ExportJob?> BeginCloseAsync(string id, FinishRequest? request, CancellationToken ct);
    Task<ExportJob?> FinalizeCloseAsync(string id, CancellationToken ct);
    Task<List<ExportJob>> FindClosingAsync(CancellationToken ct);
    Task<List<ExportJob>> FindQueuedForDispatchAsync(CancellationToken ct);
    Task MarkEnqueuedAsync(string id, CancellationToken ct);
    Task<ExportJob?> ClaimAsync(string id, CancellationToken ct);
    Task<bool> MarkReadyAsync(string id, string key, long bytes, CancellationToken ct);
    Task<bool> FailAsync(string id, string error, CancellationToken ct);
    IAsyncEnumerable<ExportBatch> ReadBatchesAsync(string id, CancellationToken ct);
    Task DeleteBatchesAsync(string id, CancellationToken ct);
    Task<List<ExportJob>> FindStaleReceivingAsync(DateTime threshold, CancellationToken ct);
    Task<List<ExportJob>> FindOverdueAsync(DateTime threshold, CancellationToken ct);
    Task<List<ExportJob>> FindExpiredFilesAsync(DateTime now, CancellationToken ct);
    Task<List<ExportJob>> FindTerminalForBatchCleanupAsync(CancellationToken ct);
    Task<List<ExportJob>> FindFailedWithFileAsync(CancellationToken ct);
    Task ClearFailedFileAsync(string id, CancellationToken ct);
    Task MarkExpiredAsync(string id, CancellationToken ct);
    Task DeleteOldHistoryAsync(DateTime threshold, CancellationToken ct);
    Task<List<ExportJob>> FindUnnotifiedAsync(CancellationToken ct);
    Task MarkNotifiedAsync(string id, string? error, CancellationToken ct);
}
