using System.Text.RegularExpressions;
using WTT.Domain.Enums;

namespace WTT.Infrastructure.PlcDrivers.AllenBradley;

/// <summary>
/// Address helpers for Allen-Bradley controllers.
/// PCCC (PLC-5 / SLC-500 / MicroLogix) file addresses: "N7:0", "F8:3", "B3:0/4", "ST10:2", "A9:0-10", "T4:0.ACC".
/// Logix: symbolic names, optionally with array index "Data[3]" or program scope "Program:Main.Speed".
/// </summary>
public static partial class AllenBradleyAddress
{
    public sealed record Parsed(string TagName, int? BitIndex, int ElementCount, string? FilePrefix);

    /// <summary>Normalise a user address for libplctag and extract bit / range information.</summary>
    public static Parsed Parse(string address)
    {
        var a = address.Trim();
        if (a.Length == 0) throw new FormatException("Address is empty.");

        // PCCC range "A9:0-10" → element 0, 11 elements
        var range = RangeRegex().Match(a);
        if (range.Success)
        {
            var from = int.Parse(range.Groups["from"].Value);
            var to = int.Parse(range.Groups["to"].Value);
            if (to < from) throw new FormatException($"Invalid range '{a}'.");
            return new Parsed($"{range.Groups["file"].Value}:{from}", null, to - from + 1, range.Groups["file"].Value.ToUpperInvariant());
        }

        // PCCC bit "B3:0/4" or "N7:0/15"
        var bit = BitRegex().Match(a);
        if (bit.Success)
        {
            return new Parsed($"{bit.Groups["file"].Value}:{bit.Groups["elem"].Value}", int.Parse(bit.Groups["bit"].Value), 1, bit.Groups["file"].Value.ToUpperInvariant());
        }

        var file = FileRegex().Match(a);
        return new Parsed(a, null, 1, file.Success ? file.Groups["file"].Value.ToUpperInvariant() : null);
    }

    /// <summary>Best-guess data type from a PCCC file prefix; null for Logix symbolic names.</summary>
    public static TagDataType? InferDataType(string address)
    {
        var p = Parse(address);
        if (p.BitIndex.HasValue) return TagDataType.Bool;
        if (p.FilePrefix is null) return null;
        var letters = new string(p.FilePrefix.TakeWhile(char.IsLetter).ToArray());
        return letters switch
        {
            "B" => TagDataType.UInt16,   // bit file read as word unless /bit is given
            "N" => TagDataType.Int16,
            "F" => TagDataType.Float32,
            "L" => TagDataType.Int32,
            "T" or "C" or "R" => TagDataType.Int16, // sub-element (.ACC/.PRE) is a word
            "ST" => TagDataType.String,
            "A" => TagDataType.String,
            "I" or "O" or "S" => TagDataType.UInt16,
            _ => null,
        };
    }

    public static bool IsPccc(DeviceModel model) => model is DeviceModel.Plc5 or DeviceModel.Slc500 or DeviceModel.MicroLogix;

    [GeneratedRegex(@"^(?<file>[A-Za-z]{1,2}\d*):(?<from>\d+)-(?<to>\d+)$")]
    private static partial Regex RangeRegex();

    [GeneratedRegex(@"^(?<file>[A-Za-z]{1,2}\d*):(?<elem>\d+)/(?<bit>\d{1,2})$")]
    private static partial Regex BitRegex();

    [GeneratedRegex(@"^(?<file>[A-Za-z]{1,2}\d*):")]
    private static partial Regex FileRegex();
}
