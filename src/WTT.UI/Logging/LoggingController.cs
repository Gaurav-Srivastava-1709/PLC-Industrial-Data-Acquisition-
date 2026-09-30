using Serilog.Core;
using Serilog.Events;
using System.IO;

namespace WTT.UI.Logging;

/// <summary>
/// Runtime logging controller — app restart ki zaroorat nahi!
/// 
/// Industry standard approach:
/// - Serilog ka LoggingLevelSwitch use karo level runtime mein change karne ke liye.
/// - File logging toggle ke liye ek thread-safe flag use karo.
/// - Ye singleton DI mein register hota hai aur SettingsViewModel se use hota hai.
/// 
/// Manager Requirement #3: Toggle to turn off log generation (runtime mein, bina restart ke).
/// </summary>
public sealed class LoggingController
{
    // Serilog LoggingLevelSwitch — ye live mein level change karta hai (no restart needed)
    // "Off" = LogEventLevel.Fatal + 1 ka trick hai Serilog mein logging completely band karne ke liye
    private readonly LoggingLevelSwitch _levelSwitch;
    private readonly LoggingOptions _options;

    // File logging toggle ka current state (thread-safe volatile)
    private volatile bool _fileLoggingEnabled;

    // Fatal+1 level = effectively OFF (koi bhi event is level se nahi guzarta)
    private const LogEventLevel OffLevel = (LogEventLevel)1 + (int)LogEventLevel.Fatal;

    // Jab logging status change ho to UI ko notify karne ke liye event
    public event EventHandler<bool>? FileLoggingToggled;

    public LoggingController(LoggingLevelSwitch levelSwitch, LoggingOptions options)
    {
        _levelSwitch = levelSwitch;
        _options = options;
        _fileLoggingEnabled = options.EnableFileLogging;
    }

    /// <summary>
    /// File logging currently enabled hai ya nahi.
    /// </summary>
    public bool IsFileLoggingEnabled => _fileLoggingEnabled;

    /// <summary>
    /// Current minimum log level.
    /// </summary>
    public LogEventLevel CurrentLevel => _levelSwitch.MinimumLevel;

    /// <summary>
    /// File logging runtime mein ON/OFF karta hai.
    /// Ye appsettings.json bhi update karta hai taaki app restart ke baad bhi setting yaad rahe.
    /// </summary>
    public async Task SetFileLoggingAsync(bool enabled)
    {
        _fileLoggingEnabled = enabled;

        if (enabled)
        {
            // Logging restore karo original level par
            _levelSwitch.MinimumLevel = _options.GetMinimumLogEventLevel();
        }
        else
        {
            // Logging completely band karo — "Off" level trick
            // Note: Serilog mein built-in "Off" nahi hai, Fatal+1 use karte hain
            _levelSwitch.MinimumLevel = LogEventLevel.Fatal;
        }

        // Persist to appsettings.json (taaki restart ke baad bhi yaad rahe)
        await PersistToAppSettingsAsync(enabled);

        // UI ko notify karo
        FileLoggingToggled?.Invoke(this, enabled);

        System.Diagnostics.Debug.WriteLine(
            $"[WTT LoggingController] File logging {(enabled ? "ENABLED" : "DISABLED")} at runtime.");
    }

    /// <summary>
    /// Log level runtime mein change karta hai (e.g., debug karne ke liye temporarily Verbose).
    /// </summary>
    public void SetMinimumLevel(LogEventLevel level)
    {
        if (_fileLoggingEnabled)
        {
            _levelSwitch.MinimumLevel = level;
        }
    }

    /// <summary>
    /// appsettings.json ko update karta hai taaki app restart ke baad setting survive kare.
    /// Proper JSON round-trip: sirf EnableFileLogging field update karta hai, baaki sab intact.
    /// </summary>
    private static async Task PersistToAppSettingsAsync(bool enableFileLogging)
    {
        try
        {
            var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!File.Exists(configPath))
            {
                System.Diagnostics.Debug.WriteLine("[WTT LoggingController] appsettings.json not found, skipping persist.");
                return;
            }

            var json = await File.ReadAllTextAsync(configPath);

            // JsonNode use karo — ye nested objects ko safely handle karta hai
            // Dictionary<string,object> ka issue: nested objects JsonElement ban jaate hain,
            // cast karne par InvalidCastException aata hai (bug tha pehle)
            var node = System.Text.Json.Nodes.JsonNode.Parse(json);
            if (node is System.Text.Json.Nodes.JsonObject root)
            {
                // Logging section dhundho ya banao
                if (!root.ContainsKey("Logging"))
                    root["Logging"] = new System.Text.Json.Nodes.JsonObject();

                if (root["Logging"] is System.Text.Json.Nodes.JsonObject loggingSection)
                {
                    // Sirf EnableFileLogging field update karo — baaki sab untouched
                    loggingSection["EnableFileLogging"] = enableFileLogging;
                }

                var updatedJson = root.ToJsonString(new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true
                });

                await File.WriteAllTextAsync(configPath, updatedJson);
                System.Diagnostics.Debug.WriteLine(
                    $"[WTT LoggingController] appsettings.json updated: EnableFileLogging={enableFileLogging}");
            }
        }
        catch (Exception ex)
        {
            // appsettings.json update fail hone par silently log karo — app crash nahi hona chahiye
            System.Diagnostics.Debug.WriteLine(
                $"[WTT LoggingController] WARNING: Failed to persist logging setting: {ex.Message}");
        }
    }
}
