using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Core.Hardware.Wmi;

namespace NitroLite.Windows.Core.Hardware;

/// <summary>Read-only diagnostic for the NitroSense 3 PSSvc interface.</summary>
public static class LegacySenseSensors
{
    public static async Task<int> RunAsync(TextWriter output)
    {
        var complete = true;
        foreach (var (id, name) in new (byte, string)[] {
            (1, "CPU temperature (C)"), (2, "CPU fan (RPM)"),
            (6, "GPU fan (RPM)"), (10, "GPU temperature (C)") })
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                var reading = await ReadAsync(id, timeout.Token).ConfigureAwait(false);
                if (!reading.HasValue) complete = false;
                await output.WriteLineAsync($"{name}: {reading?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable"}").ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException or TimeoutException)
            {
                await output.WriteLineAsync($"PSSvc: {ex.GetType().Name} ({name})").ConfigureAwait(false);
                return 1;
            }
        }
        return complete ? 0 : 1;
    }

    private static async Task<int?> ReadAsync(byte id, CancellationToken token)
    {
        using var pipe = new NamedPipeClientStream(".", "PredatorSense_service_namedpipe",
            PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(token).ConfigureAwait(false);
        await pipe.WriteAsync(Request(id), token).ConfigureAwait(false);
        var reply = new byte[13];
        await pipe.ReadExactlyAsync(reply, token).ConfigureAwait(false);
        return Decode(id, reply);
    }

    public static Task<MonitorData?> ReadTelemetryAsync() => ReadTelemetryAsync(ReadAsync);

    internal static async Task<MonitorData?> ReadTelemetryAsync(Func<byte, CancellationToken, Task<int?>> read)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
        var data = new MonitorData { IsTemperatureMeasured = false };
        try
        {
            foreach (byte id in new byte[] { 1, 2, 6, 10 })
            {
                var value = await read(id, timeout.Token).ConfigureAwait(false);
                if (value is not int n) continue;
                switch (id)
                {
                    case 1: data.CpuTemperature = n; data.Provenance[nameof(data.CpuTemperature)] = "PSSvc"; break;
                    case 2: data.CpuFanSpeed = n; data.Provenance[nameof(data.CpuFanSpeed)] = "PSSvc"; break;
                    case 6: data.GpuFanSpeed = n; data.Provenance[nameof(data.GpuFanSpeed)] = "PSSvc"; break;
                    case 10: data.GpuTemperature = n; data.Provenance[nameof(data.GpuTemperature)] = "PSSvc"; break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException or TimeoutException)
        {
            // A service can stop between replies. Keep only the readings it actually returned.
        }
        data.IsFanRpmMeasured = data.Provenance.ContainsKey(nameof(data.CpuFanSpeed)) &&
                                data.Provenance.ContainsKey(nameof(data.GpuFanSpeed));

        // The service answers each sensor by its own id, so a temperature it returned is a measurement, not a guess
        // about which id means what. Left false, the fan curve, the manual floor and the 95 °C safety floor all refused
        // to act on it, and with a real reading above zero the lost-sensor hand-back never fired either: the fans stayed
        // at whatever was last written for as long as the dashboard ran on this fallback.
        data.IsTemperatureMeasured = data.Provenance.ContainsKey(nameof(data.CpuTemperature)) &&
                                     data.CpuTemperature > 0;
        return data.Provenance.Count == 0 ? null : data;
    }

    internal static byte[] Request(byte id)
    {
        byte[] frame = { 13, 0, 1, 4, 0, 0, 0, 0, 0, 0, 0 };
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(7), AcerWmiProtocol.EncodeSysInfoQuery(id));
        return frame;
    }

    internal static int? Decode(byte id, ReadOnlySpan<byte> frame)
    {
        if (frame.Length != 13 || frame[5] != 0) return null;
        var value = AcerWmiProtocol.DecodeSysInfoValue(BinaryPrimitives.ReadUInt64LittleEndian(frame[5..]));
        return id switch
        {
            1 or 10 when SensorPlausibility.IsPlausibleTemperature(value) => value,
            2 or 6 when SensorPlausibility.IsPlausibleFanRpm(value) => value,
            _ => null
        };
    }
}
