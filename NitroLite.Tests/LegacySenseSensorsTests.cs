using NitroLite.Windows.Core.Hardware;
using Xunit;

namespace NitroLite.Tests;

public class LegacySenseSensorsTests
{
    [Fact]
    public void CpuFanQueryAndDocumentedGetterLayout()
    {
        Assert.Equal(Convert.FromHexString("0D00010400000001020000"), LegacySenseSensors.Request(2));
        // Synthetic frame: 2400 RPM with status 0, in the documented u64 getter slot.
        var frame = Convert.FromHexString("00000000000060090000000000");
        Assert.Equal(2400, LegacySenseSensors.Decode(2, frame));
        Assert.Null(LegacySenseSensors.Decode(1, frame));
        Assert.Null(LegacySenseSensors.Decode(2, frame.AsSpan(0, 9)));
        frame[5] = 1;
        Assert.Null(LegacySenseSensors.Decode(2, frame));
    }
}
