using Amazon;
using Amazon.SQS;
using Amazon.SQS.Model;

namespace CentralDeDownloads.Core;

public sealed class SqsJobQueue : IJobQueue, IDisposable
{
    private readonly AmazonSQSClient _client;
    private readonly string _queueUrl;

    public SqsJobQueue(string region, string queueUrl, string? serviceUrl = null)
    {
        if (string.IsNullOrWhiteSpace(queueUrl))
            throw new ArgumentException("Configure Aws:QueueUrl quando Queue:Provider for SQS.", nameof(queueUrl));
        _queueUrl = queueUrl;
        _client = string.IsNullOrWhiteSpace(serviceUrl)
            ? new AmazonSQSClient(RegionEndpoint.GetBySystemName(region))
            : new AmazonSQSClient(new AmazonSQSConfig
            { ServiceURL = serviceUrl, AuthenticationRegion = region });
    }

    public Task PublishAsync(string jobId, CancellationToken ct) =>
        _client.SendMessageAsync(new SendMessageRequest
        { QueueUrl = _queueUrl, MessageBody = jobId }, ct);

    public async Task<QueueMessage?> ReceiveAsync(CancellationToken ct)
    {
        var response = await _client.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = _queueUrl,
            MaxNumberOfMessages = 1,
            WaitTimeSeconds = 20,
            VisibilityTimeout = 1800
        }, ct);
        var message = response.Messages?.FirstOrDefault();
        if (message is null) return null;
        return new QueueMessage(message.Body,
            token => _client.DeleteMessageAsync(new DeleteMessageRequest
            { QueueUrl = _queueUrl, ReceiptHandle = message.ReceiptHandle }, token),
            _ => Task.CompletedTask);
    }

    public void Dispose() => _client.Dispose();
}
