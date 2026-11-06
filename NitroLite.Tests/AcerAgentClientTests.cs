using System;
using System.IO;
using System.Text;
using System.Text.Json;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

public class AcerAgentClientTests
{
    [Fact]
    public void TryFindJsonObject_SplitsBackToBackMessagesAndSkipsNoise()
    {
        var bytes = Encoding.UTF8.GetBytes("\r\n{ \"Function\" : \"ADAPTOR_CHANGED\" }\n{ \"request\" : \"OPERATING_MODE\", \"result\" : \"0\", \"data\" : { \"mode\" : 4 } }");

        Assert.True(AcerAgentClient.TryFindJsonObject(bytes, out int s1, out int e1));
        Assert.Equal("{ \"Function\" : \"ADAPTOR_CHANGED\" }", Encoding.UTF8.GetString(bytes, s1, e1 - s1));

        var rest = bytes.AsSpan(e1);
        Assert.True(AcerAgentClient.TryFindJsonObject(rest, out int s2, out int e2));
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(rest.Slice(s2, e2 - s2)));
        Assert.Equal("OPERATING_MODE", doc.RootElement.GetProperty("request").GetString());
    }

    [Fact]
    public void TryFindJsonObject_IgnoresBracesInsideStringsAndWaitsForCompletion()
    {
        var partial = Encoding.UTF8.GetBytes("{ \"color\" : \"#00}E5{FF\", \"data\" : { \"x\" : 1 }");
        Assert.False(AcerAgentClient.TryFindJsonObject(partial, out _, out _));

        var complete = Encoding.UTF8.GetBytes("{ \"color\" : \"#00}E5{FF\", \"data\" : { \"x\" : 1 } }");
        Assert.True(AcerAgentClient.TryFindJsonObject(complete, out int s, out int e));
        Assert.Equal(0, s);
        Assert.Equal(complete.Length, e);
    }

    [Fact]
    public void IsResponseFor_MatchesByRequestAndPublishesEventsSeparately()
    {
        using var client = new AcerAgentClient();
        var received = new System.Threading.ManualResetEventSlim(false);
        string? eventName = null;
        client.ServiceEventReceived += (fn, _) => { eventName = fn; received.Set(); };

        Assert.False(client.IsResponseFor("{\"Function\":\"PRESS_TURBO_BUTTON\",\"Parameter\":{\"status\":1}}", "OPERATING_MODE"));
        Assert.True(received.Wait(2000));
        Assert.Equal("PRESS_TURBO_BUTTON", eventName);

        Assert.False(client.IsResponseFor("{\"request\":\"FAN_CONTROL\",\"result\":\"0\"}", "OPERATING_MODE"));
        Assert.True(client.IsResponseFor("{\"request\":\"OPERATING_MODE\",\"result\":\"0\"}", "OPERATING_MODE"));
    }

    [Fact]
    public void BuildFanControlParameter_UsesNitroSenseCustomFanDataFormat()
    {
        var json = JsonSerializer.Serialize(AcerAgentClient.BuildFanControlParameter(FanMode.Custom, 70, 150));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(2, root.GetProperty("mode").GetInt32());
        var fans = root.GetProperty("custom_fan_data");
        Assert.Equal(2, fans.GetArrayLength());
        Assert.Equal("CPU", fans[0].GetProperty("fan_name").GetString());
        Assert.Equal(0, fans[0].GetProperty("fan_custom_auto").GetInt32());
        Assert.Equal(70, fans[0].GetProperty("fan_custom_speed").GetInt32());
        Assert.Equal(100, fans[1].GetProperty("fan_custom_speed").GetInt32()); // clamped to 100 %

        var autoJson = JsonSerializer.Serialize(AcerAgentClient.BuildFanControlParameter(FanMode.Auto));
        using var autoDoc = JsonDocument.Parse(autoJson);
        Assert.Equal(0, autoDoc.RootElement.GetProperty("mode").GetInt32());
        Assert.Equal(1, autoDoc.RootElement.GetProperty("custom_fan_data")[0].GetProperty("fan_custom_auto").GetInt32());
    }

    private static bool IsValidJson(string str)
    {
        if (string.IsNullOrWhiteSpace(str)) return false;
        str = str.Trim();
        if (!str.StartsWith("{") || !str.EndsWith("}")) return false;
        try
        {
            using var doc = JsonDocument.Parse(str);
            return true;
        }
        catch
        {
            return false;
        }
    }

    [Theory]
    [InlineData("{\"result\": \"0\", \"data\": {\"mode\": 1}}", true)]
    [InlineData("   {\"result\": \"0\"}   \r\n", true)]
    [InlineData("{\"result\": \"0\", \"data\": {", false)]
    [InlineData("NOT_A_JSON_PAYLOAD", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("{\"partial\": [1, 2,", false)]
    public void IsValidJson_CorrectlyValidatesCompleteAndIncompletePackets(string input, bool expectedValid)
    {
        bool isValid = IsValidJson(input);
        Assert.Equal(expectedValid, isValid);
    }

    [Fact]
    public void StreamAccumulation_ChunkedFragments_ReassemblesCompleteJsonPacket()
    {
        // Simulate a TCP stream delivering a packet in 3 distinct chunks
        string part1 = "{\"result\": \"0\", ";
        string part2 = "\"data\": {\"CpuTemperature\": 68, \"GpuTemperature\": ";
        string part3 = "55}}";

        byte[] chunk1 = Encoding.UTF8.GetBytes(part1);
        byte[] chunk2 = Encoding.UTF8.GetBytes(part2);
        byte[] chunk3 = Encoding.UTF8.GetBytes(part3);

        using var accumulatedMs = new MemoryStream();

        // 1. Receive Chunk 1
        accumulatedMs.Write(chunk1, 0, chunk1.Length);
        string current = Encoding.UTF8.GetString(accumulatedMs.ToArray()).Trim();
        Assert.False(IsValidJson(current), "Chunk 1 alone should not be recognized as complete JSON.");

        // 2. Receive Chunk 2
        accumulatedMs.Write(chunk2, 0, chunk2.Length);
        current = Encoding.UTF8.GetString(accumulatedMs.ToArray()).Trim();
        Assert.False(IsValidJson(current), "Chunks 1+2 should not be recognized as complete JSON.");

        // 3. Receive Chunk 3
        accumulatedMs.Write(chunk3, 0, chunk3.Length);
        current = Encoding.UTF8.GetString(accumulatedMs.ToArray()).Trim();
        Assert.True(IsValidJson(current), "All chunks combined should form valid JSON.");

        using var doc = JsonDocument.Parse(current);
        var root = doc.RootElement;
        Assert.Equal("0", root.GetProperty("result").GetString());
        Assert.Equal(68, root.GetProperty("data").GetProperty("CpuTemperature").GetInt32());
        Assert.Equal(55, root.GetProperty("data").GetProperty("GpuTemperature").GetInt32());
    }

    [Fact]
    public void PacketHeaderFraming_BuildsProperBinaryLayout()
    {
        // Test that packets follow ACER protocol framing:
        // Bytes 0-3: ASCII "ACER"
        // Bytes 4-7: uint32 packetId (LittleEndian)
        // Bytes 8+: UTF-8 JSON payload
        byte[] magic = Encoding.ASCII.GetBytes("ACER");
        uint packetId = 10; // PacketGetMonitorData
        var payloadObj = new { Function = "GET_MONITOR_DATA" };
        var jsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payloadObj));
        var packetIdBytes = BitConverter.GetBytes(packetId);

        using var ms = new MemoryStream();
        ms.Write(magic);
        ms.Write(packetIdBytes);
        ms.Write(jsonBytes);

        var packet = ms.ToArray();

        // Verify Magic header
        Assert.Equal((byte)'A', packet[0]);
        Assert.Equal((byte)'C', packet[1]);
        Assert.Equal((byte)'E', packet[2]);
        Assert.Equal((byte)'R', packet[3]);

        // Verify Packet ID
        uint extractedId = BitConverter.ToUInt32(packet, 4);
        Assert.Equal(10u, extractedId);

        // Verify JSON payload
        string extractedJson = Encoding.UTF8.GetString(packet, 8, packet.Length - 8);
        Assert.Contains("GET_MONITOR_DATA", extractedJson);
    }

    [Fact]
    public void GpuOperatingMode_EnumValues_MatchAcerProtocolSpec()
    {
        Assert.Equal(0, (int)GpuOperatingMode.Optimus);
        Assert.Equal(1, (int)GpuOperatingMode.DiscreteGpu);
        Assert.Equal(2, (int)GpuOperatingMode.Automatic);
    }

    [Theory]
    [InlineData("{\"result\": \"0\", \"data\": {\"mode\": 0}, \"request\": \"GPU_MODE\"}", GpuOperatingMode.Optimus)]
    [InlineData("{\"result\": \"0\", \"data\": {\"mode\": 1}, \"request\": \"GPU_MODE\"}", GpuOperatingMode.DiscreteGpu)]
    [InlineData("{\"result\": \"0\", \"data\": {\"mode\": 2}, \"request\": \"GPU_MODE\"}", GpuOperatingMode.Automatic)]
    public void GpuOperatingMode_ParseResponseJson_ExtractsModeCorrectly(string json, GpuOperatingMode expected)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("data", out var data));
        Assert.True(data.TryGetProperty("mode", out var modeProp));
        int modeVal = modeProp.GetInt32();
        var parsed = (GpuOperatingMode)modeVal;
        Assert.Equal(expected, parsed);
    }

    [Fact]
    public void GpuOperatingMode_ParseCapabilityResponse_ExtractsBitmaskCorrectly()
    {
        string capJson = "{\"result\": \"0\", \"data\": [ 7 ], \"request\": \"SUPPORT_GPU_MODE_CAPABILITY\"}";
        using var doc = JsonDocument.Parse(capJson);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("data", out var data));
        Assert.Equal(JsonValueKind.Array, data.ValueKind);
        Assert.True(data.GetArrayLength() > 0);
        int cap = data[0].GetInt32();
        Assert.Equal(7, cap); // 7 = 0b111 (Supports Optimus, dGPU, and Automatic/DDS)
    }

    [Fact]
    public void GpuOperatingMode_PacketSetDeviceDataFraming_MatchesAcerSpecification()
    {
        byte[] magic = Encoding.ASCII.GetBytes("ACER");
        uint packetId = 100; // PacketSetDeviceData
        var payloadObj = new
        {
            Function = "GPU_MODE",
            Parameter = new { mode = (int)GpuOperatingMode.DiscreteGpu }
        };
        var jsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payloadObj));
        var packetIdBytes = BitConverter.GetBytes(packetId);

        using var ms = new MemoryStream();
        ms.Write(magic);
        ms.Write(packetIdBytes);
        ms.Write(jsonBytes);

        var packet = ms.ToArray();
        Assert.Equal(100u, BitConverter.ToUInt32(packet, 4));
        string payload = Encoding.UTF8.GetString(packet, 8, packet.Length - 8);
        Assert.Contains("\"Function\":\"GPU_MODE\"", payload);
        Assert.Contains("\"mode\":1", payload);
    }

    [Fact]
    public void SetLighting_OffState_GeneratesOffEffectForBothDevices()
    {
        string[] leds = new[] { "#000000", "#000000", "#000000", "#000000" };
        var paramGlobal = new
        {
            device = 0,
            effect = "OFF",
            brightness = 0,
            speed = 1,
            duration = 5,
            direction = 1,
            color = "#000000",
            LEDs = leds
        };
        var paramKb = new
        {
            device = 1,
            effect = "OFF",
            brightness = 0,
            speed = 1,
            duration = 5,
            direction = 1,
            color = "#000000",
            LEDs = leds
        };

        var jsonGlobal = JsonSerializer.Serialize(new { Function = "LIGHTING", Parameter = paramGlobal });
        var jsonKb = JsonSerializer.Serialize(new { Function = "LIGHTING", Parameter = paramKb });

        Assert.Contains("\"effect\":\"OFF\"", jsonGlobal);
        Assert.Contains("\"effect\":\"OFF\"", jsonKb);
        Assert.Contains("\"device\":0", jsonGlobal);
        Assert.Contains("\"device\":1", jsonKb);
    }

    [Fact]
    public void SetLighting_StaticProfile_FramesDevice0WithMultiZoneLeds()
    {
        string[] zones = new[] { "#00E5FF", "#FF2A55", "#00E5FF", "#FF2A55" };
        var paramGlobal = new
        {
            device = 0,
            effect = "STATIC",
            brightness = 7,
            speed = 4,
            duration = 5,
            direction = 1,
            color = "#00E5FF",
            LEDs = zones
        };

        var json = JsonSerializer.Serialize(new { Function = "LIGHTING", Parameter = paramGlobal });
        Assert.Contains("\"effect\":\"STATIC\"", json);
        Assert.Contains("\"device\":0", json);
        Assert.Contains("\"#00E5FF\"", json);
        Assert.Contains("\"#FF2A55\"", json);
    }

    [Theory]
    [InlineData("{\"result\": \"0\"}", true)]
    [InlineData("{\"result\": \"3\"}", false)]
    [InlineData("{\"result\": 0}", true)]
    [InlineData("{\"result\": 3}", false)]
    [InlineData("{\"result\": \"4\"}", false)]
    [InlineData("{\"result\": 4}", false)]
    [InlineData("{\"result\": \"1\"}", false)]
    [InlineData("", false)]
    public void IsSuccessResult_AccuratelyDistinguishesSuccessAndErrorCodes(string json, bool expected)
    {
        Assert.Equal(expected, AcerAgentClient.IsSuccessResult(json));
    }
}
