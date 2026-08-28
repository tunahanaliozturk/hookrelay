using System.Text;
using System.Text.RegularExpressions;
using HookRelay.Domain.Signing;
using Shouldly;

namespace HookRelay.UnitTests;

/// <summary>
/// The signature suite covers the accept path and every reject path.
/// </summary>
/// <remarks>
/// A verifier tested only against valid signatures proves nothing: it would pass with the body of the
/// method replaced by <c>return Valid</c>. Tampering, expiry, and a wrong secret each get their own test
/// for that reason.
/// </remarks>
public sealed class WebhookSignatureTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 14, 9, 26, 53, TimeSpan.Zero);
    private const string Secret = "whsec_a2V5LWZvci10ZXN0aW5nLW9ubHktbm90LXJlYWw";
    private const string Body = """{"id":"inv_123","type":"invoice.paid","amount":4200}""";

    [Fact]
    public void Compute_produces_the_documented_header_shape()
    {
        string header = WebhookSignature.Compute(Secret, Now, Body);

        header.ShouldMatch(@"^t=\d+,v1=[0-9a-f]{64}$");
        header.ShouldStartWith($"t={Now.ToUnixTimeSeconds()},");
    }

    [Fact]
    public void Compute_is_deterministic_for_the_same_inputs()
    {
        WebhookSignature.Compute(Secret, Now, Body)
            .ShouldBe(WebhookSignature.Compute(Secret, Now, Body));
    }

    [Fact]
    public void Span_and_string_overloads_agree()
    {
        string fromStrings = WebhookSignature.Compute(Secret, Now, Body);
        string fromSpans = WebhookSignature.Compute(
            Encoding.UTF8.GetBytes(Secret),
            Now,
            Encoding.UTF8.GetBytes(Body));

        fromSpans.ShouldBe(fromStrings);
    }

    [Fact]
    public void Verify_accepts_a_signature_it_just_produced()
    {
        string header = WebhookSignature.Compute(Secret, Now, Body);

        WebhookSignatureVerifier.Verify(header, Body, Secret, Now)
            .ShouldBe(SignatureVerificationResult.Valid);
    }

    [Fact]
    public void Verify_rejects_a_tampered_body()
    {
        string header = WebhookSignature.Compute(Secret, Now, Body);
        const string Tampered = """{"id":"inv_123","type":"invoice.paid","amount":99999}""";

        WebhookSignatureVerifier.Verify(header, Tampered, Secret, Now)
            .ShouldBe(SignatureVerificationResult.SignatureMismatch);
    }

    [Fact]
    public void Verify_rejects_a_signature_made_with_a_different_secret()
    {
        string header = WebhookSignature.Compute("whsec_someone_elses_secret", Now, Body);

        WebhookSignatureVerifier.Verify(header, Body, Secret, Now)
            .ShouldBe(SignatureVerificationResult.SignatureMismatch);
    }

    [Fact]
    public void Verify_rejects_a_payload_replayed_after_the_window_closes()
    {
        string header = WebhookSignature.Compute(Secret, Now, Body);
        DateTimeOffset tooLate = Now + TimeSpan.FromMinutes(6);

        WebhookSignatureVerifier.Verify(header, Body, Secret, tooLate)
            .ShouldBe(SignatureVerificationResult.TimestampOutsideTolerance);
    }

    [Fact]
    public void Verify_rejects_a_timestamp_from_the_future()
    {
        // Tolerating an arbitrarily far-future timestamp would hand an attacker a signature that stays
        // valid indefinitely, which is the exact thing the window exists to prevent.
        string header = WebhookSignature.Compute(Secret, Now + TimeSpan.FromHours(1), Body);

        WebhookSignatureVerifier.Verify(header, Body, Secret, Now)
            .ShouldBe(SignatureVerificationResult.TimestampOutsideTolerance);
    }

    [Fact]
    public void Verify_accepts_a_replay_inside_the_window()
    {
        string header = WebhookSignature.Compute(Secret, Now, Body);

        WebhookSignatureVerifier.Verify(header, Body, Secret, Now + TimeSpan.FromMinutes(4))
            .ShouldBe(SignatureVerificationResult.Valid);
    }

    [Fact]
    public void Verify_honours_a_custom_tolerance()
    {
        string header = WebhookSignature.Compute(Secret, Now, Body);

        WebhookSignatureVerifier.Verify(header, Body, Secret, Now + TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(30))
            .ShouldBe(SignatureVerificationResult.TimestampOutsideTolerance);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-signature")]
    [InlineData("v1=abc")]
    [InlineData("t=notanumber,v1=abc")]
    [InlineData("t=1773480413")]
    public void Verify_rejects_a_header_it_cannot_parse(string? header)
    {
        WebhookSignatureVerifier.Verify(header, Body, Secret, Now)
            .ShouldBe(SignatureVerificationResult.MalformedHeader);
    }

    [Fact]
    public void Verify_rejects_a_v1_value_that_is_not_a_32_byte_hex_string()
    {
        WebhookSignatureVerifier.Verify($"t={Now.ToUnixTimeSeconds()},v1=zzzz", Body, Secret, Now)
            .ShouldBe(SignatureVerificationResult.MalformedHeader);
    }

    [Fact]
    public void Verify_accepts_a_header_carrying_the_old_and_new_signature_during_a_rotation()
    {
        // Receivers rotate by accepting both for an overlap window. The header format allows several v1
        // values so a sender can offer both at once.
        string current = ExtractSignature(WebhookSignature.Compute(Secret, Now, Body));
        string stale = ExtractSignature(WebhookSignature.Compute("whsec_previous", Now, Body));
        string header = $"t={Now.ToUnixTimeSeconds()},v1={stale},v1={current}";

        WebhookSignatureVerifier.Verify(header, Body, Secret, Now)
            .ShouldBe(SignatureVerificationResult.Valid);
    }

    [Fact]
    public void Verify_handles_an_empty_body()
    {
        string header = WebhookSignature.Compute(Secret, Now, string.Empty);

        WebhookSignatureVerifier.Verify(header, string.Empty, Secret, Now)
            .ShouldBe(SignatureVerificationResult.Valid);
    }

    [Fact]
    public void Verify_handles_a_body_larger_than_the_stack_buffer()
    {
        // The signer rents from the array pool above a threshold. This is the test that would catch a
        // mistake in that switch.
        string large = new('x', 64 * 1024);
        string header = WebhookSignature.Compute(Secret, Now, large);

        WebhookSignatureVerifier.Verify(header, large, Secret, Now)
            .ShouldBe(SignatureVerificationResult.Valid);
    }

    private static string ExtractSignature(string header) =>
        Regex.Match(header, "v1=([0-9a-f]{64})").Groups[1].Value;
}
