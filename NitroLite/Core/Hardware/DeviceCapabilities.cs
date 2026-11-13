using System.Collections.Generic;
using System.Threading.Tasks;

namespace NitroLite.Core.Hardware;

/// <summary>
/// What the Acer service says this machine can do, gathered in one place so the hardware report and the UI read
/// the same answers.
/// <para>
/// Every member is nullable on purpose. Null means "not asked, or no answer", which is not the same as "the
/// machine does not have it": a failed query must never be written down as a missing feature, or the next person
/// to read the report draws the wrong conclusion about a model nobody here owns.
/// </para>
/// </summary>
public sealed record DeviceCapabilities
{
    /// <summary>Keyboard zones and effects, from GET_CONNECTED_DEVICE.</summary>
    public KeyboardCapability? Keyboard { get; init; }

    /// <summary>The operating modes the machine offers, from SUPPORT_OPERATING_MODE_CAPABILITY.</summary>
    public IReadOnlyList<OperatingMode>? OperatingModes { get; init; }

    /// <summary>The raw GPU mode capability number, from SUPPORT_GPU_MODE_CAPABILITY.</summary>
    public int? GpuModeCapability { get; init; }

    /// <summary>The fan mode the machine is in right now, from GET_UPDATED_DATA FAN_CONTROL.</summary>
    public FanMode? FanMode { get; init; }

    /// <summary>Whether the service reports a controllable backlight, from LIGHTING_CAPABILITY.</summary>
    public bool? LightingCapability { get; init; }

    /// <summary>
    /// Asks the service everything the report needs. Each query is independent and swallows its own failure, so a
    /// machine that answers three of five still produces a useful report.
    /// </summary>
    public static async Task<DeviceCapabilities> ProbeAsync(AcerAgentClient client) => new()
    {
        Keyboard = await client.GetKeyboardCapabilityAsync(),
        OperatingModes = await client.GetOperatingModesAsync(),
        GpuModeCapability = await client.GetGpuModeCapabilityAsync(),
        FanMode = await client.GetFanModeAsync(),
        LightingCapability = await client.GetLightingCapabilityAsync()
    };
}
