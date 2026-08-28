using HookRelay.Domain.Deliveries;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HookRelay.Infrastructure.Persistence.Configurations;

internal sealed class DeliveryConfiguration : IEntityTypeConfiguration<Delivery>
{
    public void Configure(EntityTypeBuilder<Delivery> builder)
    {
        builder.ToTable("deliveries");
        builder.HasKey(delivery => delivery.Id);

        builder.Property(delivery => delivery.EventType).HasMaxLength(128).IsRequired();
        builder.Property(delivery => delivery.PayloadJson).HasColumnType("jsonb").IsRequired();
        builder.Property(delivery => delivery.OrderingKey).HasMaxLength(256).IsRequired();
        builder.Property(delivery => delivery.Status).HasConversion<int>();
        builder.Property(delivery => delivery.LastError).HasMaxLength(1024);

        // The dispatcher claim: due, pending, and head of its ordering key. Partial, because the table is
        // mostly delivered rows and none of them are ever candidates.
        builder.HasIndex(delivery => new { delivery.Status, delivery.NextAttemptAtUtc })
            .HasDatabaseName("ix_deliveries_due")
            .HasFilter("status = 0");

        // Backs the not-exists probe that keeps a key's stream in order.
        builder.HasIndex(delivery => new { delivery.OrderingKey, delivery.Sequence })
            .HasDatabaseName("ix_deliveries_ordering_key")
            .HasFilter("status IN (0, 1)");

        // Reclaiming deliveries stranded by a worker that died mid-attempt.
        builder.HasIndex(delivery => delivery.ClaimedAtUtc)
            .HasDatabaseName("ix_deliveries_stale_claims")
            .HasFilter("status = 1");

        // The customer-facing delivery log, newest first.
        builder.HasIndex(delivery => new { delivery.EndpointId, delivery.CreatedAtUtc })
            .HasDatabaseName("ix_deliveries_endpoint_log")
            .IsDescending(false, true);
    }
}
