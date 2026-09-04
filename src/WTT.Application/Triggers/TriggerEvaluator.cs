using System.Globalization;
using WTT.Domain.Entities;
using WTT.Domain.Enums;

namespace WTT.Application.Triggers;

/// <summary>
/// Evaluates a device's logical capture expression.
/// Clauses are combined strictly left-to-right using each clause's <see cref="DeviceTriggerCondition.Conjunction"/>
/// (matching the legacy behaviour: "(A) AND (B) OR (C)" == "((A AND B) OR C)").
/// Comparisons are numeric when both operands parse as numbers, otherwise ordinal string comparison.
/// </summary>
public static class TriggerEvaluator
{
    public sealed record TagSnapshot(string? CurrentValue, string? PreviousValue);

    /// <param name="conditions">Ordered clauses.</param>
    /// <param name="values">Lookup of tag name → current/previous value. Missing tags evaluate to false.</param>
    public static bool Evaluate(
        IReadOnlyList<DeviceTriggerCondition> conditions,
        IReadOnlyDictionary<string, TagSnapshot> values)
    {
        if (conditions.Count == 0) return false;

        bool? result = null;
        foreach (var c in conditions.OrderBy(c => c.Order))
        {
            var clause = EvaluateClause(c, values);
            if (result is null)
            {
                result = clause;
                continue;
            }

            result = c.Conjunction switch
            {
                LogicalOperator.Or => result.Value || clause,
                _ => result.Value && clause, // None/And
            };
        }

        return result ?? false;
    }

    public static bool EvaluateClause(DeviceTriggerCondition c, IReadOnlyDictionary<string, TagSnapshot> values)
    {
        if (!values.TryGetValue(c.TagName, out var snap)) return false;

        if (c.Operator == TriggerOperator.OnChange)
        {
            // First observation (no previous value) does not count as a change.
            if (snap.PreviousValue is null) return false;
            return !string.Equals(snap.CurrentValue, snap.PreviousValue, StringComparison.Ordinal);
        }

        var left = snap.CurrentValue;
        var right = c.Value;
        if (left is null) return false;

        var numeric = TryParse(left, out var l) & TryParse(right, out var r);
        int cmp = numeric
            ? l.CompareTo(r)
            : string.Compare(left, right, StringComparison.Ordinal);

        bool equal = numeric ? Math.Abs(l - r) < 1e-9 : string.Equals(left, right, StringComparison.Ordinal);

        return c.Operator switch
        {
            TriggerOperator.Equal => equal,
            TriggerOperator.NotEqual => !equal,
            TriggerOperator.GreaterThan => cmp > 0,
            TriggerOperator.LessThan => cmp < 0,
            TriggerOperator.GreaterOrEqual => cmp >= 0,
            TriggerOperator.LessOrEqual => cmp <= 0,
            _ => false,
        };
    }

    /// <summary>Human readable rendering, e.g. "(Speed > 100) AND (Batch On Change)".</summary>
    public static string Describe(IEnumerable<DeviceTriggerCondition> conditions)
    {
        var parts = new List<string>();
        var first = true;
        foreach (var c in conditions.OrderBy(c => c.Order))
        {
            var clause = c.Operator == TriggerOperator.OnChange
                ? $"({c.TagName} On Change)"
                : $"({c.TagName} {OperatorSymbol(c.Operator)} '{c.Value}')";
            if (!first && c.Conjunction != LogicalOperator.None)
                parts.Add(c.Conjunction.ToString().ToUpperInvariant());
            else if (!first)
                parts.Add("AND");
            parts.Add(clause);
            first = false;
        }
        return string.Join(" ", parts);
    }

    public static string OperatorSymbol(TriggerOperator op) => op switch
    {
        TriggerOperator.Equal => "=",
        TriggerOperator.NotEqual => "!=",
        TriggerOperator.GreaterThan => ">",
        TriggerOperator.LessThan => "<",
        TriggerOperator.GreaterOrEqual => ">=",
        TriggerOperator.LessOrEqual => "<=",
        TriggerOperator.OnChange => "On Change",
        _ => "?",
    };

    /// <summary>Interpret a tag value as a boolean trigger (PLC trigger mode).</summary>
    public static bool IsTrue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var v = value.Trim();
        if (bool.TryParse(v, out var b)) return b;
        if (TryParse(v, out var d)) return Math.Abs(d) > 1e-9;
        return v.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParse(string? s, out double d)
    {
        if (s is null) { d = 0; return false; }
        var v = s.Trim();
        if (bool.TryParse(v, out var b)) { d = b ? 1 : 0; return true; }
        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
    }
}
