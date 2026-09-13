using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using WTT.Application.Interfaces;
using WTT.Application.Security;
using WTT.Domain.Entities;
using WTT.Domain.Enums;
using WTT.Domain.ValueObjects;
using WTT.UI.Services;

namespace WTT.UI.ViewModels;

public partial class LiveDataViewModel : ViewModelBase
{
    private readonly IDeviceService _devices;
    private readonly ITagService _tags;
    private readonly ICaptureRuntimeStatusReader _runtimeStatus;
    private readonly ICurrentUserService _currentUser;
    private readonly NavigationContext _navContext;
    private readonly Func<WriteTagViewModel> _writeFactory;
    private readonly CsvExportService _csv;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Dictionary<int, LiveTagRow> _rowsByTagId = new();

    public LiveDataViewModel(IDeviceService devices, ITagService tags, ICaptureRuntimeStatusReader runtimeStatus, ICurrentUserService currentUser, NavigationContext navContext,
        Func<WriteTagViewModel> writeFactory, CsvExportService csv, IDialogService dialogs, ILogger<LiveDataViewModel> logger) : base(dialogs, logger)
    {
        _devices = devices; _tags = tags; _runtimeStatus = runtimeStatus; _currentUser = currentUser; _navContext = navContext; _writeFactory = writeFactory; _csv = csv;
        _statusTimer.Tick += async (_, _) => await RunAsync(LoadTagsAsync);
    }

    public ObservableCollection<Device> Devices { get; } = new();
    public ObservableCollection<LiveTagRow> Rows { get; } = new();

    [ObservableProperty] private Device? _selectedDevice;
    [ObservableProperty] private string _filter = string.Empty;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private int _goodCount;
    [ObservableProperty] private int _badCount;
    [ObservableProperty] private DateTime? _lastScanUtc;

    public bool CanWrite => _currentUser.HasPermission(Permission.WriteToPlc);
    public event EventHandler<WriteTagViewModel>? WriteRequested;

    partial void OnSelectedDeviceChanged(Device? value)
    {
        if (value is not null) _navContext.SelectedDeviceId = value.Id;
        _ = RunAsync(LoadTagsAsync);
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    public override async Task OnNavigatedToAsync()
    {
        await RunAsync(async () =>
        {
            Devices.Clear();
            foreach (var d in await _devices.GetAllAsync()) Devices.Add(d);
            SelectedDevice = Devices.FirstOrDefault(d => d.Id == _navContext.SelectedDeviceId) ?? Devices.FirstOrDefault();
        });
        _statusTimer.Start();
    }

    public override Task OnNavigatedFromAsync()
    {
        _statusTimer.Stop();
        return Task.CompletedTask;
    }

    private List<LiveTagRow> _allRows = new();

    private async Task LoadTagsAsync()
    {
        _rowsByTagId.Clear();
        _allRows = new List<LiveTagRow>();
        if (SelectedDevice is null) { Rows.Clear(); return; }

        var tags = await _tags.GetForDeviceAsync(SelectedDevice.Id);
        foreach (var t in tags.Where(t => t.IsSelected).OrderBy(t => t.Kind).ThenBy(t => t.Name))
        {
            var row = new LiveTagRow(t);
            if (t.CurrentValueAtUtc.HasValue) row.Apply(new LiveTagValue(t.DeviceId, t.Id, t.Name, t.CurrentValue ?? t.SetValue, t.CurrentQuality, t.CurrentValueAtUtc.Value));
            _allRows.Add(row);
            _rowsByTagId[t.Id] = row;
        }
        ApplyFilter();
        RefreshStatus();
    }

    private void ApplyFilter()
    {
        var q = string.IsNullOrWhiteSpace(Filter) ? _allRows : _allRows.Where(r => r.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase) || r.Address.Contains(Filter, StringComparison.OrdinalIgnoreCase));
        Rows.Clear();
        foreach (var r in q) Rows.Add(r);
    }

    private async void RefreshStatus()
    {
        if (SelectedDevice is null) { StatusText = string.Empty; return; }
        var snapshot = await _runtimeStatus.GetSnapshotAsync();
        var s = snapshot.Devices.FirstOrDefault(x => x.DeviceId == SelectedDevice.Id);
        IsConnected = s?.IsConnected ?? false;
        LastScanUtc = s?.LastScanUtc;
        StatusText = !snapshot.StatusAvailable ? $"Status unavailable: {snapshot.ServiceError}" : !snapshot.IsServiceRunning ? "Pipeline service is stopped."
            : s is null ? "Device worker is not enabled."
            : !s.IsConnected ? $"Not connected{(s.LastError is null ? string.Empty : " — " + s.LastError)}"
            : $"{(s.IsCapturing ? "Capturing" : "Monitoring")} · {s.ScanCount:N0} scans · {s.ReadingsWritten:N0} readings written · trigger {(s.LastTriggerState is true ? "ON" : "OFF")}";
        GoodCount = _allRows.Count(r => r.Quality == TagQuality.Good);
        BadCount = _allRows.Count - GoodCount;
    }

    [RelayCommand]
    private void Write(LiveTagRow? row)
    {
        if (row is null) return;
        var vm = _writeFactory();
        vm.Initialize(row.Tag);
        WriteRequested?.Invoke(this, vm);
    }

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(LoadTagsAsync);

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        if (SelectedDevice is null || Rows.Count == 0) { Dialogs.Toast("Export", "There is no visible data to export.", ToastKind.Warning); return; }
        var path = await _csv.ExportLiveDataAsync(Rows.ToList(), SelectedDevice.Name);
        if (path is not null) Dialogs.Toast("Export complete", path, ToastKind.Success);
    }
}

public partial class LiveTagRow : ObservableObject
{
    public LiveTagRow(DeviceTag tag)
    {
        Tag = tag;
        Name = tag.DisplayName;
        Address = tag.Kind == TagKind.Plc ? tag.PlcAddress : tag.Kind.ToString();
        Kind = tag.Kind;
        DataType = tag.DataType.ToString();
        Unit = tag.EngineeringUnit;
    }

    public DeviceTag Tag { get; }
    public string Name { get; }
    public string Address { get; }
    public TagKind Kind { get; }
    public string DataType { get; }
    public string? Unit { get; }
    public bool IsWritable => Kind != TagKind.Plc || Tag.IsWriteEnabled;

    [ObservableProperty] private string? _value;
    [ObservableProperty] private TagQuality _quality = TagQuality.Uncertain;
    [ObservableProperty] private DateTime? _timestampUtc;
    [ObservableProperty] private bool _changed;

    public void Apply(LiveTagValue v)
    {
        Changed = Value is not null && v.Value != Value;
        Value = v.Value;
        Quality = v.Quality;
        TimestampUtc = v.TimestampUtc;
    }
}

public partial class WriteTagViewModel : ViewModelBase
{
    private readonly IPlcOperationsService _plcOps;
    private readonly ITagService _tags;
    private DeviceTag _tag = null!;

    public WriteTagViewModel(IPlcOperationsService plcOps, ITagService tags, IDialogService dialogs, ILogger<WriteTagViewModel> logger) : base(dialogs, logger)
    {
        _plcOps = plcOps; _tags = tags;
    }

    public event EventHandler? Closed;

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string _value = string.Empty;

    public void Initialize(DeviceTag tag)
    {
        _tag = tag;
        Title = $"Write — {tag.DisplayName}";
        Description = tag.Kind == TagKind.Plc
            ? $"Write a new value to {tag.PlcAddress} ({tag.DataType}) on the controller. This changes the live process value — proceed with care."
            : "Set the constant value of this custom tag.";
        Value = tag.CurrentValue ?? tag.SetValue ?? string.Empty;
    }

    [RelayCommand]
    private async Task WriteAsync()
    {
        if (_tag.Kind == TagKind.Plc && !await Dialogs.ConfirmAsync("Confirm write", $"Write '{Value}' to {_tag.Name} ({_tag.PlcAddress})?", "Write", destructive: true)) return;
        var ok = await RunAsync(async () =>
        {
            if (_tag.Kind == TagKind.Plc) await _plcOps.WriteTagAsync(_tag.Id, Value);
            else await _tags.SetCustomValueAsync(_tag.Id, Value);
            Dialogs.Toast("Write", $"'{Value}' written to {_tag.Name}.", ToastKind.Success);
        }, inlineErrors: true);
        if (ok) Closed?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand] private void Cancel() => Closed?.Invoke(this, EventArgs.Empty);
}
