using Microsoft.EntityFrameworkCore;
using WTT.Domain.Entities;

namespace WTT.Infrastructure.Persistence;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Device> Devices => Set<Device>();
    public DbSet<DeviceAddress> DeviceAddresses => Set<DeviceAddress>();
    public DbSet<DeviceTag> DeviceTags => Set<DeviceTag>();
    public DbSet<TagReading> TagReadings => Set<TagReading>();
    public DbSet<DeviceServer> DeviceServers => Set<DeviceServer>();
    public DbSet<DeviceQuery> DeviceQueries => Set<DeviceQuery>();
    public DbSet<DeviceQueryColumn> DeviceQueryColumns => Set<DeviceQueryColumn>();
    public DbSet<DeviceTriggerCondition> DeviceTriggerConditions => Set<DeviceTriggerCondition>();
    public DbSet<User> Users => Set<User>();
    public DbSet<TransferBatch> TransferBatches => Set<TransferBatch>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<ServiceRuntimeState> ServiceRuntimeStates => Set<ServiceRuntimeState>();
    public DbSet<DeviceRuntimeState> DeviceRuntimeStates => Set<DeviceRuntimeState>();
    public DbSet<AlarmEvent> AlarmEvents => Set<AlarmEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampAuditFields();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampAuditFields();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void StampAuditFields()
    {
        var now = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries<Domain.Common.AuditableEntity>())
        {
            if (entry.State == EntityState.Added && entry.Entity.CreatedAtUtc == default)
                entry.Entity.CreatedAtUtc = now;
            if (entry.State == EntityState.Modified)
                entry.Entity.UpdatedAtUtc = now;
        }
    }
}
