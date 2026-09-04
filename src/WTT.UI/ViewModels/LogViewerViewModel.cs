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
    [ObservableProperty] private int _maxLines = 500;
    [ObservableProperty] private LogEntry? _selected;

    public string LogDirectory => _logs.LogDirectory;

    public override Task OnNavigatedToAsync() => RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync() => await RunAsync(async () =>
    {
        var entries = await _logs.ReadRecentAsync(MaxLines, MinimumLevel, string.IsNullOrWhiteSpace(Contains) ? null : Contains);
        Entries.Clear(); foreach (var e in entries) Entries.Add(e);
        var audit = await _audit.GetRecentAsync(200);
        AuditEntries.Clear(); foreach (var a in audit) AuditEntries.Add(a);
    });

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
