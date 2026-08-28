using System.Globalization;
using System.Text;
using HookRelay.Domain.Signing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace HookRelay.ChaosReceiver;

/// <summary>
/// The routes a deliberately unreliable customer endpoint exposes.
/// </summary>
/// <remarks>
/// Kept separate from the host so the integration suite can mount the same routes on its own Kestrel
/// server. The receiver the tests run against is the receiver the demo runs against, not a second
/// implementation that could drift from it.
/// </remarks>
public static class ChaosEndpoints
{
    /// <summary>Maps the delivery target and the control routes.</summary>
    /// <param name="routes">Route builder.</param>
    public static IEndpointRouteBuilder MapChaosEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapPost("/hooks/{slot}", ReceiveAsync).WithName("ReceiveWebhook");

        routes.MapPost("/_chaos/slots/{slot}", (string slot, SlotBehaviour behaviour, ChaosState chaos) =>
        {
            chaos.Configure(slot, behaviour);
            return Results.NoContent();
        })
        .WithName("ConfigureSlot");

        routes.MapGet("/_chaos/received", (string? slot, ChaosState chaos) => Results.Ok(chaos.Received(slot)))
            .WithName("ListReceived");

        routes.MapPost("/_chaos/reset", (ChaosState chaos) =>
        {
            chaos.Reset();
            return Results.NoContent();
        })
        .WithName("ResetChaos");

        return routes;
    }

    private static async Task<IResult> ReceiveAsync(
        string slot,
        HttpRequest request,
        ChaosState chaos,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        SlotBehaviour behaviour = chaos.BehaviourFor(slot);

        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);
        byte[] body = buffer.ToArray();

        SignatureVerificationResult signature = SignatureVerificationResult.MalformedHeader;
        bool verifying = behaviour.Secret is { Length: > 0 };

        if (verifying)
        {
            signature = WebhookSignatureVerifier.Verify(
                request.Headers[WebhookSignature.HeaderName].ToString(),
                body,
                Encoding.UTF8.GetBytes(behaviour.Secret!),
                time.GetUtcNow());
        }

        if (behaviour.LatencyMs > 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(behaviour.LatencyMs), time, cancellationToken);
        }

        bool signatureOk = !verifying || signature is SignatureVerificationResult.Valid;
        bool chosenToFail = behaviour.FailureRate > 0 && Random.Shared.NextDouble() < behaviour.FailureRate;
        bool accepted = signatureOk && !chosenToFail;

        _ = int.TryParse(
            request.Headers[WebhookSignature.AttemptHeaderName].ToString(),
            CultureInfo.InvariantCulture,
            out int attempt);

        chaos.Record(new ReceivedRequest(
            chaos.NextSequence(),
            slot,
            request.Headers[WebhookSignature.DeliveryIdHeaderName].ToString(),
            request.Headers[WebhookSignature.EventTypeHeaderName].ToString(),
            attempt,
            Encoding.UTF8.GetString(body),
            time.GetUtcNow(),
            signature,
            accepted));

        if (!signatureOk)
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        return chosenToFail
            ? Results.StatusCode(behaviour.StatusCodeOnFailure)
            : Results.Ok(new { received = true });
    }
}
