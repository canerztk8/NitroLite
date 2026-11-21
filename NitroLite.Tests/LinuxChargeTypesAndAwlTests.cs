using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Core.Hardware.Wmi;
using NitroLite.Linux;
using NitroLite.Linux.Sysfs;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The in-kernel acer-wmi-battery charge type (Linux 7.3) and the acer-wmi-linuwu layout, which puts the sense and
/// keyboard directories on the acer-wmi platform device and adds the Windows-key lock.
/// </summary>
public class LinuxChargeTypesAndAwlTests
{
    private const string Battery = "/sys/class/power_supply/BAT1/charge_types";

    // ---------------------------------------------------------------- the attribute's text

    [Theory]
    [InlineData("[Standard] Long Life\n", "Standard")]
    [InlineData("Standard [Long Life]\n", "Long Life")]
    [InlineData("Fast [Standard] Adaptive Custom Long Life", "Standard")]
    public void Parse_FindsTheBracketedType_IncludingOneWithASpace(string text, string active)
    {
        var parsed = LinuxChargeTypes.Parse(text);

        Assert.NotNull(parsed);
        Assert.Equal(active, parsed!.Value.Active);
        Assert.Contains("Long Life", parsed.Value.Offered);
        Assert.Contains("Standard", parsed.Value.Offered);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("rubbish")]
    public void Parse_NothingKnown_IsNull(string? text) => Assert.Null(LinuxChargeTypes.Parse(text));

    // ---------------------------------------------------------------- probe and controller

    [Fact]
    public async Task ChargeTypes_IsTheChargeLimit_AndWritesLongLifeAndStandard()
    {
        var fs = Machine();
        fs.WithFile(Battery, "[Standard] Long Life\n");
        // The kernel prints the whole list back with the new type in brackets, not the word that was written.
        fs.OnWrite = (path, value) =>
        {
            if (path != Battery) return true;
            fs.WithFile(Battery, value.Trim() == "Long Life" ? "Standard [Long Life]\n" : "[Standard] Long Life\n");
            return false;
        };

        var caps = Probe(fs);
        Assert.True(caps.Hardware.BatteryChargeLimit);
        Assert.Equal(Battery, caps.Nodes.ChargeTypes);

        using var controller = Controller(fs, caps);
        Assert.Equal(BatteryChargeMode.Full, await controller.GetBatteryChargeModeAsync());

        Assert.True(await controller.SetBatteryChargeModeAsync(BatteryChargeMode.Optimized80));
        Assert.Equal((Battery, "Long Life"), (fs.Writes[^1].Path, fs.Writes[^1].Value.Trim()));
        Assert.Equal(BatteryChargeMode.Optimized80, await controller.GetBatteryChargeModeAsync());

        Assert.True(await controller.SetBatteryChargeModeAsync(BatteryChargeMode.Full));
        Assert.Equal((Battery, "Standard"), (fs.Writes[^1].Path, fs.Writes[^1].Value.Trim()));
    }

    [Fact]
    public void ChargeTypes_WithoutLongLife_IsNotAcersHealthMode()
    {
        var fs = Machine();
        fs.WithFile(Battery, "Fast [Standard] Adaptive\n");

        var caps = Probe(fs);

        Assert.Null(caps.Nodes.ChargeTypes);
        Assert.False(caps.Hardware.BatteryChargeLimit);
    }

    [Fact]
    public void HelperPolicy_TakesOnlyTheTwoTypeNames()
    {
        var fs = Machine();
        fs.WithFile(Battery, "[Standard] Long Life\n");
        var policy = LinuxHelperPolicy.For(Probe(fs), fs);

        Assert.Null(policy.Check(Battery, "Long Life"));
        Assert.Null(policy.Check(Battery, "Standard"));
        Assert.NotNull(policy.Check(Battery, "Fast"));
        Assert.NotNull(policy.Check(Battery, "long life"));
    }

    // ---------------------------------------------------------------- acer-wmi-linuwu

    [Fact]
    public async Task Awl_SenseAndKeyboardOnThePlatformDevice_AreFound_WithTheWinKeyLock()
    {
        var fs = Machine();
        var sense = LinuxPaths.Combine(LinuxPaths.AcpiPlatformRoot, LinuxPaths.NitroSense);
        var keyboard = LinuxPaths.Combine(LinuxPaths.AcpiPlatformRoot, LinuxPaths.FourZoneKeyboard);
        fs.WithDirectory(sense);
        fs.WithFile(LinuxPaths.Combine(sense, "battery_limiter"), "0\n");
        fs.WithFile(LinuxPaths.Combine(sense, "lcd_overdrive"), "0\n");
        fs.WithFile(LinuxPaths.Combine(sense, "cool_boost"), "0\n");
        fs.WithFile(LinuxPaths.Combine(sense, "win_key_lock"), "0\n");
        fs.WithDirectory(keyboard);
        fs.WithFile(LinuxPaths.Combine(keyboard, "per_zone_mode"), "00E5FF,00E5FF,00E5FF,00E5FF,55\n");
        fs.WithFile(LinuxPaths.Combine(keyboard, "four_zone_mode"), "3,3,55,1,0,229,255\n");

        var caps = Probe(fs);

        Assert.Equal(sense, caps.Nodes.SenseRoot);
        Assert.True(caps.Hardware.WinKeyLock);
        Assert.True(caps.Hardware.LcdOverdrive);
        Assert.True(caps.Hardware.CoolBoost);
        Assert.True(caps.Hardware.KeyboardLighting);
        Assert.EndsWith("lcd_overdrive", caps.Nodes.LcdOverride, StringComparison.Ordinal);
        Assert.Equal(LinuxPaths.Combine(keyboard, "per_zone_mode"), caps.Nodes.PerZoneMode);

        using var controller = Controller(fs, caps);
        Assert.True(await controller.SetWinKeyLockAsync(true));
        Assert.Equal("1", fs.Peek(LinuxPaths.Combine(sense, "win_key_lock"))?.Trim());
        Assert.Null(LinuxHelperPolicy.For(caps, fs).Check(LinuxPaths.Combine(sense, "win_key_lock"), "1"));
    }

    [Fact]
    public async Task NoWinKeyNode_TheSwitchStaysOff_AndAWriteIsRefused()
    {
        var fs = Machine();
        var caps = Probe(fs);

        Assert.False(caps.Hardware.WinKeyLock);
        using var controller = Controller(fs, caps);
        Assert.False(await controller.SetWinKeyLockAsync(true));
        Assert.Empty(fs.Writes);
    }

    // ---------------------------------------------------------------- fixtures

    /// <summary>A stock AN515-58: the kernel driver, its platform profile, and nothing else.</summary>
    private static InMemorySysfsFileSystem Machine()
    {
        var fs = new InMemorySysfsFileSystem();
        fs.WithFile("/sys/class/dmi/id/product_name", "Nitro AN515-58\n");
        fs.WithFile("/sys/class/dmi/id/board_name", "AN515-58\n");
        fs.WithFile("/sys/class/dmi/id/sys_vendor", "Acer\n");
        fs.WithFile(LinuxPaths.PlatformProfile, "balanced\n");
        fs.WithFile(LinuxPaths.PlatformProfileChoices, "quiet balanced performance\n");
        fs.WithFile("/sys/class/power_supply/BAT1/capacity", "70\n");
        fs.WithDirectory(LinuxPaths.AcpiPlatformRoot);
        return fs;
    }

    private static LinuxCapabilities Probe(InMemorySysfsFileSystem fs) =>
        new LinuxCapabilityProbe(fs, ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _)).Probe();

    private static LinuxSysfsHardwareController Controller(InMemorySysfsFileSystem fs, LinuxCapabilities caps) =>
        new(fs, caps, new ReadBackPolicy(new AppSettings(), _ => Task.CompletedTask), null);
}
