using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NitroLite.Core;
using NitroLite.Desktop.ViewModels;
using NitroLite.Linux;
using NitroLite.Windows;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// P5 and P6: the work that stopped the app polling things nobody was looking at, and the hardening that came with
/// it — the redaction of a log line, the host allowlist on a download, the size cap on a file the app did not write.
/// </summary>
/// <remarks>
/// Every test here asserts a decision rather than a mechanism: that a hidden window does not rebuild a chart, that a
/// machine with no game rules never reads the process table, that a download from a look-alike host is refused. The
/// seams they drive are the ones the production code already had, so a test that passes is a statement about the
/// shipped path and not about a copy of it.
/// </remarks>
public class PerformanceAndHardeningTests
{
    private static string TempSettingsPath() =>
        Path.Combine(Path.GetTempPath(), "nitrolite-perf-" + Guid.NewGuid().ToString("N") + ".json");

    /// <summary>The same view model, standing on the page that owns the chart.</summary>
    /// <remarks>
    /// The chart is rebuilt while the monitoring page is the page on screen, and once when it opens. A chart on a
    /// page nobody is looking at is geometry and formatting per tick for a control that is not in the tree; the
    /// series keeps filling in either way, so arriving on the page redraws from the history already held.
    /// </remarks>
    private static MainViewModel OnChartPage(MainViewModel vm)
    {
        vm.SelectedTab = vm.NavTabs.First(tab => tab.Key == MainViewModel.NavMonitoringKey);
        return vm;
    }

    // ---------------------------------------------------------------- RACE-11 / RACE-12: the chart nobody sees

    [Fact]
    public async Task ChartVisible_HiddenSkipsTheRebuild_AndComingBackRebuildsFromTheHistory()
    {
        var hardware = new Phase3Fakes.StubHardware();
        using var vm = OnChartPage(new MainViewModel(new AppSettings(), hardware, null, TempSettingsPath()));

        await vm.RefreshTelemetryAsync();
        await vm.RefreshTelemetryAsync();

        string at58 = TelemetryMetrics.Format(TelemetryMetrics.CpuTemp, 58);
        Assert.Equal(at58, vm.ChartCurrentText);

        // The window goes to the tray. The reading changes, and the chart must not spend a tick on a series that
        // cannot be seen — but the history keeps accumulating underneath it.
        vm.ChartVisible = false;
        hardware.Sample = new MonitorData
        {
            CpuTemperature = 71,
            GpuTemperature = 51,
            SysTemperature = 44,
            CpuFanSpeed = 2200,
            GpuFanSpeed = 2150,
            IsFanRpmMeasured = true
        };

        await vm.RefreshTelemetryAsync();

        Assert.Equal(at58, vm.ChartCurrentText);

        // Coming back rebuilds immediately, from the history that kept growing while the window was away.
        vm.ChartVisible = true;

        Assert.Equal(TelemetryMetrics.Format(TelemetryMetrics.CpuTemp, 71), vm.ChartCurrentText);
        Assert.Equal(3, vm.ChartValues.Count);
    }

    [Fact]
    public async Task ChartVisible_StaysTrueByDefault_SoTheFirstTickDraws()
    {
        var hardware = new Phase3Fakes.StubHardware();
        using var vm = OnChartPage(new MainViewModel(new AppSettings(), hardware, null, TempSettingsPath()));

        Assert.True(vm.ChartVisible);

        await vm.RefreshTelemetryAsync();
        await vm.RefreshTelemetryAsync();

        Assert.False(vm.ChartEmpty);
    }

    // ---------------------------------------------------------------- RACE-12: the process table nobody needs

    /// <summary>A scanner that counts how many times the process table was read.</summary>
    private sealed class CountingScanner : IProcessScanner
    {
        public int Calls { get; private set; }

        public IReadOnlyList<RunningProcess> Scan()
        {
            Calls++;
            return Array.Empty<RunningProcess>();
        }
    }

    [Fact]
    public async Task GameDetection_WithNoAutoApplyProfiles_NeverReadsTheProcessTable()
    {
        var scanner = new CountingScanner();
        var settings = new AppSettings
        {
            AutoGameModeEnabled = true,
            GameProfiles = new List<GameProfile>()
        };

        var service = new GameProfileService(scanner, settings);

        var result = await service.PollAsync();

        Assert.Equal(GameDetectionChange.None, result.Change);
        Assert.Equal(0, scanner.Calls);
    }

    [Fact]
    public async Task GameDetection_WithAProfile_ReadsTheProcessTableOnce()
    {
        var scanner = new CountingScanner();
        var settings = new AppSettings
        {
            AutoGameModeEnabled = true,
            GameProfiles = new List<GameProfile>
            {
                new() { ExeName = "somegame.exe", AutoApply = true }
            }
        };

        var service = new GameProfileService(scanner, settings);

        await service.PollAsync();

        Assert.Equal(1, scanner.Calls);
    }

    [Fact]
    public async Task GameDetection_WithAutomaticSwitchingOff_NeverReadsTheProcessTable()
    {
        var scanner = new CountingScanner();
        var settings = new AppSettings
        {
            AutoGameModeEnabled = false,
            GameProfiles = new List<GameProfile>
            {
                new() { ExeName = "somegame.exe", AutoApply = true }
            }
        };

        var service = new GameProfileService(scanner, settings);

        await service.PollAsync();

        Assert.Equal(0, scanner.Calls);
    }

    /// <summary>A scanner that remembers the hint it was handed, and returns a table that matches nothing.</summary>
    private sealed class HintRecordingScanner : IProcessScanner
    {
        public IReadOnlyCollection<string>? ExecutablesOfInterest { get; set; }

        public IReadOnlyList<RunningProcess> Scan() => Array.Empty<RunningProcess>();
    }

    [Fact]
    public async Task GameDetection_HandsTheConfiguredNamesToTheScanner()
    {
        // The Windows scanner opens a handle per process to read an image path it does not need. It can only skip that
        // if the names it is supposed to find are known before the scan, so detection has to hand them over.
        var scanner = new HintRecordingScanner();
        var settings = new AppSettings
        {
            AutoGameModeEnabled = true,
            GameProfiles = new List<GameProfile>
            {
                new() { ExeName = "Cyberpunk2077", AutoApply = true },
                new() { ExeName = "cs2.exe", AutoApply = true },
                new() { ExeName = "manual-only", AutoApply = false }
            }
        };

        await new GameProfileService(scanner, settings).PollAsync();

        Assert.NotNull(scanner.ExecutablesOfInterest);
        Assert.Equal(new[] { "Cyberpunk2077", "cs2.exe" }, scanner.ExecutablesOfInterest);
    }

    [Fact]
    public async Task GameDetection_WithNoAutoApplyProfiles_LeavesTheHintUnset()
    {
        var scanner = new HintRecordingScanner();
        var settings = new AppSettings
        {
            AutoGameModeEnabled = true,
            GameProfiles = new List<GameProfile>()
        };

        await new GameProfileService(scanner, settings).PollAsync();

        Assert.Null(scanner.ExecutablesOfInterest);
    }

    // ---------------------------------------------------------------- SEC-08 / SEC-09: what a log line may carry

    [Fact]
    public void Redaction_ReplacesTheWindowsProfileFolder_AndKeepsTheRestOfThePath()
    {
        var scrubbed = Redaction.Scrub(@"C:\Users\caner\AppData\Local\NitroLite\settings.json");

        Assert.Equal(@"C:\Users\<user>\AppData\Local\NitroLite\settings.json", scrubbed);
        Assert.DoesNotContain("caner", scrubbed, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Redaction_ReplacesTheUnixHomeFolder()
    {
        var scrubbed = Redaction.Scrub("/home/caner/.config/nitrolite/settings.json");

        Assert.Equal("/home/<user>/.config/nitrolite/settings.json", scrubbed);
    }

    [Fact]
    public void Redaction_LeavesTextWithoutAProfileAlone()
    {
        const string line = "Acer WMI: fan write accepted (cpu=55, gpu=55)";

        Assert.Equal(line, Redaction.Scrub(line));
    }

    [Fact]
    public void Redaction_StillReplacesTheMachineNameInALineWithNoPathInIt()
    {
        // The three path patterns are skipped when the line has no separator, because none of them can match without
        // one. The machine name is not a path and must still go.
        string machine = Environment.MachineName;
        if (machine.Length < 3) return; // A two-letter name is deliberately left alone.

        var scrubbed = Redaction.Scrub($"the update was built on {machine} this morning");

        Assert.DoesNotContain(machine, scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<machine>", scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public void Redaction_ReplacesTheMachineName_WhenItIsLongEnoughToBeOne()
    {
        string machine = Environment.MachineName;
        if (machine.Length < 3) return; // A two-letter name is deliberately left alone.

        var scrubbed = Redaction.ScrubMachineName($"opened \\\\{machine}\\share on {machine}");

        Assert.DoesNotContain(machine, scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Redaction.MachinePlaceholder, scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public void Redaction_PassesNullAndEmptyThrough()
    {
        Assert.Equal("", Redaction.Scrub(null));
        Assert.Equal("", Redaction.Scrub(""));
        Assert.Equal("", Redaction.ScrubMachineName(null));
    }

    // ---------------------------------------------------------------- SEC-17 / SEC-18: where a download may come from

    [Theory]
    [InlineData("https://github.com/canerztk8/NitroLite/releases/download/v1.2.0/NitroLite-win-x64.zip", true)]
    [InlineData("https://api.github.com/repos/canerztk8/NitroLite/releases/latest", true)]
    [InlineData("https://objects.githubusercontent.com/some/asset.zip", true)]
    [InlineData("https://release-assets.githubusercontent.com/some/asset.zip", true)]
    [InlineData("https://evil-github.com/NitroLite.zip", false)]
    [InlineData("https://github.com.evil.example/NitroLite.zip", false)]
    [InlineData("http://github.com/canerztk8/NitroLite/releases/download/v1.2.0/x.zip", false)]
    [InlineData("https://raw.githubusercontent.com/canerztk8/NitroLite/main/x.zip", false)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void UpdateService_OnlyTrustsTheGitHubHosts(string? url, bool expected)
    {
        Assert.Equal(expected, UpdateService.IsTrustedDownloadUrl(url));
    }

    [Fact]
    public void UpdateService_TrustedHosts_AreTheFourTheReleasePipelineUses()
    {
        Assert.Equal(
            new[]
            {
                "github.com",
                "api.github.com",
                "objects.githubusercontent.com",
                "release-assets.githubusercontent.com"
            },
            UpdateService.TrustedHosts);
    }

    // ---------------------------------------------------------------- SEC-19: a file too large to be what it claims

    [Fact]
    public void BoundedTextFile_RefusesAFileOverTheLimit()
    {
        string path = Path.Combine(Path.GetTempPath(), "nitrolite-big-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            // Sparse: the length is what the guard reads, so nothing has to be written to disk.
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            {
                stream.SetLength(BoundedTextFile.MaxBytes + 1);
            }

            Assert.Throws<InvalidDataException>(() => BoundedTextFile.ReadAllText(path));
            Assert.Null(BoundedTextFile.TryReadAllText(path));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void BoundedTextFile_ReadsAFileUnderTheLimit()
    {
        string path = Path.Combine(Path.GetTempPath(), "nitrolite-small-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{\"theme\":\"dark\"}");

            Assert.Equal("{\"theme\":\"dark\"}", BoundedTextFile.ReadAllText(path));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    // ---------------------------------------------------------------- SEC-23 / SEC-35: a path out of a file the user can edit

    [Theory]
    [InlineData(@"C:\Program Files (x86)\Steam\steamapps", true)]
    [InlineData(@"D:\SteamLibrary", true)]
    [InlineData(@"\\host\share\steamapps", false)]
    [InlineData("//host/share/steamapps", false)]
    [InlineData(@"..\..\Windows", false)]
    [InlineData("steamapps", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void SteamLibraryPaths_OnlyAcceptsAnAbsoluteLocalPath(string? raw, bool expected)
    {
        Assert.Equal(expected, SteamLibraryPaths.TryNormalize(raw, out _));
    }

    [Fact]
    public void SteamLibraryPaths_ResolvesDotDotSegments()
    {
        Assert.True(SteamLibraryPaths.TryNormalize(@"C:\Games\Steam\..\Steam\steamapps", out var normalized));
        Assert.Equal(Path.GetFullPath(@"C:\Games\Steam\steamapps"), normalized);
    }

    [Fact]
    public void GameProfile_WithAUncInstallPath_IsNotInstalled()
    {
        // Refused without being probed: File.Exists on a UNC path opens an SMB session.
        var profile = new GameProfile { ExeName = "game.exe", InstallPath = @"\\host\share\game.exe" };

        Assert.False(profile.IsInstalled);
    }

    [Fact]
    public void GameProfile_WithNoInstallPath_IsTreatedAsInstalled()
    {
        Assert.True(new GameProfile { ExeName = "game.exe" }.IsInstalled);
    }

    // ---------------------------------------------------------------- SEC-16: the ioctl the kernel actually expects

    [Fact]
    public void Evdev_IoctlRequest_EncodesDirectionSizeTypeAndNumberInThatOrder()
    {
        // EVIOCGNAME(256) as the kernel defines it: _IOC_READ, 256 bytes, type 'E', number 0x06.
        Assert.Equal((nuint)0x81004506, RealEvdevSource.IoctlRequest(0x06, 256));

        // The order that used to be used put the number in the type byte and vice versa.
        Assert.NotEqual((nuint)0x81000645, RealEvdevSource.IoctlRequest(0x06, 256));
    }

    [Fact]
    public void Evdev_IoctlRequest_EncodesTheEventBitQuery()
    {
        // EVIOCGBIT(EV_KEY, 8): number 0x20 + 0x01, size 8.
        Assert.Equal((nuint)0x80084521, RealEvdevSource.IoctlRequest(0x20 + 0x01, 8));
    }

    // ---------------------------------------------------------------- RACE-13: the foreground window read once

    [Fact]
    public void NitroSenseWatcher_ReadsTheForegroundWindowOncePerHandle()
    {
        using var watcher = new NitroSenseWatcher();
        int reads = 0;

        ForegroundWindowInfo Read(IntPtr handle)
        {
            reads++;
            return new ForegroundWindowInfo(4242, "NitroSense", "#32770", "Title", "Content", handle);
        }

        var handle = new IntPtr(0x1234);

        var first = watcher.CachedForegroundWindow(handle, Read);
        var second = watcher.CachedForegroundWindow(handle, Read);

        Assert.Equal(1, reads);
        Assert.Equal(first, second);
        Assert.Equal(4242, second.ProcessId);

        // A different window is a different answer, and it is read.
        var other = watcher.CachedForegroundWindow(new IntPtr(0x5678), Read);

        Assert.Equal(2, reads);
        Assert.Equal(0x5678, other.Handle.ToInt64());
    }

    [Fact]
    public void NitroSenseWatcher_WithNoForegroundWindow_AnswersNoneWithoutReading()
    {
        using var watcher = new NitroSenseWatcher();
        int reads = 0;

        var info = watcher.CachedForegroundWindow(IntPtr.Zero, handle =>
        {
            reads++;
            return ForegroundWindowInfo.ForProcess(1);
        });

        Assert.Equal(0, reads);
        Assert.Equal(ForegroundWindowInfo.None, info);
    }

    // ---------------------------------------------------------------- RACE-14: one WMI watcher, not three

    [Fact]
    public void NitroKeyListener_StopsAtTheFirstClassThatAccepts()
    {
        var asked = new List<string>();

        var chosen = WindowsNitroKeyListener.FirstClassThatAccepts(
            new[] { "A", "B", "C" },
            className =>
            {
                asked.Add(className);
                return className == "B";
            });

        Assert.Equal("B", chosen);
        Assert.Equal(new[] { "A", "B" }, asked);
    }

    [Fact]
    public void NitroKeyListener_WithNoAcceptingClass_AnswersNull()
    {
        var chosen = WindowsNitroKeyListener.FirstClassThatAccepts(
            new[] { "A", "B" },
            _ => false);

        Assert.Null(chosen);
    }

    // ---------------------------------------------------------------- RACE-11: a transient failure is retried

    /// <summary>A display that can fail its first read, the way a driver that is not ready yet does.</summary>
    private sealed class FlakyDisplay : IDisplayRefreshRateService
    {
        public int Reads { get; private set; }

        public int Rate { get; set; } = 165;

        /// <summary>True when the first read answers zero, which is what the automation reads as a failure.</summary>
        public bool FailFirstRead { get; init; } = true;

        public int GetCurrentRefreshRate()
        {
            Reads++;

            return FailFirstRead && Reads == 1 ? 0 : Rate;
        }

        public int GetMaxRefreshRate() => 165;

        public bool SetRefreshRate(int hz)
        {
            Rate = hz;
            return true;
        }
    }

    [Fact]
    public void RefreshRateAutomation_RetriesAfterATransientFailure()
    {
        var display = new FlakyDisplay();
        var automation = new RefreshRateAutomation(display, new AppSettings { AutoRefreshRateSwitching = true });

        var first = automation.Apply(onAc: false);
        Assert.Equal(RefreshRateAction.Failed, first.Action);

        // The power state has not changed, and the old early return would have swallowed the retry for the rest of
        // the session. The panel is lowered on the second tick instead.
        var second = automation.Apply(onAc: false);

        Assert.Equal(RefreshRateAction.LoweredToBattery, second.Action);
        Assert.Equal(60, display.Rate);
    }

    [Fact]
    public void RefreshRateAutomation_RepeatedTickAfterSuccess_IsFree()
    {
        var display = new FlakyDisplay { Rate = 165, FailFirstRead = false };
        var automation = new RefreshRateAutomation(display, new AppSettings { AutoRefreshRateSwitching = true });

        Assert.Equal(RefreshRateAction.LoweredToBattery, automation.Apply(onAc: false).Action);

        // A second tick on battery is a no-op: the saved rate is not overwritten with 60, which would lose the user's
        // own value and leave nothing to restore when the adapter comes back.
        var again = automation.Apply(onAc: false);

        Assert.Equal(RefreshRateAction.None, again.Action);
        Assert.Equal(60, display.Rate);
        Assert.Equal(165, automation.SavedRate);
    }
}
