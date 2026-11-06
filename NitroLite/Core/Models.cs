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

public class FpsTelemetryInfo
{
    public bool IsAvailable { get; set; }
    public double Fps { get; set; }
    public double FrametimeMs { get; set; }
    public double Low01Fps { get; set; }
    public double Low1Fps { get; set; }
    public string GameTitle { get; set; } = string.Empty;
}

public class AppSettings
{
    public bool StartMinimized { get; set; } = false;
    public bool OsdEnabled { get; set; } = false;
    public string SelectedChartMetric { get; set; } = "CPU_TEMP";
    public bool AutoRamCleanerEnabled { get; set; } = false;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public int OsdCorner { get; set; } = 0;
    public LightingConfig? KeyboardLighting { get; set; }
    public bool IsBacklightOff { get; set; } = false;
    public string? SelectedRgbPreset { get; set; }

    [JsonPropertyName("dc_power_mode")]
    public DcPowerProfilePreference DcPowerMode { get; set; } = DcPowerProfilePreference.Eco;

    [JsonPropertyName("auto_refresh_rate_switching")]
    public bool AutoRefreshRateSwitching { get; set; } = true;

    [JsonPropertyName("default_atk_mouse_profile")]
    public int DefaultAtkMouseProfile { get; set; } = 1;

    [JsonPropertyName("atk_mouse_integration_enabled")]
    public bool AtkMouseIntegrationEnabled { get; set; } = false;
}

public class GameSessionRecoveryState
{
    public bool IsActive { get; set; }
    public OperatingMode SavedMode { get; set; } = OperatingMode.Default;
    public CpuBoostMode SavedBoost { get; set; } = CpuBoostMode.Aggressive;
    public FanMode SavedFan { get; set; } = FanMode.Auto;
    public int SavedMouseProfile { get; set; } = 1;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
