using System.Runtime.CompilerServices;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace CentralDeDownloads.Core;

public sealed class PostgresExportStore : IExportStore, IDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresExportStore(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("Configure Postgres:ConnectionString.", nameof(connectionString));
        _dataSource = NpgsqlDataSource.Create(connectionString);
    }

    public void Dispose() => _dataSource.Dispose();

    public async Task EnsureIndexesAsync(CancellationToken ct)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS export_jobs (
                id text PRIMARY KEY,
                status text NOT NULL,
                created_at timestamptz NOT NULL,
                last_activity_at timestamptz NOT NULL,
                closed_at timestamptz,
                expires_at timestamptz,
                last_enqueued_at timestamptz,
                notification_sent boolean NOT NULL,
                batches_cleaned boolean NOT NULL,
                document jsonb NOT NULL
            );
            CREATE TABLE IF NOT EXISTS export_batches (
                id text PRIMARY KEY,
                job_id text NOT NULL REFERENCES export_jobs(id) ON DELETE CASCADE,
                sequence bigint NOT NULL,
                batch_id text,
                data_json jsonb NOT NULL,
                item_count integer NOT NULL,
                received_at timestamptz NOT NULL,
                UNIQUE (job_id, sequence)
            );
            CREATE INDEX IF NOT EXISTS ix_export_jobs_created ON export_jobs (created_at DESC);
            CREATE INDEX IF NOT EXISTS ix_export_jobs_status_activity ON export_jobs (status, last_activity_at);
            CREATE INDEX IF NOT EXISTS ix_export_jobs_status_closed ON export_jobs (status, closed_at);
            CREATE INDEX IF NOT EXISTS ix_export_jobs_status_expires ON export_jobs (status, expires_at);
            CREATE INDEX IF NOT EXISTS ix_export_batches_job_sequence ON export_batches (job_id, sequence);
            """;
        await using var cmd = _dataSource.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task CreateAsync(ExportJob job, CancellationToken ct)
    {
        await using var cmd = _dataSource.CreateCommand("""
            INSERT INTO export_jobs (id, status, created_at, last_activity_at, closed_at,
                expires_at, last_enqueued_at, notification_sent, batches_cleaned, document)
            VALUES (@id, @status, @created, @activity, @closed, @expires, @enqueued,
                @notified, @cleaned, @document)
            """);
        BindJob(cmd, job);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<ExportJob?> GetAsync(string id, CancellationToken ct)
    {
        await using var cmd = _dataSource.CreateCommand(
            "SELECT document::text FROM export_jobs WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        return await ReadOneAsync(cmd, ct);
    }

    public Task<List<ExportJob>> ListAsync(int page, int size, string? status, CancellationToken ct) =>
        QueryJobsAsync("""
            SELECT document::text FROM export_jobs
            WHERE (@status IS NULL OR status = @status)
            ORDER BY created_at DESC, id DESC LIMIT @size OFFSET @offset
            """, cmd =>
        {
            cmd.Parameters.Add("status", NpgsqlDbType.Text).Value = (object?)status ?? DBNull.Value;
            cmd.Parameters.AddWithValue("size", size);
            cmd.Parameters.AddWithValue("offset", (page - 1) * size);
        }, ct);

    public async Task<(ExportBatch? Batch, string? Error)> AddBatchAsync(BatchInput input, CancellationToken ct)
    {
        try
        {
            return await ChangeAsync<(ExportBatch? Batch, string? Error)>(input.JobId, async (job, conn, tx) =>
            {
                if (job is null || job.Status != JobStatus.Receiving)
                    return (null, "Arquivo inexistente ou encerrado.");
                var batch = new ExportBatch
                {
                    JobId = input.JobId,
                    Sequence = ++job.NextSequence,
                    BatchId = input.BatchId,
                    DataJson = input.DataJson,
                    ItemCount = input.Count
                };
                if (batch.Sequence == 1) job.Columns = input.Columns;
                await using var insert = new NpgsqlCommand("""
                    INSERT INTO export_batches (id, job_id, sequence, batch_id, data_json, item_count, received_at)
                    VALUES (@id, @job, @sequence, @batch, @data, @count, @received)
                    """, conn, tx);
                insert.Parameters.AddWithValue("id", batch.Id);
                insert.Parameters.AddWithValue("job", batch.JobId);
                insert.Parameters.AddWithValue("sequence", batch.Sequence);
                insert.Parameters.Add("batch", NpgsqlDbType.Text).Value = (object?)batch.BatchId ?? DBNull.Value;
                insert.Parameters.Add("data", NpgsqlDbType.Jsonb).Value = batch.DataJson;
                insert.Parameters.AddWithValue("count", batch.ItemCount);
                insert.Parameters.AddWithValue("received", batch.ReceivedAt);
                await insert.ExecuteNonQueryAsync(ct);
                job.BatchCount++;
                job.ItemCount += input.Count;
                job.LastActivityAt = DateTime.UtcNow;
                job.BatchesCleaned = false;
                await SaveJobAsync(conn, tx, job, ct);
                return (batch, null);
            }, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            await FailAsync(input.JobId, "Falha ao persistir um lote.", CancellationToken.None);
            await DeleteBatchesAsync(input.JobId, CancellationToken.None);
            throw;
        }
    }

    public Task<ExportJob?> BeginCloseAsync(string id, FinishRequest? request, CancellationToken ct) =>
        ChangeAsync(id, async (job, conn, tx) =>
        {
            if (job?.Status == JobStatus.Receiving)
            {
                job.Status = JobStatus.Closing;
                job.ClosedAt = DateTime.UtcNow;
                job.ExpectedBatches = request?.TotalLotes;
                job.ExpectedItems = request?.TotalItens;
                await SaveJobAsync(conn, tx, job, ct);
            }
            return job;
        }, ct);

    public Task<ExportJob?> FinalizeCloseAsync(string id, CancellationToken ct) =>
        ChangeAsync(id, async (job, conn, tx) =>
        {
            if (job?.Status != JobStatus.Closing || job.PendingBatches != 0) return job;
            var mismatch = job.BatchCount == 0 ? "Arquivo sem lotes." :
                job.ExpectedBatches is not null && job.ExpectedBatches != job.BatchCount ? "Total de lotes divergente." :
                job.ExpectedItems is not null && job.ExpectedItems != job.ItemCount ? "Total de itens divergente." : null;
            if (mismatch is null) job.Status = JobStatus.Queued;
            else
            {
                job.Status = JobStatus.Failed;
                job.Error = mismatch;
                await DeleteBatchesInTransactionAsync(conn, tx, id, ct);
                job.BatchesCleaned = true;
            }
            await SaveJobAsync(conn, tx, job, ct);
            return job;
        }, ct);

    public Task<List<ExportJob>> FindClosingAsync(CancellationToken ct) =>
        QueryJobsAsync("SELECT document::text FROM export_jobs WHERE status = 'fechando' LIMIT 50", null, ct);

    public Task<List<ExportJob>> FindQueuedForDispatchAsync(CancellationToken ct) =>
        QueryJobsAsync("SELECT document::text FROM export_jobs WHERE status = 'na_fila' AND last_enqueued_at IS NULL LIMIT 50", null, ct);

    public Task MarkEnqueuedAsync(string id, CancellationToken ct) =>
        ChangeAsync(id, async (job, conn, tx) =>
        {
            if (job?.Status == JobStatus.Queued)
            {
                job.LastEnqueuedAt = DateTime.UtcNow;
                await SaveJobAsync(conn, tx, job, ct);
            }
            return true;
        }, ct);

    public Task<ExportJob?> ClaimAsync(string id, CancellationToken ct) =>
        ChangeAsync(id, async (job, conn, tx) =>
        {
            if (job?.Status != JobStatus.Queued) return null;
            job.Status = JobStatus.Processing;
            job.StartedAt = DateTime.UtcNow;
            job.S3Key = S3ExportStorage.ObjectKey(id);
            await SaveJobAsync(conn, tx, job, ct);
            return job;
        }, ct);

    public Task<bool> MarkReadyAsync(string id, string key, long bytes, CancellationToken ct) =>
        ChangeAsync(id, async (job, conn, tx) =>
        {
            if (job?.Status != JobStatus.Processing) return false;
            var now = DateTime.UtcNow;
            job.Status = JobStatus.Ready;
            job.ReadyAt = now;
            job.ExpiresAt = now.AddHours(24);
            job.S3Key = key;
            job.FileBytes = bytes;
            await SaveJobAsync(conn, tx, job, ct);
            return true;
        }, ct);

    public Task<bool> FailAsync(string id, string error, CancellationToken ct) =>
        ChangeAsync(id, async (job, conn, tx) =>
        {
            if (job is null || job.Status is JobStatus.Ready or JobStatus.Failed or JobStatus.Expired)
                return false;
            job.Status = JobStatus.Failed;
            job.Error = error;
            job.PendingBatches = 0;
            await SaveJobAsync(conn, tx, job, ct);
            return true;
        }, ct);

    public async IAsyncEnumerable<ExportBatch> ReadBatchesAsync(string id,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT id, sequence, batch_id, data_json::text, item_count, received_at
            FROM export_batches WHERE job_id = @id ORDER BY sequence
            """, conn);
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            yield return new ExportBatch
            {
                Id = reader.GetString(0),
                JobId = id,
                Sequence = reader.GetInt64(1),
                BatchId = reader.IsDBNull(2) ? null : reader.GetString(2),
                DataJson = reader.GetString(3),
                ItemCount = reader.GetInt32(4),
                ReceivedAt = reader.GetDateTime(5)
            };
    }

    public Task DeleteBatchesAsync(string id, CancellationToken ct) =>
        ChangeAsync(id, async (job, conn, tx) =>
        {
            await DeleteBatchesInTransactionAsync(conn, tx, id, ct);
            if (job is not null)
            {
                job.BatchesCleaned = true;
                await SaveJobAsync(conn, tx, job, ct);
            }
            return true;
        }, ct);

    public Task<List<ExportJob>> FindStaleReceivingAsync(DateTime threshold, CancellationToken ct) =>
        QueryJobsAsync("""
            SELECT document::text FROM export_jobs
            WHERE status = 'recebendo' AND last_activity_at < @threshold LIMIT 100
            """, cmd => cmd.Parameters.AddWithValue("threshold", threshold), ct);

    public Task<List<ExportJob>> FindOverdueAsync(DateTime threshold, CancellationToken ct) =>
        QueryJobsAsync("""
            SELECT document::text FROM export_jobs
            WHERE status IN ('na_fila', 'processando', 'fechando') AND closed_at < @threshold LIMIT 100
            """, cmd => cmd.Parameters.AddWithValue("threshold", threshold), ct);

    public Task<List<ExportJob>> FindExpiredFilesAsync(DateTime now, CancellationToken ct) =>
        QueryJobsAsync("""
            SELECT document::text FROM export_jobs
            WHERE status = 'pronto' AND expires_at < @now LIMIT 100
            """, cmd => cmd.Parameters.AddWithValue("now", now), ct);

    public Task<List<ExportJob>> FindTerminalForBatchCleanupAsync(CancellationToken ct) =>
        QueryJobsAsync("""
            SELECT document::text FROM export_jobs
            WHERE NOT batches_cleaned AND status IN ('pronto', 'falhou', 'expirou') LIMIT 100
            """, null, ct);

    public Task<List<ExportJob>> FindFailedWithFileAsync(CancellationToken ct) =>
        QueryJobsAsync("""
            SELECT document::text FROM export_jobs
            WHERE status = 'falhou' AND document ->> 'S3Key' IS NOT NULL LIMIT 100
            """, null, ct);

    public Task ClearFailedFileAsync(string id, CancellationToken ct) =>
        ChangeAsync(id, async (job, conn, tx) =>
        {
            if (job?.Status == JobStatus.Failed)
            {
                job.S3Key = null;
                await SaveJobAsync(conn, tx, job, ct);
            }
            return true;
        }, ct);

    public Task MarkExpiredAsync(string id, CancellationToken ct) =>
        ChangeAsync(id, async (job, conn, tx) =>
        {
            if (job?.Status == JobStatus.Ready)
            {
                job.Status = JobStatus.Expired;
                job.S3Key = null;
                await SaveJobAsync(conn, tx, job, ct);
            }
            return true;
        }, ct);

    public async Task DeleteOldHistoryAsync(DateTime threshold, CancellationToken ct)
    {
        await using var cmd = _dataSource.CreateCommand("""
            DELETE FROM export_jobs WHERE created_at < @threshold AND status IN ('falhou', 'expirou')
            """);
        cmd.Parameters.AddWithValue("threshold", threshold);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public Task<List<ExportJob>> FindUnnotifiedAsync(CancellationToken ct) =>
        QueryJobsAsync("""
            SELECT document::text FROM export_jobs
            WHERE NOT notification_sent AND status IN ('pronto', 'falhou') LIMIT 50
            """, null, ct);

    public Task MarkNotifiedAsync(string id, string? error, CancellationToken ct) =>
        ChangeAsync(id, async (job, conn, tx) =>
        {
            if (job is not null)
            {
                job.NotificationSent = true;
                job.NotificationError = error;
                await SaveJobAsync(conn, tx, job, ct);
            }
            return true;
        }, ct);

    private async Task<T> ChangeAsync<T>(string id,
        Func<ExportJob?, NpgsqlConnection, NpgsqlTransaction, Task<T>> change, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using var select = new NpgsqlCommand(
            "SELECT document::text FROM export_jobs WHERE id = @id FOR UPDATE", conn, tx);
        select.Parameters.AddWithValue("id", id);
        var job = await ReadOneAsync(select, ct);
        var result = await change(job, conn, tx);
        await tx.CommitAsync(ct);
        return result;
    }

    private async Task<List<ExportJob>> QueryJobsAsync(string sql, Action<NpgsqlCommand>? bind,
        CancellationToken ct)
    {
        await using var cmd = _dataSource.CreateCommand(sql);
        bind?.Invoke(cmd);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var jobs = new List<ExportJob>();
        while (await reader.ReadAsync(ct))
            jobs.Add(JsonSerializer.Deserialize<ExportJob>(reader.GetString(0))!);
        return jobs;
    }

    private static async Task<ExportJob?> ReadOneAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var document = await cmd.ExecuteScalarAsync(ct);
        return document is null or DBNull ? null : JsonSerializer.Deserialize<ExportJob>((string)document);
    }

    private static async Task SaveJobAsync(NpgsqlConnection conn, NpgsqlTransaction tx,
        ExportJob job, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            UPDATE export_jobs SET status = @status, created_at = @created,
                last_activity_at = @activity, closed_at = @closed, expires_at = @expires,
                last_enqueued_at = @enqueued, notification_sent = @notified,
                batches_cleaned = @cleaned, document = @document WHERE id = @id
            """, conn, tx);
        BindJob(cmd, job);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void BindJob(NpgsqlCommand cmd, ExportJob job)
    {
        cmd.Parameters.AddWithValue("id", job.Id);
        cmd.Parameters.AddWithValue("status", job.Status);
        cmd.Parameters.AddWithValue("created", job.CreatedAt);
        cmd.Parameters.AddWithValue("activity", job.LastActivityAt);
        cmd.Parameters.Add("closed", NpgsqlDbType.TimestampTz).Value = (object?)job.ClosedAt ?? DBNull.Value;
        cmd.Parameters.Add("expires", NpgsqlDbType.TimestampTz).Value = (object?)job.ExpiresAt ?? DBNull.Value;
        cmd.Parameters.Add("enqueued", NpgsqlDbType.TimestampTz).Value = (object?)job.LastEnqueuedAt ?? DBNull.Value;
        cmd.Parameters.AddWithValue("notified", job.NotificationSent);
        cmd.Parameters.AddWithValue("cleaned", job.BatchesCleaned);
        cmd.Parameters.Add("document", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(job);
    }

    private static async Task DeleteBatchesInTransactionAsync(NpgsqlConnection conn,
        NpgsqlTransaction tx, string id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("DELETE FROM export_batches WHERE job_id = @id", conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
