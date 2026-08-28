using System.Buffers.Text;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using HookRelay.Domain.Security;
using Microsoft.Extensions.Options;

namespace HookRelay.Infrastructure.Security;

/// <summary>Key material for <see cref="AesGcmSecretProtector"/>.</summary>
public sealed class SecretProtectionOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "HookRelay:SecretProtection";

    /// <summary>
    /// Base64 encoded 256 bit key.
    /// </summary>
    /// <remarks>
    /// In a real deployment this is a data-encryption key wrapped by a KMS, not a value sitting in
    /// configuration. Keeping it here makes the demo runnable with one environment variable; the ADR spells
    /// out what changes when it moves behind a KMS.
    /// </remarks>
    [Required]
    public string Key { get; init; } = string.Empty;
}

/// <summary>
/// AES-256-GCM protection for signing secrets at rest.
/// </summary>
/// <remarks>
/// Stored form is <c>v1.nonce.ciphertext.tag</c>, base64url per segment. GCM rather than CBC because the
/// authentication tag means a tampered ciphertext fails loudly instead of decrypting into garbage that then
/// gets used to sign a customer's payload. The version prefix is what makes a future key rotation possible
/// without a flag-day migration of the whole table.
/// </remarks>
public sealed class AesGcmSecretProtector : ISecretProtector, IDisposable
{
    private const string Version = "v1";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly AesGcm _aes;

    /// <summary>Creates the protector.</summary>
    /// <param name="options">Key material.</param>
    public AesGcmSecretProtector(IOptions<SecretProtectionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        byte[] key;
        try
        {
            key = Convert.FromBase64String(options.Value.Key);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                $"{SecretProtectionOptions.SectionName}:Key must be base64 encoded.",
                exception);
        }

        if (key.Length != 32)
        {
            throw new InvalidOperationException(
                $"{SecretProtectionOptions.SectionName}:Key must decode to 32 bytes, got {key.Length}.");
        }

        _aes = new AesGcm(key, TagSize);
        CryptographicOperations.ZeroMemory(key);
    }

    /// <summary>Generates a fresh key, base64 encoded, for first-run setup.</summary>
    public static string GenerateKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <inheritdoc />
    public string Protect(string plaintext)
    {
        ArgumentException.ThrowIfNullOrEmpty(plaintext);

        byte[] plaintextBytes = Encoding.UTF8.GetBytes(plaintext);

        Span<byte> nonce = stackalloc byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        byte[] ciphertext = new byte[plaintextBytes.Length];
        Span<byte> tag = stackalloc byte[TagSize];

        _aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);
        CryptographicOperations.ZeroMemory(plaintextBytes);

        return string.Concat(
            Version,
            ".",
            Base64Url.EncodeToString(nonce),
            ".",
            Base64Url.EncodeToString(ciphertext),
            ".",
            Base64Url.EncodeToString(tag));
    }

    /// <inheritdoc />
    public string Unprotect(string protectedValue)
    {
        ArgumentException.ThrowIfNullOrEmpty(protectedValue);

        string[] segments = protectedValue.Split('.');
        if (segments.Length != 4 || segments[0] != Version)
        {
            throw new CryptographicException("Protected secret is not in the expected v1 format.");
        }

        byte[] nonce = Base64Url.DecodeFromChars(segments[1]);
        byte[] ciphertext = Base64Url.DecodeFromChars(segments[2]);
        byte[] tag = Base64Url.DecodeFromChars(segments[3]);

        if (nonce.Length != NonceSize || tag.Length != TagSize)
        {
            throw new CryptographicException("Protected secret has an invalid nonce or tag length.");
        }

        byte[] plaintext = new byte[ciphertext.Length];
        try
        {
            _aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _aes.Dispose();
}
