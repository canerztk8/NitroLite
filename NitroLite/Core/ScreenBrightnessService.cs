using System.Management;

namespace NitroLite.Core;

/// <summary>
/// Reads and writes the built-in panel brightness through WMI. External monitors do not answer
/// <c>WmiMonitorBrightness</c>, so a desktop-only setup simply reports null and nothing is changed.
/// </summary>
public sealed class ScreenBrightnessService : IScreenBrightnessService
{
    public int? GetBrightness()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            using var results = searcher.Get();
            foreach (ManagementObject row in results)
            {
                using (row)
                {
                    var value = row["CurrentBrightness"];
                    if (value != null) return Convert.ToInt32(value);
                }
            }
        }
        catch { }
        return null;
    }

    public bool SetBrightness(int percent)
    {
        try
        {
            byte target = (byte)Math.Clamp(percent, 0, 100);
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
            using var results = searcher.Get();
            foreach (ManagementObject row in results)
            {
                using (row)
                {
                    using var inParams = row.GetMethodParameters("WmiSetBrightness");
                    inParams["Timeout"] = 1u;
                    inParams["Brightness"] = target;
                    row.InvokeMethod("WmiSetBrightness", inParams, null);
                    return true;
                }
            }
        }
        catch { }
        return false;
    }
}

/// <summary>Test double: remembers what was set and reports a fixed starting value.</summary>
public sealed class TestScreenBrightnessService : IScreenBrightnessService
{
    public int Current { get; set; } = 80;
    public bool Supported { get; set; } = true;
    public int SetCalls { get; private set; }

    public int? GetBrightness() => Supported ? Current : null;

    public bool SetBrightness(int percent)
    {
        if (!Supported) return false;
        SetCalls++;
        Current = Math.Clamp(percent, 0, 100);
        return true;
    }
}
