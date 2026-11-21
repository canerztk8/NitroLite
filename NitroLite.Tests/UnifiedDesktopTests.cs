using System.IO;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Core.Hardware.OpenRgb;
using NitroLite.Core.Hardware.Wmi;
using NitroLite.Desktop;
using NitroLite.Desktop.ViewModels;
using NitroLite.Linux;
using NitroLite.Linux.Sysfs;
using NitroLite.Windows;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The seam that lets one Avalonia window drive two operating systems: the Windows process scanner, the Windows
/// platform snapshot, the composition root, and the two cards that only exist because the Windows build has them.
/// </summary>
/// <remarks>
/// <para>
/// The scanner is tested through its injectable process table rather than the live one, so a test never has to start a
/// game. The snapshot is tested through a fixed probe delegate, so a test never has to own an Acer laptop. What is left
/// is the part that decides what the shared window shows, which is exactly the part that would break silently if the
/// two platforms drifted apart.
/// </para>
/// <para>
/// The battery card is driven against the Linux backend because that is the one that can be faked with an in-memory
/// <c>/sys</c> tree; the card itself is platform-neutral, so a green test here is a green test for the Windows path
/// too — the only difference is which controller answers the write.
/// </para>
/// </remarks>
public class UnifiedDesktopTests
{
    // ---------------------------------------------------------------- 1. Windows process scanner

    [Fact]
    public void WindowsScanner_ReportsThePidTheNameAndTheImagePath()
    {
        var scanner = new WindowsProcessScanner(() => new[]
        {
            new WindowsProcessEntry(4242, "cs2", @"C:\Program Files (x86)\Steam\steamapps\common\cs2.exe")
        });

        var process = Assert.Single(scanner.Scan());

        Assert.Equal(4242, process.Pid);
        Assert.Equal("cs2", process.Name);
        Assert.Equal(@"C:\Program Files (x86)\Steam\steamapps\common\cs2.exe", process.ExecutablePath);
    }

    [Fact]
    public void WindowsScanner_SkipsEntriesWithoutAPidOrAName()
    {
        var scanner = new WindowsProcessScanner(() => new[]
        {
            // The idle process and a process that exited between the enumeration and the read.
            new WindowsProcessEntry(0, "idle", ""),
            new WindowsProcessEntry(-4, "ghost", ""),
            new WindowsProcessEntry(900, "", ""),
            new WindowsProcessEntry(901, "   ", ""),
            new WindowsProcessEntry(902, "explorer", @"C:\Windows\explorer.exe")
        });

        var process = Assert.Single(scanner.Scan());

        Assert.Equal(902, process.Pid);
        Assert.Equal("explorer", process.Name);
    }

    [Fact]
    public void WindowsScanner_KeepsAProcessWhoseImagePathWasRefused()
    {
        // A protected process answers the name but not the path. Reporting it by name is enough for every profile that
        // names a plain executable, so the entry must survive.
        var scanner = new WindowsProcessScanner(() => new[]
        {
            new WindowsProcessEntry(77, "Cyberpunk2077", "")
        });

        var process = Assert.Single(scanner.Scan());

        Assert.Equal("Cyberpunk2077", process.FileName);
    }

    [Fact]
    public void WindowsScanner_ReturnsNothingWhenTheProcessTableCannotBeRead()
    {
        var scanner = new WindowsProcessScanner(
            () => throw new InvalidOperationException("the performance counter registry is broken"));

        Assert.Empty(scanner.Scan());
    }

    [Theory]
    [InlineData(null, "cs2", true)]          // no hint: every process is read, as before the hint existed
    [InlineData("cs2", "CS2", true)]         // the configured game itself, matched case-insensitively
    [InlineData("cs2.exe", "cs2", true)]     // a profile written with the extension still matches the short name
    [InlineData("cs2", "explorer", false)]   // a name no profile can match: the path would only repeat the name
    public void WindowsScanner_AsksForTheImagePathOnlyForANameAProfileCouldMatch(
        string? hint, string processName, bool expected)
    {
        IReadOnlyCollection<string>? executablesOfInterest = hint is null ? null : new[] { hint };

        Assert.Equal(expected, WindowsProcessScanner.NeedsImagePath(processName, executablesOfInterest));
    }

    [Fact]
    public void WindowsScanner_ReturnsTheWholeTableWhenTheScannerCannotUseTheHint()
    {
        // The hint is an optimisation, not a filter: a table that was already built — a test's, or a platform's that
        // reads no paths — must come back untouched even when the hint names only one of its processes.
        var scanner = new WindowsProcessScanner(() => new[]
        {
            new WindowsProcessEntry(1, "cs2", @"C:\Games\cs2.exe"),
            new WindowsProcessEntry(2, "explorer", @"C:\Windows\explorer.exe")
        })
        {
            ExecutablesOfInterest = new[] { "cs2" }
        };

        var processes = scanner.Scan();

        Assert.Equal(2, processes.Count);
        Assert.Contains(processes, p => p.Matches("explorer.exe"));
    }

    [Fact]
    public async Task WindowsScanner_MatchesAProfileByExecutableName()
    {
        var settings = new AppSettings
        {
            GameProfiles = new List<GameProfile>
            {
                new() { ExeName = "Cyberpunk2077", DisplayName = "Cyberpunk 2077", TargetMode = OperatingMode.Turbo }
            }
        };

        var scanner = new WindowsProcessScanner(() => new[]
        {
            new WindowsProcessEntry(1234, "Cyberpunk2077", @"D:\Games\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe")
        });

        var service = new GameProfileService(scanner, settings);
        var result = await service.PollAsync();

        Assert.Equal(GameDetectionChange.Started, result.Change);
        Assert.Equal("Cyberpunk2077", result.Profile?.ExeName);
        Assert.True(service.IsGameActive);
    }

    [Theory]
    [InlineData(@"C:\Games\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe", "Cyberpunk2077.exe")]
    [InlineData(@"D:\Games\cs2.exe", "cs2.exe")]
    [InlineData("/usr/bin/cs2", "cs2")]
    [InlineData("cs2", "cs2")]
    [InlineData(@"C:\Games\folder\", "folder")]
    public void RunningProcess_FileName_StripsEitherSeparator(string path, string expected)
    {
        var process = new RunningProcess(1, "ignored", path);

        Assert.Equal(expected, process.FileName);
    }

    [Fact]
    public void RunningProcess_MatchesAWindowsPathAgainstABareProfileName()
    {
        // The regression this guards: a matcher that split only on '/' compared the whole Windows path against the
        // profile's bare executable name and never matched a game on Windows.
        var process = new RunningProcess(1, "Cyberpunk2077", @"D:\Games\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe");

        Assert.True(process.Matches("Cyberpunk2077"));
        Assert.True(process.Matches("Cyberpunk2077.exe"));
        Assert.False(process.Matches("cs2"));
    }

    // ---------------------------------------------------------------- 2. Windows platform snapshot

    [Theory]
    [InlineData("WMI", "wmi")]
    [InlineData("AASSvc", "aassvc")]
    [InlineData("", "none")]
    [InlineData("something else", "none")]
    public void WindowsSnapshot_MapsTheBackendNameToTheSharedTierVocabulary(string backend, string expected)
    {
        var snapshot = Snapshot(backend: backend);

        Assert.Equal(expected, snapshot.Tier);
    }

    [Fact]
    public void WindowsSnapshot_NeverAsksForAUdevRule()
    {
        // The udev rule is a Linux permission fix. Showing the banner on Windows would send the user to a command that
        // does not exist on their machine.
        Assert.False(Snapshot().NeedsUdevRule);
    }

    [Fact]
    public void WindowsSnapshot_NotesCarryTheProfileNotesAndTheRejections()
    {
        var snapshot = Snapshot(probe: () => new CapabilitySnapshot
        {
            ProfileNotes = "AN16-41 matched from the embedded table",
            Rejections = new[] { "profile 7: unknown mode 'turbo2'" }
        });

        Assert.Equal(
            new[] { "AN16-41 matched from the embedded table", "profile 7: unknown mode 'turbo2'" },
            snapshot.Notes);
    }

    [Fact]
    public void WindowsSnapshot_NotesAreEmptyWhenTheProbeFoundNothing()
    {
        // An empty list is what hides the Notes card, so "no probe" must not produce a blank row.
        Assert.Empty(Snapshot(probe: () => null).Notes);
    }

    [Fact]
    public void WindowsSnapshot_RescanClearsTheReadBackCounters()
    {
        var settings = new AppSettings();
        settings.CapabilitySessionFailures[ReadBackPolicy.FanSpeed] = 2;
        settings.DisabledCapabilities.Add(ReadBackPolicy.MuxMode);

        var readBack = new ReadBackPolicy(settings);
        var snapshot = Snapshot(readBack: readBack);

        Assert.Equal(2, readBack.ConsecutiveSessionFailures(ReadBackPolicy.FanSpeed));
        Assert.True(readBack.IsPermanentlyDisabled(ReadBackPolicy.MuxMode));

        ((IRescannableBackend)snapshot).ResetReadBackCounters();

        Assert.Equal(0, readBack.ConsecutiveSessionFailures(ReadBackPolicy.FanSpeed));
        Assert.False(readBack.IsPermanentlyDisabled(ReadBackPolicy.MuxMode));
    }

    [Fact]
    public void WindowsSnapshot_RescanHandsBackItselfAsTheNewSnapshot()
    {
        var snapshot = Snapshot();

        Assert.Same(snapshot, ((IRescannableBackend)snapshot).Snapshot);
        Assert.False(((IRescannableBackend)snapshot).PermissionHintRaised);
    }

    [Fact]
    public void WindowsSnapshot_ReportNamesTheModelAndLeaksNoPersonalData()
    {
        var report = Snapshot().BuildHardwareReport();

        Assert.Contains("AN16-41", report);
        Assert.Contains("NitroLite hardware report", report);

        // The report is meant to be pasted into a public issue, so the two things that identify a person rather than a
        // model must not be in it.
        Assert.DoesNotContain(Environment.UserName, report);
        Assert.DoesNotContain(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), report);
    }

    [Fact]
    public void WindowsSnapshot_ReportIsEmptyWhenTheProbeFoundNothing()
    {
        Assert.Equal("", Snapshot(probe: () => null).BuildHardwareReport());
    }

    // ---------------------------------------------------------------- 3. composition root

    [Fact]
    public void AppHost_OnWindows_WiresTheReportAndTheAutoStartCard()
    {
        // The composition root selects the UI language from the settings file it loads, and Loc is process-wide.
        // Without the restore this test leaves the suite on whatever language an earlier test happened to save,
        // which turns every later Turkish assertion into an order-dependent failure.
        var original = Loc.Language;
        try
        {
            using var host = AppHost.Create();

            // Both cards exist only because the Windows composition root supplied a platform snapshot and a sign-in
            // mechanism. A regression that dropped either would hide a feature the WPF build had.
            Assert.True(host.ViewModel.HardwareReportAvailable);
            Assert.True(host.ViewModel.AutoStartAvailable);

            // The udev banner is a Linux-only affordance.
            Assert.False(host.ViewModel.NeedsUdevRule);
        }
        finally
        {
            Loc.SetLanguage(original);
        }
    }

    // ---------------------------------------------------------------- 4. battery charge limit card

    [Fact]
    public async Task BatteryLimit_CardIsShownWhenTheBackendSupportsIt()
    {
        var (vm, controller) = Create(BatteryFixture());
        using var _ = vm;
        using var __ = controller;

        await vm.InitializeAsync();

        Assert.True(vm.BatteryLimitAvailable);
    }

    [Fact]
    public async Task BatteryLimit_CardIsHiddenWhenTheBackendHasNoNode()
    {
        var (vm, controller) = Create(BatteryFixture(withLimiter: false, withThreshold: false));
        using var _ = vm;
        using var __ = controller;

        await vm.InitializeAsync();

        Assert.False(vm.BatteryLimitAvailable);
    }

    [Fact]
    public async Task BatteryLimit_TurningItOnWritesTheEightyPercentThreshold()
    {
        var fs = BatteryFixture();
        var (vm, controller) = Create(fs);
        using var _ = vm;
        using var __ = controller;

        await vm.InitializeAsync();
        vm.BatteryLimitOn = true;

        await WaitForAsync(() => fs.Writes.Any(w => w.Path.EndsWith("battery_limiter", StringComparison.Ordinal)));

        var write = fs.Writes.Last(w => w.Path.EndsWith("battery_limiter", StringComparison.Ordinal));
        Assert.Equal("1", write.Value.Trim());
        Assert.True(vm.BatteryLimitOn);
    }

    [Fact]
    public async Task BatteryLimit_TurningItOffWritesTheFullChargeValue()
    {
        var fs = BatteryFixture();
        fs.WithFile(Sense("battery_limiter"), "1\n");

        var (vm, controller) = Create(fs);
        using var _ = vm;
        using var __ = controller;

        await vm.InitializeAsync();
        Assert.True(vm.BatteryLimitOn);

        vm.BatteryLimitOn = false;

        await WaitForAsync(() => fs.Writes.Any(w => w.Path.EndsWith("battery_limiter", StringComparison.Ordinal)));

        var write = fs.Writes.Last(w => w.Path.EndsWith("battery_limiter", StringComparison.Ordinal));
        Assert.Equal("0", write.Value.Trim());
    }

    [Fact]
    public async Task BatteryLimit_IsNotCountedAsAnExtra()
    {
        // The battery limit has its own card on the dashboard, so a machine that supports only that must still say
        // "no extras" on the lighting page rather than leaving an empty box.
        var (vm, controller) = Create(BatteryFixture());
        using var _ = vm;
        using var __ = controller;

        await vm.InitializeAsync();

        Assert.True(vm.BatteryLimitAvailable);
        Assert.True(vm.NoExtrasAvailable);
    }

    // ---------------------------------------------------------------- fixtures

    /// <summary>
    /// A snapshot over a fixed probe. <paramref name="probe"/> is a delegate rather than a value so a test can hand
    /// back a genuine null, which is what a probe that could not answer looks like.
    /// </summary>
    private static WindowsPlatformSnapshot Snapshot(
        string backend = "WMI",
        Func<CapabilitySnapshot?>? probe = null,
        ReadBackPolicy? readBack = null) =>
        new(
            new DeviceIdentity
            {
                Manufacturer = "Acer",
                Model = "Nitro AN16-41",
                ModelCode = "AN16-41",
                ModelFamily = "Nitro 16",
                BiosVersion = "1.27",
                BiosVendor = "INSYDE Corp.",
                CpuVendor = CpuVendor.Amd,
                IsAcer = true
            },
            probe ?? (() => new CapabilitySnapshot { ProfileId = "acer-nitro-an16-41", ProfileSource = "embedded" }),
            () => backend,
            readBack ?? new ReadBackPolicy(new AppSettings()));

    private static string SenseRoot() =>
        LinuxPaths.Combine(LinuxPaths.LinuwuModuleRoot, LinuxPaths.NitroSense);

    private static string Sense(string name) => LinuxPaths.Combine(SenseRoot(), name);

    /// <summary>
    /// A Nitro 16 with the two battery nodes and nothing else optional, so the extras page has nothing to show.
    /// </summary>
    private static InMemorySysfsFileSystem BatteryFixture(bool withLimiter = true, bool withThreshold = true)
    {
        var fs = new InMemorySysfsFileSystem();

        fs.WithFile("/sys/class/dmi/id/product_name", "Nitro AN16-41\n");
        fs.WithFile("/sys/class/dmi/id/board_name", "AN16-41\n");
        fs.WithFile("/sys/class/dmi/id/sys_vendor", "Acer\n");
        fs.WithFile("/sys/class/dmi/id/bios_version", "V1.27\n");
        fs.WithFile("/sys/class/dmi/id/bios_vendor", "Insyde\n");

        fs.WithFile(LinuxPaths.PlatformProfile, "balanced\n");
        fs.WithFile(LinuxPaths.PlatformProfileChoices, "quiet balanced balanced-performance performance\n");

        fs.WithFile("/sys/class/hwmon/hwmon0/name", "acer\n");
        fs.WithFile("/sys/class/hwmon/hwmon0/temp1_input", "55000\n");
        fs.WithFile("/sys/class/hwmon/hwmon0/fan1_input", "2436\n");
        fs.WithFile("/sys/class/hwmon/hwmon0/fan2_input", "2409\n");

        fs.WithDirectory(LinuxPaths.AcpiPlatformRoot);
        fs.WithDirectory(SenseRoot());

        if (withThreshold) fs.WithFile("/sys/class/power_supply/BAT1/charge_control_end_threshold", "100\n");
        if (withLimiter) fs.WithFile(Sense("battery_limiter"), "0\n");

        return fs;
    }

    /// <summary>
    /// A view model over a real <see cref="LinuxSysfsHardwareController"/> and an in-memory <c>/sys</c> tree. The
    /// OpenRGB client is pointed at a closed port so a developer running OpenRGB cannot change a result.
    /// </summary>
    private static (MainViewModel Vm, LinuxSysfsHardwareController Controller) Create(InMemorySysfsFileSystem fs)
    {
        var settings = new AppSettings();
        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _);
        var controller = LinuxSysfsHardwareController.Create(settings, fs, catalog, new OpenRgbClient(port: 1));

        var vm = new MainViewModel(
            settings,
            controller,
            controller.Capabilities,
            Path.Combine(Path.GetTempPath(), "nitrolite-unified-" + Guid.NewGuid().ToString("N") + ".json"));

        return (vm, controller);
    }

    /// <summary>Waits for a condition the view model reaches on a fire-and-forget write task.</summary>
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
