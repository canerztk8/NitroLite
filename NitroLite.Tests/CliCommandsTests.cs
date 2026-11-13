using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The one-shot hardware flags. These are the only way a script or a macro pad can change the machine without the
/// window, so a wrong parse is a wrong fan speed on someone's laptop: every accepted spelling and every rejection
/// is pinned here.
/// </summary>
public class CliCommandsTests
{
    [Theory]
    [InlineData("--mode")]
    [InlineData("--fan")]
    [InlineData("--profile")]
    [InlineData("--MODE")]
    [InlineData(" --fan ")]
    public void AHardwareFlag_IsRecognised(string arg)
    {
        Assert.True(CliCommands.IsHardwareCommand(new[] { arg }));
    }

    [Theory]
    [InlineData("--autostart")]
    [InlineData("--minimized")]
    [InlineData("--fix-services")]
    [InlineData("--yes")]
    [InlineData("")]
    public void AnythingElse_IsNotAHardwareCommand(string arg)
    {
        Assert.False(CliCommands.IsHardwareCommand(new[] { arg }));
    }

    [Fact]
    public void NoArguments_IsNotAHardwareCommand()
    {
        Assert.False(CliCommands.IsHardwareCommand(null));
        Assert.False(CliCommands.IsHardwareCommand(Array.Empty<string>()));
    }

    [Theory]
    [InlineData("turbo", OperatingMode.Turbo)]
    [InlineData("performance", OperatingMode.Extreme)]
    [InlineData("extreme", OperatingMode.Extreme)]
    [InlineData("balanced", OperatingMode.Default)]
    [InlineData("default", OperatingMode.Default)]
    [InlineData("quiet", OperatingMode.Quiet)]
    [InlineData("eco", OperatingMode.Eco)]
    [InlineData("TURBO", OperatingMode.Turbo)]
    [InlineData(" Turbo ", OperatingMode.Turbo)]
    public void ModeNames_MapToTheOperatingModes(string value, OperatingMode expected)
    {
        Assert.True(CliCommands.TryParse(new[] { "--mode", value }, out var command, out var error));
        Assert.Equal(string.Empty, error);
        Assert.Equal(expected, command.Mode);
        Assert.Null(command.Fan);
        Assert.Null(command.MouseProfile);
    }

    [Fact]
    public void AnUnknownMode_IsRejectedWithTheValueInTheMessage()
    {
        Assert.False(CliCommands.TryParse(new[] { "--mode", "bogus" }, out _, out var error));
        Assert.Contains("--mode", error);
        Assert.Contains("bogus", error);
    }

    [Fact]
    public void AModeWithoutAValue_IsRejected()
    {
        Assert.False(CliCommands.TryParse(new[] { "--mode" }, out _, out var error));
        Assert.Contains("--mode", error);
    }

    [Fact]
    public void AFlagFollowedByAnotherFlag_IsNotGivenThatFlagAsItsValue()
    {
        Assert.False(CliCommands.TryParse(new[] { "--mode", "--fan", "auto" }, out _, out var error));
        Assert.Contains("--mode", error);
    }

    [Theory]
    [InlineData("auto", FanMode.Auto)]
    [InlineData("automatic", FanMode.Auto)]
    [InlineData("max", FanMode.Max)]
    [InlineData("maximum", FanMode.Max)]
    [InlineData("custom", FanMode.Custom)]
    [InlineData("MAX", FanMode.Max)]
    public void FanNames_MapToTheFanModes(string value, FanMode expected)
    {
        Assert.True(CliCommands.TryParse(new[] { "--fan", value }, out var command, out _));
        Assert.Equal(expected, command.Fan);
        Assert.Null(command.FanPercent);
    }

    [Theory]
    [InlineData("30", 30)]
    [InlineData("45", 45)]
    [InlineData("100", 100)]
    public void AFanPercentage_MeansCustomAtThatSpeed(string value, int expected)
    {
        Assert.True(CliCommands.TryParse(new[] { "--fan", value }, out var command, out _));
        Assert.Equal(FanMode.Custom, command.Fan);
        Assert.Equal(expected, command.FanPercent);
    }

    [Theory]
    [InlineData("29")]
    [InlineData("101")]
    [InlineData("0")]
    [InlineData("-5")]
    public void AFanPercentageOutsideTheRangeTheServiceAccepts_IsRejected(string value)
    {
        Assert.False(CliCommands.TryParse(new[] { "--fan", value }, out _, out var error));
        Assert.Contains("--fan", error);
        Assert.Contains(value, error);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("2", 2)]
    [InlineData("4", 4)]
    public void AMouseProfile_IsAcceptedBetweenOneAndFour(string value, int expected)
    {
        Assert.True(CliCommands.TryParse(new[] { "--profile", value }, out var command, out _));
        Assert.Equal(expected, command.MouseProfile);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("5")]
    [InlineData("-1")]
    [InlineData("two")]
    public void AMouseProfileOutsideOneToFour_IsRejected(string value)
    {
        Assert.False(CliCommands.TryParse(new[] { "--profile", value }, out _, out var error));
        Assert.Contains("--profile", error);
    }

    [Fact]
    public void TheFlagsCombine_AndEachOneIsRead()
    {
        Assert.True(CliCommands.TryParse(
            new[] { "--mode", "turbo", "--fan", "60", "--profile", "3" }, out var command, out _));

        Assert.Equal(OperatingMode.Turbo, command.Mode);
        Assert.Equal(FanMode.Custom, command.Fan);
        Assert.Equal(60, command.FanPercent);
        Assert.Equal(3, command.MouseProfile);
        Assert.False(command.IsEmpty);
    }

    [Fact]
    public void AnEmptyCommandLine_ParsesToNothingToDo()
    {
        Assert.True(CliCommands.TryParse(Array.Empty<string>(), out var command, out _));
        Assert.True(command.IsEmpty);
    }

    [Fact]
    public void UnrelatedArguments_AreIgnored()
    {
        Assert.True(CliCommands.TryParse(new[] { "--autostart", "--mode", "quiet" }, out var command, out _));
        Assert.Equal(OperatingMode.Quiet, command.Mode);
    }
}
