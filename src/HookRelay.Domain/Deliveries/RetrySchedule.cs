namespace HookRelay.Domain.Deliveries;

/// <summary>
/// The fixed backoff ladder a failed delivery walks before it is dead-lettered.
/// </summary>
/// <remarks>
/// <para>
/// The delays are stored, not held in memory by a retry policy. A 24 hour gap between attempts cannot
/// live inside a <c>Polly</c> retry loop: the process would have to stay up for the whole window and a
/// restart would lose every pending retry. So the schedule only decides <em>when</em> the next attempt is
/// due, and the delivery row carries that timestamp.
/// </para>
/// <para>
/// Jitter spreads a thundering herd of retries that would otherwise land together after a shared outage.
/// It is intentionally small, because the published schedule is part of the contract customers build
/// against and the CI suite asserts real attempt timestamps against it.
/// </para>
/// </remarks>
public sealed class RetrySchedule
{
    private readonly TimeSpan[] _delays;

    /// <summary>Creates a schedule.</summary>
    /// <param name="delays">Delay before attempt 2, attempt 3, and so on. Must not be empty.</param>
    /// <param name="jitterRatio">Fraction of each delay the jitter may add or subtract. Between 0 and 0.5.</param>
    public RetrySchedule(IReadOnlyList<TimeSpan> delays, double jitterRatio = 0.1)
    {
        ArgumentNullException.ThrowIfNull(delays);
        ArgumentOutOfRangeException.ThrowIfZero(delays.Count);
        ArgumentOutOfRangeException.ThrowIfNegative(jitterRatio);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(jitterRatio, 0.5);

        _delays = [.. delays];
        foreach (TimeSpan delay in _delays)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(delay, TimeSpan.Zero, nameof(delays));
        }

        JitterRatio = jitterRatio;
    }

    /// <summary>
    /// The production ladder: 30 seconds, 2 minutes, 10 minutes, 1 hour, 6 hours, 24 hours.
    /// Seven attempts across a bounded window of 31 hours, 12 minutes and 30 seconds.
    /// </summary>
    public static RetrySchedule Default { get; } = new(
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(6),
        TimeSpan.FromHours(24),
    ]);

    /// <summary>Fraction of each delay that jitter may add or subtract.</summary>
    public double JitterRatio { get; }

    /// <summary>Total attempts a delivery gets, including the first one.</summary>
    public int MaxAttempts => _delays.Length + 1;

    /// <summary>Longest a delivery can stay alive before dead-lettering, ignoring jitter.</summary>
    public TimeSpan RetryWindow
    {
        get
        {
            TimeSpan total = TimeSpan.Zero;
            foreach (TimeSpan delay in _delays)
            {
                total += delay;
            }

            return total;
        }
    }

    /// <summary>The configured delay before a given attempt, before jitter is applied.</summary>
    /// <param name="completedAttempts">How many attempts have already been made.</param>
    public TimeSpan BaseDelayAfter(int completedAttempts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(completedAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(completedAttempts, _delays.Length);

        return _delays[completedAttempts - 1];
    }

    /// <summary>Works out when the next attempt is due, or reports that the ladder is exhausted.</summary>
    /// <param name="completedAttempts">How many attempts have already been made. One after the first failure.</param>
    /// <param name="jitterSample">A sample in [0, 1). Callers pass <c>Random.Shared.NextDouble()</c>.</param>
    /// <param name="delay">The jittered delay before the next attempt.</param>
    /// <returns><see langword="false"/> when there are no attempts left and the delivery should be dead-lettered.</returns>
    public bool TryGetDelay(int completedAttempts, double jitterSample, out TimeSpan delay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(completedAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(jitterSample);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(jitterSample, 1d);

        if (completedAttempts > _delays.Length)
        {
            delay = TimeSpan.Zero;
            return false;
        }

        TimeSpan baseDelay = _delays[completedAttempts - 1];
        double multiplier = 1d + (((jitterSample * 2d) - 1d) * JitterRatio);
        delay = baseDelay * multiplier;
        return true;
    }
}
