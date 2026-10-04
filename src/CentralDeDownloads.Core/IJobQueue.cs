namespace CentralDeDownloads.Core;

public interface IJobQueue
{
    Task PublishAsync(string jobId, CancellationToken ct);
    Task<QueueMessage?> ReceiveAsync(CancellationToken ct);
}

public sealed record QueueMessage(string Body,
    Func<CancellationToken, Task> AcknowledgeAsync,
    Func<CancellationToken, Task> ReleaseAsync);
