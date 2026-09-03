using Microsoft.EntityFrameworkCore;
using WTT.Domain.Entities;

namespace WTT.Infrastructure.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<User> Users { get; set; } = null!;
    public DbSet<PlcDevice> PlcDevices { get; set; } = null!;
    public DbSet<DeviceTag> DeviceTags { get; set; } = null!;
    public DbSet<TagReading> TagReadings { get; set; } = null!;
    public DbSet<TransferRecord> TransferRecords { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // User Entity Configuration
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Username).IsRequired().HasMaxLength(50);
            entity.HasIndex(e => e.Username).IsUnique();
            entity.Property(e => e.PasswordHash).IsRequired().HasMaxLength(255);
        });

        // PlcDevice Entity Configuration
        modelBuilder.Entity<PlcDevice>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.IpAddress).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Protocol).IsRequired().HasMaxLength(20);
            entity.Property(e => e.Description).HasMaxLength(255);
        });

        // DeviceTag Entity Configuration
        modelBuilder.Entity<DeviceTag>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TagName).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Address).IsRequired().HasMaxLength(100);
            entity.Property(e => e.DataType).IsRequired().HasMaxLength(20);
            entity.Property(e => e.Description).HasMaxLength(255);

            entity.HasOne(e => e.Device)
                  .WithMany(d => d.Tags)
                  .HasForeignKey(e => e.DeviceId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // TagReading Entity Configuration
        modelBuilder.Entity<TagReading>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Value).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Quality).IsRequired().HasMaxLength(20);
            entity.HasIndex(e => e.Timestamp);
            entity.HasIndex(e => e.IsTransferred);

            entity.HasOne(e => e.DeviceTag)
                  .WithMany()
                  .HasForeignKey(e => e.DeviceTagId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // TransferRecord Entity Configuration
        modelBuilder.Entity<TransferRecord>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TransferredAt);
            entity.Property(e => e.ErrorMessage).HasMaxLength(1000);
            entity.Property(e => e.TargetServer).HasMaxLength(255);
        });
    }
}
