using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WTT.Domain.Entities;
using WTT.Domain.Enums;
using WTT.Domain.ValueObjects;
using WTT.Infrastructure.Capture;
using WTT.Infrastructure.Persistence;
using Xunit;

namespace WTT.Tests;

[Trait("Category", "Integration")]
public sealed class RuntimeStatusIntegrationTests : IAsyncLifetime
{
    private readonly SqlServerTestDatabase _database = new();
    private IDbContextFactory<AppDbContext> _factory = null!;

    public async Task InitializeAsync()
    {
        _factory = new Factory(_database.CreateOptions());
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
        db.Devices.Add(new Device { Name = "RuntimeDevice", Host = "127.0.0.1", Protocol = ProtocolType.ModbusTcp, Manufacturer = Manufacturer.Generic, Model = DeviceModel.GenericModbusTcp });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => _database.DeleteAsync();

    [Fact]
    public async Task Heartbeat_is_visible_cross_process_and_clean_stop_is_immediate()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var deviceId = await db.Devices.Select(d => d.Id).SingleAsync();
        var service = new SqlCaptureRuntimeStatusService(_factory, NullLogger<SqlCaptureRuntimeStatusService>.Instance);
        var instance = Guid.NewGuid();
        var status = new DeviceCaptureStatus(deviceId, "RuntimeDevice", true, true, true, false, true, DateTime.UtcNow, DateTime.UtcNow, 12, 34, 0, null, 0);

        await service.BeginSessionAsync(instance);
        await service.PublishAsync(instance, [status], null);
        var running = await service.GetSnapshotAsync();
        running.IsServiceRunning.Should().BeTrue();
        running.Devices.Should().ContainSingle(s => s.IsRunning && s.IsConnected && s.ScanCount == 12);

        await service.EndSessionAsync(instance, [status]);
        var stopped = await service.GetSnapshotAsync();
        stopped.IsServiceRunning.Should().BeFalse();
        stopped.Devices.Should().ContainSingle(s => !s.IsRunning && !s.IsConnected);
    }

    private sealed class Factory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }
}
