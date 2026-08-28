using HookRelay.Domain.Deliveries;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HookRelay.Infrastructure.Persistence.Configurations;

internal sealed class DeadLetterConfiguration : IEntityTypeConfiguration<DeadLetter>
{
    public void Configure(EntityTypeBuilder<DeadLetter> builder)
    {
        builder.ToTable("dead_letters");
        builder.HasKey(deadLetter => deadLetter.Id);

        builder.Property(deadLetter => deadLetter.EventType).HasMaxLength(128).IsRequired();
        builder.Property(deadLetter => deadLetter.PayloadJson).HasColumnType("jsonb").IsRequired();
        builder.Property(deadLetter => deadLetter.FailureReason).HasMaxLength(1024).IsRequired();

        // One dead-letter row per delivery. A replay updates the row instead of adding another.
        builder.HasIndex(deadLetter => deadLetter.DeliveryId).IsUnique();

        // Bulk replay for one endpoint, and the DLQ depth gauge the alert rule watches.
        builder.HasIndex(deadLetter => new { deadLetter.EndpointId, deadLetter.DeadLetteredAtUtc })
            .HasDatabaseName("ix_dead_letters_endpoint")
            .IsDescending(false, true);
    }
}
