using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using WTT.UI.Views.Windows;

namespace WTT.UI.Services;

/// <summary>
/// Keeps the native title bar (min/max/close strip) always in the sidebar slate
/// (#1E293B), and restores the MainWindow background to slate as well.
/// WPF-UI's ApplicationThemeManager.Apply resets the caption color
/// (RemoveTitlebarBackground) and overwrites the window background on every
/// theme change, so this must be re-applied after ThemeManager.Apply.
/// The caption tint only takes effect on Windows 11+; the background restore
/// works on every Windows version.
/// </summary>
public static class TitleBarTint
{
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;
    private const int SlateCaption = 0x003B291E; // #1E293B
    private const int WhiteText = 0x00FFFFFF;

    private static readonly SolidColorBrush SlateBackground = CreateFrozenSlate();

    private static SolidColorBrush CreateFrozenSlate()
    {
        var b = new SolidColorBrush(Color.FromRgb(0x1E, 0x29, 0x3B));
        b.Freeze();
        return b;
    }

    public static void ApplySlate()
    {
        try
        {
            var app = System.Windows.Application.Current;
            if (app is null) return;
            if (!app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.Invoke(ApplySlate);
                return;
            }
            foreach (Window window in app.Windows)
            {
                try
                {
                    // The sidebar pane is transparent — the Window background is what
                    // shows as the sidebar color. Theme-apply overwrites it,
                    // so re-apply slate here (MainWindow only).
                    if (window is MainWindow mw)
                    {
                        mw.Background = SlateBackground;
                        RestoreOpaqueContentBackground(mw);
                    }
                    if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) continue;
                    var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                    if (hwnd == IntPtr.Zero) continue;
                    _ = DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, new[] { SlateCaption }, 4);
                    _ = DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, new[] { WhiteText }, 4);
                }
                catch
                {
                    // If one window fails, the others are unaffected.
                }
            }
        }
        catch
        {
            // Title bar tint is best-effort; it never blocks the app flow.
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, int[] attrValue, int attrSize);

    /// <summary>
    /// Restores the content area background to fully opaque. WPF-UI's
    /// NavigationViewContentBackground is 50% transparent white — it looked
    /// white on a white Window, but turns greyish on a slate Window.
    /// So take the current theme's window color, make it opaque and apply it
    /// to the content, keeping the content visually consistent (sidebar unaffected).
    /// </summary>
    private static void RestoreOpaqueContentBackground(MainWindow mw)
    {
        try
        {
            var nav = FindNavigationView(mw);
            if (nav is null) return;
            var app = System.Windows.Application.Current;
            if (app?.Resources["ApplicationBackgroundBrush"] is not SolidColorBrush themeBg) return;
            var opaque = new SolidColorBrush(Color.FromRgb(themeBg.Color.R, themeBg.Color.G, themeBg.Color.B));
            opaque.Freeze();
            nav.Resources["NavigationViewContentBackground"] = opaque;
        }
        catch
        {
            // Best-effort; on failure the theme default stays.
        }
    }

    private static FrameworkElement? FindNavigationView(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe && child.GetType().FullName == "Wpf.Ui.Controls.NavigationView")
                return fe;
            var found = FindNavigationView(child);
            if (found is not null) return found;
        }
        return null;
    }
}
