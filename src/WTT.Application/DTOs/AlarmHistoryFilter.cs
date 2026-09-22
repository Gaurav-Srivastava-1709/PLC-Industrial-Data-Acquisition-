using WTT.Domain.Enums;

namespace WTT.Application.DTOs;

public sealed record AlarmHistoryFilter(
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    AlarmPriority? Priority = null,
    AlarmType? AlarmType = null,
    AlarmState? State = null,
    string? DeviceName = null,
    string? TagName = null);
