using System.Text.Json;
using System.Text.Json.Serialization;

namespace HookRelay.Infrastructure.Messaging;

/// <summary>
/// The message that wakes a worker up for one delivery.
/// </summary>
/// <remarks>
/// Deliberately a pointer, not the payload. The delivery row is the source of truth, so a duplicate
/// message costs one wasted database read rather than a duplicate HTTP call, a lost message is picked back
/// up by the dispatcher's due-poll, and an endpoint that was paused between dispatch and consumption is
/// noticed by the worker instead of being acted on from a stale copy.
/// </remarks>
/// <param name="DeliveryId">Which delivery to attempt.</param>
/// <param name="EndpointId">Destination endpoint, carried for log correlation.</param>
/// <param name="OrderingKey">Ordering scope. Also the partition key.</param>
/// <param name="Attempt">Attempt number this signal was published for, used to spot duplicates in the logs.</param>
public readonly record struct DeliverySignal(
    Guid DeliveryId,
    Guid EndpointId,
    string OrderingKey,
    int Attempt);

/// <summary>Source-generated serialisation, so the hot publish path never touches reflection.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(DeliverySignal))]
public sealed partial class MessagingJsonContext : JsonSerializerContext;

/// <summary>Serialisation helpers for <see cref="DeliverySignal"/>.</summary>
public static class DeliverySignalSerializer
{
    /// <summary>Serialises a signal to UTF-8 bytes.</summary>
    /// <param name="signal">The signal.</param>
    public static byte[] Serialize(in DeliverySignal signal) =>
        JsonSerializer.SerializeToUtf8Bytes(signal, MessagingJsonContext.Default.DeliverySignal);

    /// <summary>Reads a signal back. Returns false for anything that is not a well-formed signal.</summary>
    /// <param name="utf8">Raw message bytes.</param>
    /// <param name="signal">The parsed signal.</param>
    public static bool TryDeserialize(ReadOnlySpan<byte> utf8, out DeliverySignal signal)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8);
            signal = JsonSerializer.Deserialize(ref reader, MessagingJsonContext.Default.DeliverySignal);
            return signal.DeliveryId != Guid.Empty;
        }
        catch (JsonException)
        {
            signal = default;
            return false;
        }
    }
}
