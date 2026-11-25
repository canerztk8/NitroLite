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
    NordFrost = 6,

    /// <summary>Neutral daylight white with a blue accent: the light palette for a bright room.</summary>
    CleanWhite = 7,

    /// <summary>Deep green-black with an emerald accent.</summary>
    Forest = 8,

    /// <summary>Near-black with a cyan accent, for a machine that lives under a keyboard light.</summary>
    NeonStreet = 9,

    /// <summary>Pure black, pure white ink and a yellow accent: the palette for a screen read at a glance.</summary>
    HighContrast = 10,

    /// <summary>Deep violet ground with a hot-pink accent: the sunset palette.</summary>
    ViceSunset = 11,

    /// <summary>Near-black with a signal-yellow accent and a cyan secondary: the chrome palette.</summary>
    CyberChrome = 12,

    /// <summary>Rose-tinted paper with a plum accent: the only light palette that is not neutral.</summary>
    RoseQuartz = 13,

    /// <summary>Deep navy with a brass accent: the only cool ground carrying a warm accent.</summary>
    Navy = 14
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
    /// Where each reading in this sample came from, keyed by a short metric name (<c>cpu-temp</c>, <c>cpu-clock</c>,
    /// <c>vram</c>, …). Diagnostics only: the hardware report prints it, so a reading of zero can be traced to the
    /// layer that failed instead of being guessed at. Never serialized — it is not part of the payload Acer's service
    /// speaks.
    /// </summary>
    [JsonIgnore]
    public IDictionary<string, string> Provenance { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

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
    /// True when the sensor ids behind <see cref="CpuTemperature"/> were confirmed on this machine — by the profile
    /// table naming the model, or by the live probe answering plausibly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reading is still shown when this is false: an unlisted model's temperatures are useful, and hiding them
    /// would leave the page blank on every machine the table does not name. What the flag decides is whether the
    /// <em>fan curve</em> may act on the number.
    /// </para>
    /// <para>
    /// It must not. The curve follows <see cref="CpuTemperature"/>, so a machine whose sensor id 1 answers the 55 °C
    /// system sensor instead of the die asks for roughly a third of full speed while the die is at 95 °C — and the
    /// 95 °C full-speed floor never fires, because the curve never sees 95 °C. A wrong reading on a page is a wrong
    /// number; a wrong reading in the curve is a hot machine with quiet fans.
    /// </para>
    /// <para>
    /// Defaults to true so every construction that predates the flag keeps its behaviour: the flag is set to false
    /// only by the one backend that knows it is guessing.
    /// </para>
    /// </remarks>
    [JsonIgnore]
    public bool IsTemperatureMeasured { get; set; } = true;

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
    public double? TemperatureC { get; set; }
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
    /// <remarks>
    /// A UNC path is refused without being probed. <c>File.Exists(@"\\host\share\game.exe")</c> opens an SMB session,
    /// which hands this machine's NTLM hash to whoever answered and blocks the caller until the connection times out;
    /// the path comes out of a profile file the user can edit, so it is not something to trust with either.
    /// </remarks>
    [JsonIgnore]
    public bool IsInstalled =>
        string.IsNullOrWhiteSpace(InstallPath) || (IsLocalPath(InstallPath) && File.Exists(InstallPath));

    /// <summary>True when <paramref name="path"/> is a local path rather than a UNC share.</summary>
    public static bool IsLocalPath(string path) =>
        !path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith("//", StringComparison.Ordinal);
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

    /// <summary>
    /// The 30-second keyboard timeout the user last chose, or null when they never touched the switch. The firmware
    /// keeps its own copy and the read that mirrors it can fail — the elevated helper is not connected yet when the
    /// window opens — so the stored choice is what the page puts back on the keyboard when nothing answers.
    /// </summary>
    public bool? BacklightTimeoutEnabled { get; set; }

    /// <summary>
    /// Whether the keyboard colour follows the processor's temperature instead of the colour the page holds.
    /// </summary>
    /// <remarks>
    /// Persisted, because it is a preference and not a moment: someone who set the keyboard to follow the temperature
    /// wants it to still do that after a reboot. The colour it writes is deliberately not persisted — the static
    /// configuration stays in <see cref="KeyboardLighting"/>, so turning the mode off puts the user's own colour back
    /// rather than the last shade of orange the processor happened to be.
    /// </remarks>
    [JsonPropertyName("reactive_lighting_enabled")]
    public bool ReactiveLightingEnabled { get; set; } = false;

    /// <summary>
    /// Which temperature ramp the reactive mode follows: the <see cref="ReactiveLightingProfile.Id"/> of a built-in
    /// profile, or <see cref="ReactiveLightingProfiles.CustomId"/> for the user's own stops. Null means the first
    /// built-in profile.
    /// </summary>
    [JsonPropertyName("reactive_lighting_profile")]
    public string? ReactiveLightingProfile { get; set; }

    /// <summary>
    /// The user's own ramp, used when <see cref="ReactiveLightingProfile"/> is
    /// <see cref="ReactiveLightingProfiles.CustomId"/>. Sanitised on load; null when there is no usable custom ramp.
    /// </summary>
    [JsonPropertyName("reactive_lighting_custom_stops")]
    public List<ReactiveLightingStop>? ReactiveLightingCustomStops { get; set; }

    /// <summary>
    /// Which ramp the graphics chip's half of the keyboard follows. Null means "the same ramp as the processor", which
    /// is what a settings file from before the two halves could differ means, and what a user who never opened the
    /// target picker expects.
    /// </summary>
    /// <remarks>
    /// A second profile id rather than a boolean: the two halves can each follow a built-in ramp or their own stops, so
    /// a machine with both sensors selected can show a warm processor beside a cool graphics chip instead of one colour
    /// in two shades — which is what the single ramp produced and what made the split look broken.
    /// </remarks>
    [JsonPropertyName("reactive_lighting_profile_gpu")]
    public string? ReactiveLightingProfileGpu { get; set; }

    /// <summary>
    /// The graphics chip's own ramp, used when <see cref="ReactiveLightingProfileGpu"/> is
    /// <see cref="ReactiveLightingProfiles.CustomId"/>. Null means the graphics half inherits the processor's ramp
    /// rather than painting something the user never chose.
    /// </summary>
    [JsonPropertyName("reactive_lighting_custom_stops_gpu")]
    public List<ReactiveLightingStop>? ReactiveLightingCustomStopsGpu { get; set; }

    /// <summary>
    /// The colour the processor's half paints when its profile is <see cref="ReactiveLightingProfiles.SolidId"/>:
    /// one colour whatever the temperature is. Null means the shipped cold end.
    /// </summary>
    /// <remarks>
    /// A colour per half rather than one for both, because the reason to pick a solid colour is usually the split: a
    /// blue left half and an orange right half say which sensor is which without a ramp moving all evening. The pair is
    /// stored beside the ramp ids, not instead of them, so switching back to a ramp loses nothing.
    /// </remarks>
    [JsonPropertyName("reactive_lighting_solid_cpu")]
    public string? ReactiveLightingSolidCpu { get; set; }

    /// <summary>The colour the graphics half paints when its profile is the solid entry. Null means the shipped hot end.</summary>
    [JsonPropertyName("reactive_lighting_solid_gpu")]
    public string? ReactiveLightingSolidGpu { get; set; }

    /// <summary>
    /// True when the two halves are mixed across the four zones instead of being painted as two blocks: zone 1 exactly
    /// the left colour, zone 4 exactly the right, and the two middle zones between them.
    /// </summary>
    [JsonPropertyName("reactive_lighting_blend")]
    public bool ReactiveLightingBlend { get; set; }

    /// <summary>
    /// Whether the keyboard shows a chosen colour while the charger is plugged in. Persisted, because it is a
    /// preference and not a moment: someone who set a charging colour wants it back after a reboot.
    /// </summary>
    /// <remarks>
    /// The colour itself lives beside it, and neither of them is the colour the page holds: the page's own lighting is
    /// kept in <see cref="KeyboardLighting"/> and comes back when the adapter is pulled out, so a charging colour can
    /// never become the colour the user's keyboard is left on.
    /// </remarks>
    [JsonPropertyName("charging_color_enabled")]
    public bool ChargingColorEnabled { get; set; } = false;

    /// <summary>The colour painted while the adapter is in, as <c>#RRGGBB</c>. Normalised on load.</summary>
    [JsonPropertyName("charging_color")]
    public string? ChargingColor { get; set; }

    /// <summary>
    /// Which sensors the temperature colour follows. CPU is on unless the user says otherwise, because that is the
    /// reading every machine has; GPU is opt-in, because a machine without one answers nothing and a mode that painted
    /// a colour for a sensor it does not have would look alive while saying nothing.
    /// </summary>
    /// <remarks>
    /// Both on is the split layout: the left half of the keyboard follows the processor and the right half the graphics
    /// chip. This is a preference rather than a moment — it is the way the user reads their own machine — so it is
    /// stored, and the switches on the page read it back.
    /// </remarks>
    [JsonPropertyName("reactive_lighting_use_cpu")]
    public bool ReactiveLightingUseCpu { get; set; } = true;

    /// <summary>Whether the graphics chip's temperature drives the right half of the keyboard.</summary>
    [JsonPropertyName("reactive_lighting_use_gpu")]
    public bool ReactiveLightingUseGpu { get; set; }

    /// <summary>
    /// The brightness the temperature colour paints at. Its own slider, independent of the page's: the colour the user
    /// chose and the colour the sensor chooses are two different things and each keeps its own level.
    /// </summary>
    [JsonPropertyName("reactive_lighting_brightness")]
    public int ReactiveLightingBrightness { get; set; } = 6;

    /// <summary>The brightness the charging colour paints at, on its own slider and for the same reason.</summary>
    [JsonPropertyName("charging_color_brightness")]
    public int ChargingColorBrightness { get; set; } = 6;

    [JsonPropertyName("last_operating_mode")]
    public OperatingMode? LastOperatingMode { get; set; }
    public GpuOperatingMode? RequestedGpuMode { get; set; }
    public DateTimeOffset? GpuModeRequestedAt { get; set; }

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

    /// <summary>
    /// True once <see cref="MigrateGameBoostDefaults"/> has run on this file. False (absent) in every file written
    /// before 1.3.6; a fresh install is marked as migrated by the loader, because its defaults were chosen on purpose.
    /// </summary>
    [JsonPropertyName("game_boost_defaults_migrated")]
    public bool GameBoostDefaultsMigrated { get; set; }

    /// <summary>How many rules the migration changed in this process; the loader writes the file back when it is not zero.</summary>
    [JsonIgnore]
    internal int GameBoostMigrationChanges { get; private set; }

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
    /// The chosen UI typeface, as a <see cref="UiFontCatalog"/> id. An id this build no longer ships resolves to the
    /// default rather than to a missing font.
    /// </summary>
    /// <remarks>
    /// Initialised to the default rather than left null, so a settings file written by a clean launch names the face it
    /// is in. Null is still understood — a file from an older build has no key at all — and resolves to the same face,
    /// which is what makes "the window starts in Quicksand" a property of the settings and not of a fallback that a
    /// hand-edited file could quietly avoid.
    /// </remarks>
    [JsonPropertyName("ui_font")]
    public string? UiFont { get; set; } = UiFontCatalog.DefaultId;

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

        // A face this build no longer ships would leave the window asking for a font that is not there. Null is the
        // default, so the unknown id is dropped rather than kept and resolved on every launch.
        if (UiFont != null && !UiFontCatalog.All.Any(f => string.Equals(f.Id, UiFont, StringComparison.OrdinalIgnoreCase)))
        {
            UiFont = null;
        }

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

        MigrateGameBoostDefaults();

        // The lighting block is the one part of the file that is not a number or an enum: it carries strings and a
        // list. A null Effect or a null LEDs list used to reach cfg.Effect.Equals(...) and cfg.LEDs.ToArray() in the
        // startup refresh, and the exception aborted every step after it, leaving the window half-built on every
        // launch. Null stays null: no lighting block means "read the keyboard's own state".
        KeyboardLighting = LightingPolicy.MigrateLegacyReds(LightingPolicy.Sanitize(KeyboardLighting));
        LastActiveLighting = LightingPolicy.MigrateLegacyReds(LightingPolicy.Sanitize(LastActiveLighting));

        // The reactive profile is an id from a fixed list plus an optional ramp. A hand-edited file can carry a name
        // no build knows, and a ramp with fewer than two stops cannot describe a gradient — both fall back to the
        // first built-in profile rather than being trusted. A file written by a build that stored the display name is
        // understood too: the name resolves to its profile, and the id is what gets written back.
        ReactiveLightingProfile = ReactiveLightingProfiles.CanonicalId(ReactiveLightingProfile);
        if (ReactiveLightingCustomStops != null)
        {
            var cleanStops = ReactiveLightingProfiles.SanitizeStops(ReactiveLightingCustomStops);
            ReactiveLightingCustomStops = cleanStops.Count >= 2 ? cleanStops : null;
        }

        // The graphics half carries the same rules. Null stays null: it means "follow the processor's ramp", which is a
        // real answer and the one a file written before the two halves could differ gives.
        ReactiveLightingProfileGpu = ReactiveLightingProfiles.CanonicalId(ReactiveLightingProfileGpu);
        if (ReactiveLightingCustomStopsGpu != null)
        {
            var cleanGpuStops = ReactiveLightingProfiles.SanitizeStops(ReactiveLightingCustomStopsGpu);
            ReactiveLightingCustomStopsGpu = cleanGpuStops.Count >= 2 ? cleanGpuStops : null;
        }

        // The two automatic colours paint at levels of their own, and a file a user can edit can hold a zero or a
        // negative in either. Zero is the firmware's "stay dark", which would make the mode look broken rather than
        // dim, so the stored level is brought into the range the sliders offer before anything reads it.
        ReactiveLightingBrightness = ChargingLighting.NormalizeBrightness(ReactiveLightingBrightness);
        ChargingColorBrightness = ChargingLighting.NormalizeBrightness(ChargingColorBrightness);

        // A solid colour goes straight to the keyboard, and a hand-edited file can carry "mavi" there: each half's
        // colour is normalised on load, and a blank one becomes the shipped end for that half rather than black.
        ReactiveLightingSolidCpu = NormalizeOptionalColor(ReactiveLightingSolidCpu, ReactiveLighting.ColdColor);
        ReactiveLightingSolidGpu = NormalizeOptionalColor(ReactiveLightingSolidGpu, ReactiveLighting.HotColor);

        // Both capability maps come out of a file a user can edit and a crash can truncate. A null here reached a
        // dictionary lookup and a list add on the first capability read, so a hand-edited file took the whole page
        // down instead of costing one feature.
        CapabilitySessionFailures ??= new Dictionary<string, int>();
        DisabledCapabilities ??= new List<string>();

        // A negative count is not a count: it would never reach the three that switches a capability off, so the
        // entry is dropped rather than kept as a number that means nothing. A blank name cannot be looked up either.
        foreach (var key in CapabilitySessionFailures
                     .Where(p => p.Value < 0 || string.IsNullOrWhiteSpace(p.Key))
                     .Select(p => p.Key)
                     .ToList())
        {
            CapabilitySessionFailures.Remove(key);
        }

        DisabledCapabilities.RemoveAll(name => string.IsNullOrWhiteSpace(name));

        // The recovery record is what a crash mid-game leaves behind, and it is replayed straight to the firmware on
        // the next launch. Its enums come out of the same file, so they get the same range check as everything else:
        // a record holding (OperatingMode)99 would otherwise be the one hardware write that is a guess.
        if (GameSessionRecovery is { } recovery)
        {
            if (!Enum.IsDefined(recovery.SavedMode)) recovery.SavedMode = OperatingMode.Default;
            if (!Enum.IsDefined(recovery.SavedFan)) recovery.SavedFan = FanMode.Auto;
            if (!Enum.IsDefined(recovery.SavedBoost)) recovery.SavedBoost = CpuBoostMode.Aggressive;
            // The boost values are the exact ones read from the power plan, and Windows documents PerfBoostMode as
            // 0..6: 3, 5 and 6 have no enum member but are real values an OEM plan can hold, and dropping them would
            // leave the game's value in place after a crash. The same range CpuPowerService accepts.
            if (recovery.SavedAcBoost is { } ac && !IsStoredBoostValue(ac)) recovery.SavedAcBoost = null;
            if (recovery.SavedDcBoost is { } dc && !IsStoredBoostValue(dc)) recovery.SavedDcBoost = null;
            if (recovery.SavedMouseProfile is < 0 or > 4) recovery.SavedMouseProfile = 0;
            if (recovery.Timestamp == default) recovery.Timestamp = DateTime.UtcNow;
        }
    }

    /// <summary>A PerfBoostMode value as Windows documents it (0..6), named by the enum or not.</summary>
    private static bool IsStoredBoostValue(CpuBoostMode mode) => (int)mode is >= 0 and <= 6;

    /// <summary>
    /// One-time repair of game rules whose boost nobody chose. Before 1.3.6 the hand-add path saved every new rule
    /// with the model's default of Disabled, and no build applied a rule's boost at all, so no user has ever seen that
    /// value take effect. Now that it is applied, starting such a game would switch boost off. Each stored rule that
    /// says Disabled where the library recommends something else gets the library's value; a rule for which the
    /// library itself says Disabled (Balatro) keeps it. Runs once per file: a Disabled the user sets afterwards stays.
    /// </summary>
    /// <returns>How many rules were changed. Zero when the migration had already run.</returns>
    public int MigrateGameBoostDefaults()
    {
        if (GameBoostDefaultsMigrated) return 0;

        int changed = 0;
        foreach (var profile in GameProfiles ?? new List<GameProfile>())
        {
            if (profile == null || profile.TargetCpuBoost != CpuBoostMode.Disabled) continue;

            var recommended = GameLibraryRules.DetermineOptimalCpuBoost(profile.ExeName);
            if (recommended == CpuBoostMode.Disabled) continue;

            profile.TargetCpuBoost = recommended;
            changed++;
        }

        GameBoostDefaultsMigrated = true;
        GameBoostMigrationChanges = changed;
        AppLog.Info("Game rule boost defaults migrated", $"{changed} rule(s) moved from an unchosen Disabled to the recommended boost");
        return changed;
    }

    /// <summary>
    /// A colour setting that is allowed to be null, brought to <c>#RRGGBB</c>: a value the parser cannot read becomes
    /// <paramref name="fallback"/>, and an empty one stays null so "not chosen" is not silently stored as a colour.
    /// </summary>
    private static string? NormalizeOptionalColor(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? null : LightingPolicy.NormalizeHex(value, fallback);
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
