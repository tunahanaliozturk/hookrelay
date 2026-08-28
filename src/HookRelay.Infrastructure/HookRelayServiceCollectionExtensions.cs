using System.Net;
using System.Net.Sockets;
using HookRelay.Domain.Security;
using HookRelay.Infrastructure.Configuration;
using HookRelay.Infrastructure.Diagnostics;
using HookRelay.Infrastructure.Messaging;
using HookRelay.Infrastructure.Outbox;
using HookRelay.Infrastructure.Persistence;
using HookRelay.Infrastructure.Relay;
using HookRelay.Infrastructure.Security;
using HookRelay.Infrastructure.Sending;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Polly.Registry;

namespace HookRelay.Infrastructure;

/// <summary>Registration for everything in this assembly.</summary>
public static class HookRelayServiceCollectionExtensions
{
    /// <summary>Name of the connection string the database context expects.</summary>
    public const string ConnectionStringName = "hookrelay";

    /// <summary>
    /// Registers configuration, persistence, signing, the queue client, and the delivery pipeline.
    /// Hosts add whichever background roles they run on top.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    public static IServiceCollection AddHookRelayCore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptionsWithValidation<DeliveryOptions>(configuration, DeliveryOptions.SectionName);
        services.AddOptionsWithValidation<RelayOptions>(configuration, RelayOptions.SectionName);
        services.AddOptionsWithValidation<KafkaOptions>(configuration, KafkaOptions.SectionName);
        services.AddOptionsWithValidation<SecretProtectionOptions>(
            configuration,
            SecretProtectionOptions.SectionName);

        services.TryAddTimeProvider();

        services.AddDbContext<HookRelayDbContext>((provider, builder) => builder
            .UseNpgsql(
                configuration.GetConnectionString(ConnectionStringName)
                    ?? throw new InvalidOperationException(
                        $"Connection string '{ConnectionStringName}' is not configured."),
                npgsql => npgsql.MigrationsAssembly(typeof(HookRelayDbContext).Assembly.FullName))
            .UseSnakeCaseNamingConvention());

        services.AddSingleton<ISecretProtector, AesGcmSecretProtector>();
        services.AddSingleton<HookRelayDiagnostics>();
        services.AddSingleton<IDeliverySignalPublisher, KafkaDeliverySignalPublisher>();
        services.AddSingleton<ResiliencePipelineRegistry<Guid>>();
        services.AddSingleton<EndpointResiliencePipelines>();

        services.AddScoped<IWebhookEventPublisher, OutboxEventPublisher>();
        services.AddScoped<IWebhookSender, WebhookSender>();
        services.AddScoped<IDeliveryProcessor, DeliveryProcessor>();

        services.AddDeliveryHttpClient();

        return services;
    }

    /// <summary>Adds the outbox relay and the delivery dispatcher.</summary>
    /// <param name="services">Service collection.</param>
    public static IServiceCollection AddHookRelayRelay(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHostedService<OutboxRelayService>();
        services.AddHostedService<DeliveryDispatcherService>();
        services.AddHostedService<MaintenanceService>();

        return services;
    }

    /// <summary>Adds the queue consumer that performs delivery attempts.</summary>
    /// <param name="services">Service collection.</param>
    public static IServiceCollection AddHookRelayWorker(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHostedService<KafkaTopicInitializer>();
        services.AddHostedService<KafkaDeliveryConsumer>();

        return services;
    }

    private static IServiceCollection AddOptionsWithValidation<TOptions>(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName)
        where TOptions : class
    {
        services.AddOptions<TOptions>()
            .Bind(configuration.GetSection(sectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        return services;
    }

    private static IServiceCollection TryAddTimeProvider(this IServiceCollection services)
    {
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(TimeProvider)))
        {
            services.AddSingleton(TimeProvider.System);
        }

        return services;
    }

    /// <summary>
    /// Configures the outbound client.
    /// </summary>
    /// <remarks>
    /// Redirects are refused and the resolved address is checked before the socket is opened. Validating the
    /// URL at registration time is not enough on its own: a hostname that looked fine then can be repointed
    /// at an internal address afterwards, and a 302 can send an allowed request somewhere that never would
    /// have passed the check. Both are closed here, at connect time, where the real address is known.
    /// </remarks>
    private static IServiceCollection AddDeliveryHttpClient(this IServiceCollection services)
    {
        services.AddHttpClient(WebhookSender.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(provider =>
            {
                DeliveryOptions options = provider
                    .GetRequiredService<IOptions<DeliveryOptions>>()
                    .Value;

                return new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    UseCookies = false,
                    AutomaticDecompression = DecompressionMethods.All,
                    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                    ConnectTimeout = TimeSpan.FromSeconds(5),
                    ConnectCallback = (context, cancellationToken) =>
                        ConnectGuardedAsync(context, options, cancellationToken),
                };
            })
            .SetHandlerLifetime(TimeSpan.FromMinutes(5));

        return services;
    }

    private static async ValueTask<Stream> ConnectGuardedAsync(
        SocketsHttpConnectionContext context,
        DeliveryOptions options,
        CancellationToken cancellationToken)
    {
        IPAddress[] resolved = await Dns.GetHostAddressesAsync(
            context.DnsEndPoint.Host,
            cancellationToken);

        IPAddress[] permitted = options.AllowPrivateNetworkDestinations
            ? resolved
            : [.. resolved.Where(static address => !WebhookUrlPolicy.IsPrivate(address))];

        if (permitted.Length == 0)
        {
            throw new HttpRequestException(
                $"{context.DnsEndPoint.Host} resolves only to addresses inside a blocked range.");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(permitted, context.DnsEndPoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
