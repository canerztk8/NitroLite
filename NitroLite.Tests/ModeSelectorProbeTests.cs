using System.Text.Json.Nodes;
using NitroLite.Core.Hardware.Snapshot;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The read-only selector sweep: the answer that names the selector a model's operating mode really lives in, and the
/// two rules it has to keep — a capture stays inside the entry cap, and the text is deterministic so a diff shows a
/// real change instead of the worker's walk order.
/// </summary>
public class ModeSelectorProbeTests
{
    private static JsonObject Sweep(
        JsonObject? misc = null,
        JsonObject? profile = null,
        JsonObject? apge = null) => new()
    {
        ["plan"] = "misc 0x00-0x1F, profile 0x00-0x1F, apge 0x01-0x0F+0x200-0x22F",
        ["misc"] = misc ?? new JsonObject(),
        ["profile"] = profile ?? new JsonObject(),
        ["apge"] = apge ?? new JsonObject()
    };

    [Fact]
    public void Summarize_PrintsThePlanFirstThenOneLinePerClass()
    {
        var entries = ModeSelectorProbe.Summarize(Sweep(
            misc: new JsonObject { ["0x0B"] = "0x0000000000000100" },
            apge: new JsonObject { ["0x207"] = "0x0000000000000001" }));

        Assert.Equal(
            new[] { ModeSelectorProbe.PlanKey, ModeSelectorProbe.MiscKey, ModeSelectorProbe.ProfileKey, ModeSelectorProbe.ApgeKey },
            entries.Select(e => e.Key));

        Assert.Contains("misc 0x00-0x1F", entries[0].Value, StringComparison.Ordinal);
        Assert.Equal("0x0B=0x0000000000000100", entries[1].Value);
        Assert.Equal("none answered", entries[2].Value);
        Assert.Equal("0x207=0x0000000000000001", entries[3].Value);
    }

    [Fact]
    public void Summarize_SortsSelectorsRegardlessOfTheWorkersWalkOrder()
    {
        // Descending on purpose: the worker enumerates a dictionary, and the report must not inherit that order.
        var descending = ModeSelectorProbe.Summarize(Sweep(misc: new JsonObject
        {
            ["0x1F"] = "0x0000000000002200",
            ["0x0A"] = "0x0000000000730000",
            ["0x02"] = "0x0000000000000E00"
        }));

        var ascending = ModeSelectorProbe.Summarize(Sweep(misc: new JsonObject
        {
            ["0x02"] = "0x0000000000000E00",
            ["0x0A"] = "0x0000000000730000",
            ["0x1F"] = "0x0000000000002200"
        }));

        Assert.Equal(ascending[1].Value, descending[1].Value);
        Assert.Equal(
            "0x02=0x0000000000000E00 0x0A=0x0000000000730000 0x1F=0x0000000000002200",
            ascending[1].Value);
    }

    [Fact]
    public void Summarize_NoSweepAtAllSaysSoRatherThanPrintingAnEmptyAnswer()
    {
        var entries = ModeSelectorProbe.Summarize(null);

        Assert.Equal("not swept", entries[0].Value);
        Assert.All(entries.Skip(1), entry => Assert.Equal(ModeSelectorProbe.NothingAnswered, entry.Value));
    }

    [Fact]
    public void Summarize_IgnoresSomethingThatIsNotAnObject()
    {
        var sweep = new JsonObject
        {
            ["plan"] = "misc 0x00-0x1F",
            ["misc"] = "not an object",
            ["profile"] = null,
            ["apge"] = new JsonObject()
        };

        var entries = ModeSelectorProbe.Summarize(sweep);

        Assert.All(entries.Skip(1), entry => Assert.Equal(ModeSelectorProbe.NothingAnswered, entry.Value));
    }

    [Fact]
    public void Capture_KeepsEveryProbeLineInsideTheEntryCap()
    {
        // The worst case the cap exists for: a firmware that answers every selector. One line per class, never one line
        // per selector, or a hundred-and-twenty-seven-word sweep would push the real findings off the end of the report.
        var misc = new JsonObject();
        for (int selector = 0; selector <= 0x1F; selector++)
            misc[$"0x{selector:X2}"] = $"0x{(ulong)selector:X16}";

        var entries = ModeSelectorProbe.Summarize(Sweep(misc: misc, profile: (JsonObject)misc.DeepClone(), apge: (JsonObject)misc.DeepClone()));
        var snapshot = new HardwareSnapshot(
            "AN16-61",
            "EV9_SKF",
            entries.Select(entry => new HardwareSnapshotEntry("wmi", entry.Key, entry.Value)));

        Assert.Equal(entries.Count, snapshot.Entries.Count);
        Assert.Contains(snapshot.Entries, entry => entry.Key == ModeSelectorProbe.MiscKey);
    }

    [Fact]
    public void Diff_ASelectorThatMovedInTheSweepIsOneChangedLine()
    {
        HardwareSnapshot Capture(string word)
        {
            var entries = ModeSelectorProbe.Summarize(Sweep(misc: new JsonObject { ["0x0B"] = word }));
            return new HardwareSnapshot(
                "AN16-61",
                "EV9_SKF",
                entries.Select(entry => new HardwareSnapshotEntry("wmi", entry.Key, entry.Value)));
        }

        var diff = HardwareSnapshotDiff.Compare(Capture("0x0000000000000100"), Capture("0x0000000000000500"));

        Assert.Equal(1, diff.Changed);
        Assert.Contains(ModeSelectorProbe.MiscKey, diff.Text, StringComparison.Ordinal);
        Assert.Contains("0x0000000000000500", diff.Text, StringComparison.Ordinal);
    }
}
