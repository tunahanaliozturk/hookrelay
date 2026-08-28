using Confluent.Kafka;
using HookRelay.Infrastructure.Configuration;
using HookRelay.Infrastructure.Sending;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HookRelay.Infrastructure.Messaging;

/// <summary>
/// Reads dispatch signals and runs a delivery attempt for each one.
/// </summary>
/// <remarks>
/// <para>
/// Signals are processed with bounded concurrency rather than one partition at a time, and that is safe
/// precisely because ordering is enforced upstream: the dispatcher never claims two deliveries for the same
/// ordering key at once, so no two signals in flight here can belong to the same ordered stream. Tying
/// concurrency to partition count instead would cap throughput at the partition count for no extra
/// correctness.
/// </para>
/// <para>
/// Offsets are committed on the client's normal schedule. A signal lost to a rebalance or a crash does not
/// lose the delivery, because the delivery row is still claimed and the stale-claim sweep returns it to the
/// pending pool. That is the same recovery path a worker crash takes, so it gets exercised by the same test.
/// </para>
/// </remarks>
public sealed partial class KafkaDeliveryConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<KafkaOptions> options,
    ILogger<KafkaDeliveryConsumer> logger) : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory =
        scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    private readonly KafkaOptions _options =
        options?.Value ?? throw new ArgumentNullException(nameof(options));

    private readonly ILogger<KafkaDeliveryConsumer> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Factory.StartNew(
                () => ConsumeAsync(stoppingToken),
                stoppingToken,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default)
            .Unwrap();

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            GroupId = _options.ConsumerGroup,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = true,
            PartitionAssignmentStrategy = PartitionAssignmentStrategy.CooperativeSticky,
        };

        using IConsumer<string, byte[]> consumer = new ConsumerBuilder<string, byte[]>(config)
            .SetErrorHandler((_, error) => LogConsumerError(_logger, error.Reason, error.Code))
            .Build();

        consumer.Subscribe(_options.Topic);
        LogStarted(_logger, _options.Topic, _options.ConsumerGroup, _options.MaxConcurrency);

        using var slots = new SemaphoreSlim(_options.MaxConcurrency, _options.MaxConcurrency);
        List<Task> inFlight = [];

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string, byte[]>? result = consumer.Consume(TimeSpan.FromMilliseconds(200));
                if (result?.Message is null)
                {
                    inFlight.RemoveAll(static task => task.IsCompleted);
                    continue;
                }

                if (!DeliverySignalSerializer.TryDeserialize(result.Message.Value, out DeliverySignal signal))
                {
                    LogUnreadableMessage(_logger, result.TopicPartitionOffset.ToString());
                    continue;
                }

                await slots.WaitAsync(stoppingToken);
                inFlight.Add(RunAsync(signal, slots, stoppingToken));
                inFlight.RemoveAll(static task => task.IsCompleted);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        finally
        {
            await Task.WhenAll(inFlight);
            consumer.Close();
        }
    }

    private async Task RunAsync(DeliverySignal signal, SemaphoreSlim slots, CancellationToken cancellationToken)
    {
        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
            IDeliveryProcessor processor = scope.ServiceProvider.GetRequiredService<IDeliveryProcessor>();
            await processor.ProcessAsync(signal.DeliveryId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown. The claim goes stale and is swept back into the pending pool.
        }
#pragma warning disable CA1031 // One failed delivery must not stop the consumer.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogProcessingFailed(_logger, signal.DeliveryId, exception);
        }
        finally
        {
            slots.Release();
        }
    }

    [LoggerMessage(
        EventId = 1400,
        Level = LogLevel.Information,
        Message = "Delivery consumer started on {Topic} as {Group} with concurrency {Concurrency}.")]
    private static partial void LogStarted(ILogger logger, string topic, string group, int concurrency);

    [LoggerMessage(
        EventId = 1401,
        Level = LogLevel.Warning,
        Message = "Kafka consumer error: {Reason} (code {Code})")]
    private static partial void LogConsumerError(ILogger logger, string reason, ErrorCode code);

    [LoggerMessage(
        EventId = 1402,
        Level = LogLevel.Warning,
        Message = "Skipped an unreadable message at {Offset}.")]
    private static partial void LogUnreadableMessage(ILogger logger, string offset);

    [LoggerMessage(
        EventId = 1403,
        Level = LogLevel.Error,
        Message = "Processing delivery {DeliveryId} failed. The claim will be swept back into the pending pool.")]
    private static partial void LogProcessingFailed(ILogger logger, Guid deliveryId, Exception exception);
}
