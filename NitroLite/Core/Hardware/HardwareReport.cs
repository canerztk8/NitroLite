using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace NitroLite.Core.Hardware;

/// <summary>
/// A machine-readable snapshot of what NitroLite sees on this laptop, meant to be attached to a GitHub issue.
/// <para>
/// The point is to stop guessing about models we do not own. When a Nitro V 15 user reports that the fan slider
/// does nothing, the report says which model code, which BIOS, which Acer services are running and whether the
/// WMI class answers — the four facts that decide where the bug actually lives.
/// </para>
/// <para>
/// Labels are English on purpose. The report is read by whoever triages the issue, and a fixed vocabulary keeps
/// two reports from different app languages diffable against each other. The button that produces it is localized
/// like every other control.
/// </para>
/// <para>
/// Deliberately excluded: serial numbers, machine GUID, user name, file paths, log contents, IP or MAC addresses.
/// Everything here is either public model data or a service state.
/// </para>
/// </summary>
public static class HardwareReport
{
    /// <summary>
    /// The Acer services worth reporting. The two required ones come first because their state explains most
    /// "the app cannot talk to my laptop" reports; the rest are the debloat candidates.
    /// </summary>
    private static readonly string[] ReportedServices =
    {
        "AASSvc",
        "AcerLightingService",
        "AcerCCAgentSvis",
        "AcerServiceSvc",
        "ASMSvc",
        "AcerDeviceEnablingServiceV2",
        "AcerDeviceInfoAgentService",
        "AcerDIAgentSvis",
        "AcerEZSvc",
        "AcerQAAgentSvis"
    };

    /// <summary>Builds the plain-text report. Never throws: a failed section is reported as unavailable.</summary>
    public static string BuildText()
    {
        var identity = HardwareDetector.Current;
        var hardware = HardwareInfoService.Get();
        var wmi = AcerWmiProbe.Read();
        var sb = new StringBuilder();

        sb.AppendLine("NitroLite hardware report");
        sb.AppendLine($"Generated  {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)}");
        sb.AppendLine();

        sb.AppendLine("APPLICATION");
        sb.AppendLine($"  NitroLite   {UpdateService.CurrentVersionText}");
        sb.AppendLine($"  Windows     {WindowsVersionText()}");
        sb.AppendLine($"  Runtime     {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"  Language    {Loc.Language}");
        sb.AppendLine();

        sb.AppendLine("DEVICE");
        if (identity.IsEmpty)
        {
            sb.AppendLine("  (SMBIOS did not answer)");
        }
        else
        {
            AppendField(sb, "Manufacturer", identity.Manufacturer);
            AppendField(sb, "Model", identity.Model);
            AppendField(sb, "Model code", identity.ModelCode);
            AppendField(sb, "Family", identity.ModelFamily);
            AppendField(sb, "Board", identity.BoardProduct);
            AppendField(sb, "BIOS", string.Join(" ", new[] { identity.BiosVendor, identity.BiosVersion }
                .Where(s => !string.IsNullOrWhiteSpace(s))));
            AppendField(sb, "CPU vendor", identity.CpuVendor.ToString());
        }
        sb.AppendLine();

        sb.AppendLine("HARDWARE");
        AppendField(sb, "CPU", hardware.CpuName);
        AppendField(sb, "Cores", hardware.CpuCores > 0
            ? $"{hardware.CpuCores} cores / {hardware.CpuThreads} threads"
            : "");
        AppendField(sb, "GPU", hardware.DiscreteGpuName);
        AppendField(sb, "Memory", FormatMemory(hardware));
        sb.AppendLine();

        sb.AppendLine("ACER SERVICES");
        foreach (var name in ReportedServices)
        {
            var (startType, live) = ReadService(name);
            sb.AppendLine($"  {name,-30} {live,-10} {startType}");
        }
        sb.AppendLine();

        sb.AppendLine("ACER WMI");
        sb.AppendLine($"  AcerGamingFunction             {wmi.Summary}");
        if (wmi.Methods.Count > 0)
            sb.AppendLine($"  Methods                        {string.Join(", ", wmi.Methods)}");
        sb.AppendLine();

        sb.AppendLine("This file contains no serial number, user name, file path or log content.");
        return sb.ToString();
    }

    /// <summary>Builds the same data as JSON, for anyone who wants to script against it.</summary>
    public static string BuildJson()
    {
        var identity = HardwareDetector.Current;
        var hardware = HardwareInfoService.Get();
        var wmi = AcerWmiProbe.Read();

        var services = new JsonArray();
        foreach (var name in ReportedServices)
        {
            var (startType, live) = ReadService(name);
            services.Add(new JsonObject
            {
                ["name"] = name,
                ["state"] = live,
                ["startType"] = startType
            });
        }

        var root = new JsonObject
        {
            ["generated"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
            ["application"] = new JsonObject
            {
                ["nitrolite"] = UpdateService.CurrentVersionText,
                ["windows"] = WindowsVersionText(),
                ["runtime"] = RuntimeInformation.FrameworkDescription,
                ["language"] = Loc.Language
            },
            ["device"] = new JsonObject
            {
                ["manufacturer"] = identity.Manufacturer,
                ["model"] = identity.Model,
                ["modelCode"] = identity.ModelCode,
                ["family"] = identity.ModelFamily,
                ["board"] = identity.BoardProduct,
                ["biosVendor"] = identity.BiosVendor,
                ["biosVersion"] = identity.BiosVersion,
                ["cpuVendor"] = identity.CpuVendor.ToString()
            },
            ["hardware"] = new JsonObject
            {
                ["cpu"] = hardware.CpuName,
                ["cores"] = hardware.CpuCores,
                ["threads"] = hardware.CpuThreads,
                ["gpu"] = hardware.DiscreteGpuName,
                ["memoryModules"] = hardware.MemoryModules,
                ["memoryTotalGb"] = hardware.MemoryTotalGb,
                ["memorySpeedMts"] = hardware.MemorySpeedMts,
                ["memoryType"] = hardware.MemoryType
            },
            ["acerServices"] = services,
            ["acerWmi"] = BuildWmiNode(wmi)
        };

        return root.ToJsonString(JsonOptions);
    }

    /// <summary>
    /// The WMI section. <c>providerVersion</c> is only written when the description actually carried one, so the
    /// file never shows an empty field that looks like a failed read.
    /// </summary>
    private static JsonObject BuildWmiNode(AcerWmiProbeResult wmi)
    {
        var node = new JsonObject
        {
            ["classPresent"] = wmi.ClassPresent,
            ["methods"] = new JsonArray(wmi.Methods.Select(m => (JsonNode?)JsonValue.Create(m)).ToArray()),
            ["callMethod"] = wmi.CallMethod,
            ["callSucceeded"] = wmi.CallSucceeded,
            ["callError"] = wmi.CallError
        };
        if (!string.IsNullOrWhiteSpace(wmi.ProviderVersion))
            node["providerVersion"] = wmi.ProviderVersion;
        return node;
    }

    /// <summary>
    /// Indented, and without the default HTML-safe escaping that turns a timezone offset into "\u002B01:00". The
    /// content is our own diagnostic data, never rendered as HTML, so the relaxed encoder is safe here and keeps the
    /// file readable for the person attaching it to an issue.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// Writes both formats into <paramref name="directory"/> and returns the text file's path. Throws only when the
    /// directory itself cannot be written, which the caller turns into a message.
    /// </summary>
    public static string WriteToDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var textPath = Path.Combine(directory, $"hardware-report-{stamp}.txt");
        var jsonPath = Path.Combine(directory, $"hardware-report-{stamp}.json");

        File.WriteAllText(textPath, BuildText(), new UTF8Encoding(false));
        try { File.WriteAllText(jsonPath, BuildJson(), new UTF8Encoding(false)); }
        catch { /* the text file is the deliverable; JSON is a convenience */ }

        return textPath;
    }

    /// <summary>"10.0.26100 (X64)" — the build number is what actually distinguishes Windows versions in reports.</summary>
    public static string WindowsVersionText()
    {
        try
        {
            var v = Environment.OSVersion.Version;
            return $"{v.Major}.{v.Minor}.{v.Build} ({RuntimeInformation.ProcessArchitecture})";
        }
        catch
        {
            return RuntimeInformation.OSDescription;
        }
    }

    /// <summary>"DDR5 5600 MT/s · 2 modules · 32 GB", or "" when nothing was read.</summary>
    public static string FormatMemory(HardwareInfo hardware)
    {
        var parts = new List<string>();
        var typeSpeed = string.Join(" ", new[]
        {
            hardware.MemoryType,
            hardware.MemorySpeedMts > 0 ? $"{hardware.MemorySpeedMts} MT/s" : ""
        }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (typeSpeed.Length > 0) parts.Add(typeSpeed);
        if (hardware.MemoryModules > 0) parts.Add($"{hardware.MemoryModules} modules");
        if (hardware.MemoryTotalGb > 0) parts.Add($"{hardware.MemoryTotalGb:0.#} GB");
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Start type from the registry and live state from the service manager. A missing service reports as
    /// "not installed" rather than being omitted, because its absence is itself the diagnosis.
    /// </summary>
    private static (string StartType, string State) ReadService(string name)
    {
        string startType;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}", false);
            if (key == null) return ("not installed", "-");
            var raw = key.GetValue("Start");
            var value = raw is int i ? i : 2;
            startType = value switch
            {
                0 => "Boot",
                1 => "System",
                2 => "Automatic",
                3 => "Manual",
                4 => "Disabled",
                _ => "Unknown"
            };
        }
        catch
        {
            return ("unknown", "-");
        }

        var live = DebloatService.QueryServiceStatusDirect(name) ?? "Stopped";
        return (startType, live);
    }

    private static void AppendField(StringBuilder sb, string label, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        sb.AppendLine($"  {label,-12}  {value}");
    }
}
