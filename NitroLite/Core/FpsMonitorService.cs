using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace NitroLite.Core;

/// <summary>
/// Monitors game frame rate and frametime telemetry via RivaTuner Statistics Server (RTSS)
/// shared memory bridge ("RTSSSharedMemoryV2"). Provides zero CPU overhead and hardware-accurate
/// telemetry (FPS, Frametime ms, 0.1% Low, 1% Low) without injecting code into game processes.
/// Gracefully falls back to unavailable state when RTSS is not running or on desktop.
/// </summary>
public class FpsMonitorService : IDisposable
{
    public const string RtssMapName = "RTSSSharedMemoryV2";
    public const uint RtssSignature = 0x53535452; // 'RTSS' in little-endian ASCII
    public const uint RtssSignatureAlt = 0x52545353; // 'RTSS' in big-endian / MSVC representation
    public const uint RtssMinVersion = 0x00020000; // RTSS 2.0+

    public static readonly string DefaultRtssPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        "RivaTuner Statistics Server",
        "RTSS.exe"
    );

    private bool _isDisposed = false;

    public bool IsRtssInstalled => File.Exists(DefaultRtssPath);

    public bool IsRtssRunning
    {
        get
        {
            try
            {
                using var mmf = MemoryMappedFile.OpenExisting(RtssMapName, MemoryMappedFileRights.Read);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public bool TryLaunchRtss()
    {
        if (!IsRtssInstalled) return false;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = DefaultRtssPath,
                UseShellExecute = true,
                Verb = "runas"
            };
            using var proc = Process.Start(psi);
            return proc != null;
        }
        catch
        {
            return false;
        }
    }

    public FpsTelemetryInfo GetCurrentFps(string? activeGameProcessName = null, string? displayName = null, int targetPid = 0)
    {
        string fallbackTitle = !string.IsNullOrWhiteSpace(displayName) ? displayName : (activeGameProcessName ?? string.Empty);

        if (_isDisposed)
        {
            return new FpsTelemetryInfo { IsAvailable = false, GameTitle = fallbackTitle };
        }

        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(RtssMapName, MemoryMappedFileRights.Read);
            using var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

            long capacity = accessor.Capacity;
            if (capacity < 72)
            {
                return new FpsTelemetryInfo { IsAvailable = false, GameTitle = fallbackTitle };
            }

            // Read header (72 bytes)
            byte[] header = new byte[72];
            accessor.ReadArray(0, header, 0, 72);

            uint signature = BitConverter.ToUInt32(header, 0);
            if (signature != RtssSignature && signature != RtssSignatureAlt)
            {
                return new FpsTelemetryInfo { IsAvailable = false, GameTitle = fallbackTitle };
            }

            uint version = BitConverter.ToUInt32(header, 4);
            if (version < RtssMinVersion)
            {
                return new FpsTelemetryInfo { IsAvailable = false, GameTitle = fallbackTitle };
            }

            uint appEntrySize = BitConverter.ToUInt32(header, 8);
            uint appArrOffset = BitConverter.ToUInt32(header, 12);
            uint appArrSize = BitConverter.ToUInt32(header, 16);
            uint lastForegroundApp = BitConverter.ToUInt32(header, 64);

            if (appEntrySize == 0 || appArrOffset == 0 || appArrSize == 0)
            {
                return new FpsTelemetryInfo { IsAvailable = false, GameTitle = fallbackTitle };
            }

            // Find application entry index
            int selectedIndex = -1;

            // 1. Try matching by target PID or process name if active game specified
            if (targetPid > 0 || !string.IsNullOrWhiteSpace(activeGameProcessName))
            {
                for (uint i = 0; i < appArrSize && i < 256; i++)
                {
                    long pos = appArrOffset + ((long)i * appEntrySize);
                    if (pos + 268 > capacity) break;

                    uint pid = accessor.ReadUInt32(pos);
                    if (pid == 0) continue;

                    if (targetPid > 0 && pid == (uint)targetPid)
                    {
                        selectedIndex = (int)i;
                        break;
                    }

                    if (!string.IsNullOrWhiteSpace(activeGameProcessName))
                    {
                        byte[] nameBuf = new byte[260];
                        accessor.ReadArray(pos + 4, nameBuf, 0, 260);
                        int zeroIdx = Array.IndexOf(nameBuf, (byte)0);
                        if (zeroIdx < 0) zeroIdx = 260;
                        string procName = Encoding.ASCII.GetString(nameBuf, 0, zeroIdx);

                        if (procName.Contains(activeGameProcessName, StringComparison.OrdinalIgnoreCase) ||
                            activeGameProcessName.Contains(Path.GetFileNameWithoutExtension(procName), StringComparison.OrdinalIgnoreCase) ||
                            (!string.IsNullOrWhiteSpace(displayName) && procName.Contains(displayName, StringComparison.OrdinalIgnoreCase)))
                        {
                            selectedIndex = (int)i;
                            break;
                        }
                    }
                }
            }

            // 2. Fallback to lastForegroundApp ONLY when no specific game was requested
            if (selectedIndex < 0 && string.IsNullOrWhiteSpace(activeGameProcessName) && lastForegroundApp < appArrSize)
            {
                long pos = appArrOffset + ((long)lastForegroundApp * appEntrySize);
                if (pos + 268 <= capacity)
                {
                    uint pid = accessor.ReadUInt32(pos);
                    if (pid != 0)
                    {
                        selectedIndex = (int)lastForegroundApp;
                    }
                }
            }

            // 3. Fallback to any active rendering entry with highest dwTime1 ONLY when no specific game was requested
            if (selectedIndex < 0 && string.IsNullOrWhiteSpace(activeGameProcessName))
            {
                uint latestTime1 = 0;
                for (uint i = 0; i < appArrSize && i < 256; i++)
                {
                    long pos = appArrOffset + ((long)i * appEntrySize);
                    if (pos + 276 > capacity) break;

                    uint pid = accessor.ReadUInt32(pos);
                    if (pid == 0) continue;

                    uint time1 = accessor.ReadUInt32(pos + 272);
                    if (time1 >= latestTime1)
                    {
                        latestTime1 = time1;
                        selectedIndex = (int)i;
                    }
                }
            }

            if (selectedIndex < 0)
            {
                return new FpsTelemetryInfo { IsAvailable = false, GameTitle = fallbackTitle };
            }

            long entryOffset = appArrOffset + ((long)selectedIndex * appEntrySize);
            int readBytes = (int)Math.Min((long)appEntrySize, capacity - entryOffset);
            if (readBytes < 284)
            {
                return new FpsTelemetryInfo { IsAvailable = false, GameTitle = fallbackTitle };
            }

            byte[] appEntryData = new byte[readBytes];
            accessor.ReadArray(entryOffset, appEntryData, 0, readBytes);

            return ParseAppEntry(appEntryData, fallbackTitle);
        }
        catch (FileNotFoundException)
        {
            return new FpsTelemetryInfo { IsAvailable = false, GameTitle = fallbackTitle };
        }
        catch (Exception)
        {
            return new FpsTelemetryInfo { IsAvailable = false, GameTitle = fallbackTitle };
        }
    }

    /// <summary>
    /// Pure, deterministic parser for synthetic or full RTSS shared memory buffer.
    /// Backward-compatible with existing unit test suites.
    /// </summary>
    public static FpsTelemetryInfo ParseSharedMemory(byte[]? buffer, string? defaultGameTitle = null)
    {
        if (buffer == null || buffer.Length < 72)
        {
            return new FpsTelemetryInfo { IsAvailable = false, GameTitle = defaultGameTitle ?? string.Empty };
        }

        uint signature = BitConverter.ToUInt32(buffer, 0);
        if (signature != RtssSignature && signature != RtssSignatureAlt)
        {
            return new FpsTelemetryInfo { IsAvailable = false, GameTitle = defaultGameTitle ?? string.Empty };
        }

        uint version = BitConverter.ToUInt32(buffer, 4);
        if (version < RtssMinVersion)
        {
            return new FpsTelemetryInfo { IsAvailable = false, GameTitle = defaultGameTitle ?? string.Empty };
        }

        uint appEntrySize = BitConverter.ToUInt32(buffer, 8);
        uint appArrOffset = BitConverter.ToUInt32(buffer, 12);
        uint appArrSize = BitConverter.ToUInt32(buffer, 16);
        uint lastForegroundApp = BitConverter.ToUInt32(buffer, 64);

        if (appEntrySize == 0 || appArrOffset == 0 || appArrSize == 0 || lastForegroundApp >= appArrSize)
        {
            return new FpsTelemetryInfo { IsAvailable = false, GameTitle = defaultGameTitle ?? string.Empty };
        }

        long entryOffset = unchecked((long)appArrOffset + ((long)lastForegroundApp * (long)appEntrySize));
        // Need at least 312 bytes in the application entry to read dwStatFramerateAvg (offset 308)
        if (entryOffset + 312 > buffer.Length)
        {
            return new FpsTelemetryInfo { IsAvailable = false, GameTitle = defaultGameTitle ?? string.Empty };
        }

        uint processId = BitConverter.ToUInt32(buffer, (int)entryOffset);
        if (processId == 0)
        {
            return new FpsTelemetryInfo { IsAvailable = false, GameTitle = defaultGameTitle ?? string.Empty };
        }

        int sliceLen = (int)Math.Min((long)buffer.Length - entryOffset, (long)appEntrySize);
        byte[] entryBytes = new byte[sliceLen];
        Array.Copy(buffer, (int)entryOffset, entryBytes, 0, sliceLen);

        return ParseAppEntry(entryBytes, defaultGameTitle);
    }

    /// <summary>
    /// Parses an RTSS application descriptor entry (RTSS_SHARED_MEMORY_APP_ENTRY).
    /// Extracts instantaneous FPS, frametime, 0.1% low and 1.0% low FPS.
    /// </summary>
    public static FpsTelemetryInfo ParseAppEntry(byte[]? entryBytes, string? defaultGameTitle = null)
    {
        if (entryBytes == null || entryBytes.Length < 284)
        {
            return new FpsTelemetryInfo { IsAvailable = false, GameTitle = defaultGameTitle ?? string.Empty };
        }

        uint processId = BitConverter.ToUInt32(entryBytes, 0);
        if (processId == 0)
        {
            return new FpsTelemetryInfo { IsAvailable = false, GameTitle = defaultGameTitle ?? string.Empty };
        }

        // Process Name: szName at offset 4, up to 260 chars null-terminated ASCII
        int nameLen = 0;
        while (nameLen < 260 && (4 + nameLen) < entryBytes.Length && entryBytes[4 + nameLen] != 0)
        {
            nameLen++;
        }
        string procName = nameLen > 0 ? Encoding.ASCII.GetString(entryBytes, 4, nameLen) : string.Empty;

        uint time0 = entryBytes.Length >= 272 ? BitConverter.ToUInt32(entryBytes, 268) : 0;
        uint time1 = entryBytes.Length >= 276 ? BitConverter.ToUInt32(entryBytes, 272) : 0;
        uint frames = entryBytes.Length >= 280 ? BitConverter.ToUInt32(entryBytes, 276) : 0;
        uint frameTimeUs = entryBytes.Length >= 284 ? BitConverter.ToUInt32(entryBytes, 280) : 0;

        uint statCount = entryBytes.Length >= 304 ? BitConverter.ToUInt32(entryBytes, 300) : 0;
        uint avgFpsRaw = entryBytes.Length >= 312 ? BitConverter.ToUInt32(entryBytes, 308) : 0;
        uint bufFpsRaw = entryBytes.Length >= 5028 ? BitConverter.ToUInt32(entryBytes, 5024) : 0;
        uint low1Raw = (statCount > 0 && entryBytes.Length >= 5092) ? BitConverter.ToUInt32(entryBytes, 5088) : 0;
        uint low01Raw = (statCount > 0 && entryBytes.Length >= 5096) ? BitConverter.ToUInt32(entryBytes, 5092) : 0;

        double frametimeMs = frameTimeUs > 0 ? (frameTimeUs / 1000.0) : 0.0;

        double fps = 0.0;
        if (bufFpsRaw > 0)
        {
            fps = bufFpsRaw / 10.0;
        }
        else if (avgFpsRaw > 0)
        {
            fps = (double)avgFpsRaw;
        }
        else if (frameTimeUs > 0)
        {
            fps = 1000000.0 / frameTimeUs;
        }
        else if (time1 > time0 && frames > 0)
        {
            fps = (1000.0 * frames) / (time1 - time0);
        }

        if (frametimeMs == 0.0 && fps > 0.0)
        {
            frametimeMs = 1000.0 / fps;
        }

        double low01Fps = 0.0;
        double low1Fps = 0.0;

        if (low01Raw > 0)
        {
            low01Fps = low01Raw / 10.0;
        }
        if (low1Raw > 0)
        {
            low1Fps = low1Raw / 10.0;
        }

        // Live statistical percentile calculation from 1024-entry frametime ring buffer
        if (low01Fps == 0.0 && entryBytes.Length >= 5020)
        {
            var validFrameTimes = new List<uint>(1024);
            for (int i = 0; i < 1024; i++)
            {
                int ftOffset = 924 + (i * 4);
                if (ftOffset + 4 <= entryBytes.Length)
                {
                    uint ft = BitConverter.ToUInt32(entryBytes, ftOffset);
                    if (ft >= 1000 && ft <= 10_000_000) // between 1.0ms and 10s (filter out <1ms sub-frame noise)
                    {
                        validFrameTimes.Add(ft);
                    }
                }
            }

            if (validFrameTimes.Count >= 10)
            {
                validFrameTimes.Sort();
                int p999Idx = Math.Min(validFrameTimes.Count - 1, (int)(validFrameTimes.Count * 0.999));
                int p99Idx = Math.Min(validFrameTimes.Count - 1, (int)(validFrameTimes.Count * 0.99));

                uint p999Ft = validFrameTimes[p999Idx];
                uint p99Ft = validFrameTimes[p99Idx];

                if (p999Ft > 0) low01Fps = 1_000_000.0 / p999Ft;
                if (p99Ft > 0) low1Fps = 1_000_000.0 / p99Ft;
            }
        }

        // Mathematical Sanity Clamping: %0.1 Low and %1.0 Low represent worst frame drops and can never exceed average FPS
        if (fps > 0 && low01Fps > fps)
        {
            low01Fps = fps;
        }
        if (fps > 0 && low1Fps > fps)
        {
            low1Fps = fps;
        }
        if (low01Fps < 0 || double.IsNaN(low01Fps) || double.IsInfinity(low01Fps)) low01Fps = 0;
        if (low1Fps < 0 || double.IsNaN(low1Fps) || double.IsInfinity(low1Fps)) low1Fps = 0;

        string resolvedTitle = !string.IsNullOrWhiteSpace(defaultGameTitle)
            ? defaultGameTitle
            : (!string.IsNullOrWhiteSpace(procName) ? Path.GetFileNameWithoutExtension(procName) : string.Empty);

        return new FpsTelemetryInfo
        {
            IsAvailable = fps > 0 || frametimeMs > 0,
            Fps = Math.Round(fps, 0),
            FrametimeMs = Math.Round(frametimeMs, 1),
            Low01Fps = Math.Round(low01Fps, 0),
            Low1Fps = Math.Round(low1Fps, 0),
            GameTitle = resolvedTitle
        };
    }

    public void Dispose()
    {
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
}
