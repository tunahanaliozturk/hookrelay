using System.Collections.Concurrent;
using HookRelay.Domain.Signing;

namespace HookRelay.ChaosReceiver;

/// <summary>How one receiver slot behaves.</summary>
/// <param name="Secret">Signing secret, when the slot should verify signatures.</param>
/// <param name="FailureRate">Probability in [0, 1] that a request is answered with 500.</param>
/// <param name="LatencyMs">Artificial delay added before answering.</param>
/// <param name="StatusCodeOnFailure">Status returned when a request is chosen to fail.</param>
public sealed record SlotBehaviour(
    string? Secret = null,
    double FailureRate = 0,
    int LatencyMs = 0,
    int StatusCodeOnFailure = 500);

/// <summary>One request as the receiver saw it.</summary>
/// <param name="Sequence">Global arrival order. The ordering assertions read this.</param>
/// <param name="Slot">Which slot received it.</param>
/// <param name="DeliveryId">Value of the delivery id header.</param>
/// <param name="EventType">Value of the event type header.</param>
/// <param name="Attempt">Value of the attempt header.</param>
/// <param name="Body">Raw request body.</param>
/// <param name="ReceivedAtUtc">Arrival time.</param>
/// <param name="Signature">Result of verifying the signature.</param>
/// <param name="Accepted">True when the receiver answered 2xx.</param>
public sealed record ReceivedRequest(
    long Sequence,
    string Slot,
    string? DeliveryId,
    string? EventType,
    int Attempt,
    string Body,
    DateTimeOffset ReceivedAtUtc,
    SignatureVerificationResult Signature,
    bool Accepted);

/// <summary>
/// In-memory state for the deliberately unreliable receiver.
/// </summary>
/// <remarks>
/// This stands in for a real customer endpoint in the test suite, and it is a real HTTP server rather than
/// a mocked handler on purpose. A stubbed <c>HttpMessageHandler</c> cannot reproduce a connection that
/// accepts and then goes quiet, which is the failure the per-request timeout exists for, so testing against
/// one would leave the timeout path unexercised.
/// </remarks>
public sealed class ChaosState
{
    private readonly ConcurrentDictionary<string, SlotBehaviour> _slots = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<ReceivedRequest> _received = new();
    private long _sequence;

    /// <summary>Behaviour applied to a slot that has not been configured.</summary>
    public SlotBehaviour Default { get; set; } = new();

    /// <summary>Sets how a slot behaves.</summary>
    /// <param name="slot">Slot name.</param>
    /// <param name="behaviour">New behaviour.</param>
    public void Configure(string slot, SlotBehaviour behaviour) => _slots[slot] = behaviour;

    /// <summary>Reads a slot's behaviour, falling back to <see cref="Default"/>.</summary>
    /// <param name="slot">Slot name.</param>
    public SlotBehaviour BehaviourFor(string slot) =>
        _slots.TryGetValue(slot, out SlotBehaviour? behaviour) ? behaviour : Default;

    /// <summary>Takes the next arrival sequence number.</summary>
    public long NextSequence() => Interlocked.Increment(ref _sequence);

    /// <summary>Records a request.</summary>
    /// <param name="request">What arrived.</param>
    public void Record(ReceivedRequest request) => _received.Enqueue(request);

    /// <summary>Everything received so far, in arrival order.</summary>
    /// <param name="slot">Optional slot filter.</param>
    public IReadOnlyList<ReceivedRequest> Received(string? slot = null) =>
    [
        .. _received
            .Where(request => slot is null || string.Equals(request.Slot, slot, StringComparison.Ordinal))
            .OrderBy(static request => request.Sequence)
    ];

    /// <summary>Clears recorded requests and slot configuration.</summary>
    public void Reset()
    {
        _received.Clear();
        _slots.Clear();
        Interlocked.Exchange(ref _sequence, 0);
    }
}
