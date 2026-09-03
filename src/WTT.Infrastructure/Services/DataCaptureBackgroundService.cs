using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using WTT.Application.Interfaces;
using WTT.Domain.Entities;
using WTT.Infrastructure.Data;
using WTT.Infrastructure.PlcDrivers;

namespace WTT.Infrastructure.Services;

/// <summary>
/// DataCaptureBackgroundService: Industry grade background worker.
/// 1. Har 10 seconds me saare active devices ke tags poll karta hai.
/// 2. Live values ko UI event dispatch karta hai.
/// 3. TagReading table me save karta hai.
/// 4. 30 din se purane data ko auto-delete karke disk space maintain karta hai.
/// </summary>
public class DataCaptureBackgroundService : BackgroundService, IDataCaptureService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly PlcDriverFactory _driverFactory;
    private readonly ILogger _logger = Log.ForContext<DataCaptureBackgroundService>();

    private readonly ConcurrentDictionary<int, string> _latestValues = new();
    private bool _isManualRunning = true;
    private DateTime _lastCleanupTime = DateTime.MinValue;

    public bool IsRunning => _isManualRunning && ExecuteTask != null && !ExecuteTask.IsCompleted;
    public int PollingIntervalSeconds { get; set; } = 10; // User specified: 10 seconds
    public int RetentionDays { get; set; } = 30; // Industry standard: 30 days

    public event EventHandler<TagValueChangedEventArgs>? TagValueChanged;
    public event EventHandler<bool>? ServiceStatusChanged;

    public DataCaptureBackgroundService(IServiceProvider serviceProvider, PlcDriverFactory driverFactory)
    {
        _serviceProvider = serviceProvider;
        _driverFactory = driverFactory;
    }

    public new Task StartAsync(CancellationToken cancellationToken = default)
    {
        _isManualRunning = true;
        ServiceStatusChanged?.Invoke(this, true);
        _logger.Information("Data Capture Service manually started by user.");
        return Task.CompletedTask;
    }

    public new Task StopAsync(CancellationToken cancellationToken = default)
    {
        _isManualRunning = false;
        ServiceStatusChanged?.Invoke(this, false);
        _logger.Information("Data Capture Service manually paused by user.");
        return Task.CompletedTask;
    }

    public Task<Dictionary<int, string>> GetLatestValuesAsync()
    {
        return Task.FromResult(new Dictionary<int, string>(_latestValues));
    }

    public async Task<int> GetTodayReadingsCountAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var today = DateTime.UtcNow.Date;
        return await context.TagReadings.CountAsync(r => r.Timestamp >= today);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.Information("Data Capture Background Service background loop initialized. Polling interval: {Interval}s", PollingIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (_isManualRunning)
            {
                try
                {
                    await PollAllDevicesAsync(stoppingToken);
                    await RunRetentionCleanupIfNeededAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Unexpected error in Data Capture polling cycle.");
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(PollingIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.Information("Data Capture Background Service stopped.");
    }

    private async Task PollAllDevicesAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Saare enabled devices aur unke active tags fetch karo
        var devices = await context.PlcDevices
            .Include(d => d.Tags)
            .Where(d => d.IsEnabled)
            .ToListAsync(cancellationToken);

        if (devices.Count == 0) return;

        var readingsToSave = new List<TagReading>();

        foreach (var device in devices)
        {
            var activeTags = device.Tags.Where(t => t.IsActive).ToList();
            if (activeTags.Count == 0) continue;

            IPlcDriver? driver = null;
            try
            {
                driver = _driverFactory.CreateDriver(device);
                await driver.ConnectAsync(device);

                foreach (var tag in activeTags)
                {
                    string quality = "Good";
                    string stringValue = "0";

                    try
                    {
                        var rawValue = await driver.ReadTagAsync(tag.Address);
                        stringValue = rawValue?.ToString() ?? "0";
                    }
                    catch (Exception ex)
                    {
                        quality = "Bad";
                        stringValue = "ERR";
                        _logger.Warning("Error reading tag {TagName} ({Address}) on device {DeviceName}: {Message}", 
                            tag.TagName, tag.Address, device.Name, ex.Message);
                    }

                    // Memory cache update
                    _latestValues[tag.Id] = stringValue;

                    // UI Event dispatch
                    TagValueChanged?.Invoke(this, new TagValueChangedEventArgs
                    {
                        TagId = tag.Id,
                        TagName = tag.TagName,
                        DeviceName = device.Name,
                        Value = stringValue,
                        Quality = quality,
                        Timestamp = DateTime.UtcNow
                    });

                    // Database entry
                    readingsToSave.Add(new TagReading
                    {
                        DeviceTagId = tag.Id,
                        Value = stringValue,
                        Quality = quality,
                        Timestamp = DateTime.UtcNow,
                        IsTransferred = false
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to connect or poll device {DeviceName}", device.Name);
            }
            finally
            {
                if (driver != null)
                {
                    try { await driver.DisconnectAsync(); } catch { }
                }
            }
        }

        if (readingsToSave.Count > 0)
        {
            context.TagReadings.AddRange(readingsToSave);
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task RunRetentionCleanupIfNeededAsync(CancellationToken cancellationToken)
    {
        // 24 ghante me ek baar cleanup chalana hai
        if ((DateTime.UtcNow - _lastCleanupTime).TotalHours < 24) return;

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var thresholdDate = DateTime.UtcNow.AddDays(-RetentionDays);
            var oldRecords = await context.TagReadings
                .Where(r => r.Timestamp < thresholdDate)
                .ExecuteDeleteAsync(cancellationToken);

            _lastCleanupTime = DateTime.UtcNow;

            if (oldRecords > 0)
            {
                _logger.Information("Retention Policy: Purged {Count} historical readings older than {Days} days.", 
                    oldRecords, RetentionDays);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to execute historical data retention cleanup.");
        }
    }
}
