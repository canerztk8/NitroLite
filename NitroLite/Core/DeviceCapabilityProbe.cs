using NitroLite.Core.Hardware;

namespace NitroLite.Core;

/// <summary>
/// Asks the Acer service everything the hardware report needs.
/// </summary>
/// <remarks>
/// This lives in the Windows project because it is the only place <see cref="AcerAgentClient"/> exists. The record
/// it fills in is in NitroLite.Core, so the report and the UI read the same shape on every platform.
/// </remarks>
public static class DeviceCapabilityProbe
{
    /// <summary>
    /// Each query is independent and swallows its own failure, so a machine that answers three of five still
    /// produces a useful report.
    /// </summary>
    public static async Task<DeviceCapabilities> ProbeAsync(AcerAgentClient client) => new DeviceCapabilities
    {
        Keyboard = await client.GetKeyboardCapabilityAsync(),
        OperatingModes = await client.GetOperatingModesAsync(),
        GpuModeCapability = await client.GetGpuModeCapabilityAsync(),
        FanMode = await client.GetFanModeAsync(),
        LightingCapability = await client.GetLightingCapabilityAsync()
    };
}
