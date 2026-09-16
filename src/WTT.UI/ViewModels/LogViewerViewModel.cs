using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using WTT.Application.DTOs;
using WTT.Application.Interfaces;
using WTT.UI.Services;

namespace WTT.UI.ViewModels;

public partial class LogViewerViewModel : ViewModelBase
{
    private readonly ILogReaderService _logs;
    private readonly IAuditService _audit;

    public LogViewerViewModel(ILogReaderService logs, IAuditService audit, IDialogService dialogs, ILogger<LogViewerViewModel> logger) : base(dialogs, logger)
    {
        _logs = logs; _audit = audit;
    }

    public ObservableCollection<LogEntry> Entries { get; } = new();
    public ObservableCollection<Domain.Entities.AuditLog> AuditEntries { get; } = new();
    public IReadOnlyList<string> Levels { get; } = ["Verbose", "Debug", "Information", "Warning", "Error", "Fatal"];

    [ObservableProperty] private string _minimumLevel = "Information";
    [ObservableProperty] private string _contains = string.Empty;
    [ObservableProperty] private int _maxLines = 200;
    [ObservableProperty] private LogEntry? _selected;
    // Guards stacked refreshes (double-click / fast navigation) that froze the page.
    private int _refreshing;

    public string LogDirectory => _logs.LogDirectory;

    // Page turant dikhna chahiye — data piche se aayega (progress bar chalegi).
    // Pehle navigation poora refresh ka wait karta tha, isliye 20 sec tak
    // khaali table par app freeze lagta tha.
    public override Task OnNavigatedToAsync()
    {
        _ = RefreshAsync();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (System.Threading.Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        try
        {
            await RunAsync(async () =>
            {
                // File aur database ko sath-sath padho (pehle ek-ek karke padhte the,
                // isliye slow DB par page 20 sec tak khaali lagta tha).
                var logTask = _logs.ReadRecentAsync(MaxLines, MinimumLevel, string.IsNullOrWhiteSpace(Contains) ? null : Contains);
                var auditTask = _audit.GetRecentAsync(200);
                await Task.WhenAll(logTask, auditTask);
                Entries.Clear(); foreach (var e in await logTask) Entries.Add(e);
                AuditEntries.Clear(); foreach (var a in await auditTask) AuditEntries.Add(a);
            });
        }
        finally { System.Threading.Interlocked.Exchange(ref _refreshing, 0); }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        try { Process.Start(new ProcessStartInfo { FileName = _logs.LogDirectory, UseShellExecute = true }); }
        catch (Exception ex) { Dialogs.Toast("Logs", ex.Message, ToastKind.Error); }
    }

    [RelayCommand]
    private void CopySelected()
    {
        if (Selected is null) return;
        System.Windows.Clipboard.SetText($"{Selected.Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Selected.Level}] {Selected.Source}: {Selected.Message}");
        Dialogs.Toast("Logs", "Copied to clipboard.", ToastKind.Success);
    }
}
