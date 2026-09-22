using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using WTT.Application.Catalog;
using WTT.Application.Interfaces;
using WTT.Domain.Common;
using WTT.Domain.Entities;
using WTT.Domain.Enums;
using WTT.Infrastructure.PlcDrivers.AllenBradley;
using WTT.UI.Services;

namespace WTT.UI.ViewModels;

/// <summary>Add / edit any kind of tag (PLC, Custom, External).</summary>
public partial class TagEditViewModel : ViewModelBase
{
    private readonly ITagService _tags;
    private readonly IPlcOperationsService _plcOps;
    private Device _device = null!;
    private DeviceTag? _original;

    public TagEditViewModel(ITagService tags, IPlcOperationsService plcOps, IDialogService dialogs, ILogger<TagEditViewModel> logger) : base(dialogs, logger)
    {
        _tags = tags;
        _plcOps = plcOps;
    }

    public event EventHandler? Saved;
    public event EventHandler? Closed;

    public IReadOnlyList<TagDataType> DataTypes { get; } = Enum.GetValues<TagDataType>();
    public IReadOnlyList<ModbusRegisterType> RegisterTypes { get; } = Enum.GetValues<ModbusRegisterType>();
    public IReadOnlyList<AlarmPriority> AlarmPriorities { get; } = Enum.GetValues<AlarmPriority>();
    public ObservableCollection<DeviceAddress> Blocks { get; } = new();
    public ObservableCollection<DeviceQuery> Queries { get; } = new();
    public ObservableCollection<DeviceQueryColumn> Columns { get; } = new();

    [ObservableProperty] private string _title = "Add tag";
    [ObservableProperty] private TagKind _kind = TagKind.Plc;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string? _alias;
    [ObservableProperty] private string _plcAddress = string.Empty;
    [ObservableProperty] private TagDataType _dataType = TagDataType.Float32;
    [ObservableProperty] private ModbusRegisterType? _registerType;
    [ObservableProperty] private DeviceAddress? _block;
    [ObservableProperty] private int _elementCount = 1;
    [ObservableProperty] private bool _isSelected = true;
    [ObservableProperty] private string? _setValue;
    [ObservableProperty] private double? _scaleFactor;
    [ObservableProperty] private double? _scaleOffset;
    [ObservableProperty] private int? _decimalPlaces;
    [ObservableProperty] private string? _engineeringUnit;
    [ObservableProperty] private bool _isWriteEnabled;
    [ObservableProperty] private double? _writeMinimum;
    [ObservableProperty] private double? _writeMaximum;
    [ObservableProperty] private int _minimumWriteIntervalMs = 1000;
    [ObservableProperty] private DeviceQuery? _query;
    [ObservableProperty] private DeviceQueryColumn? _column;
    [ObservableProperty] private string? _readResult;
    [ObservableProperty] private string _addressHint = string.Empty;

    // ---- Alarm threshold fields ----
    [ObservableProperty] private bool _isAlarmEnabled;
    [ObservableProperty] private double? _alarmMinThreshold;
    [ObservableProperty] private double? _alarmMaxThreshold;
    [ObservableProperty] private double _alarmDeadband = 2.0;
    [ObservableProperty] private int _alarmOnDelaySeconds;
    [ObservableProperty] private int _alarmOffDelaySeconds;
    [ObservableProperty] private AlarmPriority _alarmPriority = AlarmPriority.Medium;
    [ObservableProperty] private string? _alarmDescription;

    public bool IsPlc => Kind == TagKind.Plc;
    public bool IsCustom => Kind == TagKind.Custom;
    public bool IsExternal => Kind == TagKind.External;
    public bool IsModbus => _device.Protocol == ProtocolType.ModbusTcp;
    public bool IsPccc => AllenBradleyAddress.IsPccc(_device.Model);
    public bool ShowString => DataType == TagDataType.String;

    partial void OnKindChanged(TagKind value) { OnPropertyChanged(nameof(IsPlc)); OnPropertyChanged(nameof(IsCustom)); OnPropertyChanged(nameof(IsExternal)); }
    partial void OnDataTypeChanged(TagDataType value) => OnPropertyChanged(nameof(ShowString));
    partial void OnQueryChanged(DeviceQuery? value)
    {
        Columns.Clear();
        if (value is null) return;
        foreach (var c in value.Columns.OrderBy(c => c.Ordinal)) Columns.Add(c);
        if (Column is null || Columns.All(c => c.Id != Column.Id)) Column = Columns.FirstOrDefault();
    }
    partial void OnPlcAddressChanged(string value)
    {
        // Convenience for PLC-5/SLC users: infer the data type from the file prefix while typing.
        if (_original is null && IsPccc && !string.IsNullOrWhiteSpace(value))
        {
            try { var t = AllenBradleyAddress.InferDataType(value); if (t.HasValue) DataType = t.Value; } catch (FormatException) { }
        }
    }

    public void Initialize(Device device, DeviceTag? tag, TagKind kind, IReadOnlyList<DeviceAddress> blocks, IReadOnlyList<DeviceQuery> queries)
    {
        _device = device;
        _original = tag;
        Blocks.Clear(); foreach (var b in blocks) Blocks.Add(b);
        Queries.Clear(); foreach (var q in queries.Where(q => q.IsVerified)) Queries.Add(q);
        AddressHint = PlcModelCatalog.Get(device.Model).AddressHint;
        Kind = kind;

        if (tag is not null)
        {
            Title = $"Edit tag — {tag.Name}";
            Name = tag.Name; Alias = tag.Alias; PlcAddress = tag.PlcAddress; DataType = tag.DataType; RegisterType = tag.RegisterType;
            Block = Blocks.FirstOrDefault(b => b.Id == tag.AddressId); ElementCount = tag.ElementCount; IsSelected = tag.IsSelected; SetValue = tag.SetValue;
            ScaleFactor = tag.ScaleFactor; ScaleOffset = tag.ScaleOffset; DecimalPlaces = tag.DecimalPlaces; EngineeringUnit = tag.EngineeringUnit;
            IsWriteEnabled = tag.IsWriteEnabled; WriteMinimum = tag.WriteMinimum; WriteMaximum = tag.WriteMaximum; MinimumWriteIntervalMs = tag.MinimumWriteIntervalMs;
            Query = Queries.FirstOrDefault(q => q.Id == tag.QueryId);
            Column = Columns.FirstOrDefault(c => c.Id == tag.QueryColumnId);

            // Alarm fields
            IsAlarmEnabled = tag.IsAlarmEnabled;
            AlarmMinThreshold = tag.AlarmMinThreshold;
            AlarmMaxThreshold = tag.AlarmMaxThreshold;
            AlarmDeadband = tag.AlarmDeadband;
            AlarmOnDelaySeconds = tag.AlarmOnDelaySeconds;
            AlarmOffDelaySeconds = tag.AlarmOffDelaySeconds;
            AlarmPriority = tag.AlarmPriority;
            AlarmDescription = tag.AlarmDescription;
        }
        else
        {
            Title = kind switch { TagKind.Custom => "Add custom tag", TagKind.External => "Add external tag", _ => "Add PLC tag" };
            if (IsModbus) { DataType = TagDataType.UInt16; RegisterType = ModbusRegisterType.HoldingRegisters; }
            if (kind == TagKind.Custom || kind == TagKind.External) DataType = TagDataType.String;
        }
        OnPropertyChanged(nameof(IsModbus)); OnPropertyChanged(nameof(IsPccc));
    }

    private DeviceTag Build() => new()
    {
        Id = _original?.Id ?? 0, DeviceId = _device.Id, Name = Name, Alias = Alias, Kind = Kind, PlcAddress = PlcAddress, DataType = DataType,
        RegisterType = IsModbus ? RegisterType : null, AddressId = IsModbus ? Block?.Id : null, ElementCount = Math.Max(1, ElementCount),
        ByteCount = TagValueFormatter.DefaultByteSize(DataType, ElementCount), IsSelected = IsSelected, SetValue = Kind == TagKind.Custom ? SetValue : _original?.SetValue,
        ScaleFactor = ScaleFactor, ScaleOffset = ScaleOffset, DecimalPlaces = DecimalPlaces, EngineeringUnit = EngineeringUnit,
        IsWriteEnabled = IsPlc && IsWriteEnabled, WriteMinimum = WriteMinimum, WriteMaximum = WriteMaximum, MinimumWriteIntervalMs = Math.Clamp(MinimumWriteIntervalMs, 250, 60000),
        QueryId = IsExternal ? Query?.Id : null, QueryColumnId = IsExternal ? Column?.Id : null,
        // Alarm fields
        IsAlarmEnabled = IsAlarmEnabled,
        AlarmMinThreshold = IsAlarmEnabled ? AlarmMinThreshold : null,
        AlarmMaxThreshold = IsAlarmEnabled ? AlarmMaxThreshold : null,
        AlarmDeadband = AlarmDeadband,
        AlarmOnDelaySeconds = AlarmOnDelaySeconds,
        AlarmOffDelaySeconds = AlarmOffDelaySeconds,
        AlarmPriority = AlarmPriority,
        AlarmDescription = AlarmDescription,
    };

    [RelayCommand]
    private async Task TestReadAsync()
    {
        if (_original is null) { ErrorMessage = "Save the tag first, then test-read it."; return; }
        await RunAsync(async () =>
        {
            var r = await _plcOps.ReadTagOnceAsync(_original.Id);
            ReadResult = r.IsGood ? $"✔ Value: {TagValueFormatter.Format(Build(), r.RawValue)}" : $"✖ {r.Quality}: {r.Error}";
        }, inlineErrors: true);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var ok = await RunAsync(() => _tags.SaveAsync(Build()), inlineErrors: true);
        if (ok) { Saved?.Invoke(this, EventArgs.Empty); Closed?.Invoke(this, EventArgs.Empty); }
    }

    [RelayCommand] private void Cancel() => Closed?.Invoke(this, EventArgs.Empty);
}

/// <summary>Modbus address block editor with model-aware defaults.</summary>
public partial class AddressEditViewModel : ViewModelBase
{
    private readonly IDeviceAddressService _addresses;
    private Device _device = null!;
    private DeviceAddress? _original;

    public AddressEditViewModel(IDeviceAddressService addresses, IDialogService dialogs, ILogger<AddressEditViewModel> logger) : base(dialogs, logger) => _addresses = addresses;

    public event EventHandler? Saved;
    public event EventHandler? Closed;

    public IReadOnlyList<ModbusAddressType> AddressTypes { get; } = Enum.GetValues<ModbusAddressType>();
    public IReadOnlyList<ModbusRegisterType> RegisterTypes { get; } = Enum.GetValues<ModbusRegisterType>();
    public IReadOnlyList<TagDataType> DataTypes { get; } = Enum.GetValues<TagDataType>().Where(t => t != TagDataType.String).ToList();

    [ObservableProperty] private string _title = "Add address block";
    [ObservableProperty] private ModbusAddressType _addressType = ModbusAddressType.Sequential;
    [ObservableProperty] private ModbusRegisterType _registerType = ModbusRegisterType.HoldingRegisters;
    [ObservableProperty] private int _startAddress = 1;
    [ObservableProperty] private TagDataType _dataType = TagDataType.UInt16;
    [ObservableProperty] private int _pointCount = 10;
    [ObservableProperty] private string _tagPrefix = string.Empty;
    [ObservableProperty] private bool _zeroBasedTagIndex;
    [ObservableProperty] private int _moduleOffset;
    [ObservableProperty] private int? _decimalPlaces;
    [ObservableProperty] private string? _description;
    [ObservableProperty] private string _preview = string.Empty;

    public bool IsSequential => AddressType == ModbusAddressType.Sequential;

    partial void OnAddressTypeChanged(ModbusAddressType value) { OnPropertyChanged(nameof(IsSequential)); UpdatePreview(); }
    partial void OnTagPrefixChanged(string value) => UpdatePreview();
    partial void OnPointCountChanged(int value) => UpdatePreview();
    partial void OnZeroBasedTagIndexChanged(bool value) => UpdatePreview();
    partial void OnStartAddressChanged(int value) => UpdatePreview();
    partial void OnDataTypeChanged(TagDataType value) => UpdatePreview();

    public void Initialize(Device device, DeviceAddress? block)
    {
        _device = device;
        _original = block;
        if (block is not null)
        {
            Title = $"Edit address block — {block.TagPrefix}";
            AddressType = block.AddressType; RegisterType = block.RegisterType; StartAddress = block.StartAddress; DataType = block.DataType;
            PointCount = block.PointCount; TagPrefix = block.TagPrefix; ZeroBasedTagIndex = block.ZeroBasedTagIndex; ModuleOffset = block.ModuleOffset;
            DecimalPlaces = block.DecimalPlaces; Description = block.Description;
        }
        else
        {
            var d = PlcModelCatalog.SequentialDefaults(device.Model);
            DataType = d.DataType; ZeroBasedTagIndex = d.ZeroBasedIndex; DecimalPlaces = d.DecimalPlaces;
            RegisterType = device.Model is DeviceModel.Mw100 or DeviceModel.Gm10 ? ModbusRegisterType.InputRegisters : ModbusRegisterType.HoldingRegisters;
            TagPrefix = device.Model switch { DeviceModel.Mw100 or DeviceModel.Gm10 => "CH", DeviceModel.Adam6017 or DeviceModel.Adam6018 => "AI", _ => "TAG" };
        }
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (!IsSequential || string.IsNullOrWhiteSpace(TagPrefix) || PointCount <= 0) { Preview = string.Empty; return; }
        var regs = TagValueFormatter.ModbusRegisterCount(DataType);
        var first = ZeroBasedTagIndex ? 0 : 1;
        var last = first + PointCount - 1;
        var lastAddr = _device?.Model == DeviceModel.Gm10 ? StartAddress + ModuleOffset + PlcModelCatalog.Gm10ChannelRegisterOffset(PointCount) : StartAddress + ModuleOffset + (PointCount - 1) * regs;
        Preview = $"Will generate {PointCount} tags: {TagPrefix}{first:000} @ {StartAddress + ModuleOffset} … {TagPrefix}{last:000} @ {lastAddr} ({DataType}, {regs} register(s) each)";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var ok = await RunAsync(() => _addresses.SaveAsync(new DeviceAddress
        {
            Id = _original?.Id ?? 0, DeviceId = _device.Id, AddressType = AddressType, RegisterType = RegisterType, StartAddress = StartAddress, DataType = DataType,
            PointCount = IsSequential ? PointCount : 0, TagPrefix = IsSequential ? TagPrefix : (string.IsNullOrWhiteSpace(TagPrefix) ? "BLOCK" : TagPrefix),
            ZeroBasedTagIndex = ZeroBasedTagIndex, ModuleOffset = ModuleOffset, DecimalPlaces = DecimalPlaces, Description = Description,
        }), inlineErrors: true);
        if (ok) { Saved?.Invoke(this, EventArgs.Empty); Closed?.Invoke(this, EventArgs.Empty); }
    }

    [RelayCommand] private void Cancel() => Closed?.Invoke(this, EventArgs.Empty);
}

/// <summary>External SQL query editor.</summary>
public partial class QueryEditViewModel : ViewModelBase
{
    private readonly IExternalQueryService _queries;
    private Device _device = null!;
    private DeviceQuery? _original;

    public QueryEditViewModel(IExternalQueryService queries, IDialogService dialogs, ILogger<QueryEditViewModel> logger) : base(dialogs, logger) => _queries = queries;

    public event EventHandler? Saved;
    public event EventHandler? Closed;

    public IReadOnlyList<ExternalQueryType> QueryTypes { get; } = Enum.GetValues<ExternalQueryType>();

    [ObservableProperty] private string _title = "Add external query";
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _serverName = string.Empty;
    [ObservableProperty] private string _databaseName = string.Empty;
    [ObservableProperty] private bool _useIntegratedSecurity;
    [ObservableProperty] private string? _userId;
    [ObservableProperty] private string? _password;
    [ObservableProperty] private bool _encrypt = true;
    [ObservableProperty] private bool _trustServerCertificate = true;
    [ObservableProperty] private string _queryText = "SELECT TOP 1 BatchNo, Shift FROM dbo.Production ORDER BY StartTime DESC";
    [ObservableProperty] private ExternalQueryType _queryType = ExternalQueryType.Contextual;
    [ObservableProperty] private bool _hasStoredPassword;

    public void Initialize(Device device, DeviceQuery? q)
    {
        _device = device; _original = q;
        if (q is null) return;
        Title = $"Edit external query — {q.Name}";
        Name = q.Name; ServerName = q.ServerName; DatabaseName = q.DatabaseName; UseIntegratedSecurity = q.UseIntegratedSecurity; UserId = q.UserId;
        Encrypt = q.Encrypt; TrustServerCertificate = q.TrustServerCertificate; QueryText = q.QueryText; QueryType = q.QueryType; HasStoredPassword = q.ProtectedPassword is not null;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var ok = await RunAsync(() => _queries.SaveAsync(new DeviceQuery
        {
            Id = _original?.Id ?? 0, DeviceId = _device.Id, Name = Name, ServerName = ServerName, DatabaseName = DatabaseName, UseIntegratedSecurity = UseIntegratedSecurity,
            UserId = UserId, Encrypt = Encrypt, TrustServerCertificate = TrustServerCertificate, QueryText = QueryText, QueryType = QueryType,
        }, string.IsNullOrEmpty(Password) ? null : Password), inlineErrors: true);
        if (ok) { Saved?.Invoke(this, EventArgs.Empty); Closed?.Invoke(this, EventArgs.Empty); }
    }

    [RelayCommand] private void Cancel() => Closed?.Invoke(this, EventArgs.Empty);
}

/// <summary>Central SQL Server target for a device.</summary>
public partial class TransferServerEditViewModel : ViewModelBase
{
    private readonly IDeviceServerService _servers;
    private Device _device = null!;
    private DeviceServer? _original;

    public TransferServerEditViewModel(IDeviceServerService servers, IDialogService dialogs, ILogger<TransferServerEditViewModel> logger) : base(dialogs, logger) => _servers = servers;

    public event EventHandler? Saved;
    public event EventHandler? Closed;

    [ObservableProperty] private string _title = "Central server";
    [ObservableProperty] private string _serverName = string.Empty;
    [ObservableProperty] private string _databaseName = string.Empty;
    [ObservableProperty] private string _tableName = "TagDetail";
    [ObservableProperty] private bool _useIntegratedSecurity;
    [ObservableProperty] private string? _userId;
    [ObservableProperty] private string? _password;
    [ObservableProperty] private bool _encrypt = true;
    [ObservableProperty] private bool _trustServerCertificate = true;
    [ObservableProperty] private bool _isEnabled = true;
    [ObservableProperty] private bool _hasStoredPassword;
    [ObservableProperty] private string? _testResult;
    [ObservableProperty] private bool _testSucceeded;

    public void Initialize(Device device, DeviceServer? s)
    {
        _device = device; _original = s;
        Title = $"Central server — {device.Name}";
        if (s is null) return;
        ServerName = s.ServerName; DatabaseName = s.DatabaseName; TableName = s.TableName; UseIntegratedSecurity = s.UseIntegratedSecurity; UserId = s.UserId;
        Encrypt = s.Encrypt; TrustServerCertificate = s.TrustServerCertificate; IsEnabled = s.IsEnabled; HasStoredPassword = s.ProtectedPassword is not null;
    }

    private DeviceServer Build() => new()
    {
        Id = _original?.Id ?? 0, DeviceId = _device.Id, ServerName = ServerName, DatabaseName = DatabaseName, TableName = TableName, UseIntegratedSecurity = UseIntegratedSecurity,
        UserId = UserId, Encrypt = Encrypt, TrustServerCertificate = TrustServerCertificate, IsEnabled = IsEnabled, ProtectedPassword = _original?.ProtectedPassword,
    };

    [RelayCommand]
    private async Task TestAsync()
    {
        TestResult = null;
        await RunAsync(async () =>
        {
            var r = await _servers.TestConnectionAsync(Build(), string.IsNullOrEmpty(Password) ? null : Password);
            TestSucceeded = r.Succeeded;
            TestResult = $"{(r.Succeeded ? "✔" : "✖")} {r.Message} ({r.Elapsed.TotalMilliseconds:F0} ms)";
        }, inlineErrors: true);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var ok = await RunAsync(() => _servers.SaveAsync(Build(), string.IsNullOrEmpty(Password) ? null : Password), inlineErrors: true);
        if (ok) { Saved?.Invoke(this, EventArgs.Empty); Closed?.Invoke(this, EventArgs.Empty); }
    }

    [RelayCommand] private void Cancel() => Closed?.Invoke(this, EventArgs.Empty);
}
