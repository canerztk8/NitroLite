using System;
using System.Collections.Generic;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Empirical adversarial stress tests designed by challenger_nitrolite_1.
/// Challenges behavioral robustness of StartupOptions.Parse under adversarial
/// combinatorial inputs.
/// </summary>
public class AdversarialRegressionAndStartupTests
{
    // =========================================================================
    // 1. THERMAL STABILITY REGRESSION MATH STRESS TESTS
    // =========================================================================

    [Fact]
    public void StartupOptions_NullAndWhitespaceInputs_NeverStartMinimized()
    {
        string[]?[] inputs = new string[]?[]
        {
            null,
            Array.Empty<string>(),
            new[] { "" },
            new[] { "   ", "\t", "\r\n" }
        };

        foreach (var inputArgs in inputs)
        {
            var options = StartupOptions.Parse(inputArgs);
            Assert.NotNull(options);
            Assert.False(options.StartMinimized, "Manual launch with empty/whitespace args must NEVER start minimized!");
            Assert.False(options.IsAutoStart, "Manual launch with empty/whitespace args must NOT be marked autostart!");
        }
    }

    [Theory]
    [InlineData("--autostart", false, true)]
    [InlineData("--AuToStArT", false, true)]
    [InlineData("--AUTOSTART", false, true)]
    [InlineData("--minimized", true, false)]
    [InlineData("--MiNiMiZeD", true, false)]
    [InlineData("--MINIMIZED", true, false)]
    [InlineData("  --autostart  ", false, true)]
    [InlineData("\t--minimized\t", true, false)]
    public void StartupOptions_CasePermutationsAndPadding_ParsedAccurately(
        string flag, bool expectedMinimized, bool expectedAutoStart)
    {
        var options = StartupOptions.Parse(new[] { flag });

        Assert.Equal(expectedMinimized, options.StartMinimized);
        Assert.Equal(expectedAutoStart, options.IsAutoStart);
    }

    [Fact]
    public void StartupOptions_DuplicatedFlags_AreIdempotent()
    {
        var opt1 = StartupOptions.Parse(new[] { "--autostart", "--autostart", "--autostart" });
        Assert.False(opt1.StartMinimized);
        Assert.True(opt1.IsAutoStart);

        var opt2 = StartupOptions.Parse(new[] { "--minimized", "--minimized" });
        Assert.True(opt2.StartMinimized);
        Assert.False(opt2.IsAutoStart);

        var opt3 = StartupOptions.Parse(new[] { "--autostart", "--minimized", "--autostart" });
        Assert.True(opt3.StartMinimized);
        Assert.True(opt3.IsAutoStart);
    }

    [Fact]
    public void StartupOptions_MixedAndUnrecognizedFlags_HandledGracefully()
    {
        var testCases = new (string[] args, bool expectedMinimized, bool expectedAutoStart)[]
        {
            (new[] { "--foo", "--autostart", "--bar" }, false, true),
            (new[] { "--foo", "--minimized", "--bar" }, true, false),
            (new[] { "--foo", "--bar", "-a", "/autostart", "--", "-" }, false, false),
            (new[] { "C:\\Path\\To\\File.txt", "--start-minimized", "--auto-start" }, false, false)
        };

        foreach (var tc in testCases)
        {
            var options = StartupOptions.Parse(tc.args);
            Assert.Equal(tc.expectedMinimized, options.StartMinimized);
            Assert.Equal(tc.expectedAutoStart, options.IsAutoStart);
        }
    }

    [Fact]
    public void StartupOptions_ManualLaunchInvariant_ComprehensivePermutations()
    {
        // Test 15 distinct variations that represent manual interactive execution
        var manualInvocations = new List<string[]?>
        {
            null,
            Array.Empty<string>(),
            new[] { "" },
            new[] { " " },
            new[] { "   " },
            new[] { "\t" },
            new[] { "--help" },
            new[] { "-h" },
            new[] { "/?" },
            new[] { "--version" },
            new[] { "-v" },
            new[] { "run" },
            new[] { "launch" },
            new[] { "--profile", "gaming" },
            new[] { "--tray" } // Note: --tray without --minimized/--autostart is not recognized as minimize
        };

        foreach (var args in manualInvocations)
        {
            var options = StartupOptions.Parse(args);
            Assert.False(options.StartMinimized, $"Args '{string.Join(" ", args ?? Array.Empty<string>())}' must NOT start minimized!");
            Assert.False(options.IsAutoStart, $"Args '{string.Join(" ", args ?? Array.Empty<string>())}' must NOT be autostart!");
        }
    }
}
