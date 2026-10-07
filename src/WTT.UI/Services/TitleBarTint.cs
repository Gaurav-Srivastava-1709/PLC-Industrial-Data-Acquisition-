using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using WTT.UI.Views.Windows;

namespace WTT.UI.Services;

/// <summary>
/// Native title bar (min/max/close wali patti) ko hamesha sidebar wale slate
/// (#1E293B) me rakhta hai, aur MainWindow ka background bhi slate par wapas
/// lata hai. WPF-UI ka ApplicationThemeManager.Apply har theme change par
/// caption color reset (RemoveTitlebarBackground) aur window background overwrite
/// kar deta hai, isliye ThemeManager.Apply ke baad ise dobara lagana padta hai.
/// Caption tint sirf Windows 11+ par asar karta hai; background restore har
/// Windows par kaam karta hai.
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
                    // Sidebar pane transparent hai - Window ka background hi
                    // sidebar ka rang hai. Theme-apply isko overwrite kar deta
                    // hai, isliye yahan wapas slate karo (sirf MainWindow par).
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
                    // Ek window fail ho to dusri par asar nahi.
                }
            }
        }
        catch
        {
            // Title bar tint best-effort hai; kabhi app flow nahi rokta.
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, int[] attrValue, int attrSize);

    /// <summary>
    /// Content area ka background wapas fully-opaque karta hai. WPF-UI ka
    /// NavigationViewContentBackground 50% transparent safed hai - safed Window
    /// par safed dikhta tha, lekin slate Window par bhoora (grey) pad jata hai.
    /// Isliye current theme ke window-rang ko opaque karke content par lagao,
    /// taaki content hamesha pehle jaisa dikhe (sidebar par asar nahi).
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
            // Best-effort; fail ho to theme default rehta hai.
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
