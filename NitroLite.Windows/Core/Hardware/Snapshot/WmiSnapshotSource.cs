using System.Globalization;
using System.Text.Json.Nodes;
using NitroLite.Core.Hardware.Snapshot;
using NitroLite.Core.Hardware.Wmi;

namespace NitroLite.Windows.Core.Hardware.Snapshot;

/// <summary>
/// The Acer WMI half of a snapshot: every selector the app can read without writing anything.
/// </summary>
/// <remarks>
/// <para>
/// Each entry is named after the selector it came from, not after the label the window shows, because the diff is read
/// by someone comparing it with a NitroSense capture and a WMI trace: <c>misc0x0B</c> and <c>gamingProfile.overdrive</c>
/// are the words in the documentation, "Balanced" is not.
/// </para>
/// <para>
/// The raw worker state is captured first and flattened, so a firmware word that this build does not decode still shows
/// up and still diffs. The typed reads follow, because a decoded value is what tells a reader that the mode actually
/// changed rather than the status byte.
/// </para>
/// </remarks>
internal sealed class WmiSnapshotSource : IHardwareSnapshotSource
{
    /// <summary>The section these entries are filed under.</summary>
    public const string SectionName = "wmi";

    private readonly WmiHardwareController _wmi;

    internal WmiSnapshotSource(WmiHardwareController wmi) =>
        _wmi = wmi ?? throw new ArgumentNullException(nameof(wmi));

    public string Section => SectionName;

    public async Task<IReadOnlyList<HardwareSnapshotEntry>> CaptureAsync(CancellationToken cancellationToken)
    {
        var entries = new List<HardwareSnapshotEntry>();
        var windowsMode = new NitroLite.Windows.Core.Hardware.WindowsPowerModeService().Read();
        entries.Add(new HardwareSnapshotEntry("windows", "effectivePowerMode", windowsMode?.ToString() ?? "not read"));

        // The raw words first: GetGamingMiscSetting(0x02/0x0B), GetGamingFanBehavior, GetGamingFanSpeed, the battery
        // limit register and the registry-only dGPU capability mask, exactly as the elevated worker answered them.
        var raw = await HardwareSnapshotService.ReadAsync(Section, "deviceState", async () =>
        {
            var state = await _wmi.ReadRawDeviceStateAsync().ConfigureAwait(false);
            return state == null ? null : Flatten(state);
        }).ConfigureAwait(false);

        if (raw.IsUnavailable || !raw.Value.Contains('=', StringComparison.Ordinal))
        {
            entries.Add(raw);
        }
        else
        {
            foreach (var line in raw.Value.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                int separator = line.IndexOf('=', StringComparison.Ordinal);
                if (separator <= 0) continue;

                entries.Add(new HardwareSnapshotEntry(
                    Section, "deviceState." + line[..separator].Trim(), line[(separator + 1)..].Trim()));
            }
        }

        await AddAsync(entries, "misc0x09.muxSupportOverWmi", async () =>
            (await _wmi.GetCapabilitiesAsync().ConfigureAwait(false)).MuxSwitchOverWmi?.ToString()).ConfigureAwait(false);

        await AddAsync(entries, "misc0x0A.supportedModeMask", async () =>
        {
            var mask = (await _wmi.GetCapabilitiesAsync().ConfigureAwait(false)).SupportedModeMask;
            return mask.HasValue
                ? "0x" + mask.Value.ToString("X", CultureInfo.InvariantCulture)
                : null;
        }).ConfigureAwait(false);

        await AddAsync(entries, "gamingProfile.operatingMode", async () =>
            (await _wmi.GetOperatingModeAsync().ConfigureAwait(false))?.ToString()).ConfigureAwait(false);

        await AddAsync(entries, "gamingProfile.lcdOverdrive", async () =>
            (await _wmi.GetLcdOverdriveAsync().ConfigureAwait(false))?.ToString()).ConfigureAwait(false);

        await AddAsync(entries, "gamingProfile.backlightTimeout", async () =>
            (await _wmi.GetBacklightTimeoutAsync().ConfigureAwait(false))?.ToString()).ConfigureAwait(false);

        await AddAsync(entries, "apge0x207.coolBoost", async () =>
            (await _wmi.GetCoolBoostAsync().ConfigureAwait(false))?.ToString()).ConfigureAwait(false);

        await AddAsync(entries, "battery.chargeMode", async () =>
            (await _wmi.GetBatteryChargeModeAsync().ConfigureAwait(false))?.ToString()).ConfigureAwait(false);

        await AddAsync(entries, "gpu.mode", async () =>
            (await _wmi.GetGpuModeAsync().ConfigureAwait(false))?.ToString()).ConfigureAwait(false);

        await AddAsync(entries, "fan.behavior", async () =>
            (await _wmi.GetFanModeAsync().ConfigureAwait(false))?.ToString()).ConfigureAwait(false);

        await AddAsync(entries, "fan.percent", async () =>
        {
            var percent = await _wmi.GetFanPercentAsync().ConfigureAwait(false);
            return percent.HasValue ? percent.Value.ToString(CultureInfo.InvariantCulture) : null;
        }).ConfigureAwait(false);

        await AddAsync(entries, "lighting.config", async () =>
        {
            var lighting = await _wmi.GetLightingConfigAsync().ConfigureAwait(false);
            if (lighting == null) return null;

            return string.Create(
                CultureInfo.InvariantCulture,
                $"{lighting.Effect} brightness={lighting.Brightness} speed={lighting.Speed} direction={lighting.Direction} zones={string.Join(',', lighting.LEDs)}");
        }).ConfigureAwait(false);

        return entries;
    }

    /// <summary>One guarded read appended to the capture.</summary>
    private async Task AddAsync(List<HardwareSnapshotEntry> entries, string key, Func<Task<string?>> read) =>
        entries.Add(await HardwareSnapshotService.ReadAsync(Section, key, read).ConfigureAwait(false));

    /// <summary>
    /// The worker's JSON as <c>key=value</c> lines, nested objects flattened with dots and arrays joined with commas.
    /// Values keep their own type — a bool stays <c>true</c> — so the diff compares what the firmware said.
    /// </summary>
    internal static string Flatten(JsonObject state)
    {
        var lines = new List<string>();
        Walk("", state, lines);
        lines.Sort(StringComparer.Ordinal);
        return string.Join('\n', lines);
    }

    private static void Walk(string prefix, JsonNode? node, List<string> lines)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    Walk(prefix.Length == 0 ? key : $"{prefix}.{key}", value, lines);
                }

                break;

            case JsonArray array:
                var items = new List<string>();
                foreach (var item in array)
                {
                    items.Add(item is JsonValue value ? value.ToJsonString() : "");
                }

                lines.Add($"{prefix}={string.Join(',', items)}");
                break;

            case JsonValue scalar:
                lines.Add($"{prefix}={scalar.ToJsonString().Trim('"')}");
                break;

            default:
                lines.Add($"{prefix}=null");
                break;
        }
    }
}
