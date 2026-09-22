using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WTT.Application.Interfaces;
using WTT.Application.Options;
using WTT.Domain.Enums;
using WTT.Domain.ValueObjects;

namespace WTT.Infrastructure.Services;

/// <summary>In-App notifier: optional Beep (silent toggle) + log. Snackbar is handled by MainViewModel via events, so no duplicate toast here.</summary>
public sealed class InAppAlarmNotifier : IAlarmNotifier
{
    private readonly IAlarmService _alarmService;
    private readonly ILogger<InAppAlarmNotifier> _logger;

    public InAppAlarmNotifier(IAlarmService alarmService, ILogger<InAppAlarmNotifier> logger)
    {
        _alarmService = alarmService;
        _logger = logger;
    }

    public async Task NotifyTriggeredAsync(AlarmTriggeredMessage msg, CancellationToken ct)
    {
        bool beep = true;
        try { beep = await _alarmService.GetBeepEnabledAsync(ct); } catch { }
        if (beep && (msg.Priority == AlarmPriority.Critical || msg.Priority == AlarmPriority.High))
        {
            try { Console.Beep(1000, 300); } catch (Exception ex) { _logger.LogDebug(ex, "Beep failed"); }
        }
        _logger.LogWarning("NOTIFY InApp Triggered {Device}/{Tag} {Type} {Priority}", msg.DeviceName, msg.TagName, msg.AlarmType, msg.Priority);
    }

    public Task NotifyClearedAsync(AlarmClearedMessage msg, CancellationToken ct)
    {
        _logger.LogInformation("NOTIFY InApp Cleared {Device}/{Tag}", msg.DeviceName, msg.TagName);
        return Task.CompletedTask;
    }
}

public sealed class CompositeAlarmNotifier : IAlarmNotifier
{
    private readonly IReadOnlyList<IAlarmNotifier> _notifiers;
    public CompositeAlarmNotifier(IEnumerable<IAlarmNotifier> notifiers) => _notifiers = notifiers.ToList();
    public async Task NotifyTriggeredAsync(AlarmTriggeredMessage msg, CancellationToken ct) { foreach (var n in _notifiers) try { await n.NotifyTriggeredAsync(msg, ct); } catch { } }
    public async Task NotifyClearedAsync(AlarmClearedMessage msg, CancellationToken ct) { foreach (var n in _notifiers) try { await n.NotifyClearedAsync(msg, ct); } catch { } }
}

/// <summary>Future Email notifier - plug SMTP here without touching core. Disabled until EmailEnabled=true.</summary>
public sealed class EmailAlarmNotifier : IAlarmNotifier
{
    private readonly ILogger<EmailAlarmNotifier> _logger;
    private readonly IOptions<AlarmOptions> _options;
    public EmailAlarmNotifier(ILogger<EmailAlarmNotifier> logger, IOptions<AlarmOptions> options) { _logger = logger; _options = options; }
    public Task NotifyTriggeredAsync(AlarmTriggeredMessage msg, CancellationToken ct)
    {
        if (!_options.Value.EmailEnabled) return Task.CompletedTask;
        _logger.LogInformation("EMAIL (stub) would send alarm {Device}/{Tag} to {To}", msg.DeviceName, msg.TagName, _options.Value.EmailToCsv);
        return Task.CompletedTask;
    }
    public Task NotifyClearedAsync(AlarmClearedMessage msg, CancellationToken ct) => Task.CompletedTask;
}
