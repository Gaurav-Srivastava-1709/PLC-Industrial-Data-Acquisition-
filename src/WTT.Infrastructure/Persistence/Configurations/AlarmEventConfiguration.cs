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

        // ---- Indexes, modelled in EF so `dotnet ef migrations` owns them ----
        // Every index below backs a query that actually exists in AlarmEvaluationService.
        // They are declared here (not hand-written as raw SQL) so that:
        //   * they are discoverable by EF and applied by the migrations bundle,
        //   * the model snapshot and the live database can never drift apart.

        // History page + GetAlarmHistoryAsync paging: ORDER BY TriggeredAtUtc DESC.
        builder.HasIndex(a => a.TriggeredAtUtc);

        // GetActiveAlarmsAsync():
        //   .Where(a => a.State == Active || a.State == Acknowledged)
        //   .OrderByDescending(a => a.Priority).ThenByDescending(a => a.TriggeredAtUtc)
        // AlarmState is stored as INT (no string conversion): Active = 1, Acknowledged = 2.
        // The filter MUST be (1, 2); (0, 1) would silently exclude Acknowledged alarms —
        // which is precisely the row set this index exists to serve.
        builder.HasIndex(a => new { a.State, a.Priority, a.TriggeredAtUtc })
               .HasDatabaseName("IX_AlarmEvents_ActiveQuery")
               .IsDescending(false, true, true)
               .HasFilter("[State] IN (1, 2)")
               .IncludeProperties(a => new
               {
                   a.Id,
                   a.DeviceName,
                   a.TagName,
                   a.AlarmType,
                   a.TriggerValue,
                   a.ThresholdValue,
                   a.EngineeringUnit,
                   a.AcknowledgedAtUtc,
                   a.AcknowledgedBy
               });

        // AlarmRetentionWorker purge: WHERE State = ? AND TriggeredAtUtc < @cutoff.
        builder.HasIndex(a => new { a.State, a.TriggeredAtUtc })
               .HasDatabaseName("IX_AlarmEvents_RetentionCleanup");

        // Cross-process sync (SyncCrossProcessAlarmsAsync): scans TriggeredAtUtc and ClearedAtUtc
        // to reconcile alarms created/changed by the other process.
        builder.HasIndex(a => new { a.TriggeredAtUtc, a.ClearedAtUtc })
               .HasDatabaseName("IX_AlarmEvents_SyncQuery");
    }
}
