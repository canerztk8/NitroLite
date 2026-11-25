using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Core.Hardware.Lighting;
using NitroLite.Core.Hardware.Wmi;
using NitroLite.Desktop;
using NitroLite.Linux;
using NitroLite.Windows;
using NitroLite.Windows.Core.Hardware.Lighting;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Cleanup B: the Windows services, the CLI and the release script. Nothing here reaches WMI, the EC, a service or
/// the registry for writing: every elevated path runs through its seam, and the PowerShell that is executed only
/// renames files in a temporary folder.
/// </summary>
public sealed class CleanupBTests : IDisposable
{
    public void Dispose()
    {
        SystemToolsService.ElevatedRunnerSeam = null;
        SystemToolsService.CurrentStateSeam = null;
        SystemToolsService.RepairServiceStateSeam = null;
        SystemToolsService.RepairMuxStateSeam = null;
        HidLightingDevices.ProbeOverride = null;
        HidLightingDevices.ResetCache();
    }

    // ================================================================ 1. the worker hands manual fans back

    private sealed class FanExecutor : IWmiExecutor
    {
        public List<(FanMode Cpu, FanMode Gpu)> Behaviours { get; } = new();
        public List<(int Cpu, int Gpu)> Speeds { get; } = new();
        public bool FailWrites { get; set; }

        /// <summary>Every write in order: "speed:60,60", "behaviour:Auto,Auto", "mode:Turbo".</summary>
        public List<string> Log { get; } = new();

        /// <summary>Called on every write, before it is recorded.</summary>
        public Action<string>? OnWrite { get; set; }

        private OperatingMode _mode = OperatingMode.Default;

        public bool IsAvailable => true;
        public WmiOperationResult Ping() => WmiOperationResult.Success();
        public WmiOperationResult GetCapabilities() => WmiOperationResult.Success();
        public WmiOperationResult ReadTelemetry(SensorQuery query) => WmiOperationResult.Success();

        public WmiOperationResult ReadDeviceState()
        {
            var data = new JsonObject { ["operatingMode"] = _mode.ToString() };
            if (Speeds.Count > 0)
            {
                data["cpuFanPercent"] = Speeds[^1].Cpu;
                data["gpuFanPercent"] = Speeds[^1].Gpu;
            }

            return WmiOperationResult.Success(data);
        }

        private void Record(string entry)
        {
            OnWrite?.Invoke(entry);
            Log.Add(entry);
        }

        public WmiOperationResult SetFanBehavior(FanMode cpu, FanMode gpu)
        {
            Record($"behaviour:{cpu},{gpu}");
            Behaviours.Add((cpu, gpu));
            return FailWrites ? WmiOperationResult.WmiFailed("test") : WmiOperationResult.Success();
        }

        public WmiOperationResult SetFanSpeed(int cpuPercent, int gpuPercent)
        {
            Record($"speed:{cpuPercent},{gpuPercent}");
            Speeds.Add((cpuPercent, gpuPercent));
            return FailWrites ? WmiOperationResult.WmiFailed("test") : WmiOperationResult.Success();
        }

        public WmiOperationResult SetOperatingMode(OperatingMode mode)
        {
            Record($"mode:{mode}");
            _mode = mode;
            return WmiOperationResult.Success();
        }
        public WmiOperationResult SetMuxMode(GpuOperatingMode mode) => WmiOperationResult.Success();
        public WmiOperationResult SetBatteryLimit(BatteryChargeMode mode) => WmiOperationResult.Success();
        public WmiOperationResult GetKeyboardBacklight() => WmiOperationResult.Success();
        public WmiOperationResult SetKeyboardLighting(KeyboardLightingRequest request) => WmiOperationResult.Success();
        public WmiOperationResult GetCoolBoost() => WmiOperationResult.Success();
        public WmiOperationResult SetCoolBoost(bool on) => WmiOperationResult.Success();
        public WmiOperationResult GetLcdOverdrive() => WmiOperationResult.Success();
        public WmiOperationResult SetLcdOverdrive(bool on) => WmiOperationResult.Success();
        public WmiOperationResult SetWinKeyLock(bool enabled) => WmiOperationResult.Success();
        public WmiOperationResult GetBacklightTimeout() => WmiOperationResult.Success();
        public WmiOperationResult SetBacklightTimeout(bool enabled) => WmiOperationResult.Success();
        public void Dispose() { }
    }

    private static WmiWorkerServer Server(FanExecutor executor) =>
        new("nitrolite-cleanupb-" + Guid.NewGuid().ToString("N"), executor);

    private static WmiWorkerRequest Speed(int cpu, int gpu) => new()
    {
        Command = WmiWorkerCommand.SetFanSpeed,
        Args = new JsonObject { ["cpuPercent"] = cpu, ["gpuPercent"] = gpu }
    };

    private static WmiWorkerRequest Behaviour(FanMode cpu, FanMode gpu) => new()
    {
        Command = WmiWorkerCommand.SetFanBehavior,
        Args = new JsonObject { ["cpu"] = (int)cpu, ["gpu"] = (int)gpu }
    };

    [Fact]
    public void Worker_AFixedFanSpeed_IsHandedBackToAutoWhenTheWindowIsGone()
    {
        var executor = new FanExecutor();
        using var server = Server(executor);

        server.Dispatch(Speed(60, 60));
        Assert.True(server.HoldsManualFans);

        Assert.True(server.ReleaseManualFans(TimeSpan.FromSeconds(5)));

        // Exactly the call the window uses for Auto, once.
        Assert.Equal(new[] { (FanMode.Auto, FanMode.Auto) }, executor.Behaviours);
        Assert.False(server.HoldsManualFans);
    }

    [Fact]
    public void Worker_FansAlreadyReleased_AreNotWrittenOnExit()
    {
        var executor = new FanExecutor();
        using var server = Server(executor);

        server.Dispatch(Speed(60, 60));
        server.Dispatch(Speed(0, 0)); // the release route: SetFanBehavior(Auto, Auto)
        Assert.False(server.HoldsManualFans);
        executor.Behaviours.Clear();

        Assert.True(server.ReleaseManualFans(TimeSpan.FromSeconds(5)));
        Assert.Empty(executor.Behaviours);
    }

    [Fact]
    public void Worker_CustomBehaviourCounts_AutoAndMaxDoNot()
    {
        var executor = new FanExecutor();
        using var server = Server(executor);

        server.Dispatch(Behaviour(FanMode.Custom, FanMode.Custom));
        Assert.True(server.HoldsManualFans);

        server.Dispatch(Behaviour(FanMode.Max, FanMode.Max));
        Assert.False(server.HoldsManualFans);

        server.Dispatch(Behaviour(FanMode.Custom, FanMode.Custom));
        server.Dispatch(Behaviour(FanMode.Auto, FanMode.Auto));
        Assert.False(server.HoldsManualFans);
    }

    [Fact]
    public void Worker_AFailedManualWrite_StillCounts_AFailedReleaseDoesNotClear()
    {
        var executor = new FanExecutor { FailWrites = true };
        using var server = Server(executor);

        // The write may have reached the EC half way; handing back is the safe answer.
        server.Dispatch(Speed(60, 60));
        Assert.True(server.HoldsManualFans);

        server.Dispatch(Speed(0, 0));
        Assert.True(server.HoldsManualFans);
    }

    [Fact]
    public void Worker_AnInvalidSpeedPair_DoesNotChangeOwnership()
    {
        var executor = new FanExecutor();
        using var server = Server(executor);

        server.Dispatch(Speed(0, 60)); // refused by the executor's router, nothing written
        Assert.False(server.HoldsManualFans);
    }

    // ================================================================ 15. the pipe name

    [Theory]
    [InlineData("nitrolite-wmi-0123456789abcdef0123456789abcdef", true)]
    [InlineData("p", true)]
    [InlineData("name.with_under-score", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData(@"\\.\pipe\x", false)]
    [InlineData("../x", false)]
    [InlineData("a b", false)]
    [InlineData("host/pipe", false)]
    [InlineData("pipé", false)]
    public void PipeName_IsAllowListed(string? name, bool expected)
    {
        Assert.Equal(expected, StartupMode.IsValidPipeName(name));
    }

    [Fact]
    public void PipeName_TooLong_IsRefused_AndMakesTheRequestInvalid()
    {
        var name = new string('a', StartupMode.MaxPipeNameLength + 1);
        Assert.False(StartupMode.IsValidPipeName(name));
        Assert.False(StartupMode.Parse(new[] { "--wmi-worker", "--pipe", name, "--parent-pid", "7" }).IsValidWorkerRequest);
        Assert.False(StartupMode.Parse(new[] { "--wmi-worker", "--pipe", @"\\host\pipe\x", "--parent-pid", "7" }).IsValidWorkerRequest);
    }

    // ================================================================ 4b. an early activation is not lost

    [Fact]
    public void SingleInstance_AnActivationBeforeTheFirstSubscriber_IsRaisedOnSubscribe()
    {
        using var guard = SingleInstanceGuard.Acquire();
        guard.WatchForWakeRequests();

        Assert.True(SingleInstanceGuard.PostWakeRequest());
        Thread.Sleep(2600); // the wake file is polled once a second

        int raised = 0;
        guard.ActivationRequested += () => Interlocked.Increment(ref raised);
        Assert.Equal(1, Volatile.Read(ref raised));

        // Raised once, not again for a second subscriber.
        int second = 0;
        guard.ActivationRequested += () => Interlocked.Increment(ref second);
        Assert.Equal(0, Volatile.Read(ref second));
    }

    // ================================================================ 13. the CLI

    [Fact]
    public void Cli_RestoreMux_AsksForYesInBothSpellings()
    {
        Assert.True(CliTaskRunner.RequiresConfirmation(new[] { "--cli", "restore-mux" }));
        Assert.True(CliTaskRunner.RequiresConfirmation(new[] { "--restore-mux" }));
        Assert.True(CliTaskRunner.IsConfirmed(new[] { "--cli", "restore-mux", "--yes" }));
        Assert.False(CliTaskRunner.RequiresConfirmation(new[] { "--cli", "optimize" }));
    }

    [Theory]
    [InlineData("foo", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("uninstall-cleanup", true)]
    [InlineData("RESTORE-MUX", true)]
    public void Cli_CommandNames_AreValidatedBeforeAnythingIsBuilt(string? command, bool expected)
    {
        Assert.Equal(expected, CliTaskRunner.IsKnownCommand(command));
    }

    [Fact]
    public void Cli_UnknownFlags_AreReported_KnownOnesAndValuesAreNot()
    {
        var unknown = CliTaskRunner.FindUnknownFlags(new[]
        {
            "--minimzed", "--autostart", "--mode", "turbo", "--pipe=x", "--fix-lighting", "-y", "--frobnicate=1"
        });

        Assert.Equal(new[] { "--minimzed", "--frobnicate=1" }, unknown);
        Assert.Empty(CliTaskRunner.FindUnknownFlags(null));
    }

    [Fact]
    public async Task Cli_UninstallCleanup_RunsEveryStepInOrder_ReportsEach_AndEndsWithFansOnAuto()
    {
        var hardware = new Phase3Fakes.StubHardware();
        var settings = new AppSettings { FanCurveEnabled = true, SchoolModeEnabled = true };
        var order = new List<string>();
        var output = new StringWriter();

        var steps = new List<CliCleanupStep>
        {
            new("one", _ => { order.Add("one"); return Task.FromResult<(bool, string?)>((true, null)); }),
            new("two", _ => { order.Add("two"); return Task.FromResult<(bool, string?)>((false, "refused")); }),
            new("three", _ => { order.Add("three"); throw new InvalidOperationException("boom"); }),
            new("four", _ => { order.Add("four"); return Task.FromResult<(bool, string?)>((true, null)); })
        };

        int code = await CliTaskRunner.RunAsync("uninstall-cleanup", hardware, settings, null, steps, output);

        // A failed step does not stop the rest, and the result says something failed.
        Assert.Equal(new[] { "one", "two", "three", "four" }, order);
        Assert.Equal(1, code);
        Assert.Equal(FanMode.Auto, hardware.FanWrites.Last().Mode);
        Assert.False(settings.FanCurveEnabled);
        Assert.False(settings.SchoolModeEnabled);

        var text = output.ToString();
        Assert.Contains("ok    one", text);
        Assert.Contains("FAIL  two: refused", text);
        Assert.Contains("FAIL  three: boom", text);
        Assert.Contains("ok    four", text);
    }

    // ================================================================ 5. Debloat

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nl-cleanupb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Runs the generated script for real against files in a temporary folder (no elevation, no services).</summary>
    private static Func<string, bool, Task<int>> ScriptRunner(string dir) => async (script, _) =>
    {
        var path = Path.Combine(dir, "run-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(path, script, new System.Text.UTF8Encoding(true));
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + path + "\"",
            UseShellExecute = false,
            CreateNoWindow = true
        });
        Assert.NotNull(process);
        await process!.WaitForExitAsync();
        return process.ExitCode;
    };

    [Fact]
    public async Task Launcher_ReinstalledBesideAStaleBlockedCopy_CanBeBlockedAgain()
    {
        var dir = TempDir();
        try
        {
            var launcher = Path.Combine(dir, "NitroSenseLauncher.exe");
            File.WriteAllText(launcher, "new");
            File.WriteAllText(launcher + DebloatService.LauncherBlockedSuffix, "stale");
            var service = new DebloatService { LauncherPathOverride = launcher, PowerShellRunner = ScriptRunner(dir) };

            Assert.True(await service.BlockNitroSenseLauncherAsync(), service.LastError);

            Assert.False(File.Exists(launcher));
            Assert.Equal("new", File.ReadAllText(launcher + DebloatService.LauncherBlockedSuffix));
            Assert.True(service.IsNitroSenseLauncherBlocked);
        }
        finally
        {
            try { TestDataIsolation.DeleteFolder(dir); } catch { }
        }
    }

    [Fact]
    public async Task Launcher_ReinstalledBesideAStaleBlockedCopy_UnblockRemovesTheStaleCopy()
    {
        var dir = TempDir();
        try
        {
            var launcher = Path.Combine(dir, "NitroSenseLauncher.exe");
            File.WriteAllText(launcher, "new");
            File.WriteAllText(launcher + DebloatService.LauncherBlockedSuffix, "stale");
            var service = new DebloatService { LauncherPathOverride = launcher, PowerShellRunner = ScriptRunner(dir) };

            Assert.True(await service.UnblockNitroSenseLauncherAsync(), service.LastError);

            Assert.Equal("new", File.ReadAllText(launcher));
            Assert.False(File.Exists(launcher + DebloatService.LauncherBlockedSuffix));
        }
        finally
        {
            try { TestDataIsolation.DeleteFolder(dir); } catch { }
        }
    }

    [Fact]
    public async Task Debloat_AnEmptyButValidBackup_IsOverwritten_NotReportedAsInvalid()
    {
        var dir = TempDir();
        try
        {
            var backupPath = Path.Combine(dir, "debloat_backup_state.json");
            File.WriteAllText(backupPath, "{}");
            string? script = null;
            var service = new DebloatService
            {
                BackupStateFilePath = backupPath,
                ServiceStartupTypeQuerySeam = _ => "Automatic",
                ServiceStatusQuerySeam = _ => "Running",
                ScheduledTaskStateQuerySeam = _ => "Disabled",
                PowerShellRunner = (s, _) => { script = s; return Task.FromResult(0); }
            };

            Assert.True(await service.ApplyTier1DebloatAsync(), service.LastError);

            var saved = service.LoadBackupState();
            Assert.All(DebloatService.Tier1Services, s => Assert.Equal("Automatic", saved[s]));
            Assert.NotNull(script);
        }
        finally
        {
            try { TestDataIsolation.DeleteFolder(dir); } catch { }
        }
    }

    [Fact]
    public void Debloat_ACorruptOrServiceNamingBackup_IsStillNotValidButEmpty()
    {
        var dir = TempDir();
        try
        {
            var service = new DebloatService { BackupStateFilePath = Path.Combine(dir, "b.json") };

            File.WriteAllText(service.BackupStateFilePath, "{}");
            Assert.True(service.IsValidButEmptyBackupFile());

            File.WriteAllText(service.BackupStateFilePath, "{\"Task:NitroSenseLauncher\":\"Ready\"}");
            Assert.True(service.IsValidButEmptyBackupFile());

            File.WriteAllText(service.BackupStateFilePath, "not json");
            Assert.False(service.IsValidButEmptyBackupFile());

            File.WriteAllText(service.BackupStateFilePath, "{\"ASMSvc\":\"Bogus\"}");
            Assert.False(service.IsValidButEmptyBackupFile());
        }
        finally
        {
            try { TestDataIsolation.DeleteFolder(dir); } catch { }
        }
    }

    [Fact]
    public void Debloat_RestoreScript_SkipsAMissingService_AndRequiresOnlyTheStartType()
    {
        var script = new DebloatService().GenerateRestoreScript(new Dictionary<string, string>
        {
            ["ASMSvc"] = "Automatic",
            ["ASMSvc:Status"] = "Running"
        });

        Assert.Contains("$svc = Get-Service -Name 'ASMSvc' -ErrorAction SilentlyContinue; if ($svc) {", script);
        Assert.Contains("Start-Service -Name 'ASMSvc' -ErrorAction SilentlyContinue", script);
        Assert.Contains("if ($vSvc.StartType -ne 'Automatic') { throw", script);
        Assert.Contains("if ($vSvc.Status -ne 'Running') { $warns += 'ASMSvc' }", script);
        Assert.DoesNotContain("Start-Service -Name 'ASMSvc' -ErrorAction Stop", script);
    }

    [Fact]
    public async Task Debloat_PartialRestore_KeepsOnlyTheServicesThatStillFailed()
    {
        var dir = TempDir();
        try
        {
            var service = new DebloatService
            {
                BackupStateFilePath = Path.Combine(dir, "b.json"),
                ServiceStartupTypeQuerySeam = _ => "Disabled"
            };
            service.SaveBackupState(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ASMSvc"] = "Automatic", ["ASMSvc:Status"] = "Running",
                ["AcerEZSvc"] = "Manual", ["AcerEZSvc:Status"] = "Stopped",
                ["Task:NitroSenseLauncher"] = "Ready"
            });

            service.PowerShellRunner = (script, _) =>
            {
                var errLog = Regex.Match(script, @"\$errLog = '([^']+)'").Groups[1].Value;
                File.WriteAllText(errLog, "ASMSvc: access denied");
                return Task.FromResult(1);
            };

            Assert.False(await service.RestoreTier1ServicesAsync());

            var left = service.LoadBackupState();
            Assert.Equal("Automatic", left["ASMSvc"]);
            Assert.Equal("Running", left["ASMSvc:Status"]);
            Assert.False(left.ContainsKey("AcerEZSvc"));
            Assert.False(left.ContainsKey("Task:NitroSenseLauncher"));
            Assert.Contains("access denied", service.LastError);
        }
        finally
        {
            try { TestDataIsolation.DeleteFolder(dir); } catch { }
        }
    }

    [Fact]
    public void Debloat_PruneToFailures_IgnoresTextThatNamesNothing()
    {
        var backup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["ASMSvc"] = "Automatic" };

        Assert.Null(DebloatService.PruneBackupToFailures(backup, "the user declined the prompt"));
        Assert.Null(DebloatService.PruneBackupToFailures(backup, null));
        Assert.NotNull(DebloatService.PruneBackupToFailures(backup, "x\nASMSvc: boom"));
    }

    [Fact]
    public async Task Debloat_Scripts_CarryTheBoundedServiceCommands()
    {
        string? script = null;
        var service = new DebloatService { PowerShellRunner = (s, _) => { script = s; return Task.FromResult(0); } };

        await service.RunElevatedPowerShellAsync("exit 0");

        Assert.NotNull(script);
        Assert.StartsWith(SystemToolsService.BoundedServiceCommands, script, StringComparison.Ordinal);
        Assert.Contains("function Stop-Service", script);
    }

    // ================================================================ 6. the HAGS undo record

    [Fact]
    public void Optimize_ARecordedAbsentHags_IsNeverReplacedByALaterReading()
    {
        var record = SystemToolsService.ParseOptimizeBackup("""{"services":{},"hags":-1,"tasks":[]}""");
        Assert.True(record.HagsRecorded);

        SystemToolsService.MergeMissing(record, new SystemToolsService.OptimizeSnapshot(
            new Dictionary<string, string>(), 2, new Dictionary<string, string>()));

        Assert.Equal(-1, record.Hags);
    }

    [Fact]
    public void Optimize_AnUnreadableHags_IsNotRecorded_AndNotChanged()
    {
        var record = new SystemToolsService.OptimizeBackupRecord();

        SystemToolsService.MergeMissing(record, new SystemToolsService.OptimizeSnapshot(
            new Dictionary<string, string>(), -1, new Dictionary<string, string>(), HagsKnown: false));

        Assert.False(record.HagsRecorded);
        var json = SystemToolsService.SerializeOptimizeBackup(record, indented: false);
        Assert.DoesNotContain("hags", json);
        Assert.False(SystemToolsService.ParseOptimizeBackup(json).HagsRecorded);

        Assert.Contains("$setHags = $false", SystemToolsService.BuildOptimizeScript(Array.Empty<string>(), setHags: false));
        Assert.Contains("$setHags = $true", SystemToolsService.BuildOptimizeScript(Array.Empty<string>()));
    }

    // ================================================================ 5c. restore optimize after a removed service

    [Fact]
    public async Task RestoreOptimize_SkipsARemovedService_AndKeepsOnlyWhatStillFailed()
    {
        File.WriteAllText(SystemToolsService.OptimizeBackupPath,
            """{"services":{"SysMain":"Automatic","ClickToRunSvc":"Automatic"},"hags":1,"tasks":[]}""");

        string? script = null;
        SystemToolsService.ElevatedRunnerSeam = s => { script = s; return Task.FromResult(1); };
        await SystemToolsService.RestoreOptimizePcAsync();

        Assert.NotNull(script);
        Assert.Contains("if (-not (Get-Service -Name $prop.Name -ErrorAction SilentlyContinue)) { continue }", script);

        var record = SystemToolsService.ParseOptimizeBackup(
            """{"services":{"SysMain":"Automatic","ClickToRunSvc":"Automatic"},"hags":1,"tasks":[]}""");
        var pruned = SystemToolsService.PruneOptimizeRecordToFailures(record, "SysMain: access denied\nHAGS: nope");
        Assert.NotNull(pruned);
        Assert.Equal(new[] { "SysMain" }, pruned!.Services.Keys.ToArray());
        Assert.True(pruned.HagsRecorded);
        Assert.Equal(1, pruned.Hags);

        Assert.Null(SystemToolsService.PruneOptimizeRecordToFailures(record, "İşlem kodu: 1"));
        try { File.Delete(SystemToolsService.OptimizeBackupPath); } catch { }
    }

    // ================================================================ 7. the repair undo record

    [Fact]
    public async Task Repair_RecordsThePreviousValuesBeforeElevating_AndTheUndoValidatesThem()
    {
        try { File.Delete(SystemToolsService.RepairBackupPath); } catch { }
        SystemToolsService.RepairServiceStateSeam = name => ($@"%SystemRoot%\System32\DriverStore\FileRepository\old\{name}.exe", 2);

        string? recordAtElevation = null;
        SystemToolsService.ElevatedRunnerSeam = _ =>
        {
            recordAtElevation = File.Exists(SystemToolsService.RepairBackupPath)
                ? File.ReadAllText(SystemToolsService.RepairBackupPath)
                : null;
            return Task.FromResult(0);
        };

        await SystemToolsService.FixAcerServicesAsync();

        Assert.NotNull(recordAtElevation);
        var clean = SystemToolsService.ParseRepairBackup(recordAtElevation!);
        Assert.NotNull(clean["services"]?["AASSvc"]);
        Assert.NotNull(clean["services"]?["AcerLightingService"]);

        string? undoScript = null;
        SystemToolsService.ElevatedRunnerSeam = s => { undoScript = s; return Task.FromResult(0); };
        var undo = await SystemToolsService.UndoLastRepairAsync();

        Assert.True(undo.Success, undo.Message);
        Assert.NotNull(undoScript);
        Assert.Contains("Resolve-TrustedImage", undoScript);
        Assert.Contains("Test-AcerSigned", undoScript);
        Assert.False(File.Exists(SystemToolsService.RepairBackupPath));
    }

    [Fact]
    public void Repair_ParseKeepsOnlyKnownServicesAndValues()
    {
        var clean = SystemToolsService.ParseRepairBackup("""
            {"services":{
               "AASSvc":{"imagePath":"C:\\x.exe","start":2},
               "Evil":{"imagePath":"C:\\evil.exe","start":2},
               "AcerLightingService":{"imagePath":"C:\\y.exe","start":0}},
             "mux":{"present":true,"value":7}}
            """);

        var services = clean["services"]!.AsObject();
        Assert.Single(services);
        Assert.NotNull(services["AASSvc"]);
        Assert.Null(clean["mux"]);
    }

    [Fact]
    public async Task FixAcerServices_PrefersTheFolderTheServicesAlreadyRunFrom()
    {
        string? script = null;
        SystemToolsService.ElevatedRunnerSeam = s => { script = s; return Task.FromResult(0); };
        SystemToolsService.RepairServiceStateSeam = _ => (null, null);

        await SystemToolsService.FixAcerServicesAsync();

        Assert.NotNull(script);
        Assert.Contains("function Get-ImageFolder", script);
        Assert.Contains("$dirs = @($preferred) + @(", script);
    }

    // ================================================================ 8. the memory cleaner runs from a protected copy

    [Fact]
    public void MemoryCleaner_LaunchesAVerifiedCopyFromAnAdminOnlyFolder()
    {
        var script = MemoryCleanerService.BuildIsolatedLaunchScript(@"C:\Users\x\AppData\Local\NitroLite\tools\WinMemoryCleaner.exe");

        Assert.Contains("SetAccessRuleProtection($true, $false)", script);
        Assert.Contains("'S-1-5-32-544', 'S-1-5-18'", script);
        Assert.Contains("Get-FileHash -LiteralPath $dst -Algorithm SHA256", script);
        Assert.Contains(MemoryCleanerService.KnownGoodSha256[0], script);
        Assert.Contains("Start-Process -FilePath $dst", script);
        Assert.Contains("-WorkingDirectory $dir", script);
        Assert.DoesNotContain("Start-Process -FilePath $src", script);
        Assert.Contains("Remove-Item -LiteralPath $dir -Recurse -Force", script);
    }

    // ================================================================ 9. the NitroSense dialog rule

    private static ForegroundWindowInfo Dialog(string process, string title, string content = "") =>
        new(42, process, NitroSenseWatcher.DialogClassName, title, content, new IntPtr(7));

    [Fact]
    public void NitroSenseDialog_FromExplorerOrAFilePicker_IsLeftAlone()
    {
        Assert.Equal(NitroSenseWindowKind.None,
            NitroSenseWatcher.Classify(Dialog("explorer", "NitroSense Properties", @"C:\Program Files\NitroSense"), false));
        Assert.Equal(NitroSenseWindowKind.None,
            NitroSenseWatcher.Classify(Dialog("notepad", "Open", "NitroSense"), false));
    }

    [Fact]
    public void NitroSenseDialog_FromTheAcerStack_IsStillThePermissionPrompt()
    {
        Assert.Equal(NitroSenseWindowKind.ErrorDialog,
            NitroSenseWatcher.Classify(Dialog("AcerHardwareService", "Error", "NitroSenseforNotebook"), false));
        Assert.Equal(NitroSenseWindowKind.ErrorDialog,
            NitroSenseWatcher.Classify(Dialog("SenseOverlay", "NitroSense"), false));
        Assert.Equal(NitroSenseWindowKind.ErrorDialog,
            NitroSenseWatcher.Classify(Dialog("whatever", "NitroSense"), processIsNitroSense: true));
    }

    // ================================================================ 10. game libraries, one game per folder

    private static void FakeGame(string folder, string exeName)
    {
        Directory.CreateDirectory(folder);
        using var stream = File.Create(Path.Combine(folder, exeName));
        stream.SetLength(GameLibraryRules.MinimumGameExecutableBytes + 1024);
    }

    [Fact]
    public void WindowsLibrary_TwoLibraries_ListEveryGame_NotOnePerLibrary()
    {
        var dir = TempDir();
        try
        {
            var libA = Path.Combine(dir, "A", "steamapps", "common");
            var libB = Path.Combine(dir, "B", "steamapps", "common");
            FakeGame(Path.Combine(libA, "Alpha Game"), "Alpha.exe");
            FakeGame(Path.Combine(libA, "Beta Game"), "Beta.exe");
            FakeGame(Path.Combine(libB, "Gamma Game"), "Gamma.exe");
            FakeGame(Path.Combine(libB, "Steamworks Shared"), "Redist.exe");

            var folders = WindowsGameLibraryScanner.ExpandLibraryRoots(new[] { libA, libB });
            var games = WindowsGameLibraryScanner.ScanGameFolders(folders);

            Assert.Equal(new[] { "Alpha", "Beta", "Gamma" }, games.Select(g => g.ExeName).OrderBy(n => n).ToArray());
            Assert.DoesNotContain(games, g => g.DisplayName.Equals("common", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { TestDataIsolation.DeleteFolder(dir); } catch { }
        }
    }

    [Fact]
    public void LinuxLibrary_TheHomeRootAndASecondLibrary_ListEveryGame()
    {
        var home = TempDir();
        var second = TempDir();
        try
        {
            var steam = Path.Combine(home, ".steam", "steam", "steamapps");
            FakeGame(Path.Combine(steam, "common", "Alpha Game"), "Alpha.exe");
            FakeGame(Path.Combine(steam, "common", "Beta Game"), "Beta.exe");
            FakeGame(Path.Combine(second, "steamapps", "common", "Gamma Game"), "Gamma.exe");
            File.WriteAllText(Path.Combine(steam, "libraryfolders.vdf"),
                "\"libraryfolders\"\n{\n \"1\"\n {\n  \"path\" \"" + second.Replace(@"\", @"\\") + "\"\n }\n}\n");

            var games = new LinuxGameLibraryScanner(home).ScanInstalledGames();

            Assert.Equal(new[] { "Alpha", "Beta", "Gamma" }, games.Select(g => g.ExeName).OrderBy(n => n).ToArray());
        }
        finally
        {
            try { TestDataIsolation.DeleteFolder(home); } catch { }
            try { TestDataIsolation.DeleteFolder(second); } catch { }
        }
    }

    // ================================================================ 11. device paths are not local libraries

    [Theory]
    [InlineData(@"\\?\UNC\host\share\steam", false)]
    [InlineData(@"\??\UNC\host\share\steam", false)]
    [InlineData(@"\\.\PhysicalDrive0", false)]
    [InlineData(@"\\?\C:\Games", false)]
    [InlineData(@"D:\SteamLibrary", true)]
    [InlineData("c:/program files (x86)/steam", true)]
    public void SteamLibraryPath_AcceptsOnlyADriveLetterPath(string raw, bool expected)
    {
        Assert.Equal(expected, SteamLibraryPaths.TryNormalize(raw, out _));
    }

    // ================================================================ 12. an effect the protocol cannot build

    private sealed class EneWriter : IHidLightingWriter
    {
        public int Writes { get; private set; }
        public bool Disposed { get; private set; }
        public string Description => "test";
        public string Protocol => "ene-kb5130";
        public bool CanWrite => !Disposed;
        public string? Refusal => null;

        public bool TryWrite(KeyboardLightingFrame frame, out string? error)
        {
            Writes++;
            error = null;
            return true;
        }

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void HidLighting_AnUnbuildableEffect_IsRefusedWithoutDroppingTheDevice()
    {
        var inner = new EneWriter();
        int probes = 0;
        HidLightingDevices.ResetCache();
        HidLightingDevices.ProbeOverride = () => { probes++; return inner; };

        var writer = HidLightingDevices.FindWriter();
        Assert.NotNull(writer);

        var meteor = new KeyboardLightingFrame { Effect = "METEOR", BrightnessPercent = 50 };
        Assert.False(writer!.TryWrite(meteor, out var error));
        Assert.False(string.IsNullOrEmpty(error));

        Assert.Equal(0, inner.Writes);
        Assert.False(inner.Disposed);
        Assert.Same(writer, HidLightingDevices.FindWriter());
        Assert.Equal(1, probes);

        Assert.True(writer.TryWrite(new KeyboardLightingFrame { Effect = "STATIC", BrightnessPercent = 50 }, out _));
        Assert.Equal(1, inner.Writes);
    }

    // ================================================================ review round: who owns the fans on disconnect

    /// <summary>
    /// One real pipe session through the host's serve-and-hand-back path: the client optionally opts out, writes a
    /// fixed speed and disconnects. <paramref name="parentExited"/> stands in for the watchdog.
    /// </summary>
    private static async Task<FanExecutor> FanSessionAsync(bool optOut)
    {
        var executor = new FanExecutor();
        var pipe = "nitrolite-cleanupb-" + Guid.NewGuid().ToString("N");
        using var server = new WmiWorkerServer(pipe, executor);
        var serve = Task.Run(() => WmiWorkerHost.ServeAndHandBackAsync(
            server, TimeSpan.FromSeconds(15), CancellationToken.None));

        using (var client = await WmiWorkerClient.ConnectAsync(pipe, WmiWorkerClient.GenerateNonce(), TimeSpan.FromSeconds(5)))
        {
            if (optOut)
            {
                var declared = await client.SendAsync(WmiWorkerCommand.SetFanHandBack, new JsonObject { ["handBack"] = false });
                Assert.True(declared.Ok);
            }

            var write = await client.SendAsync(WmiWorkerCommand.SetFanSpeed,
                new JsonObject { ["cpuPercent"] = 60, ["gpuPercent"] = 60 });
            Assert.True(write.Ok);
        }

        var (served, busy) = await serve.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(served);
        Assert.False(busy);
        return executor;
    }

    [Fact]
    public async Task WorkerHost_TheWindowDiesWithAFixedSpeed_FansGoBackToAuto()
    {
        var executor = await FanSessionAsync(optOut: false);

        Assert.Equal(new[] { (FanMode.Auto, FanMode.Auto) }, executor.Behaviours);
    }

    [Fact]
    public async Task WorkerHost_AOneShotFanCommand_LeavesItsSpeedInPlace()
    {
        // The documented --fan behaviour: the speed stays after the command exits.
        var executor = await FanSessionAsync(optOut: true);

        Assert.Empty(executor.Behaviours);
    }

    /// <summary>
    /// A launcher whose every start is a fresh in-process worker on its own pipe, so a controller can be driven through
    /// real connections, drops and reconnects without an elevated process.
    /// </summary>
    private sealed class WorkerFarm : IDisposable
    {
        public List<(WmiWorkerServer Server, FanExecutor Executor)> Workers { get; } = new();
        public WmiWorkerLauncher Launcher { get; }

        public WorkerFarm(Action<WmiWorkerServer, FanExecutor>? configure = null)
        {
            Launcher = new WmiWorkerLauncher(
                executablePath: "NitroLite.exe",
                start: _ => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c exit")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                }),
                connect: async (_, nonce, _, _, ct) =>
                {
                    var pipe = "nitrolite-cleanupb-" + Guid.NewGuid().ToString("N");
                    var executor = new FanExecutor();
                    var server = new WmiWorkerServer(pipe, executor);
                    configure?.Invoke(server, executor);
                    lock (Workers) Workers.Add((server, executor));
                    _ = Task.Run(() => server.RunAsync(TimeSpan.FromSeconds(15)));
                    return await WmiWorkerClient.ConnectAsync(pipe, nonce, TimeSpan.FromSeconds(5), null, ct);
                },
                isElevated: () => true,
                isTrustedLocation: _ => true);
        }

        public void Dispose()
        {
            Launcher.Dispose();
            lock (Workers)
            {
                foreach (var (server, _) in Workers) server.Dispose();
            }
        }
    }

    private static Task NoDelay(TimeSpan _) => Task.CompletedTask;

    [Fact]
    public async Task Controller_AfterADroppedWorker_ResendsTheLastManualFanCommandToTheNewOne()
    {
        using var farm = new WorkerFarm();
        using var controller = new WmiHardwareController(farm.Launcher, new ReadBackPolicy(new AppSettings(), NoDelay));

        await controller.SetFanModeAsync(FanMode.Custom, 60, 60);
        Assert.Contains("speed:60,60", farm.Workers[0].Executor.Log);

        // The pipe breaks (a timed-out call, a crashed worker): the controller drops the worker and the next write
        // starts a new one.
        farm.Workers[0].Server.Dispose();
        for (int attempt = 0; attempt < 3 && farm.Workers.Count < 2; attempt++)
            await controller.SetOperatingModeAsync(OperatingMode.Turbo);

        Assert.Equal(2, farm.Workers.Count);
        var log = farm.Workers[1].Executor.Log;
        Assert.Equal("speed:60,60", log[0]);
        Assert.Contains("mode:Turbo", log);
    }

    [Fact]
    public async Task Controller_AfterAutoWasAccepted_ResendsNothing()
    {
        using var farm = new WorkerFarm();
        using var controller = new WmiHardwareController(farm.Launcher, new ReadBackPolicy(new AppSettings(), NoDelay));

        await controller.SetFanModeAsync(FanMode.Custom, 60, 60);
        await controller.SetFanModeAsync(FanMode.Auto);

        farm.Workers[0].Server.Dispose();
        for (int attempt = 0; attempt < 3 && farm.Workers.Count < 2; attempt++)
            await controller.SetOperatingModeAsync(OperatingMode.Turbo);

        Assert.Equal(2, farm.Workers.Count);
        Assert.DoesNotContain(farm.Workers[1].Executor.Log, e => e.StartsWith("speed:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Controller_LeaveFansOnDisconnect_DeclaresItBeforeTheFirstCommand()
    {
        var observed = new List<bool>();
        using var farm = new WorkerFarm((server, executor) =>
            executor.OnWrite = _ => observed.Add(server.HandBackOnDisconnect));

        WmiHardwareController.LeaveFansOnDisconnect = true;
        try
        {
            using var controller = new WmiHardwareController(farm.Launcher, new ReadBackPolicy(new AppSettings(), NoDelay));
            await controller.SetFanModeAsync(FanMode.Custom, 60, 60);
        }
        finally
        {
            WmiHardwareController.LeaveFansOnDisconnect = false;
        }

        Assert.NotEmpty(observed);
        Assert.False(observed[0]); // already opted out when the first write arrived
    }

    [Fact]
    public async Task Controller_WithoutTheFlag_LeavesTheWorkerOnItsSafeDefault()
    {
        var observed = new List<bool>();
        using var farm = new WorkerFarm((server, executor) =>
            executor.OnWrite = _ => observed.Add(server.HandBackOnDisconnect));
        using var controller = new WmiHardwareController(farm.Launcher, new ReadBackPolicy(new AppSettings(), NoDelay));

        await controller.SetFanModeAsync(FanMode.Custom, 60, 60);

        Assert.NotEmpty(observed);
        Assert.All(observed, Assert.True);
    }

    [Fact]
    public void Launcher_HoldsNoKillOnCloseJob_SoAKilledWindowCannotKillTheWorkerBeforeItsHandBack()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "NitroLite.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var source = File.ReadAllText(Path.Combine(dir!.FullName,
            "NitroLite.Windows", "Core", "Hardware", "Wmi", "WmiWorkerLauncher.cs"));

        Assert.DoesNotContain("CreateJobObject", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AssignProcessToJobObject", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SingleInstance_LocalAfterAGlobalFailureThatWasNotAnotherSession_OwnsTheMachine()
    {
        Assert.True(SingleInstanceGuard.OwnsMachineWideFor(SingleInstanceGuard.MutexName, refusedByAnotherSession: false));
        Assert.True(SingleInstanceGuard.OwnsMachineWideFor(SingleInstanceGuard.MutexNameFallback, refusedByAnotherSession: false));
        Assert.False(SingleInstanceGuard.OwnsMachineWideFor(SingleInstanceGuard.MutexNameFallback, refusedByAnotherSession: true));
    }

    [Fact]
    public void SingleInstance_AHarnessCopy_IsAllowedToWrite()
    {
        var previous = Environment.GetEnvironmentVariable(SingleInstanceGuard.AllowSecondInstanceVariable);
        Environment.SetEnvironmentVariable(SingleInstanceGuard.AllowSecondInstanceVariable, "1");
        try
        {
            using var guard = SingleInstanceGuard.Acquire();
            Assert.True(guard.IsFirstInstance);
            Assert.True(guard.OwnsMachineWide);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SingleInstanceGuard.AllowSecondInstanceVariable, previous);
        }
    }

    [Fact]
    public void Worker_SetFanHandBack_IsValidated_AndTouchesNoHardware()
    {
        var executor = new FanExecutor();
        using var server = Server(executor);

        Assert.False(server.Dispatch(new WmiWorkerRequest { Command = WmiWorkerCommand.SetFanHandBack }).Ok);
        Assert.False(server.Dispatch(new WmiWorkerRequest
        {
            Command = WmiWorkerCommand.SetFanHandBack,
            Args = new JsonObject { ["handBack"] = "false" }
        }).Ok);
        Assert.True(server.HandBackOnDisconnect);

        Assert.True(server.Dispatch(new WmiWorkerRequest
        {
            Command = WmiWorkerCommand.SetFanHandBack,
            Args = new JsonObject { ["handBack"] = false }
        }).Ok);
        Assert.False(server.HandBackOnDisconnect);
        Assert.Empty(executor.Behaviours);
        Assert.Empty(executor.Speeds);
    }

    // ================================================================ review round: the refused Global\ name

    [Fact]
    public void SingleInstance_RefusedGlobal_AnElevatedWindowInThisSession_IsAlreadyRunning()
    {
        var list = new[]
        {
            new SingleInstanceGuard.SessionProcess(10, 1, null),                         // this process
            new SingleInstanceGuard.SessionProcess(20, 1, null)                          // elevated window: no command line
        };

        Assert.Equal(SingleInstanceGuard.RefusalDecision.AlreadyRunning,
            SingleInstanceGuard.DecideRefusedGlobal(list, selfId: 10, selfSessionId: 1, parentId: 5));
    }

    [Fact]
    public void SingleInstance_RefusedGlobal_HelpersShimsAndOtherSessions_DoNotCount()
    {
        var list = new[]
        {
            new SingleInstanceGuard.SessionProcess(10, 1, "\"NitroLite.exe\""),
            new SingleInstanceGuard.SessionProcess(5, 1, "nitrolite.exe"),               // the shim that started us
            new SingleInstanceGuard.SessionProcess(21, 1, "NitroLite.exe --wmi-worker --pipe x --parent-pid 9"),
            new SingleInstanceGuard.SessionProcess(22, 1, "\"C:\\x\\NitroLite.exe\" --cli optimize"),
            new SingleInstanceGuard.SessionProcess(23, 1, "NitroLite.exe --fan 45"),
            new SingleInstanceGuard.SessionProcess(24, 2, null)                          // another user's session
        };

        Assert.Equal(SingleInstanceGuard.RefusalDecision.OtherSession,
            SingleInstanceGuard.DecideRefusedGlobal(list, selfId: 10, selfSessionId: 1, parentId: 5));
    }

    [Fact]
    public void SingleInstance_RefusedGlobal_AnUnreadableProcessList_FailsSafe()
    {
        Assert.Equal(SingleInstanceGuard.RefusalDecision.AlreadyRunning,
            SingleInstanceGuard.DecideRefusedGlobal(null, selfId: 10, selfSessionId: 1, parentId: 5));
        Assert.False(SingleInstanceGuard.IsNonWindowCommandLine("NitroLite.exe --autostart --minimized"));
        Assert.True(SingleInstanceGuard.IsNonWindowCommandLine("NitroLite.exe --pipe=x --wmi-worker"));
    }

    // ================================================================ review round: uninstall-cleanup without a backend

    [Fact]
    public async Task Cli_UninstallCleanup_RunsItsStepsEvenWithoutAHardwareBackend()
    {
        var ran = new List<string>();
        var output = new StringWriter();
        var steps = new List<CliCleanupStep>
        {
            new("sys", _ => { ran.Add("sys"); return Task.FromResult<(bool, string?)>((true, null)); }, Elevates: true),
            new("task", _ => { ran.Add("task"); return Task.FromResult<(bool, string?)>((true, null)); }, Elevates: true)
        };

        int code = await CliTaskRunner.RunAsync("uninstall-cleanup", hardware: null, new AppSettings(), null, steps, output);

        Assert.Equal(new[] { "sys", "task" }, ran);
        Assert.Equal(1, code); // the fans could not be handed back, and that is reported
        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("2", lines[0]); // the UAC notice comes first
        Assert.Contains(lines, l => l.Contains("ok    sys", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("FAIL  " + Loc.T("Fan"), StringComparison.Ordinal));
    }

    [Fact]
    public void Cli_UninstallCleanupSteps_HaveTheirOrder_AndOnlyWhatHasSomethingToUndo()
    {
        var sys = new CliCleanupStep("sys", _ => Task.FromResult<(bool, string?)>((true, null)));
        var task = new CliCleanupStep("task", _ => Task.FromResult<(bool, string?)>((true, null)));

        var everything = CliTaskRunner.BuildUninstallCleanupSteps(new AppSettings(), null,
            new Phase3Fakes.FakeDebloat { HasRestorableBackup = true, LauncherBlocked = true }, sys, task);
        Assert.Equal(new[] { "sys", Loc.T("Acer arka plan servisleri"), Loc.T("NitroSense başlatıcısı"), "task" },
            everything.Select(s => s.Label).ToArray());
        Assert.True(everything[1].Elevates);
        Assert.True(everything[2].Elevates);

        var nothingAcer = CliTaskRunner.BuildUninstallCleanupSteps(new AppSettings(), null,
            new Phase3Fakes.FakeDebloat(), sys, task);
        Assert.Equal(new[] { "sys", "task" }, nothingAcer.Select(s => s.Label).ToArray());

        Assert.Empty(CliTaskRunner.BuildUninstallCleanupSteps(new AppSettings(), null, null, null, null));
    }

    [Fact]
    public void Cli_UndoRepair_IsACommand_AndAsksForYes()
    {
        Assert.True(CliTaskRunner.IsKnownCommand("undo-repair"));
        Assert.True(CliTaskRunner.RequiresConfirmation(new[] { "--cli", "undo-repair" }));
    }

    // ================================================================ review round: the undo's path check, run for real

    [Fact]
    public async Task UndoRepair_ResolveTrustedImage_AcceptsOnlyAPlainSignedExeUnderTheTrustedRoots()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var dotnet = Path.Combine(programFiles, "dotnet", "dotnet.exe");
        if (!File.Exists(dotnet)) return; // the test needs one real .exe under Program Files

        var script = SystemToolsService.BuildUndoRepairScript("{}");
        int start = script.IndexOf("$trustedRoots =", StringComparison.Ordinal);
        int end = script.IndexOf("if ($null -ne $backup.services)", StringComparison.Ordinal);
        Assert.True(start > 0 && end > start);
        var functions = script[start..end];

        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        string[] inputs =
        [
            dotnet,                                      // 0: real signature check (Microsoft, not Acer) -> refused
            dotnet,                                      // 1: plain path, signature stubbed -> accepted
            "\"" + dotnet + "\"",                        // 2: quoted -> accepted
            "\"" + dotnet + "\" --info",                 // 3: arguments -> refused
            dotnet + " --info",                          // 4: unquoted arguments -> refused
            cmd,                                         // 5: outside the trusted roots -> refused
            "dotnet.exe",                                // 6: relative -> refused
            Path.Combine(programFiles, "..", "Windows", "System32", "cmd.exe"), // 7: .. out of the root -> refused
            Path.Combine(programFiles, "dotnet", "missing-" + Guid.NewGuid().ToString("N") + ".exe") // 8: missing
        ];

        var body = new System.Text.StringBuilder(functions);
        body.AppendLine();
        body.AppendLine("Write-Output ('R0:' + (Resolve-TrustedImage '" + inputs[0].Replace("'", "''") + "'))");
        body.AppendLine("function Test-AcerSigned([string]$path) { return $true }");
        for (int i = 1; i < inputs.Length; i++)
            body.AppendLine("Write-Output ('R" + i + ":' + (Resolve-TrustedImage '" + inputs[i].Replace("'", "''") + "'))");

        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "resolve.ps1");
            File.WriteAllText(path, body.ToString(), new System.Text.UTF8Encoding(true));
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + path + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            });
            Assert.NotNull(process);
            var text = await process!.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            var results = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => l.StartsWith('R'))
                .ToDictionary(l => l[..l.IndexOf(':', StringComparison.Ordinal)], l => l[(l.IndexOf(':', StringComparison.Ordinal) + 1)..]);

            Assert.Equal("", results["R0"]);
            Assert.Equal(dotnet, results["R1"], ignoreCase: true);
            Assert.Equal(dotnet, results["R2"], ignoreCase: true);
            for (int i = 3; i < inputs.Length; i++) Assert.Equal("", results["R" + i]);
        }
        finally
        {
            try { TestDataIsolation.DeleteFolder(dir); } catch { }
        }
    }

    [Fact]
    public void UndoRepair_TrustedRoots_DoNotComeFromTheEnvironment()
    {
        var script = SystemToolsService.BuildUndoRepairScript("{}");
        var roots = script.Split('\n').Single(l => l.StartsWith("$trustedRoots =", StringComparison.Ordinal));

        Assert.DoesNotContain("$env:", roots, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[Environment]::GetFolderPath('ProgramFiles')", roots, StringComparison.Ordinal);
    }
}
