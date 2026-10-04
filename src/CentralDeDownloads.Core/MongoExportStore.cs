using System.Runtime.CompilerServices;
using MongoDB.Driver;

namespace CentralDeDownloads.Core;

public sealed class MongoExportStore : IExportStore
{
    private readonly IMongoCollection<ExportJob> _jobs;
    private readonly IMongoCollection<ExportBatch> _batches;

    public MongoExportStore(string connection, string database)
    {
        var db = new MongoClient(connection).GetDatabase(database);
        _jobs = db.GetCollection<ExportJob>("arquivos");
        _batches = db.GetCollection<ExportBatch>("lotes");
    }

    public async Task EnsureIndexesAsync(CancellationToken ct)
    {
        await _jobs.Indexes.CreateManyAsync([
            new CreateIndexModel<ExportJob>(Builders<ExportJob>.IndexKeys.Descending(x => x.CreatedAt)),
            new CreateIndexModel<ExportJob>(Builders<ExportJob>.IndexKeys.Ascending(x => x.Status).Ascending(x => x.LastActivityAt)),
            new CreateIndexModel<ExportJob>(Builders<ExportJob>.IndexKeys.Ascending(x => x.ExpiresAt))
        ], ct);
        await _batches.Indexes.CreateOneAsync(new CreateIndexModel<ExportBatch>(
            Builders<ExportBatch>.IndexKeys.Ascending(x => x.JobId).Ascending(x => x.Sequence),
            new CreateIndexOptions { Unique = true }), cancellationToken: ct);
    }

    public Task CreateAsync(ExportJob job, CancellationToken ct) => _jobs.InsertOneAsync(job, cancellationToken: ct);

    public async Task<ExportJob?> GetAsync(string id, CancellationToken ct) =>
        await _jobs.Find(x => x.Id == id).FirstOrDefaultAsync(ct);

    public async Task<List<ExportJob>> ListAsync(int page, int size, string? status, CancellationToken ct)
    {
        var filter = string.IsNullOrWhiteSpace(status)
            ? Builders<ExportJob>.Filter.Empty
            : Builders<ExportJob>.Filter.Eq(x => x.Status, status);
        return await _jobs.Find(filter).SortByDescending(x => x.CreatedAt)
            .Skip((page - 1) * size).Limit(size).ToListAsync(ct);
    }

    public async Task<(ExportBatch? Batch, string? Error)> AddBatchAsync(BatchInput input, CancellationToken ct)
    {
        var filter = Builders<ExportJob>.Filter.Eq(x => x.Id, input.JobId) &
                     Builders<ExportJob>.Filter.Eq(x => x.Status, JobStatus.Receiving);
        var update = Builders<ExportJob>.Update.Inc(x => x.PendingBatches, 1)
            .Inc(x => x.NextSequence, 1).Set(x => x.LastActivityAt, DateTime.UtcNow);
        var job = await _jobs.FindOneAndUpdateAsync(filter, update,
            new FindOneAndUpdateOptions<ExportJob> { ReturnDocument = ReturnDocument.After }, ct);
        if (job is null) return (null, "Arquivo inexistente ou encerrado.");

        var batch = new ExportBatch
        {
            JobId = input.JobId,
            Sequence = job.NextSequence,
            BatchId = input.BatchId,
            DataJson = input.DataJson,
            ItemCount = input.Count
        };
        try
        {
            if (batch.Sequence == 1)
                await _jobs.UpdateOneAsync(x => x.Id == input.JobId,
                    Builders<ExportJob>.Update.Set(x => x.Columns, input.Columns), cancellationToken: ct);
            await _batches.InsertOneAsync(batch, cancellationToken: ct);
            await _jobs.UpdateOneAsync(x => x.Id == input.JobId,
                Builders<ExportJob>.Update.Inc(x => x.BatchCount, 1)
                    .Inc(x => x.ItemCount, input.Count).Inc(x => x.PendingBatches, -1)
                    .Set(x => x.BatchesCleaned, false), cancellationToken: ct);
            return (batch, null);
        }
        catch
        {
            await FailAsync(input.JobId, "Falha ao persistir um lote.", CancellationToken.None);
            await DeleteBatchesAsync(input.JobId, CancellationToken.None);
            throw;
        }
    }

    public async Task<ExportJob?> BeginCloseAsync(string id, FinishRequest? request, CancellationToken ct)
    {
        var filter = Builders<ExportJob>.Filter.Eq(x => x.Id, id) &
                     Builders<ExportJob>.Filter.Eq(x => x.Status, JobStatus.Receiving);
        var update = Builders<ExportJob>.Update.Set(x => x.Status, JobStatus.Closing)
            .Set(x => x.ClosedAt, DateTime.UtcNow)
            .Set(x => x.ExpectedBatches, request?.TotalLotes)
            .Set(x => x.ExpectedItems, request?.TotalItens);
        var job = await _jobs.FindOneAndUpdateAsync(filter, update,
            new FindOneAndUpdateOptions<ExportJob> { ReturnDocument = ReturnDocument.After }, ct);
        return job ?? await GetAsync(id, ct);
    }

    public async Task<ExportJob?> FinalizeCloseAsync(string id, CancellationToken ct)
    {
        var job = await GetAsync(id, ct);
        if (job is null || job.Status != JobStatus.Closing || job.PendingBatches != 0) return job;
        var mismatch = job.BatchCount == 0 ? "Arquivo sem lotes." :
            job.ExpectedBatches is not null && job.ExpectedBatches != job.BatchCount ? "Total de lotes divergente." :
            job.ExpectedItems is not null && job.ExpectedItems != job.ItemCount ? "Total de itens divergente." : null;
        var filter = Builders<ExportJob>.Filter.Eq(x => x.Id, id) &
                     Builders<ExportJob>.Filter.Eq(x => x.Status, JobStatus.Closing) &
                     Builders<ExportJob>.Filter.Eq(x => x.PendingBatches, 0);
        var update = mismatch is null
            ? Builders<ExportJob>.Update.Set(x => x.Status, JobStatus.Queued)
                .Set(x => x.Format, ExportFormatPolicy.AfterClose(job.Format, job.ItemCount))
            : Builders<ExportJob>.Update.Set(x => x.Status, JobStatus.Failed).Set(x => x.Error, mismatch);
        var result = await _jobs.UpdateOneAsync(filter, update, cancellationToken: ct);
        if (result.ModifiedCount > 0 && mismatch is not null) await DeleteBatchesAsync(id, ct);
        return await GetAsync(id, ct);
    }

    public Task<List<ExportJob>> FindClosingAsync(CancellationToken ct) =>
        _jobs.Find(x => x.Status == JobStatus.Closing && x.PendingBatches == 0).Limit(50).ToListAsync(ct);

    public Task<List<ExportJob>> FindQueuedForDispatchAsync(CancellationToken ct) =>
        _jobs.Find(x => x.Status == JobStatus.Queued && x.LastEnqueuedAt == null)
            .Limit(50).ToListAsync(ct);

    public Task MarkEnqueuedAsync(string id, CancellationToken ct) =>
        _jobs.UpdateOneAsync(x => x.Id == id && x.Status == JobStatus.Queued,
            Builders<ExportJob>.Update.Set(x => x.LastEnqueuedAt, DateTime.UtcNow), cancellationToken: ct);

    public async Task<ExportJob?> ClaimAsync(string id, CancellationToken ct) =>
        await _jobs.FindOneAndUpdateAsync(x => x.Id == id && x.Status == JobStatus.Queued,
            Builders<ExportJob>.Update.Set(x => x.Status, JobStatus.Processing)
                .Set(x => x.StartedAt, DateTime.UtcNow)
                .Set(x => x.S3Key, S3ExportStorage.ObjectKey(id)),
            new FindOneAndUpdateOptions<ExportJob> { ReturnDocument = ReturnDocument.After }, ct);

    public async Task<bool> MarkReadyAsync(string id, string key, long bytes, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var result = await _jobs.UpdateOneAsync(x => x.Id == id && x.Status == JobStatus.Processing,
            Builders<ExportJob>.Update.Set(x => x.Status, JobStatus.Ready)
                .Set(x => x.ReadyAt, now).Set(x => x.ExpiresAt, now.AddHours(24))
                .Set(x => x.S3Key, key).Set(x => x.FileBytes, bytes), cancellationToken: ct);
        return result.ModifiedCount > 0;
    }

    public async Task<bool> FailAsync(string id, string error, CancellationToken ct)
    {
        var filter = Builders<ExportJob>.Filter.Eq(x => x.Id, id) &
                     Builders<ExportJob>.Filter.Nin(x => x.Status,
                         [JobStatus.Ready, JobStatus.Failed, JobStatus.Expired]);
        var result = await _jobs.UpdateOneAsync(filter,
            Builders<ExportJob>.Update.Set(x => x.Status, JobStatus.Failed)
                .Set(x => x.Error, error).Set(x => x.PendingBatches, 0), cancellationToken: ct);
        return result.ModifiedCount > 0;
    }

    public async IAsyncEnumerable<ExportBatch> ReadBatchesAsync(string id,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var cursor = await _batches.Find(x => x.JobId == id)
            .SortBy(x => x.Sequence).ToCursorAsync(ct);
        while (await cursor.MoveNextAsync(ct))
            foreach (var batch in cursor.Current) yield return batch;
    }

    public async Task DeleteBatchesAsync(string id, CancellationToken ct)
    {
        await _batches.DeleteManyAsync(x => x.JobId == id, ct);
        await _jobs.UpdateOneAsync(x => x.Id == id,
            Builders<ExportJob>.Update.Set(x => x.BatchesCleaned, true), cancellationToken: ct);
    }

    public Task<List<ExportJob>> FindStaleReceivingAsync(DateTime threshold, CancellationToken ct) =>
        _jobs.Find(x => x.Status == JobStatus.Receiving && x.LastActivityAt < threshold)
            .Limit(100).ToListAsync(ct);

    public Task<List<ExportJob>> FindOverdueAsync(DateTime threshold, CancellationToken ct) =>
        _jobs.Find(x => (x.Status == JobStatus.Queued || x.Status == JobStatus.Processing ||
            x.Status == JobStatus.Closing) && x.ClosedAt < threshold).Limit(100).ToListAsync(ct);

    public Task<List<ExportJob>> FindExpiredFilesAsync(DateTime now, CancellationToken ct) =>
        _jobs.Find(x => x.Status == JobStatus.Ready && x.ExpiresAt < now).Limit(100).ToListAsync(ct);

    public Task<List<ExportJob>> FindTerminalForBatchCleanupAsync(CancellationToken ct) =>
        _jobs.Find(x => !x.BatchesCleaned && (x.Status == JobStatus.Ready ||
            x.Status == JobStatus.Failed || x.Status == JobStatus.Expired)).Limit(100).ToListAsync(ct);

    public Task<List<ExportJob>> FindFailedWithFileAsync(CancellationToken ct) =>
        _jobs.Find(x => x.Status == JobStatus.Failed && x.S3Key != null).Limit(100).ToListAsync(ct);

    public Task ClearFailedFileAsync(string id, CancellationToken ct) =>
        _jobs.UpdateOneAsync(x => x.Id == id && x.Status == JobStatus.Failed,
            Builders<ExportJob>.Update.Set(x => x.S3Key, null), cancellationToken: ct);

    public Task MarkExpiredAsync(string id, CancellationToken ct) =>
        _jobs.UpdateOneAsync(x => x.Id == id && x.Status == JobStatus.Ready,
            Builders<ExportJob>.Update.Set(x => x.Status, JobStatus.Expired)
                .Set(x => x.S3Key, null), cancellationToken: ct);

    public Task DeleteOldHistoryAsync(DateTime threshold, CancellationToken ct) =>
        _jobs.DeleteManyAsync(x => x.CreatedAt < threshold &&
            (x.Status == JobStatus.Failed || x.Status == JobStatus.Expired), ct);

    public Task<List<ExportJob>> FindUnnotifiedAsync(CancellationToken ct) =>
        _jobs.Find(x => !x.NotificationSent && (x.Status == JobStatus.Ready || x.Status == JobStatus.Failed))
            .Limit(50).ToListAsync(ct);

    public Task MarkNotifiedAsync(string id, string? error, CancellationToken ct) =>
        _jobs.UpdateOneAsync(x => x.Id == id,
            Builders<ExportJob>.Update.Set(x => x.NotificationSent, true)
                .Set(x => x.NotificationError, error), cancellationToken: ct);
}
