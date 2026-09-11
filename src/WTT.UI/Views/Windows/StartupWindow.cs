using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WTT.UI.Views.Windows;

public sealed class StartupWindow : Window
{
    public StartupWindow()
    {
        Title = "WTT starting";
        Width = 420;
        Height = 230;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.None;
        Background = new SolidColorBrush(Color.FromRgb(18, 28, 42));

        Content = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(36),
            Children =
            {
                new TextBlock { Text = "WTT", FontSize = 34, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock { Text = "Industrial Data Acquisition", FontSize = 15, Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 24) },
                new ProgressBar { IsIndeterminate = true, Height = 5 },
                new TextBlock { Text = "Checking secure database and runtime services...", FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(203, 213, 225)), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) },
            },
        };
    }
}
