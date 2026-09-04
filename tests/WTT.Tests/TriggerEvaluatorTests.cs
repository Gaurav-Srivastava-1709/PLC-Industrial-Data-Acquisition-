using FluentAssertions;
using WTT.Application.Triggers;
using WTT.Domain.Entities;
using WTT.Domain.Enums;
using Xunit;

namespace WTT.Tests;

public class TriggerEvaluatorTests
{
    private static DeviceTriggerCondition C(int order, LogicalOperator conj, string tag, TriggerOperator op, string? value = null) =>
        new() { Order = order, Conjunction = conj, TagName = tag, Operator = op, Value = value };

    private static Dictionary<string, TriggerEvaluator.TagSnapshot> Values(params (string Tag, string? Current, string? Previous)[] items) =>
        items.ToDictionary(i => i.Tag, i => new TriggerEvaluator.TagSnapshot(i.Current, i.Previous));

    [Theory]
    [InlineData(TriggerOperator.GreaterThan, "100", "150", true)]
    [InlineData(TriggerOperator.GreaterThan, "100", "100", false)]
    [InlineData(TriggerOperator.GreaterOrEqual, "100", "100", true)]
    [InlineData(TriggerOperator.LessThan, "100", "99.5", true)]
    [InlineData(TriggerOperator.LessOrEqual, "100", "100.0", true)]
    [InlineData(TriggerOperator.Equal, "1", "1.0", true)]
    [InlineData(TriggerOperator.NotEqual, "1", "2", true)]
    [InlineData(TriggerOperator.Equal, "RUN", "RUN", true)]
    [InlineData(TriggerOperator.Equal, "RUN", "run", false)]
    public void Numeric_and_string_comparisons(TriggerOperator op, string expected, string current, bool result)
    {
        var conditions = new[] { C(1, LogicalOperator.None, "T", op, expected) };
        TriggerEvaluator.Evaluate(conditions, Values(("T", current, null))).Should().Be(result);
    }

    [Fact]
    public void Numeric_comparison_is_not_lexicographic()
    {
        var conditions = new[] { C(1, LogicalOperator.None, "T", TriggerOperator.GreaterThan, "9") };
        TriggerEvaluator.Evaluate(conditions, Values(("T", "10", null))).Should().BeTrue("10 > 9 numerically even though \"10\" < \"9\" as strings");
    }

    [Fact]
    public void OnChange_requires_a_previous_value_and_a_difference()
    {
        var conditions = new[] { C(1, LogicalOperator.None, "Batch", TriggerOperator.OnChange) };
        TriggerEvaluator.Evaluate(conditions, Values(("Batch", "B1", null))).Should().BeFalse("first observation is not a change");
        TriggerEvaluator.Evaluate(conditions, Values(("Batch", "B1", "B1"))).Should().BeFalse();
        TriggerEvaluator.Evaluate(conditions, Values(("Batch", "B2", "B1"))).Should().BeTrue();
    }

    [Fact]
    public void Left_to_right_evaluation_matches_legacy_semantics()
    {
        // (A) AND (B) OR (C)  ==  ((A AND B) OR C)
        var conditions = new[]
        {
            C(1, LogicalOperator.None, "A", TriggerOperator.Equal, "1"),
            C(2, LogicalOperator.And, "B", TriggerOperator.Equal, "1"),
            C(3, LogicalOperator.Or, "C", TriggerOperator.Equal, "1"),
        };
        TriggerEvaluator.Evaluate(conditions, Values(("A", "1", null), ("B", "0", null), ("C", "1", null))).Should().BeTrue();
        TriggerEvaluator.Evaluate(conditions, Values(("A", "1", null), ("B", "0", null), ("C", "0", null))).Should().BeFalse();
        TriggerEvaluator.Evaluate(conditions, Values(("A", "1", null), ("B", "1", null), ("C", "0", null))).Should().BeTrue();
    }

    [Fact]
    public void Missing_tag_evaluates_false_and_empty_conditions_false()
    {
        var conditions = new[] { C(1, LogicalOperator.None, "Missing", TriggerOperator.Equal, "1") };
        TriggerEvaluator.Evaluate(conditions, Values()).Should().BeFalse();
        TriggerEvaluator.Evaluate(Array.Empty<DeviceTriggerCondition>(), Values()).Should().BeFalse();
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("true", true)]
    [InlineData("False", false)]
    [InlineData("ON", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("3.5", true)]
    public void IsTrue_interprets_plc_trigger_values(string? value, bool expected) =>
        TriggerEvaluator.IsTrue(value).Should().Be(expected);

    [Fact]
    public void Describe_renders_expression()
    {
        var conditions = new[]
        {
            C(1, LogicalOperator.None, "Speed", TriggerOperator.GreaterThan, "100"),
            C(2, LogicalOperator.And, "Batch", TriggerOperator.OnChange),
        };
        TriggerEvaluator.Describe(conditions).Should().Be("(Speed > '100') AND (Batch On Change)");
    }
}
