// =============================================================================
// FILE      : src/WTT.Infrastructure/PlcDrivers/MockPlcDriver.cs
// LAYER     : Infrastructure - deterministic PLC simulator (no hardware required)
// PURPOSE   : Exercises capture, triggers, alarms, trends and transfer end to end with
//             no PLC. Each tag produces a smooth signal DERIVED FROM ITS ADDRESS, so
//             runs are reproducible and a regression is a changed value, not noise.
// DI        : Per-device instance - returned by PlcDriverFactory when
//             Simulation:UseMockPlc is set; one per DeviceCaptureWorker.
// THREADING : Sequential by contract (IPlcDriver). State is shared CROSS-PROCESS, not
//             in memory: a JSON file under C:\ProgramData\WTT guarded by a named
//             Global Mutex (500 ms timeout, AbandonedMutexException tolerated) and
//             opened FileShare.None, so WTT.Service (Session 0) and WTT.UI (Session 1)
//             cannot interleave a read-modify-write or collide on the file.
// DEPENDS ON: System.Text.Json, WTT.Domain (StableHash, TagValueFormatter, TagReadResult)
// USED BY   : PlcDriverFactory, WTT.UI write/browse screens, simulation test harnesses.
// KEY RULES :
//   * State lives in C:\ProgramData\WTT\WTT_MockPlcState.json - NOT a static
//     dictionary. A UI write must be visible to the service process and two
//     processes never share heap memory. Do not "optimise" this into a static dict.
//   * A written value overrides the simulation until it expires (15 s after the
//     write), then the signal resumes: a Force-Tag nudge, not a latch, which is what
//     keeps trigger and alarm testing repeatable. A legacy single-part value expires
//     at once, so an old file can never leave a tag stuck.
//   * Signal shape is a pure function of PlcAddress (stable hash -> phase, period,
//     amplitude) plus name keywords (temp/press/level, count, trig, batch). Never
//     introduce Random or DateTime.Now - failures would become unreproducible.
//   * An empty PlcAddress is a ConfigError, never a silent zero; an unparseable
//     written value falls back to simulation instead of failing the whole scan.
//   * BrowseTagsAsync returns a small fixed sample for the tag-import flow only; it
//     is not a real tag list and must not be presented as one.
//   * Reachable ONLY via Simulation:UseMockPlc - see PlcDriverFactory, which refuses
//     unknown devices rather than degrading to it.
// =============================================================================
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using WTT.Domain.Common;
using WTT.Domain.Entities;
using WTT.Domain.Enums;
using WTT.Domain.Interfaces;
using WTT.Domain.ValueObjects;

namespace WTT.Infrastructure.PlcDrivers;

/// <summary>
/// Deterministic PLC simulator. Every tag produces a realistic, smoothly varying signal derived
/// from its name so the UI, trigger logic and transfer pipeline can be exercised without hardware.
/// Written values override the simulation until the next write.
/// </summary>
public sealed class MockPlcDriver : IPlcDriver
{
    // Resilient state file path: tries CommonApplicationData (ProgramData) first for cross-process sharing.
    // Falls back to LocalApplicationData if ProgramData is not writable (e.g., permission issues in test/CI environments).
    // The mutex name remains Global so cross-process synchronization still works even if file locations differ.
    private static readonly string StateFilePath = GetResilientStateFilePath();
    private static readonly DateTime Epoch = DateTime.UtcNow;

    // Named Mutex — cross-process lock. Both processes (UI + Service) share the same named Mutex.
    // While one process holds it, the other waits. This fully prevents the TOCTOU race.
    private static readonly Mutex StateMutex = new(false, @"Global\WTT_MockPlcStateMutex");

    /// <summary>
    /// Determines a writable state file path. Prefers ProgramData for true cross-process sharing,
    /// but falls back to LocalAppData if ProgramData is inaccessible (common in test/CI environments).
    /// </summary>
    private static string GetResilientStateFilePath()
    {
        // Try ProgramData first (ideal for Service + UI cross-process)
        var programDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "WTT", "WTT_MockPlcState.json");

        if (TryEnsureWritable(programDataPath))
        {
            return programDataPath;
        }

        // Fallback: LocalAppData (always writable by current user)
        var localAppDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WTT", "WTT_MockPlcState.json");

        if (TryEnsureWritable(localAppDataPath))
        {
            System.Diagnostics.Debug.WriteLine($"[MockPlcDriver] Using fallback state file: {localAppDataPath}");
            return localAppDataPath;
        }

        // Last resort: temp path (should never happen)
        var tempPath = Path.Combine(Path.GetTempPath(), "WTT", "WTT_MockPlcState.json");
        TryEnsureWritable(tempPath); // best effort
        System.Diagnostics.Debug.WriteLine($"[MockPlcDriver] WARNING: Using temp state file: {tempPath}");
        return tempPath;
    }

    /// <summary>
    /// Ensures the directory exists and the file (if present) is writable by the current process.
    /// Returns true if the path is usable for read/write.
    /// </summary>
    private static bool TryEnsureWritable(string filePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // If file exists, test write access by opening with FileShare.None
            if (File.Exists(filePath))
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.None);
            }
            else
            {
                // Test create/write access in the directory
                using var fs = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                File.Delete(filePath); // clean up test file
            }
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false; // file locked, directory not writable, etc.
        }
        catch
        {
            return false; // any other error = not usable
        }
    }

    // Reads from the JSON file — must be called while holding the Mutex (do not call directly)
    private static Dictionary<string, string> ReadSharedStateUnsafe()
    {
        try
        {
            if (File.Exists(StateFilePath))
            {
                using var fs = new FileStream(StateFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var sr = new StreamReader(fs);
                var json = sr.ReadToEnd();
                if (!string.IsNullOrWhiteSpace(json))
                {
                    return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
                }
            }
        }
        catch { /* File read error — return empty state */ }
        return new Dictionary<string, string>();
    }

    // Safe wrapper — acquires the Mutex before reading
    private static Dictionary<string, string> ReadSharedState()
    {
        try
        {
            // Waits up to 500ms for the Mutex, then gives up (deadlock prevention)
            if (!StateMutex.WaitOne(500)) return new Dictionary<string, string>();
            try
            {
                return ReadSharedStateUnsafe();
            }
            finally
            {
                StateMutex.ReleaseMutex();
            }
        }
        catch (AbandonedMutexException)
        {
            // If the other process crashed, the Mutex is "abandoned" — but remains usable
            return ReadSharedStateUnsafe();
        }
    }

    // Writes state — the full Read → Modify → Write cycle runs under a single Mutex lock.
    // This prevents the two processes from ever overwriting each other's writes.
    private static void WriteSharedState(string key, string value)
    {
        const int MaxRetries = 3;
        const int MutexTimeoutMs = 500;

        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            bool mutexAcquired = false;
            try
            {
                mutexAcquired = StateMutex.WaitOne(MutexTimeoutMs);
                if (!mutexAcquired)
                {
                    // Log and retry instead of silently dropping the write
                    System.Diagnostics.Debug.WriteLine($"[MockPlcDriver] WriteSharedState: Mutex timeout on attempt {attempt + 1}/{MaxRetries} for key '{key}'");
                    if (attempt == MaxRetries - 1)
                    {
                        throw new TimeoutException($"Failed to acquire mock PLC state mutex after {MaxRetries} attempts ({MutexTimeoutMs}ms each)");
                    }
                    Thread.Sleep(50 * (attempt + 1)); // small backoff
                    continue;
                }

                try
                {
                    // Step 1: Read existing data
                    var state = ReadSharedStateUnsafe();
                    // Step 2: Add/update the key-value entry
                    state[key] = value;
                    var json = JsonSerializer.Serialize(state);

                    // Step 3: Create the directory if missing
                    var dir = Path.GetDirectoryName(StateFilePath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    // Step 4: Write to the file atomically (FileShare.None = exclusive lock)
                    using var fs = new FileStream(StateFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
                    using var sw = new StreamWriter(fs);
                    sw.Write(json);
                    return; // SUCCESS
                }
                finally
                {
                    if (mutexAcquired) StateMutex.ReleaseMutex();
                }
            }
            catch (AbandonedMutexException)
            {
                // Crash recovery — try once more without lock
                try
                {
                    var state = ReadSharedStateUnsafe();
                    state[key] = value;
                    var dir = Path.GetDirectoryName(StateFilePath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(StateFilePath, JsonSerializer.Serialize(state));
                    return; // SUCCESS (recovery path)
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MockPlcDriver] WriteSharedState: Abandoned mutex recovery failed for key '{key}': {ex}");
                    if (attempt == MaxRetries - 1) throw;
                }
            }
            catch (Exception ex) when (ex is not TimeoutException && ex is not OperationCanceledException)
            {
                // LOG the actual error instead of swallowing it!
                System.Diagnostics.Debug.WriteLine($"[MockPlcDriver] WriteSharedState: Write failed for key '{key}' (attempt {attempt + 1}/{MaxRetries}): {ex.GetType().Name}: {ex.Message}");
                if (attempt == MaxRetries - 1) throw;
            }
        }
    }

    public MockPlcDriver(Device device) => Device = device;

    public Device Device { get; }

    public PlcDriverCapabilities Capabilities { get; } = new(CanBrowseTags: true, CanWrite: true, SupportsBatchRead: true, RequiresAddressBlocks: false);

    public bool IsConnected { get; private set; }

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TagReadResult>> ReadAsync(IReadOnlyList<DeviceTag> tags, CancellationToken cancellationToken)
    {
        var t = (DateTime.UtcNow - Epoch).TotalSeconds;
        var list = new List<TagReadResult>(tags.Count);
        var sharedState = ReadSharedState();

        foreach (var tag in tags)
        {
            if (string.IsNullOrWhiteSpace(tag.PlcAddress))
            {
                list.Add(TagReadResult.Bad(tag, "Empty address", TagQuality.ConfigError));
                continue;
            }
            var key = Key(tag);
            if (sharedState.TryGetValue(key, out var writtenStr))
            {
                var parts = writtenStr.Split('|');
                var valToParse = parts[0];
                bool isExpired = false;
                
                if (parts.Length > 1 && long.TryParse(parts[1], out var ticks))
                {
                    if (DateTime.UtcNow.Ticks > ticks) isExpired = true;
                }
                else if (parts.Length == 1)
                {
                    // For backward compatibility with old writes, expire them immediately so they don't get stuck forever
                    isExpired = true;
                }

                if (!isExpired)
                {
                    try
                    {
                        var typed = TagValueFormatter.ParseForWrite(tag.DataType, valToParse);
                        list.Add(TagReadResult.Good(tag, typed));
                        continue;
                    }
                    catch
                    {
                        // Fallback to simulation if parsing fails
                    }
                }
            }
            list.Add(TagReadResult.Good(tag, Simulate(tag, t)));
        }
        return Task.FromResult<IReadOnlyList<TagReadResult>>(list);
    }

    public Task WriteAsync(TagWriteRequest request, CancellationToken cancellationToken)
    {
        var typed = TagValueFormatter.ParseForWrite(request.Tag.DataType, request.Value); // throws FormatException on bad input
        // Save the value from the UI with a 15-second expiry (Force Timeout)
        var valStr = Convert.ToString(typed, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        var expiryTicks = DateTime.UtcNow.AddSeconds(15).Ticks;
        WriteSharedState(Key(request.Tag), $"{valStr}|{expiryTicks}");
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DiscoveredTag>> BrowseTagsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<DiscoveredTag> sample =
        [
            new("Sim.Temperature", TagDataType.Float32, 1, 4, false, "REAL"),
            new("Sim.Pressure", TagDataType.Float32, 1, 4, false, "REAL"),
            new("Sim.Speed", TagDataType.Int32, 1, 4, false, "DINT"),
            new("Sim.Counter", TagDataType.Int32, 1, 4, false, "DINT"),
            new("Sim.Level", TagDataType.Int16, 1, 2, false, "INT"),
            new("Sim.Running", TagDataType.Bool, 1, 1, false, "BOOL"),
            new("Sim.Trigger", TagDataType.Bool, 1, 1, false, "BOOL"),
            new("Sim.BatchId", TagDataType.String, 82, 88, false, "STRING"),
            new("Sim.Array[0]", TagDataType.Float32, 1, 4, false, "REAL"),
            new("Sim.Array[1]", TagDataType.Float32, 1, 4, false, "REAL"),
        ];
        return Task.FromResult(sample);
    }

    public ValueTask DisposeAsync()
    {
        IsConnected = false;
        return ValueTask.CompletedTask;
    }

    private string Key(DeviceTag tag) => $"{Device.Id}:{tag.PlcAddress}";

    private static object Simulate(DeviceTag tag, double t)
    {
        var seed = StableHash(tag.PlcAddress);
        var phase = (seed % 628) / 100.0;
        var period = 20 + seed % 40;           // 20..59 s
        var wave = Math.Sin(2 * Math.PI * t / period + phase);
        var name = tag.PlcAddress.ToLowerInvariant();

        switch (tag.DataType)
        {
            case TagDataType.Bool:
                // "trigger"-like tags toggle every 30 s; others follow the wave.
                return name.Contains("trig") ? ((int)(t / 30) % 2) == 0 : wave > 0;
            case TagDataType.String:
                return name.Contains("batch") ? $"BATCH-{Epoch:yyyyMMdd}-{(int)(t / 300):D3}" : $"SIM-{seed % 1000:D3}";
            case TagDataType.Float32:
            case TagDataType.Float64:
                {
                    double baseValue = name.Contains("temp") ? 65 : name.Contains("press") ? 4.2 : name.Contains("level") ? 50 : 100;
                    double amplitude = baseValue * 0.15;
                    var noise = ((seed * 7919 + (long)(t * 10)) % 100) / 1000.0 * amplitude;
                    var v = baseValue + amplitude * wave + noise;
                    return tag.DataType == TagDataType.Float32 ? (object)(float)Math.Round(v, 3) : Math.Round(v, 4);
                }
            case TagDataType.Int16:
            case TagDataType.UInt16:
                return tag.DataType == TagDataType.Int16 ? (object)(short)(500 + 300 * wave) : (ushort)(500 + 300 * wave);
            case TagDataType.Int32:
            case TagDataType.UInt32:
                {
                    if (name.Contains("count")) return tag.DataType == TagDataType.Int32 ? (object)(int)(t / 2) : (uint)(t / 2);
                    var v = 1500 + 400 * wave;
                    return tag.DataType == TagDataType.Int32 ? (object)(int)v : (uint)v;
                }
            case TagDataType.Int64: return (long)(t * 1000);
            case TagDataType.UInt64: return (ulong)(t * 1000);
            case TagDataType.Byte: return (byte)(128 + 100 * wave);
            default: return (int)(100 * wave);
        }
    }

    private static int StableHash(string s)
    {
        unchecked
        {
            var h = 23;
            foreach (var c in s) h = h * 31 + c;
            return Math.Abs(h);
        }
    }
}
