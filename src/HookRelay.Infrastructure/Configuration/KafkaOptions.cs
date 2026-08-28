using System.ComponentModel.DataAnnotations;

namespace HookRelay.Infrastructure.Configuration;

/// <summary>Kafka connection and topic settings.</summary>
public sealed class KafkaOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "HookRelay:Kafka";

    /// <summary>Bootstrap servers, comma separated.</summary>
    [Required]
    public string BootstrapServers { get; init; } = "localhost:9092";

    /// <summary>Topic carrying dispatch signals, keyed by ordering key.</summary>
    [Required]
    public string Topic { get; init; } = "hookrelay.deliveries";

    /// <summary>Consumer group the delivery workers join.</summary>
    [Required]
    public string ConsumerGroup { get; init; } = "hookrelay-delivery-workers";

    /// <summary>
    /// Partition count for the topic.
    /// </summary>
    /// <remarks>
    /// This is the knob that trades isolation against rebalance cost. Too few and unrelated endpoints
    /// queue behind each other on the same partition; too many and every worker restart drags the group
    /// through a long rebalance. Twelve suits the demo's endpoint count. A real deployment sizes it from
    /// endpoint cardinality and worker count, and it cannot be lowered later without recreating the topic.
    /// </remarks>
    [Range(1, 1000)]
    public int PartitionCount { get; init; } = 12;

    /// <summary>Replication factor used when the topic is created.</summary>
    [Range(1, 10)]
    public short ReplicationFactor { get; init; } = 1;

    /// <summary>Create the topic at startup if it is missing. Convenient locally, usually off in production.</summary>
    public bool CreateTopicIfMissing { get; init; } = true;

    /// <summary>
    /// Deliveries a single worker attempts at once. Independent of partition count, because ordering is
    /// enforced by the dispatcher's head-of-line claim rather than by consuming one partition at a time.
    /// </summary>
    [Range(1, 512)]
    public int MaxConcurrency { get; init; } = 16;
}
