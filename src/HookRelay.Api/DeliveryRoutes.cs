using HookRelay.Domain.Deliveries;
using HookRelay.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HookRelay.Api;

/// <summary>Delivery log, dead-letter browsing, and replay routes.</summary>
public static class DeliveryRoutes
{
    private const int MaxPageSize = 200;

    /// <summary>Maps the delivery and dead-letter routes.</summary>
    /// <param name="routes">Route builder.</param>
    public static IEndpointRouteBuilder MapDeliveryRoutes(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        RouteGroupBuilder endpoints = routes.MapGroup("/v1/endpoints").WithTags("Deliveries");
        endpoints.MapGet("/{id:guid}/deliveries", ListForEndpointAsync).WithName("ListDeliveries");
        endpoints.MapGet("/{id:guid}/dead-letters", ListDeadLettersAsync).WithName("ListDeadLetters");
        endpoints.MapPost("/{id:guid}/replay-dead-letters", ReplayAllAsync).WithName("ReplayDeadLetters");

        RouteGroupBuilder deliveries = routes.MapGroup("/v1/deliveries").WithTags("Deliveries");
        deliveries.MapGet("/{id:guid}", GetAsync).WithName("GetDelivery");
        deliveries.MapPost("/{id:guid}/replay", ReplayOneAsync).WithName("ReplayDelivery");

        return routes;
    }

    private static async Task<IResult> ListForEndpointAsync(
        Guid id,
        DeliveryStatus? status,
        int? limit,
        HttpRequest request,
        HookRelayDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (!RequestContext.TryGetTenantId(request, out Guid tenantId))
        {
            return RequestContext.MissingTenant();
        }

        int pageSize = Math.Clamp(limit ?? 50, 1, MaxPageSize);

        List<DeliveryResponse> deliveries = await dbContext.Deliveries
            .AsNoTracking()
            .Where(delivery => delivery.EndpointId == id
                && delivery.TenantId == tenantId
                && (status == null || delivery.Status == status))
            .OrderByDescending(delivery => delivery.CreatedAtUtc)
            .Take(pageSize)
            .Select(delivery => new DeliveryResponse(
                delivery.Id,
                delivery.EndpointId,
                delivery.EventType,
                delivery.Status,
                delivery.AttemptCount,
                delivery.CreatedAtUtc,
                delivery.NextAttemptAtUtc,
                delivery.CompletedAtUtc,
                delivery.LastError))
            .ToListAsync(cancellationToken);

        return Results.Ok(deliveries);
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        HttpRequest request,
        HookRelayDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (!RequestContext.TryGetTenantId(request, out Guid tenantId))
        {
            return RequestContext.MissingTenant();
        }

        DeliveryResponse? delivery = await dbContext.Deliveries
            .AsNoTracking()
            .Where(candidate => candidate.Id == id && candidate.TenantId == tenantId)
            .Select(candidate => new DeliveryResponse(
                candidate.Id,
                candidate.EndpointId,
                candidate.EventType,
                candidate.Status,
                candidate.AttemptCount,
                candidate.CreatedAtUtc,
                candidate.NextAttemptAtUtc,
                candidate.CompletedAtUtc,
                candidate.LastError))
            .FirstOrDefaultAsync(cancellationToken);

        if (delivery is null)
        {
            return Results.NotFound();
        }

        List<AttemptResponse> attempts = await dbContext.DeliveryAttempts
            .AsNoTracking()
            .Where(attempt => attempt.DeliveryId == id)
            .OrderBy(attempt => attempt.AttemptNumber)
            .Select(attempt => new AttemptResponse(
                attempt.AttemptNumber,
                attempt.Outcome,
                attempt.StatusCode,
                attempt.LatencyMs,
                attempt.ResponseSnippet,
                attempt.Error,
                attempt.AttemptedAtUtc,
                attempt.NextAttemptAtUtc))
            .ToListAsync(cancellationToken);

        return Results.Ok(new DeliveryDetailResponse(delivery, attempts));
    }

    private static async Task<IResult> ListDeadLettersAsync(
        Guid id,
        int? limit,
        HttpRequest request,
        HookRelayDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (!RequestContext.TryGetTenantId(request, out Guid tenantId))
        {
            return RequestContext.MissingTenant();
        }

        int pageSize = Math.Clamp(limit ?? 50, 1, MaxPageSize);

        List<DeadLetterResponse> deadLetters = await dbContext.DeadLetters
            .AsNoTracking()
            .Where(deadLetter => deadLetter.EndpointId == id && deadLetter.TenantId == tenantId)
            .OrderByDescending(deadLetter => deadLetter.DeadLetteredAtUtc)
            .Take(pageSize)
            .Select(deadLetter => new DeadLetterResponse(
                deadLetter.Id,
                deadLetter.DeliveryId,
                deadLetter.EventType,
                deadLetter.FailureReason,
                deadLetter.AttemptCount,
                deadLetter.DeadLetteredAtUtc,
                deadLetter.ReplayedAtUtc,
                deadLetter.ReplayCount))
            .ToListAsync(cancellationToken);

        return Results.Ok(deadLetters);
    }

    private static async Task<IResult> ReplayOneAsync(
        Guid id,
        HttpRequest request,
        HookRelayDbContext dbContext,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!RequestContext.TryGetTenantId(request, out Guid tenantId))
        {
            return RequestContext.MissingTenant();
        }

        Delivery? delivery = await dbContext.Deliveries
            .FirstOrDefaultAsync(
                candidate => candidate.Id == id && candidate.TenantId == tenantId,
                cancellationToken);

        if (delivery is null)
        {
            return Results.NotFound();
        }

        if (delivery.Status is not DeliveryStatus.DeadLettered)
        {
            return Results.Problem(
                detail: $"Only dead-lettered deliveries can be replayed. This one is {delivery.Status}.",
                statusCode: StatusCodes.Status409Conflict,
                title: "Not replayable");
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        delivery.Replay(now);

        DeadLetter? deadLetter = await dbContext.DeadLetters
            .FirstOrDefaultAsync(candidate => candidate.DeliveryId == id, cancellationToken);
        deadLetter?.MarkReplayed(now);

        await dbContext.SaveChangesAsync(cancellationToken);

        // Nothing is published here. The delivery goes back to pending and the dispatcher picks it up on
        // its next pass, which keeps the replay path identical to the normal one instead of a shortcut
        // that skips the head-of-line claim and quietly breaks ordering.
        return Results.Accepted($"/v1/deliveries/{delivery.Id}");
    }

    private static async Task<IResult> ReplayAllAsync(
        Guid id,
        HttpRequest request,
        HookRelayDbContext dbContext,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!RequestContext.TryGetTenantId(request, out Guid tenantId))
        {
            return RequestContext.MissingTenant();
        }

        List<Delivery> deliveries = await dbContext.Deliveries
            .Where(delivery => delivery.EndpointId == id
                && delivery.TenantId == tenantId
                && delivery.Status == DeliveryStatus.DeadLettered)
            .ToListAsync(cancellationToken);

        if (deliveries.Count == 0)
        {
            return Results.Ok(new BulkReplayResponse(0));
        }

        Guid[] deliveryIds = [.. deliveries.Select(static delivery => delivery.Id)];

        List<DeadLetter> deadLetters = await dbContext.DeadLetters
            .Where(deadLetter => deliveryIds.Contains(deadLetter.DeliveryId))
            .ToListAsync(cancellationToken);

        DateTimeOffset now = timeProvider.GetUtcNow();

        foreach (Delivery delivery in deliveries)
        {
            delivery.Replay(now);
        }

        foreach (DeadLetter deadLetter in deadLetters)
        {
            deadLetter.MarkReplayed(now);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Ok(new BulkReplayResponse(deliveries.Count));
    }
}
