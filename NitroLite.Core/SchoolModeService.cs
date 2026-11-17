using System.IO;
using System.Text.Json;
using NitroLite.Core.Hardware;

namespace NitroLite.Core;

/// <summary>One power-scheme setting school mode lowers on battery.</summary>
public sealed record SchoolModePowerSetting(Guid SubGroup, Guid Setting, uint TargetDc, string Label);

/// <summary>Reads and writes battery (DC) values of the active power scheme.</summary>
public interface IPowerSchemeAccess
{
    /// <summary>
    /// False on a platform that has no power schemes at all, which is every Linux distribution. School mode then
    /// skips these settings instead of reporting eight failures for a feature the machine never had.
    /// </summary>
    bool IsSupported => true;

    bool TryReadDc(Guid subGroup, Guid setting, out uint value);
    bool TryWriteDc(Guid subGroup, Guid setting, uint value);
}

public sealed class SchoolModePowerBackup
{
    public string SubGroup { get; set; } = string.Empty;
    public string Setting { get; set; } = string.Empty;
    public uint PreviousDc { get; set; }
}

/// <summary>
/// Everything school mode replaced, so turning it off can put the machine back exactly as it was.
/// Written before the first change: a crash mid-apply must still be undoable.
/// </summary>
public sealed class SchoolModeBackup
{
    public List<SchoolModePowerBackup> PowerSettings { get; set; } = new();
    public OperatingMode Mode { get; set; } = OperatingMode.Default;
    public FanMode Fan { get; set; } = FanMode.Auto;
    public CpuBoostMode? DcBoost { get; set; }
    public int? RefreshRate { get; set; }
    public int? Brightness { get; set; }
    public bool LightingWasOn { get; set; }
    public DateTime EnabledUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>What school mode managed to change, and what it could not.</summary>
public sealed class SchoolModeOutcome
{
    public List<string> Applied { get; } = new();
    public List<string> Failed { get; } = new();

    /// <summary>Things the user should know that did not stop school mode from being applied.</summary>
    public List<string> Warnings { get; } = new();

    /// <summary>
    /// True when school mode was not applied at all — the undo record could not be written, so nothing was
    /// changed. The caller must leave the switch off and hand the machine back exactly as it found it.
    /// </summary>
    public bool Refused { get; set; }

    public bool Success => Failed.Count == 0;
}

/// <summary>
/// School mode: the most economical the machine can be on one charge, applied as one switch and undone as one
/// switch. Every value it replaces is written to <see cref="BackupPath"/> before the first change.
/// <para>
/// It writes battery (DC) values into the active power scheme instead of switching to the "Power saver" plan.
/// Switching plans would move the scheme the rest of the app writes CPU boost into, and the same settings are
/// reachable directly. DC values also mean Windows applies them on battery and drops them on the adapter by itself.
/// </para>
/// <para>
/// Deliberately not touched: Wi-Fi and Bluetooth (a school day needs them), PCIe link power management (stutters
/// NVMe for very little gain), background services and startup apps (not cleanly reversible).
/// </para>
/// </summary>
public sealed class SchoolModeService
{
    public const int TargetBrightnessPercent = 40;
    public const int TargetRefreshRate = 60;
    public const uint TargetMaxProcessorPercent = 60;
    public const uint TargetDisplayOffSeconds = 180;
    public const uint TargetSleepSeconds = 600;
    public const uint TargetDiskOffSeconds = 120;

    /// <summary>
    /// Passive: the processor slows down before the fan speeds up, so the fan stays off in a quiet room.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The setting is <c>System cooling policy</c> under the processor power management subgroup, and its two values
    /// are 0 = Active and 1 = Passive. Passive is the one a lecture hall wants: the machine throttles instead of
    /// spinning up, which is the difference between a laptop nobody notices and one the whole row can hear.
    /// </para>
    /// <para>
    /// It is written on DC only, like every other setting in <see cref="PowerSettings"/>, because school mode is a
    /// battery profile. On a machine whose firmware hides the setting the write is refused, and that is reported as
    /// one failed part rather than aborting the rest: the processor cap and the screen timeout still apply, and a
    /// school mode that refused to start over a fan curve would be worse than one that is missing a step.
    /// </para>
    /// </remarks>
    public const uint TargetCoolingPolicy = 1;

    /// <summary>100 = the processor prefers efficiency over speed for the same work (CPPC energy preference).</summary>
    public const uint TargetEnergyPreference = 100;

    private static readonly Guid SubProcessor = new("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid MaxProcessorState = new("bc5038f7-23e0-4960-96da-33abaf5935ec");
    private static readonly Guid SystemCoolingPolicy = new("94d3a615-a899-4ac5-ae2b-e4d8f634367f");
    private static readonly Guid EnergyPreference = new("36687f9e-e3a5-4dbf-b1dc-15eb381c6863");
    private static readonly Guid SubEnergySaver = new("de830923-a562-41af-a086-e3a2c6bad2da");
    private static readonly Guid EnergySaverThreshold = new("e69653ca-cf7f-4f05-aa73-cb833fa90ad4");
    private static readonly Guid SubVideo = new("7516b95f-f776-4464-8c53-06167f40cc99");
    private static readonly Guid VideoIdle = new("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");
    private static readonly Guid SubSleep = new("238c9fa8-0aad-41ed-83f4-97be242c8f20");
    private static readonly Guid StandbyIdle = new("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");
    private static readonly Guid SubDisk = new("0012ee47-9041-4b5d-9b77-535fba8b1442");
    private static readonly Guid DiskIdle = new("6738e2c4-e8a5-4a42-b16a-e040e769756e");
    private static readonly Guid SubUsb = new("2a737441-1930-4402-8d77-b2bebba308a3");
    private static readonly Guid UsbSelectiveSuspend = new("48e6b7a6-50f5-4782-a5d4-53bb8f07e226");

    /// <summary>The battery values school mode writes. Labels are catalogue keys, translated where they are shown.</summary>
    public static IReadOnlyList<SchoolModePowerSetting> PowerSettings { get; } = new[]
    {
        new SchoolModePowerSetting(SubProcessor, MaxProcessorState, TargetMaxProcessorPercent, "Maksimum işlemci durumu"),
        new SchoolModePowerSetting(SubProcessor, SystemCoolingPolicy, TargetCoolingPolicy, "Sistem soğutma ilkesi"),
        new SchoolModePowerSetting(SubProcessor, EnergyPreference, TargetEnergyPreference, "İşlemci enerji tercihi"),
        new SchoolModePowerSetting(SubEnergySaver, EnergySaverThreshold, 100, "Windows pil tasarrufu"),
        new SchoolModePowerSetting(SubVideo, VideoIdle, TargetDisplayOffSeconds, "Ekran kapanma süresi"),
        new SchoolModePowerSetting(SubSleep, StandbyIdle, TargetSleepSeconds, "Uyku süresi"),
        new SchoolModePowerSetting(SubDisk, DiskIdle, TargetDiskOffSeconds, "Disk kapanma süresi"),
        new SchoolModePowerSetting(SubUsb, UsbSelectiveSuspend, 1, "USB güç tasarrufu")
    };

    private readonly IHardwareController _hardware;
    private readonly ICpuBoostAccess _cpuBoost;
    private readonly IDisplayRefreshRateService _displayService;
    private readonly IScreenBrightnessService _brightnessService;
    private readonly AppSettings _settings;
    private readonly IPowerSchemeAccess _scheme;

    /// <summary>
    /// <paramref name="powerScheme"/> is required, not optional. It used to default to null and then throw, which let
    /// a caller that forgot it compile cleanly and fail at startup instead — the exact way the production
    /// <c>MainWindow</c> constructor lost its scheme. A required parameter makes that a compile error.
    /// </summary>
    public SchoolModeService(
        IHardwareController hardware,
        ICpuBoostAccess cpuBoost,
        IDisplayRefreshRateService displayService,
        IScreenBrightnessService brightnessService,
        AppSettings settings,
        IPowerSchemeAccess powerScheme)
    {
        // Every dependency is an interface owned by this assembly, which is what lets the same state machine run on
        // the Acer service socket, on direct WMI, or on Linux sysfs without a line changing here.
        _hardware = hardware ?? throw new ArgumentNullException(nameof(hardware));
        _cpuBoost = cpuBoost ?? throw new ArgumentNullException(nameof(cpuBoost));
        _displayService = displayService ?? throw new ArgumentNullException(nameof(displayService));
        _brightnessService = brightnessService ?? throw new ArgumentNullException(nameof(brightnessService));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _scheme = powerScheme ?? throw new ArgumentNullException(nameof(powerScheme));
    }

    /// <summary>Where the undo record lives. Null disables persistence (unit tests).</summary>
    public string? BackupPath { get; set; } = AppPaths.GetDataFile("school_mode_backup.json");

    public Func<OperatingMode>? GetCurrentOperatingMode { get; set; }
    public Func<FanMode>? GetCurrentFanMode { get; set; }

    /// <summary>
    /// Whether the machine lists <paramref name="mode"/> among the modes it offers. Null means "not asked yet", and
    /// then Eco is tried: a capability query that has not run is not evidence that the mode is missing.
    /// </summary>
    public Func<OperatingMode, bool>? IsModeSupported { get; set; }

    /// <summary>
    /// The hardware mode school mode asks for: Eco, the most economical mode the machine offers. A model that does
    /// not list Eco gets Quiet instead, which is the next lowest.
    /// </summary>
    public OperatingMode TargetOperatingMode =>
        IsModeSupported?.Invoke(OperatingMode.Eco) == false ? OperatingMode.Quiet : OperatingMode.Eco;

    /// <summary>
    /// Turns the keyboard backlight off. True when it was on and is now off, so there is something to put back;
    /// false when it was already off. The value is stored as <see cref="SchoolModeBackup.LightingWasOn"/> and is
    /// what decides whether switching school mode off restores the backlight.
    /// </summary>
    public Func<Task<bool>>? TurnLightingOffAsync { get; set; }

    /// <summary>Puts the keyboard backlight back the way the user had it.</summary>
    public Func<Task>? RestoreLightingAsync { get; set; }

    public bool IsEnabled => _settings.SchoolModeEnabled;

    /// <summary>Applies school mode, recording what it replaces first. Already on: re-applies without re-recording.</summary>
    public async Task<SchoolModeOutcome> EnableAsync()
    {
        if (_settings.SchoolModeEnabled) return await ReapplyAsync();

        // Reading the current state launches nine powercfg processes and a WMI call, which is up to a second of
        // work. It runs off the UI thread so flipping the switch does not freeze the window; none of it touches a
        // control.
        var backup = await Task.Run(() =>
        {
            var state = new SchoolModeBackup
            {
                Mode = GetCurrentOperatingMode?.Invoke() ?? OperatingMode.Default,
                Fan = GetCurrentFanMode?.Invoke() ?? FanMode.Auto,
                DcBoost = _cpuBoost.IsSupported ? TryQueryDcBoost() : null,
                RefreshRate = _displayService.IsSupported ? TryQueryRefreshRate() : null,
                Brightness = _brightnessService.GetBrightness()
            };

            // A platform with no power schemes has nothing to record here, and recording nothing is what makes the
            // undo record honest: switching school mode off will not try to write settings that were never read.
            if (_scheme.IsSupported)
            {
                foreach (var setting in PowerSettings)
                {
                    if (_scheme.TryReadDc(setting.SubGroup, setting.Setting, out uint previous))
                    {
                        state.PowerSettings.Add(new SchoolModePowerBackup
                        {
                            SubGroup = setting.SubGroup.ToString(),
                            Setting = setting.Setting.ToString(),
                            PreviousDc = previous
                        });
                    }
                }
            }

            return state;
        });

        // The undo record goes to disk before the first change, so a crash or a forced exit still leaves a way back.
        // If it cannot be written, nothing is changed: a mode the user cannot turn off is worse than no mode, and
        // "switching this off puts everything back" is the whole promise the switch makes.
        if (!WriteBackup(backup))
        {
            var refused = new SchoolModeOutcome { Refused = true };
            refused.Failed.Add(Loc.T("Geri alma kaydı yazılamadı (disk dolu veya klasör salt okunur); okul modu uygulanmadı."));
            return refused;
        }

        _settings.SchoolModeEnabled = true;

        var outcome = await ApplyAsync(BackedUpSettings(backup));
        if (TurnLightingOffAsync != null)
        {
            // A keyboard that refuses the command must not undo the rest: the power settings are already written and
            // the undo record is on disk, so the failure is reported as one step, not as a broken school mode.
            try
            {
                backup.LightingWasOn = await TurnLightingOffAsync();
                if (!WriteBackup(backup))
                {
                    // The one change made after the record was written could not be added to it, so it is undone
                    // here: a school mode that cannot put the keyboard back is not one that can be undone.
                    if (RestoreLightingAsync != null) await RestoreLightingAsync();
                    backup.LightingWasOn = false;
                    outcome.Failed.Add(Loc.T("Klavye ışığı"));
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("Okul modu: klavye ışığı kapatılamadı", ex.Message);
                outcome.Failed.Add(Loc.T("Klavye ışığı"));
            }
        }
        return outcome;
    }

    /// <summary>
    /// A backup file with school mode switched off means a run died between writing the undo record and saving the
    /// setting. The machine is still in school-mode values — 60 % processor, dim screen, 60 Hz — with nothing in the
    /// UI to say so, so the record is applied in reverse and removed. Returns true when an orphan was undone.
    /// </summary>
    public async Task<bool> RecoverOrphanedBackupAsync()
    {
        if (_settings.SchoolModeEnabled) return false;

        var backup = ReadBackup();
        if (backup == null) return false;

        AppLog.Warn("Sahipsiz okul modu yedeği bulundu", "Yarım kalan okul modu geri alınıyor.");
        await RestoreFromBackupAsync(backup);
        DeleteBackup();
        return true;
    }

    /// <summary>Puts back every value school mode replaced.</summary>
    public async Task<SchoolModeOutcome> DisableAsync()
    {
        var backup = ReadBackup();

        // The record is deleted only when it was actually read. A null here means the file is missing — nothing to
        // delete — or unreadable, and deleting an unreadable record throws away the only copy of the values the
        // machine was in before school mode, which is the one thing that could still be repaired by hand.
        var outcome = backup == null ? new SchoolModeOutcome() : await RestoreFromBackupAsync(backup);
        if (backup != null) DeleteBackup();

        _settings.SchoolModeEnabled = false;
        return outcome;
    }

    /// <summary>Applies the undo record. Shared by the normal switch-off and the orphaned-record recovery.</summary>
    private async Task<SchoolModeOutcome> RestoreFromBackupAsync(SchoolModeBackup backup)
    {
        var outcome = new SchoolModeOutcome();

        if (backup != null)
        {
            // One powercfg process per recorded setting: off the UI thread, like the forward pass.
            var (restored, restoreFailed) = await Task.Run(() =>
            {
                var ok = new List<string>();
                var bad = new List<string>();
                foreach (var saved in backup.PowerSettings)
                {
                    if (!Guid.TryParse(saved.SubGroup, out var subGroup) || !Guid.TryParse(saved.Setting, out var setting))
                        continue;
                    string label = PowerSettings.FirstOrDefault(s => s.SubGroup == subGroup && s.Setting == setting)?.Label
                                   ?? Loc.T("Güç planı ayarı");
                    if (_scheme.TryWriteDc(subGroup, setting, saved.PreviousDc)) ok.Add(label);
                    else bad.Add(label);
                }
                return (ok, bad);
            });
            outcome.Applied.AddRange(restored);
            outcome.Failed.AddRange(restoreFailed);

            if (backup.DcBoost.HasValue && _cpuBoost.IsSupported)
            {
                if (await _cpuBoost.SetBoostModeAsync(backup.DcBoost.Value, targetAc: false, targetDc: true))
                    outcome.Applied.Add(Loc.T("CPU Boost (pil)"));
                else outcome.Failed.Add(Loc.T("CPU Boost (pil)"));
            }

            if (await _hardware.SetOperatingModeAsync(backup.Mode)) outcome.Applied.Add(Loc.T("Çalışma modu"));
            else outcome.Failed.Add(Loc.T("Çalışma modu"));

            if (await _hardware.SetFanModeAsync(backup.Fan)) outcome.Applied.Add(Loc.T("Fan"));
            else outcome.Failed.Add(Loc.T("Fan"));

            if (backup.RefreshRate is int rate && rate > 0 && _displayService.IsSupported)
            {
                if (_displayService.SetRefreshRate(rate)) outcome.Applied.Add(Loc.T("Ekran tazeleme hızı"));
                else outcome.Failed.Add(Loc.T("Ekran tazeleme hızı"));
            }

            if (backup.Brightness is int brightness)
            {
                if (_brightnessService.SetBrightness(brightness)) outcome.Applied.Add(Loc.T("Ekran parlaklığı"));
                else outcome.Failed.Add(Loc.T("Ekran parlaklığı"));
            }

            if (backup.LightingWasOn && RestoreLightingAsync != null)
            {
                try { await RestoreLightingAsync(); outcome.Applied.Add(Loc.T("Klavye ışığı")); }
                catch { outcome.Failed.Add(Loc.T("Klavye ışığı")); }
            }
        }

        return outcome;
    }

    /// <summary>Applies the school-mode values again without touching the undo record (start of a new session).</summary>
    public async Task<SchoolModeOutcome> ReapplyAsync()
    {
        // The undo record from the first enable decides what may be written. A setting it does not hold was never
        // readable, so re-applying it would change a value switching school mode off cannot restore. A missing
        // record means the machine is already in school-mode values, so writing them again loses nothing.
        var backup = ReadBackup();
        var outcome = await ApplyAsync(backup == null ? null : BackedUpSettings(backup));
        if (TurnLightingOffAsync != null)
        {
            try { await TurnLightingOffAsync(); } catch { }
        }
        return outcome;
    }

    /// <summary>
    /// The power-scheme settings whose previous value is in the undo record. A setting that could not be read is
    /// not in it, and writing that setting would change something switching school mode off could not put back.
    /// </summary>
    private static HashSet<(Guid SubGroup, Guid Setting)> BackedUpSettings(SchoolModeBackup backup)
    {
        var set = new HashSet<(Guid, Guid)>();
        foreach (var saved in backup.PowerSettings)
        {
            if (Guid.TryParse(saved.SubGroup, out var subGroup) && Guid.TryParse(saved.Setting, out var setting))
            {
                set.Add((subGroup, setting));
            }
        }
        return set;
    }

    /// <summary>
    /// Applies the school-mode values. <paramref name="only"/> restricts the power-scheme writes to the settings
    /// whose previous value is in the undo record; null means "no record to respect" and every setting is written.
    /// </summary>
    private async Task<SchoolModeOutcome> ApplyAsync(IReadOnlySet<(Guid SubGroup, Guid Setting)>? only = null)
    {
        var outcome = new SchoolModeOutcome();

        // Eight powercfg writes, each a process launch. Off the UI thread for the same reason as the read above.
        // A platform without power schemes skips the whole block: reporting eight failures for settings the machine
        // does not have would make a working school mode look broken.
        if (_scheme.IsSupported)
        {
            var (powerApplied, powerFailed, powerSkipped) = await Task.Run(() =>
            {
                var applied = new List<string>();
                var failed = new List<string>();
                var skipped = new List<string>();
                foreach (var setting in PowerSettings)
                {
                    if (only != null && !only.Contains((setting.SubGroup, setting.Setting)))
                    {
                        // Its current value could not be read, so it is not in the undo record. Writing it would
                        // break the promise that switching school mode off puts everything back.
                        skipped.Add(Loc.T(setting.Label));
                        continue;
                    }
                    if (_scheme.TryWriteDc(setting.SubGroup, setting.Setting, setting.TargetDc)) applied.Add(Loc.T(setting.Label));
                    else failed.Add(Loc.T(setting.Label));
                }
                return (applied, failed, skipped);
            });
            outcome.Applied.AddRange(powerApplied);
            outcome.Failed.AddRange(powerFailed);
            if (powerSkipped.Count > 0)
            {
                AppLog.Warn("Okul modu: okunamayan ayarlar atlandı", string.Join(", ", powerSkipped));
                outcome.Warnings.Add(Loc.T($"Okul modu: mevcut değeri okunamayan ayarlar değiştirilmedi: {string.Join(", ", powerSkipped)}"));
            }
        }

        if (!_cpuBoost.IsSupported)
        {
            // No boost node on this kernel: nothing to disable, and nothing to report.
        }
        else if (await _cpuBoost.SetBoostModeAsync(CpuBoostMode.Disabled, targetAc: false, targetDc: true))
            outcome.Applied.Add(Loc.T("CPU Boost (pil)"));
        else outcome.Failed.Add(Loc.T("CPU Boost (pil)"));

        var targetMode = TargetOperatingMode;
        bool modeOk = await _hardware.SetOperatingModeAsync(targetMode);
        if (!modeOk && targetMode != OperatingMode.Quiet)
        {
            // A model that lists Eco but refuses it (some firmware only allows it on battery) still gets the next
            // lowest mode rather than no mode change at all.
            targetMode = OperatingMode.Quiet;
            modeOk = await _hardware.SetOperatingModeAsync(OperatingMode.Quiet);
        }
        if (modeOk)
            outcome.Applied.Add(Loc.T(targetMode == OperatingMode.Eco ? "Çalışma modu (Eco)" : "Çalışma modu (Sessiz)"));
        else outcome.Failed.Add(Loc.T("Çalışma modu"));

        if (await _hardware.SetFanModeAsync(FanMode.Auto)) outcome.Applied.Add(Loc.T("Fan (Otomatik)"));
        else outcome.Failed.Add(Loc.T("Fan (Otomatik)"));

        if (_displayService.IsSupported)
        {
            int currentRate = _displayService.GetCurrentRefreshRate();
            if (currentRate > 0 && currentRate <= TargetRefreshRate) outcome.Applied.Add(Loc.T("Ekran 60 Hz"));
            else if (_displayService.SetRefreshRate(TargetRefreshRate)) outcome.Applied.Add(Loc.T("Ekran 60 Hz"));
            else outcome.Failed.Add(Loc.T("Ekran 60 Hz"));
        }

        // A panel that does not report brightness (external monitor only) is not a failure: there is nothing to dim.
        int? brightness = _brightnessService.GetBrightness();
        if (brightness.HasValue)
        {
            if (brightness.Value <= TargetBrightnessPercent || _brightnessService.SetBrightness(TargetBrightnessPercent))
                outcome.Applied.Add(Loc.T("Ekran parlaklığı"));
            else outcome.Failed.Add(Loc.T("Ekran parlaklığı"));
        }

        return outcome;
    }

    private CpuBoostMode? TryQueryDcBoost()
    {
        try { return _cpuBoost.QueryBoostModeExact(false); }
        catch { return null; }
    }

    /// <summary>The current refresh rate, or null when the panel does not report one.</summary>
    private int? TryQueryRefreshRate()
    {
        try
        {
            int hz = _displayService.GetCurrentRefreshRate();
            return hz > 0 ? hz : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Writes the undo record. Returns false when it could not be written, and the caller then refuses to apply
    /// school mode at all: without a record there is no way back, and a switch that cannot be undone is worse than
    /// one that never turned on.
    /// </summary>
    private bool WriteBackup(SchoolModeBackup backup)
    {
        var path = BackupPath;
        if (string.IsNullOrEmpty(path)) return true; // No persistence configured (unit tests): memory is the record.
        try
        {
            // A redirected data directory may not exist yet; creating it here is what makes the first enable work
            // on a fresh profile instead of silently losing the undo record.
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(backup, new JsonSerializerOptions { WriteIndented = true });
            AtomicFile.WriteAllText(path, json);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Okul modu yedeği yazılamadı", $"{path}: {ex.Message}");
            return false;
        }
    }

    private SchoolModeBackup? ReadBackup()
    {
        var path = BackupPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<SchoolModeBackup>(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Okul modu yedeği okunamadı", $"{path}: {ex.Message}");
            return null;
        }
    }

    private void DeleteBackup()
    {
        var path = BackupPath;
        if (string.IsNullOrEmpty(path)) return;
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { AppLog.Warn("Okul modu yedeği silinemedi", $"{path}: {ex.Message}"); }
    }
}
