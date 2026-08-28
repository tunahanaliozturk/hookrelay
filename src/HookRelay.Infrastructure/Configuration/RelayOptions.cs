using System.ComponentModel.DataAnnotations;

namespace HookRelay.Infrastructure.Configuration;

/// <summary>Tuning for the two pollers: outbox fan-out and delivery dispatch.</summary>
public sealed class RelayOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "HookRelay:Relay";

    /// <summary>
    /// How long to sleep after a poll that found nothing. A poll that found work loops straight round
    /// again, so this only sets idle latency, not throughput.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00.010", "00:01:00")]
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Outbox rows claimed per poll.</summary>
    [Range(1, 5000)]
    public int OutboxBatchSize { get; init; } = 100;

    /// <summary>Deliveries claimed per poll.</summary>
    [Range(1, 5000)]
    public int DispatchBatchSize { get; init; } = 200;

    /// <summary>How often stale claims are swept back into the pending pool.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:10:00")]
    public TimeSpan StaleClaimSweepInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long delivery attempt rows are kept for customer-facing debugging.</summary>
    [Range(typeof(TimeSpan), "1.00:00:00", "3650.00:00:00")]
    public TimeSpan AttemptRetention { get; init; } = TimeSpan.FromDays(90);

    /// <summary>How long dead letters are kept after their final attempt.</summary>
    [Range(typeof(TimeSpan), "1.00:00:00", "3650.00:00:00")]
    public TimeSpan DeadLetterRetention { get; init; } = TimeSpan.FromDays(30);

    /// <summary>How often the retention sweep runs. Set to zero to turn it off.</summary>
    public TimeSpan RetentionSweepInterval { get; init; } = TimeSpan.FromHours(1);
}
