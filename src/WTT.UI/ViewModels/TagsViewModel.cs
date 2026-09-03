using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WTT.Application.Interfaces;
using WTT.Domain.Entities;
using WTT.UI.Views.Windows;

namespace WTT.UI.ViewModels;

public class TagDisplayItem : ObservableObject
{
    public DeviceTag Tag { get; set; } = null!;

    private string _liveValue = "--";
    public string LiveValue
    {
        get => _liveValue;
        set => SetProperty(ref _liveValue, value);
    }

    private string _quality = "Unknown";
    public string Quality
    {
        get => _quality;
        set => SetProperty(ref _quality, value);
    }

    private string _lastUpdated = "Never";
    public string LastUpdated
    {
        get => _lastUpdated;
        set => SetProperty(ref _lastUpdated, value);
    }
}

public partial class TagsViewModel : ObservableObject
{
    private readonly ITagService _tagService;
    private readonly IDeviceService _deviceService;
    private readonly IDataCaptureService _dataCaptureService;

    [ObservableProperty]
    private ObservableCollection<PlcDevice> _devices = new();

    [ObservableProperty]
    private PlcDevice? _selectedDevice;

    [ObservableProperty]
    private ObservableCollection<TagDisplayItem> _tags = new();

    [ObservableProperty]
    private TagDisplayItem? _selectedTag;

    [ObservableProperty]
    private bool _isLoading;

    public TagsViewModel(ITagService tagService, IDeviceService deviceService, IDataCaptureService dataCaptureService)
    {
        _tagService = tagService;
        _deviceService = deviceService;
        _dataCaptureService = dataCaptureService;

        _dataCaptureService.TagValueChanged += OnTagValueChanged;
    }

    private void OnTagValueChanged(object? sender, TagValueChangedEventArgs e)
    {
        System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
        {
            var item = Tags.FirstOrDefault(t => t.Tag.Id == e.TagId);
            if (item != null)
            {
                item.LiveValue = e.Value;
                item.Quality = e.Quality;
                item.LastUpdated = e.Timestamp.ToLocalTime().ToString("HH:mm:ss");
            }
        });
    }

    public async Task InitializeAsync()
    {
        await LoadDevicesAsync();
    }

    [RelayCommand]
    private async Task LoadDevicesAsync()
    {
        IsLoading = true;
        try
        {
            var list = await _deviceService.GetAllDevicesAsync();
            Devices = new ObservableCollection<PlcDevice>(list);
            if (Devices.Count > 0 && SelectedDevice == null)
            {
                SelectedDevice = Devices[0];
            }
        }
        catch { }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnSelectedDeviceChanged(PlcDevice? value)
    {
        if (value != null)
        {
            _ = LoadTagsAsync();
        }
    }

    [RelayCommand]
    private async Task LoadTagsAsync()
    {
        if (SelectedDevice == null) return;

        IsLoading = true;
        try
        {
            var list = await _tagService.GetTagsByDeviceIdAsync(SelectedDevice.Id);
            var latest = await _dataCaptureService.GetLatestValuesAsync();

            var displayList = list.Select(t => new TagDisplayItem
            {
                Tag = t,
                LiveValue = latest.TryGetValue(t.Id, out var v) ? v : "--",
                Quality = latest.ContainsKey(t.Id) ? "Good" : "Unknown"
            });

            Tags = new ObservableCollection<TagDisplayItem>(displayList);
        }
        catch { }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task AddTagAsync()
    {
        if (SelectedDevice == null)
        {
            // If device list was empty, try loading devices first
            await LoadDevicesAsync();
            
            if (SelectedDevice == null)
            {
                MessageBox.Show("No PLC Device found! Please go to the 'Devices' page and add a device first.", 
                    "Device Required", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }

        var newTag = new DeviceTag
        {
            DeviceId = SelectedDevice.Id,
            DataType = "Float",
            IsActive = true
        };

        var vm = new TagEditViewModel(_tagService, newTag, isNew: true);
        var win = new TagEditWindow(vm);

        win.ShowDialog();
        await LoadTagsAsync();
    }

    [RelayCommand]
    private async Task EditTagAsync(TagDisplayItem? item)
    {
        if (item == null) return;

        var vm = new TagEditViewModel(_tagService, item.Tag, isNew: false);
        var win = new TagEditWindow(vm);

        win.ShowDialog();
        await LoadTagsAsync();
    }

    [RelayCommand]
    private async Task DeleteTagAsync(TagDisplayItem? item)
    {
        if (item == null) return;

        var result = MessageBox.Show($"Are you sure you want to delete tag '{item.Tag.TagName}'?", 
            "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            await _tagService.DeleteTagAsync(item.Tag.Id);
            await LoadTagsAsync();
        }
    }
}
