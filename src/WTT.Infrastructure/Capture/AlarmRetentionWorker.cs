// =============================================================================
// FILE      : src/WTT.Infrastructure/Capture/AlarmRetentionWorker.cs
// LAYER     : Infrastructure - daily retention housekeeping for the alarm history
// PURPOSE   : Purges AlarmEvents older than the configured retention window so the
//             alarm table cannot grow without bound. Runs at startup, then every 24 h.
// DI        : HostedService (singleton) - one instance per WTT.Service process.
// THREADING : One async loop owns the schedule. Each run opens its own DI scope, so
//             the DbContext is never captured for the service lifetime - that is what
//             keeps a purge from running concurrently with itself across instances.
// DEPENDS ON: IServiceProvider (per-run scope), IOptionsMonitor<AlarmOptions>, IDbContextFactory<AppDbContext> (from that scope)
// USED BY   : Registered as a hosted service in DependencyInjection; nothing calls it.
// KEY RULES :
//   * RetentionDays <= 0 means "keep forever" - return without touching the table.
//     Zero is a legitimate configuration, not unbounded retention by accident.
//   * The runtime `AppSettings["Alarm.RetentionDays"]` override wins over the bound
//     option, because IAlarmService.UpdateRetentionAsync writes it there.
//   * The cutoff uses TriggeredAtUtc (UTC), not the local timestamp, so a timezone
//     change cannot shift which rows are deleted.
//   * Deletion is one ExecuteDeleteAsync, never a read-then-delete loop.
//   * Failures are logged and swallowed so the loop survives; the 24 h delay runs
//     from the END of a purge, so a slow run delays the next rather than stacking.
// =============================================================================
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
