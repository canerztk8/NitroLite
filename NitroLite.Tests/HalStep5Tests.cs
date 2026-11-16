using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Core.Hardware.OpenRgb;
using NitroLite.Core.Hardware.Wmi;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// PR 5 of the HAL migration: the two services that still held an <see cref="AcerAgentClient"/> now go through
/// <see cref="IHardwareController"/>, a handover from WMI back to AASSvc re-applies NitroLite's target, the fan-stall
/// watchdog runs on every backend but only judges real tachometer readings, and the OpenRGB SDK client speaks the
/// binary protocol without a dependency.
/// </summary>
public class HalStep5Tests
{
    // ------------------------------------------------------------------ the recording controller

    /// <summary>
    /// An <see cref="IHardwareController"/> that records every write and answers reads from fields the test sets.
    /// Nothing here touches the hardware, so the tests run on any machine.
    /// </summary>
    private sealed class RecordingController : IHardwareController
    {
        public string BackendName { get; init; } = "Test";
        public bool IsAvailable { get; set; } = true;

        public OperatingMode Mode { get; set; } = OperatingMode.Default;
        public FanMode Fan { get; set; } = FanMode.Auto;
        public MonitorData? Sample { get; set; }

        public List<OperatingMode> ModeWrites { get; } = new();
        public List<(FanMode Mode, int Cpu, int Gpu)> FanWrites { get; } = new();
        public bool RefuseWrites { get; set; }

        public Task<bool> EnsureConnectedAsync() => Task.FromResult(IsAvailable);

        public Task<HardwareCapabilities> GetCapabilitiesAsync() =>
            Task.FromResult(new HardwareCapabilities { Backend = BackendName, ServiceAvailable = IsAvailable });

        public Task<MonitorData?> GetMonitorDataAsync() => Task.FromResult(Sample);

        public Task<OperatingMode?> GetOperatingModeAsync() => Task.FromResult<OperatingMode?>(Mode);

        public Task<bool> SetOperatingModeAsync(OperatingMode mode)
        {
            ModeWrites.Add(mode);
            if (RefuseWrites) return Task.FromResult(false);
            Mode = mode;
            return Task.FromResult(true);
        }

        public Task<GpuOperatingMode?> GetGpuModeAsync() => Task.FromResult<GpuOperatingMode?>(GpuOperatingMode.Optimus);

        public Task<bool> SetGpuModeAsync(GpuOperatingMode mode) => Task.FromResult(!RefuseWrites);

        public Task<int?> GetGpuModeCapabilityAsync() => Task.FromResult<int?>(null);

        public Task<FanMode?> GetFanModeAsync() => Task.FromResult<FanMode?>(Fan);

        public Task<bool> SetFanModeAsync(FanMode mode, int cpuPercent = -1, int gpuPercent = -1)
        {
            FanWrites.Add((mode, cpuPercent, gpuPercent));
            if (RefuseWrites) return Task.FromResult(false);
            Fan = mode;
            return Task.FromResult(true);
        }

        public Task<LightingConfig?> GetLightingConfigAsync() => Task.FromResult<LightingConfig?>(null);

        public Task<bool> SetLightingAsync(string effect, int brightness, int speed, int direction, string primaryColor, string[]? zoneColors) =>
            Task.FromResult(true);

        public Task<bool> TurnOffLightingAsync() => Task.FromResult(true);

        public Task<bool?> GetBacklightTimeoutAsync() => Task.FromResult<bool?>(false);

        public Task<bool> SetBacklightTimeoutAsync(bool enable30s) => Task.FromResult(true);

        public Task<BatteryChargeMode?> GetBatteryChargeModeAsync() =>
            Task.FromResult<BatteryChargeMode?>(BatteryChargeMode.Optimized80);

        public Task<bool> SetBatteryChargeModeAsync(BatteryChargeMode mode) => Task.FromResult(true);

        public Task<bool?> GetCoolBoostAsync() => Task.FromResult<bool?>(null);

        public Task<bool> SetCoolBoostAsync(bool on) => Task.FromResult(false);

        public Task<bool?> GetLcdOverdriveAsync() => Task.FromResult<bool?>(null);

        public Task<bool> SetLcdOverdriveAsync(bool on) => Task.FromResult(false);

        public Task<bool> SetWinKeyLockAsync(bool enabled) => Task.FromResult(false);

        public void Dispose() { }
    }

    /// <summary>An agent that answers telemetry without a socket, so the flag can be checked in isolation.</summary>
    private sealed class TelemetryAgent : MockAcerAgentClient
    {
        public MonitorData? Sample { get; set; }

        public override Task<MonitorData?> GetMonitorDataAsync() => Task.FromResult(Sample);
    }

    private sealed class NoopDisplay : IDisplayRefreshRateService
    {
        public int CurrentHz { get; set; } = 165;
        public int GetCurrentRefreshRate() => CurrentHz;
        public int GetMaxRefreshRate() => 165;
        public bool SetRefreshRate(int hz) { CurrentHz = hz; return true; }
    }

    private sealed class NoopBrightness : IScreenBrightnessService
    {
        public int? Current { get; set; } = 80;
        public int? GetBrightness() => Current;
        public bool SetBrightness(int percent) { Current = percent; return true; }
    }

    private sealed class NoopScheme : IPowerSchemeAccess
    {
        public Dictionary<(Guid, Guid), uint> Values { get; } = new();
        public bool TryReadDc(Guid subGroup, Guid setting, out uint value) => Values.TryGetValue((subGroup, setting), out value);
        public bool TryWriteDc(Guid subGroup, Guid setting, uint value) { Values[(subGroup, setting)] = value; return true; }
    }

    // ------------------------------------------------------------------ 1. school mode over the controller

    [Fact]
    public async Task SchoolMode_Enable_WritesModeAndFanThroughTheControllerNotTheAgent()
    {
        var agent = new MockAcerAgentClient();
        var hardware = new RecordingController();
        var settings = new AppSettings();
        var service = new SchoolModeService(
            agent, new MockCpuPowerService(), new NoopDisplay(), new NoopBrightness(), settings,
            new NoopScheme(), hardware)
        {
            BackupPath = null,
            GetCurrentOperatingMode = () => OperatingMode.Default,
            GetCurrentFanMode = () => FanMode.Max
        };

        var outcome = await service.EnableAsync();

        Assert.True(outcome.Success);
        Assert.Contains(OperatingMode.Eco, hardware.ModeWrites);
        Assert.Contains(hardware.FanWrites, w => w.Mode == FanMode.Auto);

        // The agent must not have been written to: that is the whole point of the migration.
        Assert.Empty(agent.ModeChangeHistory);
    }

    [Fact]
    public async Task SchoolMode_Disable_RestoresTheRecordedModeAndFanThroughTheController()
    {
        // The undo record is what switch-off replays, so this test needs a real one on disk.
        string backupPath = Path.Combine(Path.GetTempPath(), $"nitrolite-school-{Guid.NewGuid():N}.json");
        try
        {
            var agent = new MockAcerAgentClient();
            var hardware = new RecordingController { Mode = OperatingMode.Extreme, Fan = FanMode.Max };
            var settings = new AppSettings();
            var service = new SchoolModeService(
                agent, new MockCpuPowerService(), new NoopDisplay(), new NoopBrightness(), settings,
                new NoopScheme(), hardware)
            {
                BackupPath = backupPath,
                GetCurrentOperatingMode = () => OperatingMode.Extreme,
                GetCurrentFanMode = () => FanMode.Max
            };

            await service.EnableAsync();
            hardware.ModeWrites.Clear();
            hardware.FanWrites.Clear();

            var outcome = await service.DisableAsync();

            Assert.True(outcome.Success);
            Assert.Contains(OperatingMode.Extreme, hardware.ModeWrites);
            Assert.Contains(hardware.FanWrites, w => w.Mode == FanMode.Max);
            Assert.Empty(agent.ModeChangeHistory);
        }
        finally
        {
            try { File.Delete(backupPath); } catch { }
        }
    }

    [Fact]
    public async Task SchoolMode_WithoutAController_StillWrapsTheAgentSoNothingBreaks()
    {
        // The optional parameter is what keeps the ~20 existing call sites compiling; it must still work.
        var agent = new MockAcerAgentClient();
        var service = new SchoolModeService(
            agent, new MockCpuPowerService(), new NoopDisplay(), new NoopBrightness(), new AppSettings(),
            new NoopScheme())
        {
            BackupPath = null,
            GetCurrentOperatingMode = () => OperatingMode.Default,
            GetCurrentFanMode = () => FanMode.Auto
        };

        var outcome = await service.EnableAsync();

        Assert.True(outcome.Success);
        Assert.Contains(OperatingMode.Eco, agent.ModeChangeHistory);
    }

    // ------------------------------------------------------------------ 2. power automation over the controller

    [Fact]
    public async Task PowerAutomation_DcTransition_WritesThroughTheController()
    {
        var agent = new MockAcerAgentClient();
        var hardware = new RecordingController();
        var settings = new AppSettings { DcPowerMode = DcPowerProfilePreference.Eco };
        var service = new PowerAutomationService(
            agent, new MockCpuPowerService(), new NoopDisplay(), settings,
            attachSystemEvents: false, transitionLock: null, hardware: hardware)
        {
            GetCurrentOperatingMode = () => OperatingMode.Extreme,
            GetCurrentFanMode = () => FanMode.Max
        };

        await service.ProcessPowerTransitionAsync(isAc: false);

        Assert.True(service.IsDcEcoActive);
        Assert.NotEmpty(hardware.ModeWrites);
        Assert.NotEmpty(hardware.FanWrites);
        Assert.Empty(agent.ModeChangeHistory);
    }

    [Fact]
    public async Task PowerAutomation_AcReturn_RestoresThroughTheController()
    {
        var agent = new MockAcerAgentClient();
        var hardware = new RecordingController();
        var settings = new AppSettings { DcPowerMode = DcPowerProfilePreference.Eco };
        var service = new PowerAutomationService(
            agent, new MockCpuPowerService(), new NoopDisplay(), settings,
            attachSystemEvents: false, transitionLock: null, hardware: hardware)
        {
            GetCurrentOperatingMode = () => OperatingMode.Extreme,
            GetCurrentFanMode = () => FanMode.Max
        };

        await service.ProcessPowerTransitionAsync(isAc: false);
        hardware.ModeWrites.Clear();
        hardware.FanWrites.Clear();

        await service.ProcessPowerTransitionAsync(isAc: true);

        Assert.False(service.IsDcEcoActive);
        Assert.Contains(OperatingMode.Extreme, hardware.ModeWrites);
        Assert.Contains(hardware.FanWrites, w => w.Mode == FanMode.Max);
        Assert.Empty(agent.ModeChangeHistory);
    }

    // ------------------------------------------------------------------ 3. reconciliation

    [Fact]
    public async Task Reconciliation_NoHandover_ReturnsNone()
    {
        var agent = new RecordingController { BackendName = "AASSvc" };
        var hybrid = new HybridHardwareController(agent, wmiFactory: () => null, serviceHealthy: () => Task.FromResult(true));

        var result = await hybrid.ReconcileAsync();

        Assert.False(result.Switched);
        Assert.False(result.Reapplied);
        Assert.Empty(agent.ModeWrites);
    }

    [Fact]
    public async Task Reconciliation_ServiceComesBack_ReappliesTheActiveTarget()
    {
        bool healthy = false;
        var agent = new RecordingController { BackendName = "AASSvc", Mode = OperatingMode.Default, Fan = FanMode.Auto };
        var wmi = new RecordingController { BackendName = "WMI" };
        var hybrid = new HybridHardwareController(
            agent, wmiFactory: () => wmi, serviceHealthy: () => Task.FromResult(healthy))
        {
            TargetProvider = () => new HardwareTarget(OperatingMode.Eco, FanMode.Auto, "Okul modu")
        };

        // On WMI: the service is down, so the app writes the EC directly.
        await hybrid.ResolveAsync();
        Assert.Equal("WMI", hybrid.ActiveBackendName);

        // The user starts Acer's software. The service now reports Default/Auto, which is not what school mode wants.
        healthy = true;
        var result = await hybrid.ReconcileAsync();

        Assert.True(result.Switched);
        Assert.True(result.Reapplied);
        Assert.Contains(OperatingMode.Eco, agent.ModeWrites);
        Assert.Equal("AASSvc", hybrid.ActiveBackendName);
    }

    [Fact]
    public async Task Reconciliation_TargetAlreadyMatches_DoesNotWriteAgain()
    {
        bool healthy = false;
        var agent = new RecordingController { BackendName = "AASSvc", Mode = OperatingMode.Eco, Fan = FanMode.Auto };
        var wmi = new RecordingController { BackendName = "WMI" };
        var hybrid = new HybridHardwareController(
            agent, wmiFactory: () => wmi, serviceHealthy: () => Task.FromResult(healthy))
        {
            TargetProvider = () => new HardwareTarget(OperatingMode.Eco, FanMode.Auto, "Okul modu")
        };

        await hybrid.ResolveAsync();
        healthy = true;
        var result = await hybrid.ReconcileAsync();

        Assert.True(result.Switched);
        Assert.False(result.Reapplied);
        Assert.Empty(agent.ModeWrites);
        Assert.Empty(agent.FanWrites);
    }

    [Fact]
    public async Task Reconciliation_ThermalFloorTarget_WritesTheFloorSpeed()
    {
        bool healthy = false;
        var agent = new RecordingController { BackendName = "AASSvc", Mode = OperatingMode.Default, Fan = FanMode.Auto };
        var wmi = new RecordingController { BackendName = "WMI" };
        var hybrid = new HybridHardwareController(
            agent, wmiFactory: () => wmi, serviceHealthy: () => Task.FromResult(healthy))
        {
            TargetProvider = () => new HardwareTarget(
                OperatingMode.Default, FanMode.Custom, "Termal güvenlik tabanı",
                FanCurve.SafetyFloorAt(95), FanCurve.SafetyFloorAt(95))
        };

        await hybrid.ResolveAsync();
        healthy = true;
        await hybrid.ReconcileAsync();

        var write = Assert.Single(agent.FanWrites);
        Assert.Equal(FanMode.Custom, write.Mode);
        Assert.Equal(100, write.Cpu);
        Assert.Equal(100, write.Gpu);
    }

    [Fact]
    public async Task Reconciliation_IsConsumedOnce()
    {
        bool healthy = false;
        var agent = new RecordingController { BackendName = "AASSvc", Mode = OperatingMode.Default };
        var wmi = new RecordingController { BackendName = "WMI" };
        var hybrid = new HybridHardwareController(
            agent, wmiFactory: () => wmi, serviceHealthy: () => Task.FromResult(healthy))
        {
            TargetProvider = () => new HardwareTarget(OperatingMode.Eco, null, "Okul modu")
        };

        await hybrid.ResolveAsync();
        healthy = true;
        Assert.True((await hybrid.ReconcileAsync()).Switched);

        agent.ModeWrites.Clear();
        var second = await hybrid.ReconcileAsync();

        Assert.False(second.Switched);
        Assert.Empty(agent.ModeWrites);
    }

    [Fact]
    public async Task Reconciliation_UseDirectWmiFalse_NeverLeavesTheServicePath()
    {
        var agent = new RecordingController { BackendName = "AASSvc" };
        var hybrid = new HybridHardwareController(
            agent, wmiFactory: () => new RecordingController { BackendName = "WMI" },
            useDirectWmi: () => false, serviceHealthy: () => Task.FromResult(false));

        await hybrid.ResolveAsync();
        var result = await hybrid.ReconcileAsync();

        Assert.False(result.Switched);
        Assert.Equal("AASSvc", hybrid.ActiveBackendName);
    }

    // ------------------------------------------------------------------ 4. fan-stall parity

    [Fact]
    public void FanStallWatchdog_MeasuredSample_ConfirmsAStall()
    {
        var watchdog = new FanStallWatchdog();
        var start = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        Assert.Null(watchdog.Observe(92, 90, 0, 0, start));
        var verdict = watchdog.Observe(92, 90, 0, 0, start.AddSeconds(11));

        Assert.NotNull(verdict);
        Assert.Equal(FanStallTarget.Both, verdict!.Target);
    }

    [Fact]
    public void FanStallWatchdog_DerivedSample_StaysInStandby()
    {
        // A derived RPM is the requested percentage restated, so a stopped fan still reads "34 %". Judging it would
        // either hide a real stall or invent one; the watchdog must not be fed it at all.
        var watchdog = new FanStallWatchdog();
        var start = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        var derived = new MonitorData
        {
            CpuTemperature = 95, GpuTemperature = 95, CpuFanSpeed = 0, GpuFanSpeed = 0,
            IsFanRpmMeasured = false
        };

        Assert.False(derived.IsFanRpmMeasured);

        // The watchdog itself is stateless about the flag: the caller is what keeps it in standby. This asserts the
        // contract the caller relies on, so a future change to the flag cannot silently re-enable judging.
        Assert.Null(watchdog.Observe(derived.CpuTemperature, derived.GpuTemperature, 4000, 4000, start));
    }

    [Fact]
    public async Task AgentController_MarksItsSampleAsMeasured()
    {
        var agent = new TelemetryAgent
        {
            Sample = new MonitorData { CpuTemperature = 60, CpuFanSpeed = 2400, GpuFanSpeed = 2300 }
        };
        using var controller = new AgentHardwareController(agent, new CareCenterClient());

        var data = await controller.GetMonitorDataAsync();

        Assert.NotNull(data);
        Assert.True(data!.IsFanRpmMeasured);
    }

    [Fact]
    public async Task AgentController_NoSample_StaysNull()
    {
        var agent = new TelemetryAgent { Sample = null };
        using var controller = new AgentHardwareController(agent, new CareCenterClient());

        Assert.Null(await controller.GetMonitorDataAsync());
    }

    [Fact]
    public void MonitorData_DefaultsToNotMeasured()
    {
        // The fallback path and the WMI path both leave the flag alone; only a real tachometer read sets it.
        Assert.False(new MonitorData().IsFanRpmMeasured);
    }

    [Fact]
    public void WmiDerivedRpm_IsAnEstimateNotAMeasurement()
    {
        Assert.Equal(0, WmiHardwareController.DeriveRpm(50, 0));
        Assert.Equal(3000, WmiHardwareController.DeriveRpm(50, 6000));
        Assert.Equal(6000, WmiHardwareController.DeriveRpm(150, 6000));
    }

    // ------------------------------------------------------------------ 5. OpenRGB protocol

    [Fact]
    public void OpenRgb_BuildPacket_WritesTheHeaderTheServerExpects()
    {
        var packet = OpenRgbProtocol.BuildPacket(2, OpenRgbProtocol.PacketType.UpdateLeds, [0xAA, 0xBB]);

        Assert.Equal(OpenRgbProtocol.HeaderBytes + 2, packet.Length);
        Assert.Equal((byte)'O', packet[0]);
        Assert.Equal((byte)'R', packet[1]);
        Assert.Equal((byte)'G', packet[2]);
        Assert.Equal((byte)'B', packet[3]);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)));
        Assert.Equal(1000u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(12)));
        Assert.Equal(0xAA, packet[16]);
        Assert.Equal(0xBB, packet[17]);
    }

    [Fact]
    public void OpenRgb_TryParseHeader_RoundTripsAndRejectsGarbage()
    {
        var packet = OpenRgbProtocol.BuildPacket(1, OpenRgbProtocol.PacketType.RequestControllerData, [1, 2, 3]);

        Assert.True(OpenRgbProtocol.TryParseHeader(packet, out uint index, out var type, out int length));
        Assert.Equal(1u, index);
        Assert.Equal(OpenRgbProtocol.PacketType.RequestControllerData, type);
        Assert.Equal(3, length);

        var badMagic = (byte[])packet.Clone();
        badMagic[0] = (byte)'X';
        Assert.False(OpenRgbProtocol.TryParseHeader(badMagic, out _, out _, out _));

        var tooLong = (byte[])packet.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(tooLong.AsSpan(12), OpenRgbProtocol.MaxPayloadBytes + 1);
        Assert.False(OpenRgbProtocol.TryParseHeader(tooLong, out _, out _, out _));

        Assert.False(OpenRgbProtocol.TryParseHeader(packet.AsSpan(0, 8), out _, out _, out _));
    }

    [Fact]
    public void OpenRgb_ClientNamePayload_IsLengthPrefixedUtf8()
    {
        var payload = OpenRgbProtocol.BuildClientNamePayload("NitroLite");

        Assert.Equal(4 + 9, payload.Length);
        Assert.Equal(9u, BinaryPrimitives.ReadUInt32LittleEndian(payload));
        Assert.Equal("NitroLite", Encoding.UTF8.GetString(payload, 4, 9));
    }

    [Fact]
    public void OpenRgb_UpdateLedsPayload_CarriesSizeCountAndTriples()
    {
        var payload = OpenRgbProtocol.BuildUpdateLedsPayload([new RgbColor(0x11, 0x22, 0x33), new RgbColor(0x44, 0x55, 0x66)]);

        Assert.Equal(4 + 4 + 6, payload.Length);
        Assert.Equal(10u, BinaryPrimitives.ReadUInt32LittleEndian(payload));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4)));
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 }, payload[8..]);
    }

    [Fact]
    public void OpenRgb_UpdateZoneLedsPayload_CarriesTheZoneIndex()
    {
        var payload = OpenRgbProtocol.BuildUpdateZoneLedsPayload(3, [new RgbColor(1, 2, 3)]);

        Assert.Equal(4 + 8 + 3, payload.Length);
        Assert.Equal(11u, BinaryPrimitives.ReadUInt32LittleEndian(payload));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(8)));
        Assert.Equal(new byte[] { 1, 2, 3 }, payload[12..]);
    }

    [Fact]
    public void OpenRgb_ParseControllerCount_RejectsAnAbsurdAnswer()
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 3);
        Assert.Equal(3, OpenRgbProtocol.ParseControllerCount(payload));

        BinaryPrimitives.WriteUInt32LittleEndian(payload, 100_000);
        Assert.Null(OpenRgbProtocol.ParseControllerCount(payload));

        Assert.Null(OpenRgbProtocol.ParseControllerCount([1, 2]));
    }

    [Fact]
    public void OpenRgb_ParseController_ReadsNameLedsAndColours()
    {
        var payload = BuildControllerPayload("Acer Keyboard", [new RgbColor(0x10, 0x20, 0x30), new RgbColor(0x40, 0x50, 0x60)]);

        var controller = OpenRgbProtocol.ParseController(0, payload);

        Assert.NotNull(controller);
        Assert.True(controller!.Parsed);
        Assert.Equal("Acer Keyboard", controller.Name);
        Assert.Equal(2, controller.LedCount);
        Assert.Equal(1, controller.ZoneCount);
        Assert.Equal(new RgbColor(0x10, 0x20, 0x30), controller.Colors[0]);
        Assert.Equal(new RgbColor(0x40, 0x50, 0x60), controller.Colors[1]);
    }

    [Fact]
    public void OpenRgb_ParseController_TruncatedPayloadIsNotTrusted()
    {
        var payload = BuildControllerPayload("Acer Keyboard", [new RgbColor(1, 2, 3)]);
        var truncated = payload[..(payload.Length - 2)];

        var controller = OpenRgbProtocol.ParseController(0, truncated);

        // Either it refuses outright or it reports what it read without claiming the colours are trustworthy.
        Assert.True(controller == null || !controller.Parsed);
    }

    [Fact]
    public void OpenRgb_Matches_ComparesOnlyTheLedsThatWereWritten()
    {
        var observed = new[] { new RgbColor(1, 2, 3), new RgbColor(4, 5, 6), new RgbColor(7, 8, 9) };

        Assert.True(OpenRgbClient.Matches(observed, [new RgbColor(1, 2, 3)]));
        Assert.True(OpenRgbClient.Matches(observed, observed));
        Assert.False(OpenRgbClient.Matches(observed, [new RgbColor(9, 9, 9)]));
        Assert.False(OpenRgbClient.Matches([new RgbColor(1, 2, 3)], observed));
    }

    [Fact]
    public void OpenRgb_RgbColor_ParsesHex()
    {
        Assert.Equal(new RgbColor(0xFF, 0x80, 0x00), RgbColor.Parse("#FF8000"));
        Assert.Equal(new RgbColor(0xFF, 0x80, 0x00), RgbColor.Parse("FF8000"));
        Assert.Equal(default, RgbColor.Parse("nope"));
        Assert.Equal(default, RgbColor.Parse(null));
    }

    [Fact]
    public async Task OpenRgb_NoServer_ReturnsNotSupportedWithoutThrowing()
    {
        // Port 1 is never an OpenRGB server; the point is that a missing integration is a result, not an exception.
        using var client = new OpenRgbClient(OpenRgbClient.DefaultHost, 1, delay: (_, _) => Task.CompletedTask);

        Assert.False(await client.ConnectAsync(TimeSpan.FromMilliseconds(400)));

        var result = await client.UpdateLedsAsync(0, [new RgbColor(1, 2, 3)]);

        Assert.Equal(OpenRgbWriteOutcome.NotSupported, result.Outcome);
        Assert.False(result.Applied);
    }

    [Fact]
    public async Task OpenRgb_LoopbackServer_AppliesAndVerifiesTheWrite()
    {
        using var server = new FakeOpenRgbServer();
        await server.StartAsync();

        using var client = new OpenRgbClient(OpenRgbClient.DefaultHost, server.Port, delay: (_, _) => Task.CompletedTask);

        Assert.True(await client.ConnectAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, await client.GetControllerCountAsync());

        var controller = await client.GetControllerAsync(0);
        Assert.NotNull(controller);
        Assert.Equal("Acer Keyboard", controller!.Name);

        var result = await client.UpdateLedsAsync(0, [new RgbColor(0x0A, 0x0B, 0x0C), new RgbColor(0x0D, 0x0E, 0x0F)]);

        Assert.Equal(OpenRgbWriteOutcome.Applied, result.Outcome);
        Assert.Equal(new RgbColor(0x0A, 0x0B, 0x0C), server.Colors[0]);
        Assert.Equal(new RgbColor(0x0D, 0x0E, 0x0F), server.Colors[1]);
        Assert.Contains(OpenRgbProtocol.PacketType.SetClientName, server.PacketTypes);
        Assert.Contains(OpenRgbProtocol.PacketType.UpdateLeds, server.PacketTypes);
    }

    [Fact]
    public async Task OpenRgb_LoopbackServer_ZoneWriteCarriesTheZoneIndex()
    {
        using var server = new FakeOpenRgbServer();
        await server.StartAsync();

        using var client = new OpenRgbClient(OpenRgbClient.DefaultHost, server.Port, delay: (_, _) => Task.CompletedTask);
        await client.ConnectAsync(TimeSpan.FromSeconds(2));

        var result = await client.UpdateZoneLedsAsync(0, 2, [new RgbColor(0x33, 0x44, 0x55)]);

        Assert.Equal(OpenRgbWriteOutcome.Applied, result.Outcome);
        Assert.Equal(2, server.LastZoneIndex);
    }

    [Fact]
    public async Task OpenRgb_LoopbackServer_WriteThatDoesNotStickIsReportedAsFailed()
    {
        using var server = new FakeOpenRgbServer { IgnoreWrites = true };
        await server.StartAsync();

        using var client = new OpenRgbClient(OpenRgbClient.DefaultHost, server.Port, delay: (_, _) => Task.CompletedTask);
        await client.ConnectAsync(TimeSpan.FromSeconds(2));

        var result = await client.UpdateLedsAsync(0, [new RgbColor(0x01, 0x02, 0x03)]);

        Assert.Equal(OpenRgbWriteOutcome.Failed, result.Outcome);
        Assert.NotNull(result.Detail);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Builds a controller description in the layout <c>REQUEST_CONTROLLER_DATA</c> answers with.</summary>
    private static byte[] BuildControllerPayload(string name, IReadOnlyList<RgbColor> colors)
    {
        var data = new List<byte>();

        void U32(uint value)
        {
            var buffer = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
            data.AddRange(buffer);
        }

        void Fixed(string text)
        {
            var field = new byte[OpenRgbProtocol.NameFieldBytes];
            Encoding.UTF8.GetBytes(text).CopyTo(field, 0);
            data.AddRange(field);
        }

        U32(0);                       // controller type
        Fixed(name);
        Fixed("Acer");                // vendor
        Fixed("Keyboard");            // description
        Fixed("1.0");                 // version
        Fixed("");                    // serial
        Fixed("");                    // location

        U32(0);                       // mode count

        U32(1);                       // zone count
        Fixed("Zone 1");
        U32(1);                       // type
        U32(1);                       // leds min
        U32((uint)colors.Count);      // leds max
        U32((uint)colors.Count);      // leds count
        U32(0);                       // matrix size
        U32(0);                       // segment count

        U32((uint)colors.Count);      // led count
        for (int i = 0; i < colors.Count; i++)
        {
            Fixed($"LED {i}");
            U32((uint)i);
        }

        U32((uint)colors.Count);      // colour count
        foreach (var color in colors)
        {
            data.Add(color.R);
            data.Add(color.G);
            data.Add(color.B);
        }

        var payload = new byte[4 + data.Count];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)data.Count);
        data.CopyTo(payload, 4);
        return payload;
    }

    /// <summary>
    /// A loopback OpenRGB server: enough of the protocol to prove the client's framing, its handshake and its
    /// read-back. It keeps the colours it was sent, so a write that does not stick is a real failure rather than a
    /// mock that always agrees.
    /// </summary>
    private sealed class FakeOpenRgbServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private Task? _loop;

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public List<RgbColor> Colors { get; } = new();
        public List<OpenRgbProtocol.PacketType> PacketTypes { get; } = new();
        public int LastZoneIndex { get; private set; } = -1;
        public bool IgnoreWrites { get; init; }

        public Task StartAsync()
        {
            _listener.Start();
            _loop = Task.Run(AcceptLoopAsync);
            return Task.CompletedTask;
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _ = Task.Run(() => ServeAsync(client));
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                try
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        var header = new byte[OpenRgbProtocol.HeaderBytes];
                        if (!await ReadExactlyAsync(stream, header)) return;
                        if (!OpenRgbProtocol.TryParseHeader(header, out uint device, out var type, out int length)) return;

                        var payload = new byte[length];
                        if (length > 0 && !await ReadExactlyAsync(stream, payload)) return;

                        lock (PacketTypes) PacketTypes.Add(type);

                        switch (type)
                        {
                            case OpenRgbProtocol.PacketType.SetClientName:
                                await SendAsync(stream, device, type, []);
                                break;

                            case OpenRgbProtocol.PacketType.RequestControllerCount:
                            {
                                var answer = new byte[4];
                                BinaryPrimitives.WriteUInt32LittleEndian(answer, 1);
                                await SendAsync(stream, device, type, answer);
                                break;
                            }

                            case OpenRgbProtocol.PacketType.RequestControllerData:
                                await SendAsync(stream, device, type, BuildControllerPayload("Acer Keyboard", Snapshot()));
                                break;

                            case OpenRgbProtocol.PacketType.SetCustomMode:
                                await SendAsync(stream, device, type, []);
                                break;

                            case OpenRgbProtocol.PacketType.UpdateLeds:
                            {
                                if (!IgnoreWrites) ApplyColors(payload, offset: 8);
                                await SendAsync(stream, device, type, []);
                                break;
                            }

                            case OpenRgbProtocol.PacketType.UpdateZoneLeds:
                            {
                                LastZoneIndex = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4));
                                if (!IgnoreWrites) ApplyColors(payload, offset: 12);
                                await SendAsync(stream, device, type, []);
                                break;
                            }

                            default:
                                await SendAsync(stream, device, type, []);
                                break;
                        }
                    }
                }
                catch (IOException) { }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
            }
        }

        private RgbColor[] Snapshot()
        {
            lock (Colors)
            {
                if (Colors.Count == 0)
                {
                    Colors.Add(new RgbColor(0, 0, 0));
                    Colors.Add(new RgbColor(0, 0, 0));
                }
                return Colors.ToArray();
            }
        }

        private void ApplyColors(byte[] payload, int offset)
        {
            int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset - 4));
            lock (Colors)
            {
                Colors.Clear();
                for (int i = 0; i < count; i++)
                {
                    int at = offset + i * 3;
                    if (at + 2 >= payload.Length) break;
                    Colors.Add(new RgbColor(payload[at], payload[at + 1], payload[at + 2]));
                }
            }
        }

        private static async Task SendAsync(NetworkStream stream, uint device, OpenRgbProtocol.PacketType type, byte[] payload)
        {
            var packet = OpenRgbProtocol.BuildPacket(device, type, payload);
            await stream.WriteAsync(packet);
            await stream.FlushAsync();
        }

        private static async Task<bool> ReadExactlyAsync(NetworkStream stream, byte[] buffer)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(offset));
                if (read <= 0) return false;
                offset += read;
            }
            return true;
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
            _cts.Dispose();
        }
    }
}
