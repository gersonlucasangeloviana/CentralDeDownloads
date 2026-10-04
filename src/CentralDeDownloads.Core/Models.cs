using MongoDB.Bson.Serialization.Attributes;

namespace CentralDeDownloads.Core;

public static class JobStatus
{
    public const string Receiving = "recebendo";
    public const string Closing = "fechando";
    public const string Queued = "na_fila";
    public const string Processing = "processando";
    public const string Ready = "pronto";
    public const string Failed = "falhou";
    public const string Expired = "expirou";
}

public sealed class ExportJob
{
    [BsonId] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Format { get; set; } = "";
    public string? RequestedFormat { get; set; }
    public DateOnly? PeriodStart { get; set; }
    public DateOnly? PeriodEnd { get; set; }
    public string? Webhook { get; set; }
    public string? Email { get; set; }
    public string Status { get; set; } = JobStatus.Receiving;
    public List<string> Columns { get; set; } = [];
    public long BatchCount { get; set; }
    public long ItemCount { get; set; }
    public long PendingBatches { get; set; }
    public long NextSequence { get; set; }
    public long? ExpectedBatches { get; set; }
    public long? ExpectedItems { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? ReadyAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime? LastEnqueuedAt { get; set; }
    public string? Error { get; set; }
    public string? S3Key { get; set; }
    public long? FileBytes { get; set; }
    public bool NotificationSent { get; set; }
    public string? NotificationError { get; set; }
    public bool BatchesCleaned { get; set; }
}

public sealed class ExportBatch
{
    [BsonId] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string JobId { get; set; } = "";
    public long Sequence { get; set; }
    public string? BatchId { get; set; }
    public string DataJson { get; set; } = "";
    public int ItemCount { get; set; }
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
}

public sealed record CreateJobRequest(string Nome, string Formato, PeriodRequest? Periodo, string? Webhook, string? Email);
public sealed record PeriodRequest(DateOnly Inicio, DateOnly Fim);
public sealed record FinishRequest(long? TotalLotes, long? TotalItens);
public sealed record BatchInput(string JobId, string? BatchId, string DataJson, int Count, List<string> Columns);

public static class ExportFormatPolicy
{
    public const long MaximumXlsxItems = 1_000_000;

    public static string AfterClose(string format, long itemCount) =>
        format == "xlsx" && itemCount > MaximumXlsxItems ? "csv" : format;
}
