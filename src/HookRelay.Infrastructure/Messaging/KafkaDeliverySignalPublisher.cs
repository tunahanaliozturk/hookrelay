using Confluent.Kafka;
using HookRelay.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HookRelay.Infrastructure.Messaging;

/// <summary>Publishes dispatch signals to the delivery workers.</summary>
public interface IDeliverySignalPublisher
{
    /// <summary>Publishes one signal, keyed so the ordering scope lands on a stable partition.</summary>
    /// <param name="signal">The signal.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task PublishAsync(DeliverySignal signal, CancellationToken cancellationToken);
}

/// <summary>Kafka-backed <see cref="IDeliverySignalPublisher"/>.</summary>
/// <remarks>
/// <para>
/// Produces with <c>acks=all</c> and idempotence on. The dispatcher only marks a delivery in flight after
/// the broker acknowledges, so a crash between publish and commit costs a duplicate signal rather than a
/// lost delivery, which the worker absorbs by re-reading the row.
/// </para>
/// <para>
/// The partition key is the ordering key. That gives every ordered stream a stable partition and therefore
/// worker affinity, which keeps a stream's attempts on one connection pool and one circuit-breaker instance.
/// It is not what makes ordering correct on its own: see the head-of-line claim in the dispatcher.
/// </para>
/// </remarks>
public sealed partial class KafkaDeliverySignalPublisher : IDeliverySignalPublisher, IDisposable
{
    private readonly IProducer<string, byte[]> _producer;
    private readonly string _topic;
    private readonly ILogger<KafkaDeliverySignalPublisher> _logger;

    /// <summary>Creates the producer.</summary>
    /// <param name="options">Kafka settings.</param>
    /// <param name="logger">Logger.</param>
    public KafkaDeliverySignalPublisher(IOptions<KafkaOptions> options, ILogger<KafkaDeliverySignalPublisher> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _topic = options.Value.Topic;

        var config = new ProducerConfig
        {
            BootstrapServers = options.Value.BootstrapServers,
            Acks = Acks.All,
            EnableIdempotence = true,
            MessageSendMaxRetries = 5,
            LingerMs = 5,
            CompressionType = CompressionType.Lz4,
        };

        _producer = new ProducerBuilder<string, byte[]>(config)
            .SetErrorHandler((_, error) => LogProducerError(_logger, error.Reason, error.Code))
            .Build();
    }

    /// <inheritdoc />
    public async Task PublishAsync(DeliverySignal signal, CancellationToken cancellationToken)
    {
        var message = new Message<string, byte[]>
        {
            Key = signal.OrderingKey,
            Value = DeliverySignalSerializer.Serialize(signal),
        };

        DeliveryResult<string, byte[]> result =
            await _producer.ProduceAsync(_topic, message, cancellationToken);

        LogPublished(_logger, signal.DeliveryId, signal.Attempt, result.Partition.Value, result.Offset.Value);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _producer.Flush(TimeSpan.FromSeconds(5));
        _producer.Dispose();
    }

    [LoggerMessage(
        EventId = 1500,
        Level = LogLevel.Warning,
        Message = "Kafka producer error: {Reason} (code {Code})")]
    private static partial void LogProducerError(ILogger logger, string reason, ErrorCode code);

    [LoggerMessage(
        EventId = 1501,
        Level = LogLevel.Debug,
        Message = "Published delivery {DeliveryId} attempt {Attempt} to partition {Partition} offset {Offset}.")]
    private static partial void LogPublished(
        ILogger logger,
        Guid deliveryId,
        int attempt,
        int partition,
        long offset);
}
