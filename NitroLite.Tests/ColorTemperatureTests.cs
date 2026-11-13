using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// VisionCare: the display white point written through the gamma ramp. The colour maths is pure, so it is tested
/// without a screen; the write itself is tested through a double that records what would have been sent to the
/// driver, because a test must never tint the machine it runs on.
/// </summary>
public class ColorTemperatureTests
{
    /// <summary>Stands in for the GDI ramp so a test never touches the real display.</summary>
    private sealed class RecordingColorTemperatureService : ColorTemperatureService
    {
        public List<int> Applied { get; } = new();
        public int RestoreCount { get; private set; }
        public int ResetCount { get; private set; }
        public bool Accept { get; set; } = true;

        public override bool Apply(int kelvin)
        {
            Applied.Add(kelvin);
            if (!Accept) return false;
            IsApplied = true;
            CurrentKelvin = kelvin;
            return true;
        }

        public override void ResetToNeutral()
        {
            ResetCount++;
            IsApplied = false;
            CurrentKelvin = NeutralKelvin;
        }

        public override void Restore()
        {
            RestoreCount++;
            IsApplied = false;
            CurrentKelvin = NeutralKelvin;
        }
    }

    // ---------------------------------------------------------------- colour maths

    [Fact]
    public void NeutralTemperatureIsExactlyNoTint()
    {
        Assert.Equal((1.0, 1.0, 1.0), ColorTemperatureService.MultipliersFor(ColorTemperatureService.NeutralKelvin));
    }

    [Fact]
    public void AboveNeutralIsAlsoNoTint()
    {
        // The slider cannot go there, but a hand-edited settings file can, and it must not produce a blue screen.
        Assert.Equal((1.0, 1.0, 1.0), ColorTemperatureService.MultipliersFor(9000));
    }

    [Theory]
    [InlineData(1900)]
    [InlineData(2700)]
    [InlineData(3400)]
    [InlineData(4500)]
    [InlineData(5500)]
    [InlineData(6400)]
    public void WarmerTemperaturesTakeBlueOutAndKeepRed(int kelvin)
    {
        var (r, g, b) = ColorTemperatureService.MultipliersFor(kelvin);

        Assert.True(r >= g, $"{kelvin} K: red {r} should not be below green {g}");
        Assert.True(g >= b, $"{kelvin} K: green {g} should not be below blue {b}");
        Assert.True(b < 1.0, $"{kelvin} K: blue should be reduced, was {b}");
        Assert.Equal(1.0, r, 6); // normalised so the brightest channel is untouched
    }

    [Fact]
    public void LowerTemperatureIsLessBlue()
    {
        double blueAt2700 = ColorTemperatureService.MultipliersFor(2700).B;
        double blueAt4500 = ColorTemperatureService.MultipliersFor(4500).B;
        Assert.True(blueAt2700 < blueAt4500, $"{blueAt2700} should be below {blueAt4500}");
    }

    [Fact]
    public void BelowTheMinimumIsClampedToTheMinimum()
    {
        Assert.Equal(ColorTemperatureService.MultipliersFor(ColorTemperatureService.MinKelvin),
            ColorTemperatureService.MultipliersFor(500));
    }

    [Fact]
    public void MultipliersStayInsideTheRampRange()
    {
        for (int kelvin = ColorTemperatureService.MinKelvin; kelvin <= ColorTemperatureService.MaxKelvin; kelvin += 100)
        {
            var (r, g, b) = ColorTemperatureService.MultipliersFor(kelvin);
            foreach (double channel in new[] { r, g, b })
            {
                Assert.InRange(channel, 0.0, 1.0);
            }
        }
    }

    // ---------------------------------------------------------------- the ramp itself

    [Fact]
    public void NeutralRampIsTheIdentityWindowsWritesItself()
    {
        var ramp = ColorTemperatureService.BuildGammaRamp(ColorTemperatureService.NeutralKelvin);

        Assert.Equal(768, ramp.Length);
        for (int i = 0; i < 256; i++)
        {
            Assert.Equal((ushort)(i * 257), ramp[i]);
            Assert.Equal((ushort)(i * 257), ramp[256 + i]);
            Assert.Equal((ushort)(i * 257), ramp[512 + i]);
        }
    }

    [Fact]
    public void WarmRampLowersBlueOnly()
    {
        var ramp = ColorTemperatureService.BuildGammaRamp(3000);

        Assert.Equal((ushort)(255 * 257), ramp[255]);        // red untouched at full scale
        Assert.True(ramp[512 + 255] < 255 * 257);            // blue pulled down
        Assert.True(ramp[512 + 128] < ramp[128]);            // and down at mid grey too
    }

    [Fact]
    public void EveryChannelRisesWithInput()
    {
        var ramp = ColorTemperatureService.BuildGammaRamp(2700);

        for (int channel = 0; channel < 3; channel++)
        {
            int offset = channel * 256;
            for (int i = 1; i < 256; i++)
            {
                Assert.True(ramp[offset + i] >= ramp[offset + i - 1],
                    $"channel {channel} fell at {i}: {ramp[offset + i - 1]} -> {ramp[offset + i]}");
            }
        }
    }

    [Fact]
    public void RampIsClampedForATemperatureOutsideTheSlider()
    {
        Assert.Equal(ColorTemperatureService.BuildGammaRamp(ColorTemperatureService.MinKelvin),
            ColorTemperatureService.BuildGammaRamp(1000));
        Assert.Equal(ColorTemperatureService.BuildGammaRamp(ColorTemperatureService.NeutralKelvin),
            ColorTemperatureService.BuildGammaRamp(20000));
    }

    // ---------------------------------------------------------------- settings

    [Fact]
    public void SettingsDefaultToOffAndNeutral()
    {
        var settings = new AppSettings();

        Assert.False(settings.ColorTemperatureEnabled);
        Assert.Equal(ColorTemperatureService.NeutralKelvin, settings.ColorTemperatureKelvin);
    }

    [Theory]
    [InlineData(0, ColorTemperatureService.MinKelvin)]
    [InlineData(-4000, ColorTemperatureService.MinKelvin)]
    [InlineData(3000, 3000)]
    [InlineData(6500, ColorTemperatureService.NeutralKelvin)]
    [InlineData(12000, ColorTemperatureService.MaxKelvin)]
    public void NormalizeClampsTheTemperatureToTheSlider(int stored, int expected)
    {
        var settings = new AppSettings { ColorTemperatureKelvin = stored };

        settings.Normalize();

        Assert.Equal(expected, settings.ColorTemperatureKelvin);
    }

    // ---------------------------------------------------------------- apply / restore

    [Fact]
    public void ApplyRecordsTheTemperatureAndRestorePutsItBack()
    {
        var service = new RecordingColorTemperatureService();

        Assert.True(service.Apply(3400));
        Assert.Equal(new[] { 3400 }, service.Applied);

        service.Restore();
        Assert.Equal(1, service.RestoreCount);
    }

    [Fact]
    public void DisposeRestoresTheRamp()
    {
        var service = new RecordingColorTemperatureService();
        service.Apply(2700);

        service.Dispose();

        Assert.Equal(1, service.RestoreCount);
    }

    [Fact]
    public void ARefusedRampIsReportedAsFalse()
    {
        var service = new RecordingColorTemperatureService { Accept = false };

        Assert.False(service.Apply(3000));
    }

    [Fact]
    public void RealServiceStartsNeutralAndNotApplied()
    {
        // No Apply call here: constructing the service must not touch the display.
        using var service = new ColorTemperatureService();

        Assert.Equal(ColorTemperatureService.NeutralKelvin, service.CurrentKelvin);
        Assert.False(service.IsApplied);
    }

    [Fact]
    public void RestoringWithoutApplyingIsHarmless()
    {
        using var service = new ColorTemperatureService();

        service.Restore();
        service.Restore();

        Assert.Equal(ColorTemperatureService.NeutralKelvin, service.CurrentKelvin);
        Assert.False(service.IsApplied);
    }

    [Fact]
    public void ResetToNeutralForgetsTheRampItWouldHaveRestored()
    {
        // The startup path: a tint left by a crash must not become the ramp Restore puts back.
        var service = new RecordingColorTemperatureService();
        service.Apply(2700);
        Assert.True(service.IsApplied);

        service.ResetToNeutral();

        Assert.Equal(1, service.ResetCount);
        Assert.Equal(ColorTemperatureService.NeutralKelvin, service.CurrentKelvin);
        Assert.False(service.IsApplied);
    }
}
