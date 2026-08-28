using HookRelay.Domain.Outbox;
using HookRelay.Infrastructure.Outbox;
using HookRelay.Infrastructure.Persistence;

namespace HookRelay.Api;

/// <summary>
/// A stand-in producer, so the pipeline can be driven end to end without a second service.
/// </summary>
/// <remarks>
/// A real producer calls <see cref="IWebhookEventPublisher"/> inside the transaction that performs its own
/// business write. This route does the same thing with nothing but the outbox row in the transaction, which
/// is what makes the demo and the test suite able to publish events on demand.
/// </remarks>
public static class EventRoutes
{
    /// <summary>Maps the event publishing route.</summary>
    /// <param name="routes">Route builder.</param>
    public static IEndpointRouteBuilder MapEventRoutes(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapPost("/v1/events", PublishAsync)
            .WithTags("Events")
            .WithName("PublishEvent");

        return routes;
    }

    private static async Task<IResult> PublishAsync(
        PublishEventRequest body,
        HttpRequest request,
        HookRelayDbContext dbContext,
        IWebhookEventPublisher publisher,
        CancellationToken cancellationToken)
    {
        if (!RequestContext.TryGetTenantId(request, out Guid tenantId))
        {
            return RequestContext.MissingTenant();
        }

        if (!RequestContext.TryValidate(body, out IResult validationProblem))
        {
            return validationProblem;
        }

        OutboxMessage message = publisher.PublishJson(
            tenantId,
            body.EventType,
            body.AggregateId,
            body.Payload.GetRawText());

        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Accepted(
            $"/v1/events/{message.Id}",
            new PublishEventResponse(message.Id));
    }
}
