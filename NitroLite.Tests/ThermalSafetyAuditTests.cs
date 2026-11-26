using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Core.Hardware.Wmi;
using NitroLite.Windows;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Thermal-safety audit items: fan read-back covers both fans (HW-16), a -1 custom speed means the saved speed
/// (HW-19), a downloaded profile can never claim verified sensors (SEC-07), the agent payload keeps its 30 % floor
/// (TEST-05) and the shutdown handback hooks stay wired (TEST-02). No hardware is touched: the worker is a fake.
/// </summary>
public class ThermalSafetyAuditTests
{
    private static Task NoDelay(TimeSpan _) => Task.CompletedTask;

    private sealed class FakeExecutor : IWmiExecutor
    {
        public int? ReportedCpu { get; set; }
        public int? ReportedGpu { get; set; }
        public bool Echo { get; set; }
        public List<(int Cpu, int Gpu)> Speeds { get; } = new();

        public bool IsAvailable => true;
        public WmiOperationResult Ping() => WmiOperationResult.Success();
        public WmiOperationResult GetCapabilities() => WmiOperationResult.Success();
        public WmiOperationResult ReadTelemetry(SensorQuery query) => WmiOperationResult.Success(new JsonObject());

        public WmiOperationResult ReadDeviceState()
        {
            var data = new JsonObject();
            if (ReportedCpu.HasValue) data["cpuFanPercent"] = ReportedCpu.Value;
            if (ReportedGpu.HasValue) data["gpuFanPercent"] = ReportedGpu.Value;
            return WmiOperationResult.Success(data);
        }

        public WmiOperationResult SetFanBehavior(FanMode cpu, FanMode gpu) => WmiOperationResult.Success();

        public WmiOperationResult SetFanSpeed(int cpuPercent, int gpuPercent)
        {
            Speeds.Add((cpuPercent, gpuPercent));
            if (Echo) { ReportedCpu = cpuPercent; ReportedGpu = gpuPercent; }
            return WmiOperationResult.Success();
        }

        public WmiOperationResult SetOperatingMode(OperatingMode mode) => WmiOperationResult.Success();
        public WmiOperationResult SetMuxMode(GpuOperatingMode mode) => WmiOperationResult.Success();
        public WmiOperationResult SetBatteryLimit(BatteryChargeMode mode) => WmiOperationResult.Success();
        public WmiOperationResult GetKeyboardBacklight() => WmiOperationResult.Success();
        public WmiOperationResult SetKeyboardLighting(KeyboardLightingRequest request) => WmiOperationResult.Success();
        public WmiOperationResult GetCoolBoost() => WmiOperationResult.Success();
        public WmiOperationResult SetCoolBoost(bool on) => WmiOperationResult.Success();
        public WmiOperationResult SetWinKeyLock(bool enabled) => WmiOperationResult.Success();
        public WmiOperationResult GetBacklightTimeout() => WmiOperationResult.Success();
        public WmiOperationResult SetBacklightTimeout(bool enabled) => WmiOperationResult.Success();
        public void Dispose() { }
    }

    private static WmiWorkerLauncher Launcher(FakeExecutor executor)
    {
        return new WmiWorkerLauncher(
            executablePath: "NitroLite.exe",
            start: _ => Process.Start(new ProcessStartInfo("cmd.exe", "/c exit")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            }),
            connect: async (_, nonce, _, _, ct) =>
            {
                var pipe = "nitrolite-thermal-" + Guid.NewGuid().ToString("N");
                var server = new WmiWorkerServer(pipe, executor);
                _ = Task.Run(() => server.RunAsync(TimeSpan.FromSeconds(15)));
                return await WmiWorkerClient.ConnectAsync(pipe, nonce, TimeSpan.FromSeconds(5), null, ct);
            },
            isElevated: () => true,
            isTrustedLocation: _ => true);
    }

    // ---------------------------------------------------------------- HW-16

    [Fact]
    public async Task FanWrite_CpuMatchesButGpuDoesNot_IsReportedButNotCounted()
    {
        var executor = new FakeExecutor { ReportedCpu = 90, ReportedGpu = 40 };
        using var launcher = Launcher(executor);
        using var controller = new WmiHardwareController(launcher, new ReadBackPolicy(new AppSettings(), NoDelay));

        var applied = await controller.SetFanModeAsync(FanMode.Custom, 90, 90);

        // Reported (not applied, off for the session, fans handed back) ...
        Assert.False(applied);
        Assert.True(controller.ReadBack.IsDisabled(ReadBackPolicy.FanSpeed));
        // ... but the GPU read-back is unmeasured on real hardware, so it never moves the permanent-disable counter.
        Assert.Equal(0, controller.ReadBack.ConsecutiveSessionFailures(ReadBackPolicy.FanSpeed));
        Assert.False(controller.ReadBack.IsPermanentlyDisabled(ReadBackPolicy.FanSpeed));
    }

    [Fact]
    public async Task FanWrite_GpuOnlyMismatch_NeverReachesThePermanentDisable()
    {
        var settings = new AppSettings();
        for (int session = 0; session < ReadBackPolicy.SessionsBeforePermanentDisable + 1; session++)
        {
            var executor = new FakeExecutor { ReportedCpu = 90, ReportedGpu = 40 };
            using var launcher = Launcher(executor);
            using var controller = new WmiHardwareController(launcher, new ReadBackPolicy(settings, NoDelay));
            Assert.False(await controller.SetFanModeAsync(FanMode.Custom, 90, 90));
        }

        Assert.DoesNotContain(ReadBackPolicy.FanSpeed, settings.DisabledCapabilities);
        // (The Auto handback is a different capability and its fake read-back is not modelled here.)
        Assert.False(settings.CapabilitySessionFailures.ContainsKey(ReadBackPolicy.FanSpeed));
    }

    [Fact]
    public async Task FanWrite_CpuMismatch_StillCountsTowardTheDisable()
    {
        var executor = new FakeExecutor { ReportedCpu = 40, ReportedGpu = 90 };
        using var launcher = Launcher(executor);
        using var controller = new WmiHardwareController(launcher, new ReadBackPolicy(new AppSettings(), NoDelay));

        Assert.False(await controller.SetFanModeAsync(FanMode.Custom, 90, 90));
        Assert.Equal(1, controller.ReadBack.ConsecutiveSessionFailures(ReadBackPolicy.FanSpeed));
    }

    [Fact]
    public async Task FanWrite_BothFansEchoed_IsApplied()
    {
        var executor = new FakeExecutor { Echo = true };
        using var launcher = Launcher(executor);
        using var controller = new WmiHardwareController(launcher, new ReadBackPolicy(new AppSettings(), NoDelay));

        Assert.True(await controller.SetFanModeAsync(FanMode.Custom, 90, 60));
    }

    [Fact]
    public async Task FanWrite_GpuValueMissing_IsUnverifiedNotAMismatch()
    {
        var executor = new FakeExecutor { ReportedCpu = 90, ReportedGpu = null };
        using var launcher = Launcher(executor);
        using var controller = new WmiHardwareController(launcher, new ReadBackPolicy(new AppSettings(), NoDelay));

        Assert.True(await controller.SetFanModeAsync(FanMode.Custom, 90, 90));
        Assert.Equal(0, controller.ReadBack.ConsecutiveSessionFailures(ReadBackPolicy.FanSpeed));
    }

    // ---------------------------------------------------------------- HW-19

    [Fact]
    public void NormalisePercent_Negative_UsesTheSavedSpeedAndKeepsTheFloor()
    {
        Assert.Equal(70, WmiHardwareController.NormalisePercent(-1, 70));
        Assert.Equal(30, WmiHardwareController.NormalisePercent(-1, 0));
        Assert.Equal(100, WmiHardwareController.NormalisePercent(-1, 250));
        Assert.Equal(50, WmiHardwareController.NormalisePercent(-1));
        Assert.Equal(60, WmiHardwareController.NormalisePercent(60, 70));
    }

    [Fact]
    public async Task FanWrite_MinusOne_SendsTheSavedCustomSpeeds()
    {
        var executor = new FakeExecutor { Echo = true };
        using var launcher = Launcher(executor);
        using var controller = new WmiHardwareController(launcher, new ReadBackPolicy(new AppSettings(), NoDelay),
            customFanDefaults: () => (75, 65));

        Assert.True(await controller.SetFanModeAsync(FanMode.Custom));

        Assert.Equal((75, 65), Assert.Single(executor.Speeds));
    }

    [Fact]
    public void AgentClient_MinusOne_ResolvesToTheSavedSpeeds_NotTheFiftyDefault()
    {
        using var client = new AcerAgentClient();
        Assert.Equal((50, 50), client.SavedCustomFanPercents());

        client.CustomFanDefaults = () => (80, 45);
        Assert.Equal((80, 45), client.SavedCustomFanPercents());

        // The 30 % floor still wins at the payload, whatever the saved speed says.
        var json = JsonSerializer.Serialize(AcerAgentClient.BuildFanControlParameter(FanMode.Custom, 5, 5));
        using var doc = JsonDocument.Parse(json);
        foreach (var fan in doc.RootElement.GetProperty("custom_fan_data").EnumerateArray())
            Assert.Equal(AppSettings.MinCustomFanPercent, fan.GetProperty("fan_custom_speed").GetInt32());
    }

    [Fact]
    public void AppHost_WiresBothControllersWithTheSavedFanSpeeds()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "NitroLite.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var source = File.ReadAllText(Path.Combine(dir!.FullName, "NitroLite.Desktop", "AppHost.cs"));

        Assert.Contains("savedCustomFan = () => (settings.CustomFanCpuPercent, settings.CustomFanGpuPercent)", source);
        Assert.Matches(new Regex(@"new WmiHardwareController\([^;]*customFanDefaults:\s*savedCustomFan"), source);
        Assert.Matches(new Regex(@"new AgentHardwareController\([^;]*customFanDefaults:\s*savedCustomFan"), source);
    }

    // ---------------------------------------------------------------- SEC-07

    private const string RemoteWithVerified = """
    {
      "schemaVersion": 1,
      "defaultSensorLayout": { "verified": true },
      "profiles": [
        {
          "id": "remote-an16-41",
          "match": { "modelCodes": [ "AN16-41" ] },
          "sensors": { "cpuTempSensor": 1, "cpuFanRpmSensor": 2, "sys1TempSensor": 3, "gpuFanRpmSensor": 6, "gpuTempSensor": 10, "verified": true },
          "maxFanRpm": { "cpu": 6200, "gpu": 6000 }
        }
      ]
    }
    """;

    private static DeviceIdentity An1641() => new()
    {
        Manufacturer = "Acer",
        Model = "Nitro AN16-41",
        ModelCode = "AN16-41",
        IsAcer = true
    };

    [Fact]
    public void RemoteProfile_VerifiedTrue_IsIgnored()
    {
        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, RemoteWithVerified, out var rejections);
        Assert.Empty(rejections);

        var profile = catalog.Match(An1641());
        Assert.Equal("remote-an16-41", profile.Id);
        Assert.Equal(ProfileCatalog.RemoteOrigin, profile.Source);
        Assert.False(profile.Sensors.Verified);
        Assert.False(catalog.DefaultSensors.Verified);
    }

    private static string VerifiedDoc(string id, int cpuTemp) => $$"""
    {
      "schemaVersion": 1,
      "profiles": [
        {
          "id": "{{id}}",
          "match": { "modelCodes": [ "AN16-41" ] },
          "sensors": { "cpuTempSensor": {{cpuTemp}}, "cpuFanRpmSensor": 2, "sys1TempSensor": 3, "gpuFanRpmSensor": 6, "gpuTempSensor": 10, "verified": true, "supported": true },
          "maxFanRpm": { "cpu": 6200, "gpu": 6000 }
        }
      ]
    }
    """;

    [Fact]
    public void RemoteProfile_SameIdAndSameSensorIds_KeepsVerified()
    {
        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null,
            VerifiedDoc("nitro-16-an16-41", 1), out var rejections);
        Assert.Empty(rejections);

        var profile = catalog.Match(An1641());
        Assert.Equal(ProfileCatalog.RemoteOrigin, profile.Source);
        Assert.True(profile.Sensors.Verified);
    }

    [Fact]
    public void RemoteProfile_SameIdChangedSensorIds_IsNotVerified()
    {
        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null,
            VerifiedDoc("nitro-16-an16-41", 5), out _);

        var profile = catalog.Match(An1641());
        Assert.Equal(ProfileCatalog.RemoteOrigin, profile.Source);
        Assert.False(profile.Sensors.Verified);
    }

    [Fact]
    public void UserProfile_FollowsTheSameVerifiedRule()
    {
        var same = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(),
            VerifiedDoc("nitro-16-an16-41", 1), out _).Match(An1641());
        Assert.Equal(ProfileCatalog.UserOrigin, same.Source);
        Assert.True(same.Sensors.Verified);

        var changed = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(),
            VerifiedDoc("nitro-16-an16-41", 5), out _).Match(An1641());
        Assert.False(changed.Sensors.Verified);

        var unknown = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(),
            VerifiedDoc("my-own-id", 1), out _).Match(An1641());
        Assert.Equal("my-own-id", unknown.Id);
        Assert.False(unknown.Sensors.Verified);
    }

    [Fact]
    public async Task RemoteProfile_VerifiedTrue_StillRunsTheSensorProbe()
    {
        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, RemoteWithVerified, out _);
        var layout = catalog.Match(An1641()).Sensors;

        var executor = new FakeExecutor();
        using var launcher = Launcher(executor);
        using var controller = new WmiHardwareController(launcher, new ReadBackPolicy(new AppSettings(), NoDelay),
            () => new CapabilitySnapshot { Sensors = layout });

        await controller.GetMonitorDataAsync();

        Assert.True(controller.SensorProbeRan);
    }

    [Fact]
    public void EmbeddedProfile_KeepsItsVerifiedFlag()
    {
        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _);
        Assert.Contains(catalog.Profiles, p => p.Source == ProfileCatalog.EmbeddedOrigin && p.Sensors.Verified);
    }

    // ---------------------------------------------------------------- TEST-05

    [Fact]
    public void AgentFanPayload_CustomZeroAndFive_NeverGoesBelowTheFloor()
    {
        var json = JsonSerializer.Serialize(AcerAgentClient.BuildFanControlParameter(FanMode.Custom, 0, 5));
        using var doc = JsonDocument.Parse(json);

        var fans = doc.RootElement.GetProperty("custom_fan_data");
        Assert.Equal(2, fans.GetArrayLength());
        foreach (var fan in fans.EnumerateArray())
        {
            Assert.True(fan.GetProperty("fan_custom_speed").GetInt32() >= AppSettings.MinCustomFanPercent);
        }
    }

    // ---------------------------------------------------------------- TEST-02

    [Fact]
    public void Desktop_HandsFansBackOnShutdownProcessExitAndUnhandledException()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "NitroLite.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var source = File.ReadAllText(Path.Combine(dir!.FullName, "NitroLite.Desktop", "App.axaml.cs"));

        Assert.Contains("desktop.ShutdownRequested += (_, _) => OnShutdownRequested();", source);
        Assert.Contains("AppDomain.CurrentDomain.ProcessExit += OnProcessExit;", source);
        Assert.Contains("AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;", source);

        // ...and each handler really reaches the handback.
        Assert.Matches(new Regex(@"void OnShutdownRequested\(\)\s*\{\s*try\s*\{\s*_host\?\.ViewModel\.RestoreFansToAutoBlocking"), source);
        Assert.Contains("OnProcessExit(object? sender, EventArgs e) => HandBackFansBestEffort(", source);
        Assert.Matches(new Regex(@"void OnUnhandledException\([^)]*\)\s*\{[^}]*HandBackFansBestEffort"), source);
    }
}
