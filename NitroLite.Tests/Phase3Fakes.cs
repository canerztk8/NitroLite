using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NitroLite.Core;
using NitroLite.Core.Hardware;

namespace NitroLite.Tests;

/// <summary>
/// The fakes the phase-3 page tests share: a battery, a drive list, the Acer service list, the repair tools, the update
/// checker, the bundle builder and the sign-in entry.
/// </summary>
/// <remarks>
/// Each one is a recording stub rather than a mock framework: the assertions are about what the view model asked for
/// and what it printed, so a list of calls is enough and a library would only hide the call order.
/// </remarks>
internal static class Phase3Fakes
{
    internal static string TempSettingsPath() =>
        Path.Combine(Path.GetTempPath(), "nitrolite-phase3-" + Guid.NewGuid().ToString("N") + ".json");

    /// <summary>
    /// Waits for a fire-and-forget command to have had its effect.
    /// </summary>
    /// <remarks>
    /// <c>AsyncCommand.Execute</c> is <c>async void</c> by design — a button click cannot await — so a test that called
    /// it would otherwise assert against the state from before the work started. Polling the effect is honest about
    /// that: it fails with a timeout rather than passing on a race.
    /// </remarks>
    internal static async Task WaitUntilAsync(Func<bool> condition, int timeoutMilliseconds = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The command did not reach the expected state in time.");
            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    /// <summary>A battery that answers one fixed snapshot and keeps its wear history in memory.</summary>
    internal sealed class FakeBattery : IBatteryHealthService
    {
        private readonly List<BatteryWearSample> _samples = new();

        public bool IsSupported { get; set; } = true;

        public bool WatchdogEnabled { get; set; } = true;

        /// <summary>How many times the composition root asked the service to start its loop.</summary>
        public int Starts { get; private set; }

        public void Start() => Starts++;

        public void Dispose()
        {
        }

        public BatteryHealthSnapshot Snapshot { get; set; } = new()
        {
            Status = new BatteryStatusInfo
            {
                BatteryPercent = 64,
                IsPluggedIn = false,
                IsDischarging = true,
                DischargeRateWatts = 12.4,
                EstimatedRemainingMinutes = 185
            },
            Details = new BatteryHealthDetails
            {
                DesignCapacityMWh = 90_000,
                FullChargeCapacityMWh = 82_800,
                HealthPercent = 92.0,
                WearLevelPercent = 8.0,
                VoltageVolts = 15.9,
                Chemistry = "Lityum-iyon",
                CycleCount = 143
            }
        };

        public int Reads { get; private set; }

        public int Clears { get; private set; }

        public Task<BatteryHealthSnapshot> ReadAsync(CancellationToken cancellationToken = default)
        {
            Reads++;
            return Task.FromResult(Snapshot);
        }

        public IReadOnlyList<BatteryWearSample> GetWearSamples() => _samples;

        public BatteryWearTrend GetWearTrend() => new() { Samples = _samples.ToArray() };

        public bool RecordWearSample(BatteryHealthDetails details)
        {
            _samples.Add(new BatteryWearSample
            {
                TimestampUtc = DateTime.UtcNow.AddDays(_samples.Count),
                HealthPercent = details.HealthPercent,
                WearLevelPercent = details.WearLevelPercent,
                FullChargeCapacityMWh = details.FullChargeCapacityMWh
            });
            return true;
        }

        public bool ClearWearHistory()
        {
            Clears++;
            _samples.Clear();
            return true;
        }

        /// <summary>Seeds a history without going through a read, so a trend test does not need three ticks.</summary>
        public void SeedWear(params double[] wearLevels) => SeedWearAt(TimeSpan.FromDays(7), wearLevels);

        /// <summary>
        /// Seeds a history at an explicit spacing. A week is a trend; minutes are two reads of the same afternoon,
        /// which is the case the chart has to refuse.
        /// </summary>
        public void SeedWearAt(TimeSpan spacing, params double[] wearLevels)
        {
            var start = DateTime.UtcNow - spacing * wearLevels.Length;
            for (int i = 0; i < wearLevels.Length; i++)
            {
                _samples.Add(new BatteryWearSample
                {
                    TimestampUtc = start + spacing * i,
                    HealthPercent = 100 - wearLevels[i],
                    WearLevelPercent = wearLevels[i],
                    FullChargeCapacityMWh = 90_000 * (1 - wearLevels[i] / 100)
                });
            }
        }
    }

    /// <summary>A drive list with one NVMe and one SATA entry.</summary>
    internal sealed class FakeStorage : IStorageHealthService
    {
        public bool IsSupported { get; set; } = true;

        public IReadOnlyList<StorageDrive> GetDrives() => new[]
        {
            new StorageDrive("C:", "Samsung SSD 990 PRO 1TB", "NVMe", 931.5, 412.3, 55.7, 41, "İyi", true),
            new StorageDrive("D:", "Crucial MX500 2TB", "SATA", 1863.0, 1204.8, 35.3, 33, "İyi", true)
        };
    }

    /// <summary>The Acer background services, with a call log so a test can see what was asked for.</summary>
    internal sealed class FakeDebloat : IDebloatService
    {
        public bool IsSupported { get; set; } = true;

        public bool HasRestorableBackup { get; set; }

        public bool NitroSenseRunning { get; set; } = true;

        public List<string> Calls { get; } = new();

        public IReadOnlyList<AcerServiceEntry> GetServiceStatuses() => new[]
        {
            new AcerServiceEntry("AASSvc", "Acer Agent Service", "Running", "Automatic", false),
            new AcerServiceEntry("AcerCCAgentSvis", "Acer Care Center Agent", "Stopped", "Manual", true)
        };

        public bool IsNitroSenseRunning() => NitroSenseRunning;

        public Task<DebloatResult> DisableBackgroundServicesAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("disable");
            return Task.FromResult(DebloatResult.Ok);
        }

        public Task<DebloatResult> RestoreServicesAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("restore");
            return Task.FromResult(DebloatResult.Ok);
        }

        public Task<DebloatResult> KillNitroSenseAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("kill");
            NitroSenseRunning = false;
            return Task.FromResult(DebloatResult.Ok);
        }

        /// <summary>Whether the launcher is currently renamed away, as the real service reads it from disk.</summary>
        public bool LauncherBlocked { get; set; }

        /// <summary>What the block call answers. False models a refused elevation prompt.</summary>
        public bool LauncherBlockResult { get; set; } = true;

        public bool IsNitroSenseLauncherBlocked => LauncherBlocked;

        public Task<DebloatResult> BlockNitroSenseLauncherAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("block-launcher");
            if (!LauncherBlockResult) return Task.FromResult(DebloatResult.Failed("denied"));
            LauncherBlocked = true;
            return Task.FromResult(DebloatResult.Ok);
        }

        public Task<DebloatResult> UnblockNitroSenseLauncherAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("unblock-launcher");
            if (!LauncherBlockResult) return Task.FromResult(DebloatResult.Failed("denied"));
            LauncherBlocked = false;
            return Task.FromResult(DebloatResult.Ok);
        }
    }

    /// <summary>The repair and optimisation tools, recording every call in order.</summary>
    internal sealed class FakeSystemTools : ISystemToolsService
    {
        public bool IsSupported { get; set; } = true;

        public List<string> Calls { get; } = new();

        public Task<ToolResult> FixKeyboardLightingAsync(CancellationToken cancellationToken = default) =>
            Record("lighting");

        public Task<ToolResult> FixAcerServicesAsync(CancellationToken cancellationToken = default) =>
            Record("services");

        public Task<ToolResult> RestoreMuxSwitchAsync(CancellationToken cancellationToken = default) =>
            Record("mux");

        public Task<ToolResult> OptimizeAsync(CancellationToken cancellationToken = default) => Record("optimize");

        public Task<ToolResult> RestoreOptimizeAsync(CancellationToken cancellationToken = default) =>
            Record("restore-optimize");

        private Task<ToolResult> Record(string name)
        {
            Calls.Add(name);
            return Task.FromResult(ToolResult.Ok(name));
        }
    }

    /// <summary>An update checker that never finds anything, so a test never touches the network.</summary>
    internal sealed class FakeUpdates : IUpdateService
    {
        public string CurrentVersionText => "v1.2.5";

        /// <summary>True models a published single-file build, which is the only one that can install over itself.</summary>
        public bool IsPackaged { get; set; }

        public string ReleasePageUrl => "https://example.invalid/releases";

        /// <summary>What the next check answers. Null means "already current".</summary>
        public UpdateCheckResult? CheckResult { get; set; }

        public Task<UpdateCheckResult?> CheckAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CheckResult);

        /// <summary>How many times an install was attempted, so a test can assert the button reached the pipeline.</summary>
        public int InstallAttempts { get; private set; }

        /// <summary>What the next install answers. Null means the refusal a non-packaged build gives.</summary>
        public UpdateInstallResult? InstallResult { get; set; }

        public Task<UpdateInstallResult> InstallAsync(
            UpdateCheckResult result, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            InstallAttempts++;
            progress?.Report(1);
            return Task.FromResult(InstallResult ?? UpdateInstallResult.Fail("not packaged"));
        }

        public string ReleaseNotesUrl(UpdateCheckResult? result) => ReleasePageUrl;

        public string BuildIssueBody(string version, string osDescription, string machineModel, string language) =>
            version;

        public string GetMachineModel() => "AN16-41";

        public string BuildIssueUrl(string title, string body) => ReleasePageUrl;
    }

    /// <summary>Captures URLs opened by the view model so a test can verify the link without opening a browser.</summary>
    internal sealed class UrlCapturer
    {
        public List<string> OpenedUrls { get; } = new();
        public Action<string> Capture => url => OpenedUrls.Add(url);
    }

    /// <summary>A bundle builder that writes a real file, so the note can name it.</summary>
    internal sealed class FakeDiagnostics : IDiagnosticsService
    {
        public bool IsSupported { get; set; } = true;

        public string DataDirectory => Path.GetTempPath();

        public string? LastPath { get; private set; }

        public string CreateBundle()
        {
            LastPath = Path.Combine(Path.GetTempPath(), "NitroLite-diagnostics-test.zip");
            File.WriteAllText(LastPath, "zip");
            return LastPath;
        }
    }

    /// <summary>The plain per-user sign-in entry, with the write recorded.</summary>
    internal sealed class FakeRunAtStartup : IRunAtStartupService
    {
        public bool IsSupported { get; set; } = true;

        public bool Enabled { get; private set; }

        public bool? LastStartMinimized { get; private set; }

        public Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default) => Task.FromResult(Enabled);

        public Task<bool> SetEnabledAsync(bool enabled, bool startMinimized,
            CancellationToken cancellationToken = default)
        {
            Enabled = enabled;
            LastStartMinimized = startMinimized;
            return Task.FromResult(true);
        }
    }

    /// <summary>
    /// The file pickers and the confirmation, answered from fixed values.
    /// </summary>
    /// <remarks>
    /// The real implementation opens a window, which a headless test cannot do. Every answer is settable so a test can
    /// drive both the accepted and the cancelled path.
    /// </remarks>
    internal sealed class FakeDialogs : IDialogService
    {
        public string? PickResult { get; set; }

        public string? SaveResult { get; set; }

        public bool ConfirmResult { get; set; } = true;

        public List<string> Confirms { get; } = new();

        public Task<string?> PickExecutableAsync(string? startDirectory = null,
            CancellationToken cancellationToken = default) => Task.FromResult(PickResult);

        public Task<string?> PickFileAsync(string title, string? extension = null,
            CancellationToken cancellationToken = default) => Task.FromResult(PickResult);

        public Task<string?> SaveFileAsync(string title, string suggestedName, string? extension = null,
            CancellationToken cancellationToken = default) => Task.FromResult(SaveResult);

        public Task<bool> ConfirmAsync(string title, string message,
            CancellationToken cancellationToken = default)
        {
            Confirms.Add(title);
            return Task.FromResult(ConfirmResult);
        }
    }

    /// <summary>A backend that answers one fixed capability set and accepts every write.</summary>
    internal sealed class StubHardware : IHardwareController
    {
        public string BackendName => "Test";

        /// <summary>
        /// What this backend reports. Settable so a test can describe a machine whose backend object exists but has
        /// never opened a transport, which is the state every Windows launch starts in.
        /// </summary>
        public bool IsAvailable { get; set; } = true;

        public List<string> Writes { get; } = new();

        /// <summary>
        /// How many times the view model asked the backend to connect. The probe tests use it to prove that a second
        /// <c>InitializeAsync</c> does not run the first read again.
        /// </summary>
        public int ConnectCount { get; private set; }

        public Task<bool> EnsureConnectedAsync()
        {
            ConnectCount++;
            return Task.FromResult(true);
        }

        /// <summary>
        /// What the backend says the machine can do. Settable so a test can describe a machine without a keyboard, a
        /// fan or a charge limit without writing a second stub.
        /// </summary>
        public HardwareCapabilities Capabilities { get; set; } = new()
        {
            Backend = "Test",
            ServiceAvailable = true,
            FanControl = true,
            OperatingModeControl = true,
            KeyboardLighting = true,
            BatteryChargeLimit = true,
            SupportedOperatingModes = new[]
            {
                OperatingMode.Quiet, OperatingMode.Default, OperatingMode.Extreme, OperatingMode.Turbo
            }
        };

        public Task<HardwareCapabilities> GetCapabilitiesAsync() => Task.FromResult(Capabilities);

        /// <summary>
        /// The sample the backend answers. Settable so a test can change a reading between two ticks and watch what
        /// the page does with the change.
        /// </summary>
        public MonitorData Sample { get; set; } = new()
        {
            CpuTemperature = 58,
            GpuTemperature = 51,
            SysTemperature = 44,
            CpuFanSpeed = 2200,
            GpuFanSpeed = 2150,
            IsFanRpmMeasured = true
        };

        public Task<MonitorData?> GetMonitorDataAsync() => Task.FromResult<MonitorData?>(Sample);

        public Task<OperatingMode?> GetOperatingModeAsync() => Task.FromResult<OperatingMode?>(OperatingMode.Default);

        public Task<bool> SetOperatingModeAsync(OperatingMode mode)
        {
            Writes.Add("mode:" + mode);
            return Task.FromResult(true);
        }

        public Task<GpuOperatingMode?> GetGpuModeAsync() => Task.FromResult<GpuOperatingMode?>(null);

        /// <summary>What the MUX write answers. False models a backend that refuses the mode.</summary>
        public bool GpuWriteResult { get; set; } = true;

        public List<GpuOperatingMode> GpuWrites { get; } = new();

        public Task<bool> SetGpuModeAsync(GpuOperatingMode mode)
        {
            GpuWrites.Add(mode);
            return Task.FromResult(GpuWriteResult);
        }

        public Task<int?> GetGpuModeCapabilityAsync() => Task.FromResult<int?>(null);

        /// <summary>What the backend says the fans are doing. Settable so a test can start in custom mode.</summary>
        public FanMode ReportedFanMode { get; set; } = FanMode.Auto;

        public Task<FanMode?> GetFanModeAsync() => Task.FromResult<FanMode?>(ReportedFanMode);

        /// <summary>
        /// Every fan write with the speed it asked for. Kept apart from <see cref="Writes"/> so a test can assert the
        /// percentage a curve produced without changing the order-only assertions the other tests make.
        /// </summary>
        public List<(FanMode Mode, int CpuPercent, int GpuPercent)> FanWrites { get; } = new();

        public Task<bool> SetFanModeAsync(FanMode mode, int cpuPercent = -1, int gpuPercent = -1)
        {
            Writes.Add("fan:" + mode);
            FanWrites.Add((mode, cpuPercent, gpuPercent));
            return Task.FromResult(true);
        }

        public Task<LightingConfig?> GetLightingConfigAsync() => Task.FromResult<LightingConfig?>(null);

        /// <summary>
        /// Every lighting write, in the order it arrived, as <c>effect|brightness|speed|direction|colour|zones</c>.
        /// Kept apart from <see cref="Writes"/> so the tests that count the other writes are not disturbed by it.
        /// </summary>
        public List<string> LightingWrites { get; } = new();

        public Task<bool> SetLightingAsync(string effect, int brightness, int speed, int direction,
            string primaryColor, string[]? zoneColors)
        {
            LightingWrites.Add(string.Join("|", effect, brightness, speed, direction, primaryColor,
                string.Join(",", zoneColors ?? Array.Empty<string>())));
            return Task.FromResult(true);
        }

        public Task<bool> TurnOffLightingAsync()
        {
            LightingWrites.Add("OFF");
            return Task.FromResult(true);
        }

        public Task<bool?> GetBacklightTimeoutAsync() => Task.FromResult<bool?>(false);

        public Task<bool> SetBacklightTimeoutAsync(bool enable30s) => Task.FromResult(true);

        public Task<BatteryChargeMode?> GetBatteryChargeModeAsync() =>
            Task.FromResult<BatteryChargeMode?>(BatteryChargeMode.Full);

        public Task<bool> SetBatteryChargeModeAsync(BatteryChargeMode mode)
        {
            Writes.Add("charge:" + mode);
            return Task.FromResult(true);
        }

        public Task<bool?> GetCoolBoostAsync() => Task.FromResult<bool?>(false);

        public Task<bool> SetCoolBoostAsync(bool on) => Task.FromResult(true);

        public Task<bool?> GetLcdOverdriveAsync() => Task.FromResult<bool?>(false);

        public Task<bool> SetLcdOverdriveAsync(bool on) => Task.FromResult(true);

        public Task<bool> SetWinKeyLockAsync(bool enabled) => Task.FromResult(true);

        public void Dispose() { }
    }
}
