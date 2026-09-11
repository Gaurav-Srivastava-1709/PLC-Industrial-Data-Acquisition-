using WTT.Domain.Enums;

namespace WTT.Application.DTOs;

/// <summary>Safe projection of a user (no password hash).</summary>
public sealed record UserDto(
    int Id,
    string Username,
    string FullName,
    UserRole Role,
    bool IsActive,
    bool MustChangePassword,
    DateTime? LastLoginAtUtc,
    DateTime? LockoutEndUtc,
    DateTime CreatedAtUtc);

public sealed record LoginRequest(string Username, string Password);
public sealed record InitialAdminRequest(string Username, string FullName, string Password);

public sealed record LoginResult(bool Succeeded, UserDto? User, string? Error, bool MustChangePassword)
{
    public static LoginResult Fail(string error) => new(false, null, error, false);
    public static LoginResult Ok(UserDto user) => new(true, user, null, user.MustChangePassword);
}

public sealed record ChangePasswordRequest(int UserId, string CurrentPassword, string NewPassword);

public sealed record CreateUserRequest(string Username, string FullName, string Password, UserRole Role);

public sealed record UpdateUserRequest(int Id, string FullName, UserRole Role, bool IsActive);

public sealed record ResetPasswordRequest(int UserId, string NewPassword);

/// <summary>Connection test outcome for PLC or SQL targets.</summary>
public sealed record ConnectionTestResult(bool Succeeded, string Message, TimeSpan Elapsed)
{
    public static ConnectionTestResult Ok(string message, TimeSpan elapsed) => new(true, message, elapsed);
    public static ConnectionTestResult Fail(string message, TimeSpan elapsed) => new(false, message, elapsed);
}

/// <summary>Result of verifying an external query: discovered column names + sample row.</summary>
public sealed record ExternalQueryVerifyResult(
    bool Succeeded,
    IReadOnlyList<string> Columns,
    IReadOnlyDictionary<string, string?> SampleRow,
    string? Error);

/// <summary>Aggregated statistics shown on dashboards.</summary>
public sealed record TransferStatistics(
    long PendingRecords,
    long TransferredToday,
    long FailedBatchesToday,
    DateTime? LastSuccessAtUtc,
    bool IsRunning);

public sealed record CaptureStatistics(
    int DevicesTotal,
    int DevicesRunning,
    int DevicesConnected,
    long ReadingsToday,
    long ErrorsToday,
    long BufferedReadings);

/// <summary>One line from an application log file.</summary>
public sealed record LogEntry(DateTime Timestamp, string Level, string Source, string Message);

/// <summary>Entry of the Protocol → Manufacturer → Model catalog.</summary>
public sealed record PlcModelInfo(
    DeviceModel Model,
    string DisplayName,
    Manufacturer Manufacturer,
    ProtocolType Protocol,
    int DefaultPort,
    bool SupportsBrowse,
    bool UsesAddressBlocks,
    bool UsesSlot,
    bool UsesRack,
    bool UsesConnectionSize,
    bool UsesUnitId,
    bool UsesWordOrder,
    string AddressHint);
