using System.Buffers.Binary;
using System.Text;

namespace NitroLite.Core.Hardware.OpenRgb;

/// <summary>One LED colour, in the order the wire carries it.</summary>
public readonly record struct RgbColor(byte R, byte G, byte B)
{
    /// <summary>Parses <c>#RRGGBB</c> or <c>RRGGBB</c>. Anything else is black, which is what an unset LED is.</summary>
    public static RgbColor Parse(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return default;
        var text = hex.Trim().TrimStart('#');
        if (text.Length != 6) return default;
        if (!byte.TryParse(text[..2], System.Globalization.NumberStyles.HexNumber, null, out byte r)) return default;
        if (!byte.TryParse(text[2..4], System.Globalization.NumberStyles.HexNumber, null, out byte g)) return default;
        if (!byte.TryParse(text[4..], System.Globalization.NumberStyles.HexNumber, null, out byte b)) return default;
        return new RgbColor(r, g, b);
    }

    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>One device as OpenRGB describes it, reduced to what a keyboard client needs.</summary>
public sealed record OpenRgbController(
    int Index,
    string Name,
    int LedCount,
    int ZoneCount,
    IReadOnlyList<RgbColor> Colors,
    bool Parsed);

/// <summary>
/// The OpenRGB SDK wire format, protocol versions 0 through <see cref="ClientProtocolVersion"/>.
/// <para>
/// Sources: OpenRGB's <c>Documentation/OpenRGBSDK.md</c>, <c>NetworkProtocol.h</c>, <c>NetworkServer.cpp</c> and
/// <c>RGBController/RGBControllerInterface.h</c> (master branch). Every packet is a 16-byte header — the ASCII magic
/// <c>ORGB</c>, the device index, the packet id and the payload length, each field four bytes little-endian (OpenRGB
/// copies native unsigned ints, and every machine it runs on is little-endian) — followed by that many bytes.
/// </para>
/// <para>
/// Strings are a <c>u16</c> length that counts the terminating NUL, then the bytes and the NUL. Counts are
/// <c>u16</c>. A colour is OpenRGB's <c>RGBColor</c>, an <c>unsigned int</c> laid out <c>0x00BBGGRR</c>, so on the wire
/// it is the four bytes <c>R G B 00</c>. A block that starts with <c>data_size</c> counts those four bytes too:
/// <c>data_size</c> equals the packet's payload length.
/// </para>
/// <para>
/// Only the server answers some packets. <c>SET_CLIENT_NAME</c>, <c>SETCUSTOMMODE</c>, <c>UPDATELEDS</c> and
/// <c>UPDATEZONELEDS</c> get no reply below protocol 6 (6 added an ACK), and this client never asks for 6, so it never
/// waits for one.
/// </para>
/// <para>
/// This is a re-implementation from that description, not a port of OpenRGB's C++. It is deliberately defensive: a
/// payload that does not parse returns null rather than throwing, because the server on the other end is a
/// third-party process the user may be running at any version.
/// </para>
/// </summary>
public static class OpenRgbProtocol
{
    /// <summary>Bytes in front of every payload.</summary>
    public const int HeaderBytes = 16;

    /// <summary>
    /// The highest protocol version this client parses. Version 4 (OpenRGB 0.9) is the newest layout whose controller
    /// description this codec reads: 5 adds zone and controller flags and 6 removes fields, and neither is needed to
    /// light a keyboard. A newer server is asked for 4 and answers in the version-4 layout.
    /// </summary>
    public const uint ClientProtocolVersion = 4;

    /// <summary>Refuse a payload larger than this. A wrong length field must not become a 4 GB allocation.</summary>
    public const int MaxPayloadBytes = 4 * 1024 * 1024;

    /// <summary>Packet ids this client uses or must recognise. The numbers are OpenRGB's (<c>NetworkProtocol.h</c>).</summary>
    public enum PacketType : uint
    {
        RequestControllerCount = 0,
        RequestControllerData = 1,
        Ack = 10,
        RequestProtocolVersion = 40,
        SetClientName = 50,
        SetServerName = 51,
        DeviceListUpdated = 100,
        ResizeZone = 1000,
        UpdateLeds = 1050,
        UpdateZoneLeds = 1051,
        UpdateSingleLed = 1052,
        SetCustomMode = 1100,
        UpdateMode = 1101
    }

    /// <summary>Builds one packet: magic, device index, type, payload length, payload.</summary>
    public static byte[] BuildPacket(uint deviceIndex, PacketType type, ReadOnlySpan<byte> payload)
    {
        var packet = new byte[HeaderBytes + payload.Length];
        packet[0] = (byte)'O';
        packet[1] = (byte)'R';
        packet[2] = (byte)'G';
        packet[3] = (byte)'B';
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), deviceIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), (uint)type);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), (uint)payload.Length);
        payload.CopyTo(packet.AsSpan(HeaderBytes));
        return packet;
    }

    /// <summary>Reads a header. False when the magic is wrong or the length is not one this client will accept.</summary>
    public static bool TryParseHeader(
        ReadOnlySpan<byte> header,
        out uint deviceIndex,
        out PacketType type,
        out int payloadBytes)
    {
        deviceIndex = 0;
        type = 0;
        payloadBytes = 0;

        if (header.Length < HeaderBytes) return false;
        if (header[0] != (byte)'O' || header[1] != (byte)'R' || header[2] != (byte)'G' || header[3] != (byte)'B')
            return false;

        deviceIndex = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        type = (PacketType)BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        if (length > MaxPayloadBytes) return false;

        payloadBytes = (int)length;
        return true;
    }

    /// <summary>The <c>REQUEST_PROTOCOL_VERSION</c> payload: one <c>u32</c>, the highest version the client speaks.</summary>
    public static byte[] BuildProtocolVersionPayload(uint version)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, version);
        return payload;
    }

    /// <summary>The server's answer to <c>REQUEST_PROTOCOL_VERSION</c>: its own highest version. Null when short.</summary>
    public static uint? ParseProtocolVersion(ReadOnlySpan<byte> payload) =>
        payload.Length < 4 ? null : BinaryPrimitives.ReadUInt32LittleEndian(payload);

    /// <summary>The version both sides speak: the lower of the two. The server clamps the same way.</summary>
    public static uint NegotiateVersion(uint serverVersion) => Math.Min(serverVersion, ClientProtocolVersion);

    /// <summary>The <c>SET_CLIENT_NAME</c> payload: the UTF-8 name followed by a NUL. The server sends no reply.</summary>
    public static byte[] BuildClientNamePayload(string name)
    {
        var bytes = Encoding.UTF8.GetBytes((name ?? string.Empty).Replace("\0", string.Empty, StringComparison.Ordinal));
        var payload = new byte[bytes.Length + 1];
        bytes.CopyTo(payload, 0);
        return payload;
    }

    /// <summary>
    /// The <c>REQUEST_CONTROLLER_DATA</c> payload: nothing at protocol 0, the negotiated version as a <c>u32</c> from 1
    /// on. The answer is laid out for that version.
    /// </summary>
    public static byte[] BuildControllerDataRequestPayload(uint protocolVersion) =>
        protocolVersion == 0 ? Array.Empty<byte>() : BuildProtocolVersionPayload(protocolVersion);

    /// <summary>
    /// The <c>RGBCONTROLLER_UPDATELEDS</c> payload: <c>u32 data_size</c> (the whole payload, itself included),
    /// <c>u16 num_colors</c>, then four bytes <c>R G B 00</c> per LED.
    /// </summary>
    public static byte[] BuildUpdateLedsPayload(IReadOnlyList<RgbColor> colors)
    {
        ArgumentNullException.ThrowIfNull(colors);
        CheckColorCount(colors.Count);

        int total = 4 + 2 + colors.Count * 4;
        var payload = new byte[total];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)total);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), (ushort)colors.Count);
        WriteColors(payload.AsSpan(6), colors);
        return payload;
    }

    /// <summary>
    /// The <c>RGBCONTROLLER_UPDATEZONELEDS</c> payload: <c>u32 data_size</c> (the whole payload), <c>u32 zone_idx</c>,
    /// <c>u16 num_colors</c>, then four bytes <c>R G B 00</c> per LED.
    /// </summary>
    public static byte[] BuildUpdateZoneLedsPayload(int zoneIndex, IReadOnlyList<RgbColor> colors)
    {
        ArgumentNullException.ThrowIfNull(colors);
        ArgumentOutOfRangeException.ThrowIfNegative(zoneIndex);
        CheckColorCount(colors.Count);

        int total = 4 + 4 + 2 + colors.Count * 4;
        var payload = new byte[total];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)total);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), (uint)zoneIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8), (ushort)colors.Count);
        WriteColors(payload.AsSpan(10), colors);
        return payload;
    }

    private static void CheckColorCount(int count)
    {
        // num_colors is a u16 on the wire; more than that cannot be described, so it is refused rather than wrapped.
        if (count > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(count), count, "OpenRGB carries at most 65535 colours.");
    }

    private static void WriteColors(Span<byte> target, IReadOnlyList<RgbColor> colors)
    {
        int offset = 0;
        foreach (var color in colors)
        {
            target[offset++] = color.R;
            target[offset++] = color.G;
            target[offset++] = color.B;
            target[offset++] = 0;
        }
    }

    /// <summary>Reads the controller count out of a <c>REQUEST_CONTROLLER_COUNT</c> answer.</summary>
    public static int? ParseControllerCount(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4) return null;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        return count > 1024 ? null : (int)count;
    }

    /// <summary>
    /// Reads a <c>REQUEST_CONTROLLER_DATA</c> answer laid out for <paramref name="protocolVersion"/> (the version
    /// sent in the request). Returns null when the payload is not a controller description or the version is newer
    /// than this client parses; a description whose colour list is cut short comes back with
    /// <see cref="OpenRgbController.Parsed"/> false and the fields it did read, so a caller can still name the device
    /// without trusting the colour list.
    /// </summary>
    public static OpenRgbController? ParseController(
        int index, ReadOnlySpan<byte> payload, uint protocolVersion = ClientProtocolVersion)
    {
        if (protocolVersion > ClientProtocolVersion) return null;
        if (payload.Length < 4) return null;

        // data_size counts itself, so it is the payload length. A larger one points past the packet; a smaller one
        // is honoured as the end of the block.
        uint dataSize = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        if (dataSize < 4 || dataSize > MaxPayloadBytes || dataSize > payload.Length) return null;

        var cursor = new Cursor(payload[4..(int)dataSize]);

        if (!cursor.TryUInt32(out _)) return null;                          // type
        if (!cursor.TryString(out string name)) return null;                 // name
        if (protocolVersion >= 1 && !cursor.TryString(out _)) return null;   // vendor (v1+)
        for (int i = 0; i < 4; i++)                                         // description, version, serial, location
        {
            if (!cursor.TryString(out _)) return null;
        }

        if (!cursor.TryUInt16(out ushort modeCount)) return null;
        if (!cursor.TryUInt32(out _)) return null;                          // active_mode
        for (int mode = 0; mode < modeCount; mode++)
        {
            if (!cursor.TryString(out _)) return null;                      // mode name
            // value, flags, speed min, speed max, [brightness min, brightness max (v3+)], colours min, colours max,
            // speed, [brightness (v3+)], direction, colour mode
            int fields = protocolVersion >= 3 ? 12 : 9;
            if (!cursor.Skip(fields * 4L)) return null;
            if (!cursor.TryUInt16(out ushort modeColors)) return null;
            if (!cursor.Skip(modeColors * 4L)) return null;
        }

        if (!cursor.TryUInt16(out ushort zoneCount)) return null;
        for (int zone = 0; zone < zoneCount; zone++)
        {
            if (!cursor.TryString(out _)) return null;                      // zone name
            if (!cursor.Skip(4 * 4)) return null;                           // type, leds min, leds max, leds count
            if (!cursor.TryUInt16(out ushort matrixBytes)) return null;     // zone_matrix_len, in bytes
            if (!cursor.Skip(matrixBytes)) return null;                     // height, width, height*width u32
            if (protocolVersion >= 4)
            {
                if (!cursor.TryUInt16(out ushort segmentCount)) return null;
                for (int segment = 0; segment < segmentCount; segment++)
                {
                    if (!cursor.TryString(out _)) return null;              // segment name
                    if (!cursor.Skip(3 * 4)) return null;                   // type, start index, led count
                }
            }
        }

        if (!cursor.TryUInt16(out ushort ledCount)) return null;
        for (int led = 0; led < ledCount; led++)
        {
            if (!cursor.TryString(out _)) return null;                      // led name
            if (!cursor.Skip(4)) return null;                               // value
        }

        if (!cursor.TryUInt16(out ushort colorCount)) return null;
        var colors = new List<RgbColor>(colorCount);
        for (int color = 0; color < colorCount; color++)
        {
            if (!cursor.TryColor(out var rgb))
            {
                return new OpenRgbController(index, name, ledCount, zoneCount, colors, Parsed: false);
            }
            colors.Add(rgb);
        }

        return new OpenRgbController(index, name, ledCount, zoneCount, colors, Parsed: true);
    }

    /// <summary>A bounds-checked reader over one payload. Every read reports failure instead of throwing.</summary>
    private ref struct Cursor
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _offset;

        public Cursor(ReadOnlySpan<byte> data)
        {
            _data = data;
            _offset = 0;
        }

        public bool TryUInt16(out ushort value)
        {
            if ((long)_offset + 2 > _data.Length) { value = 0; return false; }
            value = BinaryPrimitives.ReadUInt16LittleEndian(_data[_offset..]);
            _offset += 2;
            return true;
        }

        public bool TryUInt32(out uint value)
        {
            if ((long)_offset + 4 > _data.Length) { value = 0; return false; }
            value = BinaryPrimitives.ReadUInt32LittleEndian(_data[_offset..]);
            _offset += 4;
            return true;
        }

        /// <summary>One <c>RGBColor</c>: <c>R G B</c> and a padding byte.</summary>
        public bool TryColor(out RgbColor value)
        {
            if ((long)_offset + 4 > _data.Length) { value = default; return false; }
            value = new RgbColor(_data[_offset], _data[_offset + 1], _data[_offset + 2]);
            _offset += 4;
            return true;
        }

        public bool Skip(long bytes)
        {
            if (bytes < 0 || _offset + bytes > _data.Length) return false;
            _offset += (int)bytes;
            return true;
        }

        /// <summary>A <c>u16</c> length (NUL included), then the bytes. The text stops at the first NUL.</summary>
        public bool TryString(out string value)
        {
            value = string.Empty;
            if (!TryUInt16(out ushort length)) return false;
            if ((long)_offset + length > _data.Length) return false;

            var field = _data.Slice(_offset, length);
            _offset += length;

            int end = field.IndexOf((byte)0);
            if (end < 0) end = field.Length;
            value = Encoding.UTF8.GetString(field[..end]);
            return true;
        }
    }
}
