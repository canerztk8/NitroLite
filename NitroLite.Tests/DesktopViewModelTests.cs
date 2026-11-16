using System.IO;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Core.Hardware.OpenRgb;
using NitroLite.Desktop.ViewModels;
using NitroLite.Linux;
using NitroLite.Linux.Sysfs;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The cross-platform view model, driven end to end against an in-memory <c>/sys</c> tree.
/// </summary>
/// <remarks>
/// <para>
/// The view model holds no Avalonia types, which is the whole reason these tests can exist: the window, the timer and
/// the clipboard are the only parts that need a display, and none of them are here. What is left is the part that
/// decides what the user sees — whether the RPM number is a measurement or a guess, which modes the firmware
/// advertises, whether the curve respects the thermal floors, and whether the udev banner appears.
/// </para>
/// <para>
/// The OpenRGB client is pointed at a closed port so a developer who happens to be running OpenRGB on 6742 cannot
/// change a test result.
/// </para>
/// </remarks>
public class DesktopViewModelTests
{
    // ---------------------------------------------------------------- fixtures

    /// <summary>A Nitro 16 AN16-41 with both tiers, a tachometer and four advertised platform profiles.</summary>
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
        fs.WithFile("/sys/class/hwmon/hwmon2/name", "amdgpu\n");
        fs.WithFile("/sys/class/hwmon/hwmon2/temp1_input", "57000\n");

        fs.WithFile("/sys/class/power_supply/BAT1/charge_control_end_threshold", "100\n");

        fs.WithDirectory(LinuxPaths.AcpiPlatformRoot);
        fs.WithDirectory(SenseRoot());
        fs.WithFile(Sense("fan_speed"), "60,50\n");
        fs.WithFile(Sense("backlight_timeout"), "0\n");
        fs.WithFile(Sense("battery_limiter"), "0\n");
        fs.WithFile(Sense("lcd_override"), "0\n");
        fs.WithFile(Sense("cool_boost"), "0\n");

        fs.WithDirectory(KeyboardRoot());
        fs.WithFile(Keyboard("per_zone_mode"), "00E5FF,00E5FF,00E5FF,00E5FF,55\n");
        fs.WithFile(Keyboard("four_zone_mode"), "3,3,55,1,0,229,255\n");

        return fs;
    }

    /// <summary>The same machine with no <c>fan*_input</c>: the kernel exposes control but no tachometer.</summary>
    private static InMemorySysfsFileSystem Nitro16WithoutTachometer()
    {
        var fs = Nitro16();
        fs.WithoutFile("/sys/class/hwmon/hwmon0/fan1_input");
        fs.WithoutFile("/sys/class/hwmon/hwmon0/fan2_input");
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

    private static string SenseRoot() =>
        LinuxPaths.Combine(LinuxPaths.LinuwuModuleRoot, LinuxPaths.NitroSense);

    private static string Sense(string name) => LinuxPaths.Combine(SenseRoot(), name);

    private static string KeyboardRoot() => LinuxPaths.Combine(SenseRoot(), LinuxPaths.FourZoneKeyboard);

    private static string Keyboard(string name) => LinuxPaths.Combine(KeyboardRoot(), name);

    /// <summary>The embedded model table only, so a developer's own profiles.user.json cannot change a result.</summary>
    private static ProfileCatalog Catalog() =>
        ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _);

    /// <summary>
    /// A view model wired to a real <see cref="LinuxSysfsHardwareController"/> over <paramref name="fs"/>.
    /// </summary>
    private static (MainViewModel Vm, LinuxSysfsHardwareController Controller) Create(
        InMemorySysfsFileSystem fs,
        AppSettings? settings = null)
    {
        var appSettings = settings ?? new AppSettings();

        // Port 1 is closed on every machine, so the OpenRGB probe answers "nothing listening" deterministically
        // instead of depending on whether the developer runs OpenRGB.
        var controller = LinuxSysfsHardwareController.Create(
            appSettings, fs, Catalog(), new OpenRgbClient(port: 1));

        var vm = new MainViewModel(appSettings, controller, controller.Capabilities, AppPaths.GetDataFile("settings.json"));
        return (vm, controller);
    }

    // ---------------------------------------------------------------- 1. telemetry

    [Fact]
    public async Task Telemetry_WithATachometer_ReportsMeasuredRpm()
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;

        await vm.InitializeAsync();
        await vm.RefreshTelemetryAsync();

        Assert.True(vm.FanRpmMeasured);
        Assert.Equal(vm.Labels.Measured, vm.FanRpmSourceText);
        Assert.Equal(2436, vm.CpuFanRpm);
        Assert.Equal(2409, vm.GpuFanRpm);
        Assert.Equal("2436 RPM", vm.CpuFanRpmText);

        // The three temperatures come from three different hwmon devices, which is the point of the name lookup.
        Assert.Equal(63, vm.CpuTemperature);
        Assert.Equal(57, vm.GpuTemperature);
        Assert.Equal(55, vm.SysTemperature);
    }

    [Fact]
    public async Task Telemetry_WithoutATachometer_ReportsDerivedRpm()
    {
        var (vm, controller) = Create(Nitro16WithoutTachometer());
        using var _ = vm;
        using var __ = controller;

        await vm.InitializeAsync();
        await vm.RefreshTelemetryAsync();

        Assert.False(vm.FanRpmMeasured);
        Assert.Equal(vm.Labels.Derived, vm.FanRpmSourceText);

        // 60 % of the profile's 6200 RPM ceiling and 50 % of 6000, not a measurement.
        Assert.Equal(LinuxSysfsHardwareController.DeriveRpm(60, 6200), vm.CpuFanRpm);
        Assert.Equal(LinuxSysfsHardwareController.DeriveRpm(50, 6000), vm.GpuFanRpm);
        Assert.Equal(3720, vm.CpuFanRpm);
        Assert.Equal(3000, vm.GpuFanRpm);
    }

    // ---------------------------------------------------------------- 2. operating modes

    [Fact]
    public async Task Modes_ListOnlyWhatTheFirmwareAdvertises()
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;

        await vm.InitializeAsync();

        // The fixture advertises quiet, balanced, balanced-performance and performance — no low-power, so no Eco.
        Assert.Equal(
            new[] { OperatingMode.Quiet, OperatingMode.Default, OperatingMode.Extreme, OperatingMode.Turbo },
            vm.Modes.Select(m => m.Mode).ToArray());
        Assert.DoesNotContain(vm.Modes, m => m.Mode == OperatingMode.Eco);
        Assert.True(vm.ModeControlAvailable);
        Assert.NotNull(vm.SelectedMode);
    }

    [Fact]
    public async Task Modes_FallBackToTheFullSetWhenTheFirmwareDoesNotAnswer()
    {
        var fs = Nitro16();
        fs.WithoutFile(LinuxPaths.PlatformProfileChoices);

        var (vm, controller) = Create(fs);
        using var _ = vm;
        using var __ = controller;

        await vm.InitializeAsync();

        // An unreadable choices file is "not asked", never "no mode works": hiding every button would be a lie.
        Assert.Equal(5, vm.Modes.Count);
        Assert.Contains(vm.Modes, m => m.Mode == OperatingMode.Eco);
    }

    // ---------------------------------------------------------------- 3. the fan curve

    [Fact]
    public async Task Curve_KeepsTheThermalFloorsWhenTheUserDragsAnAnchorDown()
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;

        await vm.InitializeAsync();

        Assert.Equal(6, vm.CurveAnchors.Count);
        Assert.Equal(new[] { 40, 50, 60, 70, 80, 90 }, vm.CurveAnchors.Select(a => a.TemperatureC).ToArray());

        var anchor90 = vm.CurveAnchors.Single(a => a.TemperatureC == 90);
        anchor90.Percent = 30;

        // The slider keeps what the user set; the floor is what the fans get.
        Assert.Equal(30, anchor90.Percent);
        Assert.Equal(80, anchor90.EffectivePercent);
        Assert.Equal("%80", anchor90.EffectiveText);

        // And the curve the write path builds agrees, because it applies the same floor.
        var curve = new FanCurve(vm.CurvePoints);
        Assert.True(curve.PercentAt(90) >= 80);
        Assert.True(curve.PercentAt(95) >= 100);
        Assert.True(curve.PercentAt(85) >= 60);
    }

    [Fact]
    public async Task Curve_ClampsAnAnchorToThePwmRange()
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;

        await vm.InitializeAsync();

        var anchor = vm.CurveAnchors.Single(a => a.TemperatureC == 60);
        anchor.Percent = 5;
        Assert.Equal(FanCurve.MinPercent, anchor.Percent);

        anchor.Percent = 500;
        Assert.Equal(FanCurve.MaxPercent, anchor.Percent);
    }

    [Fact]
    public async Task Curve_EditingAnAnchorRaisesThePreviewAndTheDrawing()
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;

        await vm.InitializeAsync();

        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");

        vm.CurveAnchors.Single(a => a.TemperatureC == 70).Percent = 55;

        Assert.Contains(nameof(MainViewModel.CurvePoints), changed);
        Assert.Contains(nameof(MainViewModel.CurvePreview), changed);
        Assert.Equal(55, vm.CurvePoints.Single(p => p.TemperatureC == 70).Percent);
    }

    // ---------------------------------------------------------------- 4. the udev banner

    [Fact]
    public async Task UdevBanner_AppearsWhenTheNodesExistButCannotBeWritten()
    {
        var fs = Nitro16();
        fs.DenyWrite(LinuxPaths.PlatformProfile);

        var (vm, controller) = Create(fs);
        using var _ = vm;
        using var __ = controller;

        await vm.InitializeAsync();

        Assert.True(vm.NeedsUdevRule);
        Assert.False(string.IsNullOrWhiteSpace(vm.UdevCommand));
        Assert.Contains("99-nitrolite.rules", vm.UdevCommand);
        Assert.Contains("udevadm", vm.UdevCommand);
    }

    [Fact]
    public async Task UdevBanner_StaysHiddenWhenTheNodesAreWritable()
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;

        await vm.InitializeAsync();

        Assert.False(vm.NeedsUdevRule);
        Assert.True(string.IsNullOrEmpty(vm.UdevCommand));
    }

    // ---------------------------------------------------------------- 5. no Acer nodes at all

    [Fact]
    public async Task NoAcerNodes_DoesNotThrow_AndSaysSoInsteadOfClaimingReady()
    {
        var (vm, controller) = Create(new InMemorySysfsFileSystem());
        using var _ = vm;
        using var __ = controller;

        await vm.InitializeAsync();

        Assert.True(vm.HasNoBackend);
        Assert.False(vm.HasBackend);
        Assert.False(vm.IsReady);
        Assert.Equal(vm.Labels.NoAcerNodes, vm.Status);
        Assert.Equal(vm.Labels.NoAcerNodes, vm.NoBackendDetail);

        // Every control that needs hardware is off, so the window offers nothing that cannot work.
        Assert.False(vm.FanControlAvailable);
        Assert.False(vm.ModeControlAvailable);
        Assert.False(vm.LightingAvailable);
        Assert.False(vm.CoolBoostAvailable);
        Assert.False(vm.LcdOverdriveAvailable);
        Assert.False(vm.BacklightTimeoutAvailable);
        Assert.False(vm.BatteryLimitAvailable);
    }

    [Fact]
    public async Task NoAcerNodes_TelemetryTickIsANoOpRatherThanAnException()
    {
        var (vm, controller) = Create(new InMemorySysfsFileSystem());
        using var _ = vm;
        using var __ = controller;

        await vm.InitializeAsync();
        await vm.RefreshTelemetryAsync();
        await vm.RefreshTelemetryAsync();

        Assert.Equal(0, vm.CpuTemperature);
        Assert.Equal(0, vm.CpuFanRpm);
    }

    [Fact]
    public void NoBackendAtAll_StillConstructsAndExplainsItself()
    {
        using var vm = new MainViewModel(new AppSettings(), null, null, AppPaths.GetDataFile("settings.json"));

        Assert.True(vm.HasNoBackend);
        Assert.Equal(vm.Labels.NoBackend, vm.Status);
        Assert.Equal(vm.Labels.NoBackendDetail, vm.NoBackendDetail);
        Assert.Equal(vm.Labels.NoBackend, vm.CapabilitySummaryText);
    }

    // ---------------------------------------------------------------- 6. the settings page

    [Fact]
    public async Task SettingsPage_NamesTheDeviceAndTheTier()
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;

        await vm.InitializeAsync();

        Assert.Contains("AN16-41", vm.DeviceModel);
        Assert.Equal("V1.27", vm.DeviceBios);
        Assert.Equal("both", vm.TierText);
        Assert.Contains(vm.Labels.CapFanControl, vm.CapabilitySummaryText);
        Assert.Contains("✓", vm.CapabilitySummaryText);
    }

    [Fact]
    public async Task SettingsPage_LanguageChangeRepaintsEveryLabel()
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;

        await vm.InitializeAsync();

        var before = vm.Labels.Dashboard;
        var english = Loc.Languages.Single(l => l.Code == "en");

        // Loc is process-wide, so the original language goes back in the finally: leaving it on English would make
        // every other test that reads a Turkish key fail for a reason that has nothing to do with it.
        var original = Loc.Language;
        try
        {
            vm.SelectedLanguage = english;

            Assert.Equal("en", Loc.Language);
            Assert.Equal("Dashboard", vm.Labels.Dashboard);
            Assert.NotEqual(before, vm.Labels.Dashboard);
        }
        finally
        {
            Loc.SetLanguage(original);
        }
    }
}
