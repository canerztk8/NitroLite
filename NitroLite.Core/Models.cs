using System.IO;
using System.Text.Json.Serialization;

namespace NitroLite.Core;

public enum OperatingMode
{
    Quiet = 0,    // Silent
    Default = 1,  // Balanced
    Extreme = 4,  // Power / Performance (+50 MHz GPU OC)
    Turbo = 5,    // Turbo (+100 MHz GPU OC, Max Fans)
    Eco = 6       // Eco Battery Saver
}

public enum GpuOperatingMode
{
    Optimus = 0,        // Hybrid iGPU + dGPU
    DiscreteGpu = 1,    // NVIDIA GPU only (dGPU)
    Automatic = 2       // Advanced Optimus / Dynamic Display Switch (DDS)
}

public enum FanMode
{
    Auto = 0,
    Max = 1,
    Custom = 2
}

public enum DcPowerProfilePreference
{
    Eco = 0,      // 60Hz, Quiet Fan, Disabled CPU Boost
    Balanced = 1  // Retain active settings (NitroSense behavior)
}

/// <summary>
/// Colour world of the window. The OSD is always dark and does not follow this.
/// </summary>
/// <remarks>
/// The numeric values are the on-disk contract: a settings file written before the extra palettes existed carries
/// <c>0</c>, <c>1</c> or <c>2</c>, and those still mean Light, Dark and System. New members are appended, never
/// inserted, and <see cref="ThemeCatalog"/> is what decides which of them are dark.
/// </remarks>
public enum AppTheme
{
    /// <summary>
    /// The warm-cream "recording paper" surface NitroLite shipped with, and the palette a fresh install starts in.
    /// </summary>
    WarmCream = 0,
    Dark = 1,

    /// <summary>
    /// Follow the Windows app theme. Stored as its own value, not resolved on save, so a user who picked "System"
    /// keeps following Windows instead of being frozen into whichever palette was current that day.
    /// </summary>
    System = 2,

    /// <summary>Pure black for OLED panels, with Nitro red as the only accent.</summary>
    MidnightOled = 3,

    /// <summary>Cold anthracite with an ice-blue accent.</summary>
    SlateSteel = 4,

    /// <summary>Warm coffee ground with a gold accent.</summary>
    AmberConsole = 5,

    /// <summary>Nord's polar-night greys with a glacier-turquoise accent.</summary>
    NordFrost = 6
}

public class MonitorData
{
    [JsonPropertyName("CPU_TEMPERATURE")]
    public int CpuTemperature { get; set; }

    [JsonPropertyName("GPU1_TEMPERATURE")]
    public int GpuTemperature { get; set; }

    [JsonPropertyName("SYS1_TEMPERATURE")]
    public int SysTemperature { get; set; }

    [JsonPropertyName("CPU_FANSPEED")]
    public int CpuFanSpeed { get; set; }

    [JsonPropertyName("GPU1_FANSPEED")]
    public int GpuFanSpeed { get; set; }

    [JsonPropertyName("SYS1_FANSPEED")]
    public int SysFanSpeed { get; set; }

    [JsonPropertyName("CPU_USAGE")]
    public double CpuUsage { get; set; }

    [JsonPropertyName("GPU1_USAGE")]
    public double GpuUsage { get; set; }

    [JsonPropertyName("RAM_USAGE")]
    public double RamUsage { get; set; }

    [JsonPropertyName("RAM_TOTAL")]
    public double RamTotalMB { get; set; }

    [JsonPropertyName("CPU_FREQUENCY")]
    public double CpuFrequencyMHz { get; set; }

    [JsonPropertyName("GPU1_FREQUENCY")]
    public double GpuFrequencyMHz { get; set; }

    [JsonPropertyName("CPU_CORE_AMOUNT")]
    public int CpuCores { get; set; }

    public double GpuPowerWatts { get; set; }
    public double CpuPowerWatts { get; set; }
    public double VramUsedMB { get; set; }
    public double VramTotalMB { get; set; }

    /// <summary>
    /// True when NVML answered for this sample. False means the GPU power/VRAM/clock values are unknown, not zero:
    /// a running GPU is never at 0 W, so "0" must not be shown as a reading.
    /// </summary>
    [JsonIgnore]
    public bool GpuMetricsAvailable { get; set; }

    /// <summary>
    /// True when <see cref="CpuFanSpeed"/> and <see cref="GpuFanSpeed"/> are tachometer readings the hardware
    /// actually reported. False means they were derived from a fan percentage and a model ceiling, or that nothing
    /// measured the fans at all.
    /// <para>
    /// The distinction decides whether the fan-stall watchdog may judge a sample. A derived number is a restatement
    /// of what the app asked for, not a measurement of what the fan is doing: a fan that has failed still reads
    /// "34 %" because the register says 34 %, so judging it would either hide a real stall or invent one.
    /// </para>
    /// </summary>
    [JsonIgnore]
    public bool IsFanRpmMeasured { get; set; }

    /// <summary>
    /// The processor's marketing name, filled by the platform monitor rather than by the Acer backend: the firmware
    /// answers sensors, not model strings. Empty when nothing could read it, and the page then shows its own label.
    /// </summary>
    [JsonIgnore]
    public string CpuName { get; set; } = "";

    /// <summary>The discrete graphics adapter's name, from the same source as <see cref="CpuName"/>.</summary>
    [JsonIgnore]
    public string GpuName { get; set; } = "";

    public DateTime Timestamp { get; set; } = DateTime.Now;
}

public class BatteryStatusInfo
{
    public int BatteryPercent { get; set; }
    public bool IsCharging { get; set; }
    public bool IsPluggedIn { get; set; }
    public bool IsWatchdogActive { get; set; }
    public int EstimatedRemainingMinutes { get; set; }
    public bool IsOvercharged { get; set; } // > 80%
    public double DischargeRateWatts { get; set; }
    public double ChargeRateWatts { get; set; }
    // True when the charge rate was not read (game session fast path): do not infer "full/held" from 0 W.
    public bool ChargeRateUnknown { get; set; }
    public bool IsDischarging { get; set; }
    public double DischargeRateMilliwatts { get; set; }
    public double RemainingCapacityMWh { get; set; }
    public double SmoothedDischargeRateWatts { get; set; }
}

public class BatteryHealthDetails
{
    public double DesignCapacityMWh { get; set; } = 0;
    public double FullChargeCapacityMWh { get; set; } = 0;
    public double RemainingCapacityMWh { get; set; } = 0;
    public double WearLevelPercent { get; set; } = 0;
    public double HealthPercent { get; set; } = 0;
    public double VoltageVolts { get; set; } = 0;
    public string Manufacturer { get; set; } = "";
    public string ModelName { get; set; } = "";
    // True when DesignCapacityMWh is the AN16-41 factory constant because WMI and powercfg gave no value.
    public bool IsDesignCapacityEstimated { get; set; }
    // True when FullChargeCapacityMWh is DesignCapacity × 0.923 because WMI reported no full-charge capacity
    // (WearLevelPercent / HealthPercent are then derived from an assumption, not a measurement).
    public bool IsFullChargeCapacityEstimated { get; set; }
    // True when VoltageVolts is the 16.45 V nominal constant because WMI reported no voltage.
    public bool IsVoltageEstimated { get; set; }
    // Win32_Battery.Chemistry as text ("Lityum-iyon", …); empty when unknown.
    public string Chemistry { get; set; } = "";
    // BatteryCycleCount; null when the firmware does not report it (reports 0).
    public int? CycleCount { get; set; }
}

public class GameProfile
{
    public string ExeName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public OperatingMode TargetMode { get; set; } = OperatingMode.Turbo;
    public CpuBoostMode TargetCpuBoost { get; set; } = CpuBoostMode.Disabled;
    public FanMode TargetFanMode { get; set; } = FanMode.Auto;
    public string InstallPath { get; set; } = "";
    public bool AutoApply { get; set; } = true;
    public int TargetAtkMouseProfile { get; set; } = 0; // 0: Değiştirme / Pasif, 1..4: Config 1..4

    /// <summary>
    /// Run a fan curve for the length of this game's session instead of a fixed <see cref="TargetFanMode"/>.
    /// The shape comes from <see cref="TargetFanCurvePoints"/>; when that is null the desktop curve is used.
    /// </summary>
    public bool TargetFanCurveEnabled { get; set; } = false;

    /// <summary>This game's own curve anchors. Null means "follow the curve set on the fan card".</summary>
    public List<FanCurvePoint>? TargetFanCurvePoints { get; set; }

    /// <summary>
    /// False when the recorded executable is gone (game uninstalled or moved). A profile without a path is
    /// treated as installed: it was added by hand and there is nothing to check.
    /// </summary>
    [JsonIgnore]
    public bool IsInstalled => string.IsNullOrWhiteSpace(InstallPath) || File.Exists(InstallPath);
}

/// <summary>
/// The game rules a fresh install starts from, and the list <see cref="AppSettings.GameProfiles"/> falls back to when
/// a settings file predates the field.
/// </summary>
/// <remarks>
/// These are the same titles the Windows build seeds into <c>game_profiles.json</c>, so a user who moves between the
/// two front ends recognises the list. They are only defaults: a settings file that carries its own list — including
/// an empty one, which is how a user says "no automatic switching" — is never re-seeded.
/// </remarks>
public static class GameProfileDefaults
{
    public static List<GameProfile> Create() => new()
    {
        new GameProfile { ExeName = "cs2", DisplayName = "Counter-Strike 2", TargetMode = OperatingMode.Turbo, TargetCpuBoost = CpuBoostMode.Aggressive },
        new GameProfile { ExeName = "VALORANT-Win64-Shipping", DisplayName = "Valorant", TargetMode = OperatingMode.Turbo, TargetCpuBoost = CpuBoostMode.Aggressive },
        new GameProfile { ExeName = "bfv", DisplayName = "Battlefield V", TargetMode = OperatingMode.Turbo, TargetCpuBoost = CpuBoostMode.Aggressive },
        new GameProfile { ExeName = "P5R", DisplayName = "Persona 5 Royal", TargetMode = OperatingMode.Extreme, TargetCpuBoost = CpuBoostMode.Efficient },
        new GameProfile { ExeName = "TslGame", DisplayName = "PUBG: BATTLEGROUNDS", TargetMode = OperatingMode.Turbo, TargetCpuBoost = CpuBoostMode.Aggressive, TargetFanMode = FanMode.Max },
        new GameProfile { ExeName = "Balatro", DisplayName = "Balatro", TargetMode = OperatingMode.Default, TargetCpuBoost = CpuBoostMode.Disabled },
        new GameProfile { ExeName = "Cyberpunk2077", DisplayName = "Cyberpunk 2077", TargetMode = OperatingMode.Turbo, TargetCpuBoost = CpuBoostMode.Aggressive },
        new GameProfile { ExeName = "b1-Win64-Shipping", DisplayName = "Black Myth: Wukong", TargetMode = OperatingMode.Turbo, TargetCpuBoost = CpuBoostMode.Aggressive },
        new GameProfile { ExeName = "GTA5", DisplayName = "Grand Theft Auto V", TargetMode = OperatingMode.Turbo, TargetCpuBoost = CpuBoostMode.Efficient },
        new GameProfile { ExeName = "RainbowSix", DisplayName = "Tom Clancy's Rainbow Six Siege", TargetMode = OperatingMode.Turbo, TargetCpuBoost = CpuBoostMode.Aggressive }
    };
}

public enum ThermalStatusLevel
{
    Excellent,
    Good,
    Degraded,
    Critical
}

public class ThermalStabilityTrend
{
    public int DaysCount { get; set; }
    public double StabilityScorePercent { get; set; }
    public double DegradationSlopePerDay { get; set; }
    public string StatusText { get; set; } = string.Empty;
    public ThermalStatusLevel StatusLevel { get; set; }
    public double CurrentRatio { get; set; }
    public double BaselineRatio { get; set; }
    public double AverageRatio { get; set; }
    public double CoefficientOfVariation { get; set; }
}

/// <summary>
/// One capability answer as it is written to the settings file. The outcome is stored by name rather than by number
/// so a reordered enum cannot turn "access denied" into "supported" on the next launch.
/// </summary>
public class CachedCapability
{
    public string Capability { get; set; } = "";
    public string Outcome { get; set; } = "";
    public string Detail { get; set; } = "";
}

/// <summary>
/// The capability probe result, cached so the window does not re-probe WMI and the registry on every launch.
/// <para>
/// <see cref="Key"/> is "model code|BIOS version". A BIOS update changes it, which is the point: a firmware update
/// can add or remove a WMI method, so an answer captured under the old firmware must not be reused.
/// </para>
/// </summary>
public class HardwareCapabilityCache
{
    public string Key { get; set; } = "";
    public DateTime CapturedUtc { get; set; }
    public List<CachedCapability> Capabilities { get; set; } = new();

    /// <summary>
    /// The probe version that wrote this cache. Bumped when a capability answer changes meaning, so a cache written by
    /// an older build is a miss rather than a stale "not supported" the user cannot clear without finding the rescan
    /// button. Zero means the cache predates the field.
    /// </summary>
    public int ProbeVersion { get; set; }

    /// <summary>
    /// The <c>GetGamingMiscSetting(0x09) == 3</c> answer, when an elevated session managed to read it. Null means
    /// nobody asked, which is not the same as "the machine said no".
    /// </summary>
    public bool? WmiMuxSignal { get; set; }

    /// <summary>
    /// Whether <c>WMID_GUID5</c> was registered when the cache was written. Null on a cache from a build that did not
    /// record it, which reads as "nobody asked" rather than "the machine said no".
    /// </summary>
    public bool? WmiBatteryChargeLimitPresent { get; set; }
}

public class AppSettings
{
    public bool StartMinimized { get; set; } = false;
    public bool OsdEnabled { get; set; } = false;
    public string SelectedChartMetric { get; set; } = "CPU_TEMP";
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    // Normal (restored) window size; null = default 1060 x 730.
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
    public int OsdCorner { get; set; } = 0;
    // Index into the monitor list (System.Windows.Forms.Screen.AllScreens) the OSD is shown on.
    public int OsdMonitor { get; set; } = 0;
    public LightingConfig? KeyboardLighting { get; set; }
    // Last lighting that was actually on, so raising brightness or picking an effect after "off" can restore it.
    public LightingConfig? LastActiveLighting { get; set; }
    public bool IsBacklightOff { get; set; } = false;
    public string? SelectedRgbPreset { get; set; }

    [JsonPropertyName("last_operating_mode")]
    public OperatingMode? LastOperatingMode { get; set; }

    [JsonPropertyName("dc_power_mode")]
    public DcPowerProfilePreference DcPowerMode { get; set; } = DcPowerProfilePreference.Eco;

    [JsonPropertyName("auto_refresh_rate_switching")]
    public bool AutoRefreshRateSwitching { get; set; } = true;

    /// <summary>
    /// Drop to Eco when a fan stops reporting. Off by default: a tachometer that reads zero on a machine whose fans are
    /// simply below the sensor's floor would otherwise put the machine into Eco on every idle minute.
    /// </summary>
    [JsonPropertyName("fan_failure_protection")]
    public bool FanFailureProtection { get; set; } = false;

    [JsonPropertyName("default_atk_mouse_profile")]
    public int DefaultAtkMouseProfile { get; set; } = 1;

    [JsonPropertyName("atk_mouse_integration_enabled")]
    public bool AtkMouseIntegrationEnabled { get; set; } = false;

    [JsonPropertyName("auto_game_mode_enabled")]
    public bool AutoGameModeEnabled { get; set; } = true;

    /// <summary>
    /// The game session that was in effect when the app last wrote the settings file. A session that is still marked
    /// active on the next launch means the process died while a game was running, and the mode and fan the game
    /// overrode have to be put back.
    /// </summary>
    [JsonPropertyName("game_session_recovery")]
    public GameSessionRecoveryState? GameSessionRecovery { get; set; }

    /// <summary>
    /// The game rules automatic switching matches against: one entry per title, keyed by executable name.
    /// </summary>
    /// <remarks>
    /// This is the cross-platform half of the Windows <c>game_profiles.json</c>. It lives in the settings file because
    /// the Linux front end has no separate profile store, and because a rule that changes the machine's mode belongs
    /// next to the other preferences rather than in a second file the user has to find. An empty list is a valid
    /// answer — it means "never switch automatically" — so the defaults are only applied when the field is absent.
    /// </remarks>
    [JsonPropertyName("game_profiles")]
    public List<GameProfile> GameProfiles { get; set; } = GameProfileDefaults.Create();

    [JsonPropertyName("battery_watchdog_enabled")]
    public bool BatteryWatchdogEnabled { get; set; } = true;

    /// <summary>
    /// School mode: the most economical the machine can be on one charge. Persisted so it survives a restart and is
    /// re-applied at startup; <see cref="SchoolModeService"/> keeps the undo record next to the other data files.
    /// </summary>
    [JsonPropertyName("school_mode_enabled")]
    public bool SchoolModeEnabled { get; set; } = false;

    /// <summary>
    /// UI language code (see <see cref="Loc.Languages"/>). Null means the user has not chosen one: a fresh install
    /// follows the Windows display language, and a settings file written before v1.2 stays Turkish.
    /// </summary>
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    /// <summary>
    /// The chosen palette. Written as its member name and read as either that name or the number older builds
    /// wrote, so a config.json from any version still loads.
    /// </summary>
    [JsonPropertyName("theme")]
    [JsonConverter(typeof(AppThemeJsonConverter))]
    public AppTheme Theme { get; set; } = AppTheme.WarmCream;

    /// <summary>
    /// The live temperature strip on the modes page, hidden by the user. Persisted because it is a layout choice, not
    /// a per-session one: someone who collapsed it to reach the game profiles wants it collapsed next time too.
    /// </summary>
    [JsonPropertyName("mode_trace_collapsed")]
    public bool ModeTraceCollapsed { get; set; } = false;

    /// <summary>
    /// Width of the left navigation rail, in device-independent pixels. Persisted because it is a layout choice:
    /// someone who widened the rail to read a long translated label wants it wide next time too. Clamped to
    /// <see cref="MinSidebarWidth"/>..<see cref="MaxSidebarWidth"/> on read, so a hand-edited file cannot leave the
    /// window with no content area.
    /// </summary>
    [JsonPropertyName("sidebar_width")]
    public double SidebarWidth { get; set; } = DefaultSidebarWidth;

    public const double DefaultSidebarWidth = 215;
    public const double MinSidebarWidth = 175;
    public const double MaxSidebarWidth = 290;

    public static double ClampSidebarWidth(double value) =>
        double.IsNaN(value) || double.IsInfinity(value)
            ? DefaultSidebarWidth
            : Math.Clamp(value, MinSidebarWidth, MaxSidebarWidth);

    /// <summary>Ask GitHub for a newer release shortly after start and every six hours.</summary>
    [JsonPropertyName("auto_update_check")]
    public bool AutoUpdateCheck { get; set; } = true;

    /// <summary>
    /// Bring the window forward when the laptop's own Nitro key is pressed. On by default: the key exists to open
    /// NitroSense, and a user who installs NitroLite expects it to answer the same button.
    /// <para>
    /// There is no shortcut to configure. The key is a physical button with one meaning, so this is a switch and not a
    /// key binding, and a machine without the button never shows the switch at all.
    /// </para>
    /// </summary>
    [JsonPropertyName("nitro_key_enabled")]
    public bool NitroKeyEnabled { get; set; } = true;

    /// <summary>
    /// Take the Nitro key away from NitroSense: close it when it comes to the front and raise NitroLite instead.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A second switch rather than part of <see cref="NitroKeyEnabled"/> because the two answer different questions.
    /// That one says "the key opens NitroLite"; this one says "and NitroSense does not get to open as well". A user who
    /// wants NitroLite on the key but keeps NitroSense for its own panels can have exactly that.
    /// </para>
    /// <para>
    /// On by default, which is the behaviour the key has always had here: on a model whose button the Acer service
    /// stack owns, NitroSense coming forward is the only signal the press produces, so leaving it running would mean
    /// the key opens the wrong window.
    /// </para>
    /// </remarks>
    [JsonPropertyName("redirect_nitro_sense_enabled")]
    public bool RedirectNitroSenseEnabled { get; set; } = true;

    /// <summary>
    /// Emergency rollback switch for the hardware layer. On (the default) NitroLite may reach the hardware through a
    /// direct WMI backend when the Acer service is not running; off pins every read and write to the Acer service
    /// socket, which is the behaviour before the hardware abstraction existed.
    /// <para>
    /// A setting rather than a build flag on purpose: a machine where the new path misbehaves has to be able to get
    /// back to the old one without waiting for a release, and the person reporting the bug needs to be able to say
    /// which path they were on.
    /// </para>
    /// </summary>
    [JsonPropertyName("use_direct_wmi_backend")]
    public bool UseDirectWmiBackend { get; set; } = true;

    /// <summary>
    /// True once the window has offered the Task Scheduler entry after a four-zone colour write. The offer is made
    /// at the moment the user has just seen the feature work, and only once: a prompt that returns every session is
    /// the thing the setting exists to remove.
    /// </summary>
    [JsonPropertyName("elevated_auto_start_offered")]
    public bool ElevatedAutoStartOffered { get; set; }

    /// <summary>
    /// True once the first-run walkthrough has been finished or skipped. Skipping counts: the user has answered the
    /// question, and showing the walkthrough again on the next launch would be nagging rather than helping.
    /// </summary>
    [JsonPropertyName("onboarding_completed")]
    public bool OnboardingCompleted { get; set; }

    /// <summary>
    /// The last capability probe result, keyed by model code and BIOS version. Null means "not probed yet", which is
    /// the state a fresh install is in. A BIOS update changes the key, so a stale answer is never reused.
    /// </summary>
    [JsonPropertyName("hardware_capability_cache")]
    public HardwareCapabilityCache? HardwareCapabilityCache { get; set; }

    /// <summary>
    /// How many consecutive sessions a direct-WMI capability has failed its read-back, keyed by capability name.
    /// <para>
    /// A single failed session is usually the EC being busy during sleep or a resume, so it must not kill a working
    /// feature. Only when the count reaches three does the capability get switched off for good, and a successful
    /// read-back resets it to zero. The rescan button clears the whole map.
    /// </para>
    /// </summary>
    [JsonPropertyName("capability_session_failures")]
    public Dictionary<string, int> CapabilitySessionFailures { get; set; } = new();

    /// <summary>
    /// Capabilities the direct-WMI path gave up on after three consecutive failed sessions. Kept as a list rather
    /// than a set so the settings file stays readable and the order is stable.
    /// </summary>
    [JsonPropertyName("disabled_capabilities")]
    public List<string> DisabledCapabilities { get; set; } = new();

    // Custom fan speeds (%), used when the fan mode is "Özel". Kept within MinCustomFanPercent..100.
    [JsonPropertyName("custom_fan_cpu_percent")]
    public int CustomFanCpuPercent { get; set; } = 50;

    [JsonPropertyName("custom_fan_gpu_percent")]
    public int CustomFanGpuPercent { get; set; } = 50;

    /// <summary>Lowest custom fan speed offered: NitroSense's own default, low enough to be quiet at idle.</summary>
    public const int MinCustomFanPercent = 30;

    public static int ClampCustomFanPercent(int value) => Math.Clamp(value, MinCustomFanPercent, 100);

    // Software fan curve. When enabled it owns the fan mode: NitroLite keeps the fans on a custom speed and
    // moves that speed with the CPU temperature instead of leaving it where the user last put it.
    [JsonPropertyName("fan_curve_enabled")]
    public bool FanCurveEnabled { get; set; } = false;

    [JsonPropertyName("fan_curve_points")]
    public List<FanCurvePoint>? FanCurvePoints { get; set; } = FanCurve.DefaultPoints();

    [JsonPropertyName("fan_curve_hysteresis_c")]
    public int FanCurveHysteresisC { get; set; } = FanCurve.DefaultHysteresisC;

    [JsonPropertyName("fan_curve_min_dwell_seconds")]
    public int FanCurveMinDwellSeconds { get; set; } = FanCurve.DefaultMinDwellSeconds;

    /// <summary>The saved curve, cleaned up and ready to apply.</summary>
    public FanCurve BuildFanCurve() => new(FanCurvePoints, FanCurveHysteresisC, FanCurveMinDwellSeconds);

    /// <summary>
    /// When a fan has stopped while its die is hot, drop to the lowest power mode instead of only warning. On by
    /// default: a machine that is cooking itself is not the moment to ask the user to pick a setting.
    /// </summary>
    [JsonPropertyName("fan_stall_protection_enabled")]
    public bool FanStallProtectionEnabled { get; set; } = true;

    /// <summary>
    /// Brings every value that came out of a settings file back into the range the app can act on.
    /// System.Text.Json accepts any number for an enum, so a hand-edited or half-written file can carry
    /// (OperatingMode)99 or a fan speed of 400 without failing to parse. Nothing here invents a preference:
    /// an unusable value falls back to the same default a fresh install would use.
    /// <para>
    /// <see cref="SelectedChartMetric"/> is deliberately left alone: it is a display preference, the graph already
    /// falls back to CPU temperature for a metric it does not know, and rewriting it would silently discard what
    /// the user chose.
    /// </para>
    /// </summary>
    public void Normalize()
    {
        // An unusable theme name falls back to following Windows rather than to a palette the user never chose.
        if (!Enum.IsDefined(Theme)) Theme = AppTheme.System;
        if (LastOperatingMode.HasValue && !Enum.IsDefined(LastOperatingMode.Value)) LastOperatingMode = null;
        if (!Enum.IsDefined(DcPowerMode)) DcPowerMode = DcPowerProfilePreference.Eco;

        OsdCorner = ((OsdCorner % 4) + 4) % 4;
        if (OsdMonitor < 0) OsdMonitor = 0;
        DefaultAtkMouseProfile = Math.Clamp(DefaultAtkMouseProfile, 0, 4);
        CustomFanCpuPercent = ClampCustomFanPercent(CustomFanCpuPercent);
        CustomFanGpuPercent = ClampCustomFanPercent(CustomFanGpuPercent);

        // A curve with no usable anchor would leave the feature on with nothing to follow: fall back to the
        // default shape instead of silently running the fans at whatever the last write happened to be.
        FanCurvePoints = FanCurve.Sanitize(FanCurvePoints);
        FanCurveHysteresisC = Math.Clamp(FanCurveHysteresisC, 0, 15);
        FanCurveMinDwellSeconds = Math.Clamp(FanCurveMinDwellSeconds, 0, 120);

        if (Language != null && !Loc.IsSupported(Language)) Language = null;

        // A game rule with no executable can never match, and one with a broken curve would leave the fans under
        // software control with nothing to follow. Both are dropped or repaired here rather than at detection time,
        // where the cost would be paid on every scan.
        GameProfiles ??= new List<GameProfile>();
        GameProfiles.RemoveAll(p => p == null || string.IsNullOrWhiteSpace(p.ExeName));
        foreach (var profile in GameProfiles)
        {
            profile.ExeName = profile.ExeName.Trim();
            if (string.IsNullOrWhiteSpace(profile.DisplayName)) profile.DisplayName = profile.ExeName;
            if (!Enum.IsDefined(profile.TargetMode)) profile.TargetMode = OperatingMode.Default;
            if (!Enum.IsDefined(profile.TargetFanMode)) profile.TargetFanMode = FanMode.Auto;
            if (!Enum.IsDefined(profile.TargetCpuBoost)) profile.TargetCpuBoost = CpuBoostMode.Enabled;
            if (profile.TargetFanCurvePoints != null) profile.TargetFanCurvePoints = FanCurve.Sanitize(profile.TargetFanCurvePoints);
        }

        // The lighting block is the one part of the file that is not a number or an enum: it carries strings and a
        // list. A null Effect or a null LEDs list used to reach cfg.Effect.Equals(...) and cfg.LEDs.ToArray() in the
        // startup refresh, and the exception aborted every step after it, leaving the window half-built on every
        // launch. Null stays null: no lighting block means "read the keyboard's own state".
        KeyboardLighting = LightingPolicy.Sanitize(KeyboardLighting);
        LastActiveLighting = LightingPolicy.Sanitize(LastActiveLighting);
    }
}

public class GameSessionRecoveryState
{
    public bool IsActive { get; set; }
    public bool RestoreMode { get; set; } = true;
    public bool RestoreFan { get; set; } = true;
    public OperatingMode SavedMode { get; set; } = OperatingMode.Default;
    public CpuBoostMode SavedBoost { get; set; } = CpuBoostMode.Aggressive;
    public CpuBoostMode? SavedAcBoost { get; set; }
    public CpuBoostMode? SavedDcBoost { get; set; }
    public FanMode SavedFan { get; set; } = FanMode.Auto;
    public int SavedMouseProfile { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
