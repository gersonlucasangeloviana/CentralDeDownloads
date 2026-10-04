using System.Text;
using System.Threading.Channels;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace GeradorExcel.Core;

public sealed class RabbitMqJobQueue : IJobQueue, IDisposable
{
    private readonly ConnectionFactory _factory;
    private readonly string _queueName;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Channel<QueueMessage> _deliveries = Channel.CreateBounded<QueueMessage>(1);
    private IConnection? _connection;
    private IChannel? _consumerChannel;

    public RabbitMqJobQueue(string uri, string queueName)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme is not ("amqp" or "amqps"))
            throw new ArgumentException("Configure RabbitMq:Uri com uma URL amqp:// ou amqps://.", nameof(uri));
        if (string.IsNullOrWhiteSpace(queueName))
            throw new ArgumentException("Configure RabbitMq:QueueName.", nameof(queueName));
        _queueName = queueName;
        _factory = new ConnectionFactory
        {
            Uri = endpoint,
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            RequestedHeartbeat = TimeSpan.FromSeconds(30)
        };
    }

    public async Task PublishAsync(string jobId, CancellationToken ct)
    {
        var connection = await GetConnectionAsync(ct);
        await using var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true), ct);
        await channel.QueueDeclareAsync(_queueName, durable: true, exclusive: false,
            autoDelete: false, cancellationToken: ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await channel.BasicPublishAsync(exchange: string.Empty, routingKey: _queueName,
            mandatory: true, basicProperties: new BasicProperties
            { Persistent = true, ContentType = "text/plain" },
            body: Encoding.UTF8.GetBytes(jobId), cancellationToken: timeout.Token);
    }

    public async Task<QueueMessage?> ReceiveAsync(CancellationToken ct)
    {
        await EnsureConsumerAsync(ct);
        return await _deliveries.Reader.ReadAsync(ct);
    }

    private async Task<IConnection> GetConnectionAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_connection?.IsOpen == true) return _connection;
            _connection?.Dispose();
            _connection = await _factory.CreateConnectionAsync(ct);
            return _connection;
        }
        finally { _gate.Release(); }
    }

    private async Task EnsureConsumerAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_consumerChannel?.IsOpen == true) return;
            _consumerChannel?.Dispose();
            if (_connection?.IsOpen != true)
            {
                _connection?.Dispose();
                _connection = await _factory.CreateConnectionAsync(ct);
            }
            var channel = await _connection.CreateChannelAsync(cancellationToken: ct);
            await channel.QueueDeclareAsync(_queueName, durable: true, exclusive: false,
                autoDelete: false, cancellationToken: ct);
            await channel.BasicQosAsync(0, 1, false, ct);
            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += async (_, delivery) =>
            {
                // O cliente reutiliza o buffer depois do callback: copiar antes de retornar.
                var body = Encoding.UTF8.GetString(delivery.Body.Span);
                var tag = delivery.DeliveryTag;
                await _deliveries.Writer.WriteAsync(new QueueMessage(body,
                    token => channel.BasicAckAsync(tag, false, token).AsTask(),
                    token => channel.BasicNackAsync(tag, false, true, token).AsTask()));
            };
            await channel.BasicConsumeAsync(_queueName, autoAck: false, consumer, ct);
            _consumerChannel = channel;
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _deliveries.Writer.TryComplete();
        _consumerChannel?.Dispose();
        _connection?.Dispose();
        _gate.Dispose();
    }
}
