using HookRelay.Domain.Deliveries;
using HookRelay.Infrastructure.Configuration;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.Registry;

namespace HookRelay.Infrastructure.Sending;

/// <summary>
/// One circuit breaker per endpoint, never one shared across the fleet.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole isolation story in one type. A single shared pipeline would let one customer's dead
/// endpoint open a circuit that stops delivery to everyone, which is the failure mode that turns one
/// customer's outage into an incident. Keying the registry by endpoint id gives each destination its own
/// failure counters, its own open and half-open state, and its own recovery probe.
/// </para>
/// <para>
/// Polly's breaker samples a failure ratio rather than counting consecutive failures. Pairing a ratio of
/// 1.0 with a minimum throughput of N gives the behaviour the docs describe: the circuit opens once N
/// calls inside the sampling window have all failed.
/// </para>
/// <para>
/// Known ceiling: the registry holds one pipeline per endpoint for the life of the process and does not
/// evict. At tens of thousands of endpoints per worker that is worth an eviction policy keyed on last use.
/// </para>
/// </remarks>
public sealed class EndpointResiliencePipelines(
    ResiliencePipelineRegistry<Guid> registry,
    IOptions<DeliveryOptions> options)
{
    private readonly ResiliencePipelineRegistry<Guid> _registry =
        registry ?? throw new ArgumentNullException(nameof(registry));

    private readonly DeliveryOptions _options =
        options?.Value ?? throw new ArgumentNullException(nameof(options));

    /// <summary>Gets, and on first use builds, the pipeline for one endpoint.</summary>
    /// <param name="endpointId">Destination endpoint.</param>
    public ResiliencePipeline<SendResult> For(Guid endpointId) =>
        _registry.GetOrAddPipeline<SendResult>(endpointId, builder => builder
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<SendResult>
            {
                FailureRatio = 1.0,
                MinimumThroughput = _options.CircuitMinimumThroughput,
                SamplingDuration = _options.CircuitSamplingDuration,
                BreakDuration = _options.CircuitBreakDuration,
                ShouldHandle = static arguments => ValueTask.FromResult(IsFailure(arguments.Outcome)),
            })
            .Build());

    private static bool IsFailure(Outcome<SendResult> outcome) =>
        outcome.Exception is not null || !outcome.Result.IsSuccess;
}
