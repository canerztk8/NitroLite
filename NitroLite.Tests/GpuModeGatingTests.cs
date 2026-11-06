using NitroLite;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

public class GpuModeGatingTests
{
    // Live AN16-41 values: SUPPORT_GPU_MODE = 0, SUPPORT_GPU_MODE_CAPABILITY = 7, and NitroSense still shows
    // all three options. The capability bitmask alone decides, exactly like NitroSense's updateGPU_Mode().
    [Theory]
    [InlineData(GpuOperatingMode.Optimus)]
    [InlineData(GpuOperatingMode.DiscreteGpu)]
    [InlineData(GpuOperatingMode.Automatic)]
    public void Capability7_AllowsAllThreeModes(GpuOperatingMode target)
    {
        Assert.True(MainWindow.CanSwitchGpuMode(7, target));
    }

    [Fact]
    public void Capability3_AllowsOptimusAndDiscreteOnly()
    {
        Assert.True(MainWindow.CanSwitchGpuMode(3, GpuOperatingMode.Optimus));
        Assert.True(MainWindow.CanSwitchGpuMode(3, GpuOperatingMode.DiscreteGpu));
        Assert.False(MainWindow.CanSwitchGpuMode(3, GpuOperatingMode.Automatic));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void UnknownOrZeroCapability_NeverAllowsSwitch(int? capability)
    {
        Assert.False(MainWindow.CanSwitchGpuMode(capability, GpuOperatingMode.Optimus));
        Assert.False(MainWindow.CanSwitchGpuMode(capability, GpuOperatingMode.DiscreteGpu));
    }
}
