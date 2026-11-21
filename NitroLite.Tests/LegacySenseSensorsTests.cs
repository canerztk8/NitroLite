using NitroLite.Windows.Core.Hardware;
using NitroLite.Core;
using NitroLite.Core.Hardware.Wmi;
using Xunit;

namespace NitroLite.Tests;

public class LegacySenseSensorsTests
{
    [Fact]
    public async Task DashboardFallsBackOnMissingPrimaryAndKeepsPartialReadings()
    {
        var agent = new Phase3Fakes.StubHardware();
        int queries = 0;
        Task<int?> Read(byte id, CancellationToken token)
        {
            queries++;
            return Task.FromResult<int?>(id switch { 1 => 57, 2 => 2300, _ => null });
        }
        using var controller = new HybridHardwareController(agent,
            legacyTelemetry: () => LegacySenseSensors.ReadTelemetryAsync(Read));
        Assert.Same(agent.Sample, await controller.GetTelemetryAsync());
        Assert.Equal(0, queries);
        agent.Sample = null!;
        var sample = await controller.GetTelemetryAsync();
        Assert.NotNull(sample);
        Assert.Equal(4, queries);
        Assert.Equal(57, sample.CpuTemperature);
        Assert.Equal(2300, sample.CpuFanSpeed);
        // A temperature the service returned by its own id is a measurement: the fan curve and the 95 °C floor may act
        // on it. The fan RPM is not, because only one of the two tachometers answered.
        Assert.True(sample.IsTemperatureMeasured);
        Assert.False(sample.IsFanRpmMeasured);
    }

    [Fact]
    public async Task MissingServiceIsNotPolledEveryTick()
    {
        var agent = new Phase3Fakes.StubHardware { Sample = null! };
        int attempts = 0;
        using var controller = new HybridHardwareController(agent, legacyTelemetry: () =>
        {
            attempts++;
            return Task.FromResult<MonitorData?>(null);
        });
        Assert.Null(await controller.GetTelemetryAsync());
        Assert.Null(await controller.GetTelemetryAsync());
        Assert.Equal(1, attempts);
    }
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
