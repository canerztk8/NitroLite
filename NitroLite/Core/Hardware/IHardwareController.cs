namespace NitroLite.Core.Hardware;

/// <summary>
/// What the hardware layer on this machine can actually do, as opposed to what the model name suggests.
/// <para>
/// The Nitro line is not uniform: a Nitro V 15 has no four-zone keyboard, some Predators have no MUX switch, and a
/// machine with no Acer software installed has no service socket at all. Every flag here is answered by the backend
/// that is actually in use, so the UI can hide a control instead of offering one that cannot work.
/// </para>
/// </summary>
public sealed record HardwareCapabilities
{
    /// <summary>Name of the backend that produced this snapshot, for the hardware report and the log.</summary>
    public string Backend { get; init; } = "";

    /// <summary>True when the Acer service socket answered, so reads and writes have a working path.</summary>
    public bool ServiceAvailable { get; init; }

    /// <summary>Fan mode and fan speed can be written.</summary>
    public bool FanControl { get; init; }

    /// <summary>Operating modes (Silent/Balanced/Performance/Turbo) can be written.</summary>
    public bool OperatingModeControl { get; init; }

    /// <summary>A GPU mode switch exists and reports a capability value.</summary>
    public bool MuxSwitch { get; init; }

    /// <summary>The battery charge limit can be read and written.</summary>
    public bool BatteryChargeLimit { get; init; }

    /// <summary>The keyboard advertises at least one lighting zone.</summary>
    public bool KeyboardLighting { get; init; }

    /// <summary>The keyboard backlight timeout can be written.</summary>
    public bool BacklightTimeout { get; init; }

    /// <summary>Nothing is known yet: the backend has not been asked.</summary>
    public static HardwareCapabilities Unknown { get; } = new();
}

/// <summary>
/// The single contract every hardware backend implements: telemetry, fan, operating mode, MUX, battery limit and
/// keyboard lighting.
/// <para>
/// This exists so the application logic stops talking to <see cref="AcerAgentClient"/> and
/// <see cref="CareCenterClient"/> directly. Today the only implementation is
/// <see cref="AgentHardwareController"/>, which wraps those two clients without changing their behaviour; a direct
/// WMI backend and a Linux sysfs backend can be added behind the same interface without touching the pages, the
/// thermal guards or the school-mode undo record.
/// </para>
/// <para>
/// Reads are free to run concurrently. Writes are not: two backends writing the same fan register interleave into a
/// state neither of them asked for, so every implementation must serialise its writes.
/// </para>
/// </summary>
public interface IHardwareController : IDisposable
{
    /// <summary>Short backend name, e.g. "AASSvc" or "WMI". Shown in the hardware report.</summary>
    string BackendName { get; }

    /// <summary>True when the backend currently has a usable path to the hardware.</summary>
    bool IsAvailable { get; }

    /// <summary>Opens the connection if it is not open yet. Never throws: a failure is reported as <c>false</c>.</summary>
    Task<bool> EnsureConnectedAsync();

    /// <summary>
    /// Asks the hardware what it supports. Cached by the caller, not here: a probe can cost a round trip per
    /// capability and the answer does not change while the machine is running.
    /// </summary>
    Task<HardwareCapabilities> GetCapabilitiesAsync();

    /// <summary>One telemetry sample: temperatures, fan RPM, load, clocks. Null when the read failed.</summary>
    Task<MonitorData?> GetMonitorDataAsync();

    /// <summary>The mode the hardware is in now, or null when it did not answer.</summary>
    Task<OperatingMode?> GetOperatingModeAsync();

    /// <summary>Writes the operating mode. False when the hardware refused or did not confirm.</summary>
    Task<bool> SetOperatingModeAsync(OperatingMode mode);

    /// <summary>The GPU mode the hardware is in now, or null when it did not answer.</summary>
    Task<GpuOperatingMode?> GetGpuModeAsync();

    /// <summary>Writes the GPU mode. False when the hardware refused or did not confirm.</summary>
    Task<bool> SetGpuModeAsync(GpuOperatingMode mode);

    /// <summary>The raw MUX capability value, or null when the machine has no switch.</summary>
    Task<int?> GetGpuModeCapabilityAsync();

    /// <summary>The fan mode the hardware is in now, or null when it did not answer.</summary>
    Task<FanMode?> GetFanModeAsync();

    /// <summary>
    /// Writes the fan mode. <paramref name="cpuPercent"/> and <paramref name="gpuPercent"/> are only used by
    /// <see cref="FanMode.Custom"/>; pass a negative value to keep the backend's own default.
    /// </summary>
    Task<bool> SetFanModeAsync(FanMode mode, int cpuPercent = -1, int gpuPercent = -1);

    /// <summary>The lighting configuration the keyboard reports, or null when it did not answer.</summary>
    Task<LightingConfig?> GetLightingConfigAsync();

    /// <summary>Writes effect, brightness, speed, direction and up to four zone colours.</summary>
    Task<bool> SetLightingAsync(string effect, int brightness, int speed, int direction, string primaryColor, string[]? zoneColors);

    /// <summary>Turns the backlight off without forgetting the last active configuration.</summary>
    Task<bool> TurnOffLightingAsync();

    /// <summary>True when the backlight turns itself off after 30 seconds, null when the keyboard did not answer.</summary>
    Task<bool?> GetBacklightTimeoutAsync();

    /// <summary>Enables or disables the 30-second backlight timeout.</summary>
    Task<bool> SetBacklightTimeoutAsync(bool enable30s);

    /// <summary>The battery charge mode the firmware reports, or null when it did not answer.</summary>
    Task<BatteryChargeMode?> GetBatteryChargeModeAsync();

    /// <summary>Writes the battery charge mode. False when the firmware refused.</summary>
    Task<bool> SetBatteryChargeModeAsync(BatteryChargeMode mode);
}
