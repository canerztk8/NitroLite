using System.Globalization;
using System.Text;
using NitroLite.Core;
using NitroLite.Linux.Sysfs;

namespace NitroLite.Linux;

/// <summary>
/// Writes a capture from inside the app, in the same format as <c>tools/linux-capture/nitrolite-capture.sh</c>, so a
/// user can send their machine with one button instead of running a script.
/// </summary>
/// <remarks>
/// <para>
/// It only reads. The same privacy rules as the script apply: the DMI serial and UUID files are never read, a HID
/// uevent is cut down to its driver, ids and name (its <c>HID_UNIQ</c> is a device serial on many dongles), the input
/// device list loses its <c>Uniq=</c> and <c>Phys=</c> lines, and the finished text goes through
/// <see cref="Redaction.Scrub"/>, which replaces the home folder and the machine name.
/// </para>
/// <para>
/// The output parses with <see cref="LinuxCapture.Parse"/>, which is what the tests check, so a capture made by the
/// button replays in <c>--linux-capture</c> exactly like one made by the script.
/// </para>
/// </remarks>
public sealed class LinuxCaptureWriter
{
    private static readonly string[] DmiFields =
    {
        "sys_vendor", "product_name", "product_family", "product_version", "board_name", "board_vendor",
        "bios_version", "bios_vendor", "bios_date"
    };

    private static readonly string[] BatteryFields =
    {
        "type", "online", "status", "capacity", "technology", "manufacturer", "model_name", "cycle_count",
        "voltage_now", "current_now", "power_now", "energy_now", "energy_full", "energy_full_design", "charge_now",
        "charge_full", "charge_full_design", "time_to_empty_now", "charge_control_end_threshold",
        "charge_control_start_threshold", "charge_behaviour", "charge_types"
    };

    private static readonly string[] PowerProfileService =
    {
        "org.freedesktop.UPower.PowerProfiles", "/org/freedesktop/UPower/PowerProfiles", "org.freedesktop.UPower.PowerProfiles"
    };

    private static readonly string[] LegacyPowerProfileService =
    {
        "net.hadess.PowerProfiles", "/net/hadess/PowerProfiles", "net.hadess.PowerProfiles"
    };

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);

    private readonly ISysfsFileSystem _fs;
    private readonly ICommandRunner _runner;
    private readonly Func<string, byte[]?> _readBytes;
    private readonly StringBuilder _out = new();

    public LinuxCaptureWriter(ISysfsFileSystem? fs = null, ICommandRunner? runner = null, Func<string, byte[]?>? readBytes = null)
    {
        _fs = fs ?? RealSysfsFileSystem.Instance;
        _runner = runner ?? ProcessCommandRunner.Instance;
        _readBytes = readBytes ?? DefaultReadBytes;
    }

    /// <summary>Records this machine and returns the capture text.</summary>
    public string Capture(DateTime? nowUtc = null)
    {
        _out.Clear();
        Line(LinuxCapture.Header);
        Line("@@ meta captured " + (nowUtc ?? DateTime.UtcNow).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
        Line("@@ meta note Written by NitroLite " + Redaction.Scrub(AppVersion()) + " (Settings, Send Linux info).");

        foreach (var field in DmiFields) File("/sys/class/dmi/id/" + field);
        File("/etc/os-release");
        File("/proc/sys/kernel/osrelease");
        File("/proc/cpuinfo");
        File("/proc/meminfo");

        File(LinuxPaths.PlatformProfile);
        File(LinuxPaths.PlatformProfileChoices);
        foreach (var dir in _fs.EnumerateDirectories("/sys/class/platform-profile"))
        {
            File(LinuxPaths.Combine(dir, "name"));
            File(LinuxPaths.Combine(dir, "profile"));
            File(LinuxPaths.Combine(dir, "choices"));
        }

        Dir(LinuxPaths.AcerWmiModule);
        foreach (var name in _fs.EnumerateFiles("/sys/module/acer_wmi/parameters"))
        {
            File("/sys/module/acer_wmi/parameters/" + name);
        }

        Attributes(LinuxPaths.AcpiPlatformRoot);
        CaptureSense();

        foreach (var dir in _fs.EnumerateDirectories(LinuxPaths.HwmonRoot))
        {
            Dir(dir);
            File(LinuxPaths.Combine(dir, "name"));
            foreach (var name in _fs.EnumerateFiles(dir))
            {
                if (IsSensorFile(name)) File(LinuxPaths.Combine(dir, name));
            }
        }

        foreach (var dir in _fs.EnumerateDirectories(LinuxPaths.PowerSupplyRoot))
        {
            Dir(dir);
            foreach (var field in BatteryFields) File(LinuxPaths.Combine(dir, field));
        }

        foreach (var dir in _fs.EnumerateDirectories(LinuxPaths.BacklightRoot))
        {
            Dir(dir);
            File(LinuxPaths.Combine(dir, LinuxPaths.Brightness));
            File(LinuxPaths.Combine(dir, LinuxPaths.MaxBrightness));
        }

        File(LinuxPaths.CpuFreqBoost);
        File(LinuxPaths.IntelNoTurbo);
        File(LinuxPaths.AmdPstateStatus);
        File("/sys/devices/system/cpu/cpu0/cpufreq/scaling_cur_freq");

        foreach (var dir in _fs.EnumerateDirectories(LinuxHidrawLocator.HidrawClassRoot))
        {
            Dir(dir);
            HidUevent(LinuxPaths.Combine(dir, "device/uevent"));
            Hex(LinuxPaths.Combine(dir, "device/report_descriptor"));
            Node("/dev/" + Path.GetFileName(dir));
        }

        foreach (var dir in _fs.EnumerateDirectories(LinuxNvidiaGpu.PciRoot))
        {
            var pciClass = _fs.ReadAllText(LinuxPaths.Combine(dir, "class"))?.Trim();
            if (pciClass == null || !pciClass.StartsWith("0x03", StringComparison.Ordinal)) continue;

            Dir(dir);
            File(LinuxPaths.Combine(dir, "vendor"));
            File(LinuxPaths.Combine(dir, "device"));
            File(LinuxPaths.Combine(dir, "class"));
            File(LinuxPaths.Combine(dir, "power/runtime_status"));
        }

        InputDevices();

        foreach (var endpoint in new[] { PowerProfileService, LegacyPowerProfileService })
        {
            Command("busctl", "--system", "--json=short", "get-property", endpoint[0], endpoint[1], endpoint[2], "ActiveProfile");
            Command("busctl", "--system", "--json=short", "get-property", endpoint[0], endpoint[1], endpoint[2], "Profiles");
            Command("busctl", "--system", "get-property", endpoint[0], endpoint[1], endpoint[2], "ActiveProfile");
        }

        Command("powerprofilesctl", "get");
        Command("nvidia-smi",
            "--query-gpu=name,temperature.gpu,utilization.gpu,memory.used,memory.total,clocks.gr,power.draw",
            "--format=csv,noheader,nounits");
        Which("pkexec");
        Which("sh");

        Info("lsusb", "lsusb");
        Info("lsmod", "lsmod", filter: line =>
            line.Contains("acer", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("linuwu", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("wmi", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("amd_pmf", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("nvidia", StringComparison.OrdinalIgnoreCase));
        Info("secure-boot", "mokutil", "--sb-state");

        Line("@@ end");
        return Redaction.Scrub(_out.ToString());
    }

    /// <summary>Writes the capture into <paramref name="directory"/> and returns the file's path.</summary>
    public string WriteTo(string directory, DateTime? nowUtc = null)
    {
        Directory.CreateDirectory(directory);
        var stamp = (nowUtc ?? DateTime.UtcNow).ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var path = Path.Combine(directory, $"nitrolite-capture-{stamp}.txt");
        System.IO.File.WriteAllText(path, Capture(nowUtc), new UTF8Encoding(false));
        return path;
    }

    // ---------------------------------------------------------------- sections

    /// <summary>
    /// The sense and keyboard directories: Linuwu-Sense's under its module, acer-wmi-linuwu's on the platform device.
    /// The keyboard directory is captured beside the sense directory and below it, since builds have used both.
    /// </summary>
    private void CaptureSense()
    {
        if (_fs.DirectoryExists("/sys/module/linuwu_sense")) Dir("/sys/module/linuwu_sense");

        foreach (var root in new[] { LinuxPaths.LinuwuModuleRoot, LinuxPaths.AcpiPlatformRoot })
        {
            if (!_fs.DirectoryExists(root)) continue;
            if (root == LinuxPaths.LinuwuModuleRoot) Dir(root);

            foreach (var name in new[] { LinuxPaths.NitroSense, LinuxPaths.PredatorSense, LinuxPaths.FourZoneKeyboard })
            {
                var dir = LinuxPaths.Combine(root, name);
                if (!_fs.DirectoryExists(dir)) continue;

                Flat(dir);
                if (name != LinuxPaths.FourZoneKeyboard) Flat(LinuxPaths.Combine(dir, LinuxPaths.FourZoneKeyboard));
            }
        }
    }

    private void Flat(string dir)
    {
        if (!_fs.DirectoryExists(dir)) return;

        Dir(dir);
        foreach (var name in _fs.EnumerateFiles(dir)) File(LinuxPaths.Combine(dir, name));
    }

    /// <summary>The attributes of a platform device, without the links back into the device tree.</summary>
    private void Attributes(string dir)
    {
        if (!_fs.DirectoryExists(dir)) return;

        Dir(dir);
        foreach (var name in _fs.EnumerateFiles(dir))
        {
            if (name is "uevent" or "modalias") continue;
            File(LinuxPaths.Combine(dir, name));
        }
    }

    private static bool IsSensorFile(string name) =>
        (name.StartsWith("temp", StringComparison.Ordinal) || name.StartsWith("fan", StringComparison.Ordinal) ||
         name.StartsWith("pwm", StringComparison.Ordinal)) &&
        (name.EndsWith("_input", StringComparison.Ordinal) || name.EndsWith("_label", StringComparison.Ordinal) ||
         name.EndsWith("_enable", StringComparison.Ordinal) || IsBarePwm(name));

    private static bool IsBarePwm(string name) =>
        name.Length > 3 && name.StartsWith("pwm", StringComparison.Ordinal) && name[3..].All(char.IsAsciiDigit);

    private void HidUevent(string path)
    {
        var text = _fs.ReadAllText(path);
        if (text == null) return;

        Line("@@ file " + path);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("DRIVER=", StringComparison.Ordinal) ||
                line.StartsWith("HID_ID=", StringComparison.Ordinal) ||
                line.StartsWith("HID_NAME=", StringComparison.Ordinal))
            {
                Line("| " + line);
            }
        }
    }

    private void InputDevices()
    {
        var text = _fs.ReadAllText("/proc/bus/input/devices");
        if (text == null) return;

        Line("@@ info input-devices");
        foreach (var raw in text.Split('\n'))
        {
            if (raw.StartsWith("U: Uniq=", StringComparison.Ordinal) || raw.StartsWith("P: Phys=", StringComparison.Ordinal)) continue;
            Line("| " + raw);
        }
    }

    // ---------------------------------------------------------------- primitives

    private void File(string path)
    {
        var text = _fs.ReadAllText(path);
        if (text == null) return;

        Line("@@ file " + path);
        Body(text);
        if (!_fs.CanWrite(path)) Line("@@ ro " + path);
    }

    private void Node(string path)
    {
        if (!_fs.FileExists(path)) return;
        Line("@@ file " + path);
        if (!_fs.CanWrite(path)) Line("@@ ro " + path);
    }

    private void Dir(string path)
    {
        if (_fs.DirectoryExists(path)) Line("@@ dir " + path);
    }

    private void Hex(string path)
    {
        var bytes = _readBytes(path);
        if (bytes == null) return;
        Line("@@ hex " + path);
        Line("| " + Convert.ToHexString(bytes).ToLowerInvariant());
    }

    private void Which(string tool) =>
        Line($"@@ which {tool} {(_runner.Find(tool) != null ? "present" : "absent")}");

    private void Command(string tool, params string[] arguments)
    {
        var path = _runner.Find(tool);
        if (path == null)
        {
            Line($"@@ which {tool} absent");
            return;
        }

        Line($"@@ which {tool} present");
        var result = _runner.Run(path, arguments, CommandTimeout);
        Line("@@ cmd " + LinuxCapture.CommandKey(tool, arguments));
        Line("@@ exit " + result.ExitCode.ToString(CultureInfo.InvariantCulture));
        Body(result.Output);
    }

    private void Info(string label, string tool, params string[] arguments) => Info(label, tool, null, arguments);

    private void Info(string label, string tool, Func<string, bool>? filter, params string[] arguments)
    {
        var path = _runner.Find(tool);
        if (path == null) return;

        var result = _runner.Run(path, arguments, CommandTimeout);
        Line("@@ info " + label);
        var lines = result.Output.Split('\n');
        Body(string.Join("\n", filter == null ? lines : lines.Where(filter)));
    }

    private void Body(string text)
    {
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')) Line("| " + line);
    }

    private void Line(string text) => _out.Append(text).Append('\n');

    private static string AppVersion() =>
        typeof(LinuxCaptureWriter).Assembly.GetName().Version?.ToString(3) ?? "?";

    // Read to the real end: sysfs advertises a page for a binary attribute whatever it holds, and File.ReadAllBytes
    // throws when the stream ends before the advertised length.
    internal static readonly Func<string, byte[]?> DefaultReadBytes = Sysfs.SysfsBinary.Read;
}
