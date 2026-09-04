using System.Globalization;
using WTT.Domain.Entities;
using WTT.Domain.Enums;

namespace WTT.Domain.Common;

/// <summary>
/// Converts raw driver values into the canonical invariant-culture string stored in the historian,
/// applying optional linear scaling and rounding defined on the tag.
/// </summary>
public static class TagValueFormatter
{
    public static string Format(DeviceTag tag, object? raw)
    {
        if (raw is null) return string.Empty;

        switch (raw)
        {
            case string s:
                return s.TrimEnd('\0');
            case bool b:
                return b ? "1" : "0";
            case Array arr:
                return string.Join(",", arr.Cast<object?>().Select(x => Format(tag, x)));
        }

        if (TryToDouble(raw, out var d))
        {
            var scale = tag.ScaleFactor ?? 1.0;
            var offset = tag.ScaleOffset ?? 0.0;
            var isScaled = tag.ScaleFactor.HasValue || tag.ScaleOffset.HasValue;
            if (isScaled) d = d * scale + offset;

            var decimals = tag.DecimalPlaces ?? tag.Address?.DecimalPlaces;
            if (decimals.HasValue) d = Math.Round(d, Math.Clamp(decimals.Value, 0, 15), MidpointRounding.AwayFromZero);

            // Integers stay integers unless scaling introduced fractions.
            if (!isScaled && !decimals.HasValue && IsIntegral(raw))
                return Convert.ToString(raw, CultureInfo.InvariantCulture) ?? string.Empty;

            return d.ToString("R", CultureInfo.InvariantCulture);
        }

        return Convert.ToString(raw, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    public static bool TryToDouble(object? value, out double result)
    {
        switch (value)
        {
            case null: result = 0; return false;
            case double d: result = d; return true;
            case float f: result = f; return true;
            case decimal m: result = (double)m; return true;
            case byte or sbyte or short or ushort or int or uint or long or ulong:
                result = Convert.ToDouble(value, CultureInfo.InvariantCulture); return true;
            case bool b: result = b ? 1 : 0; return true;
            case string s:
                return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out result)
                    || double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out result);
            default:
                result = 0; return false;
        }
    }

    public static bool IsIntegral(object value) =>
        value is byte or sbyte or short or ushort or int or uint or long or ulong;

    /// <summary>Parse a user supplied string into the CLR type matching the tag data type.</summary>
    public static object ParseForWrite(TagDataType dataType, string value)
    {
        var v = value.Trim();
        var ci = CultureInfo.InvariantCulture;
        return dataType switch
        {
            TagDataType.Bool => v is "1" or "true" or "True" or "TRUE" or "on" or "ON",
            TagDataType.Byte => byte.Parse(v, ci),
            TagDataType.Int16 => short.Parse(v, ci),
            TagDataType.UInt16 => ushort.Parse(v, ci),
            TagDataType.Int32 => int.Parse(v, ci),
            TagDataType.UInt32 => uint.Parse(v, ci),
            TagDataType.Int64 => long.Parse(v, ci),
            TagDataType.UInt64 => ulong.Parse(v, ci),
            TagDataType.Float32 => float.Parse(v, ci),
            TagDataType.Float64 => double.Parse(v, ci),
            TagDataType.String => v,
            _ => v,
        };
    }

    public static int DefaultByteSize(TagDataType dataType, int elementCount = 1) => dataType switch
    {
        TagDataType.Bool => 1,
        TagDataType.Byte => 1,
        TagDataType.Int16 or TagDataType.UInt16 => 2,
        TagDataType.Int32 or TagDataType.UInt32 or TagDataType.Float32 => 4,
        TagDataType.Int64 or TagDataType.UInt64 or TagDataType.Float64 => 8,
        TagDataType.String => Math.Max(1, elementCount),
        _ => 4,
    } * (dataType == TagDataType.String ? 1 : Math.Max(1, elementCount));

    /// <summary>Number of 16-bit Modbus registers occupied by a value of this type.</summary>
    public static int ModbusRegisterCount(TagDataType dataType, int elementCount = 1) => dataType switch
    {
        TagDataType.Bool => 1,
        TagDataType.Byte or TagDataType.Int16 or TagDataType.UInt16 => 1,
        TagDataType.Int32 or TagDataType.UInt32 or TagDataType.Float32 => 2,
        TagDataType.Int64 or TagDataType.UInt64 or TagDataType.Float64 => 4,
        TagDataType.String => Math.Max(1, (elementCount + 1) / 2),
        _ => 1,
    };
}
