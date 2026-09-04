using WTT.Domain.Common;

namespace WTT.Domain.Entities;

/// <summary>
/// Central SQL Server destination for one device's readings.
/// Each device may ship to a different server / database.
/// </summary>
public class DeviceServer : AuditableEntity
{
    public int DeviceId { get; set; }
    public Device? Device { get; set; }

    public string ServerName { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = string.Empty;

    /// <summary>Destination table; defaults to the legacy "TagDetail".</summary>
    public string TableName { get; set; } = "TagDetail";

    public bool UseIntegratedSecurity { get; set; }
    public string? UserId { get; set; }

    /// <summary>DPAPI-protected password. Never stored in clear text.</summary>
    public string? ProtectedPassword { get; set; }

    public bool Encrypt { get; set; } = true;
    public bool TrustServerCertificate { get; set; } = true;

    /// <summary>Whether transfer is active for this device.</summary>
    public bool IsEnabled { get; set; }

    public DateTime? LastSuccessAtUtc { get; set; }
    public string? LastError { get; set; }
}
