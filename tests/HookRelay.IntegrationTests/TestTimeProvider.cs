namespace HookRelay.IntegrationTests;

/// <summary>
/// A clock the test moves by hand.
/// </summary>
/// <remarks>
/// The backoff ladder is the thing under test, and waiting real seconds for it turns a suite into
/// something nobody runs. Only <see cref="GetUtcNow"/> is overridden, so elapsed-time measurements still
/// use the real high-resolution timer and reported latencies stay meaningful.
/// </remarks>
/// <param name="start">Starting instant.</param>
public sealed class TestTimeProvider(DateTimeOffset start) : TimeProvider
{
    private long _utcTicks = start.UtcTicks;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

    /// <summary>Moves the clock forward.</summary>
    /// <param name="delta">How far.</param>
    public void Advance(TimeSpan delta)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delta, TimeSpan.Zero);
        Interlocked.Add(ref _utcTicks, delta.Ticks);
    }
}
