using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace NitroLite.Core;

/// <summary>One power-scheme setting school mode lowers on battery.</summary>
public sealed record SchoolModePowerSetting(Guid SubGroup, Guid Setting, uint TargetDc, string Label);

/// <summary>Reads and writes battery (DC) values of the active power scheme.</summary>
public interface IPowerSchemeAccess
{
    bool TryReadDc(Guid subGroup, Guid setting, out uint value);
    bool TryWriteDc(Guid subGroup, Guid setting, uint value);
}

/// <summary>The real power scheme, through powrprof.dll.</summary>
public sealed class WindowsPowerSchemeAccess : IPowerSchemeAccess
{
    public bool TryReadDc(Guid subGroup, Guid setting, out uint value) =>
        CpuPowerService.TryReadDcValue(subGroup, setting, out value);

    public bool TryWriteDc(Guid subGroup, Guid setting, uint value) =>
        CpuPowerService.TryWriteDcValue(subGroup, setting, value);
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
    public const uint TargetMaxProcessorPercent = 70;
    public const uint TargetDisplayOffSeconds = 180;
    public const uint TargetSleepSeconds = 600;
    public const uint TargetDiskOffSeconds = 120;

    private static readonly Guid SubProcessor = new("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid MaxProcessorState = new("bc5038f7-23e0-4960-96da-33abaf5935ec");
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
        new SchoolModePowerSetting(SubEnergySaver, EnergySaverThreshold, 100, "Windows pil tasarrufu"),
        new SchoolModePowerSetting(SubVideo, VideoIdle, TargetDisplayOffSeconds, "Ekran kapanma süresi"),
        new SchoolModePowerSetting(SubSleep, StandbyIdle, TargetSleepSeconds, "Uyku süresi"),
        new SchoolModePowerSetting(SubDisk, DiskIdle, TargetDiskOffSeconds, "Disk kapanma süresi"),
        new SchoolModePowerSetting(SubUsb, UsbSelectiveSuspend, 1, "USB güç tasarrufu")
    };

    private readonly AcerAgentClient _agentClient;
    private readonly CpuPowerService _cpuPowerService;
    private readonly IDisplayRefreshRateService _displayService;
    private readonly IScreenBrightnessService _brightnessService;
    private readonly AppSettings _settings;
    private readonly IPowerSchemeAccess _scheme;

    public SchoolModeService(
        AcerAgentClient agentClient,
        CpuPowerService cpuPowerService,
        IDisplayRefreshRateService displayService,
        IScreenBrightnessService brightnessService,
        AppSettings settings,
        IPowerSchemeAccess? powerScheme = null)
    {
        _agentClient = agentClient ?? throw new ArgumentNullException(nameof(agentClient));
        _cpuPowerService = cpuPowerService ?? throw new ArgumentNullException(nameof(cpuPowerService));
        _displayService = displayService ?? throw new ArgumentNullException(nameof(displayService));
        _brightnessService = brightnessService ?? throw new ArgumentNullException(nameof(brightnessService));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _scheme = powerScheme ?? new WindowsPowerSchemeAccess();
    }

    /// <summary>Where the undo record lives. Null disables persistence (unit tests).</summary>
    public string? BackupPath { get; set; } = AppPaths.GetDataFile("school_mode_backup.json");

    public Func<OperatingMode>? GetCurrentOperatingMode { get; set; }
    public Func<FanMode>? GetCurrentFanMode { get; set; }

    /// <summary>Turns the keyboard backlight off; true when it is off afterwards.</summary>
    public Func<Task<bool>>? TurnLightingOffAsync { get; set; }

    /// <summary>Puts the keyboard backlight back the way the user had it.</summary>
    public Func<Task>? RestoreLightingAsync { get; set; }

    public bool IsEnabled => _settings.SchoolModeEnabled;

    /// <summary>Applies school mode, recording what it replaces first. Already on: re-applies without re-recording.</summary>
    public async Task<SchoolModeOutcome> EnableAsync()
    {
        if (_settings.SchoolModeEnabled) return await ReapplyAsync();

        var backup = new SchoolModeBackup
        {
            Mode = GetCurrentOperatingMode?.Invoke() ?? OperatingMode.Default,
            Fan = GetCurrentFanMode?.Invoke() ?? FanMode.Auto,
            DcBoost = TryQueryDcBoost(),
            RefreshRate = _displayService.GetCurrentRefreshRate() is var hz && hz > 0 ? hz : null,
            Brightness = _brightnessService.GetBrightness()
        };

        foreach (var setting in PowerSettings)
        {
            if (_scheme.TryReadDc(setting.SubGroup, setting.Setting, out uint previous))
            {
                backup.PowerSettings.Add(new SchoolModePowerBackup
                {
                    SubGroup = setting.SubGroup.ToString(),
                    Setting = setting.Setting.ToString(),
                    PreviousDc = previous
                });
            }
        }

        // The undo record goes to disk before the first change, so a crash or a forced exit still leaves a way back.
        WriteBackup(backup);
        _settings.SchoolModeEnabled = true;

        var outcome = await ApplyAsync();
        if (TurnLightingOffAsync != null)
        {
            // A keyboard that refuses the command must not undo the rest: the power settings are already written and
            // the undo record is on disk, so the failure is reported as one step, not as a broken school mode.
            try
            {
                backup.LightingWasOn = await TurnLightingOffAsync();
                WriteBackup(backup);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Okul modu: klavye ışığı kapatılamadı", ex.Message);
                outcome.Failed.Add(Loc.T("Klavye ışığı"));
            }
        }
        return outcome;
    }

    /// <summary>Puts back every value school mode replaced.</summary>
    public async Task<SchoolModeOutcome> DisableAsync()
    {
        var outcome = new SchoolModeOutcome();
        var backup = ReadBackup();

        if (backup != null)
        {
            foreach (var saved in backup.PowerSettings)
            {
                if (!Guid.TryParse(saved.SubGroup, out var subGroup) || !Guid.TryParse(saved.Setting, out var setting))
                    continue;
                string label = PowerSettings.FirstOrDefault(s => s.SubGroup == subGroup && s.Setting == setting)?.Label
                               ?? Loc.T("Güç planı ayarı");
                if (_scheme.TryWriteDc(subGroup, setting, saved.PreviousDc)) outcome.Applied.Add(label);
                else outcome.Failed.Add(label);
            }

            if (backup.DcBoost.HasValue)
            {
                if (await _cpuPowerService.SetBoostModeAsync(backup.DcBoost.Value, targetAc: false, targetDc: true))
                    outcome.Applied.Add(Loc.T("CPU Boost (pil)"));
                else outcome.Failed.Add(Loc.T("CPU Boost (pil)"));
            }

            if (await _agentClient.SetOperatingModeAsync(backup.Mode)) outcome.Applied.Add(Loc.T("Çalışma modu"));
            else outcome.Failed.Add(Loc.T("Çalışma modu"));

            if (await _agentClient.SetFanModeAsync(backup.Fan)) outcome.Applied.Add(Loc.T("Fan"));
            else outcome.Failed.Add(Loc.T("Fan"));

            if (backup.RefreshRate is int rate && rate > 0)
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

        DeleteBackup();
        _settings.SchoolModeEnabled = false;
        return outcome;
    }

    /// <summary>Applies the school-mode values again without touching the undo record (start of a new session).</summary>
    public async Task<SchoolModeOutcome> ReapplyAsync()
    {
        var outcome = await ApplyAsync();
        if (TurnLightingOffAsync != null)
        {
            try { await TurnLightingOffAsync(); } catch { }
        }
        return outcome;
    }

    private async Task<SchoolModeOutcome> ApplyAsync()
    {
        var outcome = new SchoolModeOutcome();

        foreach (var setting in PowerSettings)
        {
            if (_scheme.TryWriteDc(setting.SubGroup, setting.Setting, setting.TargetDc)) outcome.Applied.Add(Loc.T(setting.Label));
            else outcome.Failed.Add(Loc.T(setting.Label));
        }

        if (await _cpuPowerService.SetBoostModeAsync(CpuBoostMode.Disabled, targetAc: false, targetDc: true))
            outcome.Applied.Add(Loc.T("CPU Boost (pil)"));
        else outcome.Failed.Add(Loc.T("CPU Boost (pil)"));

        if (await _agentClient.SetOperatingModeAsync(OperatingMode.Quiet)) outcome.Applied.Add(Loc.T("Çalışma modu (Sessiz)"));
        else outcome.Failed.Add(Loc.T("Çalışma modu (Sessiz)"));

        if (await _agentClient.SetFanModeAsync(FanMode.Auto)) outcome.Applied.Add(Loc.T("Fan (Otomatik)"));
        else outcome.Failed.Add(Loc.T("Fan (Otomatik)"));

        int currentRate = _displayService.GetCurrentRefreshRate();
        if (currentRate > 0 && currentRate <= TargetRefreshRate) outcome.Applied.Add(Loc.T("Ekran 60 Hz"));
        else if (_displayService.SetRefreshRate(TargetRefreshRate)) outcome.Applied.Add(Loc.T("Ekran 60 Hz"));
        else outcome.Failed.Add(Loc.T("Ekran 60 Hz"));

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
        try { return _cpuPowerService.QueryBoostModeExact(false); }
        catch { return null; }
    }

    private void WriteBackup(SchoolModeBackup backup)
    {
        var path = BackupPath;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var json = JsonSerializer.Serialize(backup, new JsonSerializerOptions { WriteIndented = true });
            var tmp = path + ".tmp." + Guid.NewGuid().ToString("N");
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            // Without this file school mode cannot be undone, so the failure has to reach the log even though the
            // running session still holds the values in memory.
            AppLog.Warn("Okul modu yedeği yazılamadı", $"{path}: {ex.Message}");
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
