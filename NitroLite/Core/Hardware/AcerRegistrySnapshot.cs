using Microsoft.Win32;

namespace NitroLite.Core.Hardware;

/// <summary>
/// The capability values the Acer agent writes into the registry, read without elevation.
/// <para>
/// This is the cheapest layer of the capability probe: <c>HKLM\SOFTWARE\OEM\AcerAgentService</c> is world-readable,
/// so the MUX bitmask and the keyboard zone count can be answered before anything is asked of WMI or of the service
/// socket. On an AN16-41 the key carries <c>dgpu_mode_capability = 7</c> (Optimus, discrete and automatic all
/// supported) and <c>KeyBoardArea = 4</c> (four zones).
/// </para>
/// <para>
/// Every value is nullable. A machine with no Acer software installed has no key at all, and "the key is missing"
/// must not be written down as "the feature is missing": the service may still answer, and the WMI schema may still
/// exist.
/// </para>
/// </summary>
public sealed record AcerRegistrySnapshot
{
    /// <summary>Key the Acer agent maintains. Absent on a clean Windows install.</summary>
    public const string KeyPath = @"SOFTWARE\OEM\AcerAgentService";

    /// <summary>True when the key itself exists, whatever values it carries.</summary>
    public bool KeyPresent { get; init; }

    /// <summary>Bitmask of supported GPU modes: bit 0 Optimus, bit 1 discrete, bit 2 automatic. 7 on an AN16-41.</summary>
    public int? DgpuModeCapability { get; init; }

    /// <summary>Independent keyboard colour zones the agent reports. 4 on a four-zone keyboard.</summary>
    public int? KeyboardArea { get; init; }

    /// <summary>1 when the agent reports a discrete-GPU-only mode.</summary>
    public int? DiscreteGpuSupport { get; init; }

    /// <summary>1 when the GPU can be overclocked through the agent.</summary>
    public int? GpuOverclockSupport { get; init; }

    /// <summary>1 when the panel supports LCD overdrive.</summary>
    public int? LcdOverdriveSupport { get; init; }

    /// <summary>Nothing was readable: no key, or no rights to it.</summary>
    public static AcerRegistrySnapshot Empty { get; } = new();

    /// <summary>
    /// True when <paramref name="mode"/> is set in a capability bitmask. The bit position is the enum value, which
    /// is what NitroSense does and what the existing GPU-mode picker already assumes.
    /// </summary>
    public static bool IsGpuModeSupported(int capabilityBitmask, GpuOperatingMode mode) =>
        (capabilityBitmask & (1 << (int)mode)) != 0;

    /// <summary>The modes the bitmask advertises, in enum order. Empty when the value is missing or zero.</summary>
    public IReadOnlyList<GpuOperatingMode> SupportedGpuModes
    {
        get
        {
            if (DgpuModeCapability is not int mask || mask <= 0) return Array.Empty<GpuOperatingMode>();
            return Enum.GetValues<GpuOperatingMode>().Where(m => IsGpuModeSupported(mask, m)).ToList();
        }
    }

    /// <summary>
    /// Reads the key. Never throws: a missing key, a denied key and a value of the wrong type all come back as
    /// null. Both registry views are tried, because the agent is 32-bit on some models and 64-bit on others.
    /// </summary>
    public static AcerRegistrySnapshot Read()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            var snapshot = ReadView(view);
            if (snapshot.KeyPresent) return snapshot;
        }

        return Empty;
    }

    private static AcerRegistrySnapshot ReadView(RegistryView view)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = baseKey.OpenSubKey(KeyPath, writable: false);
            if (key == null) return Empty;

            return new AcerRegistrySnapshot
            {
                KeyPresent = true,
                DgpuModeCapability = ReadDword(key, @"AdvanceSettings", "dgpu_mode_capability"),
                DiscreteGpuSupport = ReadDword(key, @"AdvanceSettings", "discrete_gpu_support"),
                LcdOverdriveSupport = ReadDword(key, @"AdvanceSettings", "LCD_Overdrive_support"),
                KeyboardArea = ReadDword(key, @"LightSetting", "KeyBoardArea"),
                GpuOverclockSupport = ReadDword(key, @"Overclock", "GPU_OC_Support")
            };
        }
        catch
        {
            return Empty;
        }
    }

    /// <summary>
    /// Reads one DWORD from a subkey. A value stored as a string is parsed as well: the agent has shipped both
    /// shapes, and refusing the string form would report a working machine as having no capability.
    /// </summary>
    private static int? ReadDword(RegistryKey parent, string subKeyName, string valueName)
    {
        try
        {
            using var subKey = parent.OpenSubKey(subKeyName, writable: false);
            var raw = subKey?.GetValue(valueName);
            return raw switch
            {
                int number => number,
                long number => (int)number,
                string text when int.TryParse(text, out var parsed) => parsed,
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }
}
