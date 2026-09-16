using System.Collections.ObjectModel;
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
using WTT.UI.Services;

namespace WTT.UI.ViewModels;

public partial class DevicesViewModel : ViewModelBase
{
    private readonly IDeviceService _devices;
    private readonly ICurrentUserService _currentUser;
    private readonly INavigationService _navigation;
    private readonly NavigationContext _navContext;
    private readonly Func<DeviceEditViewModel> _editFactory;
    private List<Device> _all = new();

    public DevicesViewModel(IDeviceService devices, ICurrentUserService currentUser, INavigationService navigation, NavigationContext navContext,
        Func<DeviceEditViewModel> editFactory, IDialogService dialogs, ILogger<DevicesViewModel> logger) : base(dialogs, logger)
    {
        _devices = devices;
        _currentUser = currentUser;
        _navigation = navigation;
        _navContext = navContext;
        _editFactory = editFactory;
    }

    public ObservableCollection<Device> Devices { get; } = new();

    [ObservableProperty] private Device? _selected;
    [ObservableProperty] private string _search = string.Empty;

    public bool CanManage => _currentUser.HasPermission(Permission.ManageDevices);

    /// <summary>Raised when the view should show the edit dialog.</summary>
    public event EventHandler<DeviceEditViewModel>? EditRequested;

    partial void OnSearchChanged(string value) => ApplyFilter();

    public override Task OnNavigatedToAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        await RunAsync(async () =>
        {
            _all = (await _devices.GetAllAsync()).ToList();
            ApplyFilter();
        });
    }

    private void ApplyFilter()
    {
        var q = string.IsNullOrWhiteSpace(Search) ? _all
            : _all.Where(d => d.Name.Contains(Search, StringComparison.OrdinalIgnoreCase) || d.Host.Contains(Search, StringComparison.OrdinalIgnoreCase) || d.Model.ToString().Contains(Search, StringComparison.OrdinalIgnoreCase));
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
    private void Edit(Device? device)
    {
        device ??= Selected;
        if (device is null) return;
        var vm = _editFactory();
        vm.Initialize(device);
        vm.Saved += async (_, _) => await LoadAsync();
        EditRequested?.Invoke(this, vm);
    }

    [RelayCommand]
    private void Configure(Device? device)
    {
        device ??= Selected;
        if (device is null) return;
        _navContext.SelectedDeviceId = device.Id;
        _navigation.Navigate(typeof(Views.Pages.DeviceConfigPage));
    }

    [RelayCommand]
    private async Task DeleteAsync(Device? device)
    {
        device ??= Selected;
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

    public bool ShowSlot => Model?.UsesSlot ?? false;
    public bool ShowRack => Model?.UsesRack ?? false;
    public bool ShowConnectionSize => Model?.UsesConnectionSize ?? false;
    public bool ShowUnitId => Model?.UsesUnitId ?? false;
    public bool ShowWordOrder => Model?.UsesWordOrder ?? false;
    public bool ShowCipPath => Protocol == ProtocolType.EthernetIp;
    public string AddressHint => Model?.AddressHint ?? string.Empty;

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
            if (_original is null) await _devices.CreateAsync(d); else await _devices.UpdateAsync(d);
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
