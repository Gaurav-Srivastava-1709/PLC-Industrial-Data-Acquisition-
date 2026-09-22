namespace WTT.Application.Interfaces;

/// <summary>
/// Extensible alarm notification pipeline (Industry-grade: In-App today, Email/SMS tomorrow).
/// Implementations are called by AlarmEvaluationService after DB commit.
/// </summary>
public interface IAlarmNotifier
{
    Task NotifyTriggeredAsync(WTT.Domain.ValueObjects.AlarmTriggeredMessage msg, CancellationToken ct);
    Task NotifyClearedAsync(WTT.Domain.ValueObjects.AlarmClearedMessage msg, CancellationToken ct);
}
