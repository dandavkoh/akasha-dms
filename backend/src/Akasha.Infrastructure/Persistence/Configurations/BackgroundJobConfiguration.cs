using Akasha.Domain.Jobs;
using Akasha.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Akasha.Infrastructure.Persistence.Configurations;

internal sealed class BackgroundJobConfiguration : IEntityTypeConfiguration<BackgroundJob>
{
    public void Configure(EntityTypeBuilder<BackgroundJob> builder)
    {
        builder.HasKey(job => job.Id);

        builder.Property(job => job.TenantId).IsRequired();

        builder.Property(job => job.Type)
            .HasMaxLength(BackgroundJob.MaxTypeLength)
            .IsRequired();

        builder.Property(job => job.Payload).IsRequired();

        builder.Property(job => job.IdempotencyKey)
            .HasMaxLength(BackgroundJob.MaxIdempotencyKeyLength);

        builder.Property(job => job.CorrelationId).HasMaxLength(64).IsRequired();

        builder.Property(job => job.InitiatedBySubject).HasMaxLength(200);

        builder.Property(job => job.InitiatedByProvider).HasMaxLength(100);

        builder.Property(job => job.LastError)
            .HasMaxLength(BackgroundJob.MaxErrorLength);

        builder.Property(job => job.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(job => job.CreatedAtUtc)
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(job => job.ScheduledAtUtc)
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(job => job.StartedAtUtc)
            .HasColumnType("timestamp with time zone");

        builder.Property(job => job.CompletedAtUtc)
            .HasColumnType("timestamp with time zone");

        // Poller: due queued jobs, cheapest first.
        builder.HasIndex(job => new { job.Status, job.ScheduledAtUtc });

        // Enqueue idempotency (rule #34): the same logical operation cannot be enqueued twice.
        builder.HasIndex(job => new { job.TenantId, job.Type, job.IdempotencyKey })
            .IsUnique()
            .HasFilter("idempotency_key IS NOT NULL");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(job => job.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
