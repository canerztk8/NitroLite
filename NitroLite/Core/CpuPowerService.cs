using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace NitroLite.Core;

public class CpuPowerService : ICpuBoostAccess, IDisposable
{
    private const string SubProcessorGuid = "54533251-82be-4824-96c1-47b60b740d00";
    private const string PerfBoostModeGuid = "be337238-0d82-4146-a960-4f3749d470c7";

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerGetActiveScheme(IntPtr UserRootPowerKey, out IntPtr ActivePolicyGuid);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerReadACValueIndex(
        IntPtr RootPowerKey,
        ref Guid SchemeGuid,
        ref Guid SubGroupOfPowerSettingsGuid,
        ref Guid PowerSettingGuid,
        out uint AcValueIndex);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerReadDCValueIndex(
        IntPtr RootPowerKey,
        ref Guid SchemeGuid,
        ref Guid SubGroupOfPowerSettingsGuid,
        ref Guid PowerSettingGuid,
        out uint DcValueIndex);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerWriteACValueIndex(
        IntPtr RootPowerKey,
        ref Guid SchemeGuid,
        ref Guid SubGroupOfPowerSettingsGuid,
        ref Guid PowerSettingGuid,
        uint AcValueIndex);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerWriteDCValueIndex(
        IntPtr RootPowerKey,
        ref Guid SchemeGuid,
        ref Guid SubGroupOfPowerSettingsGuid,
        ref Guid PowerSettingGuid,
        uint DcValueIndex);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerSetActiveScheme(
        IntPtr UserRootPowerKey,
        ref Guid SchemeGuid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    public CpuBoostMode CurrentMode { get; internal set; } = CpuBoostMode.Aggressive;
    internal void SetCurrentModeForTesting(CpuBoostMode mode) => CurrentMode = mode;
    public bool IsModeVerified { get; private set; }
    public CpuBoostMode? VerifiedCurrentMode => IsModeVerified ? CurrentMode : null;
    internal bool BypassNativePowerCalls { get; set; } = false;

    public CpuPowerService() : this(true, false)
    {
    }

    public CpuPowerService(bool autoInitialize, bool bypassNativePowerCalls = false)
    {
        BypassNativePowerCalls = bypassNativePowerCalls;
        if (autoInitialize)
        {
            // No HKLM write here. UnhideSetting used to flip the "Attributes" value of the boost setting under
            // HKLM on every start: it needs administrator rights, so it silently failed for a standard user, and
            // for an administrator it permanently exposed a hidden Windows power option the user never asked for.
            // PowerWriteACValueIndex writes hidden settings perfectly well, so nothing needs unhiding.
            CurrentMode = QueryCurrentBoostMode(IsAcPowerActive());
        }
    }

    internal static bool TryGetActiveScheme(out Guid schemeGuid)
    {
        schemeGuid = Guid.Empty;
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out IntPtr guidPtr) == 0 && guidPtr != IntPtr.Zero)
            {
                try
                {
                    schemeGuid = Marshal.PtrToStructure<Guid>(guidPtr);
                    return true;
                }
                finally
                {
                    LocalFree(guidPtr);
                }
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Reads the battery (DC) value of any power-scheme setting. Locale-independent, unlike parsing
    /// <c>powercfg /query</c>, whose labels are translated.
    /// </summary>
    internal static bool TryReadDcValue(Guid subGroup, Guid setting, out uint value)
    {
        value = 0;
        if (!TryGetActiveScheme(out var scheme)) return false;
        return PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref subGroup, ref setting, out value) == 0;
    }

    /// <summary>Writes the battery (DC) value of a power-scheme setting and re-activates the scheme.</summary>
    internal static bool TryWriteDcValue(Guid subGroup, Guid setting, uint value)
    {
        if (!TryGetActiveScheme(out var scheme)) return false;
        if (PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subGroup, ref setting, value) != 0) return false;
        return PowerSetActiveScheme(IntPtr.Zero, ref scheme) == 0;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    public static bool IsAcPowerActive()
    {
        try
        {
            if (GetSystemPowerStatus(out var status))
            {
                return status.ACLineStatus != 0;
            }
        }
        catch { }
        return true;
    }

    public virtual CpuBoostMode QueryCurrentBoostMode(bool isAc)
    {
        var exact = QueryBoostModeExact(isAc);
        if (exact.HasValue)
        {
            CurrentMode = exact.Value;
            IsModeVerified = true;
            return exact.Value;
        }
        IsModeVerified = false;
        return CurrentMode;
    }

    public virtual CpuBoostMode QueryCurrentBoostMode() => QueryCurrentBoostMode(IsAcPowerActive());

    public virtual CpuBoostMode? QueryBoostModeExact(bool isAc)
    {
        try
        {
            if (!BypassNativePowerCalls && TryGetActiveScheme(out var schemeGuid))
            {
                var subGroup = new Guid(SubProcessorGuid);
                var setting = new Guid(PerfBoostModeGuid);

                uint ret = isAc
                    ? PowerReadACValueIndex(IntPtr.Zero, ref schemeGuid, ref subGroup, ref setting, out uint val)
                    : PowerReadDCValueIndex(IntPtr.Zero, ref schemeGuid, ref subGroup, ref setting, out val);

                if (ret == 0)
                {
                    // Windows PerfBoostMode is documented as 0..6. 3, 5 and 6 have no enum member, but they are
                    // real values the firmware can be holding: reporting them as "unknown" hid the user's actual
                    // setting. Anything above 6 is not a PerfBoostMode at all.
                    return val <= 6 ? (CpuBoostMode)val : null;
                }
            }
        }
        catch { }

        if (BypassNativePowerCalls && ProcessInfoFactory == null && MockPowerCfgQueryOutput == null)
        {
            return EnsureNoHangingProcess() ? CurrentMode : null;
        }

        return QueryCurrentBoostModePowerCfg(isAc);
    }

    internal string? MockPowerCfgQueryOutput { get; set; }

    internal CpuBoostMode? QueryCurrentBoostModePowerCfg(bool isAc = true)
    {
        if (!EnsureNoHangingProcess())
        {
            return null;
        }

        if (BypassNativePowerCalls && ProcessInfoFactory == null && MockPowerCfgQueryOutput == null)
        {
            return CurrentMode;
        }

        // Only the real spawn is cached. A test seam has no process to avoid, and caching it would hide a change
        // the test made between two calls.
        bool cacheable = MockPowerCfgQueryOutput == null && ProcessInfoFactory == null;
        if (cacheable && TryGetCachedPowerCfgMode(isAc, out var cached))
        {
            return cached;
        }

        try
        {
            string output;
            if (MockPowerCfgQueryOutput != null)
            {
                output = MockPowerCfgQueryOutput;
            }
            else
            {
                var psi = ProcessInfoFactory != null
                    ? ProcessInfoFactory($"/qh SCHEME_CURRENT {SubProcessorGuid} {PerfBoostModeGuid}")
                    : new ProcessStartInfo
                    {
                        FileName = WindowsPaths.SystemTool("powercfg.exe"),
                        Arguments = $"/qh SCHEME_CURRENT {SubProcessorGuid} {PerfBoostModeGuid}",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    };

                psi.RedirectStandardOutput = true;

                Process? p = null;
                try
                {
                    p = Process.Start(psi);
                    if (p == null) return null;

                    var readTask = p.StandardOutput.ReadToEndAsync();
                    // powercfg /query answers in tens of milliseconds. Three seconds was long enough to freeze the
                    // caller for four seconds on every poll when the tool hung; 800 ms is still generous.
                    if (!p.WaitForExit(800))
                    {
                        bool exited = false;
                        try
                        {
                            p.Kill(entireProcessTree: true);
                            exited = p.WaitForExit(1000) && p.HasExited;
                        }
                        catch
                        {
                            try { exited = p.HasExited; } catch { exited = false; }
                        }

                        if (!exited)
                        {
                            _hangingProcess = p;
                            p = null; // Ownership transferred: local finally will NOT dispose _hangingProcess
                        }
                        return null;
                    }

                    if (!p.HasExited || p.ExitCode != 0) return null;

                    if (!readTask.Wait(500))
                    {
                        try { p.Kill(entireProcessTree: true); } catch { }
                        return null;
                    }

                    output = readTask.Result;
                }
                finally
                {
                    p?.Dispose();
                }
            }

            // Parsed by GUID, not by label. powercfg /query is localised ("Geçerli AC Güç Ayarları Dizini" on a
            // Turkish Windows, "Aktueller AC-Energieeinstellungsindex" on a German one), so the old English-only
            // regex never matched and the fallback was dead on every non-English machine.
            var val = ParsePowerCfgIndex(output, isAc);
            if (val.HasValue)
            {
                // 3, 5 and 6 are real Windows values with no matching enum member. They are still the value the
                // firmware holds, so they are reported as-is; only values above 6 are rejected.
                CpuBoostMode? parsed = val.Value <= 6 ? (CpuBoostMode)val.Value : null;
                if (cacheable) StorePowerCfgMode(isAc, parsed);
                return parsed;
            }
        }
        catch { }

        // A failed read is cached too: retrying a hung powercfg every 5 s is exactly what this cache is for.
        if (cacheable) StorePowerCfgMode(isAc, null);
        return null;
    }

    /// <summary>
    /// Reads the AC or DC index out of <c>powercfg /qh</c> output without depending on the display language.
    /// The <c>/h</c> flag matters: the boost setting is hidden by default, and a plain <c>/query</c> prints only the
    /// scheme header for it, so the fallback path could never read a value.
    /// The block for the boost setting is found by its GUID, and the index is the last <c>0x...</c> value inside it;
    /// every label around those two facts is localised, the GUID and the hex value are not.
    /// </summary>
    internal static uint? ParsePowerCfgIndex(string? output, bool isAc)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;

        // The setting block starts at its GUID and ends at the next setting or subgroup header. "GUID Alias" is not
        // a terminator: the boost setting carries its own alias line right after its GUID, so stopping there cut the
        // block off before the current index and every read came back null.
        int start = output.IndexOf(PerfBoostModeGuid, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;

        int end = output.Length;
        foreach (var marker in new[] { "Power Setting GUID", "Subgroup GUID" })
        {
            int next = output.IndexOf(marker, start + PerfBoostModeGuid.Length, StringComparison.OrdinalIgnoreCase);
            if (next >= 0 && next < end) end = next;
        }
        var block = output[start..end];

        // "Current AC Power Setting Index" / "Current DC ..." are localised, but the two values always appear in
        // that order inside the block: AC first, then DC.
        var values = Regex.Matches(block, @"0x([0-9a-fA-F]{1,8})");
        if (values.Count == 0) return null;

        // The GUID itself is not a 0x value, so the first two matches are the AC and DC indexes.
        int index = isAc ? 0 : 1;
        if (values.Count <= index) return null;

        return uint.TryParse(values[index].Groups[1].Value, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private readonly SemaphoreSlim _powerLock = new(1, 1);
    internal Process? _hangingProcess;
    internal Func<string, ProcessStartInfo>? ProcessInfoFactory { get; set; }

    /// <summary>
    /// How long a real <c>powercfg.exe</c> read is reused. The native read fails on schemes where the boost setting
    /// is hidden, and the fallback then spawns a process; <c>SyncDeviceStateAsync</c> asks for the mode every 5 s,
    /// so without this that is ~12 process spawns a minute for the whole session. The value only changes when this
    /// app or the user writes it, and every write path calls <see cref="InvalidatePowerCfgCache"/>.
    /// </summary>
    private static readonly TimeSpan PowerCfgCacheTtl = TimeSpan.FromSeconds(60);

    private (CpuBoostMode? Mode, DateTime At) _powerCfgCacheAc;
    private (CpuBoostMode? Mode, DateTime At) _powerCfgCacheDc;

    /// <summary>Test seam: the clock the cache TTL is measured against.</summary>
    internal Func<DateTime> UtcNowFactory { get; set; } = () => DateTime.UtcNow;

    internal bool TryGetCachedPowerCfgMode(bool isAc, out CpuBoostMode? mode)
    {
        var entry = isAc ? _powerCfgCacheAc : _powerCfgCacheDc;
        if (entry.At != default && UtcNowFactory() - entry.At < PowerCfgCacheTtl)
        {
            mode = entry.Mode;
            return true;
        }
        mode = null;
        return false;
    }

    internal void StorePowerCfgMode(bool isAc, CpuBoostMode? mode)
    {
        if (isAc) _powerCfgCacheAc = (mode, UtcNowFactory());
        else _powerCfgCacheDc = (mode, UtcNowFactory());
    }

    /// <summary>Drops the cached fallback read so the next one goes back to the hardware.</summary>
    internal void InvalidatePowerCfgCache()
    {
        _powerCfgCacheAc = default;
        _powerCfgCacheDc = default;
    }

    internal bool EnsureNoHangingProcess()
    {
        if (_hangingProcess != null)
        {
            try
            {
                if (!_hangingProcess.HasExited)
                {
                    if (!_hangingProcess.WaitForExit(500) || !_hangingProcess.HasExited)
                    {
                        return false;
                    }
                }
                _hangingProcess.Dispose();
                _hangingProcess = null;
            }
            catch (InvalidOperationException)
            {
                // Process already terminated or handle closed
                try { _hangingProcess?.Dispose(); } catch { }
                _hangingProcess = null;
            }
            catch
            {
                return false;
            }
        }
        return true;
    }

    public virtual async Task<bool> SetBoostModeAsync(CpuBoostMode mode, bool targetAc = true, bool targetDc = true)
    {
        await _powerLock.WaitAsync();
        try
        {
            if (!EnsureNoHangingProcess())
            {
                UpdateCurrentModeAfterFailure(targetAc, targetDc);
                return false;
            }

            if (BypassNativePowerCalls)
            {
                InvalidatePowerCfgCache();
                UpdateCurrentModeAfterSuccess(mode, targetAc, targetDc);
                IsModeVerified = true;
                return true;
            }

            return await Task.Run(() =>
            {
                try
                {
                    if (!BypassNativePowerCalls && TryGetActiveScheme(out var schemeGuid))
                    {
                        var subGroup = new Guid(SubProcessorGuid);
                        var setting = new Guid(PerfBoostModeGuid);
                        uint val = (uint)mode;
                        bool success = true;

                        if (targetAc)
                        {
                            if (PowerWriteACValueIndex(IntPtr.Zero, ref schemeGuid, ref subGroup, ref setting, val) != 0)
                            {
                                success = false;
                            }
                        }

                        if (success && targetDc)
                        {
                            if (PowerWriteDCValueIndex(IntPtr.Zero, ref schemeGuid, ref subGroup, ref setting, val) != 0)
                            {
                                success = false;
                            }
                        }

                        if (success)
                        {
                            if (PowerSetActiveScheme(IntPtr.Zero, ref schemeGuid) != 0)
                            {
                                success = false;
                            }
                        }

                        if (success)
                        {
                            // The value just changed, so the cached fallback read is stale by definition.
                            InvalidatePowerCfgCache();
                            UpdateCurrentModeAfterSuccess(mode, targetAc, targetDc);
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    // The powercfg path below is the fallback, so the boost mode still gets set; the reason the
                    // WMI path failed is what a bug report needs.
                    AppLog.Warn("CPU boost WMI yolundan ayarlanamadı", ex.Message);
                }

                return SetBoostModePowerCfg(mode, targetAc, targetDc);
            });
        }
        finally
        {
            _powerLock.Release();
        }
    }

    private void UpdateCurrentModeAfterSuccess(CpuBoostMode mode, bool targetAc, bool targetDc)
    {
        bool isAcActive = IsAcPowerActive();
        bool activeTargeted = isAcActive ? targetAc : targetDc;
        if (activeTargeted)
        {
            CurrentMode = mode;
            IsModeVerified = true;
        }
        else
        {
            var verified = QueryBoostModeExact(isAcActive);
            if (verified.HasValue)
            {
                CurrentMode = verified.Value;
                IsModeVerified = true;
            }
            else
            {
                IsModeVerified = false;
            }
        }
    }

    internal void UpdateCurrentModeAfterFailure(bool targetAc, bool targetDc)
    {
        bool isAcActive = IsAcPowerActive();
        var verified = QueryBoostModeExact(isAcActive);
        if (verified.HasValue)
        {
            CurrentMode = verified.Value;
            IsModeVerified = true;
        }
        else
        {
            IsModeVerified = false;
        }
    }

    internal bool SetBoostModePowerCfg(CpuBoostMode mode, bool targetAc = true, bool targetDc = true)
    {
        if (!EnsureNoHangingProcess())
        {
            UpdateCurrentModeAfterFailure(targetAc, targetDc);
            return false;
        }

        try
        {
            var val = (int)mode;
            // Remember the AC value so a failed DC write can be rolled back instead of leaving the plan half-changed.
            var previousAc = targetAc ? QueryBoostModeExact(true) : null;

            if (targetAc)
            {
                if (!RunPowerCfg($"/setacvalueindex SCHEME_CURRENT {SubProcessorGuid} {PerfBoostModeGuid} {val}"))
                {
                    UpdateCurrentModeAfterFailure(targetAc, targetDc);
                    return false;
                }
            }

            if (targetDc)
            {
                if (!RunPowerCfg($"/setdcvalueindex SCHEME_CURRENT {SubProcessorGuid} {PerfBoostModeGuid} {val}"))
                {
                    // The AC value was already written: put it back so a failed call does not leave the plan
                    // half-changed while the caller reports a plain failure.
                    if (previousAc.HasValue)
                    {
                        RunPowerCfg($"/setacvalueindex SCHEME_CURRENT {SubProcessorGuid} {PerfBoostModeGuid} {(int)previousAc.Value}");
                        RunPowerCfg("/setactive SCHEME_CURRENT");
                    }
                    UpdateCurrentModeAfterFailure(targetAc, targetDc);
                    return false;
                }
            }

            if (!RunPowerCfg("/setactive SCHEME_CURRENT"))
            {
                UpdateCurrentModeAfterFailure(targetAc, targetDc);
                return false;
            }

            InvalidatePowerCfgCache();
            UpdateCurrentModeAfterSuccess(mode, targetAc, targetDc);
            return true;
        }
        catch
        {
            UpdateCurrentModeAfterFailure(targetAc, targetDc);
            return false;
        }
    }

    internal bool RunPowerCfg(string args)
    {
        if (!EnsureNoHangingProcess())
        {
            return false;
        }

        if (BypassNativePowerCalls && ProcessInfoFactory == null)
        {
            return true;
        }

        Process? p = null;
        try
        {
            var psi = ProcessInfoFactory != null
                ? ProcessInfoFactory(args)
                : new ProcessStartInfo
                {
                    FileName = WindowsPaths.SystemTool("powercfg.exe"),
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

            p = Process.Start(psi);
            if (p == null) return false;

            // The write path runs from a button press on the UI thread, so it gets a shorter leash than the old
            // three seconds: powercfg answers in tens of milliseconds when it answers at all.
            if (!p.WaitForExit(1500))
            {
                bool exited = false;
                try
                {
                    p.Kill(entireProcessTree: true);
                    exited = p.WaitForExit(1000) && p.HasExited;
                }
                catch
                {
                    try { exited = p.HasExited; } catch { exited = false; }
                }

                if (!exited)
                {
                    _hangingProcess = p;
                    p = null;
                }
                return false;
            }

            return p.HasExited && p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            p?.Dispose();
        }
    }

    /// <summary>
    /// Releases a powercfg process that could not be killed on timeout. Without this the process outlives the app
    /// and every later boost write stays blocked.
    /// </summary>
    public void Dispose()
    {
        var hanging = _hangingProcess;
        _hangingProcess = null;
        if (hanging == null) return;
        try { if (!hanging.HasExited) hanging.Kill(entireProcessTree: true); } catch { }
        try { hanging.Dispose(); } catch { }
    }
}
