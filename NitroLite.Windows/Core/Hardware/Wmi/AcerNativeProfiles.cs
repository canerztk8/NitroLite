using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;

namespace NitroLite.Core.Hardware.Wmi;

internal static class AcerNativeProfiles
{
    internal static IReadOnlyList<OperatingMode> Modes { get; } =
        Array.AsReadOnly(new[] { OperatingMode.Eco, OperatingMode.Default, OperatingMode.Extreme, OperatingMode.Turbo });

    // AcerAgentService 5.2.84 uses these two requests to AcerHardwareService for its operating-mode API.
    internal static byte[] Request(OperatingMode? mode)
    {
        if (mode == null) return [0x14, 0, 0];
        if (!Modes.Contains(mode.Value)) throw new ArgumentOutOfRangeException(nameof(mode));
        byte[] frame = [0x15, 0, 1, 4, 0, 0, 0, 0, 0, 0, 0];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(7), (int)mode.Value);
        return frame;
    }

    private static async Task<ulong?> ExchangeAsync(OperatingMode? mode)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            using var pipe = new NamedPipeClientStream(".", "predatorsense_hardware_service_",
                PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            await pipe.ConnectAsync(400, timeout.Token).ConfigureAwait(false);
            await pipe.WriteAsync(Request(mode), timeout.Token).ConfigureAwait(false);
            int length = mode.HasValue ? 4 : 8;
            var reply = new byte[5 + length];
            await pipe.ReadExactlyAsync(reply, timeout.Token).ConfigureAwait(false);
            if (reply[0] != 1 || BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(1)) != length) return null;
            return mode.HasValue ? BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(5))
                : BinaryPrimitives.ReadUInt64LittleEndian(reply.AsSpan(5));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or OperationCanceledException)
        {
            return null;
        }
    }

    internal static async Task<OperatingMode?> ReadAsync() =>
        await ExchangeAsync(null).ConfigureAwait(false) is { } raw ? AcerWmiProtocol.DecodeOperatingMode(raw) : null;

    internal static async Task<bool> SetAsync(OperatingMode mode)
    {
        if (!Modes.Contains(mode) || await ReadAsync().ConfigureAwait(false) == null) return false;
        if (await ExchangeAsync(mode).ConfigureAwait(false) != 0) return false;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (await ReadAsync().ConfigureAwait(false) == mode) return true;
            if (attempt < 2) await Task.Delay(150).ConfigureAwait(false);
        }
        return false;
    }
}
