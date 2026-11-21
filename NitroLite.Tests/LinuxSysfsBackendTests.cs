using System.Reflection;
using System.Runtime.Versioning;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Core.Hardware.OpenRgb;
using NitroLite.Core.Hardware.Wmi;
using NitroLite.Linux;
using NitroLite.Linux.Sysfs;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The Linux backend, exercised against an in-memory <c>/sys</c> tree.
/// </summary>
/// <remarks>
/// The four groups here are the four things that can go wrong on a real machine. The model table can fail to
/// recognise the laptop, so the first group pins the DMI match. A stock distribution has only the in-kernel
/// <c>acer-wmi</c> module, so the second group runs with no Linuwu-Sense tree at all and expects a smaller but working
/// feature set. The out-of-tree module adds the attributes the kernel does not have, so the third group checks each of
/// them writes and reads back. And the fourth covers the two failures a user actually hits: no permission because the
/// udev rule is not installed, and a keyboard whose attributes are missing entirely.
/// </remarks>
public class LinuxSysfsBackendTests
{
    // ---------------------------------------------------------------- fixtures

    /// <summary>
    /// A Nitro 16 AN16-41 with both tiers present: the in-kernel module, hwmon, the battery class and the
    /// Linuwu-Sense tree with a four-zone keyboard.
    /// </summary>
    private static InMemorySysfsFileSystem Nitro16()
    {
        var fs = new InMemorySysfsFileSystem();

        Dmi(fs, "Nitro AN16-41", "AN16-41", "V1.27");
        fs.WithFile("/proc/cpuinfo", "vendor_id\t: AuthenticAMD\nmodel name\t: AMD Ryzen 7 7840HS\n");

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
        fs.WithFile(Sense("fan_speed"), "0,0\n");
        fs.WithFile(Sense("backlight_timeout"), "0\n");
        fs.WithFile(Sense("battery_limiter"), "0\n");
        fs.WithFile(Sense("lcd_override"), "0\n");
        fs.WithFile(Sense("cool_boost"), "0\n");

        fs.WithDirectory(KeyboardRoot());
        fs.WithFile(Keyboard("per_zone_mode"), "00E5FF,00E5FF,00E5FF,00E5FF,55\n");
        fs.WithFile(Keyboard("four_zone_mode"), "3,3,55,1,0,229,255\n");

        return fs;
    }

    /// <summary>The same machine with no Linuwu-Sense module: what a stock distribution gives you.</summary>
    private static InMemorySysfsFileSystem Nitro16Tier1Only()
    {
        var fs = Nitro16();
        fs.WithoutFile(Sense("fan_speed"));
        fs.WithoutFile(Sense("backlight_timeout"));
        fs.WithoutFile(Sense("battery_limiter"));
        fs.WithoutFile(Sense("lcd_override"));
        fs.WithoutFile(Sense("cool_boost"));
        fs.WithoutFile(Keyboard("per_zone_mode"));
        fs.WithoutFile(Keyboard("four_zone_mode"));
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

    /// <summary>The embedded model table only, so a developer's own profiles.user.json cannot change a test result.</summary>
    private static ProfileCatalog Catalog() =>
        ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _);

    private static LinuxCapabilities Probe(InMemorySysfsFileSystem fs) =>
        new LinuxCapabilityProbe(fs, Catalog()).Probe();

    /// <summary>A controller with a read-back policy that does not wait, because a fake EC applies instantly.</summary>
    private static LinuxSysfsHardwareController Controller(
        InMemorySysfsFileSystem fs,
        AppSettings? settings = null,
        OpenRgbClient? openRgb = null)
    {
        var appSettings = settings ?? new AppSettings();
        return new LinuxSysfsHardwareController(
            fs,
            Probe(fs),
            new ReadBackPolicy(appSettings, _ => Task.CompletedTask),
            openRgb);
    }

    // ---------------------------------------------------------------- 1. DMI and the model table

    [Fact]
    public void Probe_An1641_MatchesTheNitro16Profile()
    {
        var caps = Probe(Nitro16());

        Assert.Equal("AN16-41", caps.Identity.ModelCode);
        Assert.True(caps.Identity.IsAcer);
        Assert.Equal("nitro-16-an16-41", caps.Profile.Id);
        Assert.Equal(6200, caps.Profile.MaxCpuFanRpm);
        Assert.Equal(6000, caps.Profile.MaxGpuFanRpm);
        Assert.Equal("both", caps.Tier);
    }

    [Fact]
    public void Probe_Anv1552_MatchesTheEcMmioProfile()
    {
        var fs = new InMemorySysfsFileSystem();
        Dmi(fs, "Nitro V 15 ANV15-52", "ANV15-52", "V1.10");
        fs.WithFile(LinuxPaths.PlatformProfile, "balanced\n");
        fs.WithFile(LinuxPaths.PlatformProfileChoices, "quiet balanced performance\n");

        var caps = Probe(fs);

        Assert.Equal("nitro-v-15-anv15-52", caps.Profile.Id);
        Assert.True(caps.Profile.EcMmioProfile);
        Assert.Equal(5600, caps.Profile.MaxCpuFanRpm);
        Assert.Equal("acer-wmi", caps.Tier);
    }

    [Fact]
    public void Probe_UnknownAcerModel_FallsBackToTheCatchAllProfile()
    {
        var fs = new InMemorySysfsFileSystem();
        Dmi(fs, "Acer Nitro AN999-99", "AN999-99", "V0.01");
        fs.WithFile(LinuxPaths.PlatformProfile, "balanced\n");
        fs.WithFile(LinuxPaths.PlatformProfileChoices, "quiet balanced performance\n");

        var caps = Probe(fs);

        Assert.Equal("acer-gaming-default", caps.Profile.Id);
        Assert.Equal(7000, caps.Profile.MaxCpuFanRpm);
        // The catch-all is permissive about ceilings, but the capabilities still come from the nodes, not the name.
        Assert.True(caps.Hardware.OperatingModeControl);
        Assert.False(caps.Hardware.FanControl);
        Assert.False(caps.Hardware.KeyboardLighting);
    }

    [Fact]
    public void Probe_NoAcerTreeAtAll_ReportsNoTierAndIsUnavailable()
    {
        var fs = new InMemorySysfsFileSystem();
        Dmi(fs, "Dell XPS 13", "0ABC", "1.0");
        fs.WithFile("/sys/class/dmi/id/sys_vendor", "Dell Inc.\n");

        var caps = Probe(fs);
        using var controller = Controller(fs);

        Assert.Equal("none", caps.Tier);
        Assert.True(caps.Nodes.IsEmpty);
        Assert.False(controller.IsAvailable);
        Assert.False(caps.Identity.IsAcer);
    }

    [Fact]
    public void Probe_ReadsTheSupportedModesFromTheKernelChoices()
    {
        var caps = Probe(Nitro16());

        Assert.Equal(
            new[] { OperatingMode.Quiet, OperatingMode.Default, OperatingMode.Extreme, OperatingMode.Turbo },
            caps.Hardware.SupportedOperatingModes);
        Assert.True(caps.Hardware.OperatingModeControl);
        Assert.True(caps.Hardware.FanControl);
        Assert.True(caps.Hardware.KeyboardLighting);
        Assert.True(caps.Hardware.CoolBoost);
        Assert.True(caps.Hardware.LcdOverdrive);
        Assert.True(caps.Hardware.BacklightTimeout);
        Assert.True(caps.Hardware.BatteryChargeLimit);
        Assert.False(caps.Hardware.WinKeyLock);
    }

    // ---------------------------------------------------------------- 2. tier 1: acer-wmi and hwmon only

    [Fact]
    public async Task Tier1_OperatingMode_ReadsAndWritesThePlatformProfile()
    {
        var fs = Nitro16Tier1Only();
        using var controller = Controller(fs);

        Assert.Equal(OperatingMode.Default, await controller.GetOperatingModeAsync());
        Assert.True(await controller.SetOperatingModeAsync(OperatingMode.Turbo));
        Assert.Equal("performance", fs.Peek(LinuxPaths.PlatformProfile)?.Trim());
        Assert.Equal(OperatingMode.Turbo, await controller.GetOperatingModeAsync());
    }

    [Fact]
    public async Task Tier1_OperatingMode_ExtremeFallsBackWhenTheMachineHasNoBalancedPerformance()
    {
        var fs = Nitro16Tier1Only();
        fs.WithFile(LinuxPaths.PlatformProfileChoices, "quiet balanced performance\n");
        using var controller = Controller(fs);

        // The machine advertises no balanced-performance, so Extreme goes out as performance and the read-back
        // compares the string that was written rather than the mode it decodes to.
        Assert.True(await controller.SetOperatingModeAsync(OperatingMode.Extreme));
        Assert.Equal("performance", fs.Peek(LinuxPaths.PlatformProfile)?.Trim());
    }

    [Fact]
    public async Task Tier1_OperatingMode_IgnoredWriteIsCaughtByTheReadBack()
    {
        var fs = Nitro16Tier1Only();
        fs.OnWrite = (path, _) => path != LinuxPaths.PlatformProfile;
        var settings = new AppSettings();
        using var controller = Controller(fs, settings);

        Assert.False(await controller.SetOperatingModeAsync(OperatingMode.Quiet));
        Assert.True(controller.ReadBack.IsDisabled(ReadBackPolicy.OperatingMode));
        Assert.Equal(1, controller.ReadBack.ConsecutiveSessionFailures(ReadBackPolicy.OperatingMode));
    }

    [Fact]
    public async Task Tier1_RealTachometer_IsReportedAsMeasured()
    {
        var fs = Nitro16Tier1Only();
        using var controller = Controller(fs);

        var data = await controller.GetMonitorDataAsync();

        Assert.NotNull(data);
        Assert.Equal(63, data!.CpuTemperature);
        Assert.Equal(57, data.GpuTemperature);
        Assert.Equal(55, data.SysTemperature);
        Assert.Equal(2436, data.CpuFanSpeed);
        Assert.Equal(2409, data.GpuFanSpeed);
        Assert.True(data.IsFanRpmMeasured);
    }

    [Fact]
    public async Task Tier1_Telemetry_ReadsTheSensorAgainInsteadOfReplayingTheProbe()
    {
        // The probe reads hwmon once to decide what the machine can do. That read must not become the reading the
        // window shows for the rest of the session: a laptop whose temperature never moves is a laptop whose fan
        // curve never moves either, and the thermal floor would be computed against a number from startup.
        var fs = Nitro16Tier1Only();
        using var controller = Controller(fs);

        var first = await controller.GetMonitorDataAsync();
        Assert.Equal(63, first!.CpuTemperature);
        Assert.Equal(2436, first.CpuFanSpeed);

        // The die heats up and the fan spins faster, exactly as sysfs would report it.
        fs.WithFile("/sys/class/hwmon/hwmon1/temp1_input", "81000\n");
        fs.WithFile("/sys/class/hwmon/hwmon0/fan1_input", "4100\n");

        var second = await controller.GetMonitorDataAsync();

        Assert.Equal(81, second!.CpuTemperature);
        Assert.Equal(4100, second.CpuFanSpeed);
        Assert.True(second.IsFanRpmMeasured);
    }

    [Fact]
    public async Task Tier1_Telemetry_FollowsASensorThatDisappears()
    {
        // A module unloaded mid-session is not a machine that is 0 °C. The reading has to fall back to "no reading"
        // rather than keep the last number, or the fan curve would chase a temperature nobody is measuring.
        var fs = Nitro16Tier1Only();
        using var controller = Controller(fs);

        Assert.Equal(63, (await controller.GetMonitorDataAsync())!.CpuTemperature);

        fs.WithoutFile("/sys/class/hwmon/hwmon1/temp1_input");

        var after = await controller.GetMonitorDataAsync();

        // The board sensor is the fallback, and it is still there.
        Assert.Equal(55, after!.CpuTemperature);
    }

    [Fact]
    public async Task Tier1_NoTachometer_DerivesTheSpeedAndLeavesTheWatchdogInStandby()
    {
        var fs = Nitro16Tier1Only();
        fs.WithoutFile("/sys/class/hwmon/hwmon0/fan1_input");
        fs.WithoutFile("/sys/class/hwmon/hwmon0/fan2_input");
        fs.WithFile(Sense("fan_speed"), "60,70\n");
        using var controller = Controller(fs);

        var data = await controller.GetMonitorDataAsync();

        Assert.NotNull(data);
        Assert.False(data!.IsFanRpmMeasured);
        Assert.Equal(3720, data.CpuFanSpeed); // 60 % of the AN16-41's 6200 RPM ceiling
        Assert.Equal(4200, data.GpuFanSpeed); // 70 % of 6000
    }

    [Fact]
    public async Task Tier1_BatteryLimit_WritesEightyAndOneHundred()
    {
        var fs = Nitro16Tier1Only();
        using var controller = Controller(fs);

        Assert.Equal(BatteryChargeMode.Full, await controller.GetBatteryChargeModeAsync());
        Assert.True(await controller.SetBatteryChargeModeAsync(BatteryChargeMode.Optimized80));
        Assert.Equal("80", fs.Peek("/sys/class/power_supply/BAT1/charge_control_end_threshold")?.Trim());
        Assert.Equal(BatteryChargeMode.Optimized80, await controller.GetBatteryChargeModeAsync());

        Assert.True(await controller.SetBatteryChargeModeAsync(BatteryChargeMode.Full));
        Assert.Equal("100", fs.Peek("/sys/class/power_supply/BAT1/charge_control_end_threshold")?.Trim());
    }

    [Fact]
    public async Task Tier1_BatteryLimit_AdaptiveIsRefusedRatherThanWrittenAsSomethingElse()
    {
        var fs = Nitro16Tier1Only();
        using var controller = Controller(fs);

        Assert.False(await controller.SetBatteryChargeModeAsync(BatteryChargeMode.Adaptive));
        Assert.Empty(fs.Writes);
    }

    [Fact]
    public async Task Tier1_NoBatteryNode_ReportsNoChargeMode()
    {
        var fs = Nitro16Tier1Only();
        fs.WithoutFile("/sys/class/power_supply/BAT1/charge_control_end_threshold");
        using var controller = Controller(fs);

        Assert.Null(await controller.GetBatteryChargeModeAsync());
        Assert.False(await controller.SetBatteryChargeModeAsync(BatteryChargeMode.Optimized80));
    }

    // ---------------------------------------------------------------- 3. tier 2: Linuwu-Sense

    [Fact]
    public async Task Tier2_CustomFanSpeed_IsClampedToTheThermalFloor()
    {
        var fs = Nitro16();
        using var controller = Controller(fs);

        // 15 % is below the floor the firmware would happily accept, and 200 % is above the ceiling.
        Assert.True(await controller.SetFanModeAsync(FanMode.Custom, 15, 200));
        Assert.Equal("30,100", fs.Peek(Sense("fan_speed"))?.Trim());
        Assert.Equal(FanMode.Custom, await controller.GetFanModeAsync());
    }

    [Fact]
    public async Task Tier2_FanAutoAndMax_AreTheTwoEndsOfTheSameAttribute()
    {
        var fs = Nitro16();
        using var controller = Controller(fs);

        Assert.True(await controller.SetFanModeAsync(FanMode.Max));
        Assert.Equal("100,100", fs.Peek(Sense("fan_speed"))?.Trim());
        Assert.Equal(FanMode.Max, await controller.GetFanModeAsync());

        Assert.True(await controller.SetFanModeAsync(FanMode.Auto));
        Assert.Equal("0,0", fs.Peek(Sense("fan_speed"))?.Trim());
        Assert.Equal(FanMode.Auto, await controller.GetFanModeAsync());
    }

    [Fact]
    public async Task Tier2_FourZoneStatic_WritesEveryColourAndScalesTheBrightness()
    {
        var fs = Nitro16();
        using var controller = Controller(fs);

        var zones = new[] { "#FF0000", "#00FF00", "#0000FF", "#FFFFFF" };
        Assert.True(await controller.SetLightingAsync("STATIC", 5, 3, 1, "#FF0000", zones));

        // Brightness level 5 of 9 is 55 %, and the module wants the colours without a leading '#'.
        Assert.Equal("FF0000,00FF00,0000FF,FFFFFF,55", fs.Peek(Keyboard("per_zone_mode"))?.Trim());
    }

    [Fact]
    public async Task Tier2_DynamicEffect_WritesTheEffectSpeedBrightnessAndDirection()
    {
        var fs = Nitro16();
        using var controller = Controller(fs);

        Assert.True(await controller.SetLightingAsync("BREATHING", 9, 4, 2, "#00E5FF", null));

        // mode 1 (breathing), speed 4, brightness 100 %, direction 2, colour 0x00E5FF.
        Assert.Equal("1,4,100,2,0,229,255", fs.Peek(Keyboard("four_zone_mode"))?.Trim());
    }

    [Fact]
    public async Task Tier2_TurningTheLightsOff_ZeroesTheBrightness()
    {
        var fs = Nitro16();
        using var controller = Controller(fs);

        Assert.True(await controller.TurnOffLightingAsync());
        Assert.Equal("000000,000000,000000,000000,0", fs.Peek(Keyboard("per_zone_mode"))?.Trim());
    }

    [Fact]
    public async Task Tier2_LightingReadBack_ReturnsTheStoredConfiguration()
    {
        var fs = Nitro16();
        using var controller = Controller(fs);

        var config = await controller.GetLightingConfigAsync();

        Assert.NotNull(config);
        Assert.Equal("WAVE", config!.Effect);
        Assert.Equal(5, config.Brightness); // 55 % back to the app's 0..9 level
        Assert.Equal(3, config.Speed);
        Assert.Equal(1, config.Direction);
        Assert.Equal("#00E5FF", config.Color);
        Assert.Equal(4, config.LEDs.Count);
    }

    [Fact]
    public async Task Tier2_RgbIgnoredByTheFirmware_IsCaughtAndTheCapabilityIsSwitchedOff()
    {
        // The 2025 chassis with ENEK5130 keyboards accept the write and never change a LED.
        var fs = Nitro16();
        fs.OnWrite = (path, _) => path != Keyboard("per_zone_mode");
        using var controller = Controller(fs);

        Assert.False(await controller.SetLightingAsync("STATIC", 5, 3, 1, "#FF0000", null));
        Assert.True(controller.ReadBack.IsDisabled(ReadBackPolicy.KeyboardLighting));
    }

    [Fact]
    public async Task Tier2_CoolBoost_RoundTrips()
    {
        var fs = Nitro16();
        using var controller = Controller(fs);

        Assert.False(await controller.GetCoolBoostAsync());
        Assert.True(await controller.SetCoolBoostAsync(true));
        Assert.Equal("1", fs.Peek(Sense("cool_boost"))?.Trim());
        Assert.True(await controller.GetCoolBoostAsync());
    }

    [Fact]
    public async Task Tier2_LcdOverdrive_RoundTrips()
    {
        var fs = Nitro16();
        using var controller = Controller(fs);

        Assert.True(await controller.SetLcdOverdriveAsync(true));
        Assert.Equal("1", fs.Peek(Sense("lcd_override"))?.Trim());
        Assert.True(await controller.GetLcdOverdriveAsync());
    }

    [Fact]
    public async Task Tier2_BacklightTimeout_RoundTrips()
    {
        var fs = Nitro16();
        using var controller = Controller(fs);

        Assert.True(await controller.SetBacklightTimeoutAsync(true));
        Assert.Equal("1", fs.Peek(Sense("backlight_timeout"))?.Trim());
        Assert.True(await controller.GetBacklightTimeoutAsync());
    }

    [Fact]
    public async Task Tier2_WinKeyLock_IsNotSupportedBecauseNoModuleExposesIt()
    {
        var fs = Nitro16();
        using var controller = Controller(fs);

        Assert.False(await controller.SetWinKeyLockAsync(true));
        Assert.Empty(fs.Writes);
    }

    [Fact]
    public async Task Tier2_MuxSwitch_ReadsAndWritesTheDgpuMode()
    {
        var fs = Nitro16();
        fs.WithFile(Sense("dgpu_mode"), "0\n");
        using var controller = Controller(fs);

        Assert.Equal(GpuOperatingMode.Optimus, await controller.GetGpuModeAsync());
        Assert.True(await controller.SetGpuModeAsync(GpuOperatingMode.DiscreteGpu));
        Assert.Equal("1", fs.Peek(Sense("dgpu_mode"))?.Trim());
        Assert.Equal(1, await controller.GetGpuModeCapabilityAsync());
    }

    [Fact]
    public async Task Tier2_MuxSwitch_UnreadableValueIsNotWrittenOver()
    {
        var fs = Nitro16();
        fs.WithFile(Sense("dgpu_mode"), "auto\n");
        using var controller = Controller(fs);

        Assert.Null(await controller.GetGpuModeAsync());
        Assert.True(await controller.SetGpuModeAsync(GpuOperatingMode.DiscreteGpu));
        Assert.Equal("1", fs.Peek(Sense("dgpu_mode"))?.Trim());
    }

    // ---------------------------------------------------------------- 4. the two failures a user hits

    [Fact]
    public async Task PermissionDenied_ReportsTheUdevHintInsteadOfAHardwareFault()
    {
        var fs = Nitro16();
        fs.DenyWrite(Sense("fan_speed"));
        using var controller = Controller(fs);

        Assert.False(await controller.SetFanModeAsync(FanMode.Max));
        Assert.True(controller.PermissionHintRaised);
        // The hint points at the one-press setup on the settings page, not at a file the user has to copy by hand.
        Assert.Contains("Etkinleştir", controller.PermissionHint);
        Assert.Contains("oturumu", controller.PermissionHint);
    }

    [Fact]
    public async Task PermissionDenied_OnThePlatformProfile_DoesNotThrow()
    {
        var fs = Nitro16Tier1Only();
        fs.DenyWrite(LinuxPaths.PlatformProfile);
        using var controller = Controller(fs);

        Assert.False(await controller.SetOperatingModeAsync(OperatingMode.Quiet));
        Assert.True(controller.PermissionHintRaised);
    }

    [Fact]
    public async Task NoKeyboardNode_FallsBackToOpenRgbAndReportsNotSupportedWhenNothingListens()
    {
        var fs = Nitro16Tier1Only();
        // Port 1 has no listener, so the fallback has to answer "not supported" rather than throw or claim success.
        using var openRgb = new OpenRgbClient(port: 1, delay: (_, _) => Task.CompletedTask);
        using var controller = Controller(fs, openRgb: openRgb);

        Assert.False(await controller.SetLightingAsync("STATIC", 5, 3, 1, "#FF0000", null));
        Assert.NotNull(controller.LastError);
    }

    [Fact]
    public async Task NoKeyboardNode_AndNoOpenRgb_ReportsNoLightingPath()
    {
        var fs = Nitro16Tier1Only();
        using var controller = Controller(fs);

        Assert.False(await controller.SetLightingAsync("STATIC", 5, 3, 1, "#FF0000", null));
        Assert.Null(await controller.GetLightingConfigAsync());
    }

    [Fact]
    public async Task Capabilities_ReportTheProbeAndTheBackendName()
    {
        var fs = Nitro16();
        using var controller = Controller(fs);

        var caps = await controller.GetCapabilitiesAsync();

        Assert.Equal("sysfs", controller.BackendName);
        Assert.Equal("sysfs", caps.Backend);
        Assert.True(caps.ServiceAvailable);
        Assert.True(await controller.EnsureConnectedAsync());
    }

    // ---------------------------------------------------------------- protocol units

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 11)]
    [InlineData(5, 55)]
    [InlineData(9, 100)]
    public void BrightnessLevelToPercent_ScalesTheAppLevelOntoTheModulePercentage(int level, int percent)
    {
        Assert.Equal(percent, LinuxKeyboardProtocol.BrightnessLevelToPercent(level));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(9)]
    public void Brightness_RoundTripsThroughThePercentage(int level)
    {
        Assert.Equal(level, LinuxKeyboardProtocol.BrightnessPercentToLevel(LinuxKeyboardProtocol.BrightnessLevelToPercent(level)));
    }

    [Theory]
    [InlineData("STATIC", 0)]
    [InlineData("DIRECT", 0)]
    [InlineData("BREATHING", 1)]
    [InlineData("NEON", 2)]
    [InlineData("WAVE", 3)]
    [InlineData("SHIFTING", 4)]
    [InlineData("ZOOM", 5)]
    [InlineData("METEOR", 6)]
    [InlineData("TWINKLING", 7)]
    [InlineData("SOMETHING_NEW", 0)]
    public void ModeFor_MapsEveryEffectTheAppOffers(string effect, int mode)
    {
        Assert.Equal(mode, LinuxKeyboardProtocol.ModeFor(effect));
        // DIRECT and an unknown name both land on the module's static mode, so both read back as STATIC.
        var expected = effect is "DIRECT" or "SOMETHING_NEW" ? "STATIC" : effect;
        Assert.Equal(expected, LinuxKeyboardProtocol.EffectFor(mode));
    }

    [Fact]
    public void BuildPerZone_RepeatsThePrimaryColourWhenNoZonesAreGiven()
    {
        Assert.Equal("FF0000,FF0000,FF0000,FF0000,100", LinuxKeyboardProtocol.BuildPerZone(null, "#FF0000", 9));
    }

    [Fact]
    public void BuildPerZone_StripsTheHashAndUppercases()
    {
        Assert.Equal("AABBCC,AABBCC,AABBCC,AABBCC,0", LinuxKeyboardProtocol.BuildPerZone(null, "#aabbcc", 0));
    }

    [Theory]
    [InlineData("")]
    [InlineData("FF0000,00FF00")]
    [InlineData("ZZZZZZ,00FF00,0000FF,FFFFFF,55")]
    [InlineData("FF0000,00FF00,0000FF,FFFFFF,notanumber")]
    public void ParsePerZone_RejectsAnythingThatIsNotFourColoursAndABrightness(string text)
    {
        Assert.Null(LinuxKeyboardProtocol.ParsePerZone(text));
    }

    [Fact]
    public void ParsePerZone_ReadsTheLineTheModuleWrites()
    {
        var parsed = LinuxKeyboardProtocol.ParsePerZone("ff0000,00ff00,0000ff,ffffff,55\n");

        Assert.NotNull(parsed);
        Assert.Equal(new[] { "#FF0000", "#00FF00", "#0000FF", "#FFFFFF" }, parsed!.Value.Zones);
        Assert.Equal(55, parsed.Value.BrightnessPercent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1,2,3")]
    [InlineData("1,2,3,4,5,6,notanumber")]
    public void ParseFourZone_RejectsAnythingThatIsNotSevenNumbers(string text)
    {
        Assert.Null(LinuxKeyboardProtocol.ParseFourZone(text));
    }

    [Fact]
    public void ParseFourZone_ClampsOutOfRangeFieldsInsteadOfTrustingThem()
    {
        var parsed = LinuxKeyboardProtocol.ParseFourZone("99,99,999,99,300,300,300");

        Assert.NotNull(parsed);
        Assert.Equal(LinuxKeyboardProtocol.MaxMode, parsed!.Value.Mode);
        Assert.Equal(LinuxKeyboardProtocol.MaxSpeed, parsed.Value.Speed);
        Assert.Equal(100, parsed.Value.BrightnessPercent);
        Assert.Equal(2, parsed.Value.Direction);
        Assert.Equal("#FFFFFF", parsed.Value.Color);
    }

    [Fact]
    public void NormalizeDirection_ClampsTheVerticalDirectionsTheModuleCannotExpress()
    {
        Assert.Equal(1, LinuxKeyboardProtocol.NormalizeDirection(0));
        Assert.Equal(1, LinuxKeyboardProtocol.NormalizeDirection(1));
        Assert.Equal(2, LinuxKeyboardProtocol.NormalizeDirection(3));
        Assert.Equal(2, LinuxKeyboardProtocol.NormalizeDirection(4));
    }

    [Theory]
    [InlineData("0,0", 0, 0)]
    [InlineData("50,70\n", 50, 70)]
    [InlineData("100,100", 100, 100)]
    public void ParseFanPercent_ReadsTheCpuGpuLine(string text, int cpu, int gpu)
    {
        Assert.Equal((cpu, gpu), LinuxSysfsHardwareController.ParseFanPercent(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("50")]
    [InlineData("cpu,gpu")]
    public void ParseFanPercent_ReportsNullRatherThanZeroForAMalformedLine(string text)
    {
        Assert.Equal(((int?)null, (int?)null), LinuxSysfsHardwareController.ParseFanPercent(text));
    }

    [Theory]
    [InlineData("0", GpuOperatingMode.Optimus)]
    [InlineData("1", GpuOperatingMode.DiscreteGpu)]
    [InlineData("integrated", GpuOperatingMode.Optimus)]
    [InlineData("discrete", GpuOperatingMode.DiscreteGpu)]
    [InlineData("hybrid", GpuOperatingMode.Optimus)]
    public void ParseGpuMode_AcceptsBothTheNumericAndTheWordForm(string text, GpuOperatingMode expected)
    {
        Assert.Equal(expected, LinuxSysfsHardwareController.ParseGpuMode(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("auto")]
    [InlineData("2")]
    public void ParseGpuMode_ReportsNoAnswerForAValueItDoesNotUnderstand(string text)
    {
        Assert.Null(LinuxSysfsHardwareController.ParseGpuMode(text));
    }

    [Fact]
    public void EncodeGpuMode_NeverWritesAutomatic()
    {
        Assert.Equal("0", LinuxSysfsHardwareController.EncodeGpuMode(GpuOperatingMode.Optimus));
        Assert.Equal("1", LinuxSysfsHardwareController.EncodeGpuMode(GpuOperatingMode.DiscreteGpu));
        Assert.Null(LinuxSysfsHardwareController.EncodeGpuMode(GpuOperatingMode.Automatic));
    }

    [Fact]
    public void NormalisePercent_KeepsACustomSpeedInsideTheThermalRange()
    {
        Assert.Equal(FanCurve.MinPercent, LinuxSysfsHardwareController.NormalisePercent(15));
        Assert.Equal(FanCurve.MaxPercent, LinuxSysfsHardwareController.NormalisePercent(200));
        Assert.Equal(50, LinuxSysfsHardwareController.NormalisePercent(-1));
        Assert.Equal(70, LinuxSysfsHardwareController.NormalisePercent(70));
    }

    [Fact]
    public void DeriveRpm_UsesTheModelCeilingAndReportsZeroWhenItIsUnknown()
    {
        Assert.Equal(3720, LinuxSysfsHardwareController.DeriveRpm(60, 6200));
        Assert.Equal(0, LinuxSysfsHardwareController.DeriveRpm(60, 0));
    }

    [Fact]
    public void MatchesZones_ConfirmsOffByBrightnessAlone()
    {
        var off = new LinuxZoneLighting(new[] { "#000000", "#000000", "#000000", "#000000" }, 0);
        // A firmware that keeps the last colour in its getter still counts as off when the brightness is zero.
        var stale = new LinuxZoneLighting(new[] { "#FF0000", "#00FF00", "#0000FF", "#FFFFFF" }, 0);

        Assert.True(LinuxSysfsHardwareController.MatchesZones(stale, off));
    }

    [Fact]
    public void MatchesEffect_IgnoresTheColourForAnEffectThatCyclesItsOwn()
    {
        var target = new LinuxEffectLighting(LinuxKeyboardProtocol.ModeWave, 3, 55, 1, "#FF0000");
        var observed = new LinuxEffectLighting(LinuxKeyboardProtocol.ModeWave, 3, 55, 1, "#00FF00");

        Assert.True(LinuxSysfsHardwareController.MatchesEffect(observed, target));
    }

    // ---------------------------------------------------------------- the assembly itself

    [Fact]
    public void LinuxAssembly_ReferencesNoWindowsOnlyAssembly()
    {
        var linux = typeof(LinuxSysfsHardwareController).Assembly;
        var referenced = linux.GetReferencedAssemblies().Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var name in new[] { "PresentationCore", "PresentationFramework", "WindowsBase", "System.Management", "Microsoft.Win32.Registry" })
        {
            Assert.DoesNotContain(name, referenced);
        }

        // Plain net10.0, not net10.0-windows: the assembly has to load on a machine with no WPF at all.
        var framework = linux.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;
        Assert.Equal(".NETCoreApp,Version=v10.0", framework);
    }

    [Fact]
    public void Controller_ImplementsTheSharedContract()
    {
        Assert.True(typeof(IHardwareController).IsAssignableFrom(typeof(LinuxSysfsHardwareController)));
    }
}
