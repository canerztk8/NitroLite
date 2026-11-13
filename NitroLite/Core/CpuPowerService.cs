using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace NitroLite.Core;

public enum CpuBoostMode
{
    Disabled = 0,    // Windows PerfBoostMode 0: no boost, stays at base clock (~3.8 GHz on the 7840HS)
    Enabled = 1,     // Windows default boost
    Aggressive = 2,  // Windows PerfBoostMode 2: boost as high as the CPU allows
    Efficient = 4    // Windows PerfBoostMode 4 "Efficient Aggressive": boost on, efficiency-biased; no fixed frequency cap
}

public class CpuPowerService : IDisposable
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
            UnhideSetting();
            CurrentMode = QueryCurrentBoostMode(IsAcPowerActive());
        }
    }

    private static bool TryGetActiveScheme(out Guid schemeGuid)
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

    private void UnhideSetting()
    {
        if (BypassNativePowerCalls) return;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Control\Power\PowerSettings\{SubProcessorGuid}\{PerfBoostModeGuid}", true);
            if (key != null)
            {
                key.SetValue("Attributes", 2, RegistryValueKind.DWord);
            }
        }
        catch { }
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
                    if (val == 0) return CpuBoostMode.Disabled;
                    if (val == 4) return CpuBoostMode.Efficient;
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
                    ? ProcessInfoFactory($"/query SCHEME_CURRENT {SubProcessorGuid} {PerfBoostModeGuid}")
                    : new ProcessStartInfo
                    {
                        FileName = AppPaths.SystemTool("powercfg.exe"),
                        Arguments = $"/query SCHEME_CURRENT {SubProcessorGuid} {PerfBoostModeGuid}",
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
                    if (!p.WaitForExit(3000))
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

                    if (!readTask.Wait(1000))
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

            var pattern = isAc 
                ? @"Current AC Power Setting Index:\s*0x([0-9a-fA-F]+)"
                : @"Current DC Power Setting Index:\s*0x([0-9a-fA-F]+)";

            var match = Regex.Match(output, pattern);
            if (match.Success && int.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out var val))
            {
                if (val == 0) return CpuBoostMode.Disabled;
                if (val == 4) return CpuBoostMode.Efficient;
                return val <= 6 ? (CpuBoostMode)val : null;
            }
        }
        catch { }

        return null;
    }

    private readonly SemaphoreSlim _powerLock = new(1, 1);
    internal Process? _hangingProcess;
    internal Func<string, ProcessStartInfo>? ProcessInfoFactory { get; set; }

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
                            UpdateCurrentModeAfterSuccess(mode, targetAc, targetDc);
                            return true;
                        }
                    }
                }
                catch { }

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
                    FileName = AppPaths.SystemTool("powercfg.exe"),
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

            p = Process.Start(psi);
            if (p == null) return false;

            if (!p.WaitForExit(3000))
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
