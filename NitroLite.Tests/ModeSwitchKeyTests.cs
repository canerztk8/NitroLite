using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

public class ModePolicyTests
{
    [Theory]
    [InlineData(true, 80, true)]
    [InlineData(true, 40, true)]
    [InlineData(true, 39, false)]
    [InlineData(false, 100, false)]
    [InlineData(true, null, true)]   // no battery / unknown level on AC
    [InlineData(false, null, false)]
    public void HighPower_RequiresAdapterAndAtLeast40Percent(bool onAc, int? percent, bool expected)
    {
        Assert.Equal(expected, ModePolicy.IsHighPowerAllowed(onAc, percent));
        Assert.Equal(expected, ModePolicy.IsModeAllowed(OperatingMode.Turbo, onAc, percent));
        Assert.Equal(expected, ModePolicy.IsModeAllowed(OperatingMode.Extreme, onAc, percent));
    }

    [Theory]
    [InlineData(OperatingMode.Quiet)]
    [InlineData(OperatingMode.Default)]
    [InlineData(OperatingMode.Eco)]
    public void LowPowerModes_AreAlwaysAllowed(OperatingMode mode)
    {
        Assert.True(ModePolicy.IsModeAllowed(mode, onAc: false, batteryPercent: 5));
    }

    [Fact]
    public void ClampForPower_FallsBackToBalanced()
    {
        Assert.Equal(OperatingMode.Default, ModePolicy.ClampForPower(OperatingMode.Turbo, false, 90));
        Assert.Equal(OperatingMode.Turbo, ModePolicy.ClampForPower(OperatingMode.Turbo, true, 90));
        Assert.Equal(OperatingMode.Quiet, ModePolicy.ClampForPower(OperatingMode.Quiet, false, 10));
    }

    [Theory]
    // On the adapter the high-power rows are offered and Eco is not; on battery it is the other way round.
    [InlineData(true, OperatingMode.Default, true, false)]
    [InlineData(true, OperatingMode.Quiet, true, false)]
    [InlineData(false, OperatingMode.Default, false, true)]
    [InlineData(false, OperatingMode.Quiet, false, true)]
    // The active row stays on screen even when its group is hidden, so the list always marks the live mode.
    [InlineData(false, OperatingMode.Turbo, true, true)]
    [InlineData(false, OperatingMode.Extreme, true, true)]
    [InlineData(true, OperatingMode.Eco, true, true)]
    public void VisibleModeGroups_ShowsEachGroupWhereItCanBeUsed(
        bool onAc, OperatingMode active, bool expectHighPower, bool expectEco)
    {
        var (showHighPower, showEco) = ModePolicy.VisibleModeGroups(onAc, active);
        Assert.Equal(expectHighPower, showHighPower);
        Assert.Equal(expectEco, showEco);
    }

    [Theory]
    [InlineData(0, "Devre Dışı")]
    [InlineData(1, "Windows Varsayılanı")]
    [InlineData(2, "Agresif")]
    [InlineData(4, "Verimli")]
    [InlineData(3, "Diğer (Windows değeri 3)")]
    [InlineData(6, "Diğer (Windows değeri 6)")]
    public void BoostNames_NeverMislabelUnknownValuesAsAggressive(int value, string expected)
    {
        Assert.Equal(expected, ModePolicy.BoostDisplayName((CpuBoostMode)value));
    }

    [Fact]
    public void ModeNames_AreTurkish()
    {
        Assert.Equal("Performans", ModePolicy.DisplayName(OperatingMode.Extreme));
        Assert.Equal("Dengeli", ModePolicy.DisplayName(OperatingMode.Default));
        Assert.Equal("Eco", ModePolicy.DisplayName(OperatingMode.Eco));
    }

    [Fact]
    public void ModeNames_FollowTheSelectedLanguage()
    {
        var original = Loc.Language;
        try
        {
            Loc.SetLanguage("en");
            Assert.Equal("Performance", ModePolicy.DisplayName(OperatingMode.Extreme));
            Assert.Equal("Balanced", ModePolicy.DisplayName(OperatingMode.Default));
            Assert.Equal("Quiet", ModePolicy.DisplayName(OperatingMode.Quiet));
            // An enum value NitroLite does not know still reads as a mode, not as a raw number.
            Assert.Equal("Mode 99", ModePolicy.DisplayName((OperatingMode)99));
        }
        finally
        {
            Loc.SetLanguage(original);
        }
    }
}
