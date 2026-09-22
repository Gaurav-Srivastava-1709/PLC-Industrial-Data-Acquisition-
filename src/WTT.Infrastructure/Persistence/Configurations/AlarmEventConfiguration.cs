using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WTT.Domain.Entities;

namespace WTT.Infrastructure.Persistence.Configurations;

public sealed class AlarmEventConfiguration : IEntityTypeConfiguration<AlarmEvent>
{
    public void Configure(EntityTypeBuilder<AlarmEvent> builder)
    {
        builder.HasKey(a => a.Id);

        builder.Property(a => a.DeviceName).IsRequired().HasMaxLength(200);
        builder.Property(a => a.TagName).IsRequired().HasMaxLength(200);
        builder.Property(a => a.EngineeringUnit).HasMaxLength(50);
        builder.Property(a => a.Description).HasMaxLength(500);
        builder.Property(a => a.AcknowledgedBy).HasMaxLength(100);

        builder.HasOne(a => a.DeviceTag)
               .WithMany()
               .HasForeignKey(a => a.DeviceTagId)
               .OnDelete(DeleteBehavior.Cascade); // If tag is deleted, delete its alarms

        // Indexes for fast queries
        builder.HasIndex(a => a.TriggeredAtUtc);
        builder.HasIndex(a => new { a.Priority, a.State });
        
        // Note: Filtered index for active alarms is handled in the migration SQL
    }
}
