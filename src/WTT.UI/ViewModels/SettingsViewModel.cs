using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Data.SqlClient;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using WTT.Application.Interfaces;

namespace WTT.UI.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly IDataTransferService _dataTransferService;

    [ObservableProperty]
    private bool _isDarkMode = true;

    [ObservableProperty]
    private bool _isMockPlcEnabled;

    [ObservableProperty]
    private bool _isTransferEnabled;

    [ObservableProperty]
    private string _sqlServerName = string.Empty;

    [ObservableProperty]
    private string _sqlDatabaseName = string.Empty;

    [ObservableProperty]
    private bool _sqlUseWindowsAuth;

    [ObservableProperty]
    private string _sqlUserId = string.Empty;

    [ObservableProperty]
    private string _sqlPassword = string.Empty;

    [ObservableProperty]
    private string _connectionTestResult = string.Empty;

    [ObservableProperty]
    private System.Windows.Media.Brush _testResultColor = System.Windows.Media.Brushes.Transparent;

    public SettingsViewModel(IDataTransferService dataTransferService)
    {
        _dataTransferService = dataTransferService;
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

    private string GetConfigFilePath()
    {
        var basePath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        return File.Exists(basePath) ? basePath : "appsettings.json";
    }

    private void LoadSettings()
    {
        try
        {
            var configPath = GetConfigFilePath();
            if (File.Exists(configPath))
            {
                var json = File.ReadAllText(configPath);
                var node = JsonNode.Parse(json);
                if (node != null)
                {
                    IsMockPlcEnabled = node["MockPLC"]?["Enabled"]?.GetValue<bool>() ?? false;
                    
                    var dtNode = node["DataTransfer"];
                    if (dtNode != null)
                    {
                        IsTransferEnabled = dtNode["Enabled"]?.GetValue<bool>() ?? false;
                    }

                    var csNode = node["CentralServer"];
                    if (csNode != null)
                    {
                        SqlServerName = csNode["ServerName"]?.GetValue<string>() ?? string.Empty;
                        SqlDatabaseName = csNode["DatabaseName"]?.GetValue<string>() ?? string.Empty;
                        SqlUseWindowsAuth = csNode["UseWindowsAuth"]?.GetValue<bool>() ?? false;
                        SqlUserId = csNode["UserId"]?.GetValue<string>() ?? string.Empty;
                        SqlPassword = csNode["Password"]?.GetValue<string>() ?? string.Empty;
                    }
                }
            }
        }
        catch { }
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        ConnectionTestResult = "Testing connection...";
        TestResultColor = System.Windows.Media.Brushes.Gray;

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = SqlServerName,
            InitialCatalog = SqlDatabaseName,
            Encrypt = false
        };

        if (SqlUseWindowsAuth)
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.UserID = SqlUserId;
            builder.Password = SqlPassword;
        }

        bool result = await _dataTransferService.TestConnectionAsync(builder.ConnectionString);

        if (result)
        {
            ConnectionTestResult = "Connection successful! ✅";
            TestResultColor = System.Windows.Media.Brushes.Green;
        }
        else
        {
            ConnectionTestResult = "Connection failed. Check details. ❌";
            TestResultColor = System.Windows.Media.Brushes.Red;
        }
    }

    [RelayCommand]
    private void SaveSqlConfig()
    {
        try
        {
            var configPath = GetConfigFilePath();
            if (File.Exists(configPath))
            {
                var json = File.ReadAllText(configPath);
                var node = JsonNode.Parse(json);
                if (node != null)
                {
                    if (node["CentralServer"] == null) node["CentralServer"] = new JsonObject();
                    node["CentralServer"]!["ServerName"] = SqlServerName;
                    node["CentralServer"]!["DatabaseName"] = SqlDatabaseName;
                    node["CentralServer"]!["UseWindowsAuth"] = SqlUseWindowsAuth;
                    node["CentralServer"]!["UserId"] = SqlUserId;
                    node["CentralServer"]!["Password"] = SqlPassword;

                    if (node["DataTransfer"] == null) node["DataTransfer"] = new JsonObject();
                    node["DataTransfer"]!["Enabled"] = IsTransferEnabled;

                    var options = new JsonSerializerOptions { WriteIndented = true };
                    File.WriteAllText(configPath, node.ToJsonString(options));

                    ConnectionTestResult = "Configuration saved! Reboot app to apply.";
                    TestResultColor = System.Windows.Media.Brushes.DodgerBlue;
                }
            }
        }
        catch { }
    }

    partial void OnIsMockPlcEnabledChanged(bool value)
    {
        try
        {
            var configPath = GetConfigFilePath();
            if (File.Exists(configPath))
            {
                var json = File.ReadAllText(configPath);
                var node = JsonNode.Parse(json);
                if (node != null)
                {
                    if (node["MockPLC"] == null) node["MockPLC"] = new JsonObject();
                    node["MockPLC"]!["Enabled"] = value;
                    
                    var options = new JsonSerializerOptions { WriteIndented = true };
                    File.WriteAllText(configPath, node.ToJsonString(options));
                }
            }
        }
        catch { }
    }
}
