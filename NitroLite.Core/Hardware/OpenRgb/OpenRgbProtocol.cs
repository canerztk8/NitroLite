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
/// The OpenRGB SDK wire format, version 0 through 4.
/// <para>
/// Every packet is a 16-byte header — the ASCII magic <c>ORGB</c>, the device index, the packet type and the payload
/// length — followed by that many bytes. Both directions use the same header, so a client that can build one can read
/// the other.
/// </para>
/// <para>
/// This is a re-implementation from the protocol description, not a port of OpenRGB's C++. It is deliberately
/// defensive: a payload that does not parse returns null rather than throwing, because the server on the other end is
/// a third-party process the user may be running at any version.
/// </para>
/// </summary>
public static class OpenRgbProtocol
{
    /// <summary>Bytes in front of every payload.</summary>
    public const int HeaderBytes = 16;

    /// <summary>Fixed-width string field in the controller description.</summary>
    public const int NameFieldBytes = 60;

    /// <summary>Refuse a payload larger than this. A wrong length field must not become a 4 GB allocation.</summary>
    public const int MaxPayloadBytes = 4 * 1024 * 1024;

    /// <summary>Packet types this client uses. The numbers are OpenRGB's, not ours.</summary>
    public enum PacketType : uint
    {
        RequestControllerCount = 0,
        RequestControllerData = 1,
        RequestProtocolVersion = 2,
        SetClientName = 3,
        DeviceListUpdated = 4,
        UpdateLeds = 1000,
        UpdateZoneLeds = 1001,
        UpdateSingleLed = 1002,
        SetCustomMode = 1005,
        ResizeZone = 1050,
        UpdateMode = 1100,
        UpdateProfile = 1101
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

    /// <summary>The <c>SET_CLIENT_NAME</c> payload: a length-prefixed UTF-8 name with no terminator.</summary>
    public static byte[] BuildClientNamePayload(string name)
    {
        var bytes = Encoding.UTF8.GetBytes(name ?? string.Empty);
        var payload = new byte[4 + bytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)bytes.Length);
        bytes.CopyTo(payload, 4);
        return payload;
    }

    /// <summary>
    /// The <c>RGBCONTROLLER_UPDATELEDS</c> payload: total size, colour count, then one RGB triple per LED.
    /// </summary>
    public static byte[] BuildUpdateLedsPayload(IReadOnlyList<RgbColor> colors)
    {
        ArgumentNullException.ThrowIfNull(colors);
        int dataSize = 4 + colors.Count * 3;
        var payload = new byte[4 + dataSize];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)dataSize);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), (uint)colors.Count);
        int offset = 8;
        foreach (var color in colors)
        {
            payload[offset++] = color.R;
            payload[offset++] = color.G;
            payload[offset++] = color.B;
        }
        return payload;
    }

    /// <summary>
    /// The <c>RGBCONTROLLER_UPDATEZONELEDS</c> payload: total size, zone index, colour count, then the RGB triples.
    /// </summary>
    public static byte[] BuildUpdateZoneLedsPayload(int zoneIndex, IReadOnlyList<RgbColor> colors)
    {
        ArgumentNullException.ThrowIfNull(colors);
        ArgumentOutOfRangeException.ThrowIfNegative(zoneIndex);
        int dataSize = 8 + colors.Count * 3;
        var payload = new byte[4 + dataSize];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)dataSize);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), (uint)zoneIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), (uint)colors.Count);
        int offset = 12;
        foreach (var color in colors)
        {
            payload[offset++] = color.R;
            payload[offset++] = color.G;
            payload[offset++] = color.B;
        }
        return payload;
    }

    /// <summary>Reads the controller count out of a <c>REQUEST_CONTROLLER_COUNT</c> answer.</summary>
    public static int? ParseControllerCount(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4) return null;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        return count > 1024 ? null : (int)count;
    }

    /// <summary>
    /// Reads a <c>REQUEST_CONTROLLER_DATA</c> answer. Returns null when the payload is not a controller description;
    /// a description this client cannot follow comes back with <see cref="OpenRgbController.Parsed"/> false and the
    /// fields it did read, so a caller can still name the device without trusting the colour list.
    /// </summary>
    public static OpenRgbController? ParseController(int index, ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4) return null;

        uint dataSize = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        if (dataSize > MaxPayloadBytes || 4L + dataSize > payload.Length) return null;

        var cursor = new Cursor(payload[4..(4 + (int)dataSize)]);

        if (!cursor.TryUInt32(out _)) return null;                       // controller type
        if (!cursor.TryFixedString(out string name)) return null;
        for (int i = 0; i < 5; i++)                                      // vendor, description, version, serial, location
        {
            if (!cursor.TryFixedString(out _)) return null;
        }

        if (!cursor.TryUInt32(out uint modeCount)) return null;
        for (uint mode = 0; mode < modeCount; mode++)
        {
            if (!cursor.TryFixedString(out _)) return null;
            // value, flags, speed min/max, brightness min/max, colours min/max, speed, brightness, direction, colour mode
            if (!cursor.Skip(12 * 4)) return null;
            if (!cursor.TryUInt32(out uint modeColors)) return null;
            if (!cursor.Skip((long)modeColors * 3)) return null;
        }

        if (!cursor.TryUInt32(out uint zoneCount)) return null;
        for (uint zone = 0; zone < zoneCount; zone++)
        {
            if (!cursor.TryFixedString(out _)) return null;
            // type, leds min, leds max, leds count
            if (!cursor.Skip(4 * 4)) return null;
            if (!cursor.TryUInt32(out uint matrixSize)) return null;
            if (!cursor.Skip((long)matrixSize * 4)) return null;
            if (!cursor.TryUInt32(out uint segmentCount)) return null;
            for (uint segment = 0; segment < segmentCount; segment++)
            {
                if (!cursor.TryFixedString(out _)) return null;
                if (!cursor.Skip(3 * 4)) return null;                    // type, start index, led count
            }
        }

        if (!cursor.TryUInt32(out uint ledCount)) return null;
        for (uint led = 0; led < ledCount; led++)
        {
            if (!cursor.TryFixedString(out _)) return null;
            if (!cursor.Skip(4)) return null;                            // value
        }

        if (!cursor.TryUInt32(out uint colorCount)) return null;
        var colors = new List<RgbColor>((int)Math.Min(colorCount, 4096));
        for (uint color = 0; color < colorCount; color++)
        {
            if (!cursor.TryByte(out byte r) || !cursor.TryByte(out byte g) || !cursor.TryByte(out byte b))
            {
                return new OpenRgbController(index, name, (int)ledCount, (int)zoneCount, colors, Parsed: false);
            }
            colors.Add(new RgbColor(r, g, b));
        }

        return new OpenRgbController(index, name, (int)ledCount, (int)zoneCount, colors, Parsed: true);
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

        public bool TryByte(out byte value)
        {
            if (_offset + 1 > _data.Length) { value = 0; return false; }
            value = _data[_offset++];
            return true;
        }

        public bool TryUInt32(out uint value)
        {
            if (_offset + 4 > _data.Length) { value = 0; return false; }
            value = BinaryPrimitives.ReadUInt32LittleEndian(_data[_offset..]);
            _offset += 4;
            return true;
        }

        public bool Skip(long bytes)
        {
            if (bytes < 0 || _offset + bytes > _data.Length) return false;
            _offset += (int)bytes;
            return true;
        }

        /// <summary>A fixed-width field: UTF-8 up to the first NUL, the rest padding.</summary>
        public bool TryFixedString(out string value)
        {
            value = string.Empty;
            if (_offset + NameFieldBytes > _data.Length) return false;

            var field = _data.Slice(_offset, NameFieldBytes);
            _offset += NameFieldBytes;

            int length = field.IndexOf((byte)0);
            if (length < 0) length = field.Length;
            value = Encoding.UTF8.GetString(field[..length]);
            return true;
        }
    }
}
