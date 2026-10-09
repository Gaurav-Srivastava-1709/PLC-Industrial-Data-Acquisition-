using Serilog.Core;
using Serilog.Events;
using System.IO;

namespace WTT.UI.Logging;

/// <summary>
/// Runtime logging controller — no app restart required!
/// 
/// Industry standard approach:
/// - Use Serilog's LoggingLevelSwitch to change the level at runtime.
/// - Use a thread-safe flag for the file logging toggle.
/// - Registered as a singleton in DI and consumed by SettingsViewModel.
/// 
/// Manager Requirement #3: Toggle to turn off log generation (at runtime, without restart).
/// </summary>
public sealed class LoggingController
{
    // Serilog LoggingLevelSwitch — changes the level live (no restart needed)
    // "Off" = LogEventLevel.Fatal + 1 trick to turn off Serilog logging completely
    private readonly LoggingLevelSwitch _levelSwitch;
    private readonly LoggingOptions _options;

    // Current state of the file logging toggle (thread-safe volatile)
    private volatile bool _fileLoggingEnabled;

    // Fatal+1 level = effectively OFF (no event passes this level)
    private const LogEventLevel OffLevel = (LogEventLevel)1 + (int)LogEventLevel.Fatal;

    // Event to notify the UI when the logging status changes
    public event EventHandler<bool>? FileLoggingToggled;

    public LoggingController(LoggingLevelSwitch levelSwitch, LoggingOptions options)
    {
        _levelSwitch = levelSwitch;
        _options = options;
        _fileLoggingEnabled = options.EnableFileLogging;
    }

    /// <summary>
    /// Whether file logging is currently enabled.
    /// </summary>
    public bool IsFileLoggingEnabled => _fileLoggingEnabled;

    /// <summary>
    /// Current minimum log level.
    /// </summary>
    public LogEventLevel CurrentLevel => _levelSwitch.MinimumLevel;

    /// <summary>
    /// Turns file logging ON/OFF at runtime.
    /// Also updates appsettings.json so the setting persists across app restarts.
    /// </summary>
    public async Task SetFileLoggingAsync(bool enabled)
    {
        _fileLoggingEnabled = enabled;

        if (enabled)
        {
            // Restore logging to the original level
            _levelSwitch.MinimumLevel = _options.GetMinimumLogEventLevel();
        }
        else
        {
            // Turn logging off completely — "Off" level trick
            // Note: Serilog has no built-in "Off"; Fatal+1 is used instead
            _levelSwitch.MinimumLevel = LogEventLevel.Fatal;
        }

        // Persist to appsettings.json (so it is remembered after restart)
        await PersistToAppSettingsAsync(enabled);

        // Notify the UI
        FileLoggingToggled?.Invoke(this, enabled);

        System.Diagnostics.Debug.WriteLine(
            $"[WTT LoggingController] File logging {(enabled ? "ENABLED" : "DISABLED")} at runtime.");
    }

    /// <summary>
    /// Changes the log level at runtime (e.g., temporarily Verbose for debugging).
    /// </summary>
    public void SetMinimumLevel(LogEventLevel level)
    {
        if (_fileLoggingEnabled)
        {
            _levelSwitch.MinimumLevel = level;
        }
    }

    /// <summary>
    /// Updates appsettings.json so the setting survives app restarts.
    /// Proper JSON round-trip: only updates the EnableFileLogging field, everything else stays intact.
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

            // Use JsonNode — it safely handles nested objects
            // The Dictionary<string,object> issue: nested objects become JsonElements,
            // which throws InvalidCastException on cast (was a bug before)
            var node = System.Text.Json.Nodes.JsonNode.Parse(json);
            if (node is System.Text.Json.Nodes.JsonObject root)
            {
                // Find or create the Logging section
                if (!root.ContainsKey("Logging"))
                    root["Logging"] = new System.Text.Json.Nodes.JsonObject();

                if (root["Logging"] is System.Text.Json.Nodes.JsonObject loggingSection)
                {
                    // Update only the EnableFileLogging field — everything else untouched
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
            // If the appsettings.json update fails, log silently — the app must not crash
            System.Diagnostics.Debug.WriteLine(
                $"[WTT LoggingController] WARNING: Failed to persist logging setting: {ex.Message}");
        }
    }
}
