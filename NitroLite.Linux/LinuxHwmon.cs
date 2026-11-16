using System.Globalization;
using System.Text.RegularExpressions;
using NitroLite.Core.Hardware;
using NitroLite.Linux.Sysfs;

namespace NitroLite.Linux;

/// <summary>One hwmon device: its kernel name and the readings it exposes.</summary>
public sealed record LinuxHwmonDevice(
    string Path,
    string Name,
    IReadOnlyList<int> TemperaturesC,
    IReadOnlyList<int> FanRpms);

/// <summary>
/// One telemetry sample, already mapped onto the four readings NitroLite shows.
/// </summary>
/// <remarks>
/// <see cref="HasMeasuredFanRpm"/> is the Linux counterpart of <c>MonitorData.IsFanRpmMeasured</c>: a real
/// <c>fan*_input</c> is a tachometer, so the fan-stall watchdog may judge it. When no fan node exists the value is
/// null rather than zero, because "no tachometer" and "the fan has stopped" are different facts.
/// </remarks>
public sealed record LinuxSensorSample(
    int? CpuTemperatureC,
    int? GpuTemperatureC,
    int? SysTemperatureC,
    int? CpuFanRpm,
    int? GpuFanRpm)
{
    public bool HasMeasuredFanRpm => CpuFanRpm.HasValue || GpuFanRpm.HasValue;

    public static LinuxSensorSample Empty { get; } = new(null, null, null, null, null);
}

/// <summary>
/// Reads temperatures and fan tachometers from <c>/sys/class/hwmon</c>.
/// </summary>
/// <remarks>
/// hwmon is a class, not a device: the kernel puts every sensor driver under <c>/sys/class/hwmon/hwmonN</c> and the
/// only way to tell them apart is the <c>name</c> file. The three that matter here are <c>acer</c> (the laptop's own
/// fans and board sensor), <c>k10temp</c> / <c>coretemp</c> (the CPU die) and <c>amdgpu</c> (the discrete GPU).
/// <para>
/// Values are filtered through <see cref="SensorPlausibility"/> before they are returned. A hwmon attribute can
/// answer a sentinel when the embedded controller is busy, and a 65535 °C reading must never reach the UI or the
/// fan-stall watchdog.
/// </para>
/// </remarks>
public static class LinuxHwmon
{
    private static readonly Regex TempFile = new(@"^temp(?<index>\d+)_input$", RegexOptions.Compiled);
    private static readonly Regex FanFile = new(@"^fan(?<index>\d+)_input$", RegexOptions.Compiled);

    /// <summary>Every hwmon device that answered, in directory order.</summary>
    public static IReadOnlyList<LinuxHwmonDevice> Read(ISysfsFileSystem fs, string root = LinuxPaths.HwmonRoot)
    {
        ArgumentNullException.ThrowIfNull(fs);

        var devices = new List<LinuxHwmonDevice>();
        foreach (var directory in fs.EnumerateDirectories(root))
        {
            var name = fs.ReadAllText(LinuxPaths.Combine(directory, "name"));
            if (string.IsNullOrWhiteSpace(name)) continue;

            var temperatures = ReadIndexed(fs, directory, TempFile, millidegrees: true);
            var fans = ReadIndexed(fs, directory, FanFile, millidegrees: false);
            if (temperatures.Count == 0 && fans.Count == 0) continue;

            devices.Add(new LinuxHwmonDevice(directory, name.Trim(), temperatures, fans));
        }

        return devices;
    }

    /// <summary>
    /// Maps the devices onto the four readings the UI shows. CPU temperature prefers the die sensor and falls back
    /// to the board sensor; the GPU is only ever <c>amdgpu</c>, because a machine without one has no GPU reading and
    /// inventing a number from the board sensor would be worse than showing nothing.
    /// </summary>
    public static LinuxSensorSample Sample(IReadOnlyList<LinuxHwmonDevice> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        if (devices.Count == 0) return LinuxSensorSample.Empty;

        var acer = Find(devices, "acer");
        var cpuDie = Find(devices, "k10temp") ?? Find(devices, "coretemp");
        var gpu = Find(devices, "amdgpu");

        var cpuTemp = First(cpuDie) ?? First(acer);
        var gpuTemp = First(gpu);
        // The board sensor is only a separate reading when the CPU temperature came from somewhere else.
        var sysTemp = cpuDie != null ? First(acer) : null;

        return new LinuxSensorSample(
            PlausibleTemperature(cpuTemp),
            PlausibleTemperature(gpuTemp),
            PlausibleTemperature(sysTemp),
            PlausibleFan(acer, 0),
            PlausibleFan(acer, 1));
    }

    private static LinuxHwmonDevice? Find(IReadOnlyList<LinuxHwmonDevice> devices, string name) =>
        devices.FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static int? First(LinuxHwmonDevice? device) =>
        device != null && device.TemperaturesC.Count > 0 ? device.TemperaturesC[0] : null;

    private static int? PlausibleTemperature(int? value) =>
        value.HasValue && SensorPlausibility.IsPlausibleTemperature(value.Value) ? value : null;

    private static int? PlausibleFan(LinuxHwmonDevice? device, int index)
    {
        if (device == null || device.FanRpms.Count <= index) return null;
        var value = device.FanRpms[index];
        return SensorPlausibility.IsPlausibleFanRpm(value) ? value : null;
    }

    /// <summary>
    /// Reads <c>temp*_input</c> or <c>fan*_input</c> in index order. Temperatures are millidegrees in sysfs and are
    /// converted here, once, so no caller has to remember the factor.
    /// </summary>
    private static IReadOnlyList<int> ReadIndexed(
        ISysfsFileSystem fs,
        string directory,
        Regex pattern,
        bool millidegrees)
    {
        var found = new List<(int Index, int Value)>();

        foreach (var file in fs.EnumerateFiles(directory))
        {
            var match = pattern.Match(file);
            if (!match.Success) continue;
            if (!int.TryParse(match.Groups["index"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            {
                continue;
            }

            var raw = fs.ReadAllText(LinuxPaths.Combine(directory, file));
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (!long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) continue;

            var scaled = millidegrees ? (int)(value / 1000) : (int)value;
            found.Add((index, scaled));
        }

        return found.OrderBy(f => f.Index).Select(f => f.Value).ToArray();
    }
}
