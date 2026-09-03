using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using WTT.Application.Interfaces;
using WTT.Domain.Entities;

namespace WTT.UI.ViewModels;

public partial class DevicesViewModel : ObservableObject
{
    private readonly IDeviceService _deviceService;
    private readonly System.IServiceProvider _serviceProvider;

    [ObservableProperty]
    private ObservableCollection<PlcDevice> _devices = new();

    [ObservableProperty]
    private PlcDevice? _selectedDevice;

    [ObservableProperty]
    private bool _isLoading;

    public DevicesViewModel(IDeviceService deviceService, System.IServiceProvider serviceProvider)
    {
        _deviceService = deviceService;
        _serviceProvider = serviceProvider;
        _ = LoadDevicesAsync();
    }

    [RelayCommand]
    private async Task LoadDevicesAsync()
    {
        IsLoading = true;
        try
        {
            var result = await _deviceService.GetAllDevicesAsync();
            Devices.Clear();
            foreach (var item in result)
            {
                Devices.Add(item);
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task DeleteDeviceAsync(PlcDevice? device)
    {
        if (device == null) return;
        
        var result = MessageBox.Show($"Are you sure you want to delete {device.Name}?", "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes)
        {
            await _deviceService.DeleteDeviceAsync(device.Id);
            Devices.Remove(device);
        }
    }

    [RelayCommand]
    private void AddDevice()
    {
        var viewModel = new DeviceEditViewModel(_deviceService);
        var window = new Views.Windows.DeviceEditWindow(viewModel);
        window.ShowDialog();
        
        // Reload devices after closing the window
        _ = LoadDevicesAsync();
    }

    [RelayCommand]
    private void EditDevice(PlcDevice? device)
    {
        if (device == null) return;

        var viewModel = new DeviceEditViewModel(_deviceService, device);
        var window = new Views.Windows.DeviceEditWindow(viewModel);
        window.ShowDialog();
        
        // Reload devices after closing the window
        _ = LoadDevicesAsync();
    }
}
