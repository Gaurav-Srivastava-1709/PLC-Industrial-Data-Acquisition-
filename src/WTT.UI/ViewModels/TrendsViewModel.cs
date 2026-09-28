using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using WTT.Application.Interfaces;
using WTT.Domain.Entities;
using WTT.UI.Controls;
using WTT.UI.Services;

namespace WTT.UI.ViewModels;

/// <summary>
/// Tag selection checkbox item (which tags the operator wants to view).
/// </summary>
public partial class TagTrendItem : ObservableObject
{
    public DeviceTag Tag { get; }
    public string Name => Tag.Name;
    public string DisplayName => string.IsNullOrWhiteSpace(Tag.Alias) ? Tag.Name : $"{Tag.Alias} ({Tag.Name})";
    public string? Unit => Tag.EngineeringUnit;

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private Color _color;
    [ObservableProperty] private SolidColorBrush _colorBrush;
    [ObservableProperty] private string _latestValue = "—";

    public TagTrendItem(DeviceTag tag, Color color)
    {
        Tag = tag;
        _color = color;
        _colorBrush = new SolidColorBrush(color);
        _colorBrush.Freeze();
    }
}

/// <summary>
/// Industrial SCADA Historian Trends ViewModel:
/// Multi-device, multi-pen real-time and historical tag process data.
/// </summary>
public partial class TrendsViewModel : ViewModelBase
{
    private readonly IDeviceService _devices;
    private readonly ITagService _tags;
    private readonly IHistorianService _historian;
    private readonly NavigationContext _navContext;
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    // Standard Industrial SCADA Neon Color Palette (10 distinct pens)
    private static readonly Color[] PenPalette = new[]
    {
        Color.FromRgb(0, 229, 255),   // Cyan
        Color.FromRgb(255, 179, 0),   // Amber
        Color.FromRgb(0, 230, 118),   // Emerald Green
        Color.FromRgb(255, 64, 129),  // Magenta / Hot Pink
        Color.FromRgb(179, 136, 255), // Purple
        Color.FromRgb(238, 255, 65),  // Lime Yellow
        Color.FromRgb(255, 110, 64),  // Coral Orange
        Color.FromRgb(64, 196, 255),  // Sky Blue
        Color.FromRgb(255, 128, 171), // Soft Pink
        Color.FromRgb(29, 233, 182)   // Teal
    };

    public TrendsViewModel(
        IDeviceService devices,
        ITagService tags,
        IHistorianService historian,
        NavigationContext navContext,
        IDialogService dialogs,
        ILogger<TrendsViewModel> logger) : base(dialogs, logger)
    {
        _devices = devices;
        _tags = tags;
        _historian = historian;
        _navContext = navContext;

        _pollTimer.Tick += async (_, _) => await OnLivePollAsync();
    }

    public ObservableCollection<Device> Devices { get; } = new();
    public ObservableCollection<TagTrendItem> AllTags { get; } = new();
    public ObservableCollection<TagTrendItem> FilteredTags { get; } = new();
    public ObservableCollection<PenSeriesModel> ActiveSeries { get; } = new();

    [ObservableProperty] private Device? _selectedDevice;
    [ObservableProperty] private string _filter = string.Empty;
    [ObservableProperty] private TimeSpan _timeWindow = TimeSpan.FromMinutes(5);
    [ObservableProperty] private string _timeWindowLabel = "5 Min";
    [ObservableProperty] private bool _isLive = true;
    [ObservableProperty] private string _liveStatusText = "Live Streaming";
    [ObservableProperty] private int _activePenCount;
    [ObservableProperty] private long _trendRevision;
    [ObservableProperty] private TrendYScaleMode _yScaleMode = TrendYScaleMode.Shared;
    [ObservableProperty] private string _yModeLabel = "Shared Y";
    [ObservableProperty] private string? _unitsWarning;
    [ObservableProperty] private bool _hasMixedUnits;
    private int _pollBusy;
    private int _historyBusy;
    private int _historyPending;
    // Value older than this means the device/capture has effectively stopped:
    // draw an honest gap instead of a fabricated flat "live" line. Normal Flush
    // lag is only ~5s, so 60s is safely beyond it.
    private static readonly TimeSpan StaleGapThreshold = TimeSpan.FromSeconds(60);

    partial void OnSelectedDeviceChanged(Device? value)
    {
        if (value is not null)
        {
            _navContext.SelectedDeviceId = value.Id;
            _ = RunAsync(LoadDeviceTagsAsync);
        }
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    public override async Task OnNavigatedToAsync()
    {
        // BUG FIX: load devices inside RunAsync, set SelectedDevice OUTSIDE.
        // Both were inside before, so the inner RunAsync(LoadDeviceTagsAsync) was
        // skipped while busy — the tag list stayed empty on first visit.
        Device? target = null;
        await RunAsync(async () =>
        {
            Devices.Clear();
            var list = await _devices.GetAllAsync();
            foreach (var d in list) Devices.Add(d);
            target = Devices.FirstOrDefault(d => d.Id == _navContext.SelectedDeviceId)
                     ?? Devices.FirstOrDefault();
        });
        if (target is not null)
        {
            if (!Equals(SelectedDevice, target))
                SelectedDevice = target;
            else
                await RunAsync(LoadDeviceTagsAsync);
        }
        _manualToastShown = false;
        _pollTimer.Start();
    }

    public override Task OnNavigatedFromAsync()
    {
        _pollTimer.Stop();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Load all tags when the device changes.
    /// Other devices' pens stay untouched (multi-device trend);
    /// the previous selection is restored when returning.
    /// </summary>
    private bool _bulkUpdate;
    private async Task LoadDeviceTagsAsync()
    {
        if (SelectedDevice is null)
        {
            AllTags.Clear();
            FilteredTags.Clear();
            return;
        }

        var tags = await _tags.GetForDeviceAsync(SelectedDevice.Id);
        _bulkUpdate = true;
        try
        {
            AllTags.Clear();

            int colorIdx = 0;
            foreach (var t in tags.Where(t => t.IsSelected).OrderBy(t => t.Kind).ThenBy(t => t.Name))
            {
                var color = PenPalette[colorIdx % PenPalette.Length];
                var item = new TagTrendItem(t, color);
                item.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(TagTrendItem.IsSelected))
                    {
                        OnTagSelectionChanged(item);
                    }
                };
                AllTags.Add(item);
                // Restore the previous visit's selection (series already in ActiveSeries)
                if (ActiveSeries.Any(s => s.DeviceId == t.DeviceId && string.Equals(s.TagName, t.Name, StringComparison.OrdinalIgnoreCase)))
                    item.IsSelected = true;
                colorIdx++;
            }

            ApplyFilter();

            // Industry standard: auto-select removed.
            // The operator chooses what to view.
            if (!ActiveSeries.Any(s => s.DeviceId == SelectedDevice.Id))
            {
                _ = ReloadHistoryAsync();
            }
        }
        finally { _bulkUpdate = false; }
    }

    private void ApplyFilter()
    {
        FilteredTags.Clear();
        var q = string.IsNullOrWhiteSpace(Filter)
            ? AllTags
            : AllTags.Where(t => t.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase)
                              || (t.Tag.Alias != null && t.Tag.Alias.Contains(Filter, StringComparison.OrdinalIgnoreCase)));

        foreach (var item in q) FilteredTags.Add(item);
    }

    private void OnTagSelectionChanged(TagTrendItem item)
    {
        if (item.IsSelected)
        {
            // String tags (LineName/CurrentBatch) cannot form a line — they are text context.
            // Making them pens corrupts both pen count and Y-scale.
            if (item.Tag.DataType == WTT.Domain.Enums.TagDataType.String)
            {
                item.IsSelected = false;
                Dialogs.Toast("Trends", $"'{item.Name}' is a text value — it cannot be plotted as a line. See the value in the left list.", ToastKind.Info);
                return;
            }
            // Production guard: too many pens kills readability and render perf.
            const int MaxActivePens = 12;
            if (ActiveSeries.Count >= MaxActivePens
                && !ActiveSeries.Any(s => s.DeviceId == item.Tag.DeviceId && string.Equals(s.TagName, item.Name, StringComparison.OrdinalIgnoreCase)))
            {
                item.IsSelected = false;
                Dialogs.Toast("Trends", $"Max {MaxActivePens} pens at once — remove one first.", ToastKind.Warning);
                return;
            }
            // Add the series if not already present (identity = device + tag, so same-named tags on two devices become separate pens)
            if (!ActiveSeries.Any(s => s.DeviceId == item.Tag.DeviceId && string.Equals(s.TagName, item.Name, StringComparison.OrdinalIgnoreCase)))
            {
                // Do not reuse a color already in use (causes confusion in multi-device view)
                var color = NextFreeColor();
                item.Color = color;
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                item.ColorBrush = brush;
                var series = new PenSeriesModel
                {
                    DeviceId = item.Tag.DeviceId,
                    DeviceName = Devices.FirstOrDefault(d => d.Id == item.Tag.DeviceId)?.Name ?? SelectedDevice?.Name ?? string.Empty,
                    TagName = item.Name,
                    EngineeringUnit = item.Unit,
                    Color = color,
                    StrokeBrush = brush,
                    LinePen = new Pen(brush, 2.0),
                    MinAlarm = item.Tag.AlarmMinThreshold,
                    MaxAlarm = item.Tag.AlarmMaxThreshold,
                    DataType = item.Tag.DataType,
                    IsVisible = true
                };
                series.LinePen.Freeze();
                ActiveSeries.Add(series);
            }
        }
        else
        {
            var existing = ActiveSeries.FirstOrDefault(s => s.DeviceId == item.Tag.DeviceId && string.Equals(s.TagName, item.Name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                ActiveSeries.Remove(existing);
            }
        }

        ActivePenCount = ActiveSeries.Count;
        BumpRevision();
        // Backfill is only needed on pen ADD; repaint is enough on remove (saves an extra DB round-trip).
        // On bulk restore (device switch) the caller reloads — skip here so N reloads do not fire.
        if (item.IsSelected && !_bulkUpdate) _ = ReloadHistoryAsync();
        UpdateUnitsState();
    }

    private Color NextFreeColor()
    {
        var used = ActiveSeries.Select(s => s.Color).ToHashSet();
        foreach (var c in PenPalette)
            if (!used.Contains(c)) return c;
        return PenPalette[ActiveSeries.Count % PenPalette.Length];
    }

    // History backfill with its own guard: unlike RunAsync it never silently
    // skips when busy — checkbox toggles queue one trailing reload instead.
    private async Task ReloadHistoryAsync()
    {
        if (System.Threading.Interlocked.Exchange(ref _historyBusy, 1) == 1)
        {
            System.Threading.Interlocked.Exchange(ref _historyPending, 1);
            return;
        }
        try
        {
            do
            {
                System.Threading.Interlocked.Exchange(ref _historyPending, 0);
                try { await LoadHistoricalWindowAsync(); }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Trend history reload failed.");
                    Dialogs.Toast("Trends", "Could not load history: " + ex.Message, ToastKind.Error);
                }
                BumpRevision();
            } while (System.Threading.Interlocked.CompareExchange(ref _historyPending, 0, 0) == 1);
        }
        finally { System.Threading.Interlocked.Exchange(ref _historyBusy, 0); }
    }

    private void BumpRevision() => OnUi(() => TrendRevision++);

    /// <summary>
    /// Load data for the selected time window from the database (separate query per device).
    /// </summary>
    private async Task LoadHistoricalWindowAsync()
    {
        // Remove legacy string pens (LineName/CurrentBatch) — they never form lines.
        for (var i = ActiveSeries.Count - 1; i >= 0; i--)
        {
            var s = ActiveSeries[i];
            if (s.DataType == WTT.Domain.Enums.TagDataType.String && s.Points.Count == 0)
            {
                ActiveSeries.RemoveAt(i);
                var ti = AllTags.FirstOrDefault(t => t.Tag.DeviceId == s.DeviceId && string.Equals(t.Name, s.TagName, StringComparison.OrdinalIgnoreCase));
                if (ti != null && ti.IsSelected) ti.IsSelected = false;
            }
        }
        if (ActiveSeries.Count == 0) return;

        var toUtc = DateTime.UtcNow;
        var fromUtc = toUtc - TimeWindow;

        foreach (var g in ActiveSeries.GroupBy(s => s.DeviceId).ToList())
        {
            List<TagTrendSeries> trends;
            try
            {
                trends = (await _historian.GetTrendsAsync(g.Key, g.Select(s => s.TagName), fromUtc, toUtc, maxPoints: 400)).ToList();
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Trend history load failed for device {DeviceId}.", g.Key);
                throw;
            }

            foreach (var t in trends)
            {
                var series = ActiveSeries.FirstOrDefault(s => s.DeviceId == g.Key && string.Equals(s.TagName, t.TagName, StringComparison.OrdinalIgnoreCase));
                if (series != null)
                {
                    series.DataType = t.DataType;
                    series.Points = t.Points.Select(p => new TrendPoint(p.TimestampUtc, p.Value, p.Quality)).ToList();
                }

                // Update the latest value in the tag list (only found in the current device list)
                var tagItem = AllTags.FirstOrDefault(ti => ti.Tag.DeviceId == g.Key && string.Equals(ti.Name, t.TagName, StringComparison.OrdinalIgnoreCase));
                if (tagItem != null && t.Points.Count > 0)
                {
                    var last = t.Points[^1];
                    tagItem.LatestValue = $"{last.Value:F2} {tagItem.Unit}";
                }
            }
        }

        ActivePenCount = ActiveSeries.Count;
        OnUi(UpdateUnitsState);
    }

    /// <summary>
    /// Add live points every 1 second (smooth real-time scrolling).
    /// Values arrive from EVERY device with active pens — not just the selected device.
    /// Industry fix: use the real PLC timestamp (CurrentValueAtUtc), not UI Now.
    /// Skip duplicate timestamps so points do not pile up on a flat line.
    /// </summary>
    private async Task OnLivePollAsync()
    {
        if (!IsLive || ActiveSeries.Count == 0) return;
        if (System.Threading.Interlocked.Exchange(ref _pollBusy, 1) == 1) return;
        try
        {
            var now = DateTime.UtcNow;
            // In manual pan/zoom view the plot shows an old range; if the buffer
            // is kept only to TimeWindow+2min, the frozen view's data gets trimmed and
            // it looks like the graph is erasing itself. So keep at least a 15-min margin
            // even on small windows so panning back still shows data.
            // (Reset View / double-click returns to live-follow.)
            var retainMargin = TimeSpan.FromMinutes(Math.Max(15, TimeWindow.TotalMinutes + 2));
            var cutoff = now - retainMargin;

            // One batch query per active device (not N queries for N tags)
            foreach (var devId in ActiveSeries.Select(s => s.DeviceId).Distinct().ToList())
            {
                List<DeviceTag> tags;
                try { tags = (await _tags.GetForDeviceAsync(devId)).ToList(); }
                catch { continue; }
                var byName = tags.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
                foreach (var series in ActiveSeries.Where(s => s.DeviceId == devId).ToList())
                {
                    if (!byName.TryGetValue(series.TagName, out var t)) continue;
                    var tagItem = AllTags.FirstOrDefault(ti => ti.Tag.DeviceId == devId && string.Equals(ti.Name, t.Name, StringComparison.OrdinalIgnoreCase));

                    if (double.TryParse(t.CurrentValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var val)
                        || (bool.TryParse(t.CurrentValue, out var bv) && double.TryParse(bv ? "1" : "0", out val)))
                    {
                        // Real PLC timestamp; fall back to UI time when null or stale.
                        // The stale guard is required: if capture stalls or Flush lags 5s,
                        // the old ts falls outside the window and the chart looks permanently empty.
                        var ts = t.CurrentValueAtUtc ?? now;
                        if (ts > now.AddSeconds(30)) ts = now; // future-clock guard
                        if (ts < cutoff && now - (t.CurrentValueAtUtc ?? now) <= StaleGapThreshold)
                            ts = now; // small-stale guard: show at the live edge
                        if (tagItem != null)
                        {
                            tagItem.LatestValue = $"{val:F2} {t.EngineeringUnit}";
                        }

                        // Stopped device: gap marker, never a fabricated flat line.
                        if (t.CurrentValueAtUtc.HasValue && now - t.CurrentValueAtUtc.Value > StaleGapThreshold)
                        {
                            if (series.Points.Count == 0 || series.Points[^1].Quality is not (WTT.Domain.Enums.TagQuality.Bad or WTT.Domain.Enums.TagQuality.NotConnected or WTT.Domain.Enums.TagQuality.ConfigError))
                                series.Points.Add(new TrendPoint(now, 0, WTT.Domain.Enums.TagQuality.NotConnected));
                            series.Points.RemoveAll(p => p.Time < cutoff);
                            continue;
                        }

                        // Skip duplicate timestamps (do not append the same scan twice)
                        if (series.Points.Count > 0 && series.Points[^1].Time == ts)
                            continue;

                        series.Points.Add(new TrendPoint(ts, val, t.CurrentQuality));
                        // Trim old points to keep memory clean.
                        // Also keep a count cap so the list stays bounded on a 1h window.
                        series.Points.RemoveAll(p => p.Time < cutoff);
                        const int MaxLivePoints = 10000;
                        if (series.Points.Count > MaxLivePoints)
                            series.Points.RemoveRange(0, series.Points.Count - MaxLivePoints);
                    }
                    else if (t.CurrentQuality is WTT.Domain.Enums.TagQuality.Bad
                        or WTT.Domain.Enums.TagQuality.NotConnected
                        or WTT.Domain.Enums.TagQuality.ConfigError)
                    {
                        // Non-numeric + bad quality = insert a gap marker (do not repeat the last value)
                        var ts = t.CurrentValueAtUtc ?? now;
                        if (series.Points.Count == 0 || series.Points[^1].Time != ts)
                            series.Points.Add(new TrendPoint(ts, 0, t.CurrentQuality));
                    }
                }
            }

            // Same-reference DP needs an explicit revision bump to repaint
            BumpRevision();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Live trend polling failed.");
        }
        finally { System.Threading.Interlocked.Exchange(ref _pollBusy, 0); }
    }

    [RelayCommand]
    private void SelectAllTags()
    {
        foreach (var t in FilteredTags)
        {
            if (t.Tag.DataType == WTT.Domain.Enums.TagDataType.String) continue; // text tags skip
            t.IsSelected = true;
        }
    }

    [RelayCommand]
    private void ClearAllTags()
    {
        foreach (var t in AllTags) t.IsSelected = false;
    }

    [RelayCommand]
    private void ToggleLive()
    {
        IsLive = !IsLive;
        LiveStatusText = IsLive ? "Live Streaming" : "Paused";
        if (IsLive)
            ResetViewRequested?.Invoke(this, EventArgs.Empty); // live resume = follow mode
        Dialogs.Toast("Historian Trend", $"Trend live streaming is now {(IsLive ? "Resumed" : "Paused")}.", ToastKind.Info);
    }

    [RelayCommand]
    private async Task SetTimeWindow(string minutesStr)
    {
        if (int.TryParse(minutesStr, out var mins))
        {
            TimeWindow = TimeSpan.FromMinutes(mins);
            TimeWindowLabel = mins switch
            {
                1 => "1 Min",
                5 => "5 Min",
                15 => "15 Min",
                30 => "30 Min",
                60 => "1 Hour",
                _ => $"{mins}m"
            };

            // Reset manual pan/zoom as soon as the time range changes — otherwise the plot
            // stays stuck on the old manual range and the new short-window line never appears.
            // (WinCC standard behavior.)
            ResetViewRequested?.Invoke(this, EventArgs.Empty);
            await RunAsync(async () => { await LoadHistoricalWindowAsync(); BumpRevision(); });
        }
    }

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        if (ActiveSeries.Count == 0)
        {
            Dialogs.Toast("Export", "No active trend pens to export.", ToastKind.Warning);
            return;
        }

        var sfd = new SaveFileDialog
        {
            Title = "Export Trend Data to CSV (full resolution)",
            Filter = "CSV Files (*.csv)|*.csv|All Files (*.*)|*.*",
            FileName = $"Trend_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
        };

        if (sfd.ShowDialog() != true) return;

        try
        {
            // Full-resolution re-query (screen shows only 400 pts; export up to 10k)
            var toUtc = DateTime.UtcNow;
            var fromUtc = toUtc - TimeWindow;
            var fullData = new Dictionary<(int, string), List<WTT.Application.Interfaces.HistorianDataPoint>>();
            foreach (var g in ActiveSeries.GroupBy(s => s.DeviceId))
            {
                var trends = await _historian.GetTrendsAsync(g.Key, g.Select(s => s.TagName), fromUtc, toUtc, maxPoints: 10000);
                foreach (var t in trends)
                    fullData[(g.Key, t.TagName.ToUpperInvariant())] = t.Points.ToList();
            }

            var sb = new StringBuilder();
            sb.AppendLine("Timestamp (Local),Timestamp (UTC),Device,TagName,Value,Quality,EngineeringUnit");

            long count = 0;
            foreach (var s in ActiveSeries)
            {
                var key = (s.DeviceId, s.TagName.ToUpperInvariant());
                var pts = fullData.TryGetValue(key, out var full) && full.Count > 0
                    ? full
                    : s.Points.Select(p => new WTT.Application.Interfaces.HistorianDataPoint(p.Time, p.Value, p.Quality)).ToList();
                foreach (var p in pts)
                {
                    sb.AppendLine($"{p.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff},{p.TimestampUtc:yyyy-MM-dd HH:mm:ss.fff},\"{s.DeviceName}\",\"{s.TagName}\",{p.Value:F3},{p.Quality},\"{s.EngineeringUnit}\"");
                    count++;
                }
            }

            await File.WriteAllTextAsync(sfd.FileName, sb.ToString(), Encoding.UTF8);
            Dialogs.Toast("Export Successful", $"Exported {count:N0} data points (full resolution) to {Path.GetFileName(sfd.FileName)}.", ToastKind.Success);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to export trend data to CSV.");
            Dialogs.Toast("Export Error", "Could not export CSV: " + ex.Message, ToastKind.Error);
        }
    }

    [RelayCommand]
    private void ToggleYMode()
    {
        YScaleMode = YScaleMode == TrendYScaleMode.Shared ? TrendYScaleMode.Normalized : TrendYScaleMode.Shared;
        YModeLabel = YScaleMode == TrendYScaleMode.Shared ? "Shared Y" : "Normalized %";
        BumpRevision();
    }

    [RelayCommand]
    private void TogglePenVisibility(PenSeriesModel? series)
    {
        if (series is null) return;
        series.IsVisible = !series.IsVisible;
        BumpRevision();
    }

    /// <summary>The view listens to this and calls plot.ResetView().</summary>
    public event EventHandler? ResetViewRequested;

    [RelayCommand]
    private void ResetView()
    {
        ResetViewRequested?.Invoke(this, EventArgs.Empty);
        BumpRevision();
    }

    /// <summary>Raised from the plot: the user panned/zoomed. Live-follow is now manual — toast once.</summary>
    private bool _manualToastShown;
    public void OnUserManualControl()
    {
        if (_manualToastShown) return;
        _manualToastShown = true;
        Dialogs.Toast("Trends", "Manual view: double-click or Reset View to return to live.", ToastKind.Info);
    }

    [RelayCommand]
    private async Task ExportPngAsync(object? param)
    {
        if (param is not System.Windows.FrameworkElement visual || visual.ActualWidth < 10)
        {
            Dialogs.Toast("Export", "Chart is not ready yet.", ToastKind.Warning);
            return;
        }
        var sfd = new SaveFileDialog
        {
            Title = "Export Trend Chart to PNG",
            Filter = "PNG Image (*.png)|*.png|All Files (*.*)|*.*",
            FileName = $"TrendChart_{DateTime.Now:yyyyMMdd_HHmmss}.png"
        };
        if (sfd.ShowDialog() != true) return;
        try
        {
            await ViewModelBase.OnUiAsync(async () =>
            {
                // DPI-aware: at 125%/150% scaling a hardcoded 96dpi bitmap
                // was clipped at the bottom (X-labels + digital 0-row missing).
                visual.UpdateLayout();
                var source = System.Windows.PresentationSource.FromVisual(visual);
                double dx = 1.0, dy = 1.0;
                if (source?.CompositionTarget != null)
                {
                    var m = source.CompositionTarget.TransformToDevice;
                    if (m.M11 > 0) dx = m.M11;
                    if (m.M22 > 0) dy = m.M22;
                }
                int pxW = Math.Max(1, (int)Math.Round(visual.ActualWidth * dx));
                int pxH = Math.Max(1, (int)Math.Round(visual.ActualHeight * dy));
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    pxW, pxH, 96.0 * dx, 96.0 * dy,
                    System.Windows.Media.PixelFormats.Pbgra32);
                rtb.Render(visual);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                await using var fs = File.Create(sfd.FileName);
                enc.Save(fs);
            });
            Dialogs.Toast("Export Successful", $"Chart saved to {Path.GetFileName(sfd.FileName)}.", ToastKind.Success);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to export trend PNG.");
            Dialogs.Toast("Export Error", "Could not export PNG: " + ex.Message, ToastKind.Error);
        }
    }

    private void UpdateUnitsState()
    {
        var units = ActiveSeries
            .Where(s => s.IsVisible)
            .Select(s => (s.EngineeringUnit ?? string.Empty).Trim().ToUpperInvariant())
            .Distinct()
            .ToList();
        HasMixedUnits = units.Count > 1;
        if (HasMixedUnits)
        {
            var label = string.Join(", ", ActiveSeries
                .Where(s => s.IsVisible)
                .Select(s => string.IsNullOrWhiteSpace(s.EngineeringUnit) ? $"no-unit ({s.TagName})" : s.EngineeringUnit)
                .Distinct());
            UnitsWarning = $"Mixed units ({label}) — Shared Y may be misleading. Try Normalized %.";
            // Do not auto-switch (operator's choice), only warn.
        }
        else
        {
            UnitsWarning = null;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await RunAsync(async () => { await LoadHistoricalWindowAsync(); UpdateUnitsState(); BumpRevision(); });
    }
}
