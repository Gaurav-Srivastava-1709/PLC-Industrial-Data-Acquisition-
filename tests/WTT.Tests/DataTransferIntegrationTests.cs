using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WTT.Application.DTOs;
using WTT.Application.Interfaces;
using WTT.Domain.Entities;
using WTT.Domain.Enums;
using WTT.Infrastructure;
using WTT.Infrastructure.Persistence;
using Xunit;

namespace WTT.Tests;

[Trait("Category", "Integration")]
public sealed class DataTransferIntegrationTests : IAsyncLifetime
{
    private readonly SqlServerTestDatabase _database = new();
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] = _database.ConnectionString,
            ["Security:BcryptWorkFactor"] = "10",
            ["Transfer:RetryCount"] = "0",
        }).Build();
        _host = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(c => { c.Sources.Clear(); c.AddConfiguration(config); })
            .ConfigureLogging(l => l.ClearProviders())
            .ConfigureServices(s => s.AddInfrastructure(config))
            .Build();
        await _host.Services.GetRequiredService<DatabaseInitializer>().InitializeAsync();
        await _host.Services.GetRequiredService<IInitialAdminEnrollmentService>().EnrollAsync(new InitialAdminRequest("admin", "Admin", "Admin#Secure2026"));
        await _host.Services.GetRequiredService<IAuthService>().LoginAsync(new LoginRequest("admin", "Admin#Secure2026"));
    }

    public async Task DisposeAsync()
    {
        _host.Dispose();
        await _database.DeleteAsync();
    }

    [Fact]
    public async Task Bulk_transfer_creates_unicode_schema_and_acknowledges_rows()
    {
        const string table = "TagDetailIntegration";
        await using (var db = await _host.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync())
        {
            var device = new Device { Name = "TransferDevice", Host = "127.0.0.1", Protocol = ProtocolType.ModbusTcp, Manufacturer = Manufacturer.Generic, Model = DeviceModel.GenericModbusTcp };
            db.Devices.Add(device);
            await db.SaveChangesAsync();
            db.DeviceServers.Add(new DeviceServer { DeviceId = device.Id, ServerName = ".", DatabaseName = _database.DatabaseName, TableName = table, UseIntegratedSecurity = true, Encrypt = true, TrustServerCertificate = true, IsEnabled = true });
            db.TagReadings.AddRange(
                new TagReading { DeviceId = device.Id, DeviceName = device.Name, TagName = "Temperature-°C", Value = "42.5", Timestamp = DateTime.Now, TimestampUtc = DateTime.UtcNow, Quality = TagQuality.Good, ScanId = Guid.NewGuid() },
                new TagReading { DeviceId = device.Id, DeviceName = device.Name, TagName = "Recipe", Value = "SAMPLE", Timestamp = DateTime.Now, TimestampUtc = DateTime.UtcNow, Quality = TagQuality.Good, ScanId = Guid.NewGuid() });
            await db.SaveChangesAsync();
        }

        (await _host.Services.GetRequiredService<IDataTransferController>().TransferNowAsync()).Should().Be(2);

        await using (var db = await _host.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync())
            (await db.TagReadings.AllAsync(r => r.IsTransferred)).Should().BeTrue();

        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT_BIG(*), COUNT(DISTINCT SourceId), MAX(CASE WHEN TagValue=N'SAMPLE' THEN 1 ELSE 0 END) FROM dbo.[{table}]";
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetInt64(0).Should().Be(2);
        reader.GetInt32(1).Should().Be(1);
        reader.GetInt32(2).Should().Be(1);
    }
}
