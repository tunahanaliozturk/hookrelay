using HookRelay.Domain.Endpoints;
using HookRelay.Domain.Security;
using HookRelay.Infrastructure.Configuration;
using HookRelay.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HookRelay.Api;

/// <summary>Endpoint registration and lifecycle routes.</summary>
public static class EndpointRoutes
{
    /// <summary>Maps the routes under /v1/endpoints.</summary>
    /// <param name="routes">Route builder.</param>
    public static IEndpointRouteBuilder MapEndpointRoutes(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        RouteGroupBuilder group = routes.MapGroup("/v1/endpoints").WithTags("Endpoints");

        group.MapPost("/", RegisterAsync).WithName("RegisterEndpoint");
        group.MapGet("/", ListAsync).WithName("ListEndpoints");
        group.MapGet("/{id:guid}", GetAsync).WithName("GetEndpoint");
        group.MapPut("/{id:guid}/subscriptions", UpdateSubscriptionsAsync).WithName("UpdateSubscriptions");
        group.MapPost("/{id:guid}/pause", PauseAsync).WithName("PauseEndpoint");
        group.MapPost("/{id:guid}/resume", ResumeAsync).WithName("ResumeEndpoint");
        group.MapPost("/{id:guid}/rotate-secret", RotateSecretAsync).WithName("RotateSecret");

        return routes;
    }

    private static async Task<IResult> RegisterAsync(
        RegisterEndpointRequest body,
        HttpRequest request,
        HookRelayDbContext dbContext,
        ISecretProtector secretProtector,
        IOptions<DeliveryOptions> deliveryOptions,
        TimeProvider timeProvider,
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

        DeliveryOptions options = deliveryOptions.Value;
        UrlValidationResult urlCheck = WebhookUrlPolicy.Validate(
            body.Url,
            options.AllowInsecureHttp,
            options.AllowPrivateNetworkDestinations);

        if (urlCheck is not UrlValidationResult.Allowed)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [nameof(body.Url)] = [DescribeUrlProblem(urlCheck)],
            });
        }

        string secret = WebhookSecret.Generate();

        WebhookEndpoint endpoint = WebhookEndpoint.Register(
            tenantId,
            new Uri(body.Url, UriKind.Absolute),
            body.Description ?? string.Empty,
            body.EventTypes,
            secretProtector.Protect(secret),
            body.OrderingStrategy,
            timeProvider.GetUtcNow());

        dbContext.Endpoints.Add(endpoint);
        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Created(
            $"/v1/endpoints/{endpoint.Id}",
            new EndpointWithSecretResponse(EndpointResponse.From(endpoint), secret));
    }

    private static async Task<IResult> ListAsync(
        HttpRequest request,
        HookRelayDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (!RequestContext.TryGetTenantId(request, out Guid tenantId))
        {
            return RequestContext.MissingTenant();
        }

        List<WebhookEndpoint> endpoints = await dbContext.Endpoints
            .AsNoTracking()
            .Where(endpoint => endpoint.TenantId == tenantId)
            .OrderBy(endpoint => endpoint.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        return Results.Ok(endpoints.Select(EndpointResponse.From).ToArray());
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        HttpRequest request,
        HookRelayDbContext dbContext,
        CancellationToken cancellationToken)
    {
        WebhookEndpoint? endpoint = await FindAsync(id, request, dbContext, tracking: false, cancellationToken);
        return endpoint is null ? Results.NotFound() : Results.Ok(EndpointResponse.From(endpoint));
    }

    private static async Task<IResult> UpdateSubscriptionsAsync(
        Guid id,
        UpdateSubscriptionsRequest body,
        HttpRequest request,
        HookRelayDbContext dbContext,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!RequestContext.TryValidate(body, out IResult validationProblem))
        {
            return validationProblem;
        }

        WebhookEndpoint? endpoint = await FindAsync(id, request, dbContext, tracking: true, cancellationToken);
        if (endpoint is null)
        {
            return Results.NotFound();
        }

        endpoint.Resubscribe(body.EventTypes, timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Ok(EndpointResponse.From(endpoint));
    }

    private static Task<IResult> PauseAsync(
        Guid id,
        HttpRequest request,
        HookRelayDbContext dbContext,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) =>
        TransitionAsync(id, request, dbContext, timeProvider, static (endpoint, now) => endpoint.Pause(now), cancellationToken);

    private static Task<IResult> ResumeAsync(
        Guid id,
        HttpRequest request,
        HookRelayDbContext dbContext,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) =>
        TransitionAsync(id, request, dbContext, timeProvider, static (endpoint, now) => endpoint.Resume(now), cancellationToken);

    private static async Task<IResult> RotateSecretAsync(
        Guid id,
        HttpRequest request,
        HookRelayDbContext dbContext,
        ISecretProtector secretProtector,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        WebhookEndpoint? endpoint = await FindAsync(id, request, dbContext, tracking: true, cancellationToken);
        if (endpoint is null)
        {
            return Results.NotFound();
        }

        string secret = WebhookSecret.Generate();
        endpoint.RotateSecret(secretProtector.Protect(secret), timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Ok(new EndpointWithSecretResponse(EndpointResponse.From(endpoint), secret));
    }

    private static async Task<IResult> TransitionAsync(
        Guid id,
        HttpRequest request,
        HookRelayDbContext dbContext,
        TimeProvider timeProvider,
        Action<WebhookEndpoint, DateTimeOffset> transition,
        CancellationToken cancellationToken)
    {
        WebhookEndpoint? endpoint = await FindAsync(id, request, dbContext, tracking: true, cancellationToken);
        if (endpoint is null)
        {
            return Results.NotFound();
        }

        transition(endpoint, timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Ok(EndpointResponse.From(endpoint));
    }

    private static async Task<WebhookEndpoint?> FindAsync(
        Guid id,
        HttpRequest request,
        HookRelayDbContext dbContext,
        bool tracking,
        CancellationToken cancellationToken)
    {
        if (!RequestContext.TryGetTenantId(request, out Guid tenantId))
        {
            return null;
        }

        IQueryable<WebhookEndpoint> query = tracking
            ? dbContext.Endpoints
            : dbContext.Endpoints.AsNoTracking();

        // Tenant is part of the predicate, not checked afterwards: a mismatch has to look like "not found"
        // so the API cannot be used to probe which endpoint ids exist on another tenant.
        return await query.FirstOrDefaultAsync(
            endpoint => endpoint.Id == id && endpoint.TenantId == tenantId,
            cancellationToken);
    }

    private static string DescribeUrlProblem(UrlValidationResult result) => result switch
    {
        UrlValidationResult.NotAbsolute => "Must be an absolute URL.",
        UrlValidationResult.SchemeNotAllowed => "Must use https.",
        UrlValidationResult.CredentialsInUrl => "Must not embed credentials.",
        UrlValidationResult.PrivateNetwork => "Must not point at a private or loopback address.",
        _ => "Not an acceptable destination.",
    };
}
