using WTT.Domain.Common;
using WTT.Domain.Enums;

namespace WTT.Domain.Entities;

/// <summary>
/// An external SQL query whose first result row supplies values for External tags
/// (e.g. current batch number, shift, operator, recipe).
/// </summary>
public class DeviceQuery : AuditableEntity
{
    public int DeviceId { get; set; }
    public Device? Device { get; set; }

    public string Name { get; set; } = string.Empty;

    public string ServerName { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = string.Empty;
    public bool UseIntegratedSecurity { get; set; }
    public string? UserId { get; set; }

    /// <summary>DPAPI-protected password.</summary>
    public string? ProtectedPassword { get; set; }

    public bool Encrypt { get; set; } = true;
    public bool TrustServerCertificate { get; set; }

    public string QueryText { get; set; } = string.Empty;
    public ExternalQueryType QueryType { get; set; } = ExternalQueryType.Contextual;

    /// <summary>Set to true after the query has been executed successfully and its columns discovered.</summary>
    public bool IsVerified { get; set; }
    public DateTime? VerifiedAtUtc { get; set; }
    public string? LastError { get; set; }

    public ICollection<DeviceQueryColumn> Columns { get; set; } = new List<DeviceQueryColumn>();
}

/// <summary>A column discovered from a verified <see cref="DeviceQuery"/>.</summary>
public class DeviceQueryColumn : Entity
{
    public int QueryId { get; set; }
    public DeviceQuery? Query { get; set; }

    public string ColumnName { get; set; } = string.Empty;
    public int Ordinal { get; set; }
}
