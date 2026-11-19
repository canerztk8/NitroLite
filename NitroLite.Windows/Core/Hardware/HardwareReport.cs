using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using NitroLite.Core.Hardware.Wmi;

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
    public static string BuildText(DeviceCapabilities? capabilities = null)
    {
        var identity = HardwareDetector.Current;
        var hardware = HardwareInfoService.Get();
        var probe = CapabilityProbe.Probe(identity, ProfileCatalog.Load());
        var wmi = probe.Wmi;
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

        sb.AppendLine("KEYBOARD");
        var keyboard = capabilities?.Keyboard;
        if (keyboard == null)
        {
            sb.AppendLine("  (the Acer service did not answer)");
        }
        else
        {
            AppendField(sb, "Device", keyboard.DeviceName);
            AppendField(sb, "Type", keyboard.DeviceType > 0 ? keyboard.DeviceType.ToString(CultureInfo.InvariantCulture) : "");
            AppendField(sb, "Zones", keyboard.HasLighting ? keyboard.ZoneCount.ToString(CultureInfo.InvariantCulture) : "none");
            AppendField(sb, "Effects", string.Join(", ", keyboard.Effects.Select(e => e.Name)));
        }
        sb.AppendLine();

        sb.AppendLine("CAPABILITIES");
        AppendField(sb, "Modes", capabilities?.OperatingModes == null
            ? ""
            : string.Join(", ", capabilities.OperatingModes.Select(m => m.ToString())));
        AppendField(sb, "GPU modes", capabilities?.GpuModeCapability?.ToString(CultureInfo.InvariantCulture) ?? "");
        AppendField(sb, "Fan", capabilities?.FanMode?.ToString() ?? "");
        AppendField(sb, "Backlight", capabilities?.LightingCapability switch
        {
            true => "yes",
            false => "no",
            null => ""
        });
        sb.AppendLine();

        sb.AppendLine("ACER WMI");
        sb.AppendLine($"  AcerGamingFunction             {wmi.Summary}");
        if (wmi.Methods.Count > 0)
            sb.AppendLine($"  Methods                        {string.Join(", ", wmi.Methods)}");
        if (wmi.Signatures.Count > 0)
        {
            sb.AppendLine("  Signatures");
            foreach (var signature in wmi.Signatures) sb.AppendLine($"    {signature}");
        }

        sb.AppendLine();
        AppendProbeSection(sb, probe);
        sb.AppendLine();

        sb.AppendLine("This file contains no serial number, user name, file path or log content.");
        return sb.ToString();
    }

    /// <summary>
    /// A Markdown report meant to be pasted straight into a GitHub issue, built from the snapshot the window already
    /// holds rather than from a fresh probe.
    /// <para>
    /// This is the report a user on an unlisted model sends in, so it is built around the questions that decide where
    /// a bug lives: which profile matched (or that the catch-all did), what the firmware's own bitmasks say, whether
    /// the sensor ids answered plausibly, and which capabilities the read-back policy has switched off. Nothing here
    /// needs elevation — the caller passes whatever it already has, and a section nobody could read says so instead
    /// of being omitted.
    /// </para>
    /// <para>
    /// Deliberately excluded, and asserted by a test: serial numbers, machine UUID, Windows user name, file paths and
    /// log content. Everything below is either public model data or a firmware answer.
    /// </para>
    /// </summary>
    public static string BuildMarkdown(
        CapabilitySnapshot probe,
        HardwareCapabilities? capabilities = null,
        ReadBackPolicy? readBack = null,
        SensorLayout? discoveredSensors = null,
        bool sensorProbeRan = false,
        DeviceIdentity? identity = null,
        string? backend = null)
    {
        identity ??= HardwareDetector.Current;
        var sb = new StringBuilder();

        sb.AppendLine("### NitroLite hardware report");
        sb.AppendLine();
        sb.AppendLine("| Application | |");
        sb.AppendLine("| --- | --- |");
        sb.AppendLine($"| NitroLite | {Escape(UpdateService.CurrentVersionText)} |");
        sb.AppendLine($"| Windows | {Escape(WindowsVersionText())} |");
        sb.AppendLine($"| Runtime | {Escape(RuntimeInformation.FrameworkDescription)} |");
        sb.AppendLine($"| Language | {Escape(Loc.Language)} |");
        sb.AppendLine();

        sb.AppendLine("| Device | |");
        sb.AppendLine("| --- | --- |");
        AppendRow(sb, "Manufacturer", identity.Manufacturer);
        AppendRow(sb, "Model", identity.Model);
        AppendRow(sb, "Model code", identity.ModelCode);
        AppendRow(sb, "Family", identity.ModelFamily);
        AppendRow(sb, "Board", identity.BoardProduct);
        AppendRow(sb, "BIOS", string.Join(" ", new[] { identity.BiosVendor, identity.BiosVersion }
            .Where(s => !string.IsNullOrWhiteSpace(s))));
        AppendRow(sb, "CPU vendor", identity.CpuVendor.ToString());
        sb.AppendLine();

        sb.AppendLine("| Profile | |");
        sb.AppendLine("| --- | --- |");
        AppendRow(sb, "Matched", $"{probe.ProfileId} ({probe.ProfileSource})");
        AppendRow(sb, "Cache key", probe.CacheKey);
        AppendRow(sb, "Notes", probe.ProfileNotes);
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(backend))
        {
            // Which path answers, and why the other one does not. A report that says "nothing detected" without this
            // cannot be triaged: the same symptom is a service that is silent, a helper that was refused, or a machine
            // whose firmware has neither, and only the backend line tells them apart.
            sb.AppendLine("| Backend | |");
            sb.AppendLine("| --- | --- |");
            AppendRow(sb, "State", backend);
            sb.AppendLine();
        }

        sb.AppendLine("| Capability | Outcome | Detail |");
        sb.AppendLine("| --- | --- | --- |");
        foreach (var capability in probe.Capabilities)
            sb.AppendLine($"| {Escape(capability.Capability)} | {capability.Outcome} | {Escape(capability.Detail)} |");
        sb.AppendLine();

        sb.AppendLine("| Acer WMI | |");
        sb.AppendLine("| --- | --- |");
        AppendRow(sb, "AcerGamingFunction", probe.Wmi.Summary);
        AppendRow(sb, "Methods", string.Join(", ", probe.Wmi.Methods));
        AppendRow(sb, "Signatures", string.Join("<br>", probe.Wmi.Signatures));
        AppendRow(sb, "Acer classes", probe.AcerClassGuids.Count == 0
            ? "not read (cached snapshot)"
            : string.Join(", ", probe.AcerClassGuids.Select(p => $"{p.Key} {p.Value}")));
        sb.AppendLine();

        sb.AppendLine("| Registry signal | Reading |");
        sb.AppendLine("| --- | --- |");
        AppendRow(sb, "Key present", probe.Registry.KeyPresent ? "yes" : "no");
        AppendRow(sb, "dgpu_mode_capability", Format(probe.Registry.DgpuModeCapability));
        AppendRow(sb, "KeyBoardArea", Format(probe.Registry.KeyboardArea));
        AppendRow(sb, "LCD_Overdrive_support", Format(probe.Registry.LcdOverdriveSupport));
        AppendRow(sb, "GPU_OC_Support", Format(probe.Registry.GpuOverclockSupport));
        sb.AppendLine();

        sb.AppendLine("| Live selector | Answer |");
        sb.AppendLine("| --- | --- |");
        AppendRow(sb, "GetGamingMiscSetting(0x09) MUX", probe.WmiMuxSignal switch
        {
            true => "3 (switchable over WMI)",
            false => "not 3",
            null => "not read (needs elevation)"
        });
        AppendRow(sb, "GetGamingMiscSetting(0x0A) modes", capabilities?.SupportedModeMask is ulong mask
            ? $"0x{mask:X4} -> {FormatModes(capabilities.SupportedOperatingModes)}"
            : "not read (needs elevation)");
        AppendRow(sb, "APGeAction", capabilities == null
            ? "not read (needs elevation)"
            : capabilities.CoolBoost || capabilities.BacklightTimeout ? "present" : "absent");
        AppendRow(sb, "GetGamingProfile(0) overdrive", capabilities == null
            ? "not read (needs elevation)"
            : capabilities.LcdOverdrive ? "supported" : "not supported");
        AppendRow(sb, "Windows-key lock", capabilities == null
            ? "not read (needs elevation)"
            : capabilities.WinKeyLock ? "writable" : "not writable");
        sb.AppendLine();

        sb.AppendLine("| Sensors | |");
        sb.AppendLine("| --- | --- |");
        AppendRow(sb, "Profile layout", DescribeLayout(probe.Sensors));
        AppendRow(sb, "Live probe", sensorProbeRan
            ? discoveredSensors != null ? "ran, confirmed" : "ran, rejected"
            : "not run");
        if (discoveredSensors != null) AppendRow(sb, "Discovered layout", DescribeLayout(discoveredSensors));
        AppendRow(sb, "Fan ceiling", $"CPU {probe.MaxCpuFanRpm} / GPU {probe.MaxGpuFanRpm} RPM");
        AppendRow(sb, "Plausibility window", SensorPlausibility.Describe());
        sb.AppendLine();

        sb.AppendLine("| Read-back policy | Session failures | State |");
        sb.AppendLine("| --- | --- | --- |");
        if (readBack == null)
        {
            sb.AppendLine("| (direct-WMI backend not started) | - | - |");
        }
        else
        {
            foreach (var capability in ReadBackPolicy.AllCapabilities)
            {
                var state = readBack.IsPermanentlyDisabled(capability)
                    ? "permanently disabled"
                    : readBack.IsDisabled(capability) ? "disabled for this session" : "active";
                sb.AppendLine($"| {Escape(capability)} | {readBack.ConsecutiveSessionFailures(capability)} | {state} |");
            }
        }
        sb.AppendLine();

        if (probe.Rejections.Count > 0)
        {
            sb.AppendLine("**Rejected profile records**");
            sb.AppendLine();
            foreach (var rejection in probe.Rejections) sb.AppendLine($"- {Escape(rejection)}");
            sb.AppendLine();
        }

        sb.AppendLine("_No serial number, machine UUID, user name, file path or log content is included._");
        return sb.ToString();
    }

    /// <summary>One Markdown table row, skipped entirely when the value is empty so the table has no blank cells.</summary>
    private static void AppendRow(StringBuilder sb, string label, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        sb.AppendLine($"| {Escape(label)} | {Escape(value)} |");
    }

    /// <summary>
    /// Escapes the two characters that would break a Markdown table cell. Newlines become <c>&lt;br&gt;</c> so a
    /// multi-line note stays inside its row instead of splitting the table.
    /// </summary>
    private static string Escape(string? value) => string.IsNullOrEmpty(value)
        ? ""
        : value.Replace("|", "\\|").Replace("\r\n", "<br>").Replace("\n", "<br>").Replace("\r", "<br>");

    private static string Format(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";

    private static string FormatModes(IReadOnlyList<OperatingMode> modes) =>
        modes.Count == 0 ? "none advertised" : string.Join(", ", modes);

    private static string DescribeLayout(SensorLayout layout) =>
        $"cpu={layout.CpuTempSensor} gpu={layout.GpuTempSensor} sys1={layout.Sys1TempSensor} "
        + $"rpm={layout.CpuFanRpmSensor}/{layout.GpuFanRpmSensor} "
        + (layout.Supported ? "supported" : "unsupported")
        + (layout.Verified ? ", verified" : ", unverified");

    /// <summary>
    /// The capability probe section: which profile matched, what each capability answered, and which profile records
    /// were rejected. Written from the same snapshot the window uses, so a report and a bug report cannot disagree.
    /// </summary>
    private static void AppendProbeSection(StringBuilder sb, CapabilitySnapshot probe)
    {
        sb.AppendLine("CAPABILITY PROBE");
        AppendField(sb, "Profile", $"{probe.ProfileId} ({probe.ProfileSource})");
        AppendField(sb, "Cache key", probe.CacheKey);
        AppendField(sb, "Sensors", string.Join(" ", new[]
        {
            $"cpu={probe.Sensors.CpuTempSensor}",
            $"gpu={probe.Sensors.GpuTempSensor}",
            $"sys1={probe.Sensors.Sys1TempSensor}",
            $"rpm={probe.Sensors.CpuFanRpmSensor}/{probe.Sensors.GpuFanRpmSensor}",
            probe.Sensors.Supported ? "(supported)" : "(unsupported)",
            probe.Sensors.Verified ? "(verified)" : "(unverified)"
        }));
        AppendField(sb, "Fan ceiling", $"CPU {probe.MaxCpuFanRpm} / GPU {probe.MaxGpuFanRpm} RPM");
        foreach (var capability in probe.Capabilities)
            sb.AppendLine($"  {capability.Capability,-30} {capability.Verdict}"
                          + (string.IsNullOrWhiteSpace(capability.Detail) ? "" : $" ({capability.Detail})"));
        if (probe.Rejections.Count > 0)
        {
            sb.AppendLine("  Rejected profiles");
            foreach (var rejection in probe.Rejections) sb.AppendLine($"    {rejection}");
        }
    }

    /// <summary>Builds the same data as JSON, for anyone who wants to script against it.</summary>
    public static string BuildJson(DeviceCapabilities? capabilities = null)
    {
        var identity = HardwareDetector.Current;
        var hardware = HardwareInfoService.Get();
        var probe = CapabilityProbe.Probe(identity, ProfileCatalog.Load());
        var wmi = probe.Wmi;

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
            ["keyboard"] = BuildKeyboardNode(capabilities?.Keyboard),
            ["capabilities"] = BuildCapabilitiesNode(capabilities),
            ["acerServices"] = services,
            ["acerWmi"] = BuildWmiNode(wmi),
            ["capabilityProbe"] = BuildProbeNode(probe)
        };

        return root.ToJsonString(JsonOptions);
    }

    /// <summary>
    /// The capability probe section. Every capability carries its outcome by name, so "access denied" and "not
    /// supported" stay distinguishable in a file someone else reads.
    /// </summary>
    private static JsonObject BuildProbeNode(CapabilitySnapshot probe)
    {
        var capabilities = new JsonObject();
        foreach (var capability in probe.Capabilities)
        {
            capabilities[capability.Capability] = new JsonObject
            {
                ["outcome"] = capability.Outcome.ToString(),
                ["detail"] = capability.Detail
            };
        }

        var rejections = new JsonArray();
        foreach (var rejection in probe.Rejections) rejections.Add(rejection);

        return new JsonObject
        {
            ["profile"] = probe.ProfileId,
            ["profileSource"] = probe.ProfileSource,
            ["cacheKey"] = probe.CacheKey,
            ["sensors"] = new JsonObject
            {
                ["cpuTempSensor"] = probe.Sensors.CpuTempSensor,
                ["gpuTempSensor"] = probe.Sensors.GpuTempSensor,
                ["sys1TempSensor"] = probe.Sensors.Sys1TempSensor,
                ["cpuFanRpmSensor"] = probe.Sensors.CpuFanRpmSensor,
                ["gpuFanRpmSensor"] = probe.Sensors.GpuFanRpmSensor,
                ["supported"] = probe.Sensors.Supported,
                ["verified"] = probe.Sensors.Verified
            },
            ["maxCpuFanRpm"] = probe.MaxCpuFanRpm,
            ["maxGpuFanRpm"] = probe.MaxGpuFanRpm,
            ["capabilities"] = capabilities,
            ["rejectedProfiles"] = rejections
        };
    }

    /// <summary>
    /// The keyboard section. A null capability is written as <c>null</c> rather than an empty object, so a reader can
    /// tell "the service did not answer" from "the keyboard has no lighting".
    /// </summary>
    private static JsonNode? BuildKeyboardNode(KeyboardCapability? keyboard)
    {
        if (keyboard == null) return null;

        var effects = new JsonArray();
        foreach (var effect in keyboard.Effects)
        {
            effects.Add(new JsonObject
            {
                ["name"] = effect.Name,
                ["maxColors"] = effect.MaxColors,
                ["minSpeed"] = effect.MinSpeed,
                ["maxSpeed"] = effect.MaxSpeed
            });
        }

        return new JsonObject
        {
            ["deviceName"] = keyboard.DeviceName,
            ["deviceType"] = keyboard.DeviceType,
            ["zones"] = keyboard.ZoneCount,
            ["hasLighting"] = keyboard.HasLighting,
            ["effects"] = effects
        };
    }

    /// <summary>
    /// The capability section. A null member is written as <c>null</c> rather than an empty list, so a reader can
    /// tell "the service did not answer" from "the machine offers nothing".
    /// </summary>
    private static JsonObject BuildCapabilitiesNode(DeviceCapabilities? capabilities)
    {
        JsonArray? modes = null;
        if (capabilities?.OperatingModes != null)
        {
            modes = new JsonArray();
            foreach (var mode in capabilities.OperatingModes) modes.Add(mode.ToString());
        }

        return new JsonObject
        {
            ["operatingModes"] = modes,
            ["gpuModeCapability"] = capabilities?.GpuModeCapability,
            ["fanMode"] = capabilities?.FanMode?.ToString(),
            ["backlightControllable"] = capabilities?.LightingCapability
        };
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
            ["signatures"] = new JsonArray(wmi.Signatures.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
            ["dynamicClass"] = wmi.DynamicClass,
            ["instanceAccessDenied"] = wmi.InstanceAccessDenied,
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
    public static string WriteToDirectory(string directory, DeviceCapabilities? capabilities = null)
    {
        Directory.CreateDirectory(directory);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var textPath = Path.Combine(directory, $"hardware-report-{stamp}.txt");
        var jsonPath = Path.Combine(directory, $"hardware-report-{stamp}.json");

        File.WriteAllText(textPath, BuildText(capabilities), new UTF8Encoding(false));
        try { File.WriteAllText(jsonPath, BuildJson(capabilities), new UTF8Encoding(false)); }
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
