using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using WTT.Domain.Enums;

namespace WTT.UI.Converters;

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b ? !b : value;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b ? !b : value;
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is Visibility.Collapsed;
}

/// <summary>null → Visible (used for "empty state" placeholders).</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is null ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>non-null (and non-empty string) → Visible.</summary>
public sealed class NotNullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is null || (value is string s && string.IsNullOrWhiteSpace(s)) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class CountToVisibilityConverter : IValueConverter
{
    /// <summary>parameter "zero" → visible when count == 0; default → visible when count > 0.</summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var count = value switch { int i => i, long l => (int)Math.Min(l, int.MaxValue), System.Collections.ICollection c => c.Count, _ => 0 };
        var wantZero = string.Equals(parameter as string, "zero", StringComparison.OrdinalIgnoreCase);
        return (wantZero ? count == 0 : count > 0) ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class QualityToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        TagQuality.Good => System.Windows.Application.Current.TryFindResource("GoodBrush") ?? Brushes.Green,
        TagQuality.Uncertain => System.Windows.Application.Current.TryFindResource("WarnBrush") ?? Brushes.Orange,
        TagQuality.Bad or TagQuality.ConfigError => System.Windows.Application.Current.TryFindResource("BadBrush") ?? Brushes.Red,
        TagQuality.NotConnected => System.Windows.Application.Current.TryFindResource("NeutralBrush") ?? Brushes.Gray,
        _ => System.Windows.Application.Current.TryFindResource("NeutralBrush") ?? Brushes.Gray,
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class BoolToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true
            ? System.Windows.Application.Current.TryFindResource("GoodBrush") ?? Brushes.Green
            : System.Windows.Application.Current.TryFindResource("NeutralBrush") ?? Brushes.Gray;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// Device connection state → dot brush (ISA-101 style, display only).
/// Uses the same Good/Warn/Bad/Neutral brushes as everywhere else, so it works
/// in both dark and light themes with no theme-specific code.
/// Mapping (existing snapshot fields only, no behavior change):
/// grey = worker stopped/disabled · green = connected · red = failed with error · amber = connecting.
/// </summary>
public sealed class DeviceStatusToBrushConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var app = System.Windows.Application.Current;
        object Good() => app.TryFindResource("GoodBrush") ?? Brushes.Green;
        object Warn() => app.TryFindResource("WarnBrush") ?? Brushes.Orange;
        object Bad() => app.TryFindResource("BadBrush") ?? Brushes.Red;
        object Neutral() => app.TryFindResource("NeutralBrush") ?? Brushes.Gray;

        var isRunning = values.Length > 0 && values[0] is true;
        var isConnected = values.Length > 1 && values[1] is true;
        var lastError = values.Length > 2 ? values[2] as string : null;

        if (!isRunning) return Neutral();
        if (isConnected) return Good();
        return string.IsNullOrWhiteSpace(lastError) ? Warn() : Bad();
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Enum → friendly text (DescriptionAttribute if present, else name split on case boundaries).</summary>
public sealed class EnumDescriptionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not Enum e) return value?.ToString() ?? string.Empty;
        var member = e.GetType().GetMember(e.ToString()).FirstOrDefault();
        var desc = member?.GetCustomAttribute<DescriptionAttribute>()?.Description;
        if (desc is not null) return desc;
        var name = e.ToString().Replace('_', '-');
        return System.Text.RegularExpressions.Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " ");
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class UtcToLocalConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not DateTime dt) return string.Empty;
        var local = dt.Kind == DateTimeKind.Utc ? dt.ToLocalTime() : DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToLocalTime();
        var format = parameter as string ?? "yyyy-MM-dd HH:mm:ss";
        return local.ToString(format, CultureInfo.CurrentCulture);
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>bool → opacity (hidden pen dimmed, visible pen full).</summary>
public sealed class BoolToOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? 1.0 : 0.35;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
