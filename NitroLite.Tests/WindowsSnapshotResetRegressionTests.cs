using NitroLite.Core.Hardware;
using NitroLite.Windows.Core.Hardware.Snapshot;
using Xunit;

namespace NitroLite.Tests;

public class WindowsSnapshotResetRegressionTests
{
    [Theory]
    [InlineData("AN16-61", "")]
    [InlineData("", "Nitro AN16-61")]
    public async Task CaptureStopsBeforeAnyHardwareSourceRuns(string code, string model)
    {
        var identity = new DeviceIdentity { ModelCode = code, Model = model };
        var error = await Assert.ThrowsAsync<NotSupportedException>(
            () => WindowsHardwareSnapshot.CaptureAsync(identity, null));
        Assert.Contains("AN16-61", error.Message);
    }
}
