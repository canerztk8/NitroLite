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
    /// <summary>Every hwmon device that answered, in directory order.</summary>
    public static IReadOnlyList<LinuxHwmonDevice> Read(ISysfsFileSystem fs, string root = LinuxPaths.HwmonRoot)
    {
        var devices = new List<LinuxHwmonDevice>();
        Read(fs, devices, root);
        return devices;
    }

    /// <summary>
    /// Fills a caller-owned list instead of returning a new one. The telemetry loop reads hwmon once a second for the
    /// life of the process, so the list it fills is kept on the controller and cleared here rather than allocated
    /// again on every tick.
    /// </summary>
    public static void Read(ISysfsFileSystem fs, List<LinuxHwmonDevice> into, string root = LinuxPaths.HwmonRoot)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(into);

        into.Clear();
        foreach (var directory in fs.EnumerateDirectories(root))
        {
            var name = fs.ReadAllText(LinuxPaths.Combine(directory, "name"));
            if (string.IsNullOrWhiteSpace(name)) continue;

            var temperatures = ReadIndexed(fs, directory, "temp", millidegrees: true);
            var fans = ReadIndexed(fs, directory, "fan", millidegrees: false);
            if (temperatures.Length == 0 && fans.Length == 0) continue;

            into.Add(new LinuxHwmonDevice(directory, name.Trim(), temperatures, fans));
        }
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

    /// <summary>
    /// A hand-written loop rather than <c>FirstOrDefault</c> with a predicate: the predicate would capture
    /// <paramref name="name"/> and allocate a closure on every call, and this runs three times per telemetry tick.
    /// </summary>
    private static LinuxHwmonDevice? Find(IReadOnlyList<LinuxHwmonDevice> devices, string name)
    {
        for (var i = 0; i < devices.Count; i++)
        {
            if (devices[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return devices[i];
        }

        return null;
    }

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
    /// <remarks>
    /// The file name is parsed by hand rather than matched with a <see cref="Regex"/>. A regex allocates a
    /// <see cref="Match"/> per file per tick, and the grammar here is two fixed affixes around a run of digits — the
    /// kind of thing a span comparison answers without allocating anything at all.
    /// </remarks>
    private static int[] ReadIndexed(ISysfsFileSystem fs, string directory, string prefix, bool millidegrees)
    {
        // Allocated on the first match rather than up front: a CPU die sensor has temperatures and no fan, so half the
        // calls this makes on a real machine find nothing and would have rented a list to hold nothing.
        List<(int Index, int Value)>? found = null;

        foreach (var file in fs.EnumerateFiles(directory))
        {
            if (!TryParseIndexedName(file, prefix, out var index)) continue;

            var raw = fs.ReadAllText(LinuxPaths.Combine(directory, file));
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (!long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) continue;

            var scaled = millidegrees ? (int)(value / 1000) : (int)value;
            (found ??= new List<(int Index, int Value)>()).Add((index, scaled));
        }

        if (found == null) return Array.Empty<int>();

        // Sorted in place and copied once. The LINQ form this replaces built three objects — the ordering enumerable,
        // the projection enumerable and the array — for the same answer.
        found.Sort(static (a, b) => a.Index.CompareTo(b.Index));

        var values = new int[found.Count];
        for (var i = 0; i < found.Count; i++) values[i] = found[i].Value;
        return values;
    }

    /// <summary>
    /// True when <paramref name="file"/> is <c>{prefix}{digits}_input</c>. Written against spans so the check costs
    /// nothing: no substring, no regex, no match object.
    /// </summary>
    private static bool TryParseIndexedName(string file, string prefix, out int index)
    {
        index = 0;

        const string Suffix = "_input";
        if (file.Length <= prefix.Length + Suffix.Length) return false;
        if (!file.StartsWith(prefix, StringComparison.Ordinal)) return false;
        if (!file.EndsWith(Suffix, StringComparison.Ordinal)) return false;

        var digits = file.AsSpan(prefix.Length, file.Length - prefix.Length - Suffix.Length);
        foreach (var c in digits)
        {
            if (c is < '0' or > '9') return false;
        }

        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out index);
    }
}
