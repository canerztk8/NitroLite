using System.Text.Json.Nodes;

namespace NitroLite.Core.Hardware.Snapshot;

/// <summary>
/// The read-only sweep that names the selector a model's operating mode really lives in.
/// </summary>
/// <remarks>
/// <para>
/// A machine can advertise every mode bit — <c>GetGamingMiscSetting(0x0A)</c> answering <c>0x7300</c> — and still
/// ignore the write: AN16-61 answers status 0 to <c>SetGamingMiscSetting(0x0B)</c> for all four modes and lands on
/// Balanced every time. On that model the app cannot tell "the firmware refused" from "the mode is somewhere else",
/// because it has only ever asked one selector.
/// </para>
/// <para>
/// So the sweep asks every selector the interface has and prints the ones that answered with something other than
/// zero. No setter is ever called — but that makes it write-free, not harmless: <c>APGeAction.GetFunction</c> is an
/// undocumented ACPI subfunction call into the EC, and on one reported Acer model this sweep hard-reset the machine
/// mid-run. Two captures taken either side of a mode change in NitroSense then name the selector that moved, and
/// that is the only honest way to find it: the mode numbering of an unconfirmed model must be measured, not inferred
/// from a sibling. For exactly that reason the sweep is not part of a snapshot: it runs only from the explicitly
/// confirmed sweep action (or <c>scripts/probe-mode-selectors.ps1</c>), never from "Capture state A".
/// </para>
/// <para>
/// The answers are kept to one entry per class rather than one entry per selector. A capture is capped at
/// <see cref="HardwareSnapshot.MaximumEntries"/> entries and printed in key order, so a hundred extra selector lines
/// would push the real findings off the end of the report. One line per class always survives, and the diff still
/// shows it the moment anything in the sweep changes.
/// </para>
/// </remarks>
public static class ModeSelectorProbe
{
    /// <summary>The selectors the capture swept, so a reader of a lone snapshot knows the sweep ran.</summary>
    public const string PlanKey = "probe.plan";

    /// <summary>Every <c>GetGamingMiscSetting</c> selector that answered a non-zero word.</summary>
    public const string MiscKey = "probe.misc";

    /// <summary>Every <c>GetGamingProfile</c> selector that answered a non-zero word.</summary>
    public const string ProfileKey = "probe.profile";

    /// <summary>Every <c>APGeAction.GetFunction</c> selector that answered a non-zero word.</summary>
    public const string ApgeKey = "probe.apge";

    /// <summary>Printed in place of a list when the whole class answered zero.</summary>
    public const string NothingAnswered = "none answered";

    /// <summary>The class names inside the worker's answer, in the order the report prints them.</summary>
    private static readonly (string Json, string Key)[] Groups =
    {
        ("misc", MiscKey),
        ("profile", ProfileKey),
        ("apge", ApgeKey)
    };

    /// <summary>
    /// The sweep result as report entries: the plan first, then one line per class. A missing class is reported as
    /// "none answered" rather than dropped, because a class that disappeared is a change a diff has to be able to
    /// show — an absent line reads as "this build never asked".
    /// </summary>
    /// <param name="sweep">The worker's answer, or null when the worker could not be asked at all.</param>
    public static IReadOnlyList<KeyValuePair<string, string>> Summarize(JsonObject? sweep)
    {
        var entries = new List<KeyValuePair<string, string>>(Groups.Length + 1)
        {
            new(PlanKey, Text(sweep?["plan"]) ?? "not swept")
        };

        foreach (var (json, key) in Groups)
        {
            entries.Add(new KeyValuePair<string, string>(key, Describe(sweep?[json] as JsonObject)));
        }

        return entries;
    }

    /// <summary>One class's answers, <c>selector=word</c> pairs in selector order, or that none answered.</summary>
    private static string Describe(JsonObject? answers)
    {
        if (answers == null || answers.Count == 0) return NothingAnswered;

        var pairs = new List<KeyValuePair<string, string>>(answers.Count);
        foreach (var (selector, value) in answers)
        {
            var word = Text(value);
            if (string.IsNullOrEmpty(word)) continue;
            pairs.Add(new KeyValuePair<string, string>(selector, word));
        }

        if (pairs.Count == 0) return NothingAnswered;

        // Sorted ordinals: two captures of the same machine have to produce the same text even when the worker
        // enumerated the selectors in a different order, or the diff reports a change that did not happen.
        pairs.Sort(static (left, right) => string.CompareOrdinal(left.Key, right.Key));
        return string.Join(' ', pairs.Select(static pair => $"{pair.Key}={pair.Value}"));
    }

    /// <summary>The scalar as text: a JSON string loses its quotes, anything else keeps its own form.</summary>
    private static string? Text(JsonNode? node) => node switch
    {
        JsonValue value => value.ToJsonString().Trim('"'),
        _ => null
    };
}
