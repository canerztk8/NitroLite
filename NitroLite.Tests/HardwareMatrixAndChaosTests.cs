using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows.Input;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Core.Hardware.OpenRgb;
using NitroLite.Core.Hardware.Wmi;
using NitroLite.Desktop.ViewModels;
using NitroLite.Linux;
using NitroLite.Linux.Sysfs;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The hardware matrix and the chaos pass: five chassis, a fake <c>/sys</c> tree on a real disk, and a decorator that
/// answers nonsense on purpose.
/// </summary>
/// <remarks>
/// <para>
/// The three layers here answer three different questions. The matrix asks whether one build of the window survives
/// every machine the model table knows about — a Nitro 16 with everything, a Predator with a narrower WMI surface, an
/// old Nitro 5 whose GPU tachometer answers zero, a Nitro V 15 that refuses the battery limiter and ignores the fan
/// write, and a virtual machine with no Acer nodes at all. The sysfs layer asks whether the reader and the writer
/// behave on a tree that is missing a node, holds text that is not a number, or is owned by root. The chaos layer asks
/// whether a backend that times out, refuses and reports 0 °C, 127 °C and NaN can take the window down.
/// </para>
/// <para>
/// The matrix runs against <see cref="InMemorySysfsFileSystem"/> because that is the seam the backend was built
/// around: a hundred chassis would otherwise need a hundred laptops. The sysfs layer deliberately does not — it writes
/// a real directory and reads it through <see cref="RealSysfsFileSystem"/>, so the file I/O, the newline trimming and
/// the <c>UnauthorizedAccessException</c> mapping are the production ones rather than a fake's restatement of them.
/// </para>
/// </remarks>
public class HardwareMatrixAndChaosTests
{
    // ================================================================================================================
    // 1. The chassis matrix
    // ================================================================================================================

    /// <summary>The five machines the matrix covers.</summary>
    public enum Chassis
    {
        /// <summary>A Nitro 16 AN16-41 with both tiers, four zones, a MUX switch and every sensor answering.</summary>
        Nitro16An1641,

        /// <summary>A Predator Helios 16: Turbo on, a narrower WMI surface and the higher Predator fan ceilings.</summary>
        PredatorHelios16,

        /// <summary>An old Nitro 5: one lighting zone, no MUX, and a GPU tachometer that answers zero.</summary>
        Nitro5Legacy,

        /// <summary>A Nitro V 15 whose firmware has no battery limiter and ignores the custom fan write.</summary>
        NitroV15Restricted,

        /// <summary>A virtual machine: no Acer module, no hwmon, no power supply, nothing.</summary>
        UnsupportedVm
    }

    /// <summary>What one chassis is expected to report, so the theory body stays about the window rather than the tree.</summary>
    private sealed record ChassisExpectation(
        string ProfileId,
        string Tier,
        OperatingMode? Mode,
        bool Lighting,
        bool Mux,
        bool BatteryLimit,
        bool FanControl,
        string CpuTemperatureText,
        string GpuTemperatureText,
        string CpuFanRpmText,
        string GpuFanRpmText);

    /// <summary>
    /// Every tab, then every command, on each of the five chassis.
    /// </summary>
    /// <remarks>
    /// The assertion is not "the command returned true" — a machine with no MUX is supposed to refuse a MUX write.
    /// It is that nothing escaped: <see cref="AsyncCommand.Execute"/> is <c>async void</c>, so an exception out of a
    /// handler is raised on the thread pool where nothing catches it and the process dies. A test that drives the
    /// commands and then finds the window still usable is the only way to see that from here.
    /// </remarks>
    [Theory]
    [InlineData(Chassis.Nitro16An1641)]
    [InlineData(Chassis.PredatorHelios16)]
    [InlineData(Chassis.Nitro5Legacy)]
    [InlineData(Chassis.NitroV15Restricted)]
    [InlineData(Chassis.UnsupportedVm)]
    public async Task ChassisMatrix_EveryTabAndEveryCommandRunsWithoutAnUnhandledException(Chassis chassis)
    {
        var (fs, expected) = Build(chassis);
        var settings = new AppSettings();
        var settingsPath = TempSettingsPath();

        using var openRgb = new OpenRgbClient(port: 1);
        using var controller = LinuxSysfsHardwareController.Create(settings, fs, Catalog(), openRgb);
        using var vm = CreateViewModel(settings, controller, controller.Capabilities, settingsPath, controller);

        await vm.InitializeAsync();
        await vm.RefreshTelemetryAsync();

        // The probe decides, not the model name: the same chassis ships with and without the out-of-tree module.
        Assert.Equal(expected.ProfileId, controller.Capabilities.Profile.Id);
        Assert.Equal(expected.Tier, controller.Capabilities.Tier);
        Assert.Equal(expected.Mode, await controller.GetOperatingModeAsync());
        Assert.Equal(expected.Lighting, controller.Capabilities.Hardware.KeyboardLighting);
        Assert.Equal(expected.Mux, controller.Capabilities.Hardware.MuxSwitch);
        Assert.Equal(expected.BatteryLimit, controller.Capabilities.Hardware.BatteryChargeLimit);
        Assert.Equal(expected.FanControl, controller.Capabilities.Hardware.FanControl);

        // A sensor that did not answer is a dash, never a zero dressed up as a reading.
        Assert.Equal(expected.CpuTemperatureText, vm.CpuTemperatureText);
        Assert.Equal(expected.GpuTemperatureText, vm.GpuTemperatureText);
        Assert.Equal(expected.CpuFanRpmText, vm.CpuFanRpmText);
        Assert.Equal(expected.GpuFanRpmText, vm.GpuFanRpmText);

        await TriggerEveryTabAndCommandAsync(vm);

        // The window is still usable after the sweep, which is what "no unhandled exception" has to mean here.
        await vm.RefreshTelemetryAsync();
        Assert.Equal(expected.CpuTemperatureText, vm.CpuTemperatureText);
        Assert.Equal(expected.CpuFanRpmText, vm.CpuFanRpmText);
    }

    /// <summary>
    /// The Nitro V 15's two restrictions, checked on the backend rather than through the window: the battery limiter
    /// is absent, and the fan write is accepted by the file system and then ignored by the firmware.
    /// </summary>
    [Fact]
    public async Task NitroV15Restricted_RefusesTheBatteryLimitAndCatchesTheIgnoredFanWrite()
    {
        var (fs, _) = Build(Chassis.NitroV15Restricted);
        var settings = new AppSettings();

        using var openRgb = new OpenRgbClient(port: 1);
        using var controller = LinuxSysfsHardwareController.Create(settings, fs, Catalog(), openRgb);

        Assert.Null(await controller.GetBatteryChargeModeAsync());
        Assert.False(await controller.SetBatteryChargeModeAsync(BatteryChargeMode.Optimized80));

        // The write reaches the file system and the value does not move, so the read-back is the only thing that can
        // tell the caller the firmware dropped it.
        Assert.False(await controller.SetFanModeAsync(FanMode.Custom, 70, 70));
        Assert.True(controller.ReadBack.IsDisabled(ReadBackPolicy.FanSpeed));
    }

    /// <summary>
    /// The old Nitro 5's GPU tachometer answers zero. Zero is a real reading for the stall watchdog and a dash for the
    /// window, and the two must not be confused.
    /// </summary>
    [Fact]
    public async Task Nitro5Legacy_AGpuTachometerThatAnswersZeroReadsAsADash()
    {
        var (fs, _) = Build(Chassis.Nitro5Legacy);
        var settings = new AppSettings();

        using var openRgb = new OpenRgbClient(port: 1);
        using var controller = LinuxSysfsHardwareController.Create(settings, fs, Catalog(), openRgb);
        using var vm = CreateViewModel(settings, controller, controller.Capabilities, TempSettingsPath(), controller);

        await vm.InitializeAsync();
        await vm.RefreshTelemetryAsync();

        var data = await controller.GetMonitorDataAsync();

        Assert.NotNull(data);
        Assert.True(data!.IsFanRpmMeasured);
        Assert.Equal(0, data.GpuFanSpeed);
        Assert.Equal("-- RPM", vm.GpuFanRpmText);
        Assert.Equal("2100 RPM", vm.CpuFanRpmText);
    }

    /// <summary>
    /// The Predator's Turbo mode is the ACPI <c>performance</c> profile, and its fan ceilings are the higher ones the
    /// model table carries rather than the Nitro line's.
    /// </summary>
    [Fact]
    public async Task PredatorHelios16_ReportsTurboAndTheHigherFanCeilings()
    {
        var (fs, _) = Build(Chassis.PredatorHelios16);
        var settings = new AppSettings();

        using var openRgb = new OpenRgbClient(port: 1);
        using var controller = LinuxSysfsHardwareController.Create(settings, fs, Catalog(), openRgb);

        Assert.Equal(OperatingMode.Turbo, await controller.GetOperatingModeAsync());
        Assert.Equal(6800, controller.Capabilities.Profile.MaxCpuFanRpm);
        Assert.Equal(6600, controller.Capabilities.Profile.MaxGpuFanRpm);

        // The narrower WMI surface: no CoolBoost, no overdrive, no backlight timeout, and one keyboard attribute.
        Assert.False(controller.Capabilities.Hardware.CoolBoost);
        Assert.False(controller.Capabilities.Hardware.LcdOverdrive);
        Assert.False(controller.Capabilities.Hardware.BacklightTimeout);
        Assert.True(controller.Capabilities.Hardware.KeyboardLighting);
    }

    /// <summary>
    /// A machine with no Acer nodes is not an error: the window stays usable, the banner explains it, and every
    /// control that needs hardware stays hidden.
    /// </summary>
    [Fact]
    public async Task UnsupportedVm_KeepsTheWindowUsableAndHidesEveryHardwareControl()
    {
        var (fs, _) = Build(Chassis.UnsupportedVm);
        var settings = new AppSettings();

        using var openRgb = new OpenRgbClient(port: 1);
        using var controller = LinuxSysfsHardwareController.Create(settings, fs, Catalog(), openRgb);
        using var vm = CreateViewModel(settings, controller, controller.Capabilities, TempSettingsPath(), controller);

        await vm.InitializeAsync();
        await vm.RefreshTelemetryAsync();

        Assert.False(controller.IsAvailable);
        Assert.False(vm.IsReady);
        Assert.False(vm.HasBackend);
        Assert.True(vm.ShowNoBackendBanner);
        Assert.False(vm.FanControlAvailable);
        Assert.False(vm.LightingAvailable);
        Assert.False(vm.MuxAvailable);
        Assert.False(vm.BatteryLimitAvailable);
        Assert.Equal("-- °C", vm.CpuTemperatureText);
        Assert.Equal("-- RPM", vm.CpuFanRpmText);
    }

    // ---------------------------------------------------------------- the five trees

    private static (InMemorySysfsFileSystem Fs, ChassisExpectation Expected) Build(Chassis chassis)
    {
        var fs = new InMemorySysfsFileSystem();

        switch (chassis)
        {
            case Chassis.Nitro16An1641:
                Dmi(fs, "Nitro AN16-41", "AN16-41", "Acer", "V1.27");
                PlatformProfile(fs, "balanced", "quiet balanced balanced-performance performance");
                Hwmon(fs, "hwmon0", "acer", 55_000, 2436, 2409);
                Hwmon(fs, "hwmon1", "k10temp", 63_000);
                Hwmon(fs, "hwmon2", "amdgpu", 57_000);
                fs.WithFile("/sys/class/power_supply/BAT1/charge_control_end_threshold", "100\n");
                SenseTree(fs, LinuxPaths.NitroSense);
                fs.WithFile(Sense(LinuxPaths.NitroSense, LinuxPaths.FanSpeed), "60,50\n");
                fs.WithFile(Sense(LinuxPaths.NitroSense, LinuxPaths.BacklightTimeout), "0\n");
                fs.WithFile(Sense(LinuxPaths.NitroSense, LinuxPaths.BatteryLimiter), "0\n");
                fs.WithFile(Sense(LinuxPaths.NitroSense, LinuxPaths.LcdOverride), "0\n");
                fs.WithFile(Sense(LinuxPaths.NitroSense, LinuxPaths.CoolBoost), "0\n");
                fs.WithFile(Sense(LinuxPaths.NitroSense, LinuxPaths.DgpuMode), "0\n");
                fs.WithFile(Keyboard(LinuxPaths.NitroSense, LinuxPaths.PerZoneMode), "00E5FF,00E5FF,00E5FF,00E5FF,55\n");
                fs.WithFile(Keyboard(LinuxPaths.NitroSense, LinuxPaths.FourZoneMode), "3,3,55,1,0,229,255\n");
                return (fs, new ChassisExpectation(
                    ProfileId: "nitro-16-an16-41",
                    Tier: "both",
                    Mode: OperatingMode.Default,
                    Lighting: true,
                    Mux: true,
                    BatteryLimit: true,
                    FanControl: true,
                    CpuTemperatureText: "63 °C",
                    GpuTemperatureText: "57 °C",
                    CpuFanRpmText: "2436 RPM",
                    GpuFanRpmText: "2409 RPM"));

            case Chassis.PredatorHelios16:
                Dmi(fs, "Predator Helios 16", "PHN16-71", "Acer", "V1.15");
                // The firmware is in Turbo, which the kernel spells "performance".
                PlatformProfile(fs, "performance", "quiet balanced balanced-performance performance");
                Hwmon(fs, "hwmon0", "acer", 61_000, 6800, 6600);
                Hwmon(fs, "hwmon1", "k10temp", 78_000);
                Hwmon(fs, "hwmon2", "amdgpu", 69_000);
                fs.WithFile("/sys/class/power_supply/BAT1/charge_control_end_threshold", "100\n");
                // Predator registers predator_sense, and this chassis registers a narrower WMI surface: no CoolBoost,
                // no overdrive and no backlight timeout. One keyboard attribute, so the lighting page has a static
                // path and no effect path.
                SenseTree(fs, LinuxPaths.PredatorSense);
                fs.WithFile(Sense(LinuxPaths.PredatorSense, LinuxPaths.FanSpeed), "80,80\n");
                fs.WithFile(Sense(LinuxPaths.PredatorSense, LinuxPaths.BatteryLimiter), "0\n");
                fs.WithFile(Sense(LinuxPaths.PredatorSense, LinuxPaths.DgpuMode), "1\n");
                fs.WithFile(Keyboard(LinuxPaths.PredatorSense, LinuxPaths.PerZoneMode), "FF0000,FF0000,FF0000,FF0000,100\n");
                return (fs, new ChassisExpectation(
                    ProfileId: "predator-helios-16-phn16-71",
                    Tier: "both",
                    Mode: OperatingMode.Turbo,
                    Lighting: true,
                    Mux: true,
                    BatteryLimit: true,
                    FanControl: true,
                    CpuTemperatureText: "78 °C",
                    GpuTemperatureText: "69 °C",
                    CpuFanRpmText: "6800 RPM",
                    GpuFanRpmText: "6600 RPM"));

            case Chassis.Nitro5Legacy:
                Dmi(fs, "Nitro 5", "AN515-58", "Acer", "V1.10");
                PlatformProfile(fs, "balanced", "quiet balanced performance");
                // The GPU tachometer exists and answers zero: nothing is turning on that fan.
                Hwmon(fs, "hwmon0", "acer", 52_000, 2100, 0);
                Hwmon(fs, "hwmon1", "k10temp", 58_000);
                fs.WithFile("/sys/class/power_supply/BAT1/charge_control_end_threshold", "100\n");
                SenseTree(fs, LinuxPaths.NitroSense);
                fs.WithFile(Sense(LinuxPaths.NitroSense, LinuxPaths.FanSpeed), "40,40\n");
                fs.WithFile(Sense(LinuxPaths.NitroSense, LinuxPaths.BatteryLimiter), "0\n");
                // Single-zone keyboard: the module exposes the one-colour attribute and no per-zone one.
                fs.WithFile(Keyboard(LinuxPaths.NitroSense, LinuxPaths.FourZoneMode), "0,3,55,1,255,0,0\n");
                return (fs, new ChassisExpectation(
                    ProfileId: "nitro-5-an515",
                    Tier: "both",
                    Mode: OperatingMode.Default,
                    Lighting: true,
                    Mux: false,
                    BatteryLimit: true,
                    FanControl: true,
                    CpuTemperatureText: "58 °C",
                    GpuTemperatureText: "-- °C",
                    CpuFanRpmText: "2100 RPM",
                    GpuFanRpmText: "-- RPM"));

            case Chassis.NitroV15Restricted:
                Dmi(fs, "Nitro V 15", "ANV15-52", "Acer", "V1.10");
                PlatformProfile(fs, "balanced", "quiet balanced performance");
                Hwmon(fs, "hwmon0", "acer", 50_000, 1800);
                Hwmon(fs, "hwmon1", "k10temp", 55_000);
                // No charge_control_end_threshold: this chassis does not expose the battery limiter at all.
                SenseTree(fs, LinuxPaths.NitroSense);
                fs.WithFile(Sense(LinuxPaths.NitroSense, LinuxPaths.FanSpeed), "50,50\n");
                // The firmware accepts the write and keeps the old value, which is the custom-curve failure here.
                fs.OnWrite = (path, _) => path != Sense(LinuxPaths.NitroSense, LinuxPaths.FanSpeed);
                return (fs, new ChassisExpectation(
                    ProfileId: "nitro-v-15-anv15-52",
                    Tier: "both",
                    Mode: OperatingMode.Default,
                    Lighting: false,
                    Mux: false,
                    BatteryLimit: false,
                    FanControl: true,
                    CpuTemperatureText: "55 °C",
                    GpuTemperatureText: "-- °C",
                    CpuFanRpmText: "1800 RPM",
                    GpuFanRpmText: "-- RPM"));

            case Chassis.UnsupportedVm:
                Dmi(fs, "VMware Virtual Platform", "440BX Desktop Reference Platform", "VMware, Inc.", "6.00");
                fs.WithFile("/proc/cpuinfo", "vendor_id\t: GenuineIntel\nmodel name\t: Intel(R) Xeon(R) CPU\n");
                return (fs, new ChassisExpectation(
                    ProfileId: "unknown",
                    Tier: "none",
                    Mode: null,
                    Lighting: false,
                    Mux: false,
                    BatteryLimit: false,
                    FanControl: false,
                    CpuTemperatureText: "-- °C",
                    GpuTemperatureText: "-- °C",
                    CpuFanRpmText: "-- RPM",
                    GpuFanRpmText: "-- RPM"));

            default:
                throw new ArgumentOutOfRangeException(nameof(chassis), chassis, "No tree was defined for this chassis.");
        }
    }

    private static void Dmi(InMemorySysfsFileSystem fs, string product, string board, string vendor, string bios)
    {
        fs.WithFile("/sys/class/dmi/id/product_name", product + "\n");
        fs.WithFile("/sys/class/dmi/id/board_name", board + "\n");
        fs.WithFile("/sys/class/dmi/id/sys_vendor", vendor + "\n");
        fs.WithFile("/sys/class/dmi/id/bios_version", bios + "\n");
        fs.WithFile("/sys/class/dmi/id/bios_vendor", "Insyde\n");
    }

    private static void PlatformProfile(InMemorySysfsFileSystem fs, string current, string choices)
    {
        fs.WithFile(LinuxPaths.PlatformProfile, current + "\n");
        fs.WithFile(LinuxPaths.PlatformProfileChoices, choices + "\n");
    }

    /// <summary>One hwmon device: its kernel name, one temperature in millidegrees and its tachometers in order.</summary>
    private static void Hwmon(
        InMemorySysfsFileSystem fs, string device, string name, int? temperatureMilliC, params int[] fans)
    {
        var root = LinuxPaths.Combine(LinuxPaths.HwmonRoot, device);
        fs.WithFile(LinuxPaths.Combine(root, "name"), name + "\n");

        if (temperatureMilliC is int milli)
        {
            fs.WithFile(
                LinuxPaths.Combine(root, "temp1_input"),
                milli.ToString(CultureInfo.InvariantCulture) + "\n");
        }

        for (int i = 0; i < fans.Length; i++)
        {
            fs.WithFile(
                LinuxPaths.Combine(root, "fan" + (i + 1).ToString(CultureInfo.InvariantCulture) + "_input"),
                fans[i].ToString(CultureInfo.InvariantCulture) + "\n");
        }
    }

    private static void SenseTree(InMemorySysfsFileSystem fs, string module)
    {
        fs.WithDirectory(LinuxPaths.AcpiPlatformRoot);
        fs.WithDirectory(SenseRoot(module));
        fs.WithDirectory(KeyboardRoot(module));
    }

    private static string SenseRoot(string module) =>
        LinuxPaths.Combine(LinuxPaths.LinuwuModuleRoot, module);

    private static string Sense(string module, string name) =>
        LinuxPaths.Combine(SenseRoot(module), name);

    private static string KeyboardRoot(string module) =>
        LinuxPaths.Combine(SenseRoot(module), LinuxPaths.FourZoneKeyboard);

    private static string Keyboard(string module, string name) =>
        LinuxPaths.Combine(KeyboardRoot(module), name);

    /// <summary>The embedded model table only, so a developer's own profiles.user.json cannot change a result.</summary>
    private static ProfileCatalog Catalog() =>
        ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _);

    private static string TempSettingsPath() =>
        Path.Combine(Path.GetTempPath(), "nitrolite-matrix-" + Guid.NewGuid().ToString("N") + ".json");

    /// <summary>
    /// A view model wired to every service the pages own, so a command that reaches one gets an answer instead of a
    /// null reference. The services that would touch the real machine — the game scanner, the memory cleaner, the
    /// monitor — are left out on purpose: they are not what this file is testing, and their absence is a path the
    /// window already has to survive.
    /// </summary>
    private static MainViewModel CreateViewModel(
        AppSettings settings,
        IHardwareController hardware,
        IPlatformSnapshot? platform,
        string settingsPath,
        IRescannableBackend? rescan)
    {
        var urls = new Phase3Fakes.UrlCapturer();

        return new MainViewModel(
            settings,
            hardware,
            platform,
            settingsPath,
            rescan: rescan,
            powerState: () => (true, 80),
            dialogs: new Phase3Fakes.FakeDialogs(),
            openRgb: hardware as IOpenRgbStatusProvider,
            batteryHealth: new Phase3Fakes.FakeBattery(),
            storageHealth: new Phase3Fakes.FakeStorage(),
            debloat: new Phase3Fakes.FakeDebloat(),
            systemTools: new Phase3Fakes.FakeSystemTools(),
            updates: new Phase3Fakes.FakeUpdates(),
            diagnostics: new Phase3Fakes.FakeDiagnostics(),
            runAtStartup: new Phase3Fakes.FakeRunAtStartup(),
            urlOpener: urls.Capture);
    }

    /// <summary>Every tab, then every command the window exposes.</summary>
    private static async Task TriggerEveryTabAndCommandAsync(MainViewModel vm)
    {
        foreach (var tab in vm.NavTabs.ToArray())
        {
            vm.SelectTab(tab.Key);
            Assert.Equal(tab.Key, vm.SelectedTab?.Key);
        }

        foreach (var command in Commands(vm))
        {
            if (!command.CanExecute(null)) continue;
            command.Execute(null);
            await WaitForCommandAsync(command);
        }
    }

    /// <summary>
    /// Every public <see cref="ICommand"/> on the view model, found by reflection so a command added later is covered
    /// without anyone remembering to add it here.
    /// </summary>
    private static IEnumerable<ICommand> Commands(MainViewModel vm) =>
        typeof(MainViewModel)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => typeof(ICommand).IsAssignableFrom(p.PropertyType))
            .Select(p => p.GetValue(vm))
            .OfType<ICommand>();

    /// <summary>
    /// Waits for a fire-and-forget command to finish.
    /// </summary>
    /// <remarks>
    /// <see cref="AsyncCommand.Execute"/> is <c>async void</c> by design — a button click cannot await — and it reports
    /// itself busy through <see cref="ICommand.CanExecute"/>. Polling that is the only way to know the work is done,
    /// and it fails with a timeout rather than passing on a race.
    /// </remarks>
    private static async Task WaitForCommandAsync(ICommand command)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!command.CanExecute(null))
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("A command did not finish within fifteen seconds.");
            }

            await Task.Delay(5);
        }
    }

    // ================================================================================================================
    // 2. A fake /sys on a real disk, and the permission failure
    // ================================================================================================================

    /// <summary>
    /// A real directory laid out like <c>/sys</c>, read through <see cref="RealSysfsFileSystem"/>.
    /// </summary>
    /// <remarks>
    /// The in-memory tree is the right seam for the matrix, but it cannot prove that the production reader trims a
    /// real newline, that a real read-only attribute maps to <see cref="SysfsWriteStatus.PermissionDenied"/>, or that
    /// a directory that is not there enumerates as empty rather than throwing. This adapter keeps the absolute sysfs
    /// paths the backend uses and prefixes them with a temporary root, so the same code runs against real files.
    /// </remarks>
    private sealed class TempRootSysfsFileSystem : ISysfsFileSystem, IDisposable
    {
        private readonly string _root;
        private readonly RealSysfsFileSystem _inner = RealSysfsFileSystem.Instance;

        public TempRootSysfsFileSystem(string root) => _root = root;

        /// <summary>
        /// The sysfs path as a path on this disk.
        /// </summary>
        /// <remarks>
        /// A colon is legal in a sysfs name and illegal in a Windows one, and the Linuwu-Sense root is
        /// <c>platform:acer-wmi</c>. It is folded to an underscore here; nothing enumerates that directory, so the
        /// substitution never has to be undone.
        /// </remarks>
        public string Map(string path) =>
            Path.Combine(
                _root,
                path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar).Replace(':', '_'));

        public bool FileExists(string path) => _inner.FileExists(Map(path));

        public bool DirectoryExists(string path) => _inner.DirectoryExists(Map(path));

        public string? ReadAllText(string path) => _inner.ReadAllText(Map(path));

        public SysfsWriteResult WriteAllText(string path, string value) => _inner.WriteAllText(Map(path), value);

        public bool CanWrite(string path) => _inner.CanWrite(Map(path));

        /// <summary>
        /// The real reader answers with full paths; the backend expects sysfs paths, so the temporary root is stripped
        /// back off. Returning the disk path here would make <c>LinuxPaths.Combine</c> prefix it a second time.
        /// </summary>
        public IReadOnlyList<string> EnumerateDirectories(string path)
        {
            var prefix = path.TrimEnd('/');
            return _inner.EnumerateDirectories(Map(path))
                .Select(d => prefix + "/" + Path.GetFileName(d))
                .ToArray();
        }

        public IReadOnlyList<string> EnumerateFiles(string path) => _inner.EnumerateFiles(Map(path));

        public void Dispose()
        {
            try
            {
                // A read-only attribute left behind by the permission test would make the delete fail.
                foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                }

                Directory.Delete(_root, recursive: true);
            }
            catch
            {
                // A temporary folder that outlives the test is the sweep's problem, not the test's.
            }
        }
    }

    private static string CreateTempSysRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "nitrolite-sysfs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void WriteNode(string root, string sysfsPath, string content)
    {
        var full = Path.Combine(
            root,
            sysfsPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar).Replace(':', '_'));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    /// <summary>Overwrites one node of an existing on-disk tree, for the corrupt-text and missing-node cases.</summary>
    private static void OverwriteNode(TempRootSysfsFileSystem fs, string sysfsPath, string content) =>
        File.WriteAllText(fs.Map(sysfsPath), content);

    /// <summary>
    /// A Nitro 16 on disk: <c>acer</c>, <c>k10temp</c>, <c>coretemp</c> and <c>amdgpu</c> under hwmon, the four ACPI
    /// profiles, the battery class and the Linuwu-Sense tree.
    /// </summary>
    private static TempRootSysfsFileSystem CreateOnDiskNitro16()
    {
        var root = CreateTempSysRoot();

        WriteNode(root, "/sys/class/dmi/id/product_name", "Nitro AN16-41\n");
        WriteNode(root, "/sys/class/dmi/id/board_name", "AN16-41\n");
        WriteNode(root, "/sys/class/dmi/id/sys_vendor", "Acer\n");
        WriteNode(root, "/sys/class/dmi/id/bios_version", "V1.27\n");
        WriteNode(root, "/sys/class/dmi/id/bios_vendor", "Insyde\n");

        WriteNode(root, LinuxPaths.PlatformProfile, "balanced\n");
        WriteNode(root, LinuxPaths.PlatformProfileChoices, "quiet balanced balanced-performance performance\n");

        WriteNode(root, "/sys/class/hwmon/hwmon0/name", "acer\n");
        WriteNode(root, "/sys/class/hwmon/hwmon0/temp1_input", "55000\n");
        WriteNode(root, "/sys/class/hwmon/hwmon0/fan1_input", "2436\n");
        WriteNode(root, "/sys/class/hwmon/hwmon0/fan2_input", "2409\n");
        WriteNode(root, "/sys/class/hwmon/hwmon1/name", "k10temp\n");
        WriteNode(root, "/sys/class/hwmon/hwmon1/temp1_input", "63000\n");
        WriteNode(root, "/sys/class/hwmon/hwmon2/name", "coretemp\n");
        WriteNode(root, "/sys/class/hwmon/hwmon2/temp1_input", "61000\n");
        WriteNode(root, "/sys/class/hwmon/hwmon3/name", "amdgpu\n");
        WriteNode(root, "/sys/class/hwmon/hwmon3/temp1_input", "57000\n");

        WriteNode(root, "/sys/class/power_supply/BAT1/charge_control_end_threshold", "100\n");

        WriteNode(root, Sense(LinuxPaths.NitroSense, LinuxPaths.FanSpeed), "60,50\n");
        WriteNode(root, Sense(LinuxPaths.NitroSense, LinuxPaths.BatteryLimiter), "0\n");
        WriteNode(root, Sense(LinuxPaths.NitroSense, LinuxPaths.DgpuMode), "0\n");
        WriteNode(root, Keyboard(LinuxPaths.NitroSense, LinuxPaths.PerZoneMode), "00E5FF,00E5FF,00E5FF,00E5FF,55\n");

        return new TempRootSysfsFileSystem(root);
    }

    [Fact]
    public async Task FakeSysfs_OnDisk_ReadsTheHwmonTreeAndWritesThePlatformProfile()
    {
        using var fs = CreateOnDiskNitro16();
        var settings = new AppSettings();

        using var openRgb = new OpenRgbClient(port: 1);
        using var controller = LinuxSysfsHardwareController.Create(settings, fs, Catalog(), openRgb);

        Assert.Equal("both", controller.Capabilities.Tier);
        Assert.Equal("nitro-16-an16-41", controller.Capabilities.Profile.Id);

        var data = await controller.GetMonitorDataAsync();

        Assert.NotNull(data);
        // k10temp wins over coretemp: the die sensor is the CPU reading, the Intel one is only a fallback.
        Assert.Equal(63, data!.CpuTemperature);
        Assert.Equal(57, data.GpuTemperature);
        Assert.Equal(55, data.SysTemperature);
        Assert.Equal(2436, data.CpuFanSpeed);
        Assert.Equal(2409, data.GpuFanSpeed);
        Assert.True(data.IsFanRpmMeasured);

        Assert.Equal(OperatingMode.Default, await controller.GetOperatingModeAsync());
        Assert.True(await controller.SetOperatingModeAsync(OperatingMode.Turbo));

        // The write landed in a real file, and the production reader trimmed the newline it added.
        Assert.Equal("performance", fs.ReadAllText(LinuxPaths.PlatformProfile));
        Assert.Equal(OperatingMode.Turbo, await controller.GetOperatingModeAsync());
    }

    [Fact]
    public async Task FakeSysfs_MissingNode_ReportsNoReadingInsteadOfZero()
    {
        using var fs = CreateOnDiskNitro16();

        // The GPU sensor and the second tachometer are gone, and so is the keyboard: a module that does not expose
        // them is the normal state on a stock distribution.
        File.Delete(fs.Map("/sys/class/hwmon/hwmon3/temp1_input"));
        File.Delete(fs.Map("/sys/class/hwmon/hwmon0/fan2_input"));
        File.Delete(fs.Map(Keyboard(LinuxPaths.NitroSense, LinuxPaths.PerZoneMode)));

        var settings = new AppSettings();
        using var openRgb = new OpenRgbClient(port: 1);
        using var controller = LinuxSysfsHardwareController.Create(settings, fs, Catalog(), openRgb);

        var data = await controller.GetMonitorDataAsync();

        Assert.NotNull(data);
        Assert.Equal(0, data!.GpuTemperature);
        Assert.Equal(0, data.GpuFanSpeed);
        Assert.Equal(2436, data.CpuFanSpeed);
        Assert.Null(await controller.GetLightingConfigAsync());
        Assert.False(await controller.SetLightingAsync("STATIC", 5, 3, 1, "#FF0000", null));
    }

    [Fact]
    public async Task FakeSysfs_CorruptSensorText_IsRejectedRatherThanParsed()
    {
        using var fs = CreateOnDiskNitro16();

        // A busy embedded controller answers a sentinel, a half-written module answers nothing, and a firmware that
        // does not know the attribute answers words. None of the three is a reading.
        OverwriteNode(fs, "/sys/class/hwmon/hwmon0/temp1_input", "not-a-number\n");
        OverwriteNode(fs, "/sys/class/hwmon/hwmon0/fan1_input", "65535\n");
        OverwriteNode(fs, "/sys/class/hwmon/hwmon0/fan2_input", "65535\n");
        OverwriteNode(fs, "/sys/class/hwmon/hwmon1/temp1_input", "\n");
        OverwriteNode(fs, "/sys/class/hwmon/hwmon3/temp1_input", "0xDEAD\n");
        OverwriteNode(fs, LinuxPaths.PlatformProfile, "garbage\n");
        OverwriteNode(fs, Sense(LinuxPaths.NitroSense, LinuxPaths.FanSpeed), "cpu,gpu\n");

        var settings = new AppSettings();
        using var openRgb = new OpenRgbClient(port: 1);
        using var controller = LinuxSysfsHardwareController.Create(settings, fs, Catalog(), openRgb);

        var data = await controller.GetMonitorDataAsync();

        Assert.NotNull(data);
        // k10temp answered nothing, so the CPU reading falls back to coretemp rather than to the board sensor.
        Assert.Equal(61, data!.CpuTemperature);
        Assert.Equal(0, data.GpuTemperature);
        Assert.Equal(0, data.CpuFanSpeed);
        Assert.Equal(0, data.GpuFanSpeed);
        Assert.False(data.IsFanRpmMeasured);

        Assert.Null(await controller.GetOperatingModeAsync());
        Assert.Null(await controller.GetFanModeAsync());

        // The corrupt line reads as "no answer" rather than as zero, and a write replaces it with a value the module
        // can parse — which is the repair path, not a silent success over the garbage.
        Assert.True(await controller.SetFanModeAsync(FanMode.Custom, 60, 60));
        Assert.Equal("60,60", fs.ReadAllText(Sense(LinuxPaths.NitroSense, LinuxPaths.FanSpeed)));
        Assert.Equal(FanMode.Custom, await controller.GetFanModeAsync());

        // With the Intel sensor gone too there is no die reading left, and the board sensor is not a substitute for
        // one. A re-probe is what sees that: the sample comes from the tree the probe read, so a node that changes
        // afterwards is not re-read on every tick.
        OverwriteNode(fs, "/sys/class/hwmon/hwmon2/temp1_input", "not-a-number\n");
        using var openRgbAgain = new OpenRgbClient(port: 1);
        using var reprobed = LinuxSysfsHardwareController.Create(new AppSettings(), fs, Catalog(), openRgbAgain);
        var withoutDie = await reprobed.GetMonitorDataAsync();

        Assert.NotNull(withoutDie);
        Assert.Equal(0, withoutDie!.CpuTemperature);
    }

    [Fact]
    public async Task FakeSysfs_ReadOnlyPlatformProfile_ReportsTheUdevHintAndLeavesTheValueAlone()
    {
        using var fs = CreateOnDiskNitro16();

        var node = fs.Map(LinuxPaths.PlatformProfile);
        File.SetAttributes(node, FileAttributes.ReadOnly);

        var settings = new AppSettings();
        using var openRgb = new OpenRgbClient(port: 1);
        using var controller = LinuxSysfsHardwareController.Create(settings, fs, Catalog(), openRgb);

        // The probe sees the permission before the user presses anything, which is what the settings page needs.
        Assert.True(controller.Capabilities.NeedsUdevRule);
        Assert.False(fs.CanWrite(LinuxPaths.PlatformProfile));

        Assert.False(await controller.SetOperatingModeAsync(OperatingMode.Quiet));
        Assert.True(controller.PermissionHintRaised);
        Assert.NotNull(controller.PermissionHint);
        Assert.Contains("udev", controller.PermissionHint, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("99-nitrolite.rules", controller.PermissionHint);

        // A refused write is not a write: the firmware is still in the profile it was in.
        Assert.Equal("balanced", fs.ReadAllText(LinuxPaths.PlatformProfile));
    }

    // ================================================================================================================
    // 3. Chaos, boundary values and the anonymous report
    // ================================================================================================================

    /// <summary>
    /// A backend that answers nonsense on purpose: a refused write, a timeout, a sensor that reports 0 °C, 127 °C or
    /// NaN, and a read that returns nothing at all.
    /// </summary>
    /// <remarks>
    /// The seed is fixed so a failure is reproducible. The counters are what the test asserts on: a chaos pass that
    /// never produced a timeout would be a pass that proved nothing.
    /// </remarks>
    private sealed class ChaosHardwareDecorator : IHardwareController
    {
        private readonly IHardwareController _inner;
        private readonly Random _random;

        public ChaosHardwareDecorator(IHardwareController inner, int seed)
        {
            _inner = inner;
            _random = new Random(seed);
        }

        public int Timeouts { get; private set; }

        public int Refusals { get; private set; }

        public int ImpossibleReadings { get; private set; }

        public string BackendName => _inner.BackendName;

        public bool IsAvailable => _inner.IsAvailable;

        public Task<bool> EnsureConnectedAsync() => _inner.EnsureConnectedAsync();

        public Task<HardwareCapabilities> GetCapabilitiesAsync() => _inner.GetCapabilitiesAsync();

        public Task<MonitorData?> GetMonitorDataAsync()
        {
            switch (_random.Next(6))
            {
                case 0:
                    Timeouts++;
                    throw new TimeoutException("The embedded controller did not answer.");
                case 1:
                    // A read that failed is not a machine with nothing to show.
                    return Task.FromResult<MonitorData?>(null);
                case 2:
                    ImpossibleReadings++;
                    return Task.FromResult<MonitorData?>(new MonitorData
                    {
                        CpuTemperature = 0,
                        GpuTemperature = 127,
                        SysTemperature = 0,
                        CpuFanSpeed = 0,
                        GpuFanSpeed = 0,
                        IsFanRpmMeasured = true,
                        CpuUsage = double.NaN,
                        GpuUsage = double.PositiveInfinity,
                        RamUsage = double.NaN,
                        RamTotalMB = 0,
                        CpuFrequencyMHz = double.NaN
                    });
                default:
                    return _inner.GetMonitorDataAsync();
            }
        }

        public Task<OperatingMode?> GetOperatingModeAsync() => _inner.GetOperatingModeAsync();

        public Task<bool> SetOperatingModeAsync(OperatingMode mode) => RefuseOrRun(() => _inner.SetOperatingModeAsync(mode));

        public Task<GpuOperatingMode?> GetGpuModeAsync() => _inner.GetGpuModeAsync();

        public Task<bool> SetGpuModeAsync(GpuOperatingMode mode) => RefuseOrRun(() => _inner.SetGpuModeAsync(mode));

        public Task<int?> GetGpuModeCapabilityAsync() => _inner.GetGpuModeCapabilityAsync();

        public Task<FanMode?> GetFanModeAsync() => _inner.GetFanModeAsync();

        public Task<bool> SetFanModeAsync(FanMode mode, int cpuPercent = -1, int gpuPercent = -1) =>
            RefuseOrRun(() => _inner.SetFanModeAsync(mode, cpuPercent, gpuPercent));

        public Task<LightingConfig?> GetLightingConfigAsync() => _inner.GetLightingConfigAsync();

        public Task<bool> SetLightingAsync(
            string effect, int brightness, int speed, int direction, string primaryColor, string[]? zoneColors) =>
            RefuseOrRun(() => _inner.SetLightingAsync(effect, brightness, speed, direction, primaryColor, zoneColors));

        public Task<bool> TurnOffLightingAsync() => RefuseOrRun(_inner.TurnOffLightingAsync);

        public Task<bool?> GetBacklightTimeoutAsync() => _inner.GetBacklightTimeoutAsync();

        public Task<bool> SetBacklightTimeoutAsync(bool enable30s) =>
            RefuseOrRun(() => _inner.SetBacklightTimeoutAsync(enable30s));

        public Task<BatteryChargeMode?> GetBatteryChargeModeAsync() => _inner.GetBatteryChargeModeAsync();

        public Task<bool> SetBatteryChargeModeAsync(BatteryChargeMode mode) =>
            RefuseOrRun(() => _inner.SetBatteryChargeModeAsync(mode));

        public Task<bool?> GetCoolBoostAsync() => _inner.GetCoolBoostAsync();

        public Task<bool> SetCoolBoostAsync(bool on) => RefuseOrRun(() => _inner.SetCoolBoostAsync(on));

        public Task<bool?> GetLcdOverdriveAsync() => _inner.GetLcdOverdriveAsync();

        public Task<bool> SetLcdOverdriveAsync(bool on) => RefuseOrRun(() => _inner.SetLcdOverdriveAsync(on));

        public Task<bool> SetWinKeyLockAsync(bool enabled) => RefuseOrRun(() => _inner.SetWinKeyLockAsync(enabled));

        /// <summary>One write, refused outright or timed out, and otherwise handed to the real backend.</summary>
        private Task<bool> RefuseOrRun(Func<Task<bool>> write)
        {
            switch (_random.Next(4))
            {
                case 0:
                    Refusals++;
                    return Task.FromResult(false);
                case 1:
                    Timeouts++;
                    throw new TimeoutException("The firmware did not acknowledge the write.");
                default:
                    return write();
            }
        }

        public void Dispose()
        {
            // The inner controller is owned by the test that built it.
        }
    }

    [Fact]
    public async Task Chaos_OneHundredTelemetryAndWriteCycles_NeverEscapeAnException()
    {
        var (fs, _) = Build(Chassis.Nitro16An1641);
        var settings = new AppSettings();

        using var openRgb = new OpenRgbClient(port: 1);
        using var controller = LinuxSysfsHardwareController.Create(settings, fs, Catalog(), openRgb);
        using var chaos = new ChaosHardwareDecorator(controller, seed: 20261118);
        using var vm = CreateViewModel(settings, chaos, controller.Capabilities, TempSettingsPath(), rescan: null);

        await vm.InitializeAsync();

        for (int i = 0; i < 100; i++)
        {
            await vm.RefreshTelemetryAsync();

            // A reading that is not a number is a dash. "NaN °C" on the dashboard is the failure this guards.
            Assert.DoesNotContain("NaN", vm.CpuTemperatureText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("NaN", vm.GpuTemperatureText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("NaN", vm.CpuUsageText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("NaN", vm.GpuUsageText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("NaN", vm.CpuFrequencyText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("∞", vm.CpuUsageText, StringComparison.Ordinal);

            await RunCommandAsync(vm.ApplyFanModeCommand);
            await RunCommandAsync(vm.ApplyLightingCommand);
            await RunCommandAsync(vm.TurnOffLightingCommand);
            await RunCommandAsync(vm.ApplyGpuModeCommand);
            await RunCommandAsync(vm.ApplyCurveCommand);
        }

        // A pass that never hit the failures would be a pass that proved nothing.
        Assert.True(chaos.Timeouts > 0, "the chaos pass never produced a timeout");
        Assert.True(chaos.Refusals > 0, "the chaos pass never produced a refused write");
        Assert.True(chaos.ImpossibleReadings > 0, "the chaos pass never produced an impossible reading");

        // The window is still usable after a hundred cycles of nonsense.
        await vm.RefreshTelemetryAsync();
        Assert.NotNull(vm.Status);
    }

    private static async Task RunCommandAsync(ICommand command)
    {
        if (!command.CanExecute(null)) return;
        command.Execute(null);
        await WaitForCommandAsync(command);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-100.0)]
    [InlineData(0.0)]
    [InlineData(500.0)]
    public void FanCurve_ClampsEveryImpossibleTemperatureIntoTheSafeRange(double temperatureC)
    {
        var curve = new FanCurve(FanCurve.DefaultPoints());

        int percent = curve.PercentAt(temperatureC);

        Assert.InRange(percent, FanCurve.MinPercent, FanCurve.MaxPercent);

        // A reading that is not a number is not a cool machine: the safe answer is full speed, not the quietest
        // anchor. This used to return the lowest point, which turned a broken sensor into a silent fan.
        if (double.IsNaN(temperatureC)) Assert.Equal(FanCurve.MaxPercent, percent);
    }

    [Theory]
    [InlineData(-100, FanCurve.MinPercent)]
    [InlineData(0, FanCurve.MinPercent)]
    [InlineData(500, FanCurve.MaxPercent)]
    [InlineData(30, 30)]
    [InlineData(100, 100)]
    public void FanCurveSanitize_ClampsEveryAnchorPercentIntoTheRangeTheFirmwareAccepts(int percent, int expected)
    {
        var points = FanCurve.Sanitize(new[] { new FanCurvePoint(60, percent) });

        Assert.Single(points);
        Assert.Equal(expected, points[0].Percent);
    }

    [Theory]
    [InlineData(-100, FanCurve.MinTemperatureC)]
    [InlineData(0, FanCurve.MinTemperatureC)]
    [InlineData(500, FanCurve.MaxTemperatureC)]
    public void FanCurveSanitize_ClampsEveryAnchorTemperatureIntoTheRangeTheFirmwareAccepts(
        int temperatureC, int expected)
    {
        var points = FanCurve.Sanitize(new[] { new FanCurvePoint(temperatureC, 50) });

        Assert.Single(points);
        Assert.Equal(expected, points[0].TemperatureC);
    }

    [Fact]
    public void FanCurveSanitize_AnUnusableListFallsBackToTheDefaultShape()
    {
        // A curve with no anchor would leave the feature on with nothing to follow.
        Assert.Equal(FanCurve.DefaultPoints().Count, FanCurve.Sanitize(null).Count);
        Assert.Equal(FanCurve.DefaultPoints().Count, FanCurve.Sanitize(Array.Empty<FanCurvePoint>()).Count);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-100.0)]
    [InlineData(0.0)]
    public void ManualFanFloor_AnImpossibleReadingNeverChangesTheFanSpeed(double temperatureC)
    {
        var floor = new ManualFanFloor();

        Assert.Null(floor.Evaluate(50, temperatureC));
        Assert.Null(floor.AppliedPercent);
    }

    [Theory]
    [InlineData(-100)]
    [InlineData(0)]
    [InlineData(500)]
    public void ManualFanFloor_AndTheBackendClamp_KeepAnExtremeTargetInsideTheRangeTheFirmwareAccepts(int target)
    {
        var floor = new ManualFanFloor();

        foreach (var temperature in new[] { 40.0, 85.0, 95.0, 127.0 })
        {
            int effective = floor.EffectivePercent(target, temperature);
            int written = LinuxSysfsHardwareController.NormalisePercent(effective);

            // Whatever the user's number, what reaches the firmware is inside the range and at least the floor.
            Assert.InRange(written, FanCurve.MinPercent, FanCurve.MaxPercent);
            Assert.True(written >= FanCurve.SafetyFloorAt(temperature));
        }
    }

    [Fact]
    public void AppSettingsNormalize_ClampsEveryOutOfRangeNumberIntoTheRangeTheAppCanActOn()
    {
        var settings = new AppSettings
        {
            CustomFanCpuPercent = 500,
            CustomFanGpuPercent = -100,
            FanCurveHysteresisC = 500,
            FanCurveMinDwellSeconds = -100,
            OsdCorner = -7,
            OsdMonitor = -3,
            DefaultAtkMouseProfile = 99,
            FanCurvePoints = new List<FanCurvePoint>
            {
                new(-100, -100),
                new(500, 500)
            }
        };

        settings.Normalize();

        Assert.Equal(100, settings.CustomFanCpuPercent);
        Assert.Equal(AppSettings.MinCustomFanPercent, settings.CustomFanGpuPercent);
        Assert.InRange(settings.FanCurveHysteresisC, 0, 15);
        Assert.InRange(settings.FanCurveMinDwellSeconds, 0, 120);
        Assert.InRange(settings.OsdCorner, 0, 3);
        Assert.True(settings.OsdMonitor >= 0);
        Assert.InRange(settings.DefaultAtkMouseProfile, 0, 4);

        Assert.NotNull(settings.FanCurvePoints);
        Assert.All(settings.FanCurvePoints!, point =>
        {
            Assert.InRange(point.TemperatureC, FanCurve.MinTemperatureC, FanCurve.MaxTemperatureC);
            Assert.InRange(point.Percent, FanCurve.MinPercent, FanCurve.MaxPercent);
        });
    }

    [Theory]
    [InlineData(-100.0, AppSettings.MinSidebarWidth)]
    [InlineData(0.0, AppSettings.MinSidebarWidth)]
    [InlineData(500.0, AppSettings.MaxSidebarWidth)]
    [InlineData(double.NaN, AppSettings.DefaultSidebarWidth)]
    [InlineData(double.PositiveInfinity, AppSettings.DefaultSidebarWidth)]
    public void SidebarWidth_IsClampedIntoTheRangeTheRailCanDraw(double width, double expected)
    {
        Assert.Equal(expected, AppSettings.ClampSidebarWidth(width));
    }

    [Fact]
    public void HardwareReport_RedactsTheHomeDirectoryAndEveryUnitIdentifier()
    {
        var capabilities = new LinuxCapabilities
        {
            Identity = new DeviceIdentity
            {
                Manufacturer = "Acer",
                Model = "Nitro AN16-41",
                ModelCode = "AN16-41",
                BiosVersion = "V1.27",
                IsAcer = true
            },
            Notes = new[]
            {
                "settings: /home/caner/.local/share/NitroLite/settings.json",
                "serial: 1234567890",
                "uuid: 0000-1111-2222",
                "hostname: caners-laptop"
            }
        };

        var report = capabilities.BuildHardwareReport();

        // The report is what a user pastes into a public issue, so a path or a unit identifier in it is a leak.
        Assert.DoesNotContain("caner", report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("1234567890", report, StringComparison.Ordinal);
        Assert.DoesNotContain("0000-1111-2222", report, StringComparison.Ordinal);
        Assert.Contains("<redacted>", report, StringComparison.Ordinal);

        // The model-level facts are the point of the report and have to survive the redaction.
        Assert.Contains("AN16-41", report, StringComparison.Ordinal);
        Assert.Contains("V1.27", report, StringComparison.Ordinal);
    }

    [Fact]
    public void HardwareReport_FromARealProbe_CarriesNoUserNameAndNoMachineName()
    {
        var (fs, _) = Build(Chassis.Nitro16An1641);
        var settings = new AppSettings();

        using var openRgb = new OpenRgbClient(port: 1);
        using var controller = LinuxSysfsHardwareController.Create(settings, fs, Catalog(), openRgb);

        var report = controller.Capabilities.BuildHardwareReport();

        Assert.DoesNotContain(Environment.UserName, report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.MachineName, report, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AN16-41", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Redaction_ScrubsTheWindowsProfileAndTheMachineName()
    {
        var machine = Environment.MachineName;
        var text = $@"C:\Users\caner\AppData\Local\NitroLite\settings.json on {machine}";

        var scrubbed = Redaction.Scrub(text);

        Assert.DoesNotContain("caner", scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Redaction.UserPlaceholder, scrubbed, StringComparison.Ordinal);

        // A machine name shorter than three characters is left alone on purpose: replacing it would mangle the line.
        if (machine.Length >= 3)
        {
            Assert.DoesNotContain(machine, scrubbed, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(Redaction.MachinePlaceholder, scrubbed, StringComparison.Ordinal);
        }
    }
}
