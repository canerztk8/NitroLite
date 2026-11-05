using System;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

public class AdversarialRtssChallengeTests
{
    [Fact]
    public void ParseSharedMemory_AdversarialOffsetMultiplicationOverflow_ReproducesArgumentOutOfRangeException()
    {
        // Vulnerability Demonstration:
        // When untrusted RTSS shared memory provides large unsigned 32-bit values for
        // lastForegroundApp and appEntrySize, the calculation:
        // long entryOffset = appArrOffset + ((long)lastForegroundApp * appEntrySize);
        // overflows 64-bit signed long in unchecked arithmetic, producing a negative entryOffset.
        // The bounds check 'if (entryOffset + 312 > buffer.Length)' fails to detect negative values
        // because negative + 312 is still negative, which is NOT > buffer.Length.
        // Consequently, BitConverter.ToUInt32(buffer, (int)entryOffset) receives a negative index
        // and throws System.ArgumentOutOfRangeException.
        uint lastApp = 3_000_000_000U;
        uint entrySize = 4_000_000_000U;

        byte[] buffer = new byte[1024];
        BitConverter.GetBytes((uint)0x53535452).CopyTo(buffer, 0); // 'RTSS'
        BitConverter.GetBytes((uint)0x00020000).CopyTo(buffer, 4); // v2.0
        BitConverter.GetBytes(entrySize).CopyTo(buffer, 8); // appEntrySize
        BitConverter.GetBytes((uint)100).CopyTo(buffer, 12); // appArrOffset
        BitConverter.GetBytes(uint.MaxValue).CopyTo(buffer, 16); // appArrSize
        BitConverter.GetBytes(lastApp).CopyTo(buffer, 64); // lastForegroundApp
        BitConverter.GetBytes((uint)1234).CopyTo(buffer, 68); // PID

        // Demonstrates the unhandled ArgumentOutOfRangeException
        Assert.Throws<ArgumentOutOfRangeException>(() => FpsMonitorService.ParseSharedMemory(buffer));
    }

    [Fact]
    public void ParseSharedMemory_CorruptedSignatures_ReturnsUnavailable()
    {
        uint[] corruptedSignatures = [0x0, 0x12345678, 0xFFFFFFFF, 0x52545353, 0x00005353];
        foreach (var sig in corruptedSignatures)
        {
            byte[] buf = new byte[1024];
            BitConverter.GetBytes(sig).CopyTo(buf, 0);
            BitConverter.GetBytes((uint)0x00020000).CopyTo(buf, 4);
            var info = FpsMonitorService.ParseSharedMemory(buf);
            Assert.False(info.IsAvailable, $"Signature 0x{sig:X8} should have failed");
        }
    }

    [Fact]
    public void ParseSharedMemory_OlderVersions_ReturnsUnavailable()
    {
        uint[] olderVersions = [0x0, 0x00010000, 0x00019999, 0x00000002];
        foreach (var ver in olderVersions)
        {
            byte[] buf = new byte[1024];
            BitConverter.GetBytes((uint)0x53535452).CopyTo(buf, 0);
            BitConverter.GetBytes(ver).CopyTo(buf, 4);
            var info = FpsMonitorService.ParseSharedMemory(buf);
            Assert.False(info.IsAvailable, $"Version 0x{ver:X8} should have failed");
        }
    }

    [Fact]
    public void ParseSharedMemory_ZeroOrNegativeOrExtremeFrametimes_HandledGracefully()
    {
        // Test zero frametime
        byte[] bufZero = CreateRtssBuffer(1000, "game.exe", 0, 0);
        var infoZero = FpsMonitorService.ParseSharedMemory(bufZero);
        Assert.False(infoZero.IsAvailable);
        Assert.Equal(0, infoZero.Fps);
        Assert.Equal(0, infoZero.FrametimeMs);

        // Test frametime = uint.MaxValue
        byte[] bufHuge = CreateRtssBuffer(1000, "game.exe", uint.MaxValue, 0);
        var infoHuge = FpsMonitorService.ParseSharedMemory(bufHuge);
        Assert.True(infoHuge.IsAvailable);
        Assert.True(infoHuge.FrametimeMs > 0);
        Assert.True(infoHuge.Fps >= 0);

        // Test avgFps = 10000 (extreme FPS)
        byte[] bufExtFps = CreateRtssBuffer(1000, "fast.exe", 100, 10000);
        var infoExtFps = FpsMonitorService.ParseSharedMemory(bufExtFps);
        Assert.True(infoExtFps.IsAvailable);
        Assert.Equal(10000, infoExtFps.Fps);
        Assert.Equal(0.1, infoExtFps.FrametimeMs);
    }

    [Fact]
    public void ParseSharedMemory_TruncatedBuffers_NeverThrows()
    {
        for (int len = 0; len < 500; len++)
        {
            byte[] buf = new byte[len];
            if (len >= 4) BitConverter.GetBytes((uint)0x53535452).CopyTo(buf, 0);
            if (len >= 8) BitConverter.GetBytes((uint)0x00020000).CopyTo(buf, 4);
            if (len >= 12) BitConverter.GetBytes((uint)320).CopyTo(buf, 8);
            if (len >= 16) BitConverter.GetBytes((uint)128).CopyTo(buf, 12);
            if (len >= 20) BitConverter.GetBytes((uint)1).CopyTo(buf, 16);
            if (len >= 68) BitConverter.GetBytes((uint)0).CopyTo(buf, 64);
            if (len >= 72) BitConverter.GetBytes((uint)1234).CopyTo(buf, 68);

            var ex = Record.Exception(() => FpsMonitorService.ParseSharedMemory(buf));
            Assert.Null(ex);
        }
    }

    [Fact]
    public void ParseSharedMemory_NonAsciiAndUnterminatedProcessName_HandledGracefully()
    {
        byte[] buf = new byte[1024];
        BitConverter.GetBytes((uint)0x53535452).CopyTo(buf, 0);
        BitConverter.GetBytes((uint)0x00020000).CopyTo(buf, 4);
        BitConverter.GetBytes((uint)320).CopyTo(buf, 8);
        BitConverter.GetBytes((uint)128).CopyTo(buf, 12);
        BitConverter.GetBytes((uint)1).CopyTo(buf, 16);
        BitConverter.GetBytes((uint)0).CopyTo(buf, 64);
        BitConverter.GetBytes((uint)5555).CopyTo(buf, 68);

        // Entry 0
        int entry = 128;
        BitConverter.GetBytes((uint)5555).CopyTo(buf, entry);
        // Fill name with 0xFF (no null terminator in the 260 bytes window)
        for (int i = 0; i < 260; i++) buf[entry + 4 + i] = 0xFF;
        BitConverter.GetBytes((uint)16666).CopyTo(buf, entry + 280);
        BitConverter.GetBytes((uint)60).CopyTo(buf, entry + 308);

        var info = FpsMonitorService.ParseSharedMemory(buf);
        Assert.True(info.IsAvailable);
        Assert.Equal(60, info.Fps);
        Assert.Equal(16.7, info.FrametimeMs);
        Assert.NotNull(info.GameTitle);
    }

    [Theory]
    [InlineData(-100.0, true, "-- FPS")]
    [InlineData(-0.0001, true, "-- FPS")]
    [InlineData(0.0, true, "-- FPS")]
    [InlineData(0.0, false, "-- FPS")]
    [InlineData(144.0, false, "-- FPS")]
    [InlineData(144.4, true, "144 FPS")]
    [InlineData(144.9, true, "145 FPS")]
    [InlineData(999.0, true, "999 FPS")]
    [InlineData(1000.0, true, "1000 FPS")]
    [InlineData(5000.0, true, "5000 FPS")]
    public void FormatFps_StressEdgeCases_ProducesSafeOutputs(double fps, bool available, string expected)
    {
        string formatted = TelemetryFormatter.FormatFps(fps, available);
        Assert.Equal(expected, formatted);
    }

    [Fact]
    public void FormatFps_SpecialDoubleValues_ReturnsFallback()
    {
        Assert.Equal("-- FPS", TelemetryFormatter.FormatFps(double.NaN, true));
        Assert.Equal("-- FPS", TelemetryFormatter.FormatFps(double.PositiveInfinity, true));
        Assert.Equal("-- FPS", TelemetryFormatter.FormatFps(double.NegativeInfinity, true));
        Assert.Equal("-- FPS", TelemetryFormatter.FormatFps(double.MinValue, true));
        // double.MaxValue should format as large number with invariant culture without crashing
        string maxFps = TelemetryFormatter.FormatFps(double.MaxValue, true);
        Assert.NotNull(maxFps);
        Assert.EndsWith("FPS", maxFps);
    }

    [Theory]
    [InlineData(-50.0, true, "-- ms")]
    [InlineData(0.0, true, "-- ms")]
    [InlineData(0.0, false, "-- ms")]
    [InlineData(16.666, true, "16.7 ms")]
    [InlineData(6.944, true, "6.9 ms")]
    [InlineData(0.1, true, "0.1 ms")]
    [InlineData(100.0, true, "100.0 ms")]
    [InlineData(9999.9, true, "9999.9 ms")]
    public void FormatFrametime_StressEdgeCases_ProducesSafeOutputs(double frametimeMs, bool available, string expected)
    {
        string formatted = TelemetryFormatter.FormatFrametime(frametimeMs, available);
        Assert.Equal(expected, formatted);
    }

    [Fact]
    public void FormatFrametime_SpecialDoubleValues_ReturnsFallback()
    {
        Assert.Equal("-- ms", TelemetryFormatter.FormatFrametime(double.NaN, true));
        Assert.Equal("-- ms", TelemetryFormatter.FormatFrametime(double.PositiveInfinity, true));
        Assert.Equal("-- ms", TelemetryFormatter.FormatFrametime(double.NegativeInfinity, true));
        Assert.Equal("-- ms", TelemetryFormatter.FormatFrametime(double.MinValue, true));
        string maxFt = TelemetryFormatter.FormatFrametime(double.MaxValue, true);
        Assert.NotNull(maxFt);
        Assert.EndsWith("ms", maxFt);
    }

    [Fact]
    public void FpsMonitorService_OfflineFallback_MultipleCallsAndConcurrency_ReturnsCleanSafeValues()
    {
        using var service = new FpsMonitorService();

        // Sequential queries when RTSS is absent
        for (int i = 0; i < 20; i++)
        {
            var info = service.GetCurrentFps($"Game_{i}");
            Assert.NotNull(info);
            Assert.False(info.IsAvailable);
            Assert.Equal(0, info.Fps);
            Assert.Equal(0, info.FrametimeMs);
            Assert.Equal($"Game_{i}", info.GameTitle);
        }

        // Concurrent queries across multiple threads
        System.Threading.Tasks.Parallel.For(0, 50, idx =>
        {
            var info = service.GetCurrentFps("ConcurrentGame");
            Assert.NotNull(info);
            Assert.False(info.IsAvailable);
            Assert.Equal(0, info.Fps);
            Assert.Equal(0, info.FrametimeMs);
        });

        // After disposal, queries continue returning safe fallbacks without throwing
        service.Dispose();
        var disposedInfo = service.GetCurrentFps("AfterDisposeGame");
        Assert.NotNull(disposedInfo);
        Assert.False(disposedInfo.IsAvailable);
    }

    private static byte[] CreateRtssBuffer(uint pid, string processName, uint frameTimeUs, uint avgFps)
    {
        byte[] buf = new byte[1024];
        BitConverter.GetBytes((uint)0x53535452).CopyTo(buf, 0);
        BitConverter.GetBytes((uint)0x00020000).CopyTo(buf, 4);
        BitConverter.GetBytes((uint)320).CopyTo(buf, 8);
        BitConverter.GetBytes((uint)128).CopyTo(buf, 12);
        BitConverter.GetBytes((uint)1).CopyTo(buf, 16);
        BitConverter.GetBytes((uint)0).CopyTo(buf, 64);
        BitConverter.GetBytes(pid).CopyTo(buf, 68);

        int entry = 128;
        BitConverter.GetBytes(pid).CopyTo(buf, entry);
        byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(processName);
        Array.Copy(nameBytes, 0, buf, entry + 4, Math.Min(nameBytes.Length, 259));
        BitConverter.GetBytes(frameTimeUs).CopyTo(buf, entry + 280);
        BitConverter.GetBytes(avgFps).CopyTo(buf, entry + 308);

        return buf;
    }
}
