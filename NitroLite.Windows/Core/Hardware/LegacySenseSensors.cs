using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;
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
            using var pipe = new NamedPipeClientStream(".", "PredatorSense_service_namedpipe",
                PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            try
            {
                await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
                await pipe.WriteAsync(Request(id), timeout.Token).ConfigureAwait(false);
                var reply = new byte[13];
                await pipe.ReadExactlyAsync(reply, timeout.Token).ConfigureAwait(false);
                var reading = Decode(id, reply);
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
