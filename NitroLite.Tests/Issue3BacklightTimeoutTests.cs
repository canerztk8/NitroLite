using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Core.Hardware.Wmi;
using NitroLite.Desktop.ViewModels;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Issue #3, item D: an Acer Nitro AN16-61 on BIOS 1.53, no Acer service, direct-WMI helper running as administrator.
/// The capability list said "Bilinmiyor" for the backlight timeout and nothing else, while NitroSense controls it.
/// </summary>
/// <remarks>
/// <para>
/// The report's own tables show where the "unknown" came from: the helper was connected at start-up (the log has
/// "--wmi-worker connected" and "Direct WMI was enabled at startup"), the modes mask <c>0x7300</c> came back, and the
/// <c>APGeAction</c> line said "present" — which the report derives from CoolBoost or the timeout. So the class answered,
/// and the timeout selector itself gave nothing this build decodes, on both selectors it tries.
/// </para>
/// <para>
/// Which selector that board uses is not in the repository, so no write is enabled here. What changes is that the
/// worker keeps the raw answers for the log, and the row says which kind of unknown it is. Nothing here touches the
/// hardware: the selector reads are delegates and the backend is a fake.
/// </para>
/// </remarks>
public class Issue3BacklightTimeoutTests
{
    // =============================================================================================================
    // The worker's read keeps what the firmware answered.
    // =============================================================================================================

    [Fact]
    public void ReadTimeout_RecordsBothRawAnswers_WhenNeitherSelectorDecodes()
    {
        var answers = new List<string>();

        // 0x84 answers something that is neither of Linuwu-Sense's two replies; selector 0 answers a status byte.
        var state = AcerWmiExecutor.ReadTimeout(input => input == 0x88401UL ? 0x3UL : 0x1UL, answers);

        Assert.Null(state);
        Assert.Equal(new[] { "GetFunction(0x88401) -> 0x3", "GetFunction(0x80001) -> 0x1" }, answers);
    }

    [Fact]
    public void ReadTimeout_RecordsANullAnswerAsNoAnswer()
    {
        var answers = new List<string>();

        Assert.Null(AcerWmiExecutor.ReadTimeout(_ => null, answers));
        Assert.Equal(new[] { "GetFunction(0x88401) -> no answer", "GetFunction(0x80001) -> no answer" }, answers);
    }

    [Fact]
    public void ReadTimeout_RecordsTheDedicatedSelectorThrowing_AndStillAsksTheCombinedOne()
    {
        var answers = new List<string>();

        var state = AcerWmiExecutor.ReadTimeout(input => input == 0x88401UL
            ? throw new System.Management.ManagementException("refused")
            : 0x2UL, answers);

        Assert.Null(state);
        Assert.Equal(2, answers.Count);
        Assert.StartsWith("GetFunction(0x88401) threw ", answers[0], StringComparison.Ordinal);
        Assert.Equal("GetFunction(0x80001) -> 0x2", answers[1]);
    }

    [Fact]
    public void ReadTimeout_RecordsTheCombinedSelectorThrowing_AndStillLetsTheCallerHandleIt()
    {
        // The executor's own handler decides whether the provider is wedged, so the exception is not swallowed.
        var answers = new List<string>();

        Assert.Throws<InvalidOperationException>(() => AcerWmiExecutor.ReadTimeout(
            input => input == 0x88401UL ? 0x3UL : throw new InvalidOperationException("boom"), answers));

        Assert.Equal("GetFunction(0x88401) -> 0x3", answers[0]);
        Assert.StartsWith("GetFunction(0x80001) threw InvalidOperationException", answers[1], StringComparison.Ordinal);
    }

    [Fact]
    public void ReadTimeout_ADecodedAnswerIsUnchanged_ByRecordingTheAnswers()
    {
        var answers = new List<string>();

        var dedicated = AcerWmiExecutor.ReadTimeout(_ => 0x1E0000080000UL, answers);
        Assert.NotNull(dedicated);
        Assert.True(dedicated.Enabled);
        Assert.Equal(0x84, dedicated.Selector);
        Assert.Empty(answers);

        var combined = AcerWmiExecutor.ReadTimeout(input => input == 0x80001UL ? (30UL << 40) | (50UL << 32) : 0x3UL, answers);
        Assert.NotNull(combined);
        Assert.True(combined.Enabled);
        Assert.Equal(0, combined.Selector);
        Assert.Equal(50, combined.Brightness);
    }

    [Fact]
    public void Controller_ReadsTheWorkersDetail_AndTreatsAMissingOrBlankOneAsNone()
    {
        Assert.Equal("No readable backlight timeout selector: GetFunction(0x88401) -> 0x3",
            WmiHardwareController.ReadBacklightTimeoutDetail(new JsonObject
            {
                ["backlightTimeoutDetail"] = "No readable backlight timeout selector: GetFunction(0x88401) -> 0x3"
            }));

        Assert.Null(WmiHardwareController.ReadBacklightTimeoutDetail(new JsonObject()));
        Assert.Null(WmiHardwareController.ReadBacklightTimeoutDetail(new JsonObject { ["backlightTimeoutDetail"] = null }));
        Assert.Null(WmiHardwareController.ReadBacklightTimeoutDetail(new JsonObject { ["backlightTimeoutDetail"] = " " }));
        // An older worker that sent something else is not an answer.
        Assert.Null(WmiHardwareController.ReadBacklightTimeoutDetail(new JsonObject { ["backlightTimeoutDetail"] = 3 }));
    }

    // =============================================================================================================
    // The capability row says which kind of unknown it is.
    // =============================================================================================================

    /// <summary>The AN16-61's answer: the helper connected, APGeAction present, the timeout selector unreadable.</summary>
    private static readonly HardwareCapabilities An16_61 = new()
    {
        Backend = "WMI",
        ServiceAvailable = true,
        FanControl = true,
        OperatingModeControl = true,
        KeyboardLighting = true,
        CoolBoost = true,
        WinKeyLock = true,
        BacklightTimeout = false
    };

    [Fact]
    public async Task An16_61_TimeoutRow_SaysTheFirmwareDidNotAnswer_AndTheSwitchStaysHidden()
    {
        var hardware = new FakeController { IsAvailable = true, Capabilities = An16_61 };
        using var vm = CreateViewModel(hardware, new FakeSnapshot { NegativeAnswersAreConclusive = false });

        await vm.InitializeAsync();

        var row = Row(vm, vm.Labels.CapBacklightTimeout);
        Assert.True(row.Unknown);
        Assert.Equal("?", row.Glyph);
        Assert.Equal(CapabilityUnknownReason.FirmwareDidNotAnswer, row.Reason);
        Assert.Equal(Loc.T("Bilinmiyor (aygıt yazılımı yanıt vermedi)"), row.StateText);

        // No write path on a guess: the selector this board uses is not known.
        Assert.False(vm.BacklightTimeoutAvailable);
        Assert.Equal(0, hardware.SetBacklightTimeoutCalls);
    }

    [Fact]
    public async Task TimeoutRow_WithoutTheApgeClass_StaysAPlainUnknown()
    {
        // CoolBoost false means the class itself was not found; that is not "the selector did not answer".
        var hardware = new FakeController { IsAvailable = true, Capabilities = An16_61 with { CoolBoost = false } };
        using var vm = CreateViewModel(hardware, new FakeSnapshot { NegativeAnswersAreConclusive = false });

        await vm.InitializeAsync();

        var row = Row(vm, vm.Labels.CapBacklightTimeout);
        Assert.True(row.Unknown);
        Assert.Equal(CapabilityUnknownReason.None, row.Reason);
        Assert.Equal(Loc.T("Bilinmiyor"), row.StateText);
    }

    [Fact]
    public async Task OtherUnknownRows_OnAConnectedBackend_KeepThePlainUnknown()
    {
        var hardware = new FakeController { IsAvailable = true, Capabilities = An16_61 };
        using var vm = CreateViewModel(hardware, new FakeSnapshot { NegativeAnswersAreConclusive = false });

        await vm.InitializeAsync();

        var mux = Row(vm, vm.Labels.CapMux);
        Assert.True(mux.Unknown);
        Assert.Equal(Loc.T("Bilinmiyor"), mux.StateText);
        Assert.True(Row(vm, vm.Labels.CapFanControl).Ok);
    }

    [Fact]
    public async Task HelperNotConnectedAtStart_RowsSayNotConnected_ThenARescanShowsTheRealAnswer()
    {
        // A session whose helper did not come up: the backend answered nothing, so every row is "could not ask".
        var hardware = new FakeController
        {
            IsAvailable = false,
            Capabilities = new HardwareCapabilities { Backend = "WMI", ServiceAvailable = false }
        };
        using var vm = CreateViewModel(hardware, new FakeSnapshot { NegativeAnswersAreConclusive = false },
            rescan: new FakeRescan());

        await vm.InitializeAsync();

        Assert.All(vm.CapabilityRows, row =>
        {
            Assert.True(row.Unknown);
            Assert.Equal(CapabilityUnknownReason.NotConnected, row.Reason);
            Assert.Equal(Loc.T("Bilinmiyor (donanıma bağlanılamadı)"), row.StateText);
        });

        // The helper comes up and the capabilities are asked again.
        hardware.IsAvailable = true;
        hardware.Capabilities = An16_61 with { BacklightTimeout = true };
        await vm.RescanCommand.ExecuteAsync(null);

        var timeout = Row(vm, vm.Labels.CapBacklightTimeout);
        Assert.True(timeout.Ok);
        Assert.Equal(Loc.T("Destekleniyor"), timeout.StateText);
        Assert.True(vm.BacklightTimeoutAvailable);
        Assert.Equal(CapabilityUnknownReason.None, Row(vm, vm.Labels.CapMux).Reason);
    }

    [Fact]
    public async Task ConclusivePlatform_StillDrawsACross_WithNoReason()
    {
        // Linux: a false answer is a real answer, so nothing about the reason may leak into the row.
        var hardware = new FakeController { IsAvailable = true, Capabilities = An16_61 };
        using var vm = CreateViewModel(hardware, new FakeSnapshot { NegativeAnswersAreConclusive = true });

        await vm.InitializeAsync();

        var row = Row(vm, vm.Labels.CapBacklightTimeout);
        Assert.True(row.No);
        Assert.Equal(CapabilityUnknownReason.None, row.Reason);
        Assert.Equal(Loc.T("Desteklenmiyor"), row.StateText);
    }

    [Fact]
    public void CapabilityRow_IgnoresAReason_OnAKnownRow()
    {
        Assert.Equal(Loc.T("Destekleniyor"),
            new CapabilityRow("x", Supported: true, Reason: CapabilityUnknownReason.NotConnected).StateText);
        Assert.Equal(Loc.T("Desteklenmiyor"),
            new CapabilityRow("x", Supported: false, Reason: CapabilityUnknownReason.FirmwareDidNotAnswer).StateText);
    }

    // =============================================================================================================
    // Fixtures.
    // =============================================================================================================

    private static CapabilityRow Row(MainViewModel vm, string label) => vm.CapabilityRows.Single(r => r.Label == label);

    private static MainViewModel CreateViewModel(IHardwareController hardware, IPlatformSnapshot platform,
        IRescannableBackend? rescan = null) =>
        new(new AppSettings(), hardware, platform,
            Path.Combine(Path.GetTempPath(), $"nitrolite_issue3_{Guid.NewGuid():N}.json"),
            rescan: rescan, powerState: () => (true, 80));

    private sealed class FakeController : IHardwareController
    {
        public string BackendName => "WMI";
        public bool IsAvailable { get; set; }
        public HardwareCapabilities Capabilities { get; set; } = HardwareCapabilities.Unknown;
        public int SetBacklightTimeoutCalls { get; private set; }

        public Task<bool> EnsureConnectedAsync() => Task.FromResult(IsAvailable);
        public Task<HardwareCapabilities> GetCapabilitiesAsync() => Task.FromResult(Capabilities);
        public Task<MonitorData?> GetMonitorDataAsync() => Task.FromResult<MonitorData?>(null);
        public Task<OperatingMode?> GetOperatingModeAsync() => Task.FromResult<OperatingMode?>(null);
        public Task<bool> SetOperatingModeAsync(OperatingMode mode) => Task.FromResult(false);
        public Task<GpuOperatingMode?> GetGpuModeAsync() => Task.FromResult<GpuOperatingMode?>(null);
        public Task<bool> SetGpuModeAsync(GpuOperatingMode mode) => Task.FromResult(false);
        public Task<int?> GetGpuModeCapabilityAsync() => Task.FromResult<int?>(null);
        public Task<FanMode?> GetFanModeAsync() => Task.FromResult<FanMode?>(null);
        public Task<bool> SetFanModeAsync(FanMode mode, int cpuPercent = -1, int gpuPercent = -1) => Task.FromResult(false);
        public Task<LightingConfig?> GetLightingConfigAsync() => Task.FromResult<LightingConfig?>(null);
        public Task<bool> SetLightingAsync(string effect, int brightness, int speed, int direction, string primaryColor, string[]? zoneColors) => Task.FromResult(false);
        public Task<bool> TurnOffLightingAsync() => Task.FromResult(false);
        public Task<bool?> GetBacklightTimeoutAsync() => Task.FromResult<bool?>(Capabilities.BacklightTimeout ? false : null);

        public Task<bool> SetBacklightTimeoutAsync(bool enable30s)
        {
            SetBacklightTimeoutCalls++;
            return Task.FromResult(false);
        }

        public Task<BatteryChargeMode?> GetBatteryChargeModeAsync() => Task.FromResult<BatteryChargeMode?>(null);
        public Task<bool> SetBatteryChargeModeAsync(BatteryChargeMode mode) => Task.FromResult(false);
        public Task<bool?> GetCoolBoostAsync() => Task.FromResult<bool?>(null);
        public Task<bool> SetCoolBoostAsync(bool on) => Task.FromResult(false);
        public Task<bool> SetWinKeyLockAsync(bool enabled) => Task.FromResult(false);
        public void Dispose() { }
    }

    private sealed class FakeSnapshot : IPlatformSnapshot
    {
        public DeviceIdentity Identity { get; init; } = new() { Manufacturer = "Acer", Model = "Nitro AN16-61" };
        public string Tier { get; init; } = "wmi";
        public bool NeedsUdevRule { get; init; }
        public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
        public bool NegativeAnswersAreConclusive { get; init; }
        public string BuildHardwareReport(HardwareReportContext? context = null) => "";
    }

    private sealed class FakeRescan : IRescannableBackend
    {
        public void ResetReadBackCounters() { }
        public IPlatformSnapshot Snapshot { get; } = new FakeSnapshot();
        public bool PermissionHintRaised => false;
    }
}
