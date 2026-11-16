namespace NitroLite.Core.Hardware.Wmi;

/// <summary>
/// The sensor ids <c>GetGamingSysInfo</c> (method 5) accepts, as the firmware numbers them.
/// </summary>
/// <remarks>
/// These are the firmware's own enum, the same one the Linux <c>acer-wmi</c> driver defines as
/// <c>acer_wmi_predator_v4_sensor_id</c>, so they are the same on every model that answers the call at all. They
/// live in NitroLite.Core rather than next to the WMI encoder because the model table (<c>profiles.json</c>) is
/// keyed by them, and the table has to be readable on a platform that has no WMI.
/// </remarks>
public static class AcerSensorIds
{
    /// <summary>Sensor id for the CPU temperature in <c>GetGamingSysInfo</c>.</summary>
    public const byte CpuTemperature = 0x01;

    /// <summary>Sensor id for the CPU fan tachometer.</summary>
    public const byte CpuFanSpeed = 0x02;

    /// <summary>Sensor id for the board / system temperature.</summary>
    public const byte SystemTemperature = 0x03;

    /// <summary>Sensor id for the GPU fan tachometer.</summary>
    public const byte GpuFanSpeed = 0x06;

    /// <summary>Sensor id for the GPU temperature.</summary>
    public const byte GpuTemperature = 0x0A;
}
