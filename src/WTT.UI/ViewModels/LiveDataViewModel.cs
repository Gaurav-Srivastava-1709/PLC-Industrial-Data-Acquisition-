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

    /// <summary>
    /// True only while the very first load of this page instance is in flight, so the
    /// loading overlay can be shown for the initial open without strobing on the 2 s
    /// status refresh. A dedicated flag rather than "IsBusy AND Rows.Count == 0"
    /// because a device with zero selected tags legitimately keeps Rows empty, which
    /// would make that condition flash the spinner on every refresh forever.
    /// Cleared in the same step as the load finishing, and the overlay also requires
    /// IsBusy, so a failed first load cannot leave it spinning.
    /// </summary>
    [ObservableProperty] private bool _isInitialLoad;

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
        });

        // SelectedDevice is assigned OUTSIDE the RunAsync block on purpose.
        // ViewModelBase.RunAsync drops re-entrant calls (if (IsBusy) return false),
        // so assigning SelectedDevice while the block above was still busy made
        // OnSelectedDeviceChanged fire RunAsync(LoadTagsAsync) into that guard,
        // which returned false and threw the load away silently - no exception,
        // no log. The first data load then only happened on the first status
        // timer tick, i.e. up to 2 s after the page opened. That was the
        // "loads late" symptom.
        IsInitialLoad = Devices.Count > 0;
        SelectedDevice = Devices.FirstOrDefault(d => d.Id == _navContext.SelectedDeviceId) ?? Devices.FirstOrDefault();

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
        await RefreshStatusAsync();
        IsInitialLoad = false;
    }

    private void ApplyFilter()
    {
        var q = string.IsNullOrWhiteSpace(Filter) ? _allRows : _allRows.Where(r => r.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase) || r.Address.Contains(Filter, StringComparison.OrdinalIgnoreCase));
        Rows.Clear();
        foreach (var r in q) Rows.Add(r);
    }

    // Was `async void`. That is an unobserved exception sink: on a transient SQL
    // failure the throw had nowhere to go and surfaced as a Dispatcher-level crash
    // instead of a handled error, and it also let the status text race the tag
    // load. Now a normal awaited Task, so the snapshot read is part of the same
    // guarded operation as the tag read.
    private async Task RefreshStatusAsync()
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
    private Task<bool> RefreshAsync() => RunAsync(LoadTagsAsync);

    [RelayCommand]
    private Task ExportCsvAsync() => RunAsync(async () =>
    {
        if (SelectedDevice is null || Rows.Count == 0) { Dialogs.Toast("Export", "There is no visible data to export.", ToastKind.Warning); return; }
        var path = await _csv.ExportLiveDataAsync(Rows.ToList(), SelectedDevice.Name);
        if (path is not null) Dialogs.Toast("Export complete", path, ToastKind.Success);
    }, "Cannot export CSV");
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
        AlarmMinThreshold = tag.AlarmMinThreshold;
        AlarmMaxThreshold = tag.AlarmMaxThreshold;
        IsAlarmEnabled = tag.IsAlarmEnabled;
        AlarmPriority = tag.AlarmPriority;
    }

    public DeviceTag Tag { get; }
    public string Name { get; }
    public string Address { get; }
    public TagKind Kind { get; }
    public string DataType { get; }
    public string? Unit { get; }
    public bool IsWritable => Kind != TagKind.Plc || Tag.IsWriteEnabled;
    public double? AlarmMinThreshold { get; }
    public double? AlarmMaxThreshold { get; }
    public bool IsAlarmEnabled { get; }
    public AlarmPriority AlarmPriority { get; }
    public string MinDisplay => AlarmMinThreshold.HasValue ? AlarmMinThreshold.Value.ToString("0.##") : "—";
    public string MaxDisplay => AlarmMaxThreshold.HasValue ? AlarmMaxThreshold.Value.ToString("0.##") : "—";
    public string AlarmStatus
    {
        get
        {
            if (!IsAlarmEnabled) return "—";
            if (!double.TryParse(Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v)) return "OK";
            if (AlarmMaxThreshold.HasValue && v > AlarmMaxThreshold.Value) return "HIGH";
            if (AlarmMinThreshold.HasValue && v < AlarmMinThreshold.Value) return "LOW";
            return "OK";
        }
    }
    public bool HasActiveAlarm => AlarmStatus == "HIGH" || AlarmStatus == "LOW";
    public bool IsHighAlarm => AlarmStatus == "HIGH";
    public bool IsLowAlarm => AlarmStatus == "LOW";

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
        OnPropertyChanged(nameof(AlarmStatus));
        OnPropertyChanged(nameof(HasActiveAlarm));
        OnPropertyChanged(nameof(IsHighAlarm));
        OnPropertyChanged(nameof(IsLowAlarm));
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
    [ObservableProperty] private bool _awaitingConfirm;
    [ObservableProperty] private string _writeButtonText = "Write";
    [ObservableProperty] private string _confirmMessage = string.Empty;

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
        // Two-step confirm inside this same window. The old code showed a second
        // ContentDialog in MainWindow which stayed hidden behind this modal window,
        // so users never saw it until they pressed Cancel. Industry practice
        // (ISA-101 style value echo + explicit confirm) stays, but in one place.
        if (_tag.Kind == TagKind.Plc && !AwaitingConfirm)
        {
            AwaitingConfirm = true;
            WriteButtonText = "Confirm Write";
            ConfirmMessage = $"Write '{Value}' to {_tag.Name} ({_tag.PlcAddress})? Click Confirm Write to proceed.";
            return;
        }
        AwaitingConfirm = false;
        WriteButtonText = "Write";
        ConfirmMessage = string.Empty;
        var ok = await RunAsync(async () =>
        {
            if (_tag.Kind == TagKind.Plc) await _plcOps.WriteTagAsync(_tag.Id, Value);
            else await _tags.SetCustomValueAsync(_tag.Id, Value);
            Dialogs.Toast("Write", $"'{Value}' written to {_tag.Name}.", ToastKind.Success);
        }, inlineErrors: true);
        if (ok) Closed?.Invoke(this, EventArgs.Empty);
    }

    partial void OnValueChanged(string value)
    {
        // If the user edits the value after arming, require fresh confirmation.
        if (AwaitingConfirm)
        {
            AwaitingConfirm = false;
            WriteButtonText = "Write";
            ConfirmMessage = string.Empty;
        }
    }

    [RelayCommand] private void Cancel()
    {
        // First Cancel press only disarms the armed confirm state.
        if (AwaitingConfirm)
        {
            AwaitingConfirm = false;
            WriteButtonText = "Write";
            ConfirmMessage = string.Empty;
            return;
        }
        Closed?.Invoke(this, EventArgs.Empty);
    }
}
