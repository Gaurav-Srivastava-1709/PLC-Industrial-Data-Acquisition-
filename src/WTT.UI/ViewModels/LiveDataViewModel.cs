using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WTT.Application.Interfaces;

namespace WTT.UI.ViewModels;

public class LiveTelemetryCard : ObservableObject
{
    public int TagId { get; set; }
    public string TagName { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;

    private string _value = "--";
    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }

    private string _quality = "Unknown";
    public string Quality
    {
        get => _quality;
        set => SetProperty(ref _quality, value);
    }

    private string _timestamp = "Waiting...";
    public string Timestamp
    {
        get => _timestamp;
        set => SetProperty(ref _timestamp, value);
    }
}

public partial class LiveDataViewModel : ObservableObject
{
    private readonly ITagService _tagService;
    private readonly IDataCaptureService _dataCaptureService;

    [ObservableProperty]
    private ObservableCollection<LiveTelemetryCard> _cards = new();

    [ObservableProperty]
    private bool _isServiceRunning = true;

    [ObservableProperty]
    private int _pollingInterval = 10;

    public LiveDataViewModel(ITagService tagService, IDataCaptureService dataCaptureService)
    {
        _tagService = tagService;
        _dataCaptureService = dataCaptureService;

        _isServiceRunning = _dataCaptureService.IsRunning;
        _pollingInterval = _dataCaptureService.PollingIntervalSeconds;

        _dataCaptureService.TagValueChanged += OnTagValueChanged;
        _dataCaptureService.ServiceStatusChanged += (_, running) => IsServiceRunning = running;
    }

    private void OnTagValueChanged(object? sender, TagValueChangedEventArgs e)
    {
        System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
        {
            var card = Cards.FirstOrDefault(c => c.TagId == e.TagId);
            if (card != null)
            {
                card.Value = e.Value;
                card.Quality = e.Quality;
                card.Timestamp = e.Timestamp.ToLocalTime().ToString("HH:mm:ss");
            }
        });
    }

    public async Task InitializeAsync()
    {
        var tags = await _tagService.GetAllTagsAsync();
        var latest = await _dataCaptureService.GetLatestValuesAsync();

        var list = tags.Where(t => t.IsActive).Select(t => new LiveTelemetryCard
        {
            TagId = t.Id,
            TagName = t.TagName,
            DeviceName = t.Device?.Name ?? "PLC",
            Address = t.Address,
            DataType = t.DataType,
            Value = latest.TryGetValue(t.Id, out var v) ? v : "--",
            Quality = latest.ContainsKey(t.Id) ? "Good" : "Unknown",
            Timestamp = DateTime.Now.ToString("HH:mm:ss")
        });

        Cards = new ObservableCollection<LiveTelemetryCard>(list);
    }

    [RelayCommand]
    private async Task ToggleServiceAsync()
    {
        if (IsServiceRunning)
        {
            await _dataCaptureService.StopAsync();
        }
        else
        {
            await _dataCaptureService.StartAsync();
        }
        IsServiceRunning = _dataCaptureService.IsRunning;
    }
}
