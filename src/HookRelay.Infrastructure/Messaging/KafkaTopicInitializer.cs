using Confluent.Kafka;
using Confluent.Kafka.Admin;
using HookRelay.Infrastructure.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HookRelay.Infrastructure.Messaging;

/// <summary>
/// Creates the delivery topic on startup when it is missing.
/// </summary>
/// <remarks>
/// Convenient for local runs and for the test suite, which spins a broker up per fixture. Production
/// deployments turn it off and provision the topic alongside the rest of the infrastructure, because
/// partition count is a capacity decision that cannot be reduced later without recreating the topic.
/// </remarks>
public sealed partial class KafkaTopicInitializer(
    IOptions<KafkaOptions> options,
    ILogger<KafkaTopicInitializer> logger) : IHostedService
{
    private readonly KafkaOptions _options =
        options?.Value ?? throw new ArgumentNullException(nameof(options));

    private readonly ILogger<KafkaTopicInitializer> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.CreateTopicIfMissing)
        {
            return;
        }

        using IAdminClient admin = new AdminClientBuilder(
            new AdminClientConfig { BootstrapServers = _options.BootstrapServers }).Build();

        try
        {
            await admin.CreateTopicsAsync(
            [
                new TopicSpecification
                {
                    Name = _options.Topic,
                    NumPartitions = _options.PartitionCount,
                    ReplicationFactor = _options.ReplicationFactor,
                },
            ]);

            LogCreated(_logger, _options.Topic, _options.PartitionCount);
        }
        catch (CreateTopicsException exception)
            when (exception.Results.TrueForAll(result => result.Error.Code is ErrorCode.TopicAlreadyExists))
        {
            LogAlreadyExists(_logger, _options.Topic);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        EventId = 1300,
        Level = LogLevel.Information,
        Message = "Created Kafka topic {Topic} with {Partitions} partitions.")]
    private static partial void LogCreated(ILogger logger, string topic, int partitions);

    [LoggerMessage(
        EventId = 1301,
        Level = LogLevel.Debug,
        Message = "Kafka topic {Topic} already exists.")]
    private static partial void LogAlreadyExists(ILogger logger, string topic);
}
