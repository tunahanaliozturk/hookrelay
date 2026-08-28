using HookRelay.Domain.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HookRelay.Infrastructure.Persistence.Configurations;

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages");
        builder.HasKey(message => message.Id);

        builder.Property(message => message.Sequence)
            .UseIdentityAlwaysColumn()
            .ValueGeneratedOnAdd();

        builder.Property(message => message.EventType).HasMaxLength(128).IsRequired();
        builder.Property(message => message.AggregateId).HasMaxLength(128).IsRequired();
        builder.Property(message => message.OrderingKey).HasMaxLength(256).IsRequired();
        builder.Property(message => message.PayloadJson).HasColumnType("jsonb").IsRequired();
        builder.Property(message => message.Status).HasConversion<int>();

        builder.HasIndex(message => message.Sequence).IsUnique();

        // The relay claim is "oldest pending row per ordering key". Both halves of that query, the outer
        // scan and the not-exists probe, ride this index, which is why it leads with status.
        builder.HasIndex(message => new { message.Status, message.OrderingKey, message.Sequence })
            .HasDatabaseName("ix_outbox_messages_claim");
    }
}
