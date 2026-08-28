using System.Security.Cryptography;

// One command brings up Postgres, Kafka, the three services, the flaky receiver, and the dashboard.
// The demo in the README is this file plus two curl calls.
IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

IResourceBuilder<PostgresDatabaseResource> database = builder
    .AddPostgres("postgres")
    .WithDataVolume()
    .AddDatabase("hookrelay");

IResourceBuilder<KafkaServerResource> kafka = builder
    .AddKafka("kafka")
    .WithKafkaUI();

// Generated per run. Nothing here needs to outlive the process, and a key committed to a repository is
// worse than no key at all. The Aspire host cannot reference the service assemblies, so this mirrors
// AesGcmSecretProtector.GenerateKey rather than calling it.
string secretProtectionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

IResourceBuilder<ProjectResource> chaosReceiver = builder
    .AddProject<Projects.HookRelay_ChaosReceiver>("chaos-receiver")
    .WithEnvironment("CHAOS_FAILURE_RATE", "0.3")
    .WithEnvironment("CHAOS_LATENCY_MS", "25")
    .WithHttpHealthCheck("/health/live");

IResourceBuilder<ProjectResource> api = builder
    .AddProject<Projects.HookRelay_Api>("api")
    .WithReference(database)
    .WaitFor(database)
    .WithEnvironment("HookRelay__MigrateOnStartup", "true")
    .WithHookRelayDefaults(kafka, secretProtectionKey)
    .WithHttpHealthCheck("/health/ready");

builder.AddProject<Projects.HookRelay_Relay>("relay")
    .WithReference(database)
    .WaitFor(api)
    .WithHookRelayDefaults(kafka, secretProtectionKey);

builder.AddProject<Projects.HookRelay_Worker>("worker")
    .WithReference(database)
    .WaitFor(api)
    .WaitFor(chaosReceiver)
    .WithHookRelayDefaults(kafka, secretProtectionKey);

await builder.Build().RunAsync();

/// <summary>Shared wiring for the three HookRelay services.</summary>
internal static class AppHostExtensions
{
    /// <summary>Applies the Kafka connection, the secret key, and the local-development delivery policy.</summary>
    /// <param name="project">The project resource.</param>
    /// <param name="kafka">The Kafka resource.</param>
    /// <param name="secretProtectionKey">Key used to encrypt signing secrets for this run.</param>
    public static IResourceBuilder<ProjectResource> WithHookRelayDefaults(
        this IResourceBuilder<ProjectResource> project,
        IResourceBuilder<KafkaServerResource> kafka,
        string secretProtectionKey) =>
        project
            .WithReference(kafka)
            .WaitFor(kafka)
            .WithEnvironment("HookRelay__Kafka__BootstrapServers", kafka.Resource.ConnectionStringExpression)
            .WithEnvironment("HookRelay__SecretProtection__Key", secretProtectionKey)

            // The chaos receiver runs on plain http at a loopback address, which the delivery policy blocks
            // by default and should keep blocking anywhere real. Both switches exist so a local run can
            // opt out explicitly rather than by weakening the policy itself.
            .WithEnvironment("HookRelay__Delivery__AllowInsecureHttp", "true")
            .WithEnvironment("HookRelay__Delivery__AllowPrivateNetworkDestinations", "true");
}
