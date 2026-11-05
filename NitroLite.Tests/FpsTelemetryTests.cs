using System;
using System.Text;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

public class FpsTelemetryTests
{
    private static byte[] CreateSyntheticRtssBuffer(uint pid, string processName, uint frameTimeUs, uint avgFps)
    {
        byte[] buf = new byte[1024];

        // Signature 'RTSS' = 0x53535452 (in little-endian ASCII)
        BitConverter.GetBytes((uint)0x53535452).CopyTo(buf, 0);

        // Version 2.0 = 0x00020000
        BitConverter.GetBytes((uint)0x00020000).CopyTo(buf, 4);

        // AppEntrySize = 320 bytes
        BitConverter.GetBytes((uint)320).CopyTo(buf, 8);

        // AppArrOffset = 128 bytes
        BitConverter.GetBytes((uint)128).CopyTo(buf, 12);

        // AppArrSize = 2 entries
        BitConverter.GetBytes((uint)2).CopyTo(buf, 16);

        // LastForegroundApp index = 0
        BitConverter.GetBytes((uint)0).CopyTo(buf, 64);

        // LastForegroundAppProcessID = pid
        BitConverter.GetBytes(pid).CopyTo(buf, 68);

        // Application Entry 0 located at offset 128:
        int entryOffset = 128;
        BitConverter.GetBytes(pid).CopyTo(buf, entryOffset); // dwProcessID at 0

        // Process name: szName at offset 4 (up to 260 chars null-terminated ASCII)
        byte[] nameBytes = Encoding.ASCII.GetBytes(processName);
        Array.Copy(nameBytes, 0, buf, entryOffset + 4, Math.Min(nameBytes.Length, 259));

        // dwFrameTime at offset 280 (microseconds)
        BitConverter.GetBytes(frameTimeUs).CopyTo(buf, entryOffset + 280);

        // dwStatFramerateAvg at offset 308 (FPS)
        BitConverter.GetBytes(avgFps).CopyTo(buf, entryOffset + 308);

        return buf;
    }

    [Fact]
    public void ParseSharedMemory_ValidSyntheticBuffer_ReturnsCorrectFpsAndFrametime()
    {
        // 6944 microseconds = 6.944 ms frametime, 144 avg FPS
        byte[] buffer = CreateSyntheticRtssBuffer(14280, "cs2.exe", 6944, 144);

        var info = FpsMonitorService.ParseSharedMemory(buffer);

        Assert.True(info.IsAvailable);
        Assert.Equal(144, info.Fps);
        Assert.Equal(6.9, info.FrametimeMs);
        Assert.Equal("cs2", info.GameTitle);
    }

    [Fact]
    public void ParseSharedMemory_ZeroAvgFps_CalculatesInstantaneousFpsFromFrametime()
    {
        // 16666 microseconds = 16.666 ms frametime -> 1000000 / 16666 = 60 FPS
        byte[] buffer = CreateSyntheticRtssBuffer(8812, "balatro.exe", 16666, 0);

        var info = FpsMonitorService.ParseSharedMemory(buffer);

        Assert.True(info.IsAvailable);
        Assert.Equal(60, info.Fps);
        Assert.Equal(16.7, info.FrametimeMs);
        Assert.Equal("balatro", info.GameTitle);
    }

    [Fact]
    public void ParseSharedMemory_DefaultGameTitleOverride_UsesProvidedTitle()
    {
        byte[] buffer = CreateSyntheticRtssBuffer(14280, "cs2.exe", 6944, 144);

        var info = FpsMonitorService.ParseSharedMemory(buffer, "Counter-Strike 2");

        Assert.Equal("Counter-Strike 2", info.GameTitle);
    }

    [Fact]
    public void ParseSharedMemory_CorruptedSignature_ReturnsUnavailable()
    {
        byte[] buffer = CreateSyntheticRtssBuffer(14280, "cs2.exe", 6944, 144);
        buffer[0] = 0xAA; // Corrupt signature

        var info = FpsMonitorService.ParseSharedMemory(buffer);

        Assert.False(info.IsAvailable);
        Assert.Equal(0, info.Fps);
        Assert.Equal(0, info.FrametimeMs);
    }

    [Fact]
    public void ParseSharedMemory_LegacyVersion_ReturnsUnavailable()
    {
        byte[] buffer = CreateSyntheticRtssBuffer(14280, "cs2.exe", 6944, 144);
        // Set version to 1.0 (0x00010000)
        BitConverter.GetBytes((uint)0x00010000).CopyTo(buffer, 4);

        var info = FpsMonitorService.ParseSharedMemory(buffer);

        Assert.False(info.IsAvailable);
    }

    [Fact]
    public void ParseSharedMemory_TruncatedBuffer_ReturnsUnavailable()
    {
        byte[] shortBuffer = new byte[50]; // Header requires at least 72 bytes
        var info = FpsMonitorService.ParseSharedMemory(shortBuffer);

        Assert.False(info.IsAvailable);

        var nullInfo = FpsMonitorService.ParseSharedMemory(null);
        Assert.False(nullInfo.IsAvailable);
    }

    [Fact]
    public void ParseSharedMemory_ZeroPID_ReturnsUnavailable()
    {
        // PID 0 indicates idle or no active 3D hook
        byte[] buffer = CreateSyntheticRtssBuffer(0, "", 0, 0);

        var info = FpsMonitorService.ParseSharedMemory(buffer);

        Assert.False(info.IsAvailable);
    }

    [Theory]
    [InlineData(144.0, true, "144 FPS")]
    [InlineData(60.0, true, "60 FPS")]
    [InlineData(0.0, true, "-- FPS")]
    [InlineData(144.0, false, "-- FPS")]
    [InlineData(-1.0, true, "-- FPS")]
    [InlineData(double.NaN, true, "-- FPS")]
    [InlineData(double.PositiveInfinity, true, "-- FPS")]
    public void FormatFps_VariousStates_FormatsConsistently(double fps, bool available, string expected)
    {
        string result = TelemetryFormatter.FormatFps(fps, available);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(6.944, true, "6.9 ms")]
    [InlineData(16.666, true, "16.7 ms")]
    [InlineData(0.0, true, "-- ms")]
    [InlineData(6.9, false, "-- ms")]
    [InlineData(-1.0, true, "-- ms")]
    [InlineData(double.NaN, true, "-- ms")]
    [InlineData(double.PositiveInfinity, true, "-- ms")]
    public void FormatFrametime_VariousStates_FormatsConsistently(double frametime, bool available, string expected)
    {
        string result = TelemetryFormatter.FormatFrametime(frametime, available);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(95.0, true, "95 FPS")]
    [InlineData(45.2, true, "45 FPS")]
    [InlineData(0.0, true, "-- FPS")]
    [InlineData(95.0, false, "-- FPS")]
    [InlineData(-1.0, true, "-- FPS")]
    [InlineData(double.NaN, true, "-- FPS")]
    [InlineData(double.PositiveInfinity, true, "-- FPS")]
    public void FormatLow01Fps_VariousStates_FormatsConsistently(double low01Fps, bool available, string expected)
    {
        string result = TelemetryFormatter.FormatLow01Fps(low01Fps, available);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ParseAppEntry_WithLow01FpsStatField_ExtractsCorrectLow01Fps()
    {
        byte[] appEntry = new byte[6000];
        BitConverter.GetBytes((uint)14280).CopyTo(appEntry, 0); // PID
        byte[] nameBytes = Encoding.ASCII.GetBytes("cs2.exe");
        Array.Copy(nameBytes, 0, appEntry, 4, nameBytes.Length);

        // FrameTime = 6944 us (144 FPS)
        BitConverter.GetBytes((uint)6944).CopyTo(appEntry, 280);
        // dwStatCount = 100 (benchmark active)
        BitConverter.GetBytes((uint)100).CopyTo(appEntry, 300);
        // dwStatFrameTimeBufFramerate = 1440 (144.0 FPS fixed-point)
        BitConverter.GetBytes((uint)1440).CopyTo(appEntry, 5024);
        // dwStatFramerateAvg = 144
        BitConverter.GetBytes((uint)144).CopyTo(appEntry, 308);
        // dwStatFramerate0Dot1PercentLow = 950 (95.0 FPS fixed-point)
        BitConverter.GetBytes((uint)950).CopyTo(appEntry, 5092);

        var info = FpsMonitorService.ParseAppEntry(appEntry, "Counter-Strike 2");

        Assert.True(info.IsAvailable);
        Assert.Equal(144, info.Fps);
        Assert.Equal(6.9, info.FrametimeMs);
        Assert.Equal(95, info.Low01Fps);
        Assert.Equal("Counter-Strike 2", info.GameTitle);
    }

    [Fact]
    public void ParseAppEntry_WithFrametimeRingBuffer_CalculatesDynamicPercentiles()
    {
        byte[] appEntry = new byte[6000];
        BitConverter.GetBytes((uint)14280).CopyTo(appEntry, 0); // PID
        byte[] nameBytes = Encoding.ASCII.GetBytes("cs2.exe");
        Array.Copy(nameBytes, 0, appEntry, 4, nameBytes.Length);

        // FrameTime = 6944 us (144 FPS)
        BitConverter.GetBytes((uint)6944).CopyTo(appEntry, 280);

        // Fill ring buffer at offset 924 with 1024 frames of ~6944 us, with 2 stutter spikes at 20,000 us (50 FPS)
        for (int i = 0; i < 1024; i++)
        {
            uint ft = (i >= 1022) ? (uint)20000 : (uint)6944;
            BitConverter.GetBytes(ft).CopyTo(appEntry, 924 + (i * 4));
        }

        var info = FpsMonitorService.ParseAppEntry(appEntry, "Counter-Strike 2");

        Assert.True(info.IsAvailable);
        Assert.Equal(144, info.Fps);
        Assert.Equal(6.9, info.FrametimeMs);
        // Worst frame is 20000 us -> 1000000 / 20000 = 50 FPS
        Assert.Equal(50, info.Low01Fps);
    }

    [Fact]
    public void ParseAppEntry_ExtremeLow01Value_ClampedToAverageFps()
    {
        byte[] appEntry = new byte[6000];
        BitConverter.GetBytes((uint)14280).CopyTo(appEntry, 0); // PID
        byte[] nameBytes = Encoding.ASCII.GetBytes("spotifast.exe");
        Array.Copy(nameBytes, 0, appEntry, 4, nameBytes.Length);

        // FrameTime = 23255 us (~43 FPS)
        BitConverter.GetBytes((uint)23255).CopyTo(appEntry, 280);
        // dwStatCount = 0 (No active benchmark, 5092 contains garbage)
        BitConverter.GetBytes((uint)0).CopyTo(appEntry, 300);
        // dwStatFrameTimeBufFramerate = 430 (43.0 FPS)
        BitConverter.GetBytes((uint)430).CopyTo(appEntry, 5024);
        // Garbage uninitialized value at 5092 (3,987,980)
        BitConverter.GetBytes((uint)3987980).CopyTo(appEntry, 5092);

        var info = FpsMonitorService.ParseAppEntry(appEntry, "Spotifast");

        Assert.True(info.IsAvailable);
        Assert.Equal(43, info.Fps);
        // Must NOT be 398798 FPS! Must be <= 43 FPS
        Assert.True(info.Low01Fps <= 43);
    }

    [Fact]
    public void FpsMonitorService_WhenRtssRunning_CanReadSharedMemoryWithoutError()
    {
        using var service = new FpsMonitorService();
        var info = service.GetCurrentFps();
        Assert.NotNull(info);
        // If RTSS is running on the dev host, this should not throw and return either available or unavailable safely
    }
}
