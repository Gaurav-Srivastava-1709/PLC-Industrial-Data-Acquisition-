using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WTT.Application.Options;
using WTT.Infrastructure.Persistence;

namespace WTT.Infrastructure.Capture;

public sealed class AlarmRetentionWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<AlarmRetentionWorker> _logger;
    private readonly IOptionsMonitor<AlarmOptions> _alarmOptions;

    public AlarmRetentionWorker(IServiceProvider services, ILogger<AlarmRetentionWorker> logger, IOptionsMonitor<AlarmOptions> alarmOptions)
    {
        _services = services;
        _logger = logger;
        _alarmOptions = alarmOptions;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Alarm Retention Worker started. Retention = {Days} days (0=forever).", _alarmOptions.CurrentValue.RetentionDays);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PurgeOldAlarmsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred during alarm retention purge.");
            }

            // Run once a day
            await Task.Delay(TimeSpan.FromDays(1), stoppingToken);
        }
    }

    private async Task PurgeOldAlarmsAsync(CancellationToken ct)
    {
        int days = _alarmOptions.CurrentValue.RetentionDays;
        if (days <= 0) return; // 0 = keep forever
        // Also check runtime override from AppSettings (IAlarmService.UpdateRetentionAsync writes there)
        using var scope = _services.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var setting = await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(a => a.Key == "Alarm.RetentionDays", ct);
        if (setting != null && int.TryParse(setting.Value, out var v)) days = v;
        if (days <= 0) return;
        var cutoffDate = DateTime.UtcNow.AddDays(-days);
        
        int deleted = await db.AlarmEvents
            .Where(a => a.TriggeredAtUtc < cutoffDate)
            .ExecuteDeleteAsync(ct);

        if (deleted > 0)
        {
            _logger.LogInformation("Purged {Count} old alarm events (older than {Days} days, cutoff {Cutoff:u})", deleted, days, cutoffDate);
        }
    }
}
