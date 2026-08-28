using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace HookRelay.Domain.Signing;

/// <summary>
/// Computes the <c>X-HookRelay-Signature</c> header value.
/// </summary>
/// <remarks>
/// <para>
/// The header looks like <c>t=1735689600,v1=9f86d081...</c> and the MAC is computed over
/// <c>"{timestamp}.{raw request body}"</c> with HMAC-SHA256. That construction is deliberately the
/// one Stripe publishes: receivers that already integrate with Stripe recognise it, and folding the
/// timestamp into the signed material is what lets a receiver bound how old a replayed payload can be.
/// </para>
/// <para>
/// Signing sits on the per-delivery hot path, so this type never allocates a hasher, never builds an
/// intermediate signed-payload string, and rents its scratch buffer. See
/// <c>tests/HookRelay.Benchmarks</c> for the measured cost.
/// </para>
/// </remarks>
public static class WebhookSignature
{
    /// <summary>Header name carrying the signature.</summary>
    public const string HeaderName = "X-HookRelay-Signature";

    /// <summary>Header name carrying the delivery id, which receivers use to de-duplicate.</summary>
    public const string DeliveryIdHeaderName = "X-HookRelay-Delivery-Id";

    /// <summary>Header name carrying the event type, so a receiver can route without parsing the body.</summary>
    public const string EventTypeHeaderName = "X-HookRelay-Event-Type";

    /// <summary>Header name carrying the 1-based attempt number of this HTTP request.</summary>
    public const string AttemptHeaderName = "X-HookRelay-Attempt";

    internal const int HmacSizeInBytes = 32;

    /// <summary>Builds the full header value for a payload.</summary>
    /// <param name="secret">The endpoint's signing secret, in raw bytes.</param>
    /// <param name="timestamp">Signing time. Also the replay-window anchor the receiver checks.</param>
    /// <param name="body">The exact bytes that will be written to the request body.</param>
    public static string Compute(ReadOnlySpan<byte> secret, DateTimeOffset timestamp, ReadOnlySpan<byte> body)
    {
        long unixSeconds = timestamp.ToUnixTimeSeconds();
        Span<byte> mac = stackalloc byte[HmacSizeInBytes];
        ComputeMac(secret, unixSeconds, body, mac);

        return $"t={unixSeconds},v1={Convert.ToHexStringLower(mac)}";
    }

    /// <summary>
    /// Writes the raw HMAC of <c>"{unixSeconds}.{body}"</c> into <paramref name="destination"/>.
    /// </summary>
    /// <param name="secret">The endpoint's signing secret, in raw bytes.</param>
    /// <param name="unixSeconds">Signing time as Unix seconds.</param>
    /// <param name="body">The exact bytes that will be written to the request body.</param>
    /// <param name="destination">Receives 32 bytes.</param>
    internal static void ComputeMac(
        ReadOnlySpan<byte> secret,
        long unixSeconds,
        ReadOnlySpan<byte> body,
        Span<byte> destination)
    {
        // Longest possible prefix is 20 timestamp digits plus the separator.
        const int MaxPrefixLength = 21;

        int signedLength = MaxPrefixLength + body.Length;
        byte[]? rented = signedLength > 1024 ? ArrayPool<byte>.Shared.Rent(signedLength) : null;
        Span<byte> scratch = rented ?? stackalloc byte[1024];

        try
        {
            bool formatted = Utf8Formatter.TryFormat(unixSeconds, scratch, out int prefixLength);
            Debug.Assert(formatted, "A long always fits in 20 bytes.");

            scratch[prefixLength++] = (byte)'.';
            body.CopyTo(scratch[prefixLength..]);

            int macLength = HMACSHA256.HashData(secret, scratch[..(prefixLength + body.Length)], destination);
            Debug.Assert(macLength == HmacSizeInBytes, "HMAC-SHA256 is always 32 bytes.");
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    /// <summary>Convenience overload for string secrets and string bodies. Not on the hot path.</summary>
    /// <param name="secret">The endpoint's signing secret.</param>
    /// <param name="timestamp">Signing time.</param>
    /// <param name="body">The request body.</param>
    public static string Compute(string secret, DateTimeOffset timestamp, string body)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentNullException.ThrowIfNull(body);

        return Compute(Encoding.UTF8.GetBytes(secret), timestamp, Encoding.UTF8.GetBytes(body));
    }
}
