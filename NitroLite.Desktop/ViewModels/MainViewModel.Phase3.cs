using System.Globalization;
using System.IO;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Core.Hardware.Snapshot;
using NitroLite.Desktop.Mvvm;

namespace NitroLite.Desktop.ViewModels;

/// <summary>One drive in the storage card.</summary>
public sealed class StorageDriveRow
{
    public StorageDriveRow(StorageDrive drive)
    {
        LetterTag = drive.LetterTag;
        Model = drive.Model;
        BusType = drive.BusType;
        FreeText = drive.FreeText;
        UsedPercent = Math.Clamp(drive.UsedPercent, 0, 100);
        // The unit stays on the line even with no reading: a bare dash beside a drive says nothing about what is
        // missing, while "-- °C" says the app asked for a temperature and did not get one.
        TemperatureText = drive.TemperatureC is int celsius ? $"{celsius} °C" : "-- °C";
        HealthText = drive.HealthText;
        Healthy = drive.Healthy;
    }

    public string LetterTag { get; }
    public string Model { get; }
    public string BusType { get; }
    public string FreeText { get; }
    public double UsedPercent { get; }
    public string TemperatureText { get; }
    public string HealthText { get; }
    public bool Healthy { get; }

    /// <summary>The tick or cross the health badge prints.</summary>
    public string HealthGlyph => Healthy ? "\u2713" : "\u2715";

    // The column headings live on the row rather than on the page: a compiled binding inside the item template cannot
    // reach the parent's Labels, and a heading that is not translated is worse than one that repeats.
    public string FreeLabel => Loc.T("Boş alan");
    public string TemperatureLabel => Loc.T("Sıcaklık");
    public string HealthLabel => Loc.T("Sağlık");
}

/// <summary>One Acer background service in the services card.</summary>
public sealed class AcerServiceRow
{
    public AcerServiceRow(AcerServiceEntry entry)
    {
        Name = entry.Name;
        Label = entry.Label;
        Status = entry.Status;
        StartType = entry.StartType;
        IsDisabled = entry.IsDisabled;
    }

    public string Name { get; }
    public string Label { get; }
    public string Status { get; }
    public string StartType { get; }
    public bool IsDisabled { get; }

    /// <summary>
    /// The one line the row prints. The start type is already folded into the status by the platform service — a
    /// disabled service reports "Devre dışı", a stopped manual one says so in words — so printing the raw enum beside
    /// it only repeated the answer in a second language.
    /// </summary>
    public string StateText => Status;
}

/// <summary>One entry in the DC power-profile picker.</summary>
public sealed class DcProfileChoice
{
    public DcProfileChoice(DcPowerProfilePreference value, string label, string description)
    {
        Value = value;
        Label = label;
        Description = description;
    }

    public DcPowerProfilePreference Value { get; }
    public string Label { get; }
    public string Description { get; }

    public override string ToString() => Label;
}

/// <summary>
/// Phase 3 of the WPF parity work: the battery and thermal page, the services and tools page, and the first-run
/// walkthrough.
/// </summary>
/// <remarks>
/// <para>
/// A second file rather than another thousand lines in the first one. The split is by page, which is also the split the
/// WPF window used (<c>MainWindow.Battery.cs</c>, <c>MainWindow.Debloat.cs</c>, <c>MainWindow.Settings.cs</c>), so a
/// reader looking for the battery logic finds it in one place.
/// </para>
/// <para>
/// Every service here is optional. A platform that does not supply one leaves the corresponding card hidden rather than
/// offering a control that writes nowhere, which is the same rule the rest of the window follows.
/// </para>
/// </remarks>
public sealed partial class MainViewModel
{
    // ---------------------------------------------------------------- the services this page needs

    private readonly IBatteryHealthService? _batteryHealth;
    private readonly IStorageHealthService? _storageHealth;
    private readonly IDebloatService? _debloat;
    private readonly ISystemToolsService? _systemTools;
    private readonly IUpdateService? _updates;
    private readonly IDiagnosticsService? _diagnostics;
    private readonly IDirectWmiControl? _directWmi;
    private readonly IRunAtStartupService? _runAtStartup;
    private readonly INitroKeyListener? _nitroKey;
    private readonly INitroSenseWatcher? _nitroSense;

    /// <summary>
    /// URL opener injected by the composition root. When null, OpenUrl logs and does nothing — no shell fallback,
    /// so a test fixture that forgot a capturer cannot open a real browser on the developer's machine.
    /// </summary>
    private readonly Action<string>? _urlOpener;

    // ---------------------------------------------------------------- battery page state

    private BatteryHealthSnapshot _batterySnapshot = BatteryHealthSnapshot.Empty;
    private BatteryWearTrend _wearTrend = new();
    private IReadOnlyList<double> _wearTrendPoints = Array.Empty<double>();
    private string _wearTrendSummary = "";
    private string _wearTrendNote = "";
    private bool _wearTrendVisible;
    private DcProfileChoice? _selectedDcProfile;
    private bool _watchdogOn;
    private bool _batteryBusy;
    private bool? _lastBatteryOnAc;

    // ---------------------------------------------------------------- storage

    private IReadOnlyList<StorageDriveRow> _storageDrives = Array.Empty<StorageDriveRow>();

    // ---------------------------------------------------------------- services page state

    private IReadOnlyList<AcerServiceRow> _acerServices = Array.Empty<AcerServiceRow>();
    private bool _nitroSenseRunning;
    private bool _nitroSenseLauncherBlocked;
    private bool _servicesBusy;
    private string _servicesStatus = "";
    private string _repairToolsStatus = "";
    private bool _toolsBusy;

    // Both cards open closed. The state is per session rather than persisted: a user who opens the service list once
    // to look at it does not want it open on every launch afterwards.
    private bool _acerSectionExpanded;
    private bool _startupSectionExpanded;

    // ---------------------------------------------------------------- update, diagnostics, backup

    private UpdateCheckResult? _pendingUpdate;
    private string _updateStatus = "";
    private bool _updateBusy;
    private string _diagnosticsNote = "";

    // The snapshot pair: A is kept in memory between the two clicks, and is the only state the tool needs.
    private HardwareSnapshot? _snapshotA;
    private string _snapshotDiffStatus = "";

    // ---------------------------------------------------------------- the first-run walkthrough

    private int _onboardingStep;
    private bool _showOnboarding;
    private bool _onboardingAutoStartOn;

    /// <summary>
    /// How often the slow battery read runs, in telemetry ticks. The level and the flow come from the fast reader the
    /// header already uses; the capacities come from WMI, which costs tens of milliseconds and does not change between
    /// one minute and the next.
    /// </summary>
    /// <remarks>
    /// Five minutes rather than thirty seconds. The number it produces is the design capacity, the full-charge capacity
    /// and the cycle count, all of which move about once a day: reading them every half-minute bought nothing and kept
    /// a WMI connection (and, through it, the battery driver) awake for the whole session. Opening the battery page
    /// still reads on the spot, which is when the user can actually see the difference.
    /// </remarks>
    private const int BatteryHealthTickInterval = 300;

    // ================================================================================================================
    // Battery and thermal page
    // ================================================================================================================

    /// <summary>True when this machine has a battery the platform can read.</summary>
    public bool BatteryHealthAvailable => _batteryHealth?.IsSupported == true;

    /// <summary>True when the capacity half of the reading is present, so the wear card has numbers to print.</summary>
    public bool BatteryCapacityAvailable =>
        BatteryHealthAvailable && _batterySnapshot.Details is { DesignCapacityMWh: > 0, FullChargeCapacityMWh: > 0 };

    /// <summary>The big figure on the charge card, or "-- %" when the level could not be read.</summary>
    public string BatteryLevelText =>
        _batterySnapshot.Status.BatteryPercent >= 0 ? $"{_batterySnapshot.Status.BatteryPercent}%" : "-- %";

    /// <summary>
    /// The state sentence. Windows keeps its "charging" flag set while the 80 % limit holds the pack at 0 W, so the
    /// wording is decided by the actual current when it was read.
    /// </summary>
    public string BatteryFlowStateText
    {
        get
        {
            var status = _batterySnapshot.Status;
            if (!status.IsPluggedIn) return Labels.OnBattery;
            if (status.ChargeRateUnknown) return Labels.OnAdapter;
            if (status.ChargeRateWatts > 0) return Labels.OnAdapterCharging;
            return _watchdogOn && status.BatteryPercent >= 78 ? Labels.OnAdapterHeld : Labels.OnAdapterIdle;
        }
    }

    /// <summary>The live power flow, e.g. <c>0,0 W (Adaptörde)</c> or <c>12,4 W (Deşarj)</c>.</summary>
    public string BatteryPowerFlowText
    {
        get
        {
            var status = _batterySnapshot.Status;
            if (status.IsDischarging && status.DischargeRateWatts > 0)
            {
                return string.Format(CultureInfo.CurrentCulture, "{0:F1} W ({1})",
                    status.DischargeRateWatts, Labels.Discharging);
            }

            if (status.IsCharging && status.ChargeRateWatts > 0)
            {
                return string.Format(CultureInfo.CurrentCulture, "{0:F1} W ({1})",
                    status.ChargeRateWatts, Labels.Charging);
            }

            return status.IsPluggedIn
                ? string.Format(CultureInfo.CurrentCulture, "0,0 W ({0})", Labels.OnAdapter)
                : "-- W";
        }
    }

    /// <summary>
    /// The remaining-time line. Hidden on the adapter: there is no "time left" when the machine is not running off the
    /// pack, and the estimate the API returns in that state is a placeholder that reads as "calculating" forever.
    /// </summary>
    public string BatteryRemainingText
    {
        get
        {
            int minutes = _batterySnapshot.Status.EstimatedRemainingMinutes;
            if (minutes <= 0) return Labels.RemainingUnknown;
            var span = TimeSpan.FromMinutes(minutes);
            return string.Format(CultureInfo.CurrentCulture, "{0} sa {1} dk", (int)span.TotalHours, span.Minutes);
        }
    }

    public bool ShowBatteryRemaining => BatteryHealthAvailable && !_batterySnapshot.Status.IsPluggedIn;

    /// <summary>True when the pack is above the 80 % threshold the watchdog watches.</summary>
    public bool ShowOverchargeBanner => BatteryHealthAvailable && _batterySnapshot.Status.IsOvercharged;

    public string OverchargeMessage => Labels.OverchargeMessage(_batterySnapshot.Status.BatteryPercent);

    /// <summary>
    /// True when the Care Center agent refused the connection because its certificate changed. The charge limit is
    /// then unreachable, and without this the page would say "no agent" and the user would conclude the laptop does
    /// not support the limit at all.
    /// </summary>
    public bool ShowRetrustCertificate => _hardware is ICareCenterTrust { CertificateMismatch: true };

    /// <summary>The explanation shown next to the re-trust button.</summary>
    public string RetrustCertificateNote => Labels.RetrustCertificateNote;

    public string RetrustCertificate => Labels.RetrustCertificate;

    /// <summary>The health figure, marked as an estimate when the capacities behind it were not measured.</summary>
    public string BatteryHealthText
    {
        get
        {
            if (_batterySnapshot.Details is not { } details) return "--";
            if (details.DesignCapacityMWh <= 0 || details.FullChargeCapacityMWh <= 0) return "--";

            var text = string.Format(CultureInfo.CurrentCulture, "{0:F1} %", details.HealthPercent);
            return details.IsDesignCapacityEstimated || details.IsFullChargeCapacityEstimated
                ? text + TelemetryFormatter.EstimatedSuffix
                : text;
        }
    }

    /// <summary>One word under the health figure: good, worn, or time to replace it.</summary>
    public string BatteryHealthNote
    {
        get
        {
            if (_batterySnapshot.Details is not { } details) return Labels.CapacityUnreadable;
            if (details.DesignCapacityMWh <= 0 || details.FullChargeCapacityMWh <= 0) return Labels.CapacityUnreadable;
            if (details.HealthPercent >= 80) return Labels.HealthGood;
            return details.HealthPercent >= 60 ? Labels.HealthWorn : Labels.HealthReplace;
        }
    }

    public string BatteryWearText
    {
        get
        {
            if (_batterySnapshot.Details is not { } details) return "--";
            if (details.DesignCapacityMWh <= 0 || details.FullChargeCapacityMWh <= 0) return "--";

            var text = string.Format(CultureInfo.CurrentCulture, "{0:F1} %", details.WearLevelPercent);
            return details.IsDesignCapacityEstimated || details.IsFullChargeCapacityEstimated
                ? text + TelemetryFormatter.EstimatedSuffix
                : text;
        }
    }

    public string BatteryWearNote
    {
        get
        {
            if (_batterySnapshot.Details is not { } details) return "--";
            if (details.DesignCapacityMWh <= 0 || details.FullChargeCapacityMWh <= 0) return "--";
            if (details.WearLevelPercent < 10) return Labels.WearLow;
            return details.WearLevelPercent < 25 ? Labels.WearMedium : Labels.WearHigh;
        }
    }

    public string BatteryFullChargeText => _batterySnapshot.Details is { FullChargeCapacityMWh: > 0 } details
        ? string.Format(CultureInfo.CurrentCulture, "{0:F0} mWh", details.FullChargeCapacityMWh)
        : "--";

    public string BatteryDesignText => _batterySnapshot.Details is { DesignCapacityMWh: > 0 } details
        ? Labels.FactoryCapacity(string.Format(CultureInfo.CurrentCulture, "{0:F0} mWh", details.DesignCapacityMWh))
        : Labels.FactoryCapacity("--");

    /// <summary>The cycle line, or empty when the firmware does not report one. Empty hides the row.</summary>
    public string BatteryCycleText => _batterySnapshot.Details?.CycleCount is int cycles
        ? Labels.CycleCount(cycles)
        : "";

    public bool HasBatteryCycle => BatteryCycleText.Length > 0;

    public string BatteryVoltageText => _batterySnapshot.Details is { VoltageVolts: > 0 } details
        ? string.Format(CultureInfo.CurrentCulture, "{0:F2} V", details.VoltageVolts)
        : "--";

    /// <summary>
    /// The chemistry line, with the series cell count inferred from the pack voltage. Li-ion cells are 3.6 to 3.85 V
    /// nominal, so a 15.4 V pack is four cells; the inference is labelled as one.
    /// </summary>
    public string BatteryChemistryText
    {
        get
        {
            if (_batterySnapshot.Details is not { } details) return "--";

            var parts = new List<string>();
            if (details.Chemistry.Length > 0) parts.Add(details.Chemistry);

            int cells = details.VoltageVolts > 0 ? (int)Math.Round(details.VoltageVolts / 3.85) : 0;
            if (cells is >= 1 and <= 8 && details.Chemistry.StartsWith(Labels.Lithium, StringComparison.Ordinal))
            {
                parts.Add(Labels.CellCountFromVoltage(cells));
            }

            return parts.Count > 0 ? string.Join(" · ", parts) : "--";
        }
    }

    /// <summary>The battery's own name, e.g. <c>LGC AP18E8M</c>, or the generic word when it has none.</summary>
    public string BatteryNameText
    {
        get
        {
            if (_batterySnapshot.Details is not { } details) return Labels.Battery;

            var text = string.Join(" ", new[] { details.Manufacturer, details.ModelName }
                .Where(part => !string.IsNullOrWhiteSpace(part)));
            return text.Length > 0 ? text : Labels.Battery;
        }
    }

    /// <summary>The 80 % watchdog. It decides whether the overcharge banner is raised, not what the firmware does.</summary>
    public bool WatchdogOn
    {
        get => _watchdogOn;
        set
        {
            if (!Set(ref _watchdogOn, value)) return;
            if (_batteryHealth != null) _batteryHealth.WatchdogEnabled = value;
            _settings.BatteryWatchdogEnabled = value;
            SaveSettings();
            Raise(nameof(BatteryFlowStateText));
            Raise(nameof(ShowOverchargeBanner));
            Raise(nameof(OverchargeMessage));
        }
    }

    public bool WatchdogAvailable => BatteryHealthAvailable;

    /// <summary>The DC power-profile picker: what the machine does when the charger is pulled.</summary>
    /// <remarks>
    /// The descriptions name the four settings the two profiles actually differ in — operating mode, fan mode, CPU
    /// boost and panel refresh rate — because "Eco" and "Dengeli" on their own tell nobody which of them moves.
    /// </remarks>
    public IReadOnlyList<DcProfileChoice> DcProfileChoices { get; } =
    [
        new(DcPowerProfilePreference.Eco, Loc.T("Eco"),
            Loc.T("Pilde çalışma modu Sessiz'e döner, fan otomatiğe geçer, CPU Boost kapanır ve ekran 60 Hz'e iner. Pil belirgin şekilde daha uzun gider; oyunlarda ve ağır işlerde performans düşer.")),
        new(DcPowerProfilePreference.Balanced, Loc.T("Dengeli"),
            Loc.T("Pilde hiçbir ayar değişmez: çalışma modu, fan eğrisi, CPU Boost ve ekran yenileme hızı olduğu gibi kalır. Performans tam kalır; pil daha çabuk biter."))
    ];

    public DcProfileChoice? SelectedDcProfile
    {
        get => _selectedDcProfile;
        set
        {
            if (!Set(ref _selectedDcProfile, value) || value == null) return;
            _settings.DcPowerMode = value.Value;
            SaveSettings();
            Raise(nameof(SelectedDcProfileDescription));
        }
    }

    /// <summary>What the selected profile changes, in the card under the picker.</summary>
    public string SelectedDcProfileDescription => _selectedDcProfile?.Description ?? "";

    // ---------------------------------------------------------------- the wear trend

    public IReadOnlyList<double> WearTrendPoints => _wearTrendPoints;

    public string WearTrendSummary => _wearTrendSummary;

    public string WearTrendNote => _wearTrendNote;

    public bool HasWearTrendNote => _wearTrendNote.Length > 0;

    /// <summary>
    /// True when the readings span at least a week, which is what a curve needs. Two readings an hour apart are a
    /// trend only in the arithmetic sense, and drawing them would claim a direction the data does not have.
    /// </summary>
    public bool ShowWearTrendChart => _wearTrendVisible;

    /// <summary>True when there is anything to clear, so the button is not offered on a fresh install.</summary>
    public bool HasWearHistory => _wearTrend.Samples.Count > 0;

    public string WearTrendFirstDate => _wearTrend.First is { } first
        ? first.TimestampUtc.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)
        : "";

    public string WearTrendLastDate => _wearTrend.Last is { } last
        ? last.TimestampUtc.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)
        : "";

    // ---------------------------------------------------------------- component temperatures

    /// <summary>The SYS1 figure for the component matrix, or "--" when the backend reported nothing.</summary>
    public string SysTemperatureCardText => SysTemperature > 0 ? $"{SysTemperature} °C" : "--";

    public string Disk1TemperatureText => _storageDrives.Count > 0 && _storageDrives[0].TemperatureText != "--"
        ? _storageDrives[0].TemperatureText
        : "--";

    public string Disk2TemperatureText => _storageDrives.Count > 1 && _storageDrives[1].TemperatureText != "--"
        ? _storageDrives[1].TemperatureText
        : "--";

    /// <summary>
    /// The pack's own temperature. Neither WMI battery class reports one, and the Linux power-supply node only does on
    /// some machines, so the card prints "--" rather than a number it does not have.
    /// </summary>
    public string BatteryTemperatureText => "--";

    // ---------------------------------------------------------------- storage

    public bool StorageAvailable => _storageHealth?.IsSupported == true;

    public IReadOnlyList<StorageDriveRow> StorageDrives => _storageDrives;

    public bool HasStorageDrives => _storageDrives.Count > 0;

    public bool HasNoStorageDrives => StorageAvailable && _storageDrives.Count == 0;

    // ================================================================================================================
    // Services and tools page
    // ================================================================================================================

    public bool ServicesAvailable => _debloat?.IsSupported == true;

    /// <summary>
    /// True when the merged Acer card has anything to show: the service list, the repair tools, or the key.
    /// </summary>
    /// <remarks>
    /// The three used to be three cards, so each could hide on its own. One card means one visibility rule, and a
    /// machine that has only one of the three still gets the card rather than an empty frame.
    /// </remarks>
    public bool AcerHardwareAvailable => ServicesAvailable || RepairToolsAvailable || NitroKeyAvailable;

    /// <summary>
    /// The merged card's (?) text: what the service list does, then what the repair buttons do.
    /// </summary>
    /// <remarks>
    /// Two tooltips became one when the cards merged. The two sentences are still the two catalogue entries they always
    /// were, so every language keeps its own wording and neither had to be retranslated.
    /// </remarks>
    public string AcerHardwareHint => Labels.AcerServicesHint + "\n\n" + Labels.RepairToolsHint;

    /// <summary>
    /// The Nitro key's (?) text: the three switches explained, then what the launcher is doing right now.
    /// </summary>
    /// <remarks>
    /// The rows themselves carry no prose any more — a settings page with a paragraph under every switch is a page
    /// nobody reads. The explanations are all still here, one hover away, and the last line is live: it is the same
    /// derived state the page used to print under the NitroSense row.
    /// </remarks>
    public string NitroKeyHelp => string.Join(
        "\n\n",
        Labels.NitroKeyHint,
        Labels.RedirectNitroSenseHint,
        Labels.NitroSenseLauncherBlockHint,
        NitroSenseLauncherStateText);

    // ---------------------------------------------------------------- the collapsible cards

    /// <summary>
    /// Whether the merged Acer card is open. Closed by default: the list is long and most visits are for something
    /// else, so the page opens as a short stack of headings rather than a wall of rows.
    /// </summary>
    public bool AcerSectionExpanded
    {
        get => _acerSectionExpanded;
        set => Set(ref _acerSectionExpanded, value);
    }

    /// <summary>Whether the startup card is open. Closed by default, for the same reason.</summary>
    public bool StartupSectionExpanded
    {
        get => _startupSectionExpanded;
        set => Set(ref _startupSectionExpanded, value);
    }

    public IReadOnlyList<AcerServiceRow> AcerServices => _acerServices;

    public bool HasAcerServices => _acerServices.Count > 0;

    public bool HasNoAcerServices => ServicesAvailable && _acerServices.Count == 0;

    public bool NitroSenseRunning => _nitroSenseRunning;

    public string NitroSenseStateText => _nitroSenseRunning ? Labels.Running : Labels.Stopped;

    /// <summary>
    /// True when the launcher the Acer hardware service starts on the key press has been renamed away, so the key
    /// cannot open NitroSense even with NitroLite closed.
    /// </summary>
    /// <remarks>
    /// This is the only answer to "the key still opens NitroSense when the app is not running". The press is handled by
    /// Acer's own stack — the agent's hardware child process starts the launcher, which resolves the packaged app — so
    /// there is nothing of ours in the path to intercept it. Renaming the launcher removes the last link, and the
    /// Acer agent itself is left alone, which is what keeps lighting, fan and mode working.
    /// </remarks>
    public bool NitroSenseLauncherBlocked => _nitroSenseLauncherBlocked;

    /// <summary>The button's caption: what the click will do, not what the state is.</summary>
    public string NitroSenseLauncherBlockAction =>
        _nitroSenseLauncherBlocked ? Labels.NitroSenseLauncherUnblock : Labels.NitroSenseLauncherBlock;

    /// <summary>What the launcher is doing right now, in the terms the user asked about.</summary>
    public string NitroSenseLauncherBlockStateText => _nitroSenseLauncherBlocked
        ? Labels.NitroSenseLauncherBlockedState
        : Labels.NitroSenseLauncherActiveState;

    /// <summary>True when the launcher row can be offered: there is a service that manages it.</summary>
    public bool NitroSenseLauncherBlockAvailable => ServicesAvailable;

    public string ServicesStatus
    {
        get => _servicesStatus;
        private set
        {
            if (!Set(ref _servicesStatus, value)) return;
            Raise(nameof(HasServicesStatus));
            Status = value;
        }
    }

    public bool HasServicesStatus => _servicesStatus.Length > 0;

    public bool ServicesBusy => _servicesBusy;

    public bool RepairToolsAvailable => _systemTools?.IsSupported == true;

    public string RepairToolsStatus
    {
        get => _repairToolsStatus;
        private set
        {
            if (!Set(ref _repairToolsStatus, value)) return;
            Raise(nameof(HasRepairToolsStatus));
        }
    }

    public bool HasRepairToolsStatus => _repairToolsStatus.Length > 0;

    public bool ToolsBusy => _toolsBusy;

    /// <summary>True when this platform has a direct-WMI backend to offer.</summary>
    public bool DirectWmiAvailable => _directWmi != null;

    public string DirectWmiStatus => _directWmi?.StatusText ?? "";

    // ---------------------------------------------------------------- sign-in entries

    /// <summary>True when this platform has an ordinary per-user sign-in entry.</summary>
    public bool RunAtStartupAvailable => _runAtStartup?.IsSupported == true;

    /// <summary>True when this platform offers at least one way to start with the session.</summary>
    public bool StartupAvailable => RunAtStartupAvailable || AutoStartAvailable;

    /// <summary>
    /// True when the plain sign-in entry is the only way to start with the session, so its row is worth a line.
    /// </summary>
    /// <remarks>
    /// On Windows the elevated Task Scheduler entry is strictly better — it starts without a UAC prompt and it can start
    /// in the tray — so offering both was two switches for one decision, and the page now shows only the better one.
    /// Linux has no elevated entry at all (<c>AppHost</c> wires <c>LinuxRunAtStartupService</c> and leaves the other
    /// null), so there the plain entry is the whole feature and hiding it would remove autostart entirely.
    /// </remarks>
    public bool ShowRunAtStartupRow => RunAtStartupAvailable && !AutoStartAvailable;

    /// <summary>The plain <c>HKCU\...\Run</c> entry. Turning it on removes the elevated task, and the other way round.</summary>
    public bool RunAtStartupOn
    {
        get => _runAtStartupOn;
        set
        {
            if (!Set(ref _runAtStartupOn, value)) return;
            _ = ApplyRunAtStartupAsync(value);
        }
    }

    /// <summary>True when this desktop shows no tray icons, so the tray settings explain what happens instead.</summary>
    public bool TrayUnavailable => !DesktopShell.TrayAvailable;

    /// <summary>Start minimised to the tray. Baked into the command line, so changing it rewrites the entry.</summary>
    public bool StartMinimizedOn
    {
        get => _settings.StartMinimized;
        set
        {
            if (_settings.StartMinimized == value) return;
            _settings.StartMinimized = value;
            Raise();
            SaveSettings();

            // The flag lives in the registered command line, so an entry that is already there has to be rewritten.
            if (_runAtStartupOn) _ = ApplyRunAtStartupAsync(true);
        }
    }

    /// <summary>
    /// The ATK mouse integration. Off by default, and off hides the rail's FARE row and the mouse cards: a machine with
    /// no ATK mouse would otherwise show four configurations that write nowhere.
    /// </summary>
    public bool AtkMouseIntegrationOn
    {
        get => _settings.AtkMouseIntegrationEnabled;
        set
        {
            if (_settings.AtkMouseIntegrationEnabled == value) return;
            _settings.AtkMouseIntegrationEnabled = value;
            Raise();
            SaveSettings();
            Raise(nameof(ShowSystemMouseRow));
            Raise(nameof(ShowMouseProfileUi));
            Raise(nameof(SystemMouseText));
        }
    }

    // ---------------------------------------------------------------- the Nitro key

    /// <summary>
    /// True when this machine has a Nitro key the app can hear. The settings row is bound to this, so a desktop, a
    /// non-Acer laptop and a Linux install whose user is not in the <c>input</c> group all show nothing rather than a
    /// switch that cannot do anything.
    /// </summary>
    public bool NitroKeyAvailable => _nitroKey?.IsSupported == true;

    /// <summary>
    /// Bring the window forward when the key is pressed. There is no shortcut to record: the key is a physical button
    /// with one meaning, so this is a switch and not a key binding.
    /// </summary>
    public bool NitroKeyEnabled
    {
        get => _settings.NitroKeyEnabled;
        set
        {
            if (_settings.NitroKeyEnabled == value) return;
            _settings.NitroKeyEnabled = value;
            Raise();
            // The redirect row is only meaningful while the key is on, so it follows this switch.
            Raise(nameof(CanRedirectNitroSense));
            SaveSettings();
        }
    }

    /// <summary>
    /// Take the key away from NitroSense: close it when it comes to the front and raise NitroLite instead.
    /// </summary>
    /// <remarks>
    /// A second switch rather than part of <see cref="NitroKeyEnabled"/> because the two answer different questions.
    /// That one says "the key opens NitroLite"; this one says "and NitroSense does not get to open as well". The row is
    /// only offered on a machine that has the button, and only makes sense while the first switch is on, so it is
    /// disabled rather than hidden when that one is off.
    /// </remarks>
    public bool RedirectNitroSenseEnabled
    {
        get => _settings.RedirectNitroSenseEnabled;
        set
        {
            if (_settings.RedirectNitroSenseEnabled == value) return;
            _settings.RedirectNitroSenseEnabled = value;
            Raise();
            Raise(nameof(NitroSenseLauncherStateText));
            SaveSettings();
        }
    }

    /// <summary>
    /// True when the redirect row can be changed: the key is on, so there is something to redirect.
    /// </summary>
    public bool CanRedirectNitroSense => NitroKeyEnabled;

    /// <summary>
    /// What the NitroSense launcher is doing right now, in one line under the NitroSense row.
    /// </summary>
    /// <remarks>
    /// Derived from the two facts the page already has — whether the process is up and whether the key is being
    /// redirected — rather than from a third probe. The pill beside the row says whether NitroSense is running; this
    /// says what that means for the key, which is the question a user setting the switch is actually asking.
    /// </remarks>
    public string NitroSenseLauncherStateText
    {
        get
        {
            if (!NitroKeyAvailable) return Labels.NitroSenseLauncherNoKey;

            if (RedirectNitroSenseEnabled)
            {
                return NitroSenseRunning
                    ? Labels.NitroSenseLauncherRedirecting
                    : Labels.NitroSenseLauncherIdle;
            }

            return NitroSenseRunning
                ? Labels.NitroSenseLauncherRunning
                : Labels.NitroSenseLauncherStopped;
        }
    }

    /// <summary>
    /// The emergency rollback switch for the hardware layer: off pins every read and write to the Acer service socket.
    /// </summary>
    /// <remarks>
    /// The controller reads the setting through a delegate on every call rather than caching it at construction, so
    /// flipping this takes effect on the next read — no restart, which is the whole point of offering it to someone
    /// whose machine is misbehaving right now.
    /// </remarks>
    public bool UseDirectWmiBackend
    {
        get => _settings.UseDirectWmiBackend;
        set
        {
            if (_settings.UseDirectWmiBackend == value) return;
            _settings.UseDirectWmiBackend = value;
            Raise();
            SaveSettings();
            Status = value ? Labels.DirectWmiBackendOn : Labels.DirectWmiBackendOff;
        }
    }

    // ---------------------------------------------------------------- update

    public bool UpdateAvailable => _updates != null;

    public string UpdateVersionText => _updates?.CurrentVersionText ?? VersionText;

    public string UpdateStatus
    {
        get => _updateStatus;
        private set
        {
            if (!Set(ref _updateStatus, value)) return;
            Raise(nameof(HasUpdateStatus));
        }
    }

    public bool HasUpdateStatus => _updateStatus.Length > 0;

    public bool HasPendingUpdate => _pendingUpdate != null;

    public string UpdateNowText => _pendingUpdate is { } pending
        ? Labels.UpdateTo(pending.DisplayVersion)
        : Labels.Update;

    public bool AutoUpdateCheckOn
    {
        get => _settings.AutoUpdateCheck;
        set
        {
            if (_settings.AutoUpdateCheck == value) return;
            _settings.AutoUpdateCheck = value;
            Raise();
            SaveSettings();
        }
    }

    // ---------------------------------------------------------------- diagnostics and backup

    public bool DiagnosticsAvailable => _diagnostics?.IsSupported == true;

    public string DiagnosticsNote
    {
        get => _diagnosticsNote;
        private set
        {
            if (!Set(ref _diagnosticsNote, value)) return;
            Raise(nameof(HasDiagnosticsNote));
        }
    }

    public bool HasDiagnosticsNote => _diagnosticsNote.Length > 0;

    // ---------------------------------------------------------------- NitroSense snapshot diff

    /// <summary>
    /// True when the platform can take a read-only snapshot. False on a platform without the Acer WMI worker and the
    /// HID walk, which is why the two buttons are hidden rather than shown and then refused.
    /// </summary>
    public bool SnapshotDiffAvailable => _platform is IHardwareSnapshotBackend;

    /// <summary>
    /// What the two capture buttons last said: how many entries A holds, what the diff found, or why the capture failed.
    /// </summary>
    public string SnapshotDiffStatus
    {
        get => _snapshotDiffStatus;
        private set
        {
            if (!Set(ref _snapshotDiffStatus, value)) return;
            Raise(nameof(HasSnapshotDiffStatus));
        }
    }

    public bool HasSnapshotDiffStatus => _snapshotDiffStatus.Length > 0;

    /// <summary>True once state A is in hand, which is what enables the second button.</summary>
    public bool SnapshotBaselineReady => _snapshotA != null;

    /// <summary>
    /// Step one of the pair: the state before the user touches NitroSense. Nothing is written, so this is safe to press
    /// on any machine and at any time.
    /// </summary>
    private async Task CaptureSnapshotAAsync()
    {
        try
        {
            var snapshot = await CaptureHardwareSnapshotAsync();
            if (snapshot == null) return;

            _snapshotA = snapshot;
            Raise(nameof(SnapshotBaselineReady));
            CaptureSnapshotBDiffCommand.RaiseCanExecuteChanged();
            SnapshotDiffStatus = Labels.SnapshotCapturedA(snapshot.Count);
            AppLog.Info("NitroSense snapshot A was captured", $"{snapshot.Describe()} · {snapshot.Count} entries");
        }
        catch (Exception ex)
        {
            SnapshotDiffStatus = Labels.SnapshotDiffFailed(ex.Message);
            AppLog.Warn("NitroSense snapshot A could not be captured", ex.Message);
        }
    }

    /// <summary>
    /// Step two: the state after the user changed one setting in NitroSense, diffed against A, copied and offered as a
    /// prefilled compatibility issue.
    /// </summary>
    /// <remarks>
    /// The clipboard carries the report first and the diff under it, which is the order a maintainer reads them in. The
    /// URL carries the diff first, because the issue body is cut when it does not fit and the diff is the half that
    /// cannot be reproduced afterwards: A is gone from the machine by then, while the report can be regenerated from
    /// the settings page at any time.
    /// </remarks>
    private async Task CaptureSnapshotBDiffAsync()
    {
        var before = _snapshotA;
        if (before == null)
        {
            SnapshotDiffStatus = Labels.SnapshotDiffHint;
            return;
        }

        try
        {
            var after = await CaptureHardwareSnapshotAsync();
            if (after == null) return;

            var diff = HardwareSnapshotDiff.Compare(before, after);
            var report = HardwareReportText;
            var separator = Environment.NewLine + Environment.NewLine;

            var clipboard = report.Length == 0 ? diff.Text : report + separator + diff.Text;
            ClipboardService.SetText(clipboard);

            SnapshotDiffStatus = diff.HasChanges
                ? Labels.SnapshotDiffCaptured(diff.TotalChanges)
                : Labels.SnapshotDiffSame;

            AppLog.Info("NitroSense snapshot diff captured",
                $"{diff.Changed} changed · {diff.Added} added · {diff.Removed} removed · " +
                $"{diff.Unchanged} unchanged");
        }
        catch (Exception ex)
        {
            SnapshotDiffStatus = Labels.SnapshotDiffFailed(ex.Message);
            AppLog.Warn("NitroSense snapshot diff failed", ex.Message);
        }
    }

    /// <summary>
    /// One read-only capture, or null with the reason in the status line. A platform that cannot answer and a machine
    /// that could read nothing are reported separately: the first is a missing feature, the second a real finding.
    /// </summary>
    private async Task<HardwareSnapshot?> CaptureHardwareSnapshotAsync()
    {
        if (_platform is not IHardwareSnapshotBackend backend)
        {
            SnapshotDiffStatus = Labels.SnapshotDiffFailed(Labels.NoBackend);
            return null;
        }

        var snapshot = await backend.CaptureSnapshotAsync().ConfigureAwait(true);
        if (snapshot.IsEmpty)
        {
            SnapshotDiffStatus = Labels.SnapshotDiffFailed("no selector could be read");
            return null;
        }

        return snapshot;
    }

    // ================================================================================================================
    // The first-run walkthrough
    // ================================================================================================================

    /// <summary>True while the three-step overlay is on screen.</summary>
    public bool ShowOnboarding => _showOnboarding;

    /// <summary>Zero-based step index: 0 hardware, 1 profile, 2 start-up.</summary>
    public int OnboardingStep => _onboardingStep;

    public bool IsOnboardingFirstStep => _onboardingStep <= 0;

    public bool IsOnboardingSecondStep => _onboardingStep == 1;

    public bool IsOnboardingLastStep => _onboardingStep >= 2;

    public string OnboardingStepLabel => Labels.OnboardingStepFormat(_onboardingStep + 1, 3);

    public string OnboardingTitle => _onboardingStep switch
    {
        0 => Labels.OnboardingHardwareTitle,
        1 => Labels.OnboardingProfileTitle,
        _ => Labels.OnboardingStartupTitle
    };

    public string OnboardingBody => _onboardingStep switch
    {
        0 => Labels.OnboardingHardwareBody,
        1 => Labels.OnboardingProfileBody,
        _ => Labels.OnboardingStartupBody
    };

    /// <summary>The recommended starting profile, printed as one line on step two.</summary>
    public string OnboardingRecommendedText => Labels.OnboardingRecommended;

    /// <summary>
    /// The sign-in switch on step three. Hidden on a platform with no elevated auto-start, which is why the step is
    /// skipped rather than shown with a control that cannot work.
    /// </summary>
    public bool OnboardingAutoStartAvailable => _autoStart != null;

    public bool OnboardingAutoStartOn
    {
        get => _onboardingAutoStartOn;
        set => Set(ref _onboardingAutoStartOn, value);
    }

    // ================================================================================================================
    // Commands
    // ================================================================================================================

    public RelayCommand ClearWearHistoryCommand { get; private set; } = null!;
    public RelayCommand RetrustCertificateCommand { get; private set; } = null!;
    public AsyncCommand DisableServicesCommand { get; private set; } = null!;
    public AsyncCommand RestoreServicesCommand { get; private set; } = null!;
    public AsyncCommand KillNitroSenseCommand { get; private set; } = null!;
    public AsyncCommand ToggleNitroSenseLauncherCommand { get; private set; } = null!;
    public AsyncCommand FixLightingCommand { get; private set; } = null!;
    public AsyncCommand FixAcerServicesCommand { get; private set; } = null!;
    public AsyncCommand RestoreMuxCommand { get; private set; } = null!;
    public AsyncCommand OptimizeCommand { get; private set; } = null!;
    public AsyncCommand RestoreOptimizeCommand { get; private set; } = null!;
    public AsyncCommand StartDirectWmiCommand { get; private set; } = null!;
    public AsyncCommand CheckUpdatesCommand { get; private set; } = null!;
    public AsyncCommand UpdateNowCommand { get; private set; } = null!;
    public RelayCommand ReleaseNotesCommand { get; private set; } = null!;

    /// <summary>The two buttons on the support card. Both are plain links, so neither needs a busy state.</summary>
    public RelayCommand SupportKofiCommand { get; private set; } = null!;

    public RelayCommand SupportGitHubCommand { get; private set; } = null!;
    public RelayCommand ReportBugCommand { get; private set; } = null!;
    public RelayCommand OpenLogsCommand { get; private set; } = null!;
    public AsyncCommand ExportHardwareReportCommand { get; private set; } = null!;
    public RelayCommand CreateDiagnosticsCommand { get; private set; } = null!;
    public AsyncCommand CreateBackupCommand { get; private set; } = null!;
    public AsyncCommand RestoreBackupCommand { get; private set; } = null!;
    public RelayCommand OnboardingNextCommand { get; private set; } = null!;
    public RelayCommand OnboardingBackCommand { get; private set; } = null!;
    public RelayCommand OnboardingSkipCommand { get; private set; } = null!;
    public AsyncCommand OnboardingFinishCommand { get; private set; } = null!;
    public RelayCommand ReopenOnboardingCommand { get; private set; } = null!;

    /// <summary>Takes the read-only state A of a NitroSense snapshot pair.</summary>
    public AsyncCommand CaptureSnapshotACommand { get; private set; } = null!;

    /// <summary>Takes state B, diffs it against A and prepares the GitHub compatibility issue.</summary>
    public AsyncCommand CaptureSnapshotBDiffCommand { get; private set; } = null!;

    /// <summary>
    /// Wires the phase-3 commands. Called from the constructor, after the fields are assigned, so a command can never
    /// capture a null service.
    /// </summary>
    private void BuildPhase3Commands()
    {
        ClearWearHistoryCommand = new RelayCommand(() => _ = ClearWearHistoryAsync());
        RetrustCertificateCommand = new RelayCommand(() => _ = RetrustCertificateAsync());
        DisableServicesCommand = new AsyncCommand(DisableServicesAsync);
        RestoreServicesCommand = new AsyncCommand(RestoreServicesAsync);
        KillNitroSenseCommand = new AsyncCommand(KillNitroSenseAsync);
        ToggleNitroSenseLauncherCommand = new AsyncCommand(ToggleNitroSenseLauncherAsync);
        FixLightingCommand = new AsyncCommand(() => RunToolAsync(_systemTools == null ? null : _systemTools.FixKeyboardLightingAsync, Labels.FixLighting));
        FixAcerServicesCommand = new AsyncCommand(() => RunToolAsync(_systemTools == null ? null : _systemTools.FixAcerServicesAsync, Labels.FixAcerServices));
        RestoreMuxCommand = new AsyncCommand(() => RunToolAsync(_systemTools == null ? null : _systemTools.RestoreMuxSwitchAsync, Labels.RestoreMux));
        OptimizeCommand = new AsyncCommand(() => RunToolAsync(_systemTools == null ? null : _systemTools.OptimizeAsync, Labels.Optimize));
        RestoreOptimizeCommand = new AsyncCommand(() => RunToolAsync(_systemTools == null ? null : _systemTools.RestoreOptimizeAsync, Labels.RestoreOptimize));
        StartDirectWmiCommand = new AsyncCommand(StartDirectWmiAsync);
        CheckUpdatesCommand = new AsyncCommand(() => CheckForUpdatesAsync(silent: false));
        UpdateNowCommand = new AsyncCommand(UpdateNowAsync);
        ReleaseNotesCommand = new RelayCommand(OpenReleaseNotes);
        SupportKofiCommand = new RelayCommand(() => OpenUrl(SupportLinks.Kofi));
        SupportGitHubCommand = new RelayCommand(() => OpenUrl(SupportLinks.Repository));
        ReportBugCommand = new RelayCommand(ReportBug);
        OpenLogsCommand = new RelayCommand(OpenLogsFolder);
        ExportHardwareReportCommand = new AsyncCommand(ExportHardwareReportAsync);
        CreateDiagnosticsCommand = new RelayCommand(CreateDiagnostics);
        CreateBackupCommand = new AsyncCommand(CreateBackupAsync);
        RestoreBackupCommand = new AsyncCommand(RestoreBackupAsync);
        OnboardingNextCommand = new RelayCommand(OnboardingNext);
        OnboardingBackCommand = new RelayCommand(OnboardingBack);
        OnboardingSkipCommand = new RelayCommand(() => _ = FinishOnboardingAsync(applyRecommended: false));
        OnboardingFinishCommand = new AsyncCommand(() => FinishOnboardingAsync(applyRecommended: true));
        ReopenOnboardingCommand = new RelayCommand(ReopenOnboarding);
        CaptureSnapshotACommand = new AsyncCommand(CaptureSnapshotAAsync, () => SnapshotDiffAvailable);
        CaptureSnapshotBDiffCommand = new AsyncCommand(
            CaptureSnapshotBDiffAsync, () => SnapshotDiffAvailable && _snapshotA != null);
    }

    // ================================================================================================================
    // Lifecycle
    // ================================================================================================================

    /// <summary>
    /// The first read of everything this page owns. Called once from <see cref="InitializeAsync"/> after the capability
    /// probe, because the walkthrough's first step lists what the probe found.
    /// </summary>
    private async Task InitializePhase3Async()
    {
        _watchdogOn = _settings.BatteryWatchdogEnabled;
        if (_batteryHealth != null) _batteryHealth.WatchdogEnabled = _watchdogOn;

        // The toggle was bound before this ran, so it read the field's default of false and stayed there: the switch
        // showed "off" on every launch while the watchdog was in fact armed, and the only way to find out was to flip
        // it off and on again. The field is assigned directly rather than through the setter because the setter would
        // write the value back to the settings file it was just read from.
        Raise(nameof(WatchdogOn));

        _selectedDcProfile = DcProfileChoices.FirstOrDefault(c => c.Value == _settings.DcPowerMode)
                             ?? DcProfileChoices[0];
        Raise(nameof(SelectedDcProfile));
        Raise(nameof(SelectedDcProfileDescription));

        await RefreshBatteryHealthAsync().ConfigureAwait(true);
        await RefreshStorageAsync().ConfigureAwait(true);
        await RefreshServicesAsync().ConfigureAwait(true);
        await RefreshRunAtStartupAsync().ConfigureAwait(true);

        // A silent check: a machine with no network is not something to report on startup, and the window must not wait
        // for GitHub before it becomes usable.
        if (_updates != null && _settings.AutoUpdateCheck) _ = CheckForUpdatesAsync(silent: true);

        MaybeShowOnboarding();
    }

    /// <summary>
    /// The per-tick half. The battery's slow read runs once a minute; the level and the flow come from the fast reader
    /// the header already uses, so the card is never a minute behind on the number that matters.
    /// </summary>
    private async Task TickPhase3Async()
    {
        if (_batteryHealth == null) return;

        // A replug changes the state sentence and the remaining-time row, and waiting up to a minute for it would make
        // the card look stuck. The comparison is free after the first tick.
        if (_lastBatteryOnAc != OnAcPower)
        {
            _lastBatteryOnAc = OnAcPower;
            await RefreshBatteryHealthAsync().ConfigureAwait(true);
            return;
        }

        if (_tick % BatteryHealthTickInterval == 0) await RefreshBatteryHealthAsync().ConfigureAwait(true);
    }

    private async Task RefreshBatteryHealthAsync()
    {
        if (_batteryHealth == null || _batteryBusy) return;

        _batteryBusy = true;
        try
        {
            var snapshot = await _batteryHealth.ReadAsync().ConfigureAwait(true);
            _batterySnapshot = snapshot;

            // One reading a day is enough for a wear curve; the history decides whether this one is new.
            if (snapshot.Details is { } details) _batteryHealth.RecordWearSample(details);

            RefreshWearTrend();
            RaiseBatteryProperties();
        }
        catch (Exception ex)
        {
            AppLog.Warn("The battery health could not be read", ex.Message);
        }
        finally
        {
            _batteryBusy = false;
        }
    }

    private void RefreshWearTrend()
    {
        if (_batteryHealth == null)
        {
            _wearTrend = new BatteryWearTrend();
            _wearTrendPoints = Array.Empty<double>();
            _wearTrendSummary = "";
            _wearTrendNote = "";
            _wearTrendVisible = false;
            return;
        }

        _wearTrend = _batteryHealth.GetWearTrend();
        var samples = _wearTrend.Samples;

        if (samples.Count == 0)
        {
            _wearTrendPoints = Array.Empty<double>();
            _wearTrendSummary = Labels.WearNoSamples;
            _wearTrendNote = "";
            _wearTrendVisible = false;
            return;
        }

        if (!_wearTrend.HasTrend)
        {
            _wearTrendPoints = Array.Empty<double>();
            _wearTrendSummary = Labels.WearOneSample;
            _wearTrendNote = Labels.WearFirstSample(
                samples[0].TimestampUtc.ToLocalTime().ToString("d", CultureInfo.CurrentCulture),
                samples[0].WearLevelPercent);
            _wearTrendVisible = false;
            return;
        }

        double days = _wearTrend.SpanDays ?? 0;
        double change = _wearTrend.WearChangePercent ?? 0;

        // The sign is part of the text, so it is formatted here rather than through a format specifier in the
        // catalogue: a translator should not have to preserve "+0.0;-0.0;0.0" to keep the page readable.
        var changeText = change.ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture);
        _wearTrendSummary = Labels.WearChangeSummary(Math.Max(1, (int)Math.Round(days)), changeText);

        // Two readings an hour apart are a trend only in the arithmetic sense. The chart waits for the same week the
        // per-year rate does, so the plot and the sentence under it can never disagree about whether there is one.
        if (!_wearTrend.HasMeaningfulTrend)
        {
            _wearTrendPoints = Array.Empty<double>();
            _wearTrendVisible = false;
            _wearTrendNote = Labels.WearNeedsAWeek;
            return;
        }

        _wearTrendPoints = samples.Select(s => s.WearLevelPercent).ToList();
        _wearTrendVisible = true;

        if (_wearTrend.WearPerYearPercent is not double perYear)
        {
            _wearTrendNote = Labels.WearNeedsAWeek;
        }
        else if (change <= 0)
        {
            // A pack that reads healthier than before recalibrated; saying "it is healing" would be a lie.
            _wearTrendNote = Labels.WearRecalibrated;
        }
        else
        {
            _wearTrendNote = Labels.WearPerYear(perYear);
        }
    }

    private void RaiseBatteryProperties()
    {
        Raise(nameof(BatteryHealthAvailable));
        Raise(nameof(BatteryCapacityAvailable));
        Raise(nameof(BatteryLevelText));
        Raise(nameof(BatteryFlowStateText));
        Raise(nameof(BatteryPowerFlowText));
        Raise(nameof(BatteryRemainingText));
        Raise(nameof(ShowBatteryRemaining));
        Raise(nameof(ShowOverchargeBanner));
        Raise(nameof(OverchargeMessage));
        Raise(nameof(BatteryHealthText));
        Raise(nameof(BatteryHealthNote));
        Raise(nameof(BatteryWearText));
        Raise(nameof(BatteryWearNote));
        Raise(nameof(BatteryFullChargeText));
        Raise(nameof(BatteryDesignText));
        Raise(nameof(BatteryCycleText));
        Raise(nameof(HasBatteryCycle));
        Raise(nameof(BatteryVoltageText));
        Raise(nameof(BatteryChemistryText));
        Raise(nameof(BatteryNameText));
        Raise(nameof(WatchdogAvailable));
        Raise(nameof(WearTrendPoints));
        Raise(nameof(WearTrendSummary));
        Raise(nameof(WearTrendNote));
        Raise(nameof(HasWearTrendNote));
        Raise(nameof(ShowWearTrendChart));
        Raise(nameof(HasWearHistory));
        Raise(nameof(WearTrendFirstDate));
        Raise(nameof(WearTrendLastDate));
        Raise(nameof(SysTemperatureCardText));
        Raise(nameof(Disk1TemperatureText));
        Raise(nameof(Disk2TemperatureText));
        Raise(nameof(BatteryTemperatureText));
    }

    private async Task ClearWearHistoryAsync()
    {
        if (_batteryHealth == null) return;

        if (_dialogs != null)
        {
            var confirmed = await _dialogs.ConfirmAsync(Labels.ClearHistory, Labels.ClearHistoryConfirm)
                .ConfigureAwait(true);
            if (!confirmed) return;
        }

        if (!_batteryHealth.ClearWearHistory())
        {
            Status = Labels.ClearHistoryFailed;
            return;
        }

        RefreshWearTrend();
        Raise(nameof(WearTrendPoints));
        Raise(nameof(WearTrendSummary));
        Raise(nameof(WearTrendNote));
        Raise(nameof(HasWearTrendNote));
        Raise(nameof(ShowWearTrendChart));
        Raise(nameof(HasWearHistory));
        Raise(nameof(WearTrendFirstDate));
        Raise(nameof(WearTrendLastDate));
        Status = Labels.ClearHistoryDone;
    }

    /// <summary>
    /// Drops the pinned Care Center thumbprint and reads the charge limit again. Only reachable from the note that
    /// appears when the agent presented a different certificate, so trusting a new one is always a user action.
    /// </summary>
    private async Task RetrustCertificateAsync()
    {
        if (_hardware is not ICareCenterTrust trust) return;

        if (!trust.ForgetPinnedCertificate())
        {
            Status = Labels.RetrustCertificateFailed;
            return;
        }

        Raise(nameof(ShowRetrustCertificate));
        Status = Labels.RetrustCertificateDone;

        // The limit is read again through the same path the page already uses; a failure there is reported by that
        // path, so nothing is swallowed here.
        await RefreshBatteryHealthAsync().ConfigureAwait(true);
    }

    private async Task RefreshStorageAsync()
    {
        if (_storageHealth == null || !_storageHealth.IsSupported) return;

        try
        {
            // The storage namespace is slow: a cold query walks every physical disk and every partition.
            var drives = await Task.Run(() => _storageHealth.GetDrives()).ConfigureAwait(true);
            _storageDrives = drives.Select(d => new StorageDriveRow(d)).ToList();
        }
        catch (Exception ex)
        {
            AppLog.Warn("The storage drives could not be read", ex.Message);
            _storageDrives = Array.Empty<StorageDriveRow>();
        }

        Raise(nameof(StorageAvailable));
        Raise(nameof(StorageDrives));
        Raise(nameof(HasStorageDrives));
        Raise(nameof(HasNoStorageDrives));
        Raise(nameof(Disk1TemperatureText));
        Raise(nameof(Disk2TemperatureText));
    }

    // ================================================================================================================
    // Services
    // ================================================================================================================

    private async Task RefreshServicesAsync()
    {
        if (_debloat == null || !_debloat.IsSupported) return;

        try
        {
            var (statuses, running, launcherBlocked) = await Task.Run(() =>
                (_debloat.GetServiceStatuses(), _debloat.IsNitroSenseRunning(), _debloat.IsNitroSenseLauncherBlocked))
                .ConfigureAwait(true);

            _acerServices = statuses.Select(s => new AcerServiceRow(s)).ToList();
            _nitroSenseRunning = running;
            _nitroSenseLauncherBlocked = launcherBlocked;
        }
        catch (Exception ex)
        {
            AppLog.Warn("The Acer service states could not be read", ex.Message);
            _acerServices = Array.Empty<AcerServiceRow>();
        }

        Raise(nameof(ServicesAvailable));
        Raise(nameof(AcerServices));
        Raise(nameof(HasAcerServices));
        Raise(nameof(HasNoAcerServices));
        Raise(nameof(NitroSenseRunning));
        Raise(nameof(NitroSenseStateText));
        Raise(nameof(NitroSenseLauncherStateText));
        Raise(nameof(NitroSenseLauncherBlocked));
        Raise(nameof(NitroSenseLauncherBlockAction));
        Raise(nameof(NitroSenseLauncherBlockStateText));
        Raise(nameof(NitroSenseLauncherBlockAvailable));
    }

    private async Task DisableServicesAsync()
    {
        if (_debloat == null || _servicesBusy) return;

        if (_dialogs != null && !await _dialogs.ConfirmAsync(Labels.AcerServices, Labels.DisableServicesConfirm)
                .ConfigureAwait(true))
        {
            return;
        }

        _servicesBusy = true;
        Raise(nameof(ServicesBusy));
        try
        {
            var result = await _debloat.DisableBackgroundServicesAsync().ConfigureAwait(true);
            ServicesStatus = result.Success
                ? Labels.ServicesDisabled
                : Labels.ServicesFailed(result.Error ?? Labels.UnknownError);
        }
        finally
        {
            _servicesBusy = false;
            Raise(nameof(ServicesBusy));
            await RefreshServicesAsync().ConfigureAwait(true);
        }
    }

    private async Task RestoreServicesAsync()
    {
        if (_debloat == null || _servicesBusy) return;

        if (_dialogs != null && !await _dialogs.ConfirmAsync(Labels.RestoreServices, Labels.RestoreServicesConfirm)
                .ConfigureAwait(true))
        {
            return;
        }

        _servicesBusy = true;
        Raise(nameof(ServicesBusy));
        try
        {
            var result = await _debloat.RestoreServicesAsync().ConfigureAwait(true);
            ServicesStatus = result.Success
                ? Labels.ServicesRestored
                : Labels.ServicesFailed(result.Error ?? Labels.UnknownError);
        }
        finally
        {
            _servicesBusy = false;
            Raise(nameof(ServicesBusy));
            await RefreshServicesAsync().ConfigureAwait(true);
        }
    }

    private async Task KillNitroSenseAsync()
    {
        if (_debloat == null || _servicesBusy) return;

        if (!_debloat.IsNitroSenseRunning())
        {
            ServicesStatus = Labels.NitroSenseAlreadyClosed;
            await RefreshServicesAsync().ConfigureAwait(true);
            return;
        }

        if (_dialogs != null && !await _dialogs.ConfirmAsync(Labels.KillNitroSense, Labels.KillNitroSenseConfirm)
                .ConfigureAwait(true))
        {
            return;
        }

        _servicesBusy = true;
        Raise(nameof(ServicesBusy));
        try
        {
            var result = await _debloat.KillNitroSenseAsync().ConfigureAwait(true);
            ServicesStatus = result.Success ? Labels.NitroSenseClosed : Labels.NitroSenseCloseFailed;
        }
        finally
        {
            _servicesBusy = false;
            Raise(nameof(ServicesBusy));
            await RefreshServicesAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Renames the NitroSense launcher away, or puts it back. This is the switch that keeps the key from opening
    /// NitroSense while NitroLite is closed, which the foreground watcher cannot do: nothing of ours is running then.
    /// </summary>
    private async Task ToggleNitroSenseLauncherAsync()
    {
        if (_debloat == null || _servicesBusy) return;

        bool block = !_nitroSenseLauncherBlocked;

        if (_dialogs != null && !await _dialogs.ConfirmAsync(
                Labels.NitroSenseLauncherBlockTitle,
                block ? Labels.NitroSenseLauncherBlockConfirm : Labels.NitroSenseLauncherUnblockConfirm)
                .ConfigureAwait(true))
        {
            return;
        }

        _servicesBusy = true;
        Raise(nameof(ServicesBusy));
        try
        {
            var result = block
                ? await _debloat.BlockNitroSenseLauncherAsync().ConfigureAwait(true)
                : await _debloat.UnblockNitroSenseLauncherAsync().ConfigureAwait(true);

            ServicesStatus = result.Success
                ? (block ? Labels.NitroSenseLauncherBlockedDone : Labels.NitroSenseLauncherUnblockedDone)
                : Labels.NitroSenseLauncherBlockFailed(result.Error ?? Labels.UnknownError);
        }
        finally
        {
            _servicesBusy = false;
            Raise(nameof(ServicesBusy));
            await RefreshServicesAsync().ConfigureAwait(true);
        }
    }

    // ================================================================================================================
    // Repair and optimisation tools
    // ================================================================================================================

    private async Task RunToolAsync(Func<CancellationToken, Task<ToolResult>>? run, string title)
    {
        if (run == null || _toolsBusy) return;

        if (_dialogs != null && !await _dialogs.ConfirmAsync(title, Labels.RepairConfirm).ConfigureAwait(true)) return;

        _toolsBusy = true;
        Raise(nameof(ToolsBusy));
        RepairToolsStatus = Labels.RepairRunning;
        try
        {
            var result = await run(CancellationToken.None).ConfigureAwait(true);
            RepairToolsStatus = (result.Success ? "\u25CF " : "\u25B2 ") + result.Message;
            Status = result.Message;
        }
        catch (Exception ex)
        {
            RepairToolsStatus = "\u25B2 " + Labels.RepairFailed(ex.Message);
        }
        finally
        {
            _toolsBusy = false;
            Raise(nameof(ToolsBusy));
            await RefreshServicesAsync().ConfigureAwait(true);
        }
    }

    private async Task StartDirectWmiAsync()
    {
        if (_directWmi == null) return;

        try
        {
            var started = await _directWmi.StartAsync().ConfigureAwait(true);
            Raise(nameof(DirectWmiStatus));
            Status = started ? Labels.DirectWmiStarted : Labels.DirectWmiDeclined;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Direct WMI could not be started", ex.ToString());
            Status = Labels.DirectWmiFailed(ex.Message);
        }
    }

    // ================================================================================================================
    // Sign-in entries
    // ================================================================================================================

    private bool _runAtStartupOn;

    private async Task RefreshRunAtStartupAsync()
    {
        if (_runAtStartup == null || !_runAtStartup.IsSupported) return;

        try
        {
            var enabled = await _runAtStartup.IsEnabledAsync().ConfigureAwait(true);
            Set(ref _runAtStartupOn, enabled, nameof(RunAtStartupOn));
        }
        catch (Exception ex)
        {
            AppLog.Warn("The autostart state could not be read", ex.Message);
        }
    }

    private async Task ApplyRunAtStartupAsync(bool enabled)
    {
        if (_runAtStartup == null) return;

        try
        {
            var ok = await _runAtStartup.SetEnabledAsync(enabled, _settings.StartMinimized).ConfigureAwait(true);
            if (!ok)
            {
                // The write failed, so the switch goes back to what the registry actually says rather than staying on a
                // position the machine is not in.
                Status = Labels.AutoStartFailed;
                await RefreshRunAtStartupAsync().ConfigureAwait(true);
                return;
            }

            // Two registrations would start NitroLite twice at logon, so the elevated task is removed when the plain
            // entry takes over.
            if (enabled && _autoStart != null)
            {
                try { await _autoStart.DisableAsync().ConfigureAwait(true); }
                catch (Exception ex) { AppLog.Warn("The administrator task could not be removed", ex.Message); }
            }

            await RefreshAutoStartAsync().ConfigureAwait(true);
            Status = enabled ? Labels.AutoStartEnabled : Labels.AutoStartDisabled;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Autostart could not be changed", ex.ToString());
            Status = Labels.AutoStartFailed;
            await RefreshRunAtStartupAsync().ConfigureAwait(true);
        }
    }

    // ================================================================================================================
    // Update, diagnostics, backup
    // ================================================================================================================

    private async Task CheckForUpdatesAsync(bool silent)
    {
        if (_updates == null || _updateBusy) return;

        _updateBusy = true;
        try
        {
            var info = await _updates.CheckAsync().ConfigureAwait(true);
            _pendingUpdate = info;
            Raise(nameof(HasPendingUpdate));
            Raise(nameof(UpdateNowText));

            if (info == null)
            {
                if (!silent) UpdateStatus = Labels.UpdateCurrent;
                return;
            }

            UpdateStatus = Labels.UpdateFound(info.DisplayVersion);
            if (!silent) Status = UpdateStatus;
        }
        finally
        {
            _updateBusy = false;
        }
    }

    /// <summary>
    /// Downloads the pending release, verifies it against the digest GitHub published, and swaps the executable.
    /// </summary>
    /// <remarks>
    /// Every path that cannot install ends at the release page rather than at a dead button: a development build, a
    /// Linux build, a check that found nothing, and a release published without a digest all open the page the user
    /// can act on. The new build is on disk when this succeeds, but this process is still running the old one, so the
    /// card asks for a restart instead of relaunching — a relaunch would race the single-instance guard and the new
    /// process would exit on finding the old one still holding it.
    /// </remarks>
    private async Task UpdateNowAsync()
    {
        if (_updates == null) return;

        // A development build cannot replace itself, and a Linux build cannot install a Windows package over itself.
        // Both cases open the release page, which is the honest answer to "update".
        if (!_updates.IsPackaged)
        {
            OpenUrl(_updates.ReleasePageUrl);
            UpdateStatus = Labels.UpdateManual;
            return;
        }

        // Nothing to install: the check has not run, or it found nothing. The release page is still the right answer.
        if (_pendingUpdate is not { } pending)
        {
            OpenUrl(_updates.ReleasePageUrl);
            UpdateStatus = Labels.UpdateManual;
            return;
        }

        // A package with no published digest cannot be verified, and an unverified executable is not something this
        // app installs by itself. The release page lets the user do it deliberately.
        if (string.IsNullOrWhiteSpace(pending.Sha256) || string.IsNullOrWhiteSpace(pending.DownloadUrl))
        {
            OpenUrl(_updates.ReleaseNotesUrl(pending));
            UpdateStatus = Labels.UpdateManual;
            return;
        }

        if (_updateBusy) return;
        _updateBusy = true;
        UpdateStatus = Labels.UpdateDownloading;

        try
        {
            var progress = new Progress<double>(fraction => UpdateStatus = Labels.UpdateProgress(fraction));
            var result = await _updates.InstallAsync(pending, progress).ConfigureAwait(true);

            if (!result.Success || result.ExecutablePath == null)
            {
                UpdateStatus = Labels.UpdateFailed(result.Error ?? "");
                return;
            }

            UpdateStatus = Labels.UpdateInstalled;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Update could not be applied", ex.Message);
            UpdateStatus = Labels.UpdateFailed(ex.Message);
        }
        finally
        {
            _updateBusy = false;
        }
    }

    private void OpenReleaseNotes()
    {
        if (_updates == null) return;
        OpenUrl(_updates.ReleaseNotesUrl(_pendingUpdate));
    }

    private void ReportBug()
    {
        if (_updates == null) return;

        try
        {
            var language = Loc.Languages.FirstOrDefault(l => l.Code == Loc.Language)?.NativeName ?? Loc.Language;
            var body = _updates.BuildIssueBody(
                _updates.CurrentVersionText,
                System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                _updates.GetMachineModel(),
                language);
            OpenUrl(_updates.BuildIssueUrl(Labels.BugReportTitle, body));
        }
        catch (Exception ex)
        {
            Status = Labels.BugReportFailed(ex.Message);
        }
    }

    private void OpenLogsFolder()
    {
        var folder = _diagnostics?.DataDirectory ?? AppPaths.DataDirectory;
        OpenUrl(folder);
    }

    private async Task ExportHardwareReportAsync()
    {
        var report = HardwareReportText;
        if (report.Length == 0 || _dialogs == null)
        {
            Status = Labels.ReportNotReady;
            return;
        }

        var path = await _dialogs.SaveFileAsync(Labels.ExportReport, "nitrolite-hardware-report.md", "md")
            .ConfigureAwait(true);
        if (path == null) return;

        try
        {
            await File.WriteAllTextAsync(path, report).ConfigureAwait(true);
            Status = Labels.ReportExported(Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            Status = Labels.ReportExportFailed(ex.Message);
        }
    }

    private void CreateDiagnostics()
    {
        if (_diagnostics == null) return;

        try
        {
            var path = _diagnostics.CreateBundle();
            DiagnosticsNote = Labels.LastBundle(Path.GetFileName(path));
            Status = Labels.BundleCreated(Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            AppLog.Warn("The diagnostics bundle could not be created", ex.ToString());
            Status = Labels.BundleFailed(ex.Message);
        }
    }

    private async Task CreateBackupAsync()
    {
        if (_dialogs == null) return;

        var path = await _dialogs.SaveFileAsync(
            Labels.CreateBackup, BackupService.SuggestedFileName(DateTime.Now), "json").ConfigureAwait(true);
        if (path == null) return;

        try
        {
            var result = await Task.Run(() => BackupService.Create(
                path,
                _settings,
                AppPaths.GetDataFile("game_profiles.json"),
                AppPaths.GetDataFile("rgb_presets.json"),
                UpdateVersionText)).ConfigureAwait(true);

            Status = result.Success
                ? Labels.BackupSaved(Path.GetFileName(path))
                : result.Error ?? Labels.BackupFailed;
        }
        catch (Exception ex)
        {
            AppLog.Warn("The backup could not be created", ex.ToString());
            Status = Labels.BackupFailedDetail(ex.Message);
        }
    }

    private async Task RestoreBackupAsync()
    {
        if (_dialogs == null) return;

        var path = await _dialogs.PickFileAsync(Labels.RestoreBackup, "json").ConfigureAwait(true);
        if (path == null) return;

        if (!await _dialogs.ConfirmAsync(Labels.RestoreBackup, Labels.RestoreBackupConfirm).ConfigureAwait(true))
        {
            return;
        }

        try
        {
            var result = await Task.Run(() => BackupService.Restore(
                path,
                AppPaths.GetDataFile("settings.json"),
                AppPaths.GetDataFile("game_profiles.json"),
                AppPaths.GetDataFile("rgb_presets.json"))).ConfigureAwait(true);

            Status = result.Success ? Labels.BackupRestored : result.Error ?? Labels.BackupRestoreFailed;
        }
        catch (Exception ex)
        {
            AppLog.Warn("The backup could not be restored", ex.ToString());
            Status = Labels.BackupRestoreFailedDetail(ex.Message);
        }
    }

    /// <remarks>
    /// A null <c>_urlOpener</c> does not fall back to the shell. Production always injects one
    /// (<see cref="AppHost"/>), so the fallback would only ever run under a test whose fixture forgot to pass a
    /// capturer — and with the test fixtures' <c>example.invalid</c> URLs that used to open a real browser on the
    /// developer's machine mid-test-run. Logging instead keeps that failure visible and harmless.
    /// </remarks>
    private void OpenUrl(string url)
    {
        try
        {
            if (_urlOpener != null)
            {
                _urlOpener(url);
            }
            else
            {
                AppLog.Warn("The link could not be opened", $"No URL opener was injected; refusing to launch a browser for {url}");
            }
        }
        catch (Exception ex)
        {
            Status = Labels.LinkFailed(ex.Message);
        }
    }

    // ================================================================================================================
    // The first-run walkthrough
    // ================================================================================================================

    /// <summary>
    /// Shows the walkthrough on a first run. Called after the capability probe, because step one lists what the probe
    /// found: on a first run with no cache this is the first moment the list can be filled in.
    /// </summary>
    private void MaybeShowOnboarding()
    {
        if (_settings.OnboardingCompleted) return;

        _onboardingStep = 0;
        _showOnboarding = true;
        RaiseOnboardingProperties();
    }

    private void ReopenOnboarding()
    {
        _onboardingStep = 0;
        _showOnboarding = true;
        RaiseOnboardingProperties();
    }

    private void OnboardingNext()
    {
        if (_onboardingStep >= 2) return;
        _onboardingStep++;
        RaiseOnboardingProperties();
    }

    private void OnboardingBack()
    {
        if (_onboardingStep <= 0) return;
        _onboardingStep--;
        RaiseOnboardingProperties();
    }

    /// <summary>
    /// Closes the walkthrough and records that it is done. <paramref name="applyRecommended"/> is what the "Tamamla"
    /// button passes: the recommended starting profile is written only when the user finished the walkthrough, not when
    /// they skipped it.
    /// </summary>
    private async Task FinishOnboardingAsync(bool applyRecommended)
    {
        if (applyRecommended)
        {
            // The recommended profile: Balanced, automatic fans, and the 80 % charge limit when the machine has one.
            // Assigning the selection is the whole gesture — the setters apply the mode and the fan speed.
            if (ModeControlAvailable && SelectedMode?.Mode != OperatingMode.Default)
            {
                var balanced = Modes.FirstOrDefault(m => m.Mode == OperatingMode.Default);
                if (balanced != null) SelectedMode = balanced;
            }

            if (FanControlAvailable && SelectedFanMode?.Mode != FanMode.Auto)
            {
                var auto = FanModes.FirstOrDefault(m => m.Mode == FanMode.Auto);
                if (auto != null) SelectedFanMode = auto;
            }

            if (BatteryLimitAvailable && !BatteryLimitOn) BatteryLimitOn = true;

            if (OnboardingAutoStartAvailable && _onboardingAutoStartOn && _autoStart != null)
            {
                try { await _autoStart.EnableAsync().ConfigureAwait(true); }
                catch (Exception ex) { AppLog.Warn("Autostart could not be enabled", ex.Message); }
            }
        }

        _settings.OnboardingCompleted = true;
        SaveSettings();

        _showOnboarding = false;
        RaiseOnboardingProperties();
    }

    private void RaiseOnboardingProperties()
    {
        Raise(nameof(ShowOnboarding));
        Raise(nameof(OnboardingStep));
        Raise(nameof(IsOnboardingFirstStep));
        Raise(nameof(IsOnboardingSecondStep));
        Raise(nameof(IsOnboardingLastStep));
        Raise(nameof(OnboardingStepLabel));
        Raise(nameof(OnboardingTitle));
        Raise(nameof(OnboardingBody));
        Raise(nameof(OnboardingRecommendedText));
        Raise(nameof(OnboardingAutoStartAvailable));
        Raise(nameof(OnboardingAutoStartOn));
    }
}
