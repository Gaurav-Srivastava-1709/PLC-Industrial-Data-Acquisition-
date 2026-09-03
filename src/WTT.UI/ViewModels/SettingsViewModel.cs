using CommunityToolkit.Mvvm.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WTT.UI.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isDarkMode = true;

    [ObservableProperty]
    private bool _isMockPlcEnabled;

    public SettingsViewModel()
    {
        // Check current app theme
        _isDarkMode = Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme() == Wpf.Ui.Appearance.ApplicationTheme.Dark;
        LoadSettings();
    }

    partial void OnIsDarkModeChanged(bool value)
    {
        if (value)
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Dark);
        else
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Light);
    }

    private void LoadSettings()
    {
        try
        {
            var json = File.ReadAllText("appsettings.json");
            var node = JsonNode.Parse(json);
            if (node != null && node["MockPLC"] != null)
            {
                IsMockPlcEnabled = node["MockPLC"]?["Enabled"]?.GetValue<bool>() ?? false;
            }
        }
        catch { }
    }

    partial void OnIsMockPlcEnabledChanged(bool value)
    {
        try
        {
            var json = File.ReadAllText("appsettings.json");
            var node = JsonNode.Parse(json);
            if (node != null)
            {
                if (node["MockPLC"] == null)
                {
                    node["MockPLC"] = new JsonObject();
                }
                node["MockPLC"]!["Enabled"] = value;
                
                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText("appsettings.json", node.ToJsonString(options));
            }
        }
        catch { }
    }
}
