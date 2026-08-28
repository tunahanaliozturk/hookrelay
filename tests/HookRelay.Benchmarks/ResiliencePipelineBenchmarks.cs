using BenchmarkDotNet.Attributes;
using HookRelay.Domain.Deliveries;
using HookRelay.Infrastructure.Configuration;
using HookRelay.Infrastructure.Sending;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Registry;

namespace HookRelay.Benchmarks;

/// <summary>
/// What the per-endpoint resilience pipeline costs on a call that succeeds.
/// </summary>
/// <remarks>
/// Giving every endpoint its own circuit breaker is the isolation guarantee, and the obvious worry is what
/// that costs once there are a lot of endpoints. Two things are measured: the overhead the pipeline adds to
/// a successful call, and the cost of looking the pipeline up by endpoint id on every attempt.
/// </remarks>
[MemoryDiagnoser]
[HideColumns("Job", "Error", "StdDev")]
public class ResiliencePipelineBenchmarks
{
    private static readonly SendResult Success = new(AttemptOutcome.Success, 200, TimeSpan.Zero, null, null);

    private EndpointResiliencePipelines _pipelines = null!;
    private Guid[] _endpointIds = [];
    private int _cursor;

    /// <summary>How many distinct endpoints the registry holds.</summary>
    [Params(1, 1_000)]
    public int EndpointCount { get; set; }

    /// <summary>Builds the registry and warms every pipeline.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _pipelines = new EndpointResiliencePipelines(
            new ResiliencePipelineRegistry<Guid>(),
            Options.Create(new DeliveryOptions()));

        _endpointIds = [.. Enumerable.Range(0, EndpointCount).Select(static _ => Guid.NewGuid())];

        foreach (Guid endpointId in _endpointIds)
        {
            _pipelines.For(endpointId);
        }
    }

    /// <summary>Looking up the pipeline for the next endpoint in rotation.</summary>
    [Benchmark(Baseline = true)]
    public ResiliencePipeline<SendResult> Lookup() =>
        _pipelines.For(_endpointIds[_cursor++ % _endpointIds.Length]);

    /// <summary>A successful call all the way through the breaker.</summary>
    [Benchmark]
    public async ValueTask<SendResult> ExecuteThroughPipeline() =>
        await _pipelines
            .For(_endpointIds[_cursor++ % _endpointIds.Length])
            .ExecuteAsync(static _ => ValueTask.FromResult(Success));
}
