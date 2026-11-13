using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Media;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace NitroLite.Core;

public class BatteryWatchdogService : IDisposable
{
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

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    private readonly PeriodicTimer _timer = new(TimeSpan.FromSeconds(3));
    private readonly CancellationTokenSource _cts = new();

    private readonly object _dischargeLock = new();
    private CancellationTokenSource? _dischargeCts;
    private bool _isDischarging = false;
    private bool _isDisposed = false;
    private double _smoothedDischargeRateMw = 0.0;
    private bool _emaInitialized = false;
    // Counts game-session ticks so the ~235 ms WMI query runs every fifth tick on battery instead of every tick.
    private int _gameSessionWmiTick = 0;

    public bool WatchdogEnabled { get; set; } = true;
    public bool IsGameSessionActive { get; set; } = false;
    public bool IsDischarging => _isDischarging;

    public BatteryStatusInfo CurrentStatus { get; private set; } = new();

    public event Action<BatteryStatusInfo>? StatusUpdated;
    public event Action<int>? OverchargeAlert;
    public event Action? DischargeTargetReached;

    public BatteryWatchdogService(bool attachSystemEvents = true)
    {
        if (attachSystemEvents)
        {
            try
            {
                SystemEvents.PowerModeChanged += OnPowerModeChanged;
            }
            catch { }
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        // The 80 % limit itself is stored by Acer Care Center (CareCenterClient) and survives sleep and
        // reboots, so it is not re-asserted here; only the status / overcharge alert is refreshed.
        if (e.Mode == PowerModes.Resume || e.Mode == PowerModes.StatusChange)
        {
            if (e.Mode == PowerModes.StatusChange)
            {
                _emaInitialized = false;
            }

            UpdateStatus();
        }
    }

    public void Start()
    {
        Task.Run(RunLoopAsync);
    }

    private async Task RunLoopAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                UpdateStatus();
            }
            catch
            {
                // Ignore transient errors
            }

            try
            {
                await _timer.WaitForNextTickAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                // Dispose() released the timer or the token source while this wait was still pending.
                break;
            }
        }
    }

    private readonly object _statusLock = new();

    /// <summary>
    /// Reads the battery state. Serialised (the main loop and the discharge loop both call it, and the discharge
    /// rate smoothing is stateful); events are raised outside the lock so a UI handler cannot deadlock it.
    /// </summary>
    public BatteryStatusInfo UpdateStatus()
    {
        BatteryStatusInfo info;
        bool raiseOvercharge;
        lock (_statusLock)
        {
            info = ComputeStatus();
            raiseOvercharge = info.IsOvercharged && !CurrentStatus.IsOvercharged;
            CurrentStatus = info;
        }
        if (raiseOvercharge) OverchargeAlert?.Invoke(info.BatteryPercent);
        StatusUpdated?.Invoke(info);
        return info;
    }

    private BatteryStatusInfo ComputeStatus()
    {
        var info = new BatteryStatusInfo();
        if (GetSystemPowerStatus(out var status))
        {
            info.IsPluggedIn = status.ACLineStatus == 1;
            // 255 is the "unknown" sentinel for both fields. Turning it into 0 % and "charging" reported a flat,
            // charging battery whenever the status could not be read.
            info.BatteryPercent = status.BatteryLifePercent <= 100 ? status.BatteryLifePercent : -1;
            info.IsCharging = status.BatteryFlag != 255 && (status.BatteryFlag & 8) != 0;
            info.EstimatedRemainingMinutes = status.BatteryLifeTime > 0 ? status.BatteryLifeTime / 60 : -1;
            info.IsWatchdogActive = WatchdogEnabled;
            // Only meaningful while the 80 % limit is on: with it off, charging to 100 % is the user's choice.
            info.IsOvercharged = WatchdogEnabled && info.IsPluggedIn && info.BatteryPercent > 80;
        }

        // GAMING OPTIMIZATION: the WMI query below costs ~235 ms. On AC during a game session it is skipped
        // entirely; on battery it runs once every five ticks (15 s) so the discharge rate stays a real reading
        // without paying the cost every 3 s.
        if (IsGameSessionActive)
        {
            if (info.IsPluggedIn)
            {
                info.IsDischarging = false;
                info.DischargeRateWatts = 0.0;
                info.ChargeRateWatts = 0.0;
                info.SmoothedDischargeRateWatts = 0.0;
                info.ChargeRateUnknown = true;
                return info;
            }

            if (++_gameSessionWmiTick % 5 != 0)
            {
                // Reuse the last measured rate: a running game on battery is discharging, so reporting 0 W would
                // be wrong. The OS estimate from GetSystemPowerStatus stays as the remaining-time figure.
                info.IsDischarging = true;
                if (CurrentStatus.IsDischarging)
                {
                    info.DischargeRateWatts = CurrentStatus.DischargeRateWatts;
                    info.DischargeRateMilliwatts = CurrentStatus.DischargeRateMilliwatts;
                    info.SmoothedDischargeRateWatts = CurrentStatus.SmoothedDischargeRateWatts;
                    info.RemainingCapacityMWh = CurrentStatus.RemainingCapacityMWh;
                }
                info.ChargeRateUnknown = true;
                return info;
            }
        }
        else
        {
            _gameSessionWmiTick = 0;
        }

        // Live Real-Time Discharge / Charge Rate via in-process WMI with full disposal
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI",
                "SELECT DischargeRate, ChargeRate, RemainingCapacity, Voltage, Discharging FROM BatteryStatus");
            using var results = searcher.Get();
            foreach (ManagementObject obj in results)
            {
                using (obj)
                {
                    var dr = Convert.ToDouble(obj["DischargeRate"] ?? 0);
                    var cr = Convert.ToDouble(obj["ChargeRate"] ?? 0);
                    var rem = Convert.ToDouble(obj["RemainingCapacity"] ?? 0);
                    var isDisch = Convert.ToBoolean(obj["Discharging"] ?? false);

                    info.IsDischarging = isDisch || (!info.IsPluggedIn && dr > 0);
                    info.DischargeRateWatts = Math.Round(dr / 1000.0, 1);
                    info.DischargeRateMilliwatts = dr;
                    info.ChargeRateWatts = Math.Round(cr / 1000.0, 1);
                    info.RemainingCapacityMWh = rem;

                    if (info.IsDischarging && dr > 0)
                    {
                        _smoothedDischargeRateMw = TelemetryFormatter.UpdateSmoothedRate(_smoothedDischargeRateMw, dr, ref _emaInitialized, 0.20);
                        info.SmoothedDischargeRateWatts = Math.Round(_smoothedDischargeRateMw / 1000.0, 1);
                        info.EstimatedRemainingMinutes = TelemetryFormatter.CalculateRemainingMinutes(rem, _smoothedDischargeRateMw);
                    }
                    else
                    {
                        _emaInitialized = false;
                        _smoothedDischargeRateMw = 0.0;
                        info.SmoothedDischargeRateWatts = 0.0;
                    }
                    break;
                }
            }
        }
        catch { }

        // No battery temperature sensor is exposed on this model: leave it unknown ("--") instead of the old
        // wattage-based guess (28.5 °C + W × 0.08) that was shown in the component matrix. The ACPI thermal-zone
        // query that used to run here every 3 s was removed: its result was never published, and the WMI call is
        // slow enough to matter in a loop this tight.
        return info;
    }

    public async Task<BatteryHealthDetails> GetBatteryHealthDetailsAsync()
    {
        return await Task.Run(() =>
        {
            var details = new BatteryHealthDetails();

            // 1. In-process WMI query: BatteryStaticData (isolated try/catch)
            try
            {
                using var design = new ManagementObjectSearcher(@"root\WMI", "SELECT DesignedCapacity FROM BatteryStaticData");
                using var rows = design.Get();
                foreach (ManagementObject row in rows)
                {
                    using (row)
                    {
                        var cap = Convert.ToDouble(row["DesignedCapacity"] ?? 0);
                        if (cap > 0) details.DesignCapacityMWh = cap;
                        break;
                    }
                }
            }
            catch { }

            // 2. In-process WMI query: FullChargedCapacity (isolated try/catch)
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity");
                using var results = searcher.Get();
                foreach (ManagementObject obj in results)
                {
                    using (obj)
                    {
                        var fcc = Convert.ToDouble(obj["FullChargedCapacity"] ?? 0);
                        if (fcc > 0) details.FullChargeCapacityMWh = fcc;
                        break;
                    }
                }
            }
            catch { }

            // 3. In-process WMI query: BatteryStatus (isolated try/catch)
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT RemainingCapacity, Voltage FROM BatteryStatus");
                using var results = searcher.Get();
                foreach (ManagementObject obj in results)
                {
                    using (obj)
                    {
                        var rem = Convert.ToDouble(obj["RemainingCapacity"] ?? 0);
                        var volt = Convert.ToDouble(obj["Voltage"] ?? 0);
                        if (rem > 0) details.RemainingCapacityMWh = rem;
                        if (volt > 0) details.VoltageVolts = Math.Round(volt / 1000.0, 2);
                        break;
                    }
                }
            }
            catch { }

            // 4. Fallback for DesignCapacity if WMI BatteryStaticData failed (Win11 AMD ACPI 0x80041001)
            if (details.DesignCapacityMWh <= 0)
            {
                details.DesignCapacityMWh = QueryDesignCapacityFromSystem(out bool designEstimated);
                details.IsDesignCapacityEstimated = designEstimated;
            }

            // 5. Fallback for Voltage if BatteryStatus WMI was empty
            if (details.VoltageVolts <= 0)
            {
                try
                {
                    using var batSearcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT DesignVoltage FROM Win32_Battery");
                    using var bats = batSearcher.Get();
                    foreach (ManagementObject b in bats)
                    {
                        using (b)
                        {
                            var dv = Convert.ToDouble(b["DesignVoltage"] ?? 0);
                            if (dv > 0) { details.VoltageVolts = Math.Round(dv / 1000.0, 2); break; }
                        }
                    }
                }
                catch { }

                // Still nothing: leave it unknown ("--") instead of a model-specific 16.45 V constant.
            }

            // 6. Identity and cycle count (Win32_Battery / BatteryCycleCount).
            try
            {
                using var bs = new ManagementObjectSearcher(@"root\cimv2", "SELECT Name, DeviceID, Chemistry FROM Win32_Battery");
                using var rows = bs.Get();
                foreach (ManagementObject b in rows)
                {
                    using (b)
                    {
                        details.ModelName = (b["Name"]?.ToString() ?? "").Trim();
                        details.Manufacturer = ParseBatteryManufacturer(b["DeviceID"]?.ToString(), details.ModelName);
                        details.Chemistry = BatteryChemistryName(Convert.ToInt32(b["Chemistry"] ?? 0));
                        break;
                    }
                }
            }
            catch { }

            try
            {
                using var cs = new ManagementObjectSearcher(@"root\WMI", "SELECT CycleCount FROM BatteryCycleCount");
                using var rows = cs.Get();
                foreach (ManagementObject c in rows)
                {
                    using (c)
                    {
                        int cycles = Convert.ToInt32(c["CycleCount"] ?? 0);
                        details.CycleCount = cycles > 0 ? cycles : null; // 0 = not reported by the firmware
                        break;
                    }
                }
            }
            catch { }

            // No full-charge capacity from Windows: leave it unknown. (The old "design × 0.923" guess happened to
            // match this AN16-41 but would be shown as a measurement anywhere else.)

            if (details.DesignCapacityMWh > 0 && details.FullChargeCapacityMWh > 0)
            {
                var wear = (1.0 - (details.FullChargeCapacityMWh / details.DesignCapacityMWh)) * 100.0;
                details.WearLevelPercent = Math.Max(0, Math.Round(wear, 1));
                details.HealthPercent = Math.Clamp(Math.Round(100.0 - details.WearLevelPercent, 1), 0, 100);
            }

            return details;
        });
    }

    private static double _cachedDesignCapacityMWh;

    private static double QueryDesignCapacityFromSystem(out bool isEstimated)
    {
        isEstimated = false;

        // Design capacity never changes; run powercfg once per process instead of on every Battery tab visit.
        var cached = _cachedDesignCapacityMWh;
        if (cached > 0) return cached;

        var tempXml = AppPaths.UniqueTempFile("nitrolite_bat", ".xml");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = AppPaths.SystemTool("powercfg.exe"),
                Arguments = $"/batteryreport /xml /output \"{tempXml}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                if (!proc.WaitForExit(5000))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                }
                else if (File.Exists(tempXml))
                {
                    var xml = File.ReadAllText(tempXml);
                    var match = Regex.Match(xml, @"<DesignCapacity>(\d+)</DesignCapacity>");
                    if (match.Success && double.TryParse(match.Groups[1].Value, out double cap) && cap > 0)
                    {
                        _cachedDesignCapacityMWh = cap;
                        return cap;
                    }
                }
            }
        }
        catch { }
        finally
        {
            try { if (File.Exists(tempXml)) File.Delete(tempXml); } catch { }
        }

        // Unknown: no model-specific constant (the old 90,614 mWh AN16-41 value would be wrong on other batteries).
        isEstimated = false;
        return 0;
    }

    /// <summary>Win32_Battery.Chemistry codes.</summary>
    public static string BatteryChemistryName(int code) => code switch
    {
        3 => Loc.T("Kurşun asit"),
        4 => Loc.T("Nikel kadmiyum"),
        5 => Loc.T("Nikel metal hidrit"),
        6 => Loc.T("Lityum-iyon"),
        7 => Loc.T("Çinko hava"),
        8 => Loc.T("Lityum polimer"),
        _ => ""
    };

    /// <summary>
    /// Win32_Battery.DeviceID is typically serial + manufacturer + name (e.g. "34643LGCAP21A8T"): strip the name at
    /// the end and the leading serial digits to get the manufacturer ("LGC"). Empty when it does not follow that shape.
    /// </summary>
    public static string ParseBatteryManufacturer(string? deviceId, string? name)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(name)) return "";
        var id = deviceId.Trim();
        var n = name.Trim();
        if (!id.EndsWith(n, StringComparison.OrdinalIgnoreCase) || id.Length <= n.Length) return "";
        var prefix = id.Substring(0, id.Length - n.Length).TrimStart('0', '1', '2', '3', '4', '5', '6', '7', '8', '9').Trim();
        return prefix.Length is > 0 and <= 16 ? prefix : "";
    }

    /// <summary>Raised whenever discharge starts (true) or stops for any reason (false), e.g. the adapter was reconnected.</summary>
    public event Action<bool>? DischargeStateChanged;

    /// <summary>Starts the load. Returns false (nothing started) on AC power or when already at or below 80 %.</summary>
    public bool StartSafeDischarge()
    {
        CancellationTokenSource cts;
        lock (_dischargeLock)
        {
            if (_isDisposed) return false;
            if (_isDischarging) return true;
            if (!GetSystemPowerStatus(out var power) || power.ACLineStatus != 0 || power.BatteryLifePercent is <= 80 or > 100)
                return false;
            _isDischarging = true;
            cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            _dischargeCts = cts;
        }
        try { DischargeStateChanged?.Invoke(true); }
        finally { _ = RunDischargeAsync(cts); }
        return true;
    }

    private async Task RunDischargeAsync(CancellationTokenSource cts)
    {
        var token = cts.Token;
        var workers = new Task[2];
        for (int i = 0; i < workers.Length; i++)
        {
            workers[i] = Task.Run(() =>
            {
                while (!token.IsCancellationRequested)
                {
                    double d = 1.0001;
                    for (int j = 0; j < 50000; j++) d = Math.Sin(d) * Math.Cos(d);
                    Thread.Sleep(5);
                }
            });
        }

        bool reachedTarget = false;
        try
        {
            while (!token.IsCancellationRequested)
            {
                // This safety check needs only adapter and charge state. WMI telemetry must not block it.
                if (!GetSystemPowerStatus(out var power) || power.ACLineStatus != 0 || power.BatteryLifePercent > 100)
                    break;
                if (power.BatteryLifePercent <= 80)
                {
                    reachedTarget = true;
                    break;
                }
                await Task.Delay(500, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            cts.Cancel();
            await Task.WhenAll(workers).ConfigureAwait(false);
            bool notify;
            lock (_dischargeLock)
            {
                notify = ReferenceEquals(_dischargeCts, cts);
                if (notify)
                {
                    _dischargeCts = null;
                    _isDischarging = false;
                }
                cts.Dispose();
            }
            if (notify)
            {
                DischargeStateChanged?.Invoke(false);
                if (reachedTarget)
                {
                    try { SystemSounds.Asterisk.Play(); } catch { }
                    DischargeTargetReached?.Invoke();
                }
            }
        }
    }

    public void StopSafeDischarge()
    {
        lock (_dischargeLock)
        {
            if (!_isDischarging) return;
            _isDischarging = false;
            try
            {
                _dischargeCts?.Cancel();
                _dischargeCts = null;
            }
            catch { }
        }
        DischargeStateChanged?.Invoke(false);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        try
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        }
        catch { }

        _cts.Cancel();
        _cts.Dispose();
        lock (_dischargeLock)
        {
            try
            {
                _dischargeCts?.Cancel();
                _dischargeCts = null;
                _isDischarging = false;
            }
            catch { }
        }
        _timer.Dispose();
    }
}
