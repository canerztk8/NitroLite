using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace NitroLite.Core.Hardware;

/// <summary>One lighting mode the keyboard advertises, as reported by GET_CONNECTED_DEVICE.</summary>
public sealed record KeyboardEffect
{
    /// <summary>Firmware name, e.g. "DIRECT", "STATIC", "WAVE".</summary>
    public string Name { get; init; } = "";

    /// <summary>How many independent colours the mode accepts. 4 on a four-zone keyboard's DIRECT mode.</summary>
    public int MaxColors { get; init; }

    public int MinSpeed { get; init; }

    public int MaxSpeed { get; init; }
}

/// <summary>
/// What the keyboard can actually do, read from AASSvc's GET_CONNECTED_DEVICE (packet 30).
/// <para>
/// NitroLite assumed a four-zone RGB keyboard everywhere, because that is what the AN16-41 has. The Nitro line also
/// ships single-zone RGB, single-colour backlight and no lighting at all, and a four-zone picker on a single-zone
/// keyboard is a control that cannot work. This record is the device's own answer.
/// </para>
/// </summary>
public sealed record KeyboardCapability
{
    /// <summary>Firmware device name, e.g. "AcerECKeyboard Device".</summary>
    public string DeviceName { get; init; } = "";

    /// <summary>Firmware device type number. 5 on the AN16-41; not interpreted, only reported.</summary>
    public int DeviceType { get; init; }

    /// <summary>
    /// Independent colour zones. 0 means the device reported no lighting at all; 1 means one colour for the whole
    /// keyboard. Never a guess: a keyboard that did not answer produces a null capability instead.
    /// </summary>
    public int ZoneCount { get; init; }

    /// <summary>Lighting modes the firmware advertises, in its own order.</summary>
    public IReadOnlyList<KeyboardEffect> Effects { get; init; } = Array.Empty<KeyboardEffect>();

    /// <summary>True when the device reported at least one colour zone.</summary>
    public bool HasLighting => ZoneCount > 0;

    /// <summary>True when the keyboard takes a separate colour per zone, which is what the zone picker needs.</summary>
    public bool HasZoneColors => ZoneCount > 1;

    /// <summary>One line for the report: "4 zones, 9 effects" or "no lighting".</summary>
    public string Summary => HasLighting
        ? $"{ZoneCount} zone{(ZoneCount == 1 ? "" : "s")}, {Effects.Count} effects"
        : "no lighting";

    /// <summary>
    /// Reads the first device out of a GET_CONNECTED_DEVICE response. Returns null when the payload is not the
    /// shape we expect, so the caller can tell "the service did not answer" from "the keyboard has no lighting".
    /// </summary>
    public static KeyboardCapability? Parse(JsonNode? data)
    {
        if (data is not JsonArray devices) return null;

        JsonObject? device = null;
        foreach (var item in devices)
        {
            if (item is not JsonObject candidate) continue;
            // A machine can report several devices (keyboard, lid logo, ...). The keyboard is the one with modes.
            if (candidate["vector_of_modes"] is JsonArray { Count: > 0 })
            {
                device = candidate;
                break;
            }
            device ??= candidate;
        }
        if (device == null) return null;

        var effects = new List<KeyboardEffect>();
        if (device["vector_of_modes"] is JsonArray modes)
        {
            foreach (var item in modes)
            {
                if (item is not JsonObject mode) continue;
                var name = ReadString(mode, "mode_name");
                if (name.Length == 0) continue;
                effects.Add(new KeyboardEffect
                {
                    Name = name,
                    MaxColors = ReadInt(mode, "maximum_number_of_colors"),
                    MinSpeed = ReadInt(mode, "minimum_speed"),
                    MaxSpeed = ReadInt(mode, "maximum_speed")
                });
            }
        }

        return new KeyboardCapability
        {
            DeviceName = ReadString(device, "device_name"),
            DeviceType = ReadInt(device, "device_type"),
            ZoneCount = DeriveZoneCount(effects),
            Effects = effects
        };
    }

    /// <summary>
    /// The zone count is the DIRECT mode's colour count: DIRECT is the mode that addresses each zone separately, so
    /// its maximum is the number of zones. A keyboard with modes but no DIRECT has one colour for the whole deck.
    /// </summary>
    public static int DeriveZoneCount(IReadOnlyList<KeyboardEffect> effects)
    {
        if (effects.Count == 0) return 0;

        var direct = effects.FirstOrDefault(e => e.Name.Equals("DIRECT", StringComparison.OrdinalIgnoreCase));
        if (direct != null) return Math.Max(1, direct.MaxColors);

        return 1;
    }

    private static string ReadString(JsonObject o, string key)
    {
        try { return o[key]?.ToString()?.Trim() ?? ""; }
        catch { return ""; }
    }

    private static int ReadInt(JsonObject o, string key)
    {
        try
        {
            var node = o[key];
            if (node is JsonValue value && value.TryGetValue<int>(out var number)) return number;
            return 0;
        }
        catch { return 0; }
    }
}
