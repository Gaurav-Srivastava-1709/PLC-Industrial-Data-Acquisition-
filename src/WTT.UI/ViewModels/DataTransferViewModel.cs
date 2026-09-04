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

    public bool CanControl => _currentUser.HasPermission(Permission.ControlCapture);
    public bool CanManage => _currentUser.HasPermission(Permission.ManageTransfer);

    public override async Task OnNavigatedToAsync()
    {
        _transfer.BatchCompleted += OnBatchCompleted;
        await RefreshAsync();
        _timer.Start();
    }

    public override Task OnNavigatedFromAsync()
    {
        _timer.Stop();
        _transfer.BatchCompleted -= OnBatchCompleted;
        return Task.CompletedTask;
    }

    private void OnBatchCompleted(object? sender, TransferBatch b) => OnUi(() =>
    {
        Batches.Insert(0, b);
        while (Batches.Count > 100) Batches.RemoveAt(Batches.Count - 1);
    });

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            var s = await _transfer.GetStatisticsAsync();
            Pending = s.PendingRecords; TransferredToday = s.TransferredToday; FailedToday = s.FailedBatchesToday; LastSuccessUtc = s.LastSuccessAtUtc; IsPaused = _transfer.IsPaused;
            var servers = await _servers.GetAllAsync();
            Servers.Clear(); foreach (var x in servers) Servers.Add(x);
            var batches = await _transfer.GetRecentBatchesAsync(100);
            Batches.Clear(); foreach (var b in batches) Batches.Add(b);
        }
        catch (Exception ex) { Logger.LogWarning(ex, "Transfer page refresh failed."); }
    }

    [RelayCommand]
    private async Task TransferNowAsync(DeviceServer? server)
    {
        await RunAsync(async () =>
        {
            var n = await _transfer.TransferNowAsync(server?.DeviceId);
            Dialogs.Toast("Data transfer", n == 0 ? "Nothing to transfer." : $"{n:N0} readings transferred.", ToastKind.Success);
            await RefreshAsync();
        }, "Transfer failed");
    }

    [RelayCommand]
    private async Task ToggleEnabledAsync(DeviceServer? server)
    {
        if (server is null) return;
        if (await RunAsync(() => _servers.SetEnabledAsync(server.DeviceId, !server.IsEnabled))) await RefreshAsync();
    }

    [RelayCommand]
    private void TogglePause()
    {
        if (_transfer.IsPaused) _transfer.Resume(); else _transfer.Pause();
        IsPaused = _transfer.IsPaused;
    }

    [RelayCommand]
    private void Configure(DeviceServer? server)
    {
        if (server is null) return;
        _navContext.SelectedDeviceId = server.DeviceId;
        _navigation.Navigate(typeof(Views.Pages.DeviceConfigPage));
    }
}
