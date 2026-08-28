using HookRelay.Domain.Deliveries;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HookRelay.Infrastructure.Persistence.Configurations;

internal sealed class DeliveryAttemptConfiguration : IEntityTypeConfiguration<DeliveryAttempt>
{
    public void Configure(EntityTypeBuilder<DeliveryAttempt> builder)
    {
        builder.ToTable("delivery_attempts");
        builder.HasKey(attempt => attempt.Id);

        builder.Property(attempt => attempt.Outcome).HasConversion<int>();
        builder.Property(attempt => attempt.ResponseSnippet).HasMaxLength(512);
        builder.Property(attempt => attempt.Error).HasMaxLength(512);

        builder.HasOne<Delivery>()
            .WithMany()
            .HasForeignKey(attempt => attempt.DeliveryId)
            .OnDelete(DeleteBehavior.Cascade);

        // Reading one delivery's attempt history in order. Also what the backoff-adherence check queries.
        builder.HasIndex(attempt => new { attempt.DeliveryId, attempt.AttemptNumber })
            .HasDatabaseName("ix_delivery_attempts_history");

        // The per-endpoint debugging view, and the sweep that enforces the 90 day retention window.
        builder.HasIndex(attempt => new { attempt.EndpointId, attempt.AttemptedAtUtc })
            .HasDatabaseName("ix_delivery_attempts_endpoint")
            .IsDescending(false, true);
    }
}
