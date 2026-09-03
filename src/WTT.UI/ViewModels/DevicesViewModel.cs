using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using WTT.Application.Interfaces;
using WTT.Domain.Entities;
using WTT.UI.Views.Windows;

namespace WTT.UI.ViewModels;

public partial class DevicesViewModel : ObservableObject
{
    private readonly IDeviceService _deviceService;
    private readonly IServiceProvider _serviceProvider;
    private readonly IAuthService _authService;

    [ObservableProperty]
    private ObservableCollection<PlcDevice> _devices = new();

    [ObservableProperty]
    private PlcDevice? _selectedDevice;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isAdmin;

    public DevicesViewModel(IDeviceService deviceService, IServiceProvider serviceProvider, IAuthService authService)
    {
        _deviceService = deviceService;
        _serviceProvider = serviceProvider;
        _authService = authService;
        
        IsAdmin = _authService.CurrentUser?.Role == UserRole.Admin;
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
        if (!IsAdmin)
        {
            MessageBox.Show("Only administrators can delete devices.", "Access Denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (device == null) return;
        
        var result = MessageBox.Show($"Are you sure you want to delete {device.Name}?", "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes)
        {
            await _deviceService.DeleteDeviceAsync(device.Id);
            Devices.Remove(device);
        }
    }

    [RelayCommand]
    private async Task AddDeviceAsync()
    {
        if (!IsAdmin)
        {
            MessageBox.Show("Only administrators can add devices.", "Access Denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var vm = new DeviceEditViewModel(_deviceService);
        var window = new DeviceEditWindow(vm);
        window.ShowDialog();
        await LoadDevicesAsync();
    }

    [RelayCommand]
    private async Task EditDeviceAsync(PlcDevice? device)
    {
        if (!IsAdmin)
        {
            MessageBox.Show("Only administrators can edit devices.", "Access Denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (device == null) return;

        var vm = new DeviceEditViewModel(_deviceService, device);
        var window = new DeviceEditWindow(vm);
        window.ShowDialog();
        
        await LoadDevicesAsync();
    }
}
