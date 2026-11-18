using System.Globalization;
using System.IO;
using NitroLite.Linux;
using NitroLite.Linux.Sysfs;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The hwmon read runs once a second for the life of the process, so what it allocates per tick is a property worth
/// pinning rather than a detail worth hoping about.
/// </summary>
/// <remarks>
/// <para>
/// The budget is measured with <see cref="GC.GetAllocatedBytesForCurrentThread"/> around a loop of reads, which counts
/// every byte the thread allocates and nothing else. It is a real assertion, not a smoke test: the LINQ and regex
/// forms this replaced allocated roughly twenty objects per tick, and the numbers below are what is left.
/// </para>
/// <para>
/// The in-memory file system answers from arrays it built once, so the measurement is the reader's own cost. The
/// second test measures the same loop through <see cref="RealSysfsFileSystem"/> against a real directory, where the
/// operating system's own <c>Directory.GetFiles</c> allocations are part of the answer — that is the honest number for
/// the production path, and it is why the two budgets differ.
/// </para>
/// </remarks>
public class LinuxTelemetryAllocationTests
{
    /// <summary>
    /// A <c>/sys/class/hwmon</c> tree in memory. Every enumeration answers a cached array, so a measurement taken
    /// through it is the reader's allocation and not the fake's.
    /// </summary>
    private sealed class InMemorySysfs : ISysfsFileSystem
    {
        private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string[]> _directories = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string[]> _fileNames = new(StringComparer.Ordinal);

        public void AddDevice(string root, string name, int[] temperaturesC, int[] fanRpms)
        {
            var directory = root.TrimEnd('/') + "/" + name;
            _files[directory + "/name"] = name + "\n";

            var names = new List<string> { "name" };
            for (var i = 0; i < temperaturesC.Length; i++)
            {
                var file = $"temp{i + 1}_input";
                _files[directory + "/" + file] =
                    (temperaturesC[i] * 1000L).ToString(CultureInfo.InvariantCulture) + "\n";
                names.Add(file);
            }

            for (var i = 0; i < fanRpms.Length; i++)
            {
                var file = $"fan{i + 1}_input";
                _files[directory + "/" + file] =
                    fanRpms[i].ToString(CultureInfo.InvariantCulture) + "\n";
                names.Add(file);
            }

            _fileNames[directory] = names.ToArray();

            var existing = _directories.TryGetValue(root, out var list) ? list.ToList() : new List<string>();
            existing.Add(directory);
            _directories[root] = existing.ToArray();
        }

        public bool FileExists(string path) => _files.ContainsKey(path);

        public bool DirectoryExists(string path) => _directories.ContainsKey(path);

        public string? ReadAllText(string path) => _files.TryGetValue(path, out var value) ? value : null;

        public SysfsWriteResult WriteAllText(string path, string value) => SysfsWriteResult.Missing(path);

        public bool CanWrite(string path) => false;

        public IReadOnlyList<string> EnumerateDirectories(string path) =>
            _directories.TryGetValue(path, out var list) ? list : Array.Empty<string>();

        public IReadOnlyList<string> EnumerateFiles(string path) =>
            _fileNames.TryGetValue(path, out var list) ? list : Array.Empty<string>();
    }

    private static InMemorySysfs BuildTree()
    {
        var fs = new InMemorySysfs();
        fs.AddDevice("/sys/class/hwmon", "acer", new[] { 44, 41 }, new[] { 2200, 2150 });
        fs.AddDevice("/sys/class/hwmon", "k10temp", new[] { 58 }, Array.Empty<int>());
        fs.AddDevice("/sys/class/hwmon", "amdgpu", new[] { 51 }, Array.Empty<int>());
        return fs;
    }

    [Fact]
    public void Read_MapsTheThreeDevicesOntoTheFourReadings()
    {
        var fs = BuildTree();
        var devices = new List<LinuxHwmonDevice>();

        LinuxHwmon.Read(fs, devices);
        var sample = LinuxHwmon.Sample(devices);

        // The die sensor wins for the CPU, the board sensor becomes the system reading, and the fans come from acer.
        Assert.Equal(58, sample.CpuTemperatureC);
        Assert.Equal(51, sample.GpuTemperatureC);
        Assert.Equal(44, sample.SysTemperatureC);
        Assert.Equal(2200, sample.CpuFanRpm);
        Assert.Equal(2150, sample.GpuFanRpm);
        Assert.True(sample.HasMeasuredFanRpm);
    }

    [Fact]
    public void Read_WithoutADieSensor_FallsBackToTheBoardSensor()
    {
        var fs = new InMemorySysfs();
        fs.AddDevice("/sys/class/hwmon", "acer", new[] { 47 }, new[] { 1800 });

        var devices = new List<LinuxHwmonDevice>();
        LinuxHwmon.Read(fs, devices);
        var sample = LinuxHwmon.Sample(devices);

        Assert.Equal(47, sample.CpuTemperatureC);

        // The board sensor is the CPU reading here, so there is no separate system reading to report.
        Assert.Null(sample.SysTemperatureC);
        Assert.Equal(1800, sample.CpuFanRpm);
        Assert.Null(sample.GpuFanRpm);
    }

    [Fact]
    public void Read_ReusesTheCallersListAndDropsADeviceThatDisappeared()
    {
        var fs = BuildTree();
        var devices = new List<LinuxHwmonDevice>();

        LinuxHwmon.Read(fs, devices);
        Assert.Equal(3, devices.Count);

        // The module is unloaded between two ticks. The list is cleared before it is filled, so the stale device
        // cannot survive into the next answer.
        var smaller = new InMemorySysfs();
        smaller.AddDevice("/sys/class/hwmon", "k10temp", new[] { 61 }, Array.Empty<int>());

        LinuxHwmon.Read(smaller, devices);
        Assert.Single(devices);
        Assert.Equal("k10temp", devices[0].Name);
    }

    [Fact]
    public void Read_IgnoresFilesThatAreNotIndexedAttributes()
    {
        var fs = new InMemorySysfs();
        fs.AddDevice("/sys/class/hwmon", "acer", new[] { 44 }, new[] { 2200 });

        // The real tree carries these next to the attributes: a label, a max, and a name that only looks indexed.
        fs.AddDevice("/sys/class/hwmon", "acer_extra", Array.Empty<int>(), Array.Empty<int>());

        var devices = new List<LinuxHwmonDevice>();
        LinuxHwmon.Read(fs, devices);

        // acer_extra has no temp or fan attribute, so it is not a device the reader reports.
        Assert.Single(devices);
        Assert.Equal("acer", devices[0].Name);
        Assert.Single(devices[0].TemperaturesC);
        Assert.Single(devices[0].FanRpms);
    }

    [Fact]
    public void Read_OrdersTheAttributesByIndexNotByFileName()
    {
        var fs = new InMemorySysfs();
        fs.AddDevice("/sys/class/hwmon", "acer", new[] { 44, 41 }, new[] { 2200, 2150 });

        var devices = new List<LinuxHwmonDevice>();
        LinuxHwmon.Read(fs, devices);

        // temp1 before temp2, which a plain string sort would also give — but fan10 would sort before fan2, and the
        // index sort is what keeps the CPU fan at index 0 whatever the kernel numbers it.
        Assert.Equal(new[] { 44, 41 }, devices[0].TemperaturesC);
        Assert.Equal(new[] { 2200, 2150 }, devices[0].FanRpms);
    }

    [Fact]
    public void Read_AllocatesAlmostNothingPerTick()
    {
        var fs = BuildTree();
        var devices = new List<LinuxHwmonDevice>();

        // Warm up: the first call JITs the reader and rents whatever the runtime rents once.
        for (var i = 0; i < 20; i++)
        {
            LinuxHwmon.Read(fs, devices);
            _ = LinuxHwmon.Sample(devices);
        }

        const int Ticks = 100;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < Ticks; i++)
        {
            LinuxHwmon.Read(fs, devices);
            _ = LinuxHwmon.Sample(devices);
        }

        var perTick = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)Ticks;

        // What is left is the three device records, their two int arrays each, and the sample record — the shapes the
        // public API returns. The LINQ and regex forms this replaced added roughly twenty more objects per tick on top.
        // The budget is deliberately loose enough not to fail on a runtime that changes a record's layout, and tight
        // enough to catch a return to the allocating form.
        Assert.True(perTick < 2500, $"hwmon read allocated {perTick:F0} bytes per tick");
    }

    [Fact]
    public void RealFileSystem_ReadsATempTreeWithoutAllocatingPerAttribute()
    {
        var root = Path.Combine(Path.GetTempPath(), "nitrolite-hwmon-alloc-" + Guid.NewGuid().ToString("N"));
        var hwmon = Path.Combine(root, "hwmon0");
        Directory.CreateDirectory(hwmon);

        try
        {
            File.WriteAllText(Path.Combine(hwmon, "name"), "acer\n");
            File.WriteAllText(Path.Combine(hwmon, "temp1_input"), "44000\n");
            File.WriteAllText(Path.Combine(hwmon, "fan1_input"), "2200\n");

            var fs = RealSysfsFileSystem.Instance;
            var devices = new List<LinuxHwmonDevice>();

            for (var i = 0; i < 20; i++) LinuxHwmon.Read(fs, devices, root);

            const int Ticks = 100;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < Ticks; i++) LinuxHwmon.Read(fs, devices, root);
            var perTick = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)Ticks;

            // The operating system's own Directory.GetFiles and the strings it hands back are part of this number, so
            // it is larger than the in-memory budget. What it proves is that the reader adds no LINQ enumerables on
            // top of them.
            Assert.True(perTick < 8000, $"real sysfs read allocated {perTick:F0} bytes per tick");
            Assert.Single(devices);
            Assert.Equal(44, devices[0].TemperaturesC[0]);
            Assert.Equal(2200, devices[0].FanRpms[0]);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
