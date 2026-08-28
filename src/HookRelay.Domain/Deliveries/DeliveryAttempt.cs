namespace HookRelay.Domain.Deliveries;

/// <summary>How one attempt ended.</summary>
public enum AttemptOutcome
{
    /// <summary>The endpoint answered 2xx.</summary>
    Success = 0,

    /// <summary>The endpoint answered, but not with 2xx.</summary>
    HttpError = 1,

    /// <summary>The endpoint did not answer inside the per-request timeout.</summary>
    Timeout = 2,

    /// <summary>The connection failed: DNS, TLS, refused, reset.</summary>
    NetworkError = 3,

    /// <summary>The endpoint's circuit was open, so no request was sent at all.</summary>
    CircuitOpen = 4,

    /// <summary>The destination failed the URL policy check at send time, for example a host that now resolves to a private address.</summary>
    BlockedByPolicy = 5,
}

/// <summary>
/// One row per attempt, written as the attempt happens.
/// </summary>
/// <remarks>
/// Every attempt is recorded, not just the terminal outcome. That is what turns "did you even send this?"
/// into a question a support engineer can answer from a query, and it is the table the backoff-adherence
/// test reads to check that retries actually landed when the published schedule says they should.
/// </remarks>
public sealed class DeliveryAttempt
{
    private const int MaxSnippetLength = 512;

    private DeliveryAttempt()
    {
    }

    /// <summary>Identifier.</summary>
    public Guid Id { get; private set; }

    /// <summary>The delivery this attempt belongs to.</summary>
    public Guid DeliveryId { get; private set; }

    /// <summary>Destination endpoint, denormalised so the per-endpoint log query stays a single index seek.</summary>
    public Guid EndpointId { get; private set; }

    /// <summary>1-based attempt number.</summary>
    public int AttemptNumber { get; private set; }

    /// <summary>How the attempt ended.</summary>
    public AttemptOutcome Outcome { get; private set; }

    /// <summary>Response status, when the endpoint answered at all.</summary>
    public int? StatusCode { get; private set; }

    /// <summary>Wall-clock duration of the request in milliseconds.</summary>
    public int LatencyMs { get; private set; }

    /// <summary>First half a kilobyte of the response body, kept for debugging.</summary>
    public string? ResponseSnippet { get; private set; }

    /// <summary>Exception or policy message, when there was one.</summary>
    public string? Error { get; private set; }

    /// <summary>When the attempt started.</summary>
    public DateTimeOffset AttemptedAtUtc { get; private set; }

    /// <summary>When the next attempt is due, or null if this was the last one.</summary>
    public DateTimeOffset? NextAttemptAtUtc { get; private set; }

    /// <summary>Records an attempt.</summary>
    /// <param name="delivery">The delivery, already updated with the attempt's result.</param>
    /// <param name="outcome">How the attempt ended.</param>
    /// <param name="statusCode">Response status, when there was one.</param>
    /// <param name="latency">How long the request took.</param>
    /// <param name="responseSnippet">Start of the response body.</param>
    /// <param name="error">Failure message, when there was one.</param>
    /// <param name="attemptedAt">When the attempt started.</param>
    public static DeliveryAttempt Record(
        Delivery delivery,
        AttemptOutcome outcome,
        int? statusCode,
        TimeSpan latency,
        string? responseSnippet,
        string? error,
        DateTimeOffset attemptedAt)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        return new DeliveryAttempt
        {
            Id = Guid.CreateVersion7(attemptedAt),
            DeliveryId = delivery.Id,
            EndpointId = delivery.EndpointId,
            AttemptNumber = delivery.AttemptCount,
            Outcome = outcome,
            StatusCode = statusCode,
            LatencyMs = (int)Math.Clamp(latency.TotalMilliseconds, 0, int.MaxValue),
            ResponseSnippet = Truncate(responseSnippet),
            Error = Truncate(error),
            AttemptedAtUtc = attemptedAt,
            NextAttemptAtUtc = delivery.Status is DeliveryStatus.Pending ? delivery.NextAttemptAtUtc : null,
        };
    }

    private static string? Truncate(string? value) =>
        value is null || value.Length <= MaxSnippetLength ? value : value[..MaxSnippetLength];
}
