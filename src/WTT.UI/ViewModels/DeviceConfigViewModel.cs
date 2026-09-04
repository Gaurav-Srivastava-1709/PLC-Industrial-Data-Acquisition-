using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Wpf.Ui;
using WTT.Application.Catalog;
using WTT.Application.Interfaces;
using WTT.Application.Security;
using WTT.Application.Triggers;
using WTT.Domain.Entities;
using WTT.Domain.Enums;
using WTT.UI.Services;

namespace WTT.UI.ViewModels;

/// <summary>Everything about one device: tags, Modbus blocks, external context, capture trigger and transfer target.</summary>
public partial class DeviceConfigViewModel : ViewModelBase
{
    private readonly IDeviceService _devices;
    private readonly ITagService _tags;
    private readonly IDeviceAddressService _addresses;
    private readonly IExternalQueryService _queries;
    private readonly ITriggerService _triggers;
    private readonly IDeviceServerService _servers;
    private readonly IPlcOperationsService _plcOps;
    private readonly ICurrentUserService _currentUser;
    private readonly NavigationContext _navContext;
    private readonly INavigationService _navigation;
    private readonly Func<TagEditViewModel> _tagEditFactory;
    private readonly Func<AddressEditViewModel> _addressEditFactory;
    private readonly Func<QueryEditViewModel> _queryEditFactory;
    private readonly Func<TransferServerEditViewModel> _serverEditFactory;

    public DeviceConfigViewModel(IDeviceService devices, ITagService tags, IDeviceAddressService addresses, IExternalQueryService queries, ITriggerService triggers,
        IDeviceServerService servers, IPlcOperationsService plcOps, ICurrentUserService currentUser, NavigationContext navContext, INavigationService navigation,
        Func<TagEditViewModel> tagEditFactory, Func<AddressEditViewModel> addressEditFactory, Func<QueryEditViewModel> queryEditFactory, Func<TransferServerEditViewModel> serverEditFactory,
        IDialogService dialogs, ILogger<DeviceConfigViewModel> logger) : base(dialogs, logger)
    {
        _devices = devices; _tags = tags; _addresses = addresses; _queries = queries; _triggers = triggers; _servers = servers; _plcOps = plcOps;
        _currentUser = currentUser; _navContext = navContext; _navigation = navigation;
        _tagEditFactory = tagEditFactory; _addressEditFactory = addressEditFactory; _queryEditFactory = queryEditFactory; _serverEditFactory = serverEditFactory;
    }

    // ---- dialogs raised to the view ----
    public event EventHandler<TagEditViewModel>? TagEditRequested;
    public event EventHandler<AddressEditViewModel>? AddressEditRequested;
    public event EventHandler<QueryEditViewModel>? QueryEditRequested;
    public event EventHandler<TransferServerEditViewModel>? ServerEditRequested;

    [ObservableProperty] private Device? _device;
    [ObservableProperty] private string _title = "Device configuration";
    [ObservableProperty] private bool _isModbus;
    [ObservableProperty] private bool _canBrowse;

    public bool CanManage => _currentUser.HasPermission(Permission.ManageTags);
    public bool CanManageDevice => _currentUser.HasPermission(Permission.ManageDevices);
    public bool CanManageTransfer => _currentUser.HasPermission(Permission.ManageTransfer);

    // Tags
    public ObservableCollection<DeviceTag> PlcTags { get; } = new();
    public ObservableCollection<DeviceTag> CustomTags { get; } = new();
    public ObservableCollection<DeviceTag> ExternalTags { get; } = new();
    [ObservableProperty] private DeviceTag? _selectedPlcTag;
    [ObservableProperty] private string _tagFilter = string.Empty;
    private List<DeviceTag> _allPlcTags = new();

    // Address blocks
    public ObservableCollection<DeviceAddress> AddressBlocks { get; } = new();
    [ObservableProperty] private DeviceAddress? _selectedBlock;

    // External queries
    public ObservableCollection<DeviceQuery> Queries { get; } = new();

    // Trigger
    public IReadOnlyList<CaptureMode> CaptureModes { get; } = Enum.GetValues<CaptureMode>();
    public IReadOnlyList<TriggerOperator> Operators { get; } = Enum.GetValues<TriggerOperator>();
    public IReadOnlyList<LogicalOperator> Conjunctions { get; } = [LogicalOperator.And, LogicalOperator.Or];
    public ObservableCollection<DeviceTriggerCondition> Conditions { get; } = new();
    public ObservableCollection<string> AllTagNames { get; } = new();
    public ObservableCollection<string> BoolPlcTagNames { get; } = new();
    [ObservableProperty] private CaptureMode _captureMode = CaptureMode.Manual;
    [ObservableProperty] private string? _triggerTagName;
    [ObservableProperty] private string _expressionText = string.Empty;
    [ObservableProperty] private LogicalOperator _newConjunction = LogicalOperator.And;
    [ObservableProperty] private string? _newConditionTag;
    [ObservableProperty] private TriggerOperator _newOperator = TriggerOperator.Equal;
    [ObservableProperty] private string _newValue = string.Empty;
    public bool ShowPlcTrigger => CaptureMode == CaptureMode.PlcTrigger;
    public bool ShowLogical => CaptureMode == CaptureMode.Logical;
    public bool NewValueEnabled => NewOperator != TriggerOperator.OnChange;
    public bool HasConditions => Conditions.Count > 0;

    // Transfer
    [ObservableProperty] private DeviceServer? _server;

    partial void OnCaptureModeChanged(CaptureMode value) { OnPropertyChanged(nameof(ShowPlcTrigger)); OnPropertyChanged(nameof(ShowLogical)); }
    partial void OnNewOperatorChanged(TriggerOperator value) => OnPropertyChanged(nameof(NewValueEnabled));
    partial void OnTagFilterChanged(string value) => ApplyTagFilter();

    public override async Task OnNavigatedToAsync()
    {
        if (_navContext.SelectedDeviceId is null)
        {
            _navigation.Navigate(typeof(Views.Pages.DevicesPage));
            return;
        }
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        await RunAsync(async () =>
        {
            var id = _navContext.SelectedDeviceId!.Value;
            Device = await _devices.GetByIdAsync(id, includeChildren: false) ?? throw new Domain.Exceptions.NotFoundException("Device", id);
            var info = PlcModelCatalog.Get(Device.Model);
            Title = $"{Device.Name} — {info.DisplayName} @ {Device.Host}";
            IsModbus = Device.Protocol == ProtocolType.ModbusTcp;
            CanBrowse = info.SupportsBrowse;
            CaptureMode = Device.CaptureMode;
            TriggerTagName = Device.TriggerTagName;

            await LoadTagsAsync();
            if (IsModbus) await LoadBlocksAsync();
            await LoadQueriesAsync();
            await LoadConditionsAsync();
            Server = await _servers.GetForDeviceAsync(id);
        });
    }

    private async Task LoadTagsAsync()
    {
        var all = await _tags.GetForDeviceAsync(Device!.Id);
        _allPlcTags = all.Where(t => t.Kind == TagKind.Plc).ToList();
        ApplyTagFilter();
        CustomTags.Clear(); foreach (var t in all.Where(t => t.Kind == TagKind.Custom)) CustomTags.Add(t);
        ExternalTags.Clear(); foreach (var t in all.Where(t => t.Kind == TagKind.External)) ExternalTags.Add(t);
        AllTagNames.Clear(); foreach (var n in all.Select(t => t.Name).OrderBy(n => n)) AllTagNames.Add(n);
        BoolPlcTagNames.Clear(); foreach (var n in _allPlcTags.OrderBy(t => t.Name).Select(t => t.Name)) BoolPlcTagNames.Add(n);
    }

    private void ApplyTagFilter()
    {
        var q = string.IsNullOrWhiteSpace(TagFilter) ? _allPlcTags
            : _allPlcTags.Where(t => t.Name.Contains(TagFilter, StringComparison.OrdinalIgnoreCase) || t.PlcAddress.Contains(TagFilter, StringComparison.OrdinalIgnoreCase) || (t.Alias?.Contains(TagFilter, StringComparison.OrdinalIgnoreCase) ?? false));
        PlcTags.Clear(); foreach (var t in q) PlcTags.Add(t);
    }

    private async Task LoadBlocksAsync()
    {
        AddressBlocks.Clear();
        foreach (var b in await _addresses.GetForDeviceAsync(Device!.Id)) AddressBlocks.Add(b);
    }

    private async Task LoadQueriesAsync()
    {
        Queries.Clear();
        foreach (var q in await _queries.GetForDeviceAsync(Device!.Id)) Queries.Add(q);
    }

    private async Task LoadConditionsAsync()
    {
        Conditions.Clear();
        foreach (var c in await _triggers.GetForDeviceAsync(Device!.Id)) Conditions.Add(c);
        ExpressionText = Conditions.Count == 0 ? "(no conditions yet)" : TriggerEvaluator.Describe(Conditions);
        OnPropertyChanged(nameof(HasConditions));
    }

    // ------------------------------------------------------------------ tags

    [RelayCommand]
    private void AddTag(string? kind)
    {
        var k = Enum.TryParse<TagKind>(kind, out var parsed) ? parsed : TagKind.Plc;
        var vm = _tagEditFactory();
        vm.Initialize(Device!, null, k, AddressBlocks.ToList(), Queries.ToList());
        vm.Saved += async (_, _) => await RunAsync(LoadTagsAsync);
        TagEditRequested?.Invoke(this, vm);
    }

    [RelayCommand]
    private void EditTag(DeviceTag? tag)
    {
        if (tag is null) return;
        var vm = _tagEditFactory();
        vm.Initialize(Device!, tag, tag.Kind, AddressBlocks.ToList(), Queries.ToList());
        vm.Saved += async (_, _) => await RunAsync(LoadTagsAsync);
        TagEditRequested?.Invoke(this, vm);
    }

    [RelayCommand]
    private async Task DeleteTagAsync(DeviceTag? tag)
    {
        if (tag is null) return;
        if (!await Dialogs.ConfirmAsync("Delete tag", $"Delete tag '{tag.Name}'?", "Delete", destructive: true)) return;
        if (await RunAsync(() => _tags.DeleteAsync(tag.Id), "Cannot delete tag")) await RunAsync(LoadTagsAsync);
    }

    [RelayCommand]
    private async Task ToggleSelectedAsync(DeviceTag? tag)
    {
        if (tag is null) return;
        await RunAsync(() => _tags.SetSelectedAsync([tag.Id], !tag.IsSelected));
        await RunAsync(LoadTagsAsync);
    }

    [RelayCommand]
    private async Task SelectAllAsync(string? select)
    {
        var on = select == "true";
        await RunAsync(() => _tags.SetSelectedAsync(PlcTags.Select(t => t.Id), on));
        await RunAsync(LoadTagsAsync);
    }

    [RelayCommand]
    private async Task BrowseTagsAsync()
    {
        await RunAsync(async () =>
        {
            var discovered = await _plcOps.BrowseTagsAsync(Device!);
            if (discovered.Count == 0) { Dialogs.Toast("Browse", "The controller returned no tags.", ToastKind.Warning); return; }
            var import = await Dialogs.ConfirmAsync("Import tags", $"{discovered.Count} tags found on the controller.\n\nImport the ones that do not exist yet? New tags will be added as selected.", "Import");
            if (!import) return;
            var added = await _tags.ImportDiscoveredAsync(Device!.Id, discovered, selectNew: true);
            Dialogs.Toast("Browse", added == 0 ? "All discovered tags already exist." : $"{added} tag(s) imported.", ToastKind.Success);
            await LoadTagsAsync();
        }, "Browse failed");
    }

    [RelayCommand]
    private async Task SetCustomValueAsync(DeviceTag? tag)
    {
        if (tag is null) return;
        var vm = _tagEditFactory();
        vm.Initialize(Device!, tag, TagKind.Custom, AddressBlocks.ToList(), Queries.ToList());
        vm.Saved += async (_, _) => await RunAsync(LoadTagsAsync);
        TagEditRequested?.Invoke(this, vm);
        await Task.CompletedTask;
    }

    // ------------------------------------------------------------------ address blocks

    [RelayCommand]
    private void AddBlock()
    {
        var vm = _addressEditFactory();
        vm.Initialize(Device!, null);
        vm.Saved += async (_, _) => { await RunAsync(LoadBlocksAsync); await RunAsync(LoadTagsAsync); };
        AddressEditRequested?.Invoke(this, vm);
    }

    [RelayCommand]
    private void EditBlock(DeviceAddress? block)
    {
        block ??= SelectedBlock;
        if (block is null) return;
        var vm = _addressEditFactory();
        vm.Initialize(Device!, block);
        vm.Saved += async (_, _) => { await RunAsync(LoadBlocksAsync); await RunAsync(LoadTagsAsync); };
        AddressEditRequested?.Invoke(this, vm);
    }

    [RelayCommand]
    private async Task DeleteBlockAsync(DeviceAddress? block)
    {
        block ??= SelectedBlock;
        if (block is null) return;
        if (!await Dialogs.ConfirmAsync("Delete address block", "Delete this block and all tags generated from it?", "Delete", destructive: true)) return;
        if (await RunAsync(() => _addresses.DeleteAsync(block.Id))) { await RunAsync(LoadBlocksAsync); await RunAsync(LoadTagsAsync); }
    }

    [RelayCommand]
    private async Task RegenerateBlockAsync(DeviceAddress? block)
    {
        block ??= SelectedBlock;
        if (block is null) return;
        if (!await Dialogs.ConfirmAsync("Regenerate tags", $"Replace the tags of block '{block.TagPrefix}' with {block.PointCount} freshly generated tags? Aliases and scaling on existing tags will be lost.", "Regenerate")) return;
        await RunAsync(async () =>
        {
            var n = await _addresses.GenerateTagsAsync(block.Id);
            Dialogs.Toast("Address block", $"{n} tags generated.", ToastKind.Success);
            await LoadTagsAsync();
        });
    }

    // ------------------------------------------------------------------ external queries

    [RelayCommand]
    private void AddQuery()
    {
        var vm = _queryEditFactory();
        vm.Initialize(Device!, null);
        vm.Saved += async (_, _) => await RunAsync(LoadQueriesAsync);
        QueryEditRequested?.Invoke(this, vm);
    }

    [RelayCommand]
    private void EditQuery(DeviceQuery? query)
    {
        if (query is null) return;
        var vm = _queryEditFactory();
        vm.Initialize(Device!, query);
        vm.Saved += async (_, _) => await RunAsync(LoadQueriesAsync);
        QueryEditRequested?.Invoke(this, vm);
    }

    [RelayCommand]
    private async Task VerifyQueryAsync(DeviceQuery? query)
    {
        if (query is null) return;
        await RunAsync(async () =>
        {
            var r = await _queries.VerifyAsync(query.Id);
            if (r.Succeeded)
            {
                var sample = string.Join("\n", r.SampleRow.Select(kv => $"  {kv.Key} = {kv.Value ?? "NULL"}"));
                await Dialogs.ShowInfoAsync("Query verified", $"{r.Columns.Count} column(s) discovered:\n{sample}");
            }
            else await Dialogs.ShowErrorAsync("Query failed", r.Error ?? "Unknown error");
            await LoadQueriesAsync();
        });
    }

    [RelayCommand]
    private async Task DeleteQueryAsync(DeviceQuery? query)
    {
        if (query is null) return;
        if (!await Dialogs.ConfirmAsync("Delete query", $"Delete external query '{query.Name}'?", "Delete", destructive: true)) return;
        if (await RunAsync(() => _queries.DeleteAsync(query.Id), "Cannot delete query")) await RunAsync(LoadQueriesAsync);
    }

    [RelayCommand]
    private async Task RefreshExternalNowAsync()
    {
        await RunAsync(async () =>
        {
            var ok = await _queries.RefreshExternalTagValuesAsync(Device!.Id);
            Dialogs.Toast("External tags", ok ? "External values refreshed." : "Some queries failed — see query status.", ok ? ToastKind.Success : ToastKind.Warning);
            await LoadTagsAsync();
            await LoadQueriesAsync();
        });
    }

    // ------------------------------------------------------------------ trigger

    [RelayCommand]
    private async Task SaveCaptureModeAsync()
    {
        var ok = await RunAsync(() => _devices.SetCaptureModeAsync(Device!.Id, CaptureMode, TriggerTagName), "Cannot save capture mode");
        if (ok) Dialogs.Toast("Capture mode", $"Mode set to {CaptureMode}.", ToastKind.Success);
    }

    [RelayCommand]
    private async Task AddConditionAsync()
    {
        if (string.IsNullOrWhiteSpace(NewConditionTag)) { ErrorMessage = "Select a tag for the condition."; return; }
        var ok = await RunAsync(async () =>
        {
            await _triggers.AddAsync(new DeviceTriggerCondition
            {
                DeviceId = Device!.Id, Conjunction = NewConjunction, TagName = NewConditionTag!, Operator = NewOperator, Value = NewValue,
            });
            NewValue = string.Empty;
            await LoadConditionsAsync();
        }, inlineErrors: true);
        _ = ok;
    }

    [RelayCommand]
    private async Task DeleteConditionAsync(DeviceTriggerCondition? c)
    {
        if (c is null) return;
        if (await RunAsync(() => _triggers.DeleteAsync(c.Id))) await RunAsync(LoadConditionsAsync);
    }

    // ------------------------------------------------------------------ transfer server

    [RelayCommand]
    private void EditServer()
    {
        var vm = _serverEditFactory();
        vm.Initialize(Device!, Server);
        vm.Saved += async (_, _) => await RunAsync(async () => Server = await _servers.GetForDeviceAsync(Device!.Id));
        ServerEditRequested?.Invoke(this, vm);
    }

    [RelayCommand]
    private async Task ToggleServerEnabledAsync()
    {
        if (Server is null) return;
        await RunAsync(async () =>
        {
            await _servers.SetEnabledAsync(Device!.Id, !Server.IsEnabled);
            Server = await _servers.GetForDeviceAsync(Device!.Id);
        });
    }

    [RelayCommand]
    private async Task DeleteServerAsync()
    {
        if (Server is null) return;
        if (!await Dialogs.ConfirmAsync("Remove transfer target", "Remove the central server configuration for this device? Buffered readings will stay local.", "Remove", destructive: true)) return;
        await RunAsync(async () => { await _servers.DeleteAsync(Device!.Id); Server = null; });
    }

    [RelayCommand]
    private void Back() => _navigation.Navigate(typeof(Views.Pages.DevicesPage));

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();
}
