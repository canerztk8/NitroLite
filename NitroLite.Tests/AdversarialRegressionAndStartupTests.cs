using System;
using System.Collections.Generic;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Empirical adversarial stress tests designed by challenger_nitrolite_1.
/// Challenges mathematical stability of ThermalHealthService.GetStabilityTrend (OLS regression)
/// and behavioral robustness of StartupOptions.Parse under adversarial combinatorial inputs.
/// </summary>
public class AdversarialRegressionAndStartupTests
{
    // =========================================================================
    // 1. THERMAL STABILITY REGRESSION MATH STRESS TESTS
    // =========================================================================

    [Fact]
    public void StabilityTrend_ZeroEntries_ReturnsSafeDefaultsWithoutException()
    {
        var service = new ThermalHealthService();
        service.SetDailyHistoryForTesting(new List<DailyThermalSummary>());

        var trend = service.GetStabilityTrend(30);

        Assert.NotNull(trend);
        Assert.Equal(0, trend.DaysCount);
        Assert.Equal(100.0, trend.StabilityScorePercent);
        Assert.Equal(0.0, trend.DegradationSlopePerDay);
        Assert.Equal(ThermalStatusLevel.Excellent, trend.StatusLevel);
        Assert.Equal("Veri toplanıyor (Yeterli gün kaydı yok)", trend.StatusText);
        Assert.Equal(0.0, trend.CoefficientOfVariation);
        Assert.False(double.IsNaN(trend.StabilityScorePercent));
        Assert.False(double.IsInfinity(trend.StabilityScorePercent));
    }

    [Theory]
    [InlineData(1.95, 100.0, ThermalStatusLevel.Excellent)]
    [InlineData(2.05, 98.0, ThermalStatusLevel.Excellent)]
    [InlineData(2.50, 89.0, ThermalStatusLevel.Good)]
    [InlineData(3.00, 79.0, ThermalStatusLevel.Degraded)]
    [InlineData(3.50, 69.0, ThermalStatusLevel.Critical)]
    [InlineData(5.00, 50.0, ThermalStatusLevel.Critical)] // Clamped to min 50.0 for N=1
    [InlineData(0.0, 100.0, ThermalStatusLevel.Excellent)] // Falls back to baseline
    [InlineData(-1.0, 100.0, ThermalStatusLevel.Excellent)] // Falls back to baseline
    public void StabilityTrend_SingleEntry_CalculatesCalibratedScoreSafely(
        double ratio, double expectedScore, ThermalStatusLevel expectedLevel)
    {
        var service = new ThermalHealthService();
        service.SetDailyHistoryForTesting(new List<DailyThermalSummary>
        {
            new() { Date = "2026-11-01", ThermalResistanceRatio = ratio }
        });

        var trend = service.GetStabilityTrend(30);

        // A ratio of 0 or below means "no load measurement that day" and is not counted.
        Assert.Equal(ratio > 0 ? 1 : 0, trend.DaysCount);
        Assert.Equal(0.0, trend.DegradationSlopePerDay);
        Assert.Equal(0.0, trend.CoefficientOfVariation);
        Assert.InRange(trend.StabilityScorePercent, 50.0, 100.0);
        Assert.Equal(expectedScore, trend.StabilityScorePercent);
        Assert.Equal(expectedLevel, trend.StatusLevel);
        Assert.False(string.IsNullOrWhiteSpace(trend.StatusText));
        Assert.False(double.IsNaN(trend.StabilityScorePercent));
        Assert.False(double.IsInfinity(trend.StabilityScorePercent));
    }

    [Fact]
    public void StabilityTrend_TwoEntries_CalculatesSlopeAndCvSafely()
    {
        var service = new ThermalHealthService();
        service.SetDailyHistoryForTesting(new List<DailyThermalSummary>
        {
            new() { Date = "2026-11-01", ThermalResistanceRatio = 1.95 },
            new() { Date = "2026-11-02", ThermalResistanceRatio = 2.05 }
        });

        var trend = service.GetStabilityTrend(30);

        Assert.Equal(2, trend.DaysCount);
        // N=2: x0=0, x1=1, meanX=0.5, diffX0=-0.5, diffX1=0.5, denominator=0.5
        // y0=1.95, y1=2.05, meanY=2.00, diffY0=-0.05, diffY1=0.05, numerator=(-0.5*-0.05) + (0.5*0.05) = 0.05
        // slope = 0.05 / 0.5 = 0.10
        Assert.Equal(0.10, trend.DegradationSlopePerDay, precision: 2);
        Assert.True(trend.CoefficientOfVariation > 0);
        Assert.False(double.IsNaN(trend.StabilityScorePercent));
        Assert.False(double.IsInfinity(trend.StabilityScorePercent));
        Assert.InRange(trend.StabilityScorePercent, 10.0, 100.0);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(14)]
    [InlineData(30)]
    [InlineData(50)]
    public void StabilityTrend_AllIdenticalRatios_ZeroVariance_ReturnsZeroSlopeAndMaxScore(int count)
    {
        var service = new ThermalHealthService();
        var history = new List<DailyThermalSummary>();
        for (int i = 0; i < count; i++)
        {
            history.Add(new DailyThermalSummary
            {
                Date = $"2026-10-{(i + 1):D2}",
                ThermalResistanceRatio = 1.95
            });
        }
        service.SetDailyHistoryForTesting(history);

        var trend = service.GetStabilityTrend(count);

        Assert.Equal(count, trend.DaysCount);
        Assert.Equal(0.0, trend.DegradationSlopePerDay);
        Assert.Equal(0.0, trend.CoefficientOfVariation);
        Assert.Equal(100.0, trend.StabilityScorePercent);
        Assert.Equal(ThermalStatusLevel.Excellent, trend.StatusLevel);
        Assert.Contains("Kararlı", trend.StatusText);
        Assert.False(double.IsNaN(trend.StabilityScorePercent));
        Assert.False(double.IsInfinity(trend.StabilityScorePercent));
    }

    [Theory]
    [InlineData(0.10)]
    [InlineData(0.25)]
    [InlineData(1.00)]
    [InlineData(10.00)]
    public void StabilityTrend_ExtremePositiveSlope_Degradation_ClampedToMinimumScore(double steepSlope)
    {
        var service = new ThermalHealthService();
        var history = new List<DailyThermalSummary>();
        for (int i = 0; i < 30; i++)
        {
            history.Add(new DailyThermalSummary
            {
                Date = $"2026-10-{(i + 1):D2}",
                ThermalResistanceRatio = 2.00 + (i * steepSlope)
            });
        }
        service.SetDailyHistoryForTesting(history);

        var trend = service.GetStabilityTrend(30);

        Assert.Equal(30, trend.DaysCount);
        Assert.True(trend.DegradationSlopePerDay >= 0.10);
        Assert.InRange(trend.StabilityScorePercent, 10.0, 50.0); // heavily degraded, well below 70%
        if (steepSlope >= 0.25)
        {
            Assert.Equal(10.0, trend.StabilityScorePercent); // Clamped at lower bound 10.0%
        }
        Assert.Equal(ThermalStatusLevel.Critical, trend.StatusLevel);
        Assert.Contains("Kritik", trend.StatusText);
        Assert.False(double.IsNaN(trend.StabilityScorePercent));
        Assert.False(double.IsInfinity(trend.StabilityScorePercent));
    }

    [Fact]
    public void StabilityTrend_NegativeSlope_BurnInImprovement_DoesNotPenalizeScore()
    {
        var service = new ThermalHealthService();
        var history = new List<DailyThermalSummary>();
        // Thermal resistance dropping over 30 days from 2.05 down to 1.76 (-0.01 per day)
        for (int i = 0; i < 30; i++)
        {
            history.Add(new DailyThermalSummary
            {
                Date = $"2026-10-{(i + 1):D2}",
                ThermalResistanceRatio = 2.05 - (i * 0.01)
            });
        }
        service.SetDailyHistoryForTesting(history);

        var trend = service.GetStabilityTrend(30);

        Assert.Equal(30, trend.DaysCount);
        Assert.True(trend.DegradationSlopePerDay < 0.0, $"Expected negative slope, got {trend.DegradationSlopePerDay}");
        // Negative slope must not incur slopePenalty (Math.Max(0.0, slope * 250) == 0)
        // Average ratio is (2.05 + 1.76)/2 = 1.905 <= 2.05 (no ratio penalty)
        // Score should only have minor CV penalty (~2-3%) and remain >= 95%
        Assert.True(trend.StabilityScorePercent >= 95.0, $"Score {trend.StabilityScorePercent}% should be >= 95%");
        Assert.Equal(ThermalStatusLevel.Excellent, trend.StatusLevel);
        Assert.False(double.IsNaN(trend.StabilityScorePercent));
        Assert.False(double.IsInfinity(trend.StabilityScorePercent));
    }

    [Fact]
    public void StabilityTrend_AlternatingOscillatingNoise_HandledWithoutDivisionByZeroOrNaN()
    {
        var service = new ThermalHealthService();
        var history = new List<DailyThermalSummary>();
        // Alternating between 1.80 and 2.50
        for (int i = 0; i < 30; i++)
        {
            history.Add(new DailyThermalSummary
            {
                Date = $"2026-10-{(i + 1):D2}",
                ThermalResistanceRatio = (i % 2 == 0) ? 1.80 : 2.50
            });
        }
        service.SetDailyHistoryForTesting(history);

        var trend = service.GetStabilityTrend(30);

        Assert.Equal(30, trend.DaysCount);
        Assert.True(trend.CoefficientOfVariation > 0.10, "Expected significant CV for oscillating data");
        Assert.InRange(trend.StabilityScorePercent, 10.0, 100.0);
        Assert.False(double.IsNaN(trend.StabilityScorePercent));
        Assert.False(double.IsNaN(trend.DegradationSlopePerDay));
        Assert.False(double.IsNaN(trend.CoefficientOfVariation));
        Assert.False(double.IsInfinity(trend.StabilityScorePercent));
    }

    [Theory]
    [InlineData(-10)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(14)]
    [InlineData(30)]
    [InlineData(50)]
    [InlineData(100)]
    [InlineData(int.MaxValue)]
    public void StabilityTrend_DaysParameterBoundaries_NeverThrowOrProduceInvalidState(int days)
    {
        var service = new ThermalHealthService();
        var history = new List<DailyThermalSummary>();
        for (int i = 0; i < 50; i++)
        {
            history.Add(new DailyThermalSummary
            {
                Date = $"2026-10-{(i + 1):D2}",
                ThermalResistanceRatio = 1.95 + (i * 0.001)
            });
        }
        service.SetDailyHistoryForTesting(history);

        var trend = service.GetStabilityTrend(days);

        Assert.NotNull(trend);
        Assert.InRange(trend.DaysCount, 1, 50);
        Assert.InRange(trend.StabilityScorePercent, 10.0, 100.0);
        Assert.False(double.IsNaN(trend.StabilityScorePercent));
        Assert.False(double.IsInfinity(trend.StabilityScorePercent));
    }

    [Fact]
    public void StabilityTrend_ZeroAndNegativeRatios_SafelyGuardedAgainstDivisionByZero()
    {
        var service = new ThermalHealthService();
        var history = new List<DailyThermalSummary>
        {
            new() { Date = "2026-11-01", ThermalResistanceRatio = 0.0 },
            new() { Date = "2026-11-02", ThermalResistanceRatio = 0.0 }
        };
        service.SetDailyHistoryForTesting(history);

        var trend = service.GetStabilityTrend(30);

        // Days with ratio 0 have no load measurement and are skipped, so there is nothing to divide by.
        Assert.Equal(0, trend.DaysCount);
        Assert.Equal(0.0, trend.AverageRatio);
        // CV calculation checks meanY > 0, so meanY = 0 returns 0.0 instead of NaN
        Assert.Equal(0.0, trend.CoefficientOfVariation);
        Assert.False(double.IsNaN(trend.StabilityScorePercent));
        Assert.False(double.IsInfinity(trend.StabilityScorePercent));
        Assert.InRange(trend.StabilityScorePercent, 10.0, 100.0);
    }

    // =========================================================================
    // 2. STARTUP ARGUMENT PARSER CHALLENGE TESTS
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
