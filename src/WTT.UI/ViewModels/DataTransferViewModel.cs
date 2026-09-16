using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Wpf.Ui;
using WTT.Application.Interfaces;
using WTT.Application.Security;
using WTT.Domain.Entities;
using WTT.UI.Services;

namespace WTT.UI.ViewModels;

public partial class DataTransferViewModel : ViewModelBase
{
    private readonly IDeviceServerService _servers;
    private readonly IDataTransferController _transfer;
    private readonly ICurrentUserService _currentUser;
    private readonly INavigationService _navigation;
    private readonly NavigationContext _navContext;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    // Guards overlapping timer ticks: a slow refresh must never pile up on the UI thread (lag/scroll freeze).
    private int _refreshing;

    public DataTransferViewModel(IDeviceServerService servers, IDataTransferController transfer, ICurrentUserService currentUser, INavigationService navigation, NavigationContext navContext,
        IDialogService dialogs, ILogger<DataTransferViewModel> logger) : base(dialogs, logger)
    {
        _servers = servers; _transfer = transfer; _currentUser = currentUser; _navigation = navigation; _navContext = navContext;
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    public ObservableCollection<DeviceServer> Servers { get; } = new();
    public ObservableCollection<TransferBatch> Batches { get; } = new();

    [ObservableProperty] private long _pending;
    [ObservableProperty] private long _transferredToday;
    [ObservableProperty] private long _failedToday;
    [ObservableProperty] private DateTime? _lastSuccessUtc;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private bool _hasPending;

    public bool CanControl => _currentUser.HasPermission(Permission.ControlCapture);
    public bool CanManage => _currentUser.HasPermission(Permission.ManageTransfer);

    public override async Task OnNavigatedToAsync()
    {
        await RefreshAsync();
        _timer.Start();
    }

    public override Task OnNavigatedFromAsync()
    {
        _timer.Stop();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        // Skip when a previous tick is still running: prevents UI-thread pile-up and scroll jumps.
        if (System.Threading.Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        try
        {
            var s = await _transfer.GetStatisticsAsync();
            Pending = s.PendingRecords; TransferredToday = s.TransferredToday; FailedToday = s.FailedBatchesToday; LastSuccessUtc = s.LastSuccessAtUtc; IsPaused = _transfer.IsPaused;
            HasPending = Pending > 0;
            var servers = await _servers.GetAllAsync();
            // Update in place: full Clear() resets DataGrid scroll/selection every 5s (reported scroll freeze).
            SyncServers(servers);
            // 50 rows is plenty for "recent" and halves UI measure cost vs 100 rows every 5s.
            var batches = await _transfer.GetRecentBatchesAsync(50);
            SyncBatches(batches);
        }
        catch (Exception ex) { Logger.LogWarning(ex, "Transfer page refresh failed."); }
        finally { System.Threading.Interlocked.Exchange(ref _refreshing, 0); }
    }

    private void SyncServers(IReadOnlyList<DeviceServer> fresh)
    {
        // Entities don't raise property-changed, so in-place edits wouldn't repaint.
        // Instead rebuild only when something actually changed (usually 1 row; cheap and leaves the Batches grid alone).
        static string Fingerprint(DeviceServer s) =>
            $"{s.Id}|{s.IsEnabled}|{s.ServerName}|{s.DatabaseName}|{s.TableName}|{s.LastSuccessAtUtc?.Ticks}|{s.LastError}";
        if (Servers.Count == fresh.Count && Servers.Select(Fingerprint).SequenceEqual(fresh.Select(Fingerprint))) return;
        Servers.Clear(); foreach (var x in fresh) Servers.Add(x);
    }

    private void SyncBatches(IReadOnlyList<TransferBatch> fresh)
    {
        // Newest-first list: when nothing new arrived, skip rebuild so the user can scroll undisturbed.
        if (Batches.Count == fresh.Count && Batches.Select(b => b.Id).SequenceEqual(fresh.Select(b => b.Id))) return;
        // Common case: a few new batches on top -> insert them, drop from bottom (no full reset, scroll preserved).
        if (Batches.Count > 0 && fresh.Count > 0 && fresh.Count >= Batches.Count)
        {
            var existing = new HashSet<long>(Batches.Select(b => b.Id));
            var prepend = fresh.TakeWhile(b => !existing.Contains(b.Id)).ToList();
            if (prepend.Count > 0 && prepend.Count < fresh.Count)
            {
                for (var i = prepend.Count - 1; i >= 0; i--) Batches.Insert(0, prepend[i]);
                while (Batches.Count > fresh.Count) Batches.RemoveAt(Batches.Count - 1);
                return;
            }
        }
        Batches.Clear(); foreach (var b in fresh) Batches.Add(b);
    }

    [RelayCommand]
    private async Task ToggleEnabledAsync(DeviceServer? server)
    {
        if (server is null) return;
        if (await RunAsync(() => _servers.SetEnabledAsync(server.DeviceId, !server.IsEnabled))) await RefreshAsync();
    }

    [RelayCommand]
    private void Configure(DeviceServer? server)
    {
        if (server is null) return;
        _navContext.SelectedDeviceId = server.DeviceId;
        _navigation.Navigate(typeof(Views.Pages.DeviceConfigPage));
    }
}
