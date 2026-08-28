using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace HookRelay.Domain.Signing;

/// <summary>
/// Reference receiver-side verification of the <c>X-HookRelay-Signature</c> header.
/// </summary>
/// <remarks>
/// <para>
/// This is the implementation customers are pointed at in the docs, and it is the same code the
/// chaos receiver runs, so the accept path and all three reject paths are exercised on every test run
/// rather than living in a README that drifted.
/// </para>
/// <para>
/// Two properties matter here and both are easy to get wrong. The comparison is constant time, because
/// a byte-by-byte early exit leaks how much of a forged signature was correct. And the timestamp is
/// checked in both directions, because tolerating arbitrarily far-future timestamps re-opens the replay
/// window that signing the timestamp was meant to close.
/// </para>
/// </remarks>
public static class WebhookSignatureVerifier
{
    /// <summary>Replay window used when a caller does not pick one.</summary>
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromMinutes(5);

    /// <summary>Verifies a signature header against a body.</summary>
    /// <param name="header">Raw <c>X-HookRelay-Signature</c> value.</param>
    /// <param name="body">The exact bytes read off the wire, before any deserialisation.</param>
    /// <param name="secret">The signing secret shared with the sender.</param>
    /// <param name="now">Current time, for the replay-window check.</param>
    /// <param name="tolerance">How far the signed timestamp may be from <paramref name="now"/>. Defaults to five minutes.</param>
    public static SignatureVerificationResult Verify(
        ReadOnlySpan<char> header,
        ReadOnlySpan<byte> body,
        ReadOnlySpan<byte> secret,
        DateTimeOffset now,
        TimeSpan? tolerance = null)
    {
        if (!TryParseTimestamp(header, out long unixSeconds))
        {
            return SignatureVerificationResult.MalformedHeader;
        }

        TimeSpan window = tolerance ?? DefaultTolerance;
        TimeSpan drift = now - DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        if (drift > window || drift < -window)
        {
            return SignatureVerificationResult.TimestampOutsideTolerance;
        }

        Span<byte> expected = stackalloc byte[WebhookSignature.HmacSizeInBytes];
        WebhookSignature.ComputeMac(secret, unixSeconds, body, expected);

        bool sawCandidate = false;
        Span<byte> candidate = stackalloc byte[WebhookSignature.HmacSizeInBytes];
        bool matched = false;

        // A header may carry more than one v1 value. That is what makes a zero-downtime secret
        // rotation possible on the receiver's side: it accepts old and new for the overlap window.
        foreach (Range segment in SplitOnComma(header))
        {
            ReadOnlySpan<char> part = header[segment].Trim();
            if (!part.StartsWith("v1=", StringComparison.Ordinal))
            {
                continue;
            }

            ReadOnlySpan<char> hex = part[3..];
            if (hex.Length != WebhookSignature.HmacSizeInBytes * 2)
            {
                continue;
            }

            if (Convert.FromHexString(hex, candidate, out _, out int decoded) != OperationStatus.Done
                || decoded != WebhookSignature.HmacSizeInBytes)
            {
                continue;
            }

            sawCandidate = true;

            // Deliberately not short-circuiting the loop: comparing every candidate keeps the work
            // independent of which one matches.
            matched |= CryptographicOperations.FixedTimeEquals(expected, candidate);
        }

        if (!sawCandidate)
        {
            return SignatureVerificationResult.MalformedHeader;
        }

        return matched ? SignatureVerificationResult.Valid : SignatureVerificationResult.SignatureMismatch;
    }

    /// <summary>String-friendly overload for sample code and tests.</summary>
    /// <param name="header">Raw <c>X-HookRelay-Signature</c> value.</param>
    /// <param name="body">The exact body text read off the wire.</param>
    /// <param name="secret">The signing secret shared with the sender.</param>
    /// <param name="now">Current time, for the replay-window check.</param>
    /// <param name="tolerance">How far the signed timestamp may be from <paramref name="now"/>.</param>
    public static SignatureVerificationResult Verify(
        string? header,
        string body,
        string secret,
        DateTimeOffset now,
        TimeSpan? tolerance = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(secret);

        return Verify(
            header.AsSpan(),
            Encoding.UTF8.GetBytes(body),
            Encoding.UTF8.GetBytes(secret),
            now,
            tolerance);
    }

    private static bool TryParseTimestamp(ReadOnlySpan<char> header, out long unixSeconds)
    {
        foreach (Range segment in SplitOnComma(header))
        {
            ReadOnlySpan<char> part = header[segment].Trim();
            if (part.StartsWith("t=", StringComparison.Ordinal)
                && long.TryParse(part[2..], NumberStyles.None, CultureInfo.InvariantCulture, out unixSeconds))
            {
                return true;
            }
        }

        unixSeconds = 0;
        return false;
    }

    private static SplitEnumerator SplitOnComma(ReadOnlySpan<char> value) => new(value);

    /// <summary>Allocation-free comma splitter. Verification runs per inbound request on the receiver side.</summary>
    private ref struct SplitEnumerator(ReadOnlySpan<char> value)
    {
        private readonly ReadOnlySpan<char> _value = value;
        private int _next;
        private bool _done;

        public Range Current { get; private set; }

        public readonly SplitEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            if (_done)
            {
                return false;
            }

            int index = _value[_next..].IndexOf(',');
            if (index < 0)
            {
                Current = new Range(_next, _value.Length);
                _done = true;
                return _next <= _value.Length;
            }

            Current = new Range(_next, _next + index);
            _next += index + 1;
            return true;
        }
    }
}
