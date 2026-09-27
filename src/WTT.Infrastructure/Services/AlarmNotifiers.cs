using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Runtime.InteropServices;
using WTT.Application.Interfaces;
using WTT.Application.Options;
using WTT.Domain.Enums;
using WTT.Domain.ValueObjects;
using System.Net;
using System.Net.Mail;

namespace WTT.Infrastructure.Services;

/// <summary>In-App notifier: optional Beep (silent toggle) + log. Snackbar is handled by MainViewModel via events, so no duplicate toast here.</summary>
public sealed class InAppAlarmNotifier : IAlarmNotifier
{
    // Win32 system sound (sound-card) — works in WPF WinExe and Service sessions where Console.Beep is silent.
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MessageBeep(uint uType);
    private const uint MB_ICONEXCLAMATION = 0x30;

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
            // Industry standard: 3-burst beep pattern taaki factory ke shor me bhi sunai de
            // Background thread pe chalega taaki alarm engine block na ho
            _ = Task.Run(() =>
            {
                try
                {
                    // Critical = 3 loud beeps, High = 2 beeps
                    int burstCount = msg.Priority == AlarmPriority.Critical ? 3 : 2;
                    int frequency = msg.Priority == AlarmPriority.Critical ? 1200 : 1000; // Hz
                    int duration = 400; // ms per beep
                    int gap = 200;      // ms gap between beeps

                    for (int i = 0; i < burstCount; i++)
                    {
                        try { MessageBeep(MB_ICONEXCLAMATION); } catch { }
                        try { Console.Beep(frequency, duration); } catch { }
                        if (i < burstCount - 1) Thread.Sleep(gap);
                    }
                }
                catch (Exception ex) { _logger.LogDebug(ex, "Beep burst failed"); }
            }, CancellationToken.None);
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

/// <summary>Production-grade Email notifier with SMTP support for Critical/High priority alarms</summary>
public sealed class EmailAlarmNotifier : IAlarmNotifier, IDisposable
{
    private readonly ILogger<EmailAlarmNotifier> _logger;
    private readonly IOptions<AlarmOptions> _options;
    private readonly SemaphoreSlim _emailThrottle = new(5, 5); // Max 5 concurrent emails

    public EmailAlarmNotifier(ILogger<EmailAlarmNotifier> logger, IOptions<AlarmOptions> options)
    {
        _logger = logger;
        _options = options;
    }

    public void Dispose()
    {
        _emailThrottle.Dispose();
    }

    public async Task NotifyTriggeredAsync(AlarmTriggeredMessage msg, CancellationToken ct)
    {
        var opts = _options.Value;
        if (!opts.EmailEnabled) return;

        // Only send emails for Critical and High priority alarms
        if (msg.Priority != AlarmPriority.Critical && msg.Priority != AlarmPriority.High)
            return;

        if (string.IsNullOrWhiteSpace(opts.EmailToCsv))
        {
            _logger.LogWarning("Email notification enabled but no recipients configured");
            return;
        }

        await _emailThrottle.WaitAsync(ct);
        try
        {
            var recipients = opts.EmailToCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            using var mail = new MailMessage
            {
                From = new MailAddress(opts.EmailFrom ?? "wtt-alarms@factory.local", "WTT Alarm System"),
                Subject = $"[{msg.Priority}] Alarm: {msg.DeviceName}/{msg.TagName}",
                Body = BuildEmailBody(msg),
                IsBodyHtml = true,
                Priority = msg.Priority == AlarmPriority.Critical ? MailPriority.High : MailPriority.Normal
            };

            foreach (var recipient in recipients)
            {
                if (!string.IsNullOrWhiteSpace(recipient))
                {
                    mail.To.Add(recipient);
                }
            }

            if (mail.To.Count == 0)
            {
                _logger.LogWarning("No valid email recipients found in configuration");
                return;
            }

            using var smtp = new SmtpClient(opts.SmtpHost ?? "localhost", opts.SmtpPort ?? 25)
            {
                EnableSsl = opts.SmtpUseSsl ?? false,
                Timeout = 10000 // 10 seconds timeout
            };

            if (!string.IsNullOrWhiteSpace(opts.SmtpUsername))
            {
                smtp.Credentials = new NetworkCredential(opts.SmtpUsername, opts.SmtpPassword ?? string.Empty);
            }

            await smtp.SendMailAsync(mail, ct);

            _logger.LogInformation("Email notification sent for {Priority} alarm {Device}/{Tag} to {Count} recipients",
                msg.Priority, msg.DeviceName, msg.TagName, mail.To.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email notification for alarm {Device}/{Tag}", msg.DeviceName, msg.TagName);
        }
        finally
        {
            _emailThrottle.Release();
        }
    }

    public Task NotifyClearedAsync(AlarmClearedMessage msg, CancellationToken ct)
    {
        // Optionally send clear notifications (currently disabled to reduce email noise)
        return Task.CompletedTask;
    }

    private static string BuildEmailBody(AlarmTriggeredMessage msg)
    {
        var typeText = msg.AlarmType == AlarmType.High ? "HIGH" : "LOW";
        var priorityColor = msg.Priority switch
        {
            AlarmPriority.Critical => "#DC2626",
            AlarmPriority.High => "#EA580C",
            AlarmPriority.Medium => "#F59E0B",
            AlarmPriority.Low => "#3B82F6",
            _ => "#6B7280"
        };

        return $@"
<!DOCTYPE html>
<html>
<head>
    <style>
        body {{ font-family: Arial, sans-serif; margin: 0; padding: 20px; background-color: #f5f5f5; }}
        .container {{ background-color: white; padding: 20px; border-radius: 8px; max-width: 600px; margin: 0 auto; box-shadow: 0 2px 4px rgba(0,0,0,0.1); }}
        .header {{ background-color: {priorityColor}; color: white; padding: 15px; border-radius: 4px; margin-bottom: 20px; }}
        .header h2 {{ margin: 0; }}
        .detail {{ margin: 10px 0; padding: 10px; background-color: #f9f9f9; border-left: 4px solid {priorityColor}; }}
        .label {{ font-weight: bold; color: #333; }}
        .value {{ color: #666; }}
        .footer {{ margin-top: 20px; padding-top: 20px; border-top: 1px solid #ddd; font-size: 12px; color: #999; }}
    </style>
</head>
<body>
    <div class='container'>
        <div class='header'>
            <h2>⚠️ {msg.Priority} Priority Alarm Triggered</h2>
        </div>
        <div class='detail'>
            <span class='label'>Device:</span> <span class='value'>{msg.DeviceName}</span>
        </div>
        <div class='detail'>
            <span class='label'>Tag:</span> <span class='value'>{msg.TagName}</span>
        </div>
        <div class='detail'>
            <span class='label'>Alarm Type:</span> <span class='value'>{typeText} Threshold Violation</span>
        </div>
        <div class='detail'>
            <span class='label'>Current Value:</span> <span class='value'>{msg.TriggerValue:F2} {msg.EngineeringUnit}</span>
        </div>
        <div class='detail'>
            <span class='label'>Threshold:</span> <span class='value'>{msg.ThresholdValue:F2} {msg.EngineeringUnit}</span>
        </div>
        <div class='detail'>
            <span class='label'>Triggered At:</span> <span class='value'>{msg.TriggeredAtUtc:yyyy-MM-dd HH:mm:ss} UTC</span>
        </div>
        <div class='footer'>
            <p>This is an automated notification from the WTT Industrial Data Acquisition System.</p>
            <p>Please acknowledge this alarm in the WTT operator interface.</p>
        </div>
    </div>
</body>
</html>";
    }
}
