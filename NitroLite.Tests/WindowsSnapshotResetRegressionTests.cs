using NitroLite.Core.Hardware;
using NitroLite.Windows.Core.Hardware.Snapshot;
using Xunit;

namespace NitroLite.Tests;

public class WindowsSnapshotResetRegressionTests
{
    [Theory]
    [InlineData("AN16-61", "")]
    [InlineData("", "Nitro AN16-61")]
    [InlineData("AN16-61-R123", "")]
    public async Task ProtectedModelUsesOnlyWindowsDisplayRoute(string code, string model)
    {
        var identity = new DeviceIdentity { ModelCode = code, Model = model };
        try
        {
            var snapshot = await WindowsHardwareSnapshot.CaptureAsync(identity, null);
            Assert.True(snapshot.IsDisplayRouteOnly);
            Assert.Equal("internalPanelAdapter", Assert.Single(snapshot.Entries).Key);
        }
        catch (InvalidOperationException ex)
        {
            // CI can run without an active internal display.
            Assert.Contains("Windows could not identify the active internal display adapter", ex.Message);
        }
    }
}
