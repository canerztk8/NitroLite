using System.IO;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Core.Hardware.OpenRgb;
using NitroLite.Desktop;
using NitroLite.Desktop.Mvvm;
using NitroLite.Desktop.ViewModels;
using NitroLite.Linux;
using NitroLite.Linux.Sysfs;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The Linux functional parity work: <c>/proc</c> game detection, the MUX switch, school mode over sysfs, saved RGB
/// profiles, the privacy-safe hardware report and the tray's background mode.
/// </summary>
/// <remarks>
/// Everything here runs against an in-memory <c>/sys</c> and <c>/proc</c> tree, so no test needs a laptop, a game, a
/// display or a real backlight. The one thing that cannot be faked is the tray icon itself, which needs an Avalonia
/// application; the menu is therefore checked through the view model list it is built from, which is the same list the
/// icon walks.
/// </remarks>
public class LinuxFunctionalParityTests
{
    // ---------------------------------------------------------------- fixtures

    /// <summary>A Nitro 16 AN16-41 with both tiers, a tachometer, four platform profiles and a MUX switch.</summary>
    private static InMemorySysfsFileSystem Nitro16()
    {
        var fs = new InMemorySysfsFileSystem();

        Dmi(fs, "Nitro AN16-41", "AN16-41", "V1.27");

        fs.WithFile(LinuxPaths.PlatformProfile, "balanced\n");
        fs.WithFile(LinuxPaths.PlatformProfileChoices, "quiet balanced balanced-performance performance\n");

        fs.WithFile("/sys/class/hwmon/hwmon0/name", "acer\n");
        fs.WithFile("/sys/class/hwmon/hwmon0/temp1_input", "55000\n");
        fs.WithFile("/sys/class/hwmon/hwmon0/fan1_input", "2436\n");
        fs.WithFile("/sys/class/hwmon/hwmon0/fan2_input", "2409\n");
        fs.WithFile("/sys/class/hwmon/hwmon1/name", "k10temp\n");
        fs.WithFile("/sys/class/hwmon/hwmon1/temp1_input", "63000\n");

        fs.WithFile("/sys/class/power_supply/BAT1/charge_control_end_threshold", "100\n");

        fs.WithDirectory(LinuxPaths.AcpiPlatformRoot);
        fs.WithDirectory(SenseRoot());
        fs.WithFile(Sense("fan_speed"), "0,0\n");
        fs.WithFile(Sense("backlight_timeout"), "0\n");
        fs.WithFile(Sense("battery_limiter"), "0\n");
        fs.WithFile(Sense("lcd_override"), "0\n");
        fs.WithFile(Sense("cool_boost"), "0\n");
        fs.WithFile(Sense("dgpu_mode"), "0\n");

        fs.WithDirectory(KeyboardRoot());
        fs.WithFile(Keyboard("per_zone_mode"), "00E5FF,00E5FF,00E5FF,00E5FF,55\n");
        fs.WithFile(Keyboard("four_zone_mode"), "3,3,55,1,0,229,255\n");

        return fs;
    }

    private static void Dmi(InMemorySysfsFileSystem fs, string product, string board, string bios)
    {
        fs.WithFile("/sys/class/dmi/id/product_name", product + "\n");
        fs.WithFile("/sys/class/dmi/id/board_name", board + "\n");
        fs.WithFile("/sys/class/dmi/id/sys_vendor", "Acer\n");
        fs.WithFile("/sys/class/dmi/id/bios_version", bios + "\n");
        fs.WithFile("/sys/class/dmi/id/bios_vendor", "Insyde\n");
    }

    private static string SenseRoot() => LinuxPaths.Combine(LinuxPaths.LinuwuModuleRoot, LinuxPaths.NitroSense);

    private static string Sense(string name) => LinuxPaths.Combine(SenseRoot(), name);

    private static string KeyboardRoot() => LinuxPaths.Combine(SenseRoot(), LinuxPaths.FourZoneKeyboard);

    private static string Keyboard(string name) => LinuxPaths.Combine(KeyboardRoot(), name);

    /// <summary>The embedded model table only, so a developer's own profiles.user.json cannot change a result.</summary>
    private static ProfileCatalog Catalog() =>
        ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _);

    /// <summary>A controller over <paramref name="fs"/>, with OpenRGB pointed at a closed port.</summary>
    private static LinuxSysfsHardwareController Controller(InMemorySysfsFileSystem fs, AppSettings settings) =>
        LinuxSysfsHardwareController.Create(settings, fs, Catalog(), new OpenRgbClient(port: 1));

    /// <summary>A process table a test can rewrite between polls.</summary>
    private sealed class FakeScanner : IProcessScanner
    {
        public List<RunningProcess> Processes { get; } = new();
        public bool Throw { get; set; }

        public IReadOnlyList<RunningProcess> Scan() =>
            Throw ? throw new IOException("no process table") : Processes.ToList();
    }

    /// <summary>A settings object with one game rule and nothing else that could interfere.</summary>
    private static AppSettings SettingsWithGame(string exe, OperatingMode mode, FanMode fan = FanMode.Auto)
    {
        var settings = new AppSettings { AutoGameModeEnabled = true };
        settings.GameProfiles = new List<GameProfile>
        {
            new() { ExeName = exe, DisplayName = "Test Game", TargetMode = mode, TargetFanMode = fan }
        };
        return settings;
    }

    // ---------------------------------------------------------------- 1. /proc scanner

    [Fact]
    public void ProcessScanner_ReadsCommAndTheFirstCommandLineArgument()
    {
        var fs = new InMemorySysfsFileSystem();
        fs.WithFile("/proc/1234/comm", "cs2\n");
        fs.WithFile("/proc/1234/cmdline", "/usr/bin/cs2\0-novid\0");

        var processes = new LinuxProcessScanner(fs).Scan();

        var process = Assert.Single(processes);
        Assert.Equal(1234, process.Pid);
        Assert.Equal("cs2", process.Name);
        Assert.Equal("/usr/bin/cs2", process.ExecutablePath);
        Assert.Equal("cs2", process.FileName);
    }

    [Fact]
    public void ProcessScanner_SkipsEntriesThatAreNotPids()
    {
        var fs = new InMemorySysfsFileSystem();
        fs.WithFile("/proc/self/comm", "bash\n");
        fs.WithFile("/proc/sys/comm", "bash\n");
        fs.WithFile("/proc/4242/comm", "cs2\n");

        var processes = new LinuxProcessScanner(fs).Scan();

        Assert.Equal(4242, Assert.Single(processes).Pid);
    }

    [Fact]
    public void ProcessScanner_SkipsAProcessThatExitedBetweenTheListingAndTheRead()
    {
        var fs = new InMemorySysfsFileSystem();
        fs.WithDirectory("/proc/777");
        fs.WithFile("/proc/778/comm", "cs2\n");

        var processes = new LinuxProcessScanner(fs).Scan();

        Assert.Equal(778, Assert.Single(processes).Pid);
    }

    [Fact]
    public void ProcessScanner_ProtonGame_KeepsTheWineNameAndTheGamesOwnPath()
    {
        // The case the two files exist for: comm is the loader, cmdline names the game.
        var fs = new InMemorySysfsFileSystem();
        fs.WithFile("/proc/900/comm", "wine64-preloader\n");
        fs.WithFile("/proc/900/cmdline", "/home/caner/.steam/steamapps/common/Cyberpunk 2077/Cyberpunk2077.exe\0");

        var process = Assert.Single(new LinuxProcessScanner(fs).Scan());

        Assert.Equal("wine64-preloader", process.Name);
        Assert.Equal("Cyberpunk2077.exe", process.FileName);
        Assert.True(process.Matches("Cyberpunk2077"));
    }

    [Fact]
    public void ProcessScanner_AnUnreadableProcRootIsAnEmptyTable()
    {
        // No /proc at all: the scan must not throw, or detection would stop for the session.
        Assert.Empty(new LinuxProcessScanner(new InMemorySysfsFileSystem()).Scan());
    }

    [Theory]
    [InlineData("/proc/1234", 1234)]
    [InlineData("/proc/1234/", 1234)]
    [InlineData("1234", 1234)]
    [InlineData("/proc/self", 0)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    public void ProcessScanner_ParsePidAcceptsOnlyNumbers(string? directory, int expected) =>
        Assert.Equal(expected, LinuxProcessScanner.ParsePid(directory));

    [Theory]
    [InlineData("/usr/bin/cs2\0-novid\0", "/usr/bin/cs2")]
    [InlineData("/usr/bin/cs2", "/usr/bin/cs2")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void ProcessScanner_FirstArgumentStopsAtTheNul(string? commandLine, string expected) =>
        Assert.Equal(expected, LinuxProcessScanner.FirstArgument(commandLine));

    // ---------------------------------------------------------------- 2. game profile service

    [Fact]
    public async Task GameProfile_AppliesTheModeAndFanThenPutsTheUsersOwnBack()
    {
        var fs = Nitro16();
        var settings = SettingsWithGame("cs2", OperatingMode.Turbo, FanMode.Max);
        using var controller = Controller(fs, settings);
        var scanner = new FakeScanner();
        var service = new GameProfileService(scanner, settings, controller);

        scanner.Processes.Add(new RunningProcess(1234, "cs2", "/usr/bin/cs2"));
        var started = await service.PollAsync();

        Assert.Equal(GameDetectionChange.Started, started.Change);
        Assert.Equal("Test Game", started.Profile?.DisplayName);
        Assert.True(service.IsGameActive);
        Assert.Equal("performance", fs.Peek(LinuxPaths.PlatformProfile)?.Trim());
        Assert.Equal("100,100", fs.Peek(Sense("fan_speed"))?.Trim());

        // The game exits: the mode and the fan the user had before the session come back.
        scanner.Processes.Clear();
        var ended = await service.PollAsync();

        Assert.Equal(GameDetectionChange.Ended, ended.Change);
        Assert.False(service.IsGameActive);
        Assert.NotNull(ended.Snapshot);
        Assert.Equal("balanced", fs.Peek(LinuxPaths.PlatformProfile)?.Trim());
        Assert.Equal("0,0", fs.Peek(Sense("fan_speed"))?.Trim());
    }

    [Fact]
    public async Task GameProfile_DoesNothingWhileAutomaticSwitchingIsOff()
    {
        var fs = Nitro16();
        var settings = SettingsWithGame("cs2", OperatingMode.Turbo);
        settings.AutoGameModeEnabled = false;
        using var controller = Controller(fs, settings);
        var scanner = new FakeScanner();
        scanner.Processes.Add(new RunningProcess(1234, "cs2", "/usr/bin/cs2"));

        var result = await new GameProfileService(scanner, settings, controller).PollAsync();

        Assert.Equal(GameDetectionChange.None, result.Change);
        Assert.Equal("balanced", fs.Peek(LinuxPaths.PlatformProfile)?.Trim());
    }

    [Fact]
    public async Task GameProfile_RefusesAProfileForAProgramThatIsAlwaysRunning()
    {
        // A rule for bash would keep the machine in game mode for the whole session.
        var fs = Nitro16();
        var settings = SettingsWithGame("bash", OperatingMode.Turbo);
        using var controller = Controller(fs, settings);
        var scanner = new FakeScanner();
        scanner.Processes.Add(new RunningProcess(1, "bash", "/usr/bin/bash"));

        var result = await new GameProfileService(scanner, settings, controller).PollAsync();

        Assert.Equal(GameDetectionChange.None, result.Change);
        Assert.Equal("balanced", fs.Peek(LinuxPaths.PlatformProfile)?.Trim());
    }

    [Theory]
    [InlineData("bash", true)]
    [InlineData("/usr/bin/bash", true)]
    [InlineData("BASH", true)]
    [InlineData("cs2", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void GameProfile_NonGameExecutableIsRecognisedByBareNameAndPath(string? exe, bool expected) =>
        Assert.Equal(expected, GameProfileService.IsNonGameExecutable(exe));

    [Fact]
    public async Task GameProfile_ClampsAHighPowerTargetOnBattery()
    {
        var fs = Nitro16();
        var settings = SettingsWithGame("cs2", OperatingMode.Turbo);
        using var controller = Controller(fs, settings);
        var scanner = new FakeScanner();
        scanner.Processes.Add(new RunningProcess(1234, "cs2", "/usr/bin/cs2"));

        var service = new GameProfileService(scanner, settings, controller)
        {
            PowerState = () => (false, 20)
        };

        await service.PollAsync();

        // Turbo on battery is refused by the firmware, so the profile lands on Balanced instead of being written and
        // silently dropped.
        Assert.Equal("balanced", fs.Peek(LinuxPaths.PlatformProfile)?.Trim());
    }

    [Fact]
    public async Task GameProfile_AnUnknownPowerStateDoesNotDowngradeTheTarget()
    {
        var fs = Nitro16();
        var settings = SettingsWithGame("cs2", OperatingMode.Turbo);
        using var controller = Controller(fs, settings);
        var scanner = new FakeScanner();
        scanner.Processes.Add(new RunningProcess(1234, "cs2", "/usr/bin/cs2"));

        // No PowerState hook at all: guessing "on battery" would downgrade every game on a desktop replacement.
        await new GameProfileService(scanner, settings, controller).PollAsync();

        Assert.Equal("performance", fs.Peek(LinuxPaths.PlatformProfile)?.Trim());
    }

    [Fact]
    public async Task GameProfile_ExposesTheGamesOwnCurveOnlyWhileItRuns()
    {
        var fs = Nitro16();
        var settings = SettingsWithGame("cs2", OperatingMode.Turbo);
        settings.GameProfiles[0].TargetFanCurveEnabled = true;
        settings.GameProfiles[0].TargetFanCurvePoints = new List<FanCurvePoint>
        {
            new(40, 40), new(60, 60), new(80, 90), new(95, 100)
        };

        using var controller = Controller(fs, settings);
        var scanner = new FakeScanner();
        var service = new GameProfileService(scanner, settings, controller);

        Assert.Null(service.ActiveCurvePoints);

        scanner.Processes.Add(new RunningProcess(1234, "cs2", "/usr/bin/cs2"));
        await service.PollAsync();

        var points = service.ActiveCurvePoints;
        Assert.NotNull(points);
        Assert.Equal(4, points!.Count);
        // The curve puts the fans under software control; the loop that follows it is the caller's.
        Assert.Equal(FanMode.Custom, await controller.GetFanModeAsync());

        scanner.Processes.Clear();
        await service.PollAsync();
        Assert.Null(service.ActiveCurvePoints);
    }

    [Fact]
    public async Task GameProfile_StopEndsTheSessionWithoutWaitingForTheGame()
    {
        var fs = Nitro16();
        var settings = SettingsWithGame("cs2", OperatingMode.Turbo);
        using var controller = Controller(fs, settings);
        var scanner = new FakeScanner();
        scanner.Processes.Add(new RunningProcess(1234, "cs2", "/usr/bin/cs2"));

        var service = new GameProfileService(scanner, settings, controller);
        await service.PollAsync();
        Assert.True(service.IsGameActive);

        // The process is still listed: only an explicit stop ends the session here.
        var result = await service.StopAsync();

        Assert.Equal(GameDetectionChange.Ended, result.Change);
        Assert.False(service.IsGameActive);
        Assert.Equal("balanced", fs.Peek(LinuxPaths.PlatformProfile)?.Trim());
    }

    [Fact]
    public async Task GameProfile_AScannerThatThrowsIsAPassWithNoProcesses()
    {
        var fs = Nitro16();
        var settings = SettingsWithGame("cs2", OperatingMode.Turbo);
        using var controller = Controller(fs, settings);
        var scanner = new FakeScanner { Throw = true };

        var result = await new GameProfileService(scanner, settings, controller).PollAsync();

        Assert.Equal(GameDetectionChange.None, result.Change);
    }

    [Fact]
    public async Task GameProfile_RaisesTheEventsTheShellListensTo()
    {
        var fs = Nitro16();
        var settings = SettingsWithGame("cs2", OperatingMode.Turbo);
        using var controller = Controller(fs, settings);
        var scanner = new FakeScanner();
        var service = new GameProfileService(scanner, settings, controller);

        var started = new List<string>();
        var ended = new List<string>();
        service.GameStarted += profile => started.Add(profile.DisplayName);
        service.GameEnded += profile => ended.Add(profile.DisplayName);

        scanner.Processes.Add(new RunningProcess(1234, "cs2", "/usr/bin/cs2"));
        await service.PollAsync();
        scanner.Processes.Clear();
        await service.PollAsync();

        Assert.Equal(new[] { "Test Game" }, started);
        Assert.Equal(new[] { "Test Game" }, ended);
    }

    // ---------------------------------------------------------------- 3. school mode backends

    [Fact]
    public void Brightness_ConvertsPercentToRawAndBack()
    {
        var fs = new InMemorySysfsFileSystem();
        fs.WithFile("/sys/class/backlight/amdgpu_bl0/brightness", "50\n");
        fs.WithFile("/sys/class/backlight/amdgpu_bl0/max_brightness", "100\n");

        var service = new LinuxScreenBrightnessService(fs);

        Assert.True(service.IsAvailable);
        Assert.Equal(50, service.GetBrightness());
        Assert.True(service.SetBrightness(40));
        Assert.Equal("40", fs.Peek("/sys/class/backlight/amdgpu_bl0/brightness")?.Trim());
        Assert.Equal(40, service.GetBrightness());
    }

    [Fact]
    public void Brightness_NeverWritesZeroBecauseThatTurnsThePanelOff()
    {
        var fs = new InMemorySysfsFileSystem();
        fs.WithFile("/sys/class/backlight/intel_backlight/brightness", "10\n");
        fs.WithFile("/sys/class/backlight/intel_backlight/max_brightness", "10\n");

        var service = new LinuxScreenBrightnessService(fs);

        Assert.True(service.SetBrightness(1));
        Assert.Equal("1", fs.Peek("/sys/class/backlight/intel_backlight/brightness")?.Trim());
    }

    [Fact]
    public void Brightness_WithoutAPanelIsUnavailableAndRefusesQuietly()
    {
        var service = new LinuxScreenBrightnessService(new InMemorySysfsFileSystem());

        Assert.False(service.IsAvailable);
        Assert.Null(service.Node);
        Assert.Null(service.GetBrightness());
        Assert.False(service.SetBrightness(40));
    }

    [Theory]
    [InlineData(0, 100, 0)]
    [InlineData(50, 100, 50)]
    [InlineData(1, 10, 10)]
    [InlineData(100, 10, 100)]
    [InlineData(50, 0, 0)]
    public void Brightness_ToPercentRoundsToTheNearestWholePercent(int raw, int max, int expected) =>
        Assert.Equal(expected, LinuxScreenBrightnessService.ToPercent(raw, max));

    [Theory]
    [InlineData(40, 100, 40)]
    [InlineData(1, 10, 1)]
    [InlineData(0, 10, 1)]
    [InlineData(100, 10, 10)]
    [InlineData(40, 0, 0)]
    public void Brightness_ToRawNeverGoesBelowOneStep(int percent, int max, int expected) =>
        Assert.Equal(expected, LinuxScreenBrightnessService.ToRaw(percent, max));

    [Fact]
    public async Task CpuBoost_UsesCpufreqBoostWhenTheKernelExposesIt()
    {
        var fs = new InMemorySysfsFileSystem();
        fs.WithFile(LinuxPaths.CpuFreqBoost, "1\n");

        var boost = new LinuxCpuBoostAccess(fs);

        Assert.True(boost.IsSupported);
        Assert.Equal(CpuBoostMode.Enabled, boost.QueryBoostModeExact(true));

        Assert.True(await boost.SetBoostModeAsync(CpuBoostMode.Disabled));
        Assert.Equal("0", fs.Peek(LinuxPaths.CpuFreqBoost)?.Trim());
        Assert.Equal(CpuBoostMode.Disabled, boost.QueryBoostModeExact(false));
    }

    [Fact]
    public async Task CpuBoost_InvertsIntelNoTurbo()
    {
        var fs = new InMemorySysfsFileSystem();
        fs.WithFile(LinuxPaths.IntelNoTurbo, "0\n");

        var boost = new LinuxCpuBoostAccess(fs);

        // no_turbo is inverted: 0 means turbo is allowed.
        Assert.Equal(CpuBoostMode.Enabled, boost.QueryBoostModeExact(true));

        Assert.True(await boost.SetBoostModeAsync(CpuBoostMode.Disabled));
        Assert.Equal("1", fs.Peek(LinuxPaths.IntelNoTurbo)?.Trim());
    }

    [Fact]
    public async Task CpuBoost_UsesAmdPstateStatusWhenThatIsAllThereIs()
    {
        var fs = new InMemorySysfsFileSystem();
        fs.WithFile(LinuxPaths.AmdPstateStatus, "active\n");

        var boost = new LinuxCpuBoostAccess(fs);

        Assert.Equal(CpuBoostMode.Enabled, boost.QueryBoostModeExact(true));

        Assert.True(await boost.SetBoostModeAsync(CpuBoostMode.Disabled));
        Assert.Equal("passive", fs.Peek(LinuxPaths.AmdPstateStatus)?.Trim());
    }

    [Fact]
    public async Task CpuBoost_WithoutANodeIsUnsupportedAndRefuses()
    {
        var boost = new LinuxCpuBoostAccess(new InMemorySysfsFileSystem());

        Assert.False(boost.IsSupported);
        Assert.Null(boost.Node);
        Assert.Null(boost.QueryBoostModeExact(true));
        Assert.False(await boost.SetBoostModeAsync(CpuBoostMode.Disabled));
    }

    [Fact]
    public async Task CpuBoost_ANodeThatIgnoresTheWriteIsReportedAsAFailure()
    {
        var fs = new InMemorySysfsFileSystem();
        fs.WithFile(LinuxPaths.CpuFreqBoost, "1\n");
        fs.OnWrite = (_, _) => false; // accepted and dropped, like a firmware that does not implement it

        Assert.False(await new LinuxCpuBoostAccess(fs).SetBoostModeAsync(CpuBoostMode.Disabled));
    }

    [Fact]
    public async Task SchoolMode_OnLinuxAppliesTheBrightnessAndLeavesTheUnsupportedSettingsAlone()
    {
        var fs = Nitro16();
        fs.WithFile("/sys/class/backlight/amdgpu_bl0/brightness", "80\n");
        fs.WithFile("/sys/class/backlight/amdgpu_bl0/max_brightness", "100\n");
        fs.WithFile(LinuxPaths.CpuFreqBoost, "1\n");

        var settings = new AppSettings();
        using var controller = Controller(fs, settings);
        var service = new SchoolModeService(
            controller,
            new LinuxCpuBoostAccess(fs),
            new LinuxDisplayRefreshRateService(),
            new LinuxScreenBrightnessService(fs),
            settings,
            new LinuxPowerSchemeAccess())
        {
            BackupPath = AppPaths.GetDataFile("school_" + Guid.NewGuid().ToString("N") + ".json"),
            GetCurrentOperatingMode = () => OperatingMode.Default,
            GetCurrentFanMode = () => FanMode.Auto
        };

        var outcome = await service.EnableAsync();

        Assert.False(outcome.Refused);
        Assert.True(outcome.Success, string.Join(" / ", outcome.Failed));
        Assert.Equal("40", fs.Peek("/sys/class/backlight/amdgpu_bl0/brightness")?.Trim());
        Assert.Equal("0", fs.Peek(LinuxPaths.CpuFreqBoost)?.Trim());

        // No power scheme and no refresh-rate control on Linux: those steps are skipped, not reported as failures.
        Assert.DoesNotContain(outcome.Failed, f => f.Contains("Güç", StringComparison.Ordinal));

        var restored = await service.DisableAsync();
        Assert.True(restored.Success, string.Join(" / ", restored.Failed));
        Assert.Equal("80", fs.Peek("/sys/class/backlight/amdgpu_bl0/brightness")?.Trim());
        Assert.Equal("1", fs.Peek(LinuxPaths.CpuFreqBoost)?.Trim());
    }

    [Fact]
    public async Task SchoolMode_RefusesWhenTheUndoRecordCannotBeWritten()
    {
        var fs = Nitro16();
        fs.WithFile("/sys/class/backlight/amdgpu_bl0/brightness", "80\n");
        fs.WithFile("/sys/class/backlight/amdgpu_bl0/max_brightness", "100\n");

        var settings = new AppSettings();
        using var controller = Controller(fs, settings);

        // A file where the backup's parent directory should be: the record cannot be written.
        var blocker = AppPaths.GetDataFile("school_blocker_" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "not a directory");

        var service = new SchoolModeService(
            controller,
            new LinuxCpuBoostAccess(fs),
            new LinuxDisplayRefreshRateService(),
            new LinuxScreenBrightnessService(fs),
            settings,
            new LinuxPowerSchemeAccess())
        {
            BackupPath = Path.Combine(blocker, "backup.json"),
            GetCurrentOperatingMode = () => OperatingMode.Default,
            GetCurrentFanMode = () => FanMode.Auto
        };

        var outcome = await service.EnableAsync();

        Assert.True(outcome.Refused);
        Assert.False(settings.SchoolModeEnabled);
        // Nothing was touched: a mode the user cannot switch off is worse than no mode.
        Assert.Equal("80", fs.Peek("/sys/class/backlight/amdgpu_bl0/brightness")?.Trim());
    }

    // ---------------------------------------------------------------- 4. hardware report

    [Fact]
    public void HardwareReport_CarriesNoSerialUuidUserNameOrPersonalPath()
    {
        var fs = Nitro16();

        // Everything a careless report would leak, present in the tree the probe reads.
        fs.WithFile("/sys/class/dmi/id/product_serial", "PF2AB1234567890\n");
        fs.WithFile("/sys/class/dmi/id/product_uuid", "4c4c4544-0042-3510-8051-b4c04f4e4d32\n");
        fs.WithFile("/sys/class/dmi/id/board_serial", "NBQ8AB12345678\n");
        fs.WithFile("/sys/class/dmi/id/chassis_serial", "CHASSIS-SERIAL-1\n");

        var settings = new AppSettings();
        using var controller = Controller(fs, settings);

        var report = LinuxHardwareReport.Build(
            controller.Capabilities, "1.2.3", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));

        Assert.Contains("# NitroLite — Linux hardware report", report);
        Assert.Contains("Nitro AN16-41", report);

        Assert.DoesNotContain("PF2AB1234567890", report);
        Assert.DoesNotContain("4c4c4544-0042-3510-8051-b4c04f4e4d32", report);
        Assert.DoesNotContain("NBQ8AB12345678", report);
        Assert.DoesNotContain("CHASSIS-SERIAL-1", report);
        Assert.DoesNotContain("/home/", report);
        Assert.DoesNotContain(Environment.UserName, report);
    }

    [Fact]
    public void HardwareReport_RedactsHomePathsAndUnitIdentifiers()
    {
        Assert.Equal("/home/<redacted>", LinuxHardwareReport.Redact("/home/caner"));
        Assert.Equal("note /home/<redacted> tail", LinuxHardwareReport.Redact("note /home/caner/games tail"));
        Assert.Equal("/root/<redacted>", LinuxHardwareReport.Redact("/root"));
        Assert.Equal("serial: <redacted>", LinuxHardwareReport.Redact("serial: PF2AB1234567890"));
        Assert.Equal("UUID: <redacted>", LinuxHardwareReport.Redact("UUID: 4c4c4544-0042"));
        Assert.Equal("hostname: <redacted>", LinuxHardwareReport.Redact("hostname: caner-laptop"));
        Assert.Equal("", LinuxHardwareReport.Redact(null));
        Assert.Equal("nothing to hide", LinuxHardwareReport.Redact("nothing to hide"));
    }

    [Fact]
    public void HardwareReport_IsDeterministicForAGivenVersionAndTimestamp()
    {
        var fs = Nitro16();
        var settings = new AppSettings();
        using var controller = Controller(fs, settings);
        var stamp = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        var first = LinuxHardwareReport.Build(controller.Capabilities, "1.2.3", stamp);
        var second = LinuxHardwareReport.Build(controller.Capabilities, "1.2.3", stamp);

        Assert.Equal(first, second);
        Assert.Contains("2026-01-02 03:04", first);
    }

    // ---------------------------------------------------------------- 5. view model: MUX

    [Fact]
    public async Task Mux_IsOfferedWhenTheModuleExposesTheSwitchAndWritesTheMode()
    {
        var fs = Nitro16();
        var settings = new AppSettings();
        using var controller = Controller(fs, settings);
        using var vm = new MainViewModel(settings, controller, controller.Capabilities, AppPaths.GetDataFile("settings.json"));
        await vm.InitializeAsync();

        Assert.True(vm.MuxAvailable);
        Assert.Equal(1, vm.SchoolModeColumnSpan);
        Assert.Equal(2, vm.GpuModes.Count);
        Assert.Equal(GpuOperatingMode.Optimus, vm.GpuModes[0].Mode);
        Assert.Equal(GpuOperatingMode.DiscreteGpu, vm.GpuModes[1].Mode);

        vm.SelectedGpuMode = vm.GpuModes[1];
        await RunAsync(vm.ApplyGpuModeCommand);

        Assert.Equal("1", fs.Peek(Sense("dgpu_mode"))?.Trim());
        Assert.Equal(GpuOperatingMode.DiscreteGpu, await controller.GetGpuModeAsync());
    }

    [Fact]
    public async Task Mux_IsHiddenWhenTheMachineHasNoSwitch()
    {
        var fs = Nitro16();
        fs.WithoutFile(Sense("dgpu_mode"));

        var settings = new AppSettings();
        using var controller = Controller(fs, settings);
        using var vm = new MainViewModel(settings, controller, controller.Capabilities, AppPaths.GetDataFile("settings.json"));
        await vm.InitializeAsync();

        Assert.False(vm.MuxAvailable);
        // The school-mode card takes the whole row instead of leaving an empty half behind the hidden MUX card.
        Assert.Equal(2, vm.SchoolModeColumnSpan);
    }

    // ---------------------------------------------------------------- 6. view model: school mode

    [Fact]
    public async Task SchoolMode_TheSwitchAppliesAndUndoesTheLinuxBackends()
    {
        var fs = Nitro16();
        fs.WithFile("/sys/class/backlight/amdgpu_bl0/brightness", "80\n");
        fs.WithFile("/sys/class/backlight/amdgpu_bl0/max_brightness", "100\n");
        fs.WithFile(LinuxPaths.CpuFreqBoost, "1\n");

        var settings = new AppSettings();
        using var controller = Controller(fs, settings);
        var school = new SchoolModeService(
            controller,
            new LinuxCpuBoostAccess(fs),
            new LinuxDisplayRefreshRateService(),
            new LinuxScreenBrightnessService(fs),
            settings,
            new LinuxPowerSchemeAccess())
        {
            BackupPath = AppPaths.GetDataFile("school_" + Guid.NewGuid().ToString("N") + ".json"),
            GetCurrentOperatingMode = () => OperatingMode.Default,
            GetCurrentFanMode = () => FanMode.Auto
        };

        using var vm = new MainViewModel(
            settings, controller, controller.Capabilities, AppPaths.GetDataFile("settings.json"), schoolMode: school);
        await vm.InitializeAsync();

        Assert.True(vm.SchoolModeAvailable);
        Assert.False(vm.SchoolModeOn);

        vm.SchoolModeOn = true;
        await WaitForAsync(() => vm.SchoolModeDetail.Length > 0);

        Assert.True(vm.SchoolModeOn);
        Assert.Equal("40", fs.Peek("/sys/class/backlight/amdgpu_bl0/brightness")?.Trim());

        vm.SchoolModeOn = false;
        await WaitForAsync(() => fs.Peek("/sys/class/backlight/amdgpu_bl0/brightness")?.Trim() == "80");

        Assert.False(vm.SchoolModeOn);
        Assert.Equal("1", fs.Peek(LinuxPaths.CpuFreqBoost)?.Trim());
    }

    [Fact]
    public async Task SchoolMode_ARefusalPutsTheSwitchBackOff()
    {
        var fs = Nitro16();
        var settings = new AppSettings();
        using var controller = Controller(fs, settings);

        var blocker = AppPaths.GetDataFile("school_blocker_" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "not a directory");

        var school = new SchoolModeService(
            controller,
            new LinuxCpuBoostAccess(fs),
            new LinuxDisplayRefreshRateService(),
            new LinuxScreenBrightnessService(fs),
            settings,
            new LinuxPowerSchemeAccess())
        {
            BackupPath = Path.Combine(blocker, "backup.json"),
            GetCurrentOperatingMode = () => OperatingMode.Default,
            GetCurrentFanMode = () => FanMode.Auto
        };

        using var vm = new MainViewModel(
            settings, controller, controller.Capabilities, AppPaths.GetDataFile("settings.json"), schoolMode: school);
        await vm.InitializeAsync();

        vm.SchoolModeOn = true;
        await WaitForAsync(() => vm.SchoolModeDetail.Length > 0);

        // Nothing was changed, so the switch must not claim a state the machine is not in.
        Assert.False(vm.SchoolModeOn);
        Assert.Equal(vm.Labels.SchoolModeRefused, vm.SchoolModeDetail);
    }

    [Fact]
    public async Task SchoolMode_IsUnavailableWhenTheCompositionRootWiredNoStateMachine()
    {
        var fs = Nitro16();
        var settings = new AppSettings();
        using var controller = Controller(fs, settings);
        using var vm = new MainViewModel(settings, controller, controller.Capabilities, AppPaths.GetDataFile("settings.json"));
        await vm.InitializeAsync();

        Assert.False(vm.SchoolModeAvailable);
    }

    // ---------------------------------------------------------------- 7. view model: RGB profiles

    [Fact]
    public async Task RgbProfiles_SaveApplyAndDeleteRoundTrip()
    {
        var fs = Nitro16();
        var settings = new AppSettings();
        using var controller = Controller(fs, settings);
        using var vm = new MainViewModel(settings, controller, controller.Capabilities, AppPaths.GetDataFile("settings.json"));
        await vm.InitializeAsync();

        var name = "Test Preset " + Guid.NewGuid().ToString("N");
        try
        {
            Assert.NotEmpty(vm.RgbProfiles);
            Assert.Contains(vm.RgbProfiles, p => p.IsBuiltIn);

            vm.NewProfileName = name;
            vm.SaveRgbProfileCommand.Execute(null);

            var saved = Assert.Single(vm.RgbProfiles, p => p.Name == name);
            Assert.False(saved.IsBuiltIn);
            Assert.Equal(name, vm.SelectedRgbProfile?.Name);
            Assert.Equal("", vm.NewProfileName);

            // Applying it writes the keyboard and remembers the choice.
            vm.SelectedRgbProfile = saved;
            await RunAsync(vm.ApplyRgbProfileCommand);

            Assert.Equal(settings.SelectedRgbPreset, name);
            Assert.Contains(fs.Writes, w => w.Path == Keyboard("per_zone_mode") || w.Path == Keyboard("four_zone_mode"));

            vm.DeleteRgbProfileCommand.Execute(null);

            Assert.DoesNotContain(vm.RgbProfiles, p => p.Name == name);
        }
        finally
        {
            new RgbProfileService().DeletePreset(name);
        }
    }

    [Fact]
    public async Task RgbProfiles_AnEmptyNameIsRefusedAndABuiltInCannotBeDeleted()
    {
        var fs = Nitro16();
        var settings = new AppSettings();
        using var controller = Controller(fs, settings);
        using var vm = new MainViewModel(settings, controller, controller.Capabilities, AppPaths.GetDataFile("settings.json"));
        await vm.InitializeAsync();

        vm.NewProfileName = "   ";
        vm.SaveRgbProfileCommand.Execute(null);
        Assert.Equal(vm.Labels.ProfileNameRequired, vm.RgbProfileStatus);

        vm.SelectedRgbProfile = vm.RgbProfiles.First(p => p.IsBuiltIn);
        vm.DeleteRgbProfileCommand.Execute(null);
        Assert.Equal(vm.Labels.BuiltInProfileLocked, vm.RgbProfileStatus);
        Assert.Contains(vm.RgbProfiles, p => p.IsBuiltIn);
    }

    // ---------------------------------------------------------------- 8. view model: hardware report

    [Fact]
    public async Task HardwareReport_TheCopyButtonPutsTheRedactedReportOnTheClipboard()
    {
        var fs = Nitro16();
        fs.WithFile("/sys/class/dmi/id/product_serial", "PF2AB1234567890\n");

        var settings = new AppSettings();
        using var controller = Controller(fs, settings);
        using var vm = new MainViewModel(settings, controller, controller.Capabilities, AppPaths.GetDataFile("settings.json"));
        await vm.InitializeAsync();

        string? copied = null;
        var previous = ClipboardService.Setter;
        ClipboardService.Setter = text => copied = text;
        try
        {
            Assert.True(vm.HardwareReportAvailable);
            vm.CopyHardwareReportCommand.Execute(null);
        }
        finally
        {
            ClipboardService.Setter = previous;
        }

        Assert.NotNull(copied);
        Assert.Contains("# NitroLite — Linux hardware report", copied);
        Assert.DoesNotContain("PF2AB1234567890", copied);
        Assert.Equal(vm.Labels.ReportCopied, vm.Status);
    }

    [Fact]
    public async Task HardwareReport_IsUnavailableWithoutALinuxBackend()
    {
        var settings = new AppSettings();
        using var vm = new MainViewModel(settings, null, null, AppPaths.GetDataFile("settings.json"));
        await vm.InitializeAsync();

        Assert.False(vm.HardwareReportAvailable);
        Assert.Equal("", vm.HardwareReportText);
    }

    // ---------------------------------------------------------------- 9. view model: game detection

    [Fact]
    public async Task GameDetection_TheCardFollowsTheSessionAndTheControlsCatchUp()
    {
        var fs = Nitro16();
        var settings = SettingsWithGame("cs2", OperatingMode.Turbo, FanMode.Max);
        using var controller = Controller(fs, settings);
        var scanner = new FakeScanner();
        var service = new GameProfileService(scanner, settings, controller);

        using var vm = new MainViewModel(
            settings, controller, controller.Capabilities, AppPaths.GetDataFile("settings.json"), gameProfiles: service);
        await vm.InitializeAsync();

        Assert.True(vm.AutoGameModeAvailable);
        Assert.False(vm.GameActive);
        Assert.Equal(vm.Labels.GameIdle, vm.GameStatusText);

        scanner.Processes.Add(new RunningProcess(1234, "cs2", "/usr/bin/cs2"));
        await vm.PollGameAsync();

        Assert.True(vm.GameActive);
        Assert.Equal(OperatingMode.Turbo, vm.SelectedMode?.Mode);
        Assert.Equal(FanMode.Max, vm.SelectedFanMode?.Mode);
        Assert.NotEqual(vm.Labels.GameIdle, vm.GameStatusText);

        scanner.Processes.Clear();
        await vm.PollGameAsync();

        Assert.False(vm.GameActive);
        Assert.Equal(OperatingMode.Default, vm.SelectedMode?.Mode);
        Assert.Equal(FanMode.Auto, vm.SelectedFanMode?.Mode);
    }

    [Fact]
    public async Task GameDetection_TurningTheSwitchOffEndsARunningSession()
    {
        var fs = Nitro16();
        var settings = SettingsWithGame("cs2", OperatingMode.Turbo);
        using var controller = Controller(fs, settings);
        var scanner = new FakeScanner();
        var service = new GameProfileService(scanner, settings, controller);

        using var vm = new MainViewModel(
            settings, controller, controller.Capabilities, AppPaths.GetDataFile("settings.json"), gameProfiles: service);
        await vm.InitializeAsync();

        scanner.Processes.Add(new RunningProcess(1234, "cs2", "/usr/bin/cs2"));
        await vm.PollGameAsync();
        Assert.True(vm.GameActive);

        // Leaving the override in place would make the switch look broken.
        vm.AutoGameModeOn = false;
        await WaitForAsync(() => !vm.GameActive);

        Assert.False(vm.GameActive);
        Assert.False(settings.AutoGameModeEnabled);
    }

    // ---------------------------------------------------------------- 10. tray and clean exit

    [Fact]
    public async Task Tray_OffersTheFiveQuickModesWithTranslatedLabels()
    {
        var fs = Nitro16();
        var settings = new AppSettings();
        using var controller = Controller(fs, settings);
        using var vm = new MainViewModel(settings, controller, controller.Capabilities, AppPaths.GetDataFile("settings.json"));
        await vm.InitializeAsync();

        Assert.Equal(
            new[] { OperatingMode.Quiet, OperatingMode.Default, OperatingMode.Extreme, OperatingMode.Turbo, OperatingMode.Eco },
            vm.QuickModes);

        foreach (var mode in vm.QuickModes)
        {
            Assert.False(string.IsNullOrWhiteSpace(vm.QuickModeLabel(mode)));
        }

        Assert.False(string.IsNullOrWhiteSpace(vm.Labels.TrayShow));
        Assert.False(string.IsNullOrWhiteSpace(vm.Labels.TrayCleanExit));
        Assert.False(string.IsNullOrWhiteSpace(vm.Labels.TrayQuickMode));
    }

    [Fact]
    public async Task Tray_AQuickModeGoesThroughTheSamePathAsTheModeCard()
    {
        var fs = Nitro16();
        var settings = new AppSettings();
        using var controller = Controller(fs, settings);
        using var vm = new MainViewModel(settings, controller, controller.Capabilities, AppPaths.GetDataFile("settings.json"));
        await vm.InitializeAsync();

        await vm.ApplyModeFromTrayAsync(OperatingMode.Quiet);

        Assert.Equal(OperatingMode.Quiet, vm.SelectedMode?.Mode);
        Assert.Equal("quiet", fs.Peek(LinuxPaths.PlatformProfile)?.Trim());
        Assert.Equal(OperatingMode.Quiet, settings.LastOperatingMode);
    }

    [Fact]
    public async Task Tray_HidingTheWindowExplainsWhereTheAppWent()
    {
        var fs = Nitro16();
        var settings = new AppSettings();
        using var controller = Controller(fs, settings);
        using var vm = new MainViewModel(settings, controller, controller.Capabilities, AppPaths.GetDataFile("settings.json"));
        await vm.InitializeAsync();

        vm.NotifyHiddenToTray();

        Assert.Equal(vm.Labels.MinimizedToTray, vm.Status);
    }

    [Fact]
    public async Task CleanExit_HandsTheFansBackToTheFirmwareAndDisarmsTheCurve()
    {
        var fs = Nitro16();
        var settings = new AppSettings { FanCurveEnabled = true };
        using var controller = Controller(fs, settings);
        var scanner = new FakeScanner();
        var service = new GameProfileService(scanner, settings, controller);

        using var vm = new MainViewModel(
            settings, controller, controller.Capabilities, AppPaths.GetDataFile("settings.json"), gameProfiles: service);
        await vm.InitializeAsync();

        vm.SelectedFanMode = vm.FanModes.First(f => f.Mode == FanMode.Custom);
        await RunAsync(vm.ApplyFanModeCommand);
        Assert.Equal(FanMode.Custom, await controller.GetFanModeAsync());

        await vm.CleanExitAsync();

        Assert.Equal(FanMode.Auto, await controller.GetFanModeAsync());
        Assert.Equal("0,0", fs.Peek(Sense("fan_speed"))?.Trim());
        Assert.False(vm.CurveEnabled);
        Assert.False(settings.FanCurveEnabled);
        Assert.Equal(vm.Labels.CleanExitDone, vm.Status);
    }

    [Fact]
    public async Task CleanExit_EndsARunningGameSessionFirst()
    {
        var fs = Nitro16();
        var settings = SettingsWithGame("cs2", OperatingMode.Turbo);
        using var controller = Controller(fs, settings);
        var scanner = new FakeScanner();
        var service = new GameProfileService(scanner, settings, controller);

        using var vm = new MainViewModel(
            settings, controller, controller.Capabilities, AppPaths.GetDataFile("settings.json"), gameProfiles: service);
        await vm.InitializeAsync();

        scanner.Processes.Add(new RunningProcess(1234, "cs2", "/usr/bin/cs2"));
        await vm.PollGameAsync();
        Assert.True(vm.GameActive);

        await vm.CleanExitAsync();

        Assert.False(service.IsGameActive);
        Assert.Equal("balanced", fs.Peek(LinuxPaths.PlatformProfile)?.Trim());
        Assert.Equal("0,0", fs.Peek(Sense("fan_speed"))?.Trim());
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Runs an <see cref="AsyncCommand"/> and waits for it to finish. The command is <c>async void</c> by contract, so
    /// the only handle on completion is its own re-entrancy guard: it is false while the handler runs and true again
    /// when it returns.
    /// </summary>
    private static async Task RunAsync(AsyncCommand command)
    {
        command.Execute(null);
        await WaitForAsync(() => command.CanExecute(null));
    }

    /// <summary>
    /// Waits for a condition the view model reaches on a fire-and-forget task. The switch setters cannot be awaited
    /// from a test, so the assertion polls instead of sleeping for a fixed time.
    /// </summary>
    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        Assert.True(condition(), "the condition did not become true within " + timeoutMs + " ms");
    }
}
