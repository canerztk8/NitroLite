using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NitroLite.Core.Hardware.OpenRgb;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The OpenRGB SDK codec against the documented wire format (OpenRGB <c>Documentation/OpenRGBSDK.md</c>,
/// <c>NetworkProtocol.h</c>, <c>NetworkServer.cpp</c>, <c>RGBControllerInterface.h</c>): packet ids 40/50/1050/1051/1100,
/// u16-length strings that count their NUL, u16 counts, four-byte <c>RGBColor</c> (<c>R G B 00</c>), the
/// <c>active_mode</c> field, version-gated vendor/brightness/segments, and a <c>data_size</c> that counts itself.
/// </summary>
/// <remarks>
/// The client used to speak a made-up dialect (ids 2/3/1000/1005, 60-byte fixed strings, u32 counts, three-byte
/// colours) and to wait for replies to <c>SET_CLIENT_NAME</c> and colour writes that a real server never sends, so
/// against a real OpenRGB it timed out and reported "not supported".
/// </remarks>
public class AuditG5OpenRgbTests
{
    private static byte[] Hex(string text) =>
        Convert.FromHexString(string.Concat(text.Where(Uri.IsHexDigit)));

    // ------------------------------------------------------------------ golden bytes

    [Fact]
    public void ProtocolVersionRequest_IsTheDocumentedPacket()
    {
        var packet = OpenRgbProtocol.BuildPacket(0, OpenRgbProtocol.PacketType.RequestProtocolVersion,
            OpenRgbProtocol.BuildProtocolVersionPayload(OpenRgbProtocol.ClientProtocolVersion));

        Assert.Equal(Hex(
            "4F 52 47 42" +   // "ORGB"
            "00 00 00 00" +   // device 0
            "28 00 00 00" +   // NET_PACKET_ID_REQUEST_PROTOCOL_VERSION = 40
            "04 00 00 00" +   // payload length
            "04 00 00 00"),   // client protocol version 4
            packet);
    }

    [Fact]
    public void UpdateLedsPacket_IsTheDocumentedLayout()
    {
        var packet = OpenRgbProtocol.BuildPacket(2, OpenRgbProtocol.PacketType.UpdateLeds,
            OpenRgbProtocol.BuildUpdateLedsPayload([new RgbColor(0x12, 0x34, 0x56), new RgbColor(0xAB, 0xCD, 0xEF)]));

        Assert.Equal(Hex(
            "4F 52 47 42" +   // "ORGB"
            "02 00 00 00" +   // device 2
            "1A 04 00 00" +   // NET_PACKET_ID_RGBCONTROLLER_UPDATELEDS = 1050
            "0E 00 00 00" +   // payload length 14
            "0E 00 00 00" +   // data_size = whole payload, itself included
            "02 00" +         // num_colors (u16)
            "12 34 56 00" +   // RGBColor 0x00563412 -> R G B pad
            "AB CD EF 00"),
            packet);
    }

    [Fact]
    public void UpdateZoneLedsPayload_IsTheDocumentedLayout()
    {
        var payload = OpenRgbProtocol.BuildUpdateZoneLedsPayload(1, [new RgbColor(0x01, 0x02, 0x03)]);

        Assert.Equal(Hex(
            "0E 00 00 00" +   // data_size 14
            "01 00 00 00" +   // zone_idx (u32)
            "01 00" +         // num_colors (u16)
            "01 02 03 00"),
            payload);
    }

    [Fact]
    public void ClientNameAndSetCustomMode_UseTheDocumentedIds()
    {
        Assert.Equal(50u, (uint)OpenRgbProtocol.PacketType.SetClientName);
        Assert.Equal(1051u, (uint)OpenRgbProtocol.PacketType.UpdateZoneLeds);
        Assert.Equal(1100u, (uint)OpenRgbProtocol.PacketType.SetCustomMode);
        Assert.Equal(Hex("4E 4C 00"), OpenRgbProtocol.BuildClientNamePayload("NL"));
    }

    [Fact]
    public void ControllerData_MinimalVersion4Description_Parses()
    {
        var payload = Hex(
            "34 00 00 00" +            // data_size 52
            "00 00 00 00" +            // type
            "02 00 4B 00" +            // name "K"
            "02 00 41 00" +            // vendor "A" (v1+)
            "01 00 00" +               // description ""
            "01 00 00" +               // version ""
            "01 00 00" +               // serial ""
            "01 00 00" +               // location ""
            "00 00" +                  // num_modes
            "00 00 00 00" +            // active_mode
            "00 00" +                  // num_zones
            "01 00" +                  // num_leds
            "02 00 4C 00" +            // led name "L"
            "00 00 00 00" +            // led value
            "01 00" +                  // num_colors
            "12 34 56 00");            // colour
        Assert.Equal(52, payload.Length);

        var controller = OpenRgbProtocol.ParseController(0, payload, 4);

        Assert.NotNull(controller);
        Assert.True(controller!.Parsed);
        Assert.Equal("K", controller.Name);
        Assert.Equal(1, controller.LedCount);
        Assert.Equal(0, controller.ZoneCount);
        Assert.Equal(new[] { new RgbColor(0x12, 0x34, 0x56) }, controller.Colors);
    }

    [Fact]
    public void ControllerData_Version4WithModeZoneMatrixAndSegment_Parses()
    {
        var payload = Hex(
            "9E 00 00 00" +            // data_size 158
            "00 00 00 00" +            // type
            "02 00 4B 00" +            // name "K"
            "02 00 41 00" +            // vendor "A"
            "01 00 00 01 00 00 01 00 00 01 00 00" + // description, version, serial, location
            "01 00" +                  // num_modes
            "00 00 00 00" +            // active_mode
            "02 00 44 00" +            // mode name "D"
            string.Concat(Enumerable.Repeat("00 00 00 00 ", 12)) + // value..colour_mode, brightness fields included (v3+)
            "00 00" +                  // mode num_colors
            "01 00" +                  // num_zones
            "02 00 5A 00" +            // zone name "Z"
            "00 00 00 00" +            // zone type
            "01 00 00 00 01 00 00 00 01 00 00 00" + // leds min, max, count
            "0C 00" +                  // zone_matrix_len 12
            "01 00 00 00 01 00 00 00 00 00 00 00" + // height 1, width 1, map[0] = 0
            "01 00" +                  // num_segments (v4+)
            "02 00 53 00" +            // segment name "S"
            "00 00 00 00 00 00 00 00 01 00 00 00" + // segment type, start, count
            "01 00" +                  // num_leds
            "02 00 4C 00 00 00 00 00" + // led "L", value 0
            "01 00" +                  // num_colors
            "12 34 56 00");
        Assert.Equal(158, payload.Length);

        var controller = OpenRgbProtocol.ParseController(3, payload, 4);

        Assert.NotNull(controller);
        Assert.True(controller!.Parsed);
        Assert.Equal(3, controller.Index);
        Assert.Equal("K", controller.Name);
        Assert.Equal(1, controller.ZoneCount);
        Assert.Equal(1, controller.LedCount);
        Assert.Equal(new[] { new RgbColor(0x12, 0x34, 0x56) }, controller.Colors);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    [InlineData(4u)]
    public void ControllerData_EveryNegotiableVersionRoundTrips(uint version)
    {
        var colors = new[] { new RgbColor(1, 2, 3), new RgbColor(4, 5, 6), new RgbColor(7, 8, 9) };
        var payload = OpenRgbWire.BuildController("Acer Keyboard", colors, version);

        var controller = OpenRgbProtocol.ParseController(0, payload, version);

        Assert.NotNull(controller);
        Assert.True(controller!.Parsed);
        Assert.Equal("Acer Keyboard", controller.Name);
        Assert.Equal(colors, controller.Colors);
    }

    // ------------------------------------------------------------------ hostile payloads

    [Fact]
    public void ControllerData_HostileSizesAreRefused()
    {
        var good = OpenRgbWire.BuildController("K", [new RgbColor(1, 2, 3)], 4);

        // data_size pointing past the packet.
        var tooBig = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(tooBig, (uint)good.Length + 1);
        Assert.Null(OpenRgbProtocol.ParseController(0, tooBig, 4));

        // data_size smaller than itself.
        var tooSmall = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(tooSmall, 3);
        Assert.Null(OpenRgbProtocol.ParseController(0, tooSmall, 4));

        // A name length running off the end.
        var longName = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(longName.AsSpan(8), 0xFFFF);
        Assert.Null(OpenRgbProtocol.ParseController(0, longName, 4));

        // A version this client never negotiates.
        Assert.Null(OpenRgbProtocol.ParseController(0, good, 5));

        // Short payloads.
        Assert.Null(OpenRgbProtocol.ParseController(0, [], 4));
        Assert.Null(OpenRgbProtocol.ParseController(0, good.AsSpan(0, 3), 4));
    }

    [Fact]
    public void ControllerData_ColourListCutShortIsNotTrusted()
    {
        var good = OpenRgbWire.BuildController("K", [new RgbColor(1, 2, 3), new RgbColor(4, 5, 6)], 4);
        var cut = good[..^2];
        BinaryPrimitives.WriteUInt32LittleEndian(cut, (uint)cut.Length);

        var controller = OpenRgbProtocol.ParseController(0, cut, 4);

        Assert.NotNull(controller);
        Assert.False(controller!.Parsed);
        Assert.Equal("K", controller.Name);
    }

    [Fact]
    public void NegotiatedVersionIsTheLowerOfTheTwo()
    {
        Assert.Equal(4u, OpenRgbProtocol.NegotiateVersion(6));
        Assert.Equal(3u, OpenRgbProtocol.NegotiateVersion(3));
        Assert.Equal(4u, OpenRgbProtocol.NegotiateVersion(4));
    }

    // ------------------------------------------------------------------ against a protocol-faithful server

    [Fact]
    public async Task Client_NegotiatesVersion4WithANewerServerAndRegistersItsName()
    {
        using var server = new OpenRgbFakeServer { ServerVersion = 6 };
        await server.StartAsync();
        using var client = new OpenRgbClient(OpenRgbClient.DefaultHost, server.Port, delay: (_, _) => Task.CompletedTask);

        Assert.True(await client.ConnectAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(4u, client.ProtocolVersion);

        var controller = await client.GetControllerAsync(0);

        Assert.NotNull(controller);
        Assert.True(controller!.Parsed);
        Assert.Equal(4u, server.RequestedClientVersion);
        Assert.Equal(4u, server.LastDataRequestVersion);
        Assert.Equal("NitroLite", server.ClientName);
        Assert.Equal(OpenRgbProtocol.PacketType.RequestProtocolVersion, server.PacketTypes[0]);
    }

    [Fact]
    public async Task Client_ParsesAnOlderServerInItsOwnLayout()
    {
        using var server = new OpenRgbFakeServer { ServerVersion = 3 };
        await server.StartAsync();
        using var client = new OpenRgbClient(OpenRgbClient.DefaultHost, server.Port, delay: (_, _) => Task.CompletedTask);

        Assert.True(await client.ConnectAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(3u, client.ProtocolVersion);

        var result = await client.UpdateLedsAsync(0, [new RgbColor(0x21, 0x22, 0x23)]);

        Assert.Equal(OpenRgbWriteOutcome.Applied, result.Outcome);
        Assert.Equal(3u, server.LastDataRequestVersion);
    }

    [Fact]
    public async Task Client_DoesNotWaitForRepliesTheServerNeverSends()
    {
        using var server = new OpenRgbFakeServer();
        await server.StartAsync();
        using var client = new OpenRgbClient(OpenRgbClient.DefaultHost, server.Port, delay: (_, _) => Task.CompletedTask);

        var watch = Stopwatch.StartNew();
        Assert.True(await client.ConnectAsync(TimeSpan.FromSeconds(4)));
        Assert.True(await client.SetCustomModeAsync(0));
        var result = await client.UpdateLedsAsync(0, [new RgbColor(9, 8, 7)]);
        watch.Stop();

        // The old client waited for an answer to SET_CLIENT_NAME and to the write: against a real server that is a
        // full I/O timeout (5 s) each, and the write came back as failed.
        Assert.Equal(OpenRgbWriteOutcome.Applied, result.Outcome);
        Assert.True(watch.Elapsed < OpenRgbClient.IoTimeout, $"took {watch.Elapsed}");
        Assert.Equal(0, server.BadDataSizes);
        Assert.Contains(OpenRgbProtocol.PacketType.SetCustomMode, server.PacketTypes);
    }

    [Fact]
    public async Task Client_SkipsPacketsTheServerPushesOnItsOwn()
    {
        using var server = new OpenRgbFakeServer { UnsolicitedBeforeEachReply = 2 };
        await server.StartAsync();
        using var client = new OpenRgbClient(OpenRgbClient.DefaultHost, server.Port, delay: (_, _) => Task.CompletedTask);

        Assert.True(await client.ConnectAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, await client.GetControllerCountAsync());
        Assert.NotNull(await client.GetControllerAsync(0));
    }

    [Fact]
    public async Task Client_GivesUpOnAServerThatOnlySendsUnrelatedPackets()
    {
        using var server = new OpenRgbFakeServer { UnsolicitedBeforeEachReply = OpenRgbClient.MaxUnrelatedPackets + 4 };
        await server.StartAsync();
        using var client = new OpenRgbClient(OpenRgbClient.DefaultHost, server.Port, delay: (_, _) => Task.CompletedTask);

        Assert.False(await client.ConnectAsync(TimeSpan.FromSeconds(2)));
        Assert.NotNull(client.LastError);
    }

    [Fact]
    public async Task Client_RefusesAProtocol0ServerThatNeverAnswersTheVersion()
    {
        using var server = new OpenRgbFakeServer { ServerVersion = 0 };
        await server.StartAsync();
        using var client = new OpenRgbClient(OpenRgbClient.DefaultHost, server.Port, delay: (_, _) => Task.CompletedTask);

        Assert.False(await client.ConnectAsync(TimeSpan.FromMilliseconds(400)));
        Assert.NotNull(client.LastError);

        var result = await client.UpdateLedsAsync(0, [new RgbColor(1, 1, 1)]);
        Assert.Equal(OpenRgbWriteOutcome.NotSupported, result.Outcome);
    }
}

/// <summary>Builds OpenRGB controller descriptions in the documented layout for a given protocol version.</summary>
internal static class OpenRgbWire
{
    public static byte[] BuildController(string name, IReadOnlyList<RgbColor> colors, uint version = 4)
    {
        var data = new List<byte>();

        void U16(int value)
        {
            var buffer = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(buffer, (ushort)value);
            data.AddRange(buffer);
        }

        void U32(uint value)
        {
            var buffer = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
            data.AddRange(buffer);
        }

        void Str(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            U16(bytes.Length + 1);
            data.AddRange(bytes);
            data.Add(0);
        }

        void Color(RgbColor color)
        {
            data.Add(color.R);
            data.Add(color.G);
            data.Add(color.B);
            data.Add(0);
        }

        U32(0);                                    // type
        Str(name);
        if (version >= 1) Str("Acer");             // vendor
        Str("Keyboard");                           // description
        Str("1.0");                                // version
        Str("");                                   // serial
        Str("HID: /dev/hidraw0");                  // location

        U16(1);                                    // num_modes
        U32(0);                                    // active_mode
        Str("Direct");
        U32(0);                                    // value
        U32(0x20);                                 // flags
        U32(0); U32(0);                            // speed min, max
        if (version >= 3) { U32(0); U32(100); }    // brightness min, max
        U32(0); U32(0);                            // colours min, max
        U32(0);                                    // speed
        if (version >= 3) U32(100);                // brightness
        U32(0);                                    // direction
        U32(1);                                    // colour mode
        U16(1);                                    // mode num_colors
        Color(new RgbColor(0xEE, 0xEE, 0xEE));

        U16(1);                                    // num_zones
        Str("Keyboard");
        U32(2);                                    // zone type (matrix)
        U32((uint)colors.Count); U32((uint)colors.Count); U32((uint)colors.Count);
        U16(8 + 4 * colors.Count);                 // zone_matrix_len
        U32(1); U32((uint)colors.Count);           // height, width
        for (int i = 0; i < colors.Count; i++) U32((uint)i);
        if (version >= 4)
        {
            U16(1);                                // num_segments
            Str("All");
            U32(0); U32(0); U32((uint)colors.Count);
        }

        U16(colors.Count);                         // num_leds
        for (int i = 0; i < colors.Count; i++)
        {
            Str($"LED {i}");
            U32((uint)i);
        }

        U16(colors.Count);                         // num_colors
        foreach (var color in colors) Color(color);

        var payload = new byte[4 + data.Count];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)payload.Length);   // data_size counts itself
        data.CopyTo(payload, 4);
        return payload;
    }
}

/// <summary>
/// A loopback OpenRGB server that behaves like the real one below protocol 6: it answers the version request with
/// its own highest version, answers count and data requests, and never answers <c>SET_CLIENT_NAME</c>,
/// <c>SETCUSTOMMODE</c> or a colour write. It keeps the colours it was sent, so a write that does not stick is a
/// real failure rather than a mock that always agrees.
/// </summary>
internal sealed class OpenRgbFakeServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private uint _negotiated;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public List<RgbColor> Colors { get; } = new();
    public List<OpenRgbProtocol.PacketType> PacketTypes { get; } = new();
    public int LastZoneIndex { get; private set; } = -1;
    public bool IgnoreWrites { get; init; }

    /// <summary>The server's highest protocol version. Zero means a protocol-0 server that never answers the request.</summary>
    public uint ServerVersion { get; init; } = 4;

    /// <summary>DEVICE_LIST_UPDATED packets pushed in front of every reply.</summary>
    public int UnsolicitedBeforeEachReply { get; init; }

    public uint? RequestedClientVersion { get; private set; }
    public uint? LastDataRequestVersion { get; private set; }
    public string? ClientName { get; private set; }
    public int BadDataSizes { get; private set; }

    public Task StartAsync()
    {
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync);
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = Task.Run(() => ServeAsync(client));
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var header = new byte[OpenRgbProtocol.HeaderBytes];
                    if (!await ReadExactlyAsync(stream, header)) return;
                    if (!OpenRgbProtocol.TryParseHeader(header, out uint device, out var type, out int length)) return;

                    var payload = new byte[length];
                    if (length > 0 && !await ReadExactlyAsync(stream, payload)) return;

                    lock (PacketTypes) PacketTypes.Add(type);

                    switch (type)
                    {
                        case OpenRgbProtocol.PacketType.RequestProtocolVersion:
                        {
                            uint requested = payload.Length == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(payload) : 0;
                            RequestedClientVersion = requested;
                            if (ServerVersion == 0) break;           // protocol 0 does not know this packet
                            _negotiated = Math.Min(requested, ServerVersion);
                            await ReplyAsync(stream, device, type, U32(ServerVersion));
                            break;
                        }

                        case OpenRgbProtocol.PacketType.SetClientName:
                        {
                            int end = Array.IndexOf(payload, (byte)0);
                            ClientName = Encoding.UTF8.GetString(payload, 0, end < 0 ? payload.Length : end);
                            break;                                   // no reply
                        }

                        case OpenRgbProtocol.PacketType.RequestControllerCount:
                            await ReplyAsync(stream, device, type, U32(1));
                            break;

                        case OpenRgbProtocol.PacketType.RequestControllerData:
                        {
                            uint version = payload.Length == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(payload) : 0;
                            version = Math.Min(version, _negotiated);
                            LastDataRequestVersion = version;
                            await ReplyAsync(stream, device, type, OpenRgbWire.BuildController("Acer Keyboard", Snapshot(), version));
                            break;
                        }

                        case OpenRgbProtocol.PacketType.UpdateLeds:
                            if (!IgnoreWrites) ApplyColors(payload, countOffset: 4);
                            break;                                   // no reply

                        case OpenRgbProtocol.PacketType.UpdateZoneLeds:
                            LastZoneIndex = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4));
                            if (!IgnoreWrites) ApplyColors(payload, countOffset: 8);
                            break;                                   // no reply

                        default:
                            break;                                   // SETCUSTOMMODE and the rest: no reply
                    }
                }
            }
            catch (IOException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }
    }

    private static byte[] U32(uint value)
    {
        var buffer = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        return buffer;
    }

    private RgbColor[] Snapshot()
    {
        lock (Colors)
        {
            if (Colors.Count == 0)
            {
                Colors.Add(new RgbColor(0, 0, 0));
                Colors.Add(new RgbColor(0, 0, 0));
            }
            return Colors.ToArray();
        }
    }

    private void ApplyColors(byte[] payload, int countOffset)
    {
        if (payload.Length < countOffset + 2) { BadDataSizes++; return; }
        if (BinaryPrimitives.ReadUInt32LittleEndian(payload) != (uint)payload.Length) BadDataSizes++;

        int count = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(countOffset));
        int offset = countOffset + 2;
        lock (Colors)
        {
            Colors.Clear();
            for (int i = 0; i < count; i++)
            {
                int at = offset + i * 4;
                if (at + 3 >= payload.Length) break;
                Colors.Add(new RgbColor(payload[at], payload[at + 1], payload[at + 2]));
            }
        }
    }

    private async Task ReplyAsync(NetworkStream stream, uint device, OpenRgbProtocol.PacketType type, byte[] payload)
    {
        for (int i = 0; i < UnsolicitedBeforeEachReply; i++)
            await SendAsync(stream, 0, OpenRgbProtocol.PacketType.DeviceListUpdated, []);
        await SendAsync(stream, device, type, payload);
    }

    private static async Task SendAsync(NetworkStream stream, uint device, OpenRgbProtocol.PacketType type, byte[] payload)
    {
        var packet = OpenRgbProtocol.BuildPacket(device, type, payload);
        await stream.WriteAsync(packet);
        await stream.FlushAsync();
    }

    private static async Task<bool> ReadExactlyAsync(NetworkStream stream, byte[] buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset));
            if (read <= 0) return false;
            offset += read;
        }
        return true;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
    }
}
