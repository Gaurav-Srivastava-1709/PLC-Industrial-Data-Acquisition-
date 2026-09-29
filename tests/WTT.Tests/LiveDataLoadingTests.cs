using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using WTT.Application.Interfaces;
using WTT.Application.Security;
using WTT.Domain.Entities;
using WTT.Domain.Enums;
using WTT.Domain.ValueObjects;
using WTT.UI.Services;
using WTT.UI.ViewModels;
using Xunit;

namespace WTT.Tests;

/// <summary>
/// Guards the Live Data page's initial load.
/// <para>
/// Regression context: <c>ViewModelBase.RunAsync</c> drops re-entrant work
/// (<c>if (IsBusy) return false;</c>). <c>OnNavigatedToAsync</c> used to assign
/// <c>SelectedDevice</c> from inside its own <c>RunAsync</c> block. That assignment
/// synchronously fired <c>OnSelectedDeviceChanged</c>, which called
/// <c>RunAsync(LoadTagsAsync)</c> into the re-entrancy guard, so the first tag load was
/// discarded silently - no exception, no log - and the grid only filled on the first
/// 2 s status-timer tick. <c>OnNavigatedToAsync</c> now assigns the device after the busy
/// block has exited.
/// </para>
/// <para>
/// The first test here is the one that actually pins the behaviour: it asserts the tag
/// query is issued during navigation, and <c>FirstLoadCompletesWithinNavigation</c>
/// fails against the old ordering.
/// </para>
/// </summary>
public sealed class LiveDataLoadingTests
{
    // ---------------------------------------------------------------- helpers

    private sealed record Rig(
        LiveDataViewModel Vm,
        IDeviceService Devices,
        ITagService Tags,
        ICaptureRuntimeStatusReader Snapshot,
        IDialogService Dialogs);

    private static Rig Build(
        IReadOnlyList<Device>? devices = null,
        Func<int, IReadOnlyList<DeviceTag>>? tags = null,
        bool deviceListThrows = false,
        bool tagsThrow = false,
        bool snapshotThrows = false)
    {
        var user = Substitute.For<ICurrentUserService>();
        user.HasPermission(Arg.Any<Permission>()).Returns(true);

        var deviceSvc = Substitute.For<IDeviceService>();
        var tagSvc = Substitute.For<ITagService>();
        var snapshotSvc = Substitute.For<ICaptureRuntimeStatusReader>();
        var dialogs = Substitute.For<IDialogService>();

        deviceSvc.GetAllAsync(Arg.Any<CancellationToken>()).Returns(_ =>
            deviceListThrows
                ? throw new InvalidOperationException("device query failed")
                : Task.FromResult<IReadOnlyList<Device>>(devices ?? new List<Device>()));

        tagSvc.GetForDeviceAsync(Arg.Any<int>(), Arg.Any<TagKind?>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            if (tagsThrow) throw new InvalidOperationException("tag query failed");
            return Task.FromResult(tags!((int)ci[0]!));
        });

        snapshotSvc.GetSnapshotAsync(Arg.Any<CancellationToken>()).Returns(_ =>
            snapshotThrows
                ? throw new InvalidOperationException("snapshot failed")
                : Task.FromResult(HealthySnapshot()));

        // CsvExportService is sealed and irrelevant to loading, so it is left null.
        // The WriteTagViewModel factory is never invoked by these paths.
        var vm = new LiveDataViewModel(
            deviceSvc, tagSvc, snapshotSvc, user, new NavigationContext(),
            () => throw new NotSupportedException("write dialog is not exercised here"), null!, dialogs,
            NullLogger<LiveDataViewModel>.Instance);

        return new Rig(vm, deviceSvc, tagSvc, snapshotSvc, dialogs);
    }

    private static CaptureRuntimeSnapshot HealthySnapshot() => new(
        StatusAvailable: true, IsServiceRunning: true, ServiceInstanceId: Guid.NewGuid(),
        ServiceStartedAtUtc: DateTime.UtcNow, LastHeartbeatUtc: DateTime.UtcNow, ServiceError: null,
        Devices: new List<DeviceCaptureStatus>
        {
            new(1, "PLC", true, true, true, false, true, DateTime.UtcNow, DateTime.UtcNow, 10, 20, 0, null, 0),
        });

    private static Device Device(int id, string name) => new()
    {
        Id = id, Name = name, Protocol = ProtocolType.ModbusTcp, Manufacturer = Manufacturer.Generic,
        Model = DeviceModel.GenericModbusTcp, Host = "127.0.0.1", Port = 502,
        PollIntervalMs = 1000, IsEnabled = true,
    };

    private static DeviceTag Tag(int id, int deviceId, string name, string? value = "42.5") => new()
    {
        Id = id, DeviceId = deviceId, Name = name, IsSelected = true, Kind = TagKind.Plc,
        DataType = TagDataType.Float32, CurrentValue = value,
        CurrentQuality = TagQuality.Good, CurrentValueAtUtc = DateTime.UtcNow,
    };

    private static int TagQueryCount(ITagService tags) =>
        tags.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(ITagService.GetForDeviceAsync));

    // ------------------------------------------------------- the regression

    [Fact]
    public void FirstLoadCompletesWithinNavigation_WithoutWaitingForTheStatusTimer()
    {
        Sta.Run(() =>
        {
            var rig = Build([Device(1, "ManualTestPLC")], id => [Tag(1, id, "TEMP001")]);

            rig.Vm.OnNavigatedToAsync().GetAwaiter().GetResult();

            // Before the fix this was 0: the load was swallowed by the re-entrancy
            // guard and the grid stayed empty until the first 2 s timer tick.
            TagQueryCount(rig.Tags).Should().Be(1,
                "the first tag load must run during navigation, not on a later timer tick");
            rig.Vm.Rows.Should().HaveCount(1);
            rig.Vm.Rows[0].Value.Should().Be("42.5");
            rig.Vm.Rows[0].Name.Should().Be("TEMP001");
        });
    }

    [Fact]
    public void FirstLoad_ResolvesStatusTextInTheSamePass()
    {
        Sta.Run(() =>
        {
            var rig = Build([Device(1, "PLC")], id => [Tag(1, id, "T")]);

            rig.Vm.OnNavigatedToAsync().GetAwaiter().GetResult();

            // RefreshStatus is awaited by LoadTagsAsync, so the status line is already
            // resolved by the time the load finishes rather than arriving later.
            rig.Vm.StatusText.Should().Contain("scans");
            rig.Vm.GoodCount.Should().Be(1);
            rig.Vm.BadCount.Should().Be(0);
        });
    }

    [Fact]
    public void IsInitialLoad_IsClearedAfterTheFirstLoad()
    {
        Sta.Run(() =>
        {
            var rig = Build([Device(1, "PLC")], id => [Tag(1, id, "T")]);

            rig.Vm.OnNavigatedToAsync().GetAwaiter().GetResult();

            // The loading overlay binds to IsInitialLoad AND IsBusy. If this stayed true
            // the spinner would keep reappearing on every 2 s refresh.
            rig.Vm.IsInitialLoad.Should().BeFalse();
            rig.Vm.IsBusy.Should().BeFalse();
        });
    }

    [Fact]
    public void RowsAreSortedAndFilterable()
    {
        Sta.Run(() =>
        {
            var tags = new List<DeviceTag> { Tag(1, 1, "TEMP001"), Tag(2, 1, "TEMP002"), Tag(3, 1, "TEMP003") };
            var rig = Build([Device(1, "PLC")], _ => tags);

            rig.Vm.OnNavigatedToAsync().GetAwaiter().GetResult();

            rig.Vm.Rows.Select(r => r.Name).Should().Equal("TEMP001", "TEMP002", "TEMP003");

            rig.Vm.Filter = "TEMP002";
            Sta.Pump(50);
            rig.Vm.Rows.Should().ContainSingle().Which.Name.Should().Be("TEMP002");

            rig.Vm.Filter = string.Empty;
            Sta.Pump(50);
            rig.Vm.Rows.Should().HaveCount(3);
        });
    }

    // -------------------------------------------------------- failure paths

    [Fact]
    public void NoDevices_DoesNotQueryTagsAndLeavesNoStuckState()
    {
        Sta.Run(() =>
        {
            var rig = Build([]);

            rig.Vm.OnNavigatedToAsync().GetAwaiter().GetResult();

            rig.Vm.SelectedDevice.Should().BeNull();
            rig.Vm.Rows.Should().BeEmpty();
            TagQueryCount(rig.Tags).Should().Be(0);
            rig.Vm.IsBusy.Should().BeFalse();
            rig.Vm.IsInitialLoad.Should().BeFalse();
        });
    }

    [Fact]
    public void DeviceWithNoSelectedTags_LeavesNoStuckInitialLoadFlag()
    {
        Sta.Run(() =>
        {
            // A device whose tags are all deselected keeps Rows permanently empty. An
            // overlay keyed on "Rows.Count == 0" would therefore flash on every 2 s
            // refresh; this asserts the flag-based condition does not.
            var rig = Build([Device(1, "PLC")], _ => new List<DeviceTag>());

            rig.Vm.OnNavigatedToAsync().GetAwaiter().GetResult();

            rig.Vm.Rows.Should().BeEmpty();
            rig.Vm.IsInitialLoad.Should().BeFalse("otherwise the spinner would strobe forever on a tagless device");
            rig.Vm.IsBusy.Should().BeFalse();
        });
    }

    [Fact]
    public void DeviceListFailure_DoesNotHangOrLeaveBusyState()
    {
        Sta.Run(() =>
        {
            var rig = Build(deviceListThrows: true);

            var act = () => rig.Vm.OnNavigatedToAsync().GetAwaiter().GetResult();

            act.Should().NotThrow();
            rig.Vm.IsBusy.Should().BeFalse();
            rig.Vm.IsInitialLoad.Should().BeFalse();
            rig.Vm.SelectedDevice.Should().BeNull();
        });
    }

    [Fact]
    public void TagLoadFailure_ResetsBusyAndSurfacesAHandledError()
    {
        Sta.Run(() =>
        {
            var rig = Build([Device(1, "PLC")], tagsThrow: true);

            var act = () => rig.Vm.OnNavigatedToAsync().GetAwaiter().GetResult();

            act.Should().NotThrow();
            // A stuck IsBusy would leave the loading overlay visible forever.
            rig.Vm.IsBusy.Should().BeFalse();
            rig.Vm.Rows.Should().BeEmpty();
            rig.Dialogs.ReceivedCalls()
                .Should().Contain(c => c.GetMethodInfo().Name.Contains("ShowError", StringComparison.Ordinal),
                    "the failure must reach the user as a handled dialog, not vanish");
        });
    }

    [Fact]
    public void StatusSnapshotFailure_DoesNotFaultTheProcess()
    {
        // RefreshStatus used to be `async void`, so a throw here had no handler and
        // surfaced as an unhandled Dispatcher exception. It is an awaited Task now, so
        // the failure is caught by RunAsync like any other load error.
        Sta.Run(() =>
        {
            var rig = Build([Device(1, "PLC")], id => [Tag(1, id, "T")], snapshotThrows: true);

            var act = () => rig.Vm.OnNavigatedToAsync().GetAwaiter().GetResult();

            act.Should().NotThrow();
            rig.Vm.IsBusy.Should().BeFalse();
            rig.Vm.Rows.Should().ContainSingle("the tag rows still load when only the status read fails");
        });
    }

    [Fact]
    public void NavigatingAway_StopsTheStatusTimerAndReentrancyIsNotRequired()
    {
        Sta.Run(() =>
        {
            var rig = Build([Device(1, "PLC")], id => [Tag(1, id, "T")]);

            rig.Vm.OnNavigatedToAsync().GetAwaiter().GetResult();
            rig.Vm.OnNavigatedFromAsync().GetAwaiter().GetResult();

            // Leaving the page must not leave a timer ticking against a detached view.
            Sta.Pump(120);
            rig.Vm.IsBusy.Should().BeFalse();
            rig.Vm.Rows.Should().ContainSingle();
        });
    }
}
