using System.Globalization;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WTT.Application.Interfaces;
using WTT.Application.Options;
using WTT.Infrastructure.Services;
using Xunit;

namespace WTT.Tests;

public sealed class RuntimeSettingsTests
{
    [Fact]
    public async Task Changed_is_raised_only_when_values_change()
    {
        var store = new FakeSettings { [AppSettingKeys.UseMockPlc] = "true" };
        var runtime = new RuntimeSettings(store, Options.Create(new SimulationOptions()), Options.Create(new SecurityOptions()), Options.Create(new CaptureOptions()), Options.Create(new TransferOptions()), NullLogger<RuntimeSettings>.Instance);
        var changes = 0;
        runtime.Changed += (_, _) => changes++;

        await runtime.ReloadAsync();
        await runtime.ReloadAsync();
        changes.Should().Be(1);

        store[AppSettingKeys.UseMockPlc] = "false";
        await runtime.ReloadAsync();
        changes.Should().Be(2);
    }

    private sealed class FakeSettings : Dictionary<string, string>, IAppSettingsService
    {
        public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(TryGetValue(key, out var value) ? value : null);
        public async Task<T> GetAsync<T>(string key, T defaultValue, CancellationToken ct = default) where T : IParsable<T>
        {
            var raw = await GetAsync(key, ct);
            return raw is not null && T.TryParse(raw, CultureInfo.InvariantCulture, out var parsed) ? parsed : defaultValue;
        }
        public Task SetAsync(string key, string value, CancellationToken ct = default) { this[key] = value; return Task.CompletedTask; }
        public Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyDictionary<string, string>>(this);
    }
}
