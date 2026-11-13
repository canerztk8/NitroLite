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

    public DateTime Timestamp { get; set; } = DateTime.Now;
}

public class RgbColor
{
    public byte R { get; set; }
    public byte G { get; set; }
    public byte B { get; set; }

    public RgbColor(byte r, byte g, byte b)
    {
        R = r;
        G = g;
        B = b;
    }

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    public static RgbColor FromHex(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 6)
        {
            byte r = Convert.ToByte(hex.Substring(0, 2), 16);
            byte g = Convert.ToByte(hex.Substring(2, 2), 16);
            byte b = Convert.ToByte(hex.Substring(4, 2), 16);
            return new RgbColor(r, g, b);
        }
        return new RgbColor(0, 200, 255); // Default cyan
    }
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
    public double? TemperatureCelsius { get; set; }
    public double DischargeRateMilliwatts { get; set; }
    public double RemainingCapacityMWh { get; set; }
    public double SmoothedDischargeRateWatts { get; set; }
    // True when TemperatureCelsius comes from the charge/discharge wattage model instead of a sensor.
    public bool IsTemperatureEstimated { get; set; }
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
    /// False when the recorded executable is gone (game uninstalled or moved). A profile without a path is
    /// treated as installed: it was added by hand and there is nothing to check.
    /// </summary>
    [JsonIgnore]
    public bool IsInstalled => string.IsNullOrWhiteSpace(InstallPath) || File.Exists(InstallPath);
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

public class AppSettings
{
    public bool StartMinimized { get; set; } = false;
    public bool OsdEnabled { get; set; } = false;
    public string SelectedChartMetric { get; set; } = "CPU_TEMP";
    public bool AutoRamCleanerEnabled { get; set; } = false;
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

    [JsonPropertyName("default_atk_mouse_profile")]
    public int DefaultAtkMouseProfile { get; set; } = 1;

    [JsonPropertyName("atk_mouse_integration_enabled")]
    public bool AtkMouseIntegrationEnabled { get; set; } = false;

    [JsonPropertyName("auto_game_mode_enabled")]
    public bool AutoGameModeEnabled { get; set; } = true;

    [JsonPropertyName("battery_watchdog_enabled")]
    public bool BatteryWatchdogEnabled { get; set; } = true;

    // Custom fan speeds (%), used when the fan mode is "Özel". Kept within MinCustomFanPercent..100.
    [JsonPropertyName("custom_fan_cpu_percent")]
    public int CustomFanCpuPercent { get; set; } = 50;

    [JsonPropertyName("custom_fan_gpu_percent")]
    public int CustomFanGpuPercent { get; set; } = 50;

    /// <summary>Lowest custom fan speed offered: NitroSense's own default, low enough to be quiet at idle.</summary>
    public const int MinCustomFanPercent = 30;

    public static int ClampCustomFanPercent(int value) => Math.Clamp(value, MinCustomFanPercent, 100);
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
