using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WTT.Application.Interfaces;
using WTT.Application.Options;
using WTT.Domain.Entities;
using WTT.Infrastructure.Persistence;

namespace WTT.Infrastructure.Services;

public sealed class AppSettingsService : IAppSettingsService
{
    private readonly IDbContextFactory<AppDbContext> _factory;

    public AppSettingsService(IDbContextFactory<AppDbContext> factory) => _factory = factory;

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.AppSettings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync(ct);
    }

    public async Task<T> GetAsync<T>(string key, T defaultValue, CancellationToken ct = default) where T : IParsable<T>
    {
        var raw = await GetAsync(key, ct);
        return raw is not null && T.TryParse(raw, CultureInfo.InvariantCulture, out var parsed) ? parsed : defaultValue;
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var entity = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (entity is null) db.AppSettings.Add(new AppSetting { Key = key, Value = value });
        else { entity.Value = value; entity.UpdatedAtUtc = DateTime.UtcNow; }
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.AppSettings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, ct);
    }
}

/// <summary>
/// Cached snapshot of DB-backed runtime flags. Background services read properties (cheap);
/// the UI calls <see cref="ReloadAsync"/> after changing a setting.
/// </summary>
public sealed class RuntimeSettings : IRuntimeSettings
{
    private readonly IAppSettingsService _settings;
    private readonly SimulationOptions _sim;
    private readonly SecurityOptions _security;
    private readonly CaptureOptions _capture;
    private readonly TransferOptions _transfer;
    private readonly ILogger<RuntimeSettings> _logger;

    public RuntimeSettings(IAppSettingsService settings, IOptions<SimulationOptions> sim, IOptions<SecurityOptions> security,
        IOptions<CaptureOptions> capture, IOptions<TransferOptions> transfer, ILogger<RuntimeSettings> logger)
    {
        _settings = settings;
        _sim = sim.Value;
        _security = security.Value;
        _capture = capture.Value;
        _transfer = transfer.Value;
        _logger = logger;

        UseMockPlc = _sim.UseMockPlc;
        SessionTimeoutMinutes = _security.SessionTimeoutMinutes;
        TransferredRetentionDays = _capture.TransferredRetentionDays;
        TransferIntervalSeconds = _transfer.IntervalSeconds;
    }

    public bool UseMockPlc { get; private set; }
    public int SessionTimeoutMinutes { get; private set; }
    public int TransferredRetentionDays { get; private set; }
    public int TransferIntervalSeconds { get; private set; }

    public event EventHandler? Changed;

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        UseMockPlc = await _settings.GetAsync(AppSettingKeys.UseMockPlc, _sim.UseMockPlc, ct);
        SessionTimeoutMinutes = Math.Clamp(await _settings.GetAsync(AppSettingKeys.SessionTimeoutMinutes, _security.SessionTimeoutMinutes, ct), 1, 720);
        TransferredRetentionDays = Math.Clamp(await _settings.GetAsync(AppSettingKeys.TransferredRetentionDays, _capture.TransferredRetentionDays, ct), 1, 3650);
        TransferIntervalSeconds = Math.Clamp(await _settings.GetAsync(AppSettingKeys.TransferIntervalSeconds, _transfer.IntervalSeconds, ct), 1, 3600);
        _logger.LogInformation("Runtime settings loaded: MockPlc={Mock}, SessionTimeout={Session}m, Retention={Ret}d, TransferInterval={Ti}s",
            UseMockPlc, SessionTimeoutMinutes, TransferredRetentionDays, TransferIntervalSeconds);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
