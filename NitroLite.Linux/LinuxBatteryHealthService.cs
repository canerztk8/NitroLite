using System.Globalization;
using NitroLite.Core;
using NitroLite.Linux.Sysfs;

namespace NitroLite.Linux;

/// <summary>
/// The battery, read from <c>/sys/class/power_supply/BAT*</c>.
/// </summary>
/// <remarks>
/// <para>
/// The kernel exposes the same facts Windows reports through WMI, under different names: <c>capacity</c> is the level,
/// <c>power_now</c> the flow in microwatts, <c>energy_full_design</c> and <c>energy_full</c> the two capacities the wear
/// figure is computed from. Some drivers report charge in microamp-hours instead of energy in microwatt-hours, so both
/// spellings are read and the unit is carried through rather than assumed.
/// </para>
/// <para>
/// A machine with no <c>BAT*</c> node reports <see cref="IsSupported"/> false and the page hides its battery cards. A
/// desktop is not a broken laptop.
/// </para>
/// </remarks>
public sealed class LinuxBatteryHealthService : IBatteryHealthService
{
    private readonly ISysfsFileSystem _fs;
    private readonly string _root;
    private readonly BatteryWearHistory _history;
    private readonly string? _batteryDirectory;

    /// <param name="fs">The sysfs access, so the whole service runs against an in-memory tree in a test.</param>
    /// <param name="root">The power-supply root; the default is the kernel's.</param>
    /// <param name="historyPath">Null uses the standard data folder; an explicit path is what a test passes.</param>
    public LinuxBatteryHealthService(
        ISysfsFileSystem fs,
        string root = LinuxPaths.PowerSupplyRoot,
        string? historyPath = null)
    {
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _root = root;
        _history = new BatteryWearHistory(historyPath);
        _batteryDirectory = FindBattery(fs, root);
    }

    public bool IsSupported => _batteryDirectory != null;

    public bool WatchdogEnabled { get; set; } = true;

    public Task<BatteryHealthSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (_batteryDirectory == null) return Task.FromResult(BatteryHealthSnapshot.Empty);

        var status = ReadStatus(_batteryDirectory);
        var details = ReadDetails(_batteryDirectory);
        return Task.FromResult(new BatteryHealthSnapshot { Status = status, Details = details });
    }

    public IReadOnlyList<BatteryWearSample> GetWearSamples() => _history.GetSamples();

    public BatteryWearTrend GetWearTrend() => _history.GetTrend();

    public bool RecordWearSample(BatteryHealthDetails details) => _history.TryRecord(details);

    public bool ClearWearHistory() => _history.Clear();

    /// <summary>
    /// Nothing to start: the Linux service reads <c>/sys/class/power_supply</c> on demand, and a background poll of a
    /// sysfs file would be a wakeup per tick for a value that only changes when the page is open.
    /// </summary>
    public void Start()
    {
    }

    /// <summary>Nothing to release: the service holds no timer, no handle and no unmanaged memory.</summary>
    public void Dispose()
    {
    }

    private BatteryStatusInfo ReadStatus(string directory)
    {
        var status = new BatteryStatusInfo();

        var capacity = ReadInt(directory, "capacity");
        status.BatteryPercent = capacity is >= 0 and <= 100 ? capacity.Value : -1;

        var state = ReadText(directory, "status") ?? "";
        bool charging = state.Equals("Charging", StringComparison.OrdinalIgnoreCase);
        bool discharging = state.Equals("Discharging", StringComparison.OrdinalIgnoreCase);
        bool full = state.Equals("Full", StringComparison.OrdinalIgnoreCase);

        status.IsCharging = charging;
        status.IsDischarging = discharging;

        // The adapter node is the authority on the plug state, not the battery's own status word: a pack held at the
        // 80 % limit reports "Not charging" while the charger is very much connected.
        var (onAc, _) = LinuxPowerSupply.Read(_fs, _root);
        status.IsPluggedIn = onAc ?? (charging || full);

        // power_now is microwatts on every driver that has it; current_now × voltage_now is the fallback for the ones
        // that report current instead.
        double watts = ReadPowerWatts(directory);
        if (discharging)
        {
            status.DischargeRateWatts = watts;
            status.DischargeRateMilliwatts = watts * 1000.0;
            status.SmoothedDischargeRateWatts = watts;
        }
        else if (charging)
        {
            status.ChargeRateWatts = watts;
        }
        else
        {
            // Plugged in and neither charging nor discharging: the rate is a real zero, not an unread value.
            status.ChargeRateWatts = 0;
        }

        status.ChargeRateUnknown = !charging && !discharging && !full;

        // The same conversion as the details: a charge-based driver's µAh is not mWh until the voltage is applied, and
        // the two figures on one page must not disagree by the pack voltage.
        var remaining = ReadEnergyMWh(directory, "energy_now", "charge_now");
        if (remaining > 0) status.RemainingCapacityMWh = remaining;

        // The kernel's own estimate. time_to_empty_now is in seconds (power_supply ABI), the model carries minutes.
        // Absent on most drivers, which is why the page hides the row when the
        // machine is on the adapter rather than printing a placeholder.
        var seconds = ReadInt(directory, "time_to_empty_now");
        status.EstimatedRemainingMinutes = seconds is > 0 ? seconds.Value / 60 : 0;

        status.IsWatchdogActive = WatchdogEnabled;
        status.IsOvercharged = WatchdogEnabled && status.IsPluggedIn && status.BatteryPercent >= 80;

        return status;
    }

    private BatteryHealthDetails? ReadDetails(string directory)
    {
        // energy_* is in microwatt-hours, charge_* in microamp-hours. Both are divided by 1000 to reach the mWh the
        // shared model carries; a charge-based driver's figure is an approximation of energy, which is why the voltage
        // is used to convert it when it is available.
        double design = ReadEnergyMWh(directory, "energy_full_design", "charge_full_design");
        double full = ReadEnergyMWh(directory, "energy_full", "charge_full");

        if (design <= 0 && full <= 0) return null;

        var details = new BatteryHealthDetails
        {
            DesignCapacityMWh = design,
            FullChargeCapacityMWh = full,
            RemainingCapacityMWh = ReadEnergyMWh(directory, "energy_now", "charge_now"),
            VoltageVolts = (ReadInt(directory, "voltage_now") ?? 0) / 1_000_000.0,
            Manufacturer = ReadText(directory, "manufacturer") ?? "",
            ModelName = ReadText(directory, "model_name") ?? "",
            Chemistry = DescribeChemistry(ReadText(directory, "technology")),
            CycleCount = ReadInt(directory, "cycle_count") is int cycles and > 0 ? cycles : null
        };

        if (design > 0 && full > 0)
        {
            details.HealthPercent = Math.Clamp(full / design * 100.0, 0, 100);
            details.WearLevelPercent = Math.Clamp(100.0 - details.HealthPercent, 0, 100);
        }

        return details;
    }

    /// <summary>
    /// Reads an energy attribute in mWh, falling back to the charge spelling. A charge-based driver reports µAh, which
    /// is only an energy once it is multiplied by the pack voltage; without a voltage the figure is left as-is rather
    /// than being scaled by a guess.
    /// </summary>
    private double ReadEnergyMWh(string directory, string energyName, string chargeName)
    {
        if (ReadInt(directory, energyName) is int energy) return energy / 1000.0;
        if (ReadInt(directory, chargeName) is not int charge) return 0;

        double volts = (ReadInt(directory, "voltage_now") ?? 0) / 1_000_000.0;
        // µAh × V = µWh, then / 1000 for mWh.
        return volts > 0 ? charge * volts / 1000.0 : charge / 1000.0;
    }

    private double ReadPowerWatts(string directory)
    {
        if (ReadInt(directory, "power_now") is int microwatts) return microwatts / 1_000_000.0;

        if (ReadInt(directory, "current_now") is int microamps && ReadInt(directory, "voltage_now") is int microvolts)
        {
            return Math.Abs(microamps / 1_000_000.0 * (microvolts / 1_000_000.0));
        }

        return 0;
    }

    /// <summary>The kernel's <c>technology</c> word, in the same terms the Windows chemistry field uses.</summary>
    private static string DescribeChemistry(string? technology) => technology?.Trim().ToLowerInvariant() switch
    {
        "li-ion" or "li-poly" or "lipo" => Loc.T("Lityum-iyon"),
        "lifepo4" => Loc.T("Lityum demir fosfat"),
        "nimh" => Loc.T("Nikel metal hidrit"),
        "nicd" => Loc.T("Nikel kadmiyum"),
        "lead-acid" => Loc.T("Kurşun asit"),
        _ => ""
    };

    private string? ReadText(string directory, string name) =>
        _fs.ReadAllText(LinuxPaths.Combine(directory, name))?.Trim();

    private int? ReadInt(string directory, string name)
    {
        var text = ReadText(directory, name);
        if (string.IsNullOrWhiteSpace(text)) return null;
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    /// <summary>The first <c>BAT*</c> directory under the power-supply root, or null when there is none.</summary>
    private static string? FindBattery(ISysfsFileSystem fs, string root)
    {
        foreach (var directory in fs.EnumerateDirectories(root))
        {
            var name = directory.TrimEnd('/');
            int slash = name.LastIndexOf('/');
            if (slash >= 0) name = name[(slash + 1)..];
            if (name.StartsWith("BAT", StringComparison.OrdinalIgnoreCase)) return directory;
        }

        return null;
    }
}
