using NitroLite.Linux;
using NitroLite.Linux.Sysfs;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The two Linux battery paths no test covered: a driver that reports charge (µAh) instead of energy, and an adapter
/// whose directory name is not on the prefix list.
/// </summary>
public class LinuxBatteryBlindSpotTests
{
    private const string Bat = "/sys/class/power_supply/BAT0";

    [Fact]
    public async Task ChargeBasedDriver_IsConvertedWithTheVoltage_TheSameWayOnBothFigures()
    {
        var fs = new InMemorySysfsFileSystem();
        fs.WithFile(Bat + "/type", "Battery\n");
        fs.WithFile(Bat + "/status", "Discharging\n");
        fs.WithFile(Bat + "/capacity", "50\n");
        fs.WithFile(Bat + "/voltage_now", "15000000\n");      // 15 V
        fs.WithFile(Bat + "/charge_full_design", "4000000\n"); // 4000 mAh → 60 000 mWh
        fs.WithFile(Bat + "/charge_full", "3600000\n");        // 3600 mAh → 54 000 mWh
        fs.WithFile(Bat + "/charge_now", "1800000\n");         // 1800 mAh → 27 000 mWh

        using var service = new LinuxBatteryHealthService(fs, historyPath: System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"nl-wear-{Guid.NewGuid():N}.json"));
        var snapshot = await service.ReadAsync();

        Assert.NotNull(snapshot.Details);
        Assert.Equal(60_000, snapshot.Details!.DesignCapacityMWh, 3);
        Assert.Equal(54_000, snapshot.Details.FullChargeCapacityMWh, 3);
        Assert.Equal(27_000, snapshot.Details.RemainingCapacityMWh, 3);
        Assert.Equal(90, snapshot.Details.HealthPercent, 3);
        Assert.Equal(27_000, snapshot.Status.RemainingCapacityMWh, 3);
    }

    [Fact]
    public void AnAdapterWithAnUnknownName_IsFoundByItsType()
    {
        var fs = new InMemorySysfsFileSystem();
        fs.WithFile("/sys/class/power_supply/ACPI0003:00/type", "Mains\n");
        fs.WithFile("/sys/class/power_supply/ACPI0003:00/online", "1\n");
        fs.WithFile(Bat + "/capacity", "77\n");

        Assert.Equal((true, 77), LinuxPowerSupply.Read(fs));
    }

    [Fact]
    public void AnUnknownSupplyThatIsNotMains_LeavesTheAdapterUnknown()
    {
        var fs = new InMemorySysfsFileSystem();
        fs.WithFile("/sys/class/power_supply/ucsi-source-psy-USBC000:001/type", "USB\n");
        fs.WithFile("/sys/class/power_supply/ucsi-source-psy-USBC000:001/online", "1\n");
        fs.WithFile(Bat + "/capacity", "40\n");

        var (onAc, percent) = LinuxPowerSupply.Read(fs);

        Assert.Null(onAc);
        Assert.Equal(40, percent);
    }
}
