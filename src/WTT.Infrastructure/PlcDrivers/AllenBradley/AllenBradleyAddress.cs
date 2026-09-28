// =============================================================================
// FILE      : src/WTT.Infrastructure/PlcDrivers/AllenBradley/AllenBradleyAddress.cs
// LAYER     : Infrastructure — pure Allen-Bradley address parsing / normalisation
// PURPOSE   : Turns a user-typed AB address into the (TagName, bit, element count,
//             file prefix) tuple the driver needs, and best-guesses a data type from
//             a PCCC file prefix. Two dialects: PCCC file addresses and Logix
//             symbolic names. No I/O and no libplctag — the driver calls in and gets
//             a plain record back.
// DI        : Static — pure functions over strings; no instance, no state.
// THREADING : Fully thread-safe. The regexes are [GeneratedRegex] source-generated
//             (no per-call compile, no shared mutable state), so any number of
//             parses may run concurrently.
// DEPENDS ON: System.Text.RegularExpressions, WTT.Domain.Enums (TagDataType, DeviceModel)
// USED BY   : AllenBradleyDriver (GetOrCreate / Decode), device & tag config screens, AB address block builder.
// KEY RULES :
//   * PCCC forms accepted: "N7:0", "F8:3", "B3:0/4", "ST10:2", "A9:0-10", "T4:0.ACC".
//     A range "A9:0-10" becomes element 0 with ElementCount = 11 (ranges are
//     INCLUSIVE, not a count); a bit "/4" is stripped into BitIndex and the whole
//     word is still read.
//   * Logix symbolic names pass through essentially unchanged, but an array index
//     "Data[3]" or program scope "Program:Main.Speed" is exactly what libplctag
//     expects, so never case-fold or strip the scope.
//   * `Parsed.TagName` is the ONLY string handed to libplctag and must be the
//     NORMALISED form (ranges/bits collapsed to a base element), never the raw user
//     text, or the controller will not resolve the tag.
//   * `FilePrefix` tells the driver it is PCCC (decode as ASCII words / bit offsets)
//     rather than Logix; null means symbolic name.
//   * InferDataType is a HEURISTIC for PCCC prefixes only (B bit file read as a word
//     unless /bit is given, F=Float32, N=Int16, ST/A=String, ...) and returns null
//     for Logix names — the tag's configured DataType always wins.
//   * Malformed input throws FormatException, which the driver converts to a
//     per-tag ConfigError. Never swallow it and return a default, or a mistyped
//     address will look like a successful read of the wrong tag.
//   * IsPccc(model) is the model-based dialect test; keep it in sync with MapPlcType
//     in AllenBradleyDriver when adding a controller family.
// =============================================================================
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
