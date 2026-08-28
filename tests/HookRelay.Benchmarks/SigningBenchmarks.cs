using System.Text;
using BenchmarkDotNet.Attributes;
using HookRelay.Domain.Signing;

namespace HookRelay.Benchmarks;

/// <summary>
/// What signing actually costs per delivery.
/// </summary>
/// <remarks>
/// Signing sits on the hot path of every attempt, so the question worth answering is whether it is a
/// rounding error next to the HTTP call or something that needs attention at high fan-out. The payload
/// sizes bracket a realistic webhook body, and the largest one crosses the threshold where the signer
/// stops using the stack and rents a buffer instead.
/// </remarks>
[MemoryDiagnoser]
[HideColumns("Job", "Error", "StdDev")]
public class SigningBenchmarks
{
    private static readonly DateTimeOffset Timestamp = new(2026, 3, 14, 9, 26, 53, TimeSpan.Zero);
    private static readonly byte[] Secret = Encoding.UTF8.GetBytes(
        "whsec_a2V5LWZvci1iZW5jaG1hcmtpbmctb25seS1ub3QtcmVhbA");

    private byte[] _body = [];
    private string _header = string.Empty;

    /// <summary>Payload size in bytes.</summary>
    [Params(256, 4096, 65536)]
    public int PayloadBytes { get; set; }

    /// <summary>Builds the payload for the current parameter set.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _body = Encoding.UTF8.GetBytes(new string('x', PayloadBytes));
        _header = WebhookSignature.Compute(Secret, Timestamp, _body);
    }

    /// <summary>Producing the signature header for one outgoing request.</summary>
    [Benchmark(Baseline = true)]
    public string Sign() => WebhookSignature.Compute(Secret, Timestamp, _body);

    /// <summary>The receiver-side check, which is what a customer pays on every inbound webhook.</summary>
    [Benchmark]
    public SignatureVerificationResult Verify() =>
        WebhookSignatureVerifier.Verify(_header, _body, Secret, Timestamp);
}
