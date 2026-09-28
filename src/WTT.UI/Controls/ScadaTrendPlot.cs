using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using WTT.Domain.Enums;

namespace WTT.UI.Controls;

/// <summary>Y-axis mode: Shared raw values vs Normalized 0-100% (for mixed units).</summary>
public enum TrendYScaleMode
{
    Shared = 0,
    Normalized = 1,
}

/// <summary>A single chart point: time + value + quality (for gap detection).</summary>
public readonly record struct TrendPoint(DateTime Time, double Value, TagQuality Quality)
{
    public bool IsGap => Quality is TagQuality.Bad or TagQuality.NotConnected or TagQuality.ConfigError;
}

/// <summary>
/// Data and visual styling for a single trend pen (line).
/// </summary>
public class PenSeriesModel
{
    public int DeviceId { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public string TagName { get; set; } = string.Empty;
    public string DisplayPath => string.IsNullOrWhiteSpace(DeviceName) ? TagName : $"{DeviceName}/{TagName}";
    public string? EngineeringUnit { get; set; }
    public Color Color { get; set; } = Colors.Cyan;
    public SolidColorBrush StrokeBrush { get; set; } = Brushes.Cyan;
    public Pen LinePen { get; set; } = new(Brushes.Cyan, 2.0);
    public bool IsVisible { get; set; } = true;
    public double? MinAlarm { get; set; }
    public double? MaxAlarm { get; set; }
    public TagDataType DataType { get; set; } = TagDataType.Float32;
    public bool IsDigital => DataType == TagDataType.Bool;
    public List<TrendPoint> Points { get; set; } = new();

    public double? CurrentValue => Points.Count > 0 ? Points[^1].Value : null;
    public TagQuality? CurrentQuality => Points.Count > 0 ? Points[^1].Quality : null;
    public double? MinValue
    {
        get
        {
            double? m = null;
            foreach (var p in Points)
            {
                if (p.IsGap) continue;
                if (m is null || p.Value < m) m = p.Value;
            }
            return m;
        }
    }
    public double? MaxValue
    {
        get
        {
            double? m = null;
            foreach (var p in Points)
            {
                if (p.IsGap) continue;
                if (m is null || p.Value > m) m = p.Value;
            }
            return m;
        }
    }
}

/// <summary>
/// Industrial SCADA Multi-Pen Vector Trend Plot:
/// shared/normalized Y, pan/zoom, quality gaps, digital step, binary-search hover.
/// </summary>
public class ScadaTrendPlot : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty =
        DependencyProperty.Register(nameof(Series), typeof(IEnumerable<PenSeriesModel>), typeof(ScadaTrendPlot),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TimeWindowProperty =
        DependencyProperty.Register(nameof(TimeWindow), typeof(TimeSpan), typeof(ScadaTrendPlot),
            new FrameworkPropertyMetadata(TimeSpan.FromMinutes(5), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsLiveProperty =
        DependencyProperty.Register(nameof(IsLive), typeof(bool), typeof(ScadaTrendPlot),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RenderRevisionProperty =
        DependencyProperty.Register(nameof(RenderRevision), typeof(long), typeof(ScadaTrendPlot),
            new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty YScaleModeProperty =
        DependencyProperty.Register(nameof(YScaleMode), typeof(TrendYScaleMode), typeof(ScadaTrendPlot),
            new FrameworkPropertyMetadata(TrendYScaleMode.Shared, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable<PenSeriesModel>? Series
    {
        get => (IEnumerable<PenSeriesModel>?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public TimeSpan TimeWindow
    {
        get => (TimeSpan)GetValue(TimeWindowProperty);
        set => SetValue(TimeWindowProperty, value);
    }

    public bool IsLive
    {
        get => (bool)GetValue(IsLiveProperty);
        set => SetValue(IsLiveProperty, value);
    }

    public long RenderRevision
    {
        get => (long)GetValue(RenderRevisionProperty);
        set => SetValue(RenderRevisionProperty, value);
    }

    public TrendYScaleMode YScaleMode
    {
        get => (TrendYScaleMode)GetValue(YScaleModeProperty);
        set => SetValue(YScaleModeProperty, value);
    }

    /// <summary>The ViewModel listens to this so Live-follow auto-pauses on pan/zoom.</summary>
    public event EventHandler? UserTookManualControl;

    private static readonly SolidColorBrush BgBrush = new(Color.FromRgb(22, 27, 34));
    private static readonly SolidColorBrush PlotAreaBrush = new(Color.FromRgb(13, 17, 23));
    private static readonly Pen BorderPen = new(new SolidColorBrush(Color.FromRgb(48, 54, 61)), 1.0);
    private static readonly Pen GridPen = new(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 1.0) { DashStyle = DashStyles.Dash };
    private static readonly Pen CrosshairPen = new(new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)), 1.0) { DashStyle = DashStyles.DashDot };
    private static readonly Pen AlarmHighPen = new(new SolidColorBrush(Color.FromArgb(200, 248, 81, 73)), 1.5) { DashStyle = DashStyles.Dash };
    private static readonly Pen AlarmLowPen = new(new SolidColorBrush(Color.FromArgb(200, 227, 179, 65)), 1.5) { DashStyle = DashStyles.Dash };
    private static readonly SolidColorBrush TextBrush = new(Color.FromRgb(139, 148, 158));
    private static readonly SolidColorBrush TooltipBgBrush = new(Color.FromArgb(235, 22, 27, 34));
    private static readonly Pen TooltipBorderPen = new(new SolidColorBrush(Color.FromRgb(56, 139, 253)), 1.2);
    private static readonly SolidColorBrush GapBrush = new(Color.FromArgb(160, 248, 81, 73));
    private static readonly Pen GapPen = new(new SolidColorBrush(Color.FromArgb(160, 248, 81, 73)), 1.2);
    private static readonly Typeface AxisTypeface = new("Segoe UI");

    private Point? _hoverPoint;
    private long _lastHoverRenderTicks;
    // Hover redraw at most ~30fps: every MouseMove pixel doing a full multi-pen
    // redraw pegs the CPU (fan noise) with zero visual benefit past 30fps.
    private const long HoverThrottleTicks = TimeSpan.TicksPerSecond / 30;

    private void RequestHoverRender(Point p)
    {
        _hoverPoint = p;
        var now = DateTime.UtcNow.Ticks;
        if (now - _lastHoverRenderTicks < HoverThrottleTicks) return;
        _lastHoverRenderTicks = now;
        InvalidateVisual();
    }
    // Manual view state (pan/zoom). Null = live-follow.
    private DateTime? _manualMinTime;
    private DateTime? _manualMaxTime;
    private double? _manualMinY;
    private double? _manualMaxY;
    private Point? _panStart;
    private DateTime _panMin0;
    private DateTime _panMax0;

    static ScadaTrendPlot()
    {
        BgBrush.Freeze();
        PlotAreaBrush.Freeze();
        BorderPen.Freeze();
        GridPen.Freeze();
        CrosshairPen.Freeze();
        AlarmHighPen.Freeze();
        AlarmLowPen.Freeze();
        TextBrush.Freeze();
        TooltipBgBrush.Freeze();
        TooltipBorderPen.Freeze();
        GapBrush.Freeze();
        GapPen.Freeze();
    }

    public ScadaTrendPlot()
    {
        ClipToBounds = true;
        Focusable = true;

        MouseMove += (_, e) =>
        {
            if (_panStart.HasValue && e.LeftButton == MouseButtonState.Pressed)
            {
                // Drag-pan: pixel delta -> time delta
                var cur = e.GetPosition(this);
                double dx = cur.X - _panStart.Value.X;
                double plotW = Math.Max(10, ActualWidth - 65 - 25);
                if (plotW > 0 && _manualMinTime.HasValue && _manualMaxTime.HasValue)
                {
                    var span = _manualMaxTime.Value - _manualMinTime.Value;
                    var shift = TimeSpan.FromTicks((long)(-dx / plotW * span.Ticks));
                    _manualMinTime = _panMin0 + shift;
                    _manualMaxTime = _panMax0 + shift;
                    _lastHoverRenderTicks = DateTime.UtcNow.Ticks;
                    InvalidateVisual();
                }
                _hoverPoint = cur;
                return;
            }
            RequestHoverRender(e.GetPosition(this));
        };

        MouseLeave += (_, _) =>
        {
            _hoverPoint = null;
            _panStart = null;
            InvalidateVisual();
        };

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                ResetView();
                return;
            }
            // Pan start: freeze the current view into manual mode
            EnsureManualTime();
            _panStart = e.GetPosition(this);
            _panMin0 = _manualMinTime!.Value;
            _panMax0 = _manualMaxTime!.Value;
            CaptureMouse();
            UserTookManualControl?.Invoke(this, EventArgs.Empty);
        };

        MouseLeftButtonUp += (_, _) =>
        {
            _panStart = null;
            ReleaseMouseCapture();
            InvalidateVisual();
        };

        MouseWheel += (_, e) =>
        {
            var pos = e.GetPosition(this);
            double marginLeft = 65, marginRight = 25;
            double plotW = ActualWidth - marginLeft - marginRight;
            if (plotW <= 0) return;
            double frac = Math.Clamp((pos.X - marginLeft) / plotW, 0, 1);

            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                // Ctrl+Wheel = Y zoom
                EnsureManualY();
                if (_manualMinY.HasValue && _manualMaxY.HasValue)
                {
                    double range = _manualMaxY.Value - _manualMinY.Value;
                    if (range <= 0) return;
                    double factor = e.Delta > 0 ? 0.8 : 1.25;
                    double centerFrac = 0.5;
                    // Zoom on cursor Y (optional: center). Simple center zoom.
                    double mid = (_manualMinY.Value + _manualMaxY.Value) / 2;
                    double newRange = Math.Max(1e-9, range * factor);
                    _manualMinY = mid - newRange * centerFrac;
                    _manualMaxY = mid + newRange * centerFrac;
                    UserTookManualControl?.Invoke(this, EventArgs.Empty);
                    InvalidateVisual();
                }
                e.Handled = true;
                return;
            }

            // Plain wheel = time zoom around cursor
            EnsureManualTime();
            var span = _manualMaxTime!.Value - _manualMinTime!.Value;
            if (span.TotalSeconds < 5 && e.Delta > 0) return; // min 5 sec
            if (span.TotalDays > 30 && e.Delta < 0) return;   // max 30 day
            double factorT = e.Delta > 0 ? 0.8 : 1.25;
            long newTicks = (long)(span.Ticks * factorT);
            var cursorTime = _manualMinTime.Value + TimeSpan.FromTicks((long)(span.Ticks * frac));
            var newMin = cursorTime - TimeSpan.FromTicks((long)(newTicks * frac));
            _manualMinTime = newMin;
            _manualMaxTime = newMin + TimeSpan.FromTicks(newTicks);
            UserTookManualControl?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
            e.Handled = true;
        };
    }

    /// <summary>Return to Live-follow (pan/zoom reset).</summary>
    public void ResetView()
    {
        _manualMinTime = null;
        _manualMaxTime = null;
        _manualMinY = null;
        _manualMaxY = null;
        InvalidateVisual();
    }

    private void EnsureManualTime()
    {
        if (_manualMinTime.HasValue && _manualMaxTime.HasValue) return;
        var now = DateTime.UtcNow;
        _manualMaxTime = now;
        _manualMinTime = now - TimeWindow;
    }

    private void EnsureManualY()
    {
        if (_manualMinY.HasValue && _manualMaxY.HasValue) return;
        // Caller sets this after OnRender; default 0-100 here to avoid a crash
        _manualMinY = 0;
        _manualMaxY = 100;
        _pendingYInit = true;
    }
    private bool _pendingYInit;

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double width = ActualWidth;
        double height = ActualHeight;
        if (width < 100 || height < 100) return;

        dc.DrawRectangle(BgBrush, null, new Rect(0, 0, width, height));

        double marginLeft = YScaleMode == TrendYScaleMode.Normalized ? 55 : 65;
        double marginRight = 25;
        double marginTop = 25;
        double marginBottom = 35;

        double plotW = Math.Max(10, width - marginLeft - marginRight);
        double plotH = Math.Max(10, height - marginTop - marginBottom);
        Rect plotRect = new(marginLeft, marginTop, plotW, plotH);

        dc.DrawRectangle(PlotAreaBrush, BorderPen, plotRect);

        var activeSeries = Series?.Where(s => s.IsVisible && s.Points.Count > 0).ToList();
        var visiblePenCount = Series?.Count(s => s.IsVisible) ?? 0;

        if (activeSeries == null || activeSeries.Count == 0)
        {
            // Two cases: (a) no pen selected, (b) pens selected but no data in window.
            string msg = visiblePenCount == 0
                ? "Waiting for PLC tag data... Check at least one Tag from the list to display trend."
                : $"Pens selected ({visiblePenCount}) but no points in this time window. If live streaming is ON, the line appears in 1-2 sec; otherwise try a larger window (15m/1h).";
            var hintText = new FormattedText(
                msg,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                AxisTypeface,
                13,
                TextBrush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(hintText, new Point(marginLeft + (plotW - hintText.Width) / 2, marginTop + (plotH - hintText.Height) / 2));
            return;
        }

        bool normalized = YScaleMode == TrendYScaleMode.Normalized;

        // 1. Time limits: manual view (pan/zoom) vs live-follow
        DateTime minTime, maxTime;
        if (_manualMinTime.HasValue && _manualMaxTime.HasValue)
        {
            minTime = _manualMinTime.Value;
            maxTime = _manualMaxTime.Value;
        }
        else if (IsLive)
        {
            maxTime = DateTime.UtcNow;
            minTime = maxTime - TimeWindow;
        }
        else
        {
            minTime = activeSeries.SelectMany(s => s.Points).Min(p => p.Time);
            maxTime = activeSeries.SelectMany(s => s.Points).Max(p => p.Time);
        }
        if (maxTime <= minTime) maxTime = minTime.AddSeconds(10);
        double totalTicks = (maxTime - minTime).Ticks;

        // 2. Analog/Digital lane split (industry standard):
        // Bool (ON/OFF) pens go in a separate lane below — otherwise a 0/1 vs 100 shared Y
        // would squash the analog lines (CaptureOn overlap issue).
        var analogSeries = activeSeries.Where(s => !s.IsDigital).ToList();
        var digitalSeries = activeSeries.Where(s => s.IsDigital).ToList();
        bool hasDigital = digitalSeries.Count > 0;
        bool hasAnalog = analogSeries.Count > 0;
        // If only digital pens exist, give them the full plot; otherwise 72% analog / 28% digital.
        double analogH = !hasAnalog ? 0 : !hasDigital ? plotH : plotH * 0.72;
        double laneGap = hasAnalog && hasDigital ? 8 : 0;
        double digitalH = hasDigital ? plotH - analogH - laneGap : 0;
        Rect analogRect = new(marginLeft, marginTop, plotW, Math.Max(0, analogH));
        Rect digitalRect = hasDigital
            ? new Rect(marginLeft, marginTop + analogH + laneGap, plotW, Math.Max(0, digitalH))
            : default;
        if (hasDigital)
            dc.DrawRectangle(PlotAreaBrush, BorderPen, digitalRect);

        // 2b. Value limits (ANALOG only — exclude digital 0/1 from Y)
        double minY, maxY;
        if (normalized)
        {
            minY = 0; maxY = 100;
        }
        else if (_manualMinY.HasValue && _manualMaxY.HasValue && !_pendingYInit)
        {
            minY = _manualMinY.Value;
            maxY = _manualMaxY.Value;
        }
        else
        {
            minY = double.MaxValue;
            maxY = double.MinValue;
            var ySource = hasAnalog ? analogSeries : activeSeries;
            foreach (var s in ySource)
            {
                foreach (var p in s.Points)
                {
                    if (p.IsGap) continue;
                    if (p.Time < minTime || p.Time > maxTime) continue;
                    if (p.Value < minY) minY = p.Value;
                    if (p.Value > maxY) maxY = p.Value;
                }
                if (!normalized)
                {
                    if (s.MinAlarm.HasValue) minY = Math.Min(minY, s.MinAlarm.Value);
                    if (s.MaxAlarm.HasValue) maxY = Math.Max(maxY, s.MaxAlarm.Value);
                }
            }
            if (minY == double.MaxValue || maxY == double.MinValue) { minY = 0; maxY = 100; }
            else if (Math.Abs(maxY - minY) < 0.001) { minY -= 5; maxY += 5; }
            else { double pad = (maxY - minY) * 0.1; minY -= pad; maxY += pad; }

            if (_pendingYInit)
            {
                _manualMinY = minY; _manualMaxY = maxY; _pendingYInit = false;
            }
        }
        double yRange = Math.Max(1e-9, maxY - minY);

        // Per-series normalize factors (mixed units)
        var normMap = new Dictionary<PenSeriesModel, (double sMin, double sRange)>();
        if (normalized)
        {
            foreach (var s in activeSeries)
            {
                double sMin = s.MinValue ?? 0, sMax = s.MaxValue ?? 100;
                if (Math.Abs(sMax - sMin) < 1e-9) { sMin -= 5; sMax += 5; }
                normMap[s] = (sMin, sMax - sMin);
            }
        }

        double NormY(PenSeriesModel s, double v)
        {
            if (!normalized) return v;
            var (sMin, sRange) = normMap[s];
            return (v - sMin) / sRange * 100.0;
        }

        // 3. Y grid + labels (in ANALOG lane; 0/1 labels in digital lane).
        // Digital-only view: numeric 0.00-1.00 labels would stack on each other,
        // so skip them — the digital lane already draws clean 0/1 labels.
        double yBase = marginTop + analogH; // analog lane bottom
        if (hasAnalog)
        {
        int yDivisions = 5;
        for (int i = 0; i <= yDivisions; i++)
        {
            double frac = (double)i / yDivisions;
            double yVal = minY + frac * yRange;
            double yPos = yBase - (frac * analogH);
            if (i > 0 && i < yDivisions)
                dc.DrawLine(GridPen, new Point(marginLeft, yPos), new Point(marginLeft + plotW, yPos));

            string label = normalized
                ? $"{yVal:F0}%"
                : yVal >= 100 ? yVal.ToString("F1", CultureInfo.InvariantCulture) : yVal.ToString("F2", CultureInfo.InvariantCulture);
            var yText = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                AxisTypeface, 10, TextBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(yText, new Point(marginLeft - yText.Width - 8, yPos - yText.Height / 2));
        }
        }
        else
        {
            var digTitle = new FormattedText("Digital (ON/OFF)",
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight, AxisTypeface, 10,
                TextBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(digTitle, new Point(marginLeft - digTitle.Width - 8, marginTop));
        }

        // 4. X grid + labels (date-aware)
        bool showDate = (maxTime - minTime).TotalHours >= 12 || minTime.Date != maxTime.Date;
        int xDivisions = 5;
        for (int i = 0; i <= xDivisions; i++)
        {
            double frac = (double)i / xDivisions;
            DateTime tVal = minTime + TimeSpan.FromTicks((long)(frac * totalTicks));
            double xPos = marginLeft + (frac * plotW);
            if (i > 0 && i < xDivisions)
                dc.DrawLine(GridPen, new Point(xPos, marginTop), new Point(xPos, marginTop + plotH));

            string fmt = showDate ? "MM-dd HH:mm" : "HH:mm:ss";
            var xText = new FormattedText(tVal.ToLocalTime().ToString(fmt), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, AxisTypeface, 10, TextBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(xText, new Point(xPos - xText.Width / 2, marginTop + plotH + 8));
        }

        // 4b. Digital lane labels (0 / 1)
        if (hasDigital)
        {
            var oneText = new FormattedText("1", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                AxisTypeface, 10, TextBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            var zeroText = new FormattedText("0", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                AxisTypeface, 10, TextBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            double oneY = digitalRect.Top + digitalH * 0.15;
            double zeroY = digitalRect.Top + digitalH * 0.75;
            dc.DrawText(oneText, new Point(marginLeft - oneText.Width - 8, oneY - oneText.Height / 2));
            dc.DrawText(zeroText, new Point(marginLeft - zeroText.Width - 8, zeroY - zeroText.Height / 2));
            dc.DrawLine(GridPen, new Point(marginLeft, oneY), new Point(marginLeft + plotW, oneY));
            dc.DrawLine(GridPen, new Point(marginLeft, zeroY), new Point(marginLeft + plotW, zeroY));
        }

        // Manual-view badge
        if (_manualMinTime.HasValue)
        {
            var badge = new FormattedText("PAN/ZOOM — double-click to reset",
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight, AxisTypeface, 10,
                new SolidColorBrush(Color.FromRgb(255, 179, 0)), VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(badge, new Point(marginLeft + 6, 6));
        }

        // 5. Alarm thresholds (ANALOG lane, shared mode only)
        foreach (var s in analogSeries)
        {
            if (normalized) continue; // Skipped in normalized mode (alarms are meaningless in %); shown in legend
            if (s.MaxAlarm.HasValue && s.MaxAlarm.Value >= minY && s.MaxAlarm.Value <= maxY)
            {
                double aY = yBase - ((s.MaxAlarm.Value - minY) / yRange * analogH);
                dc.DrawLine(AlarmHighPen, new Point(marginLeft, aY), new Point(marginLeft + plotW, aY));
            }
            if (s.MinAlarm.HasValue && s.MinAlarm.Value >= minY && s.MinAlarm.Value <= maxY)
            {
                double aY = yBase - ((s.MinAlarm.Value - minY) / yRange * analogH);
                dc.DrawLine(AlarmLowPen, new Point(marginLeft, aY), new Point(marginLeft + plotW, aY));
            }
        }

        // 6. Multi-pen lines: analog lane + digital lane (gap-break + step)
        // Analog lane
        if (hasAnalog)
        {
        dc.PushClip(new RectangleGeometry(analogRect));

        foreach (var s in analogSeries)
        {
            // Time-window filter (binary-search friendly: points are time-sorted)
            int lo = LowerBound(s.Points, minTime);
            int hi = UpperBound(s.Points, maxTime);
            if (lo >= hi) continue;

            StreamGeometry? stream = null;
            StreamGeometryContext? ctx = null;
            bool penOpen = false;
            Point lastPt = default;
            bool hasLast = false;

            void FlushStroke()
            {
                if (ctx != null) { ctx.Close(); ctx = null; }
                if (stream != null) { stream.Freeze(); dc.DrawGeometry(null, s.LinePen, stream); stream = null; }
                penOpen = false;
            }

            for (int i = lo; i < hi; i++)
            {
                var p = s.Points[i];
                if (p.IsGap)
                {
                    // Gap: break stroke, draw red X marker (analog lane center)
                    FlushStroke();
                    double xFrac = (p.Time.Ticks - minTime.Ticks) / totalTicks;
                    double px = marginLeft + xFrac * plotW;
                    double py = marginTop + analogH / 2;
                    dc.DrawLine(GapPen, new Point(px - 4, py - 4), new Point(px + 4, py + 4));
                    dc.DrawLine(GapPen, new Point(px - 4, py + 4), new Point(px + 4, py - 4));
                    hasLast = false;
                    continue;
                }

                double yv = NormY(s, p.Value);
                double xF = (p.Time.Ticks - minTime.Ticks) / totalTicks;
                double yF = (yv - minY) / yRange;
                Point cur = new(marginLeft + xF * plotW, yBase - (yF * analogH));

                if (!penOpen)
                {
                    stream = new StreamGeometry();
                    ctx = stream.Open();
                    ctx.BeginFigure(cur, false, false);
                    penOpen = true;
                }
                else
                {
                    ctx!.LineTo(cur, true, false);
                }
                lastPt = cur;
                hasLast = true;
            }
            FlushStroke();

            if (hasLast)
                dc.DrawEllipse(s.StrokeBrush, null, lastPt, 3.5, 3.5);
        }

        dc.Pop();
        } // end analog lane

        // 6b. Digital lane (step-chart, 0 at bottom / 1 at top)
        if (hasDigital)
        {
        dc.PushClip(new RectangleGeometry(digitalRect));

        foreach (var s in digitalSeries)
        {
            int lo = LowerBound(s.Points, minTime);
            int hi = UpperBound(s.Points, maxTime);
            if (lo >= hi) continue;

            StreamGeometry? stream = null;
            StreamGeometryContext? ctx = null;
            bool penOpen = false;
            Point lastPt = default;
            bool hasLast = false;

            double DigiY(double v)
            {
                double clamped = Math.Clamp(v, 0, 1);
                // 1 = top (15%), 0 = bottom (85%)
                return digitalRect.Top + digitalH * (0.85 - clamped * 0.7);
            }

            void FlushStroke()
            {
                if (ctx != null) { ctx.Close(); ctx = null; }
                if (stream != null) { stream.Freeze(); dc.DrawGeometry(null, s.LinePen, stream); stream = null; }
                penOpen = false;
            }

            for (int i = lo; i < hi; i++)
            {
                var p = s.Points[i];
                if (p.IsGap)
                {
                    FlushStroke();
                    double xFrac = (p.Time.Ticks - minTime.Ticks) / totalTicks;
                    double px = marginLeft + xFrac * plotW;
                    double py = digitalRect.Top + digitalH / 2;
                    dc.DrawLine(GapPen, new Point(px - 4, py - 4), new Point(px + 4, py + 4));
                    dc.DrawLine(GapPen, new Point(px - 4, py + 4), new Point(px + 4, py - 4));
                    hasLast = false;
                    continue;
                }

                double xF = (p.Time.Ticks - minTime.Ticks) / totalTicks;
                Point cur = new(marginLeft + xF * plotW, DigiY(p.Value));

                if (!penOpen)
                {
                    stream = new StreamGeometry();
                    ctx = stream.Open();
                    ctx.BeginFigure(cur, false, false);
                    penOpen = true;
                }
                else
                {
                    // Step: horizontal first, then vertical
                    ctx!.LineTo(new Point(cur.X, lastPt.Y), true, false);
                    ctx.LineTo(cur, true, false);
                }
                lastPt = cur;
                hasLast = true;
            }
            FlushStroke();

            if (hasLast)
                dc.DrawEllipse(s.StrokeBrush, null, lastPt, 3.5, 3.5);
        }

        dc.Pop();
        }

        // 7. Hover crosshair + tooltip (binary search per pen)
        if (_hoverPoint.HasValue && plotRect.Contains(_hoverPoint.Value))
        {
            double hx = _hoverPoint.Value.X;
            dc.DrawLine(CrosshairPen, new Point(hx, marginTop), new Point(hx, marginTop + plotH));

            double hoverFrac = (hx - marginLeft) / plotW;
            DateTime hoverTime = minTime + TimeSpan.FromTicks((long)(hoverFrac * totalTicks));

            var tooltipLines = new List<(string text, Brush brush)>();
            tooltipLines.Add(($"⏱ {hoverTime.ToLocalTime():HH:mm:ss.fff}", TextBrush));

            foreach (var s in activeSeries)
            {
                int idx = NearestIndex(s.Points, hoverTime);
                if (idx < 0) continue;
                var nearest = s.Points[idx];
                if (nearest.IsGap)
                {
                    tooltipLines.Add(($"● {s.DisplayPath}: GAP ({nearest.Quality})", GapBrush));
                    continue;
                }
                double nx = marginLeft + ((nearest.Time.Ticks - minTime.Ticks) / totalTicks * plotW);
                double ny;
                Rect laneRect;
                if (s.IsDigital && hasDigital)
                {
                    double clamped = Math.Clamp(nearest.Value, 0, 1);
                    ny = digitalRect.Top + digitalH * (0.85 - clamped * 0.7);
                    laneRect = digitalRect;
                }
                else
                {
                    ny = yBase - ((NormY(s, nearest.Value) - minY) / yRange * analogH);
                    laneRect = hasAnalog ? analogRect : plotRect;
                }
                if (laneRect.Contains(new Point(nx, ny)))
                    dc.DrawEllipse(Brushes.White, s.LinePen, new Point(nx, ny), 4, 4);

                string unitStr = string.IsNullOrWhiteSpace(s.EngineeringUnit) ? "" : $" {s.EngineeringUnit}";
                string qStr = nearest.Quality == TagQuality.Good ? "" : $" [{nearest.Quality}]";
                tooltipLines.Add(($"● {s.DisplayPath}: {nearest.Value:F2}{unitStr}{qStr}", s.StrokeBrush));
            }

            double ttWidth = 230;
            double ttHeight = 18 + tooltipLines.Count * 18;
            double ttX = hx + 15;
            double ttY = _hoverPoint.Value.Y - 10;
            if (ttX + ttWidth > width - 10) ttX = hx - ttWidth - 15;
            if (ttY + ttHeight > height - 10) ttY = height - ttHeight - 10;
            if (ttY < 10) ttY = 10;

            Rect ttRect = new(ttX, ttY, ttWidth, ttHeight);
            dc.DrawRoundedRectangle(TooltipBgBrush, TooltipBorderPen, ttRect, 6, 6);

            double lineY = ttY + 8;
            foreach (var line in tooltipLines)
            {
                var ft = new FormattedText(line.text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    AxisTypeface, 11, line.brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(ft, new Point(ttX + 10, lineY));
                lineY += 18;
            }
        }
    }

    // ---- Binary search helpers (Points time-sorted) ----
    private static int LowerBound(List<TrendPoint> pts, DateTime t)
    {
        int lo = 0, hi = pts.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (pts[mid].Time < t) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private static int UpperBound(List<TrendPoint> pts, DateTime t)
    {
        int lo = 0, hi = pts.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (pts[mid].Time <= t) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private static int NearestIndex(List<TrendPoint> pts, DateTime t)
    {
        if (pts.Count == 0) return -1;
        int lo = 0, hi = pts.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (pts[mid].Time < t) lo = mid + 1;
            else hi = mid;
        }
        int best = lo;
        long bestDiff = Math.Abs((pts[lo].Time - t).Ticks);
        if (lo > 0)
        {
            long d = Math.Abs((pts[lo - 1].Time - t).Ticks);
            if (d < bestDiff) best = lo - 1;
        }
        return best;
    }
}
