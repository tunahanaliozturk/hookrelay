using HookRelay.Domain.Endpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HookRelay.Infrastructure.Persistence.Configurations;

internal sealed class WebhookEndpointConfiguration : IEntityTypeConfiguration<WebhookEndpoint>
{
    public void Configure(EntityTypeBuilder<WebhookEndpoint> builder)
    {
        builder.ToTable("webhook_endpoints");
        builder.HasKey(endpoint => endpoint.Id);

        builder.Property(endpoint => endpoint.Url).HasMaxLength(2048).IsRequired();
        builder.Property(endpoint => endpoint.Description).HasMaxLength(256).IsRequired();
        builder.Property(endpoint => endpoint.ProtectedSecret).HasMaxLength(512).IsRequired();
        builder.Property(endpoint => endpoint.ProtectedPreviousSecret).HasMaxLength(512);
        builder.Property(endpoint => endpoint.Status).HasConversion<int>();
        builder.Property(endpoint => endpoint.OrderingStrategy).HasConversion<int>();

        // Subscriptions are a Postgres text[] on the backing field. The read-only projection stays
        // unmapped: matching supports wildcards, so it happens in memory against a small cached list
        // rather than in SQL.
        builder.Ignore(endpoint => endpoint.SubscribedEventTypes);
        builder.Property<List<string>>("_subscribedEventTypes")
            .HasColumnName("subscribed_event_types")
            .HasColumnType("text[]")
            .IsRequired();

        // The relay resolves subscribers for a tenant on every fan-out.
        builder.HasIndex(endpoint => new { endpoint.TenantId, endpoint.Status });
    }
}
