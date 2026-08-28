using System.Security.Cryptography;
using HookRelay.Domain.Security;
using HookRelay.Infrastructure.Security;
using Microsoft.Extensions.Options;
using Shouldly;

namespace HookRelay.UnitTests;

/// <summary>Round trips, tamper detection, and the key checks that fail at startup rather than at send time.</summary>
public sealed class SecretProtectionTests
{
    [Fact]
    public void A_protected_secret_round_trips()
    {
        using AesGcmSecretProtector protector = NewProtector();
        string secret = WebhookSecret.Generate();

        protector.Unprotect(protector.Protect(secret)).ShouldBe(secret);
    }

    [Fact]
    public void Protecting_the_same_secret_twice_produces_different_ciphertext()
    {
        // A fresh nonce per call. Without it, equal ciphertexts would leak which endpoints share a secret.
        using AesGcmSecretProtector protector = NewProtector();
        const string Secret = "whsec_same_input_every_time";

        protector.Protect(Secret).ShouldNotBe(protector.Protect(Secret));
    }

    [Fact]
    public void Generated_secrets_are_prefixed_and_unique()
    {
        string[] secrets = [.. Enumerable.Range(0, 100).Select(_ => WebhookSecret.Generate())];

        secrets.ShouldAllBe(secret => secret.StartsWith(WebhookSecret.Prefix, StringComparison.Ordinal));
        secrets.Distinct(StringComparer.Ordinal).Count().ShouldBe(secrets.Length);
    }

    [Fact]
    public void A_tampered_ciphertext_is_rejected_rather_than_decrypted_into_garbage()
    {
        // This is why GCM and not CBC. Silently decrypting into nonsense would mean signing a customer's
        // payload with a secret they never had, and every delivery would fail verification for no visible reason.
        using AesGcmSecretProtector protector = NewProtector();
        string protectedSecret = protector.Protect("whsec_original");

        string[] parts = protectedSecret.Split('.');
        parts[2] = FlipFirstCharacter(parts[2]);

        Should.Throw<CryptographicException>(() => protector.Unprotect(string.Join('.', parts)));
    }

    [Fact]
    public void A_secret_encrypted_with_a_different_key_is_rejected()
    {
        using AesGcmSecretProtector first = NewProtector();
        using AesGcmSecretProtector second = NewProtector();

        Should.Throw<CryptographicException>(() => second.Unprotect(first.Protect("whsec_original")));
    }

    [Theory]
    [InlineData("not-encrypted")]
    [InlineData("v2.a.b.c")]
    [InlineData("v1.a.b")]
    public void A_stored_value_in_an_unknown_format_is_rejected(string value)
    {
        using AesGcmSecretProtector protector = NewProtector();

        Should.Throw<CryptographicException>(() => protector.Unprotect(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64 at all!!")]
    [InlineData("c2hvcnQ=")]
    public void A_bad_key_fails_at_construction_rather_than_at_the_first_delivery(string key)
    {
        Should.Throw<InvalidOperationException>(() => new AesGcmSecretProtector(
            Options.Create(new SecretProtectionOptions { Key = key })));
    }

    private static AesGcmSecretProtector NewProtector() => new(
        Options.Create(new SecretProtectionOptions { Key = AesGcmSecretProtector.GenerateKey() }));

    private static string FlipFirstCharacter(string value) =>
        (value[0] == 'A' ? 'B' : 'A') + value[1..];
}
