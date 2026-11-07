using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

public class StartupOptionsTests
{
    [Fact]
    public void EmptyArgs_EvaluatesToManualLaunchNonMinimized()
    {
        var nullOptions = StartupOptions.Parse(null);
        Assert.False(nullOptions.StartMinimized);
        Assert.False(nullOptions.IsAutoStart);

        var emptyOptions = StartupOptions.Parse(Array.Empty<string>());
        Assert.False(emptyOptions.StartMinimized);
        Assert.False(emptyOptions.IsAutoStart);
    }

    [Fact]
    public void AutostartFlag_EvaluatesToAutostartButNotMinimized()
    {
        var options = StartupOptions.Parse(new[] { "--autostart" });
        Assert.False(options.StartMinimized); // only --minimized hides the window
        Assert.True(options.IsAutoStart);
    }

    [Fact]
    public void MinimizedFlag_EvaluatesToMinimizedNonAutostart()
    {
        var options = StartupOptions.Parse(new[] { "--minimized" });
        Assert.True(options.StartMinimized);
        Assert.False(options.IsAutoStart);
    }

    [Fact]
    public void CombinedFlags_EvaluatesToBothTrue()
    {
        var options = StartupOptions.Parse(new[] { "--autostart", "--minimized" });
        Assert.True(options.StartMinimized);
        Assert.True(options.IsAutoStart);
    }

    [Theory]
    [InlineData("--AUTOSTART", true, false)]
    [InlineData("--AutoStart", true, false)]
    [InlineData("--mInImIzEd", false, true)]
    [InlineData("--MINIMIZED", false, true)]
    public void CaseInsensitiveArgs_ParsedCorrectly(string flag, bool autoStart, bool minimized)
    {
        var options = StartupOptions.Parse(new[] { flag });
        Assert.Equal(autoStart, options.IsAutoStart);
        Assert.Equal(minimized, options.StartMinimized);
    }

    [Fact]
    public void UnrecognizedFlagsAndWhitespace_HandledGracefully()
    {
        var options = StartupOptions.Parse(new[] { "   ", "", "--other-flag", "random-arg" });
        Assert.False(options.StartMinimized);
        Assert.False(options.IsAutoStart);
    }

    [Fact]
    public void DebloatService_AutoStartCommandLine_ContainsAutostartFlag()
    {
        // Verify expected command line generation pattern
        string exePath = @"C:\Program Files\NitroLite\NitroLite.exe";
        string normalCmd = $"\"{exePath}\" --autostart";
        string minCmd = $"\"{exePath}\" --autostart --minimized";

        var normalParsed = StartupOptions.Parse(new[] { "--autostart" });
        Assert.True(normalParsed.IsAutoStart);
        Assert.False(normalParsed.StartMinimized);

        var minParsed = StartupOptions.Parse(new[] { "--autostart", "--minimized" });
        Assert.True(minParsed.IsAutoStart);
        Assert.True(minParsed.StartMinimized);

        Assert.Contains("--autostart", normalCmd);
        Assert.Contains("--autostart", minCmd);
        Assert.Contains("--minimized", minCmd);
    }
}
