using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WTT.Domain.Entities;

namespace WTT.Infrastructure.Persistence.Configurations;

public sealed class DeviceConfiguration : IEntityTypeConfiguration<Device>
{
    public void Configure(EntityTypeBuilder<Device> b)
    {
        b.ToTable("Devices");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.HasIndex(x => x.Name).IsUnique();
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.Host).HasMaxLength(253).IsRequired();
        b.Property(x => x.CipPath).HasMaxLength(50);
        b.Property(x => x.TriggerTagName).HasMaxLength(200);
        b.Property(x => x.Protocol).HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.Manufacturer).HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.Model).HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.CaptureMode).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.WordOrder).HasConversion<string>().HasMaxLength(10);

        b.HasMany(x => x.Addresses).WithOne(x => x.Device).HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Tags).WithOne(x => x.Device).HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Queries).WithOne(x => x.Device).HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.TriggerConditions).WithOne(x => x.Device).HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Server).WithOne(x => x.Device).HasForeignKey<DeviceServer>(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class DeviceAddressConfiguration : IEntityTypeConfiguration<DeviceAddress>
{
    public void Configure(EntityTypeBuilder<DeviceAddress> b)
    {
        b.ToTable("DeviceAddresses");
        b.HasKey(x => x.Id);
        b.Property(x => x.TagPrefix).HasMaxLength(50).IsRequired();
        b.Property(x => x.Description).HasMaxLength(200);
        b.Property(x => x.AddressType).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.RegisterType).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.DataType).HasConversion<string>().HasMaxLength(20);
        b.HasIndex(x => x.DeviceId);
        // SQL Server rejects Device -> Tags and Device -> Addresses -> Tags cascade paths.
        // Address deletion explicitly removes generated tags in DeviceAddressService.
        b.HasMany(x => x.Tags).WithOne(x => x.Address).HasForeignKey(x => x.AddressId).OnDelete(DeleteBehavior.NoAction);
    }
}

public sealed class DeviceTagConfiguration : IEntityTypeConfiguration<DeviceTag>
{
    public void Configure(EntityTypeBuilder<DeviceTag> b)
    {
        b.ToTable("DeviceTags");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Alias).HasMaxLength(200);
        b.Property(x => x.PlcAddress).HasMaxLength(500).IsRequired();
        b.Property(x => x.SetValue).HasMaxLength(500);
        b.Property(x => x.CurrentValue).HasMaxLength(500);
        b.Property(x => x.EngineeringUnit).HasMaxLength(30);
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(10);
        b.Property(x => x.DataType).HasConversion<string>().HasMaxLength(10);
        b.Property(x => x.RegisterType).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.CurrentQuality).HasConversion<string>().HasMaxLength(15);
        b.Property(x => x.MinimumWriteIntervalMs).HasDefaultValue(1000);
        b.HasIndex(x => new { x.DeviceId, x.Name }).IsUnique();
        b.HasOne(x => x.Query).WithMany().HasForeignKey(x => x.QueryId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne(x => x.QueryColumn).WithMany().HasForeignKey(x => x.QueryColumnId).OnDelete(DeleteBehavior.NoAction);
        b.Ignore(x => x.DisplayName);
    }
}

public sealed class TagReadingConfiguration : IEntityTypeConfiguration<TagReading>
{
    public void Configure(EntityTypeBuilder<TagReading> b)
    {
        b.ToTable("TagReadings");
        b.HasKey(x => x.Id);
        b.Property(x => x.DeviceName).HasMaxLength(100).IsRequired();
        b.Property(x => x.TagName).HasMaxLength(200).IsRequired();
        b.Property(x => x.Value).HasMaxLength(500).IsRequired();
        b.Property(x => x.Timestamp).HasColumnType("datetime2(3)");
        b.Property(x => x.TimestampUtc).HasColumnType("datetime2(3)");
        b.Property(x => x.TransferredAtUtc).HasColumnType("datetime2(3)");
        b.Property(x => x.Quality).HasConversion<string>().HasMaxLength(15);
        // Transfer service query: WHERE DeviceId = ? AND IsTransferred = 0 ORDER BY Id
        b.HasIndex(x => new { x.DeviceId, x.IsTransferred, x.Id });
        // Retention cleanup query
        b.HasIndex(x => new { x.IsTransferred, x.TimestampUtc });
    }
}

public sealed class DeviceServerConfiguration : IEntityTypeConfiguration<DeviceServer>
{
    public void Configure(EntityTypeBuilder<DeviceServer> b)
    {
        b.ToTable("DeviceServers");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.DeviceId).IsUnique();
        b.Property(x => x.ServerName).HasMaxLength(200).IsRequired();
        b.Property(x => x.DatabaseName).HasMaxLength(128).IsRequired();
        b.Property(x => x.TableName).HasMaxLength(128).IsRequired();
        b.Property(x => x.UserId).HasMaxLength(128);
        b.Property(x => x.ProtectedPassword).HasMaxLength(2000);
        b.Property(x => x.LastError).HasMaxLength(2000);
    }
}

public sealed class DeviceQueryConfiguration : IEntityTypeConfiguration<DeviceQuery>
{
    public void Configure(EntityTypeBuilder<DeviceQuery> b)
    {
        b.ToTable("DeviceQueries");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.ServerName).HasMaxLength(200).IsRequired();
        b.Property(x => x.DatabaseName).HasMaxLength(128).IsRequired();
        b.Property(x => x.UserId).HasMaxLength(128);
        b.Property(x => x.ProtectedPassword).HasMaxLength(2000);
        b.Property(x => x.QueryText).HasMaxLength(8000).IsRequired();
        b.Property(x => x.QueryType).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.LastError).HasMaxLength(2000);
        b.HasIndex(x => new { x.DeviceId, x.Name }).IsUnique();
        b.HasMany(x => x.Columns).WithOne(x => x.Query).HasForeignKey(x => x.QueryId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class DeviceQueryColumnConfiguration : IEntityTypeConfiguration<DeviceQueryColumn>
{
    public void Configure(EntityTypeBuilder<DeviceQueryColumn> b)
    {
        b.ToTable("DeviceQueryColumns");
        b.HasKey(x => x.Id);
        b.Property(x => x.ColumnName).HasMaxLength(200).IsRequired();
    }
}

public sealed class DeviceTriggerConditionConfiguration : IEntityTypeConfiguration<DeviceTriggerCondition>
{
    public void Configure(EntityTypeBuilder<DeviceTriggerCondition> b)
    {
        b.ToTable("DeviceTriggerConditions");
        b.HasKey(x => x.Id);
        b.Property(x => x.TagName).HasMaxLength(200).IsRequired();
        b.Property(x => x.Value).HasMaxLength(200);
        b.Property(x => x.Conjunction).HasConversion<string>().HasMaxLength(10);
        b.Property(x => x.Operator).HasConversion<string>().HasMaxLength(20);
        b.HasIndex(x => new { x.DeviceId, x.Order });
    }
}

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.ToTable("Users");
        b.HasKey(x => x.Id);
        b.Property(x => x.Username).HasMaxLength(50).IsRequired();
        b.Property(x => x.NormalizedUsername).HasMaxLength(50).IsRequired();
        b.HasIndex(x => x.NormalizedUsername).IsUnique();
        b.Property(x => x.FullName).HasMaxLength(100).IsRequired();
        b.Property(x => x.PasswordHash).HasMaxLength(100).IsRequired();
        b.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
    }
}

public sealed class TransferBatchConfiguration : IEntityTypeConfiguration<TransferBatch>
{
    public void Configure(EntityTypeBuilder<TransferBatch> b)
    {
        b.ToTable("TransferBatches");
        b.HasKey(x => x.Id);
        b.Property(x => x.DeviceName).HasMaxLength(100).IsRequired();
        b.Property(x => x.TargetServer).HasMaxLength(200).IsRequired();
        b.Property(x => x.TargetDatabase).HasMaxLength(128).IsRequired();
        b.Property(x => x.ErrorMessage).HasMaxLength(4000);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.HasIndex(x => x.StartedAtUtc);
    }
}

public sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> b)
    {
        b.ToTable("AuditLogs");
        b.HasKey(x => x.Id);
        b.Property(x => x.Username).HasMaxLength(50).IsRequired();
        b.Property(x => x.Action).HasMaxLength(100).IsRequired();
        b.Property(x => x.Target).HasMaxLength(200);
        b.Property(x => x.Details).HasMaxLength(2000);
        b.HasIndex(x => x.TimestampUtc);
    }
}

public sealed class AppSettingConfiguration : IEntityTypeConfiguration<AppSetting>
{
    public void Configure(EntityTypeBuilder<AppSetting> b)
    {
        b.ToTable("AppSettings");
        b.HasKey(x => x.Key);
        b.Property(x => x.Key).HasMaxLength(100);
        b.Property(x => x.Value).HasMaxLength(4000).IsRequired();
    }
}

public sealed class ServiceRuntimeStateConfiguration : IEntityTypeConfiguration<ServiceRuntimeState>
{
    public void Configure(EntityTypeBuilder<ServiceRuntimeState> b)
    {
        b.ToTable("ServiceRuntimeState");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.StartedAtUtc).HasColumnType("datetime2(3)");
        b.Property(x => x.HeartbeatAtUtc).HasColumnType("datetime2(3)");
        b.Property(x => x.StoppedAtUtc).HasColumnType("datetime2(3)");
        b.Property(x => x.LastError).HasMaxLength(2000);
    }
}

public sealed class DeviceRuntimeStateConfiguration : IEntityTypeConfiguration<DeviceRuntimeState>
{
    public void Configure(EntityTypeBuilder<DeviceRuntimeState> b)
    {
        b.ToTable("DeviceRuntimeStates");
        b.HasKey(x => x.DeviceId);
        b.Property(x => x.StatusUpdatedAtUtc).HasColumnType("datetime2(3)");
        b.Property(x => x.LastError).HasMaxLength(2000);
        b.HasOne(x => x.Device).WithOne().HasForeignKey<DeviceRuntimeState>(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.InstanceId);
    }
}
