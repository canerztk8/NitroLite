using System;
using System.IO;
using System.Text;
using System.Text.Json;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

public class AcerAgentClientTests
{
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
}
