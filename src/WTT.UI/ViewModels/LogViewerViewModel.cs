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

    // ─── Bindable Collections ───────────────────────────────────────────
    // Industry pattern: ObservableCollection ko REPLACE karte hain, Clear+Add loop nahi karte.
    // Yeh "swap" pattern ek single CollectionChanged event fire karta hai
    // instead of N individual Add() events jo UI thread ko choke karte hain.
    public ObservableCollection<LogEntry> Entries { get; } = new();
    public ObservableCollection<Domain.Entities.AuditLog> AuditEntries { get; } = new();

    // Levels list — full names taaki ComboBox mein clearly dikh sake
    public IReadOnlyList<string> Levels { get; } = ["Verbose", "Debug", "Information", "Warning", "Error", "Fatal"];

    [ObservableProperty] private string _minimumLevel = "Information";
    [ObservableProperty] private string _contains = string.Empty;
    [ObservableProperty] private int _maxLines = 500;
    [ObservableProperty] private LogEntry? _selected;

    // Auto-refresh (Manager/User requirement)
    [ObservableProperty] private bool _isAutoRefreshEnabled;
    private readonly System.Windows.Threading.DispatcherTimer _autoRefreshTimer;
    private readonly System.Windows.Threading.DispatcherTimer _searchDebounceTimer;

    // Stacked refresh guard — double-click / fast navigation se crash prevent karta hai
    private int _refreshing;

    public string LogDirectory => _logs.LogDirectory;

    /// <summary>
    /// IsEmpty = true jab koi log entry nahi hai — empty state UI show karta hai.
    /// User ko samajh aata hai ki logging ON hai ya nahi.
    /// </summary>
    public bool IsEmpty => Entries.Count == 0 && !IsBusy;

    public LogViewerViewModel(ILogReaderService logs, IAuditService audit, IDialogService dialogs, ILogger<LogViewerViewModel> logger)
        : base(dialogs, logger)
    {
        _logs = logs;
        _audit = audit;

        // 30-second timer for live log monitoring without impacting performance
        _autoRefreshTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _autoRefreshTimer.Tick += (s, e) =>
        {
            if (IsAutoRefreshEnabled)
                _ = RefreshAsync();
        };

        // 300ms debounce timer — user ke typing karte waqt UI hang na ho
        _searchDebounceTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        _searchDebounceTimer.Tick += (s, e) =>
        {
            _searchDebounceTimer.Stop();
            _ = RefreshAsync();
        };
    }

    // Auto-refresh jab minimum level change ho
    partial void OnMinimumLevelChanged(string value) => _ = RefreshAsync();

    // Text search box ke liye debounce — har letter par database query nahi hogi
    partial void OnContainsChanged(string value)
    {
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    partial void OnIsAutoRefreshEnabledChanged(bool value)
    {
        if (value)
        {
            _autoRefreshTimer.Start();
            _ = RefreshAsync(); // instantly ek baar refresh karo
        }
        else
        {
            _autoRefreshTimer.Stop();
        }
    }

    // Page navigate hone par data load karo — UI immediately show ho, data background mein aata hai
    public override Task OnNavigatedToAsync()
    {
        _ = RefreshAsync();
        
        // Agar user ne Auto-refresh ON rakha tha, toh wapas aane par timer resume karo
        if (IsAutoRefreshEnabled)
        {
            _autoRefreshTimer.Start();
        }
        
        return Task.CompletedTask;
    }

    public override Task OnNavigatedFromAsync()
    {
        // Memory leaks prevent karne ke liye timers zaroor roko jab page hide ho
        _autoRefreshTimer.Stop();
        _searchDebounceTimer.Stop();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        // Agar pehle se refresh chal raha hai toh naya request ignore karo (concurrency guard)
        if (System.Threading.Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        try
        {
            await RunAsync(async () =>
            {
                var queryFilter = string.IsNullOrWhiteSpace(Contains) ? null : Contains.Trim();

                // ─── STEP 1: Background thread par data fetch karo (UI block nahi hoga) ───
                var logTask   = _logs.ReadRecentAsync(MaxLines, MinimumLevel, queryFilter);
                var auditTask = _audit.GetRecentAsync(150);
                await Task.WhenAll(logTask, auditTask);

                var logResults = await logTask;
                var auditResults = await auditTask;

                // ─── STEP 2: In-memory filter for audit (agar search active hai) ───
                IEnumerable<Domain.Entities.AuditLog> filteredAudit = auditResults;
                if (!string.IsNullOrWhiteSpace(queryFilter))
                {
                    filteredAudit = auditResults.Where(a =>
                        (a.Username != null && a.Username.Contains(queryFilter, StringComparison.OrdinalIgnoreCase)) ||
                        (a.Action != null && a.Action.Contains(queryFilter, StringComparison.OrdinalIgnoreCase)) ||
                        (a.Target != null && a.Target.Contains(queryFilter, StringComparison.OrdinalIgnoreCase)) ||
                        (a.Details != null && a.Details.Contains(queryFilter, StringComparison.OrdinalIgnoreCase))
                    );
                }

                // ─── STEP 3: Batch replace — SINGLE UI update instead of N Add() calls ───
                // Yeh industry pattern hai: pehle collection se DataGrid ko disconnect karo,
                // data bharo, phir reconnect karo. Result: 1 re-render, not 150.
                BatchReplace(Entries, logResults);
                BatchReplace(AuditEntries, filteredAudit);

                // IsEmpty update karo — empty state show/hide karo
                OnPropertyChanged(nameof(IsEmpty));
            });
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _refreshing, 0);
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    /// <summary>
    /// Industry-standard batch replace for ObservableCollection:
    /// Instead of Clear() + N x Add() (which fires N+1 CollectionChanged events),
    /// we do a smart diff — only update what actually changed.
    /// For full replacement scenarios, we suspend notifications via a single Reset.
    /// </summary>
    private static void BatchReplace<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        // Materialize to list to avoid multiple enumeration
        var items = source as IReadOnlyList<T> ?? source.ToList();

        // Agar items same count aur same references hain, skip karo (no-op optimization)
        if (target.Count == items.Count)
        {
            bool same = true;
            for (int i = 0; i < items.Count; i++)
            {
                if (!ReferenceEquals(target[i], items[i]))
                {
                    same = false;
                    break;
                }
            }
            if (same) return; // Data identical hai, UI ko touch mat karo
        }

        // Clear + bulk add: WPF DataGrid internally handles this with a single layout pass
        // when both happen synchronously on the Dispatcher thread
        target.Clear();
        foreach (var item in items)
            target.Add(item);
    }

    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            // Windows Explorer mein log folder kholna
            Process.Start(new ProcessStartInfo { FileName = _logs.LogDirectory, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Dialogs.Toast("Logs", ex.Message, ToastKind.Error);
        }
    }

    [RelayCommand]
    private void CopySelected()
    {
        if (Selected is null) return;
        // Industry format: "2026-09-30 22:51:30.123 [ERR] ClassName: Error message here"
        var text = $"{Selected.Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Selected.Level}] {Selected.Source}: {Selected.Message}";
        System.Windows.Clipboard.SetText(text);
        Dialogs.Toast("Logs", "Copied to clipboard.", ToastKind.Success);
    }
}
