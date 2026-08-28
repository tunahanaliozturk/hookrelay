using System.ComponentModel.DataAnnotations;
using HookRelay.Domain.Deliveries;

namespace HookRelay.Infrastructure.Configuration;

/// <summary>Everything about how a single delivery attempt is made.</summary>
public sealed class DeliveryOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "HookRelay:Delivery";

    /// <summary>
    /// The published backoff ladder: 30 seconds, 2 minutes, 10 minutes, 1 hour, 6 hours, 24 hours.
    /// Seven attempts across a bounded window of 31 hours, 12 minutes and 30 seconds.
    /// </summary>
    public static IReadOnlyList<TimeSpan> DefaultRetryDelays { get; } =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(6),
        TimeSpan.FromHours(24),
    ];

    /// <summary>
    /// Overrides the ladder. Empty means <see cref="DefaultRetryDelays"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately empty by default rather than pre-filled. The configuration binder binds collections
    /// into the instance it finds, so a pre-filled default would have configured values appended to the
    /// production ladder instead of replacing it, and CI would have gone on quietly waiting out 30 second
    /// rungs while believing it had compressed them.
    /// </remarks>
    public IList<TimeSpan> RetryDelays { get; init; } = [];

    /// <summary>Fraction of each delay that jitter may add or subtract.</summary>
    [Range(0d, 0.5d)]
    public double JitterRatio { get; init; } = 0.1;

    /// <summary>How long a single HTTP attempt may take before it counts as a timeout.</summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:05:00")]
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Failures needed inside the sampling window before an endpoint's circuit opens.
    /// Combined with a failure ratio of 1.0, this reads as "this many consecutive failures".
    /// </summary>
    [Range(1, 1000)]
    public int CircuitMinimumThroughput { get; init; } = 5;

    /// <summary>Window the breaker samples over.</summary>
    [Range(typeof(TimeSpan), "00:00:00.500", "01:00:00")]
    public TimeSpan CircuitSamplingDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long the circuit stays open before it half-opens and probes the endpoint again.</summary>
    [Range(typeof(TimeSpan), "00:00:00.500", "01:00:00")]
    public TimeSpan CircuitBreakDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a claimed delivery may sit before another dispatcher assumes the worker died and reclaims it.
    /// Must comfortably exceed <see cref="RequestTimeout"/>.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:05", "01:00:00")]
    public TimeSpan StaleClaimTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Bytes of the response body kept for debugging.</summary>
    [Range(0, 8192)]
    public int ResponseSnippetBytes { get; init; } = 512;

    /// <summary>Value sent as the User-Agent header.</summary>
    [Required]
    public string UserAgent { get; init; } = "HookRelay/1.0";

    /// <summary>
    /// Permit plain http destinations. Development and test only: it puts the signed payload on the wire
    /// in the clear and lets an on-path attacker read every event body.
    /// </summary>
    public bool AllowInsecureHttp { get; init; }

    /// <summary>
    /// Permit destinations inside private and loopback ranges. Development and test only: in production
    /// this is the difference between a webhook sender and an open proxy into your own network.
    /// </summary>
    public bool AllowPrivateNetworkDestinations { get; init; }

    /// <summary>Builds the domain schedule from the configured values.</summary>
    public RetrySchedule ToRetrySchedule()
    {
        IReadOnlyList<TimeSpan> delays = RetryDelays.Count > 0 ? [.. RetryDelays] : DefaultRetryDelays;
        return new RetrySchedule(delays, JitterRatio);
    }
}
