using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Linq;
using System.Threading.Tasks;
using WTT.Application.Interfaces;
using WTT.Domain.Entities;

namespace WTT.UI.ViewModels;

public partial class DeviceEditViewModel : ObservableObject
{
    private readonly IDeviceService _deviceService;
    private readonly bool _isNew;

    public event EventHandler? OnRequestClose;

    [ObservableProperty]
    private PlcDevice _device;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    partial void OnErrorMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasError));
    }

    public DeviceEditViewModel(IDeviceService deviceService, PlcDevice? device = null)
    {
        _deviceService = deviceService;
        _isNew = device == null;
        
        // If device is null, we are creating a new one. Otherwise, edit existing.
        _device = device ?? new PlcDevice 
        { 
            Protocol = "ModbusTCP", // Default
            Port = 502
        };
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var validator = new WTT.Application.Validators.PlcDeviceValidator();
        var validationResult = await validator.ValidateAsync(Device);

        if (!validationResult.IsValid)
        {
            ErrorMessage = string.Join("\n", validationResult.Errors.Select(e => e.ErrorMessage));
            return;
        }
        
        ErrorMessage = string.Empty;

        try
        {
            if (_isNew)
            {
                await _deviceService.AddDeviceAsync(Device);
            }
            else
            {
                await _deviceService.UpdateDeviceAsync(Device);
            }

            OnRequestClose?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error saving device: {ex.Message}";
        }
    }
}
