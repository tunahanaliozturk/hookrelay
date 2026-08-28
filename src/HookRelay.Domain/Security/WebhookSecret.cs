using System.Buffers.Text;
using System.Security.Cryptography;

namespace HookRelay.Domain.Security;

/// <summary>Generates endpoint signing secrets.</summary>
public static class WebhookSecret
{
    /// <summary>Prefix that makes a leaked secret obvious in a log or a code search.</summary>
    public const string Prefix = "whsec_";

    private const int EntropyInBytes = 32;

    /// <summary>Creates a new signing secret with 256 bits of entropy.</summary>
    public static string Generate() =>
        Prefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(EntropyInBytes));
}
