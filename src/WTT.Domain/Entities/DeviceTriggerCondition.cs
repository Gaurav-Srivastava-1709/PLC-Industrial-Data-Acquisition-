using WTT.Domain.Common;
using WTT.Domain.Enums;

namespace WTT.Domain.Entities;

/// <summary>
/// One clause of a logical capture expression, e.g. "AND Speed > 100".
/// Clauses are evaluated in <see cref="Order"/> left-to-right.
/// </summary>
public class DeviceTriggerCondition : Entity
{
    public int DeviceId { get; set; }
    public Device? Device { get; set; }

    public int Order { get; set; }

    /// <summary>Operator joining this clause to the previous one. Ignored for the first clause.</summary>
    public LogicalOperator Conjunction { get; set; } = LogicalOperator.None;

    /// <summary>Tag name (any kind: PLC, Custom or External).</summary>
    public string TagName { get; set; } = string.Empty;

    public TriggerOperator Operator { get; set; } = TriggerOperator.Equal;

    /// <summary>Comparison value (unused for <see cref="TriggerOperator.OnChange"/>).</summary>
    public string? Value { get; set; }
}
