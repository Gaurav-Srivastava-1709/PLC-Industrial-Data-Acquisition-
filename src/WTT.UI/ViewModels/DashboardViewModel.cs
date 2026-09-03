using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WTT.Application.Interfaces;

namespace WTT.UI.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private readonly IDeviceService _deviceService;
    private readonly ITagService _tagService;
    private readonly IDataCaptureService _dataCaptureService;

    [ObservableProperty]
    private int _deviceCount;

    [ObservableProperty]
    private int _activeTagCount;

    [ObservableProperty]
    private int _recordsToday;

    [ObservableProperty]
    private int _errorCount;

    [ObservableProperty]
    private bool _isDataCaptureRunning;

    public DashboardViewModel(IDeviceService deviceService, ITagService tagService, IDataCaptureService dataCaptureService)
    {
        _deviceService = deviceService;
        _tagService = tagService;
        _dataCaptureService = dataCaptureService;

        _isDataCaptureRunning = _dataCaptureService.IsRunning;
        _dataCaptureService.ServiceStatusChanged += (_, running) => IsDataCaptureRunning = running;
    }

    public async Task RefreshDashboardAsync()
    {
        try
        {
            var devices = await _deviceService.GetAllDevicesAsync();
            DeviceCount = devices.Count;

            ActiveTagCount = await _tagService.GetActiveTagCountAsync();
            RecordsToday = await _dataCaptureService.GetTodayReadingsCountAsync();
            IsDataCaptureRunning = _dataCaptureService.IsRunning;
        }
        catch { }
    }

    [RelayCommand]
    private async Task ToggleDataCaptureAsync()
    {
        if (IsDataCaptureRunning)
        {
            await _dataCaptureService.StopAsync();
        }
        else
        {
            await _dataCaptureService.StartAsync();
        }
        IsDataCaptureRunning = _dataCaptureService.IsRunning;
        await RefreshDashboardAsync();
    }
}
