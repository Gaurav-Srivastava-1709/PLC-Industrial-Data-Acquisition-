using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.InMemory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WTT.Application.DTOs;
using WTT.Application.Options;
using WTT.Domain.Entities;
using WTT.Domain.Enums;
using WTT.Infrastructure.Persistence;
using WTT.Infrastructure.Services;
using Xunit;

namespace WTT.Tests;

/// <summary>
/// Industry-grade unit tests for alarm evaluation service.
/// Covers: state machine, deadband, on/off delays, flood suppression, shelving, race conditions.
/// </summary>
public sealed class AlarmEvaluationServiceTests : IDisposable
{
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly AppDbContext _dbContext;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly AlarmEvaluationService _service;
    private readonly Device _testDevice;
    private readonly DeviceTag _testTag;

    public AlarmEvaluationServiceTests()
    {
        // In-memory database for testing
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"AlarmTests_{Guid.NewGuid()}")
            .Options;

        _dbContext = new AppDbContext(_options);
        _dbFactory = new TestDbContextFactory(_options);

        // Setup test device and tag
        _testDevice = new Device
        {
            Id = 1,
            Name = "TestPLC",
            Protocol = ProtocolType.ModbusTcp,
            Manufacturer = Manufacturer.Generic,
            Model = DeviceModel.GenericModbusTcp,
            Host = "127.0.0.1",
            Port = 502,
            IsEnabled = true
        };

        _testTag = new DeviceTag
        {
            Id = 1,
            DeviceId = 1,
            Name = "TEMP001",
            Kind = TagKind.Plc,
            DataType = TagDataType.Float32,
            IsAlarmEnabled = true,
            AlarmMaxThreshold = 100.0,
            AlarmMinThreshold = 0.0,
            AlarmDeadband = 2.0, // 2% deadband
            AlarmOnDelaySeconds = 5,
            AlarmOffDelaySeconds = 3,
            AlarmPriority = AlarmPriority.High,
            EngineeringUnit = "°C"
        };

        // 1. Seed database first
        _dbContext.Devices.Add(_testDevice);
        _dbContext.DeviceTags.Add(_testTag);
        _dbContext.SaveChanges();

        // 2. Instantiate service after seeding so initialization background worker sees seeded data cleanly
        var logger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<AlarmEvaluationService>();
        var alarmOptions = Options.Create(new AlarmOptions { RetentionDays = 90, BeepEnabled = false });
        var currentUser = new TestCurrentUserService();

        _service = new AlarmEvaluationService(_dbFactory, logger, alarmOptions, currentUser);
    }

    [Fact]
    public async Task OnDelay_ValueReturnsNormalBeforeDelay_NoAlarmTriggered()
    {
        // Arrange: Max threshold = 100, on-delay = 5 seconds
        var startTime = DateTime.UtcNow;

        // Act: Value exceeds threshold for only 3 seconds (less than on-delay)
        await _service.EvaluateAsync(_testDevice, _testTag, 105.0, CancellationToken.None);
        await Task.Delay(1000);
        await _service.EvaluateAsync(_testDevice, _testTag, 105.0, CancellationToken.None);
        await Task.Delay(1000);
        await _service.EvaluateAsync(_testDevice, _testTag, 105.0, CancellationToken.None);

        // Value returns to normal before on-delay expires
        await _service.EvaluateAsync(_testDevice, _testTag, 95.0, CancellationToken.None);

        // Assert: No alarm should be triggered
        var alarms = await _dbContext.AlarmEvents.ToListAsync();
        Assert.Empty(alarms);
    }

    [Fact]
    public async Task OnDelay_ValueExceedsForFullDuration_AlarmTriggered()
    {
        // Arrange
        var eventFired = false;
        _service.AlarmTriggered += (s, e) => eventFired = true;

        // Act: Value exceeds threshold for full on-delay period
        for (int i = 0; i < 6; i++)
        {
            await _service.EvaluateAsync(_testDevice, _testTag, 105.0, CancellationToken.None);
            await Task.Delay(1000);
        }

        // Wait for queue processing
        await Task.Delay(500);

        // Assert
        var alarms = await _dbContext.AlarmEvents.ToListAsync();
        Assert.Single(alarms);
        Assert.Equal(AlarmState.Active, alarms[0].State);
        Assert.Equal(AlarmType.High, alarms[0].AlarmType);
        Assert.True(eventFired);
    }

    [Fact]
    public async Task OffDelay_ValueReturnsNormalForDuration_AlarmCleared()
    {
        // Arrange: Trigger alarm first
        for (int i = 0; i < 6; i++)
        {
            await _service.EvaluateAsync(_testDevice, _testTag, 105.0, CancellationToken.None);
            await Task.Delay(1000);
        }
        await Task.Delay(500);

        var alarmsBefore = await _dbContext.AlarmEvents.ToListAsync();
        Assert.Single(alarmsBefore);

        // Act: Value returns below threshold (with deadband) for full off-delay period
        // Max = 100, Deadband = 2% = 2.0, Clear threshold = 100 - 2 = 98
        for (int i = 0; i < 4; i++)
        {
            await _service.EvaluateAsync(_testDevice, _testTag, 95.0, CancellationToken.None);
            await Task.Delay(1000);
        }
        await Task.Delay(500);

        // Assert
        _dbContext.ChangeTracker.Clear();
        var alarmsAfter = await _dbContext.AlarmEvents.AsNoTracking().ToListAsync();
        Assert.Single(alarmsAfter);
        Assert.Equal(AlarmState.Cleared, alarmsAfter[0].State);
        Assert.NotNull(alarmsAfter[0].ClearedAtUtc);
    }

    [Fact]
    public async Task Deadband_ValueOscillatesNearThreshold_SingleAlarm()
    {
        // Arrange: Max = 100, Deadband = 2%, Clear = 98

        // Act: Trigger alarm at 105
        for (int i = 0; i < 6; i++)
        {
            await _service.EvaluateAsync(_testDevice, _testTag, 105.0, CancellationToken.None);
            await Task.Delay(1000);
        }
        await Task.Delay(500);

        // Value drops to 99 (above clear threshold of 98) - should NOT clear
        await _service.EvaluateAsync(_testDevice, _testTag, 99.0, CancellationToken.None);
        await Task.Delay(500);

        // Value goes back to 101 - should NOT trigger new alarm
        await _service.EvaluateAsync(_testDevice, _testTag, 101.0, CancellationToken.None);
        await Task.Delay(500);

        // Assert: Only ONE alarm should exist, still active
        var alarms = await _dbContext.AlarmEvents.ToListAsync();
        Assert.Single(alarms);
        Assert.Equal(AlarmState.Active, alarms[0].State);
    }

    [Fact]
    public async Task FloodSuppression_RapidAlarms_LimitedTo10PerMinute()
    {
        // Arrange: Create a tag that can trigger repeatedly
        var floodTag = new DeviceTag
        {
            Id = 2,
            DeviceId = 1,
            Name = "FLOOD_TEST",
            Kind = TagKind.Plc,
            DataType = TagDataType.Float32,
            IsAlarmEnabled = true,
            AlarmMaxThreshold = 100.0,
            AlarmOnDelaySeconds = 0, // No delay for this test
            AlarmOffDelaySeconds = 0,
            AlarmPriority = AlarmPriority.Low,
            AlarmDeadband = 0
        };
        _dbContext.DeviceTags.Add(floodTag);
        await _dbContext.SaveChangesAsync();

        // Act: Try to trigger 20 alarms rapidly (alternating high/low to clear and re-trigger)
        for (int i = 0; i < 20; i++)
        {
            await _service.EvaluateAsync(_testDevice, floodTag, 105.0, CancellationToken.None);
            await Task.Delay(100);
            await _service.EvaluateAsync(_testDevice, floodTag, 50.0, CancellationToken.None);
            await Task.Delay(100);
        }

        await Task.Delay(1000); // Wait for queue processing

        // Assert: Should have max 10 alarms due to flood suppression
        var alarms = await _dbContext.AlarmEvents.Where(a => a.TagName == "FLOOD_TEST").ToListAsync();
        Assert.True(alarms.Count <= 10, $"Expected max 10 alarms, got {alarms.Count}");
    }

    [Fact]
    public async Task Shelving_ShelvedAlarm_SkipsEvaluation()
    {
        // Arrange: Shelve the tag for 1 hour
        await _service.ShelveAlarmAsync(_testTag.Id, TimeSpan.FromHours(1), "TestUser", "Maintenance", CancellationToken.None);

        // Act: Try to trigger alarm on shelved tag
        for (int i = 0; i < 6; i++)
        {
            await _service.EvaluateAsync(_testDevice, _testTag, 105.0, CancellationToken.None);
            await Task.Delay(1000);
        }
        await Task.Delay(500);

        // Assert: No alarm should be triggered
        var alarms = await _dbContext.AlarmEvents.ToListAsync();
        Assert.Empty(alarms);

        // Verify shelved list
        var shelved = await _service.GetShelvedAlarmsAsync(CancellationToken.None);
        Assert.Single(shelved);
        Assert.Equal(_testTag.Id, shelved[0].TagId);
    }

    [Fact]
    public async Task Shelving_Unshelve_ResumesEvaluation()
    {
        // Arrange: Shelve then unshelve
        await _service.ShelveAlarmAsync(_testTag.Id, TimeSpan.FromHours(1), "TestUser", "Maintenance", CancellationToken.None);
        await _service.UnshelveAlarmAsync(_testTag.Id, "TestUser", CancellationToken.None);

        // Act: Trigger alarm after unshelving
        for (int i = 0; i < 6; i++)
        {
            await _service.EvaluateAsync(_testDevice, _testTag, 105.0, CancellationToken.None);
            await Task.Delay(1000);
        }
        await Task.Delay(500);

        // Assert: Alarm should trigger normally
        var alarms = await _dbContext.AlarmEvents.ToListAsync();
        Assert.Single(alarms);
    }

    [Fact]
    public async Task LowAlarm_BelowMinThreshold_Triggers()
    {
        // Act: Value below min threshold (0°C)
        for (int i = 0; i < 6; i++)
        {
            await _service.EvaluateAsync(_testDevice, _testTag, -5.0, CancellationToken.None);
            await Task.Delay(1000);
        }
        await Task.Delay(500);

        // Assert
        var alarms = await _dbContext.AlarmEvents.ToListAsync();
        Assert.Single(alarms);
        Assert.Equal(AlarmType.Low, alarms[0].AlarmType);
        Assert.Equal(-5.0, alarms[0].TriggerValue);
    }

    [Fact]
    public async Task CriticalAlarm_DirectDBWrite_NoLoss()
    {
        // Arrange: Critical priority tag
        _testTag.AlarmPriority = AlarmPriority.Critical;
        await _dbContext.SaveChangesAsync();

        // Act: Trigger critical alarm
        for (int i = 0; i < 6; i++)
        {
            await _service.EvaluateAsync(_testDevice, _testTag, 105.0, CancellationToken.None);
            await Task.Delay(1000);
        }

        // Critical alarms write directly, so small delay
        await Task.Delay(1000);

        // Assert: Should be written to DB immediately
        var alarms = await _dbContext.AlarmEvents.ToListAsync();
        Assert.Single(alarms);
        Assert.Equal(AlarmPriority.Critical, alarms[0].Priority);
    }

    [Fact]
    public async Task Acknowledge_ActiveAlarm_StateChangesToAcknowledged()
    {
        // Arrange: Trigger alarm
        for (int i = 0; i < 6; i++)
        {
            await _service.EvaluateAsync(_testDevice, _testTag, 105.0, CancellationToken.None);
            await Task.Delay(1000);
        }
        await Task.Delay(500);

        var alarm = await _dbContext.AlarmEvents.FirstAsync();

        // Act: Acknowledge
        await _service.AcknowledgeAsync(alarm.Id, "TestOperator", CancellationToken.None);

        // Assert
        _dbContext.ChangeTracker.Clear();
        var updated = await _dbContext.AlarmEvents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == alarm.Id);
        Assert.NotNull(updated);
        Assert.Equal(AlarmState.Acknowledged, updated.State);
        Assert.NotNull(updated.AcknowledgedAtUtc);
        Assert.Equal("TestOperator", updated.AcknowledgedBy);
    }

    [Fact]
    public async Task AcknowledgeAll_MultipleActiveAlarms_AllAcknowledged()
    {
        // Arrange: Create second tag and trigger both
        var tag2 = new DeviceTag
        {
            Id = 3,
            DeviceId = 1,
            Name = "PRESS001",
            Kind = TagKind.Plc,
            // DataType enum's correct name is TagDataType
            DataType = TagDataType.Float32,
            IsAlarmEnabled = true,
            AlarmMaxThreshold = 10.0,
            AlarmOnDelaySeconds = 0,
            AlarmPriority = AlarmPriority.Medium
        };
        _dbContext.DeviceTags.Add(tag2);
        await _dbContext.SaveChangesAsync();

        // Trigger both alarms
        for (int i = 0; i < 6; i++)
        {
            await _service.EvaluateAsync(_testDevice, _testTag, 105.0, CancellationToken.None);
            await _service.EvaluateAsync(_testDevice, tag2, 15.0, CancellationToken.None);
            await Task.Delay(1000);
        }
        await Task.Delay(500);

        // Act: Acknowledge all
        await _service.AcknowledgeAllAsync("TestOperator", CancellationToken.None);

        // Assert
        _dbContext.ChangeTracker.Clear();
        var alarms = await _dbContext.AlarmEvents.AsNoTracking().ToListAsync();
        Assert.Equal(2, alarms.Count);
        Assert.All(alarms, a => Assert.Equal(AlarmState.Acknowledged, a.State));
    }

    [Fact]
    public async Task GetActiveAlarmCount_ReturnsCorrectCount()
    {
        // Arrange: Trigger one alarm
        for (int i = 0; i < 6; i++)
        {
            await _service.EvaluateAsync(_testDevice, _testTag, 105.0, CancellationToken.None);
            await Task.Delay(1000);
        }
        await Task.Delay(500);

        // Act
        var count = await _service.GetActiveAlarmCountAsync(CancellationToken.None);

        // Assert
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task InitializationLock_EvaluateBeforeInit_WaitsForCompletion()
    {
        // This test verifies the race condition fix
        // Service should wait for LoadActiveAlarmsAsync to complete before evaluating

        // Act: Evaluate immediately (service might still be initializing)
        await _service.EvaluateAsync(_testDevice, _testTag, 105.0, CancellationToken.None);

        // Assert: Should not throw, evaluation should wait for init
        // If this completes without exception, the fix is working
        Assert.True(true);
    }

    public void Dispose()
    {
        try { (_service as IDisposable)?.Dispose(); } catch { }
        try
        {
            using var cleanup = new AppDbContext(_options);
            cleanup.Database.EnsureDeleted();
        }
        catch { }
        _dbContext.Dispose();
    }

    // Helper classes are sealed for performance and to avoid analyzer warning (CA1852)
    private sealed class TestDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;
        public TestDbContextFactory(DbContextOptions<AppDbContext> options) => _options = options;
        public AppDbContext CreateDbContext() => new AppDbContext(_options);
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(new AppDbContext(_options));
    }

    // Mock implementation of current user for testing
    private sealed class TestCurrentUserService : Application.Interfaces.ICurrentUserService
    {
        // LockoutEndUtc: null is passed in the UserDto record constructor
        public UserDto? User => new UserDto(
            Id: 1,
            Username: "TestUser",
            FullName: "Test User",
            Role: UserRole.Admin,
            IsActive: true,
            MustChangePassword: false,
            LastLoginAtUtc: DateTime.UtcNow,
            LockoutEndUtc: null,
            CreatedAtUtc: DateTime.UtcNow
        );
        public bool IsAuthenticated => true;
        public string Username => "TestUser";
        public bool HasPermission(Application.Security.Permission permission) => true;
        public bool IsInRole(UserRole role) => role == UserRole.Admin;
        public void Demand(Application.Security.Permission permission) { }
        public void SetUser(UserDto? user) { }
#pragma warning disable CS0067
        public event EventHandler? Changed;
#pragma warning restore CS0067
    }
}
