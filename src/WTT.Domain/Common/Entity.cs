namespace WTT.Domain.Common;

/// <summary>Base class for all persisted entities.</summary>
public abstract class Entity
{
    public int Id { get; set; }
}

/// <summary>Adds created/updated tracking. Values are always UTC.</summary>
public abstract class AuditableEntity : Entity
{
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}
