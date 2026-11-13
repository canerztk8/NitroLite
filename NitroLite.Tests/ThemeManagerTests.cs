using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

public class ThemeManagerTests
{
    [Theory]
    [InlineData(AppTheme.Light, AppTheme.Light)]
    [InlineData(AppTheme.Dark, AppTheme.Dark)]
    public void ExplicitChoice_KeepsItsPalette(AppTheme stored, AppTheme expected)
    {
        Assert.Equal(expected, ThemeManager.Resolve(stored));
    }

    [Fact]
    public void System_FollowsTheWindowsAppTheme()
    {
        var expected = ThemeManager.SystemPrefersDark() ? AppTheme.Dark : AppTheme.Light;
        Assert.Equal(expected, ThemeManager.Resolve(AppTheme.System));
    }

    [Fact]
    public void System_IsStoredAsItsOwnValue()
    {
        // Resolving on save would freeze the user into whichever palette Windows happened to be in that day.
        Assert.NotEqual(AppTheme.Light, AppTheme.System);
        Assert.NotEqual(AppTheme.Dark, AppTheme.System);
    }

    [Theory]
    [InlineData(null)]
    [InlineData((AppTheme)99)]
    [InlineData((AppTheme)(-1))]
    public void UnknownValue_FallsBackToTheLightSurface(AppTheme? stored)
    {
        Assert.Equal(AppTheme.Light, ThemeManager.Resolve(stored));
    }

    [Theory]
    [InlineData(AppTheme.Light, "Themes/Light.xaml")]
    [InlineData(AppTheme.Dark, "Themes/Dark.xaml")]
    public void DictionaryPath_NamesThePaletteFile(AppTheme stored, string expected)
    {
        Assert.Equal(expected, ThemeManager.DictionaryPath(stored));
    }

    [Fact]
    public void DictionaryPath_ResolvesSystemToAConcreteFile()
    {
        var expected = ThemeManager.SystemPrefersDark() ? "Themes/Dark.xaml" : "Themes/Light.xaml";
        Assert.Equal(expected, ThemeManager.DictionaryPath(AppTheme.System));
    }
}
