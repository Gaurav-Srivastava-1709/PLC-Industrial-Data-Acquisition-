using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Win32;
using WTT.UI.ViewModels;

namespace WTT.UI.Services;

public sealed class CsvExportService
{
    public async Task<string?> ExportLiveDataAsync(IReadOnlyList<LiveTagRow> rows, string deviceName, CancellationToken ct = default)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export live data",
            Filter = "CSV files (*.csv)|*.csv",
            FileName = $"WTT-{SanitizeFileName(deviceName)}-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            AddExtension = true,
            DefaultExt = ".csv",
        };
        if (dialog.ShowDialog() != true) return null;

        var temp = dialog.FileName + ".tmp";
        try
        {
            await using var writer = new StreamWriter(temp, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            await writer.WriteLineAsync("Device,Tag,AddressOrKind,DataType,Value,Unit,Quality,TimestampUtc");
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                var values = new[] { deviceName, row.Name, row.Address, row.DataType, SafeSpreadsheetValue(row.Value), row.Unit, row.Quality.ToString(), row.TimestampUtc?.ToString("O", CultureInfo.InvariantCulture) };
                await writer.WriteLineAsync(string.Join(',', values.Select(Escape)));
            }
            await writer.FlushAsync(ct);
            File.Move(temp, dialog.FileName, overwrite: true);
            return dialog.FileName;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static string Escape(string? value)
    {
        var text = value ?? string.Empty;
        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{text.Replace("\"", "\"\"")}\"" : text;
    }

    private static string SafeSpreadsheetValue(string? value) =>
        !string.IsNullOrEmpty(value) && "=+-@".Contains(value[0], StringComparison.Ordinal) ? "'" + value : value ?? string.Empty;

    private static string SanitizeFileName(string value) => string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
