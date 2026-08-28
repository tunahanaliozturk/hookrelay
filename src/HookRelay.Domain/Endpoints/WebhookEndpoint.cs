using HookRelay.Domain.Deliveries;

namespace HookRelay.Domain.Endpoints;

/// <summary>A customer-registered destination for webhook deliveries.</summary>
public sealed class WebhookEndpoint
{
    private readonly List<string> _subscribedEventTypes = [];

    private WebhookEndpoint()
    {
        // Materialisation only.
        Url = null!;
        ProtectedSecret = null!;
        Description = null!;
    }

    /// <summary>Stable identifier. Also the default ordering-key scope and the Kafka partition key.</summary>
    public Guid Id { get; private set; }

    /// <summary>Owning tenant. Every read path filters on this.</summary>
    public Guid TenantId { get; private set; }

    /// <summary>Destination URL. Checked against the URL policy on write and again before each send.</summary>
    public string Url { get; private set; }

    /// <summary>Free-text label so a support engineer can tell two endpoints apart.</summary>
    public string Description { get; private set; }

    /// <summary>Current signing secret, encrypted at rest.</summary>
    public string ProtectedSecret { get; private set; }

    /// <summary>Previous signing secret, kept so in-flight retries can still be verified after a rotation.</summary>
    public string? ProtectedPreviousSecret { get; private set; }

    /// <summary>Incremented on every rotation. Each delivery pins the version it was first signed with.</summary>
    public int SecretVersion { get; private set; }

    /// <summary>When the secret was last rotated.</summary>
    public DateTimeOffset? SecretRotatedAtUtc { get; private set; }

    /// <summary>Event types this endpoint wants. Supports exact names, prefix wildcards, and a bare star.</summary>
    public IReadOnlyList<string> SubscribedEventTypes => _subscribedEventTypes;

    /// <summary>Lifecycle state.</summary>
    public EndpointStatus Status { get; private set; }

    /// <summary>How wide the ordered stream for this endpoint is.</summary>
    public OrderingStrategy OrderingStrategy { get; private set; }

    /// <summary>Creation time.</summary>
    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>Time of the last state change.</summary>
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    /// <summary>Registers a new endpoint. The caller validates the URL before calling this.</summary>
    /// <param name="tenantId">Owning tenant.</param>
    /// <param name="url">Destination URL.</param>
    /// <param name="description">Human-readable label.</param>
    /// <param name="subscribedEventTypes">Event-type patterns to deliver.</param>
    /// <param name="protectedSecret">The signing secret, already encrypted.</param>
    /// <param name="orderingStrategy">How wide the ordered stream should be.</param>
    /// <param name="now">Current time.</param>
    public static WebhookEndpoint Register(
        Guid tenantId,
        Uri url,
        string description,
        IEnumerable<string> subscribedEventTypes,
        string protectedSecret,
        OrderingStrategy orderingStrategy,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(subscribedEventTypes);
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedSecret);

        var endpoint = new WebhookEndpoint
        {
            Id = Guid.CreateVersion7(now),
            TenantId = tenantId,
            Url = url.ToString(),
            Description = description ?? string.Empty,
            ProtectedSecret = protectedSecret,
            SecretVersion = 1,
            Status = EndpointStatus.Active,
            OrderingStrategy = orderingStrategy,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        foreach (string eventType in subscribedEventTypes)
        {
            if (!string.IsNullOrWhiteSpace(eventType))
            {
                endpoint._subscribedEventTypes.Add(eventType.Trim());
            }
        }

        if (endpoint._subscribedEventTypes.Count == 0)
        {
            throw new ArgumentException(
                "An endpoint must subscribe to at least one event type.",
                nameof(subscribedEventTypes));
        }

        return endpoint;
    }

    /// <summary>True when this endpoint wants the given event type.</summary>
    /// <param name="eventType">Concrete event type, for example invoice.paid.</param>
    public bool IsSubscribedTo(string eventType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);

        foreach (string pattern in _subscribedEventTypes)
        {
            if (Matches(pattern, eventType))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Finds the encrypted secret a delivery pinned at first dispatch, so a rotation never invalidates
    /// a receiver's verification of a retry that was already in flight.
    /// </summary>
    /// <param name="secretVersion">Version recorded on the delivery.</param>
    /// <param name="protectedSecret">The matching encrypted secret.</param>
    public bool TryGetProtectedSecret(int secretVersion, out string protectedSecret)
    {
        if (secretVersion == SecretVersion)
        {
            protectedSecret = ProtectedSecret;
            return true;
        }

        if (secretVersion == SecretVersion - 1 && ProtectedPreviousSecret is not null)
        {
            protectedSecret = ProtectedPreviousSecret;
            return true;
        }

        protectedSecret = string.Empty;
        return false;
    }

    /// <summary>Rotates the signing secret. The outgoing secret stays usable for deliveries already in flight.</summary>
    /// <param name="newProtectedSecret">The new signing secret, already encrypted.</param>
    /// <param name="now">Current time.</param>
    public void RotateSecret(string newProtectedSecret, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newProtectedSecret);
        EnsureNotDisabled();

        ProtectedPreviousSecret = ProtectedSecret;
        ProtectedSecret = newProtectedSecret;
        SecretVersion++;
        SecretRotatedAtUtc = now;
        UpdatedAtUtc = now;
    }

    /// <summary>Stops dispatching. Queued deliveries stay queued, in order, and nothing is dropped.</summary>
    /// <param name="now">Current time.</param>
    public void Pause(DateTimeOffset now)
    {
        EnsureNotDisabled();
        Status = EndpointStatus.Paused;
        UpdatedAtUtc = now;
    }

    /// <summary>Resumes dispatching. Everything queued during the pause goes out in its original order.</summary>
    /// <param name="now">Current time.</param>
    public void Resume(DateTimeOffset now)
    {
        EnsureNotDisabled();
        Status = EndpointStatus.Active;
        UpdatedAtUtc = now;
    }

    /// <summary>Permanently retires the endpoint.</summary>
    /// <param name="now">Current time.</param>
    public void Disable(DateTimeOffset now)
    {
        Status = EndpointStatus.Disabled;
        UpdatedAtUtc = now;
    }

    /// <summary>Replaces the subscription list.</summary>
    /// <param name="eventTypes">New event-type patterns.</param>
    /// <param name="now">Current time.</param>
    public void Resubscribe(IEnumerable<string> eventTypes, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(eventTypes);
        EnsureNotDisabled();

        List<string> replacement = [.. eventTypes
            .Where(static type => !string.IsNullOrWhiteSpace(type))
            .Select(static type => type.Trim())];

        if (replacement.Count == 0)
        {
            throw new ArgumentException(
                "An endpoint must subscribe to at least one event type.",
                nameof(eventTypes));
        }

        _subscribedEventTypes.Clear();
        _subscribedEventTypes.AddRange(replacement);
        UpdatedAtUtc = now;
    }

    private static bool Matches(string pattern, string eventType)
    {
        if (pattern.Length == 1 && pattern[0] == '*')
        {
            return true;
        }

        if (pattern.EndsWith(".*", StringComparison.Ordinal))
        {
            ReadOnlySpan<char> prefix = pattern.AsSpan(0, pattern.Length - 1);
            return eventType.AsSpan().StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(pattern, eventType, StringComparison.OrdinalIgnoreCase);
    }

    private void EnsureNotDisabled()
    {
        if (Status is EndpointStatus.Disabled)
        {
            throw new InvalidOperationException($"Endpoint {Id} is disabled and cannot be modified.");
        }
    }
}
