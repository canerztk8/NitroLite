using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Core.Hardware.Wmi;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The operating mode through the Windows power mode, for the model whose firmware refuses Acer's mode command
/// (Nitro Lite 16, issue 11) — and only for it.
/// </summary>
public class WindowsPowerModeTests
{
    private sealed class FakePowerMode : IWindowsPowerMode
    {
        public WindowsPowerSlider? Position { get; set; } = WindowsPowerSlider.Balanced;
        public bool Refuse { get; set; }
        public List<WindowsPowerSlider> Writes { get; } = new();

        public WindowsPowerSlider? Read() => Position;

        public bool Write(WindowsPowerSlider slider)
        {
            Writes.Add(slider);
            if (Refuse) return false;
            Position = slider;
            return true;
        }
    }

    [Theory]
    [InlineData(OperatingMode.Quiet, WindowsPowerSlider.BestEfficiency)]
    [InlineData(OperatingMode.Eco, WindowsPowerSlider.BestEfficiency)]
    [InlineData(OperatingMode.Default, WindowsPowerSlider.Balanced)]
    [InlineData(OperatingMode.Extreme, WindowsPowerSlider.BestPerformance)]
    [InlineData(OperatingMode.Turbo, WindowsPowerSlider.BestPerformance)]
    public void EachModeMapsToAPosition(OperatingMode mode, WindowsPowerSlider slider) =>
        Assert.Equal(slider, WindowsPowerModeMapping.For(mode));

    [Fact]
    public void APositionReadsBackAsTheModeThatAskedForIt()
    {
        Assert.Equal(OperatingMode.Turbo, WindowsPowerModeMapping.ToMode(WindowsPowerSlider.BestPerformance, OperatingMode.Turbo));
        Assert.Equal(OperatingMode.Extreme, WindowsPowerModeMapping.ToMode(WindowsPowerSlider.BestPerformance, null));
        // Changed in Windows Settings since: the position wins over a stale request.
        Assert.Equal(OperatingMode.Quiet, WindowsPowerModeMapping.ToMode(WindowsPowerSlider.BestEfficiency, OperatingMode.Turbo));
        Assert.Null(WindowsPowerModeMapping.ToMode(null, OperatingMode.Turbo));
    }

    [Fact]
    public async Task NativeAcerProfilesNeverWriteTheWindowsOverlay()
    {
        var power = new FakePowerMode();
        using var hybrid = new HybridHardwareController(new Phase3Fakes.StubHardware(),
            serviceHealthy: () => Task.FromResult(true))
        {
            UseAcerNativeProfiles = () => true,
            ModesThroughWindows = () => true,
            WindowsPowerMode = power
        };
        var caps = await hybrid.GetCapabilitiesAsync();
        Assert.False(caps.UsesWindowsPowerMode);
        Assert.Equal(new[] { OperatingMode.Eco, OperatingMode.Default, OperatingMode.Extreme, OperatingMode.Turbo },
            caps.SupportedOperatingModes);
        Assert.True(await hybrid.SetOperatingModeAsync(OperatingMode.Default));
        Assert.False(await hybrid.SetOperatingModeAsync(OperatingMode.Turbo)); // stub still reports Balanced
        Assert.Empty(power.Writes);
        Assert.Equal(new byte[] { 0x14, 0, 0 }, AcerNativeProfiles.Request(null));
        Assert.Equal(new byte[] { 0x15, 0, 1, 4, 0, 0, 0, 5, 0, 0, 0 }, AcerNativeProfiles.Request(OperatingMode.Turbo));
    }

    [Fact]
    public async Task AFlaggedModel_SwitchesTheWindowsPowerMode_AndNeverTheFirmware()
    {
        var agent = new Phase3Fakes.StubHardware();
        var power = new FakePowerMode();
        using var hybrid = new HybridHardwareController(agent)
        {
            WindowsPowerMode = power,
            ModesThroughWindows = () => true
        };

        Assert.True(await hybrid.SetOperatingModeAsync(OperatingMode.Turbo));

        Assert.Equal(new[] { WindowsPowerSlider.BestPerformance }, power.Writes);
        Assert.DoesNotContain(agent.Writes, w => w.StartsWith("mode:", StringComparison.Ordinal));
        Assert.Equal(OperatingMode.Turbo, await hybrid.GetOperatingModeAsync());

        var caps = await hybrid.GetCapabilitiesAsync();
        Assert.True(caps.OperatingModeControl);
        Assert.Equal(WindowsPowerModeMapping.Modes, caps.SupportedOperatingModes);
    }

    [Fact]
    public async Task ANormalNitro_KeepsWritingItsModeToTheFirmware()
    {
        // The user's question: a laptop with a real Turbo mode must not be moved to the Windows setting.
        var agent = new Phase3Fakes.StubHardware();
        var power = new FakePowerMode();
        using var hybrid = new HybridHardwareController(agent)
        {
            WindowsPowerMode = power,
            ModesThroughWindows = () => false
        };

        Assert.True(await hybrid.SetOperatingModeAsync(OperatingMode.Turbo));

        Assert.Contains("mode:Turbo", agent.Writes);
        Assert.Empty(power.Writes);
        Assert.Contains(OperatingMode.Turbo, (await hybrid.GetCapabilitiesAsync()).SupportedOperatingModes);
    }

    [Fact]
    public async Task AWindowsRefusal_IsReportedAsAFailedWrite()
    {
        var power = new FakePowerMode { Refuse = true };
        using var hybrid = new HybridHardwareController(new Phase3Fakes.StubHardware())
        {
            WindowsPowerMode = power,
            ModesThroughWindows = () => true
        };

        Assert.False(await hybrid.SetOperatingModeAsync(OperatingMode.Quiet));
        Assert.Equal(OperatingMode.Default, await hybrid.GetOperatingModeAsync());
    }

    [Fact]
    public async Task NoReadablePowerMode_TheModeControlIsOff()
    {
        using var hybrid = new HybridHardwareController(new Phase3Fakes.StubHardware())
        {
            WindowsPowerMode = new FakePowerMode { Position = null },
            ModesThroughWindows = () => true
        };

        Assert.False((await hybrid.GetCapabilitiesAsync()).OperatingModeControl);
    }

    [Fact]
    public void TheGate_KeepsTheModeControl_AndSaysWhereItWent()
    {
        var profile = new ModelProfile { Id = "x", UnsupportedMethods = new[] { "SetGamingMiscSetting" } };
        var all = new HardwareCapabilities { OperatingModeControl = true, FanControl = true };

        var gated = UnsupportedMethodGate.Apply(all, profile, out var notes);

        Assert.Equal(all, gated);
        Assert.Contains(notes, n => n.Contains("Windows power mode", StringComparison.Ordinal));
    }
}
