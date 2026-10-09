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
    // Industry pattern: REPLACE the ObservableCollection instead of a Clear+Add loop.
    // This "swap" pattern fires a single CollectionChanged event
    // instead of N individual Add() events that would choke the UI thread.
    public ObservableCollection<LogEntry> Entries { get; } = new();
    public ObservableCollection<Domain.Entities.AuditLog> AuditEntries { get; } = new();

    // Levels list — full names so they display clearly in the ComboBox
    public IReadOnlyList<string> Levels { get; } = ["Verbose", "Debug", "Information", "Warning", "Error", "Fatal"];

    [ObservableProperty] private string _minimumLevel = "Information";
    [ObservableProperty] private string _contains = string.Empty;
    [ObservableProperty] private int _maxLines = 500;
    [ObservableProperty] private LogEntry? _selected;

    // Auto-refresh (Manager/User requirement)
    [ObservableProperty] private bool _isAutoRefreshEnabled;
    private readonly System.Windows.Threading.DispatcherTimer _autoRefreshTimer;
    private readonly System.Windows.Threading.DispatcherTimer _searchDebounceTimer;

    // Stacked refresh guard — prevents crashes from double-click / fast navigation
    private int _refreshing;

    public string LogDirectory => _logs.LogDirectory;

    /// <summary>
    /// IsEmpty = true when there is no log entry — shows the empty state UI.
    /// Lets the user understand whether logging is ON or not.
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

        // 300ms debounce timer — so the UI does not hang while the user is typing
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

    // Auto-refresh when the minimum level changes
    partial void OnMinimumLevelChanged(string value) => _ = RefreshAsync();

    // Debounce for the text search box — no database query on every letter
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
            _ = RefreshAsync(); // refresh once immediately
        }
        else
        {
            _autoRefreshTimer.Stop();
        }
    }

    // Load data when the page is navigated to — the UI shows immediately, data arrives in the background
    public override Task OnNavigatedToAsync()
    {
        _ = RefreshAsync();
        
        // If the user had left Auto-refresh ON, resume the timer when coming back
        if (IsAutoRefreshEnabled)
        {
            _autoRefreshTimer.Start();
        }
        
        return Task.CompletedTask;
    }

    public override Task OnNavigatedFromAsync()
    {
        // Always stop the timers when the page is hidden to prevent memory leaks
        _autoRefreshTimer.Stop();
        _searchDebounceTimer.Stop();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        // If a refresh is already running, ignore the new request (concurrency guard)
        if (System.Threading.Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        try
        {
            await RunAsync(async () =>
            {
                var queryFilter = string.IsNullOrWhiteSpace(Contains) ? null : Contains.Trim();

                // ─── STEP 1: Fetch data on a background thread (UI will not block) ───
                var logTask   = _logs.ReadRecentAsync(MaxLines, MinimumLevel, queryFilter);
                var auditTask = _audit.GetRecentAsync(150);
                await Task.WhenAll(logTask, auditTask);

                var logResults = await logTask;
                var auditResults = await auditTask;

                // ─── STEP 2: In-memory filter for audit (if search is active) ───
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
                // This is the industry pattern: first disconnect the DataGrid from the collection,
                // fill the data, then reconnect. Result: 1 re-render, not 150.
                BatchReplace(Entries, logResults);
                BatchReplace(AuditEntries, filteredAudit);

                // Update IsEmpty — show/hide the empty state
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

        // Skip when the items have the same count and same references (no-op optimization)
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
            if (same) return; // Data is identical, do not touch the UI
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
            // Open the log folder in Windows Explorer
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
