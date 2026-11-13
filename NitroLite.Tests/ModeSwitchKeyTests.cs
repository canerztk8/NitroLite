using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

public class ModeSwitchKeyTests
{
    [Theory]
    [InlineData(OperatingMode.Default, OperatingMode.Extreme)]
    [InlineData(OperatingMode.Extreme, OperatingMode.Turbo)]
    [InlineData(OperatingMode.Turbo, OperatingMode.Quiet)]
    [InlineData(OperatingMode.Quiet, OperatingMode.Default)]
    [InlineData(OperatingMode.Eco, OperatingMode.Default)]
    public void OnAdapter_FollowsNitroSenseCycle(OperatingMode current, OperatingMode expected)
    {
        Assert.Equal(expected, ModeSwitchKey.Next(current, onAc: true, batteryPercent: 80));
    }

    [Theory]
    [InlineData(OperatingMode.Default, OperatingMode.Eco)]
    [InlineData(OperatingMode.Eco, OperatingMode.Default)]
    [InlineData(OperatingMode.Turbo, OperatingMode.Default)]
    [InlineData(OperatingMode.Quiet, OperatingMode.Default)]
    public void OnBattery_TogglesDefaultAndEco_LikeNitroSense(OperatingMode current, OperatingMode expected)
    {
        var next = ModeSwitchKey.Next(current, onAc: false, batteryPercent: 70);
        Assert.Equal(expected, next);
        Assert.False(ModePolicy.IsHighPowerMode(next));
    }

    [Theory]
    [InlineData(OperatingMode.Default, OperatingMode.Quiet)]
    [InlineData(OperatingMode.Quiet, OperatingMode.Default)]
    [InlineData(OperatingMode.Turbo, OperatingMode.Default)]
    public void OnAdapterBelow40Percent_SkipsHighPowerModes(OperatingMode current, OperatingMode expected)
    {
        Assert.Equal(expected, ModeSwitchKey.Next(current, onAc: true, batteryPercent: 25));
    }
}

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

public class CustomGameValidationTests
{
    [Fact]
    public void RejectsSystemAndAlwaysRunningPrograms()
    {
        var explorer = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows), "explorer.exe");
        Assert.NotNull(GameDetectionService.ValidateCustomGameExecutable(explorer));

        var notepad = System.IO.Path.Combine(System.Environment.SystemDirectory, "notepad.exe");
        if (System.IO.File.Exists(notepad))
            Assert.NotNull(GameDetectionService.ValidateCustomGameExecutable(notepad));
    }

    [Fact]
    public void RejectsMissingAndNonExeFiles()
    {
        Assert.NotNull(GameDetectionService.ValidateCustomGameExecutable(@"C:\does\not\exist\game.exe"));

        var txt = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"nitrolite_{System.Guid.NewGuid():N}.txt");
        System.IO.File.WriteAllText(txt, "x");
        try { Assert.NotNull(GameDetectionService.ValidateCustomGameExecutable(txt)); }
        finally { System.IO.File.Delete(txt); }
    }

    [Fact]
    public void AcceptsAnOrdinaryExeOutsideWindows()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"nitrolite_game_{System.Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dir);
        var exe = System.IO.Path.Combine(dir, "MyGame.exe");
        System.IO.File.WriteAllBytes(exe, new byte[] { 0x4D, 0x5A });
        try { Assert.Null(GameDetectionService.ValidateCustomGameExecutable(exe)); }
        finally { System.IO.Directory.Delete(dir, true); }
    }

    [Fact]
    public void PathCheck_RejectsSameNameElsewhere_ButToleratesUnknownOrMovedPaths()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"nitrolite_game_{System.Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dir);
        var exe = System.IO.Path.Combine(dir, "MyGame.exe");
        System.IO.File.WriteAllBytes(exe, new byte[] { 0x4D, 0x5A });
        try
        {
            var profile = new GameProfile { ExeName = "MyGame", InstallPath = exe };
            Assert.True(GameDetectionService.IsPathCompatible(profile, exe));
            Assert.False(GameDetectionService.IsPathCompatible(profile, @"D:\Other\MyGame.exe"));
            Assert.True(GameDetectionService.IsPathCompatible(profile, null)); // protected process: path unknown

            var moved = new GameProfile { ExeName = "MyGame", InstallPath = @"C:\gone\MyGame.exe" };
            Assert.True(GameDetectionService.IsPathCompatible(moved, @"D:\Other\MyGame.exe"));
        }
        finally { System.IO.Directory.Delete(dir, true); }
    }
}
