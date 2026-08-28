using HookRelay.Domain.Deliveries;
using HookRelay.Domain.Endpoints;
using HookRelay.Domain.Outbox;
using Microsoft.EntityFrameworkCore;

namespace HookRelay.Infrastructure.Persistence;

/// <summary>The single database context. Postgres, snake_case, UTC timestamps.</summary>
/// <param name="options">Provider options.</param>
public sealed class HookRelayDbContext(DbContextOptions<HookRelayDbContext> options)
    : DbContext(options)
{
    /// <summary>Registered destinations.</summary>
    public DbSet<WebhookEndpoint> Endpoints => Set<WebhookEndpoint>();

    /// <summary>Domain events captured by business transactions.</summary>
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    /// <summary>One row per event per destination.</summary>
    public DbSet<Delivery> Deliveries => Set<Delivery>();

    /// <summary>One row per HTTP attempt.</summary>
    public DbSet<DeliveryAttempt> DeliveryAttempts => Set<DeliveryAttempt>();

    /// <summary>Deliveries that exhausted the retry ladder.</summary>
    public DbSet<DeadLetter> DeadLetters => Set<DeadLetter>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(HookRelayDbContext).Assembly);
    }
}
