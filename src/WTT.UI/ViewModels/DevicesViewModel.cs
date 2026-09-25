using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Wpf.Ui;
using WTT.Application.Catalog;
using WTT.Application.DTOs;
using WTT.Application.Interfaces;
using WTT.Application.Security;
using WTT.Domain.Entities;
using WTT.Domain.Enums;
using WTT.Domain.ValueObjects;
using WTT.UI.Services;

namespace WTT.UI.ViewModels;

public partial class DevicesViewModel : ViewModelBase
{
    private readonly IDeviceService _devices;
    private readonly ICurrentUserService _currentUser;
    private readonly INavigationService _navigation;
    private readonly NavigationContext _navContext;
    private readonly Func<DeviceEditViewModel> _editFactory;
    private readonly ICaptureRuntimeStatusReader _runtimeStatus;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private List<DeviceRowViewModel> _all = new();

    public DevicesViewModel(IDeviceService devices, ICurrentUserService currentUser, INavigationService navigation, NavigationContext navContext,
        Func<DeviceEditViewModel> editFactory, ICaptureRuntimeStatusReader runtimeStatus, IDialogService dialogs, ILogger<DevicesViewModel> logger) : base(dialogs, logger)
    {
        _devices = devices;
        _currentUser = currentUser;
        _navigation = navigation;
        _navContext = navContext;
        _editFactory = editFactory;
        _runtimeStatus = runtimeStatus;
        _statusTimer.Tick += async (_, _) => await RefreshStatusesAsync();
    }

    public ObservableCollection<DeviceRowViewModel> Devices { get; } = new();

    [ObservableProperty] private DeviceRowViewModel? _selected;
    [ObservableProperty] private string _search = string.Empty;

    public bool CanManage => _currentUser.HasPermission(Permission.ManageDevices);

    /// <summary>Raised when the view should show the edit dialog.</summary>
    public event EventHandler<DeviceEditViewModel>? EditRequested;

    partial void OnSearchChanged(string value) => ApplyFilter();

    public override async Task OnNavigatedToAsync()
    {
        await LoadAsync();
        _statusTimer.Start();
    }

    public override Task OnNavigatedFromAsync()
    {
        _statusTimer.Stop();
        return Task.CompletedTask;
    }

    private async Task LoadAsync()
    {
        await RunAsync(async () =>
        {
            var list = (await _devices.GetAllAsync()).ToList();
            var snapshot = await _runtimeStatus.GetSnapshotAsync();
            var statuses = snapshot.Devices.ToDictionary(s => s.DeviceId);
            _all = list.Select(d => new DeviceRowViewModel(d, statuses.GetValueOrDefault(d.Id))).ToList();
            ApplyFilter();
        });
    }

    // Display-only refresh: re-reads the snapshot and updates dots in place (no Clear → no flicker, selection kept).
    private async Task RefreshStatusesAsync()
    {
        try
        {
            var snapshot = await _runtimeStatus.GetSnapshotAsync();
            var statuses = snapshot.Devices.ToDictionary(s => s.DeviceId);
            OnUi(() =>
            {
                foreach (var row in _all) row.Update(statuses.GetValueOrDefault(row.Device.Id));
            });
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Devices status refresh failed.");
        }
    }

    private void ApplyFilter()
    {
        var q = string.IsNullOrWhiteSpace(Search) ? _all
            : _all.Where(d => d.Device.Name.Contains(Search, StringComparison.OrdinalIgnoreCase) || d.Device.Host.Contains(Search, StringComparison.OrdinalIgnoreCase) || d.Device.Model.ToString().Contains(Search, StringComparison.OrdinalIgnoreCase));
        Devices.Clear();
        foreach (var d in q) Devices.Add(d);
    }

    [RelayCommand]
    private void Add()
    {
        var vm = _editFactory();
        vm.Initialize(null);
        vm.Saved += async (_, _) => await LoadAsync();
        EditRequested?.Invoke(this, vm);
    }

    [RelayCommand]
    private void Edit(DeviceRowViewModel? row)
    {
        var device = row?.Device ?? Selected?.Device;
        if (device is null) return;
        var vm = _editFactory();
        vm.Initialize(device);
        vm.Saved += async (_, _) => await LoadAsync();
        EditRequested?.Invoke(this, vm);
    }

    [RelayCommand]
    private void Configure(DeviceRowViewModel? row)
    {
        var device = row?.Device ?? Selected?.Device;
        if (device is null) return;
        _navContext.SelectedDeviceId = device.Id;
        _navigation.Navigate(typeof(Views.Pages.DeviceConfigPage));
    }

    [RelayCommand]
    private async Task DeleteAsync(DeviceRowViewModel? row)
    {
        var device = row?.Device ?? Selected?.Device;
        if (device is null) return;
        if (!await Dialogs.ConfirmAsync("Delete device", $"Delete '{device.Name}' and all its tags, address blocks, queries and transfer settings?\n\nThis cannot be undone.", "Delete", destructive: true))
            return;
        if (await RunAsync(() => _devices.DeleteAsync(device.Id), "Cannot delete device"))
        {
            Dialogs.Toast("Devices", $"'{device.Name}' deleted.", ToastKind.Success);
            await LoadAsync();
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();
}

/// <summary>
/// Display-only row wrapper: the Device entity plus its live connection state.
/// Same status-text rules as <see cref="DeviceCardViewModel"/> so both screens agree.
/// </summary>
public partial class DeviceRowViewModel : ObservableObject
{
    public DeviceRowViewModel(Device device, DeviceCaptureStatus? status)
    {
        Device = device;
        Update(status);
    }

    public Device Device { get; }

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string? _lastError;
    [ObservableProperty] private string _statusText = "Stopped";

    public string StatusToolTip => string.IsNullOrWhiteSpace(LastError) ? StatusText : $"{StatusText} — {LastError}";

    public void Update(DeviceCaptureStatus? s)
    {
        if (s is null)
        {
            IsRunning = false; IsConnected = false; LastError = null;
            StatusText = Device.IsEnabled ? "Stopped" : "Disabled";
            return;
        }
        IsRunning = s.IsRunning;
        IsConnected = s.IsConnected;
        LastError = s.LastError;
        StatusText = !s.IsRunning ? "Stopped"
            : !s.IsConnected ? (s.ReconnectAttempts > 0 ? $"Reconnecting (attempt {s.ReconnectAttempts})" : "Connecting…")
            : "Connected";
    }
}

/// <summary>Add / edit dialog with Protocol → Manufacturer → Model cascade and conditional fields.</summary>
public partial class DeviceEditViewModel : ViewModelBase
{
    private readonly IDeviceService _devices;
    private readonly IPlcOperationsService _plcOps;
    private Device? _original;

    public DeviceEditViewModel(IDeviceService devices, IPlcOperationsService plcOps, IDialogService dialogs, ILogger<DeviceEditViewModel> logger) : base(dialogs, logger)
    {
        _devices = devices;
        _plcOps = plcOps;
        Protocols = PlcModelCatalog.Protocols.ToList();
    }

    public event EventHandler? Saved;
    public event EventHandler? Closed;

    public string Title => _original is null ? "Add device" : $"Edit device — {_original.Name}";

    public IReadOnlyList<ProtocolType> Protocols { get; }
    public ObservableCollection<Manufacturer> Manufacturers { get; } = new();
    public ObservableCollection<PlcModelInfo> Models { get; } = new();
    public IReadOnlyList<WordOrder> WordOrders { get; } = Enum.GetValues<WordOrder>();
    public IReadOnlyList<int> ConnectionSizes { get; } = [511, 4002];

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string? _description;
    [ObservableProperty] private ProtocolType _protocol = ProtocolType.ModbusTcp;
    [ObservableProperty] private Manufacturer? _manufacturer;
    [ObservableProperty] private PlcModelInfo? _model;
    [ObservableProperty] private string _host = string.Empty;
    [ObservableProperty] private int _port = 502;
    [ObservableProperty] private int _pollIntervalMs = 1000;
    [ObservableProperty] private int _timeoutMs = 3000;
    [ObservableProperty] private int _slot;
    [ObservableProperty] private int _rack;
    [ObservableProperty] private int _connectionSize = 511;
    [ObservableProperty] private string? _cipPath;
    [ObservableProperty] private byte _unitId = 1;
    [ObservableProperty] private WordOrder _wordOrder = WordOrder.LowHigh;
    [ObservableProperty] private bool _oneBasedAddressing = true;
    [ObservableProperty] private bool _isEnabled = true;
    [ObservableProperty] private string? _testResult;
    [ObservableProperty] private bool _testSucceeded;

    // Auto Tag Provisioning — sirf new device ke liye dikhega
    [ObservableProperty] private bool _autoSeedTags = true;
    [ObservableProperty] private string _autoSeedHint = string.Empty;

    public bool ShowSlot => Model?.UsesSlot ?? false;
    public bool ShowRack => Model?.UsesRack ?? false;
    public bool ShowConnectionSize => Model?.UsesConnectionSize ?? false;
    public bool ShowUnitId => Model?.UsesUnitId ?? false;
    public bool ShowWordOrder => Model?.UsesWordOrder ?? false;
    public bool ShowCipPath => Protocol == ProtocolType.EthernetIp;
    public string AddressHint => Model?.AddressHint ?? string.Empty;

    /// <summary>Auto-seed checkbox sirf new device add karte waqt dikhega, edit pe nahi.</summary>
    public bool ShowAutoSeed => _original is null;

    public void Initialize(Device? device)
    {
        _original = device;
        if (device is not null)
        {
            Name = device.Name; Description = device.Description; Host = device.Host; Port = device.Port;
            PollIntervalMs = device.PollIntervalMs; TimeoutMs = device.TimeoutMs; Slot = device.Slot; Rack = device.Rack;
            ConnectionSize = device.ConnectionSize; CipPath = device.CipPath; UnitId = device.UnitId; WordOrder = device.WordOrder;
            OneBasedAddressing = device.OneBasedAddressing; IsEnabled = device.IsEnabled;
            // Populate cascade deterministically: same-value assignment does not raise
            // OnProtocolChanged, so refill Manufacturers/Models explicitly.
            Manufacturers.Clear();
            foreach (var m in PlcModelCatalog.ManufacturersFor(device.Protocol)) Manufacturers.Add(m);
            Protocol = device.Protocol;
            // OnProtocolChanged may have reset Manufacturer to first; restore saved value.
            if (Manufacturers.Contains(device.Manufacturer))
                Manufacturer = device.Manufacturer;
            else
                Manufacturer ??= Manufacturers.FirstOrDefault();
            // Ensure models are populated for the final manufacturer.
            OnManufacturerChanged(Manufacturer);
            Model = Models.FirstOrDefault(m => m.Model == device.Model) ?? Models.FirstOrDefault();
        }
        else
        {
            Protocol = ProtocolType.ModbusTcp;
            // Force cascade even when Protocol value did not change (default is already ModbusTcp).
            OnProtocolChanged(Protocol);
        }
        OnPropertyChanged(nameof(Title));
    }

    partial void OnProtocolChanged(ProtocolType value)
    {
        Manufacturers.Clear();
        foreach (var m in PlcModelCatalog.ManufacturersFor(value)) Manufacturers.Add(m);
        if (Manufacturer is null || !Manufacturers.Contains(Manufacturer.Value)) Manufacturer = Manufacturers.FirstOrDefault();
        else OnManufacturerChanged(Manufacturer);
        OnPropertyChanged(nameof(ShowCipPath));
    }

    partial void OnManufacturerChanged(Manufacturer? value)
    {
        Models.Clear();
        if (value is null) return;
        foreach (var m in PlcModelCatalog.ModelsFor(Protocol, value.Value)) Models.Add(m);
        if (Model is null || Models.All(m => m.Model != Model.Model)) Model = Models.FirstOrDefault();
    }

    partial void OnModelChanged(PlcModelInfo? value)
    {
        if (value is not null && (_original is null || _original.Model != value.Model)) Port = value.DefaultPort;
        if (value is not null && _original is null)
        {
            var d = PlcModelCatalog.SequentialDefaults(value.Model);
            WordOrder = d.WordOrder;
        }
        OnPropertyChanged(nameof(ShowSlot)); OnPropertyChanged(nameof(ShowRack)); OnPropertyChanged(nameof(ShowConnectionSize));
        OnPropertyChanged(nameof(ShowUnitId)); OnPropertyChanged(nameof(ShowWordOrder)); OnPropertyChanged(nameof(AddressHint));

        // Auto-seed hint: model ke hisab se batao kitne tags banengi
        if (value is not null && _original is null)
        {
            var templateTags = Application.Catalog.TagTemplateLibrary.GetTemplateFor(value.Model);
            var canBrowse = value.Model is DeviceModel.ControlLogix or DeviceModel.CompactLogix;
            AutoSeedHint = canBrowse
                ? $"Browse {value.DisplayName} for tags (with {templateTags.Count} fallback starter tags)"
                : $"Create {templateTags.Count} default tags for {value.DisplayName}";
        }
        else AutoSeedHint = string.Empty;
    }

    private Device Build()
    {
        var d = _original is null ? new Device() : new Device { Id = _original.Id };
        d.Name = Name; d.Description = Description; d.Protocol = Protocol; d.Manufacturer = Manufacturer ?? Domain.Enums.Manufacturer.Generic;
        d.Model = Model?.Model ?? DeviceModel.GenericModbusTcp; d.Host = Host; d.Port = Port; d.PollIntervalMs = PollIntervalMs; d.TimeoutMs = TimeoutMs;
        d.Slot = Slot; d.Rack = Rack; d.ConnectionSize = ConnectionSize; d.CipPath = CipPath; d.UnitId = UnitId; d.WordOrder = WordOrder;
        d.OneBasedAddressing = OneBasedAddressing; d.IsEnabled = IsEnabled;
        return d;
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        TestResult = null;
        await RunAsync(async () =>
        {
            var r = await _plcOps.TestConnectionAsync(Build());
            TestSucceeded = r.Succeeded;
            TestResult = $"{(r.Succeeded ? "✔" : "✖")} {r.Message} ({r.Elapsed.TotalMilliseconds:F0} ms)";
        }, inlineErrors: true);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var ok = await RunAsync(async () =>
        {
            var d = Build();
            // Naya device: seedTags parameter pass karo (checkbox se)
            // Edit: seedTags=false (existing device pe provisioning nahi)
            if (_original is null)
                await _devices.CreateAsync(d, seedTags: AutoSeedTags);
            else
                await _devices.UpdateAsync(d);
        }, inlineErrors: true);
        if (ok)
        {
            Dialogs.Toast("Devices", $"'{Name}' saved.", ToastKind.Success);
            Saved?.Invoke(this, EventArgs.Empty);
            Closed?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand]
    private void Cancel() => Closed?.Invoke(this, EventArgs.Empty);
}
