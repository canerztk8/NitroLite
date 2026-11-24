using System.Globalization;
using System.Text;
using NitroLite.Core;
using NitroLite.Linux.Sysfs;

namespace NitroLite.Linux;

/// <summary>
/// <c>NitroLite --linux-selftest</c>: checks, on the real machine, every path the Linux build depends on, and prints a
/// report a maintainer can read without the machine.
/// </summary>
/// <remarks>
/// <para>
/// Most of the Linux backend has only ever run against recorded trees. This is the one command that answers "does it
/// work here": whether the helper answers and polkit lets this session use it, whether each setting can be written,
/// whether the power-profile daemon, NVIDIA and logind are reachable, and what the kernel offers this model.
/// </para>
/// <para>
/// It changes nothing. The write check writes each setting's <em>current</em> value back to it, which the firmware
/// treats as a no-op, and only where that is safe: a fan's speed is never written back, because writing a speed can
/// take the fan out of automatic mode. Brightness is not written at all.
/// </para>
/// </remarks>
public sealed class LinuxSelfTest
{
    private readonly ISysfsFileSystem _fs;
    private readonly ICommandRunner _runner;
    private readonly LinuxHelperClient? _helper;
    private readonly IPowerProfiles _power;
    private readonly StringBuilder _out = new();
    private int _failures;

    public LinuxSelfTest(ISysfsFileSystem? fs = null, ICommandRunner? runner = null, LinuxHelperClient? helper = null,
        IPowerProfiles? power = null)
    {
        _fs = fs ?? RealSysfsFileSystem.Instance;
        _runner = runner ?? ProcessCommandRunner.Instance;
        _helper = helper;
        _power = power ?? NoPowerProfiles.Instance;
    }

    /// <summary>The real machine, with the real helper client and daemon.</summary>
    public static LinuxSelfTest ForThisMachine()
    {
        var helper = new LinuxHelperClient();
        return new LinuxSelfTest(RealSysfsFileSystem.Instance, ProcessCommandRunner.Instance,
            helper.IsInstalled ? helper : null, new PowerProfilesDaemon());
    }

    /// <summary>Failed checks in the last run. Zero means everything NitroLite needs answered.</summary>
    public int Failures => _failures;

    /// <summary>Runs every check and returns the report.</summary>
    public string Run(DateTime? nowUtc = null)
    {
        _out.Clear();
        _failures = 0;

        ISysfsFileSystem fs = _helper != null ? new HelperBackedFileSystem(_fs, _helper) : _fs;
        var caps = new LinuxCapabilityProbe(fs, null, _power).Probe();

        Line("# NitroLite Linux self-test");
        Line();
        Line($"- When (UTC): {(nowUtc ?? DateTime.UtcNow).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}");
        Line($"- Distribution: {LinuxDiagnosticsService.OsName(_fs.ReadAllText("/etc/os-release"))}");
        Line($"- Kernel: {_fs.ReadAllText("/proc/sys/kernel/osrelease")?.Trim() ?? "unknown"}");
        Line($"- Model: {caps.Identity.Model} ({caps.Identity.ModelCode}), BIOS {caps.Identity.BiosVersion}");
        if (caps.ModelNote != null) Line($"- Kernel support: {caps.ModelNote}");
        Line();

        Section("Helper service");
        if (_helper == null)
        {
            Check("helper installed", false, "no /run/nitrolite/helper.sock: install the .deb/.rpm, or install.sh --system");
        }
        else
        {
            var allowed = _helper.Allowed;
            Check("helper answers and polkit allows this session", allowed.Count > 0,
                allowed.Count > 0 ? $"{allowed.Count} settings" : "no answer, or polkit refused (is this the active local session?)");
        }

        Section("Settings (current value written back)");
        foreach (var (name, path, safe) in Settings(caps.Nodes))
        {
            if (path == null) continue;
            WriteBack(fs, name, path, safe);
        }

        if (!caps.Nodes.HasKernelFanControl && caps.Nodes.FanSpeed == null)
        {
            Info("fan control", "none on this kernel (see the kernel support line)");
        }

        Section("Operating mode");
        Info("written through", caps.ModeViaPowerProfiles ? "power-profiles-daemon" : caps.Nodes.PlatformProfile != null ? "platform_profile" : "nothing");
        Info("modes", string.Join(", ", caps.Hardware.SupportedOperatingModes));
        Info("power-profiles-daemon", _power.IsAvailable ? $"answers, active {_power.GetActive() ?? "?"}" : "not found");

        Section("Readings");
        var hwmon = LinuxHwmon.Sample(LinuxHwmon.Read(_fs));
        Check("CPU temperature", hwmon.CpuTemperatureC.HasValue, hwmon.CpuTemperatureC is int t ? $"{t} °C" : "no sensor");
        Info("fan RPM", hwmon.HasMeasuredFanRpm ? $"{hwmon.CpuFanRpm} / {hwmon.GpuFanRpm}" : "no tachometer");

        var nvidia = new LinuxNvidiaGpu(_fs, _runner);
        if (nvidia.Device == null) Info("NVIDIA", "no NVIDIA GPU");
        else if (!nvidia.IsAvailable) Check("NVIDIA", false, "GPU found but nvidia-smi is missing (install the NVIDIA driver)");
        else if (nvidia.IsSuspended) Info("NVIDIA", "asleep (runtime suspend), not woken on purpose");
        else
        {
            var sample = nvidia.QueryNow();
            Check("NVIDIA", sample != null, sample == null ? "nvidia-smi did not answer" : $"{sample.Name}, {sample.TemperatureC} °C");
        }

        Section("System pieces");
        Check("pkexec (one-time setup)", _runner.Find("pkexec") != null, _runner.Find("pkexec") ?? "missing");
        Check("pkcheck (helper authorization)", _runner.Find("pkcheck") != null || _helper == null, _runner.Find("pkcheck") ?? "missing");
        Info("busctl (daemon, brightness)", _runner.Find("busctl") ?? "missing");
        Info("keyboard controller (KB5130)", caps.Nodes.HidrawKeyboard ?? "none");
        Info("Linuwu-Sense", caps.Nodes.SenseRoot != null ? "loaded" : "not loaded");
        Info("predator_v4", caps.PredatorV4 ?? "not on this kernel");

        Line();
        Line(_failures == 0 ? "Result: everything NitroLite needs answered." : $"Result: {_failures} check(s) failed.");
        return Redaction.Scrub(_out.ToString());
    }

    private static IEnumerable<(string Name, string? Path, bool Safe)> Settings(LinuxNodes n)
    {
        yield return ("operating mode", n.PlatformProfile, true);
        yield return ("CPU fan mode", n.AcerPwmCpuEnable, true);
        yield return ("GPU fan mode", n.AcerPwmGpuEnable, true);
        yield return ("CPU fan speed", n.AcerPwmCpu, false);
        yield return ("GPU fan speed", n.AcerPwmGpu, false);
        yield return ("fan speed (Linuwu-Sense)", n.FanSpeed, false);
        yield return ("keyboard zones", n.PerZoneMode, true);
        yield return ("keyboard effect", n.FourZoneMode, true);
        yield return ("charge limit", n.BatteryLimiter ?? n.ChargeTypes ?? n.ChargeThreshold, true);
        yield return ("Windows key lock", n.WinKeyLock, true);
        yield return ("CoolBoost", n.CoolBoost, true);
        yield return ("LCD overdrive", n.LcdOverride, true);
        yield return ("backlight timeout", n.BacklightTimeout, true);
        // Never written back: a MUX write schedules a GPU switch that takes effect on the next boot.
        yield return ("GPU mode (MUX)", n.DgpuMode, false);
        // Both are listed; the one the kernel does not have reads as absent and is skipped.
        yield return ("CPU boost", LinuxPaths.CpuFreqBoost, true);
        yield return ("CPU boost (Intel)", LinuxPaths.IntelNoTurbo, true);
    }

    private void WriteBack(ISysfsFileSystem fs, string name, string path, bool safe)
    {
        var current = _fs.ReadAllText(path)?.Trim();
        if (current == null) return;

        if (!safe)
        {
            Check(name, fs.CanWrite(path), fs.CanWrite(path) ? "writable (not written: a speed write can leave automatic mode)" : "not writable");
            return;
        }

        // charge_types reads "[Standard] Long Life" (the active type in brackets), which the kernel and the helper policy
        // both refuse as input; only the active type's name is a value that can be written.
        if (path.EndsWith("/charge_types", StringComparison.Ordinal))
        {
            var active = LinuxChargeTypes.Parse(current)?.Active;
            if (active == null)
            {
                Info(name, "no active charge type to write back");
                return;
            }

            current = active;
        }

        var result = fs.WriteAllText(path, current + "\n");
        Check(name, result.Ok, result.Ok
            ? $"ok ({current})"
            : result.NeedsUdevRule ? "permission denied (no helper, no rule)" : $"{result.Status} {result.Detail}");
    }

    private void Section(string title)
    {
        Line();
        Line("## " + title);
        Line();
    }

    private void Check(string name, bool ok, string detail)
    {
        if (!ok) _failures++;
        Line($"- [{(ok ? "ok" : "FAIL")}] {name}: {detail}");
    }

    private void Info(string name, string detail) => Line($"- {name}: {detail}");

    private void Line(string text = "") => _out.Append(text).Append('\n');
}
