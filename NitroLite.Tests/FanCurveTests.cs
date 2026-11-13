using System.Text.Json;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The software fan curve: the shape it draws, the speed it asks for, and the moment it is allowed to write.
/// All of it is pure arithmetic, so it is tested without a laptop, a service or a timer.
/// </summary>
public class FanCurveTests
{
    private static FanCurve TwoPointCurve(int hysteresisC = 3, int minDwellSeconds = 10) =>
        new(new[] { new FanCurvePoint(50, 40), new FanCurvePoint(80, 100) }, hysteresisC, minDwellSeconds);

    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    // ---------------------------------------------------------------- shape

    [Fact]
    public void DefaultCurveIsQuietAtIdleAndFullAtNinety()
    {
        var curve = new FanCurve(FanCurve.DefaultPoints());
        Assert.Equal(FanCurve.MinPercent, curve.PercentAt(40));
        Assert.Equal(FanCurve.MaxPercent, curve.PercentAt(90));
        Assert.Equal(FanCurve.AnchorCount, curve.Points.Count);
    }

    [Theory]
    [InlineData(20, 30)]   // below the first anchor: held flat
    [InlineData(40, 30)]
    [InlineData(55, 34)]   // halfway between 50/32 and 60/36
    [InlineData(75, 52)]   // halfway between 70/42 and 80/62
    [InlineData(90, 100)]
    [InlineData(120, 100)] // above the last anchor: held flat
    public void PercentAtInterpolatesAndHoldsTheEnds(double temperatureC, int expected)
    {
        var curve = new FanCurve(FanCurve.DefaultPoints());
        Assert.Equal(expected, curve.PercentAt(temperatureC));
    }

    [Fact]
    public void PercentAtRoundsToTheNearestWholePercent()
    {
        var curve = new FanCurve(FanCurve.DefaultPoints());
        // 44 °C sits two fifths of the way from 30 % to 32 %.
        Assert.Equal(31, curve.PercentAt(44));
    }

    [Fact]
    public void PercentAtNeverLeavesTheRangeTheServiceAccepts()
    {
        var curve = new FanCurve(new[] { new FanCurvePoint(40, 0), new FanCurvePoint(90, 400) });
        Assert.Equal(FanCurve.MinPercent, curve.PercentAt(40));
        Assert.Equal(FanCurve.MaxPercent, curve.PercentAt(90));
        Assert.InRange(curve.PercentAt(65), FanCurve.MinPercent, FanCurve.MaxPercent);
    }

    [Fact]
    public void SanitizeSortsClampsAndCollapsesDuplicateTemperatures()
    {
        var cleaned = FanCurve.Sanitize(new[]
        {
            new FanCurvePoint(90, 100),
            new FanCurvePoint(40, 10),
            new FanCurvePoint(40, 35),
            new FanCurvePoint(500, 500)
        });

        Assert.Equal(new[] { 40, 90, 100 }, cleaned.Select(p => p.TemperatureC));
        Assert.Equal(35, cleaned[0].Percent);   // the last anchor written for 40 °C wins
        Assert.Equal(FanCurve.MaxPercent, cleaned[2].Percent);
    }

    [Fact]
    public void SanitizeFallsBackToTheDefaultShapeWhenNothingUsableIsLeft()
    {
        Assert.Equal(FanCurve.AnchorCount, FanCurve.Sanitize(null).Count);
        Assert.Equal(FanCurve.AnchorCount, FanCurve.Sanitize(Array.Empty<FanCurvePoint>()).Count);
    }

    [Fact]
    public void HysteresisAndDwellAreKeptInsideTheirOwnRange()
    {
        var curve = new FanCurve(FanCurve.DefaultPoints(), hysteresisC: 99, minDwellSeconds: -5);
        Assert.Equal(15, curve.HysteresisC);
        Assert.Equal(0, curve.MinDwellSeconds);
    }

    // ---------------------------------------------------------------- when to write

    [Fact]
    public void FirstEvaluationAlwaysWrites()
    {
        var evaluator = new FanCurveEvaluator(TwoPointCurve());
        Assert.Null(evaluator.AppliedPercent);
        Assert.Equal(70, evaluator.Evaluate(65, T0));
        Assert.Equal(70, evaluator.AppliedPercent);
    }

    [Fact]
    public void AnUnchangedTargetIsNotWrittenAgain()
    {
        var evaluator = new FanCurveEvaluator(TwoPointCurve());
        evaluator.Evaluate(65, T0);
        Assert.Null(evaluator.Evaluate(65, T0.AddSeconds(1)));
        // 65.1 °C still rounds to the same 70 %: a sensor wobble must not reach the fans.
        Assert.Null(evaluator.Evaluate(65.1, T0.AddSeconds(2)));
    }

    [Fact]
    public void ARiseIsWrittenImmediately()
    {
        var evaluator = new FanCurveEvaluator(TwoPointCurve());
        evaluator.Evaluate(65, T0);
        // One second later, well inside the dwell time: heat is the emergency, so it still goes out.
        Assert.Equal(72, evaluator.Evaluate(66, T0.AddSeconds(1)));
    }

    [Fact]
    public void AFallInsideTheHysteresisBandIsHeld()
    {
        var evaluator = new FanCurveEvaluator(TwoPointCurve());
        evaluator.Evaluate(66, T0);
        // 64 °C is only 2 °C below the 66 °C that produced 72 %: inside the 3 °C band, so nothing moves.
        Assert.Null(evaluator.Evaluate(64, T0.AddSeconds(60)));
        Assert.Equal(72, evaluator.AppliedPercent);
    }

    [Fact]
    public void AFallOutsideTheBandWaitsForTheDwellTime()
    {
        var evaluator = new FanCurveEvaluator(TwoPointCurve());
        evaluator.Evaluate(66, T0);
        // 62 °C is 4 °C below: outside the band, but only 5 s have passed.
        Assert.Null(evaluator.Evaluate(62, T0.AddSeconds(5)));
        Assert.Equal(72, evaluator.AppliedPercent);
        // The same reading after the dwell time is written.
        Assert.Equal(64, evaluator.Evaluate(62, T0.AddSeconds(11)));
    }

    [Fact]
    public void AMissingOrImpossibleReadingNeverChangesTheSpeed()
    {
        var evaluator = new FanCurveEvaluator(TwoPointCurve());
        evaluator.Evaluate(65, T0);
        Assert.Null(evaluator.Evaluate(0, T0.AddSeconds(60)));
        Assert.Null(evaluator.Evaluate(-1, T0.AddSeconds(60)));
        Assert.Null(evaluator.Evaluate(double.NaN, T0.AddSeconds(60)));
        Assert.Null(evaluator.Evaluate(double.PositiveInfinity, T0.AddSeconds(60)));
        Assert.Equal(70, evaluator.AppliedPercent);
    }

    [Fact]
    public void ResetMakesTheNextEvaluationWriteAgain()
    {
        var evaluator = new FanCurveEvaluator(TwoPointCurve());
        evaluator.Evaluate(65, T0);
        evaluator.Reset();
        Assert.Null(evaluator.AppliedPercent);
        Assert.Equal(70, evaluator.Evaluate(65, T0.AddSeconds(1)));
    }

    [Fact]
    public void AZeroDwellTimeLetsTheFansSlowDownOnTheNextPoll()
    {
        var evaluator = new FanCurveEvaluator(TwoPointCurve(minDwellSeconds: 0));
        evaluator.Evaluate(66, T0);
        Assert.Equal(64, evaluator.Evaluate(62, T0));
    }

    // ---------------------------------------------------------------- settings

    [Fact]
    public void SettingsRoundTripTheCurve()
    {
        var settings = new AppSettings
        {
            FanCurveEnabled = true,
            FanCurvePoints = new List<FanCurvePoint> { new(45, 35), new(85, 95) },
            FanCurveHysteresisC = 5,
            FanCurveMinDwellSeconds = 20
        };

        var json = JsonSerializer.Serialize(settings);
        var back = JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.True(back.FanCurveEnabled);
        Assert.Equal(2, back.FanCurvePoints!.Count);
        Assert.Equal(45, back.FanCurvePoints[0].TemperatureC);
        Assert.Equal(95, back.FanCurvePoints[1].Percent);
        Assert.Equal(5, back.FanCurveHysteresisC);
        Assert.Equal(20, back.FanCurveMinDwellSeconds);
    }

    [Fact]
    public void AFreshInstallStartsWithTheCurveOffAndTheDefaultShape()
    {
        var settings = new AppSettings();
        Assert.False(settings.FanCurveEnabled);
        Assert.Equal(FanCurve.AnchorCount, settings.FanCurvePoints!.Count);
        Assert.Equal(FanCurve.DefaultHysteresisC, settings.FanCurveHysteresisC);
        Assert.Equal(FanCurve.DefaultMinDwellSeconds, settings.FanCurveMinDwellSeconds);
    }

    [Fact]
    public void NormalizeRepairsACurveThatCameOutOfAHandEditedFile()
    {
        var settings = new AppSettings
        {
            FanCurvePoints = new List<FanCurvePoint> { new(500, 500), new(10, 1) },
            FanCurveHysteresisC = 99,
            FanCurveMinDwellSeconds = -3
        };

        settings.Normalize();

        Assert.Equal(new[] { 30, 100 }, settings.FanCurvePoints!.Select(p => p.TemperatureC));
        Assert.All(settings.FanCurvePoints, p => Assert.InRange(p.Percent, FanCurve.MinPercent, FanCurve.MaxPercent));
        Assert.Equal(15, settings.FanCurveHysteresisC);
        Assert.Equal(0, settings.FanCurveMinDwellSeconds);
    }

    [Fact]
    public void NormalizeReplacesAMissingCurveWithTheDefaultShape()
    {
        var settings = new AppSettings { FanCurvePoints = null };
        settings.Normalize();
        Assert.Equal(FanCurve.AnchorCount, settings.FanCurvePoints!.Count);
    }

    [Fact]
    public void BuildFanCurveUsesTheSavedValues()
    {
        var settings = new AppSettings
        {
            FanCurvePoints = new List<FanCurvePoint> { new(50, 40), new(80, 100) },
            FanCurveHysteresisC = 4,
            FanCurveMinDwellSeconds = 15
        };

        var curve = settings.BuildFanCurve();
        Assert.Equal(4, curve.HysteresisC);
        Assert.Equal(15, curve.MinDwellSeconds);
        Assert.Equal(70, curve.PercentAt(65));
    }
}
