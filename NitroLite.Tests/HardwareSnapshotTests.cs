using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Core.Hardware.Snapshot;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The 2-step NitroSense snapshot diff: the hex form the report prints, the comparison of two captures, and the two
/// rules the tool lives or dies by — a throwing read must not lose the capture, and nothing identifying may reach the
/// issue.
/// </summary>
public class HardwareSnapshotTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 11, 19, 19, 6, 36, TimeSpan.Zero);
    private static readonly DateTimeOffset AfterChange = new(2026, 11, 19, 19, 12, 36, TimeSpan.Zero);

    private static HardwareSnapshot Snapshot(
        DateTimeOffset at,
        params (string Section, string Key, string Value)[] entries) =>
        new("AN16-61", "EV9_SKF", entries.Select(e => new HardwareSnapshotEntry(e.Section, e.Key, e.Value)), at);

    // ---------------------------------------------------------------- hex

    [Fact]
    public void HexIsUppercaseSpaceSeparatedAndRoundTrips()
    {
        byte[] bytes = { 0x10, 0xFF, 0x81, 0x00 };

        Assert.Equal("10 FF 81 00", HardwareSnapshotEntry.Hex(bytes));
        Assert.Equal("", HardwareSnapshotEntry.Hex(ReadOnlySpan<byte>.Empty));

        Assert.True(HardwareSnapshotEntry.TryParseHex("10 FF 81 00", out var parsed));
        Assert.Equal(bytes, parsed);

        // A firmware status word is not a byte array, and a half-formed group is not one either.
        Assert.False(HardwareSnapshotEntry.TryParseHex("Static", out _));
        Assert.False(HardwareSnapshotEntry.TryParseHex("10 FF 8", out _));
        Assert.False(HardwareSnapshotEntry.TryParseHex("", out _));
        Assert.False(HardwareSnapshotEntry.TryParseHex(null, out _));
    }

    [Fact]
    public void AnEntryFromBytesIsTheHexFormTheDiffCompares()
    {
        var entry = HardwareSnapshotEntry.FromBytes("hid", "048D:C985/page0xFF00/usage0x0001", new byte[] { 0x10, 0xFF });

        Assert.Equal("hid.048D:C985/page0xFF00/usage0x0001", entry.Label);
        Assert.Equal("10 FF", entry.Value);
        Assert.False(entry.IsUnavailable);
    }

    // ---------------------------------------------------------------- diff

    [Fact]
    public void OnlyTheChangedSelectorIsPrintedAndTheCountsSayWhatItCost()
    {
        var before = Snapshot(Baseline,
            ("wmi", "deviceState.muxRaw", "0"),
            ("wmi", "lighting.config", "Static brightness=100"),
            ("hid", "048D:C985/page0xFF00/usage0x0001", "10 FF 81 00"));
        var after = Snapshot(AfterChange,
            ("wmi", "deviceState.muxRaw", "1"),
            ("wmi", "lighting.config", "Static brightness=100"),
            ("hid", "048D:C985/page0xFF00/usage0x0001", "10 FF 82 00"));

        var diff = HardwareSnapshotDiff.Compare(before, after);

        Assert.Equal(2, diff.Changed);
        Assert.Equal(0, diff.Added);
        Assert.Equal(0, diff.Removed);
        Assert.Equal(1, diff.Unchanged);
        Assert.True(diff.HasChanges);
        Assert.Equal(2, diff.TotalChanges);

        Assert.Contains("~ wmi.deviceState.muxRaw = 0 -> 1", diff.Text, StringComparison.Ordinal);
        Assert.Contains("changed 2, added 0, removed 0, unchanged 1", diff.Text, StringComparison.Ordinal);

        // The selector that answered the same both times is counted, never printed: it would bury the two lines that
        // matter under the two hundred that do not.
        Assert.DoesNotContain("wmi.lighting.config", diff.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AChangedFeatureReportNamesTheBytesThatMoved()
    {
        var before = Snapshot(Baseline, ("hid", "048D:C985/page0xFF00/usage0x0001", "10 FF 81 00"));
        var after = Snapshot(AfterChange, ("hid", "048D:C985/page0xFF00/usage0x0001", "10 FF 82 01"));

        var diff = HardwareSnapshotDiff.Compare(before, after);

        Assert.Contains("(byte 2: 0x81 -> 0x82, byte 3: 0x00 -> 0x01)", diff.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AChangedStatusWordIsNotDescribedAsAByteArray()
    {
        var before = Snapshot(Baseline, ("wmi", "deviceState.operatingMode", "Balanced"));
        var after = Snapshot(AfterChange, ("wmi", "deviceState.operatingMode", "Turbo"));

        var diff = HardwareSnapshotDiff.Compare(before, after);

        Assert.DoesNotContain("byte ", diff.Text, StringComparison.Ordinal);
        Assert.Contains("~ wmi.deviceState.operatingMode = Balanced -> Turbo", diff.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ASelectorThatAppearedOrVanishedIsMarkedAsSuch()
    {
        var before = Snapshot(Baseline, ("wmi", "deviceState.muxRaw", "0"), ("hid", "048D:C985/page0xFF00", "10 FF"));
        var after = Snapshot(AfterChange, ("wmi", "deviceState.muxRaw", "0"), ("wmi", "apge0x207.coolBoost", "True"));

        var diff = HardwareSnapshotDiff.Compare(before, after);

        Assert.Equal(1, diff.Added);
        Assert.Equal(1, diff.Removed);
        Assert.Equal(0, diff.Changed);
        Assert.Contains("+ wmi.apge0x207.coolBoost = True", diff.Text, StringComparison.Ordinal);
        Assert.Contains("- hid.048D:C985/page0xFF00 = 10 FF", diff.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDiffIsTheSameWhateverOrderTheSourcesWalkedTheirDevicesIn()
    {
        var first = Snapshot(Baseline, ("hid", "b", "02"), ("hid", "a", "01"), ("wmi", "c", "03"));
        var second = Snapshot(Baseline, ("wmi", "c", "03"), ("hid", "a", "01"), ("hid", "b", "02"));

        // Two captures of an unchanged machine must produce byte-identical text, or a user's second capture looks like
        // a change that never happened.
        Assert.Equal(first.ToText(), second.ToText());
        Assert.Equal(
            HardwareSnapshotDiff.Compare(first, second).Text,
            HardwareSnapshotDiff.Compare(second, first).Text);
        Assert.False(HardwareSnapshotDiff.Compare(first, second).HasChanges);
    }

    [Fact]
    public void AnUnchangedMachineSaysSoRatherThanPrintingNothing()
    {
        var before = Snapshot(Baseline, ("wmi", "deviceState.muxRaw", "0"));
        var after = Snapshot(AfterChange, ("wmi", "deviceState.muxRaw", "0"));

        var diff = HardwareSnapshotDiff.Compare(before, after);

        Assert.False(diff.HasChanges);
        Assert.Contains(HardwareSnapshotDiff.NoDifferencesNote, diff.Text, StringComparison.Ordinal);
        Assert.Contains("changed 0, added 0, removed 0, unchanged 1", diff.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingCaptureIsNotADiffAgainstNothing()
    {
        var snapshot = Snapshot(Baseline, ("wmi", "deviceState.muxRaw", "0"));

        Assert.Empty(HardwareSnapshotDiff.Compare(null, snapshot).Text);
        Assert.Empty(HardwareSnapshotDiff.Compare(snapshot, null).Text);
        Assert.False(HardwareSnapshotDiff.Compare(null, snapshot).HasChanges);
    }

    [Fact]
    public void AnOverlongDiffIsCutWithANoteRatherThanSilently()
    {
        var beforeEntries = Enumerable.Range(0, 200)
            .Select(i => ("wmi", $"selector{i:D3}", new string('0', 40)))
            .ToArray();
        var afterEntries = Enumerable.Range(0, 200)
            .Select(i => ("wmi", $"selector{i:D3}", new string('1', 40)))
            .ToArray();

        var diff = HardwareSnapshotDiff.Compare(Snapshot(Baseline, beforeEntries), Snapshot(AfterChange, afterEntries));
        Assert.True(diff.Text.Length <= HardwareSnapshotDiff.MaximumTextLength,
            $"the diff is {diff.Text.Length} characters");
        Assert.EndsWith(HardwareSnapshotDiff.TruncatedNote, diff.Text, StringComparison.Ordinal);
        Assert.Equal(200, diff.Changed);
    }

    // ---------------------------------------------------------------- privacy

    [Fact]
    public void NoIdentifierSurvivesTheCaptureOrTheDiff()
    {
        // Three things a firmware or a driver has been seen to answer with: an asset tag, a profile path and a
        // hardware UUID. None of them may reach the issue the button opens.
        var before = Snapshot(Baseline,
            ("wmi", "deviceState.asset", "SerialNumber=PF2AB123 mode=balanced"));
        var after = Snapshot(AfterChange,
            ("wmi", "deviceState.asset", "SerialNumber=PF2AB124 mode=turbo"),
            ("hid", "048D:C985/page0xFF00", @"path=C:\Users\caner\AppData\Local\x"),
            ("hid", "048D:C985/page0xFF01", "uuid 3F2504E0-4F89-11D3-9A0C-0305E82C3301"));

        var diff = HardwareSnapshotDiff.Compare(before, after);
        var text = diff.Text.ToLowerInvariant();

        Assert.DoesNotContain("pf2ab12", text, StringComparison.Ordinal);
        Assert.DoesNotContain("caner", text, StringComparison.Ordinal);
        Assert.DoesNotContain("3f2504e0", text, StringComparison.Ordinal);
        Assert.Contains(SnapshotRedaction.ValuePlaceholder, diff.Text, StringComparison.Ordinal);
        Assert.Contains(Redaction.UserPlaceholder, diff.Text, StringComparison.Ordinal);
        Assert.Contains(SnapshotRedaction.UuidPlaceholder, diff.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDiffRidesAlongInACompatibilityIssueTheBuilderAccepts()
    {
        var before = Snapshot(Baseline, ("wmi", "deviceState.muxRaw", "0"));
        var after = Snapshot(AfterChange, ("wmi", "deviceState.muxRaw", "1"));

        var diff = HardwareSnapshotDiff.Compare(before, after);
        var url = CompatibilityReportUrl.Build("AN16-61", "EV9_SKF", diff.Text);

        Assert.True(CompatibilityReportUrl.IsCompatibilityUrl(url));
        Assert.True(url.Length <= CompatibilityReportUrl.MaximumUrlLength);
        Assert.Contains(Uri.EscapeDataString("~ wmi.deviceState.muxRaw = 0 -> 1"), url, StringComparison.Ordinal);
        Assert.Contains(Uri.EscapeDataString("[Compatibility] AN16-61 (EV9_SKF)"), url, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- a read that throws

    private sealed class ThrowingSource(string section, Exception error) : IHardwareSnapshotSource
    {
        public string Section => section;

        public Task<IReadOnlyList<HardwareSnapshotEntry>> CaptureAsync(CancellationToken cancellationToken) =>
            throw error;
    }

    private sealed class FixedSource(string section, params HardwareSnapshotEntry[] entries) : IHardwareSnapshotSource
    {
        public string Section => section;

        public Task<IReadOnlyList<HardwareSnapshotEntry>> CaptureAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<HardwareSnapshotEntry>>(entries);
    }

    [Fact]
    public async Task AThrowingSourceLosesItsOwnHalfAndNotTheWholeCapture()
    {
        var snapshot = await HardwareSnapshotService.CaptureAsync(
            "AN16-61",
            "EV9_SKF",
            [
                new ThrowingSource("wmi", new InvalidOperationException("the worker is not running")),
                new FixedSource("hid", new HardwareSnapshotEntry("hid", "048D:C985/page0xFF00", "10 FF"))
            ],
            Baseline);

        Assert.Equal(2, snapshot.Count);

        var refusal = snapshot.Entries.Single(e => e.Section == "wmi");
        Assert.True(refusal.IsUnavailable);
        Assert.Contains("the worker is not running", refusal.Value, StringComparison.Ordinal);

        // The half that answered is still there, which is the difference between a partial report and no report.
        Assert.Contains(snapshot.Entries, e => e.Value == "10 FF");
    }

    [Fact]
    public async Task AThrowingSelectorBecomesAnUnavailableEntryInsteadOfAReason()
    {
        var snapshot = await HardwareSnapshotService.CaptureAsync(
            "AN16-61",
            "EV9_SKF",
            [
                new FixedSource("hid",
                    await HardwareSnapshotService.ReadAsync(
                        "hid",
                        "048D:C985/page0xFF00",
                        () => Task.FromException<string?>(new TimeoutException("HidD_GetFeature timed out"))),
                    await HardwareSnapshotService.ReadAsync("hid", "048D:C985/page0xFF01", () => Task.FromResult<string?>("10 FF")))
            ],
            Baseline);

        Assert.Equal(2, snapshot.Count);
        Assert.True(snapshot.Entries[0].IsUnavailable);
        Assert.Contains("TimeoutException", snapshot.Entries[0].Value, StringComparison.Ordinal);
        Assert.Equal("10 FF", snapshot.Entries[1].Value);
    }

    [Fact]
    public async Task ASourceThatReadsNothingIsReportedRatherThanDisappearing()
    {
        var snapshot = await HardwareSnapshotService.CaptureAsync(
            "AN16-61", "EV9_SKF", [new FixedSource("wmi")], Baseline);

        Assert.Single(snapshot.Entries);
        Assert.True(snapshot.Entries[0].IsUnavailable);
        Assert.Equal("wmi", snapshot.Entries[0].Section);
    }

    [Fact]
    public async Task AnEmptyCaptureIsFlaggedSoTheUiDoesNotTreatItAsAMachine()
    {
        var snapshot = await HardwareSnapshotService.CaptureAsync("AN16-61", "EV9_SKF", [], Baseline);

        Assert.True(snapshot.IsEmpty);
        Assert.Equal("AN16-61 (EV9_SKF)", snapshot.Describe());
        Assert.Equal("AN16-61", HardwareSnapshot.Empty("AN16-61").Describe());
    }

    // ---------------------------------------------------------------- which HID interfaces may be read

    [Theory]
    [InlineData(0x048D, 0xFF00, 0x0001, true)]   // ITE Tech, vendor lighting page
    [InlineData(0x0C45, 0xFF00, 0x0001, true)]   // Sonix
    [InlineData(0x04F2, 0xFF03, 0x0001, true)]   // Chicony, another lighting page in the documented range
    [InlineData(0x048D, 0x01, 0x06, false)]      // the keyboard the user types through: never opened
    [InlineData(0x048D, 0x01, 0x02, false)]      // a mouse on the same vendor id
    [InlineData(0x048D, 0x07, 0x01, false)]      // the LED page belongs to that keyboard
    [InlineData(0x048D, 0x0C, 0x01, false)]      // consumer control
    [InlineData(0x046D, 0xFF00, 0x0001, false)]  // a vendor this build does not drive
    [InlineData(0x048D, 0xFF09, 0x0001, false)]  // outside the lighting page range
    public void OnlyTheKnownLightingInterfacesAreEligible(int vendorId, int usagePage, int usage, bool expected)
    {
        Assert.Equal(expected, HidSnapshotFilter.IsEligible(vendorId, usagePage, usage));
    }

    [Fact]
    public void AStandardKeyboardIsRefusedByPageWhateverTheVendor()
    {
        Assert.True(HidSnapshotFilter.IsStandardKeyboardInterface(HidSnapshotFilter.GenericDesktopPage, HidSnapshotFilter.KeyboardUsage));
        Assert.False(HidSnapshotFilter.IsEligible(0x048D, 0x01, 0x06));
        Assert.False(HidSnapshotFilter.IsEligible(0x0C45, 0x01, 0x06));
        Assert.False(HidSnapshotFilter.IsEligible(0x04F2, 0x01, 0x06));
    }

    [Fact]
    public void AnInterfaceKeyNamesTheVendorProductPageAndUsage()
    {
        Assert.Equal(
            "048D:C985/page0xFF00/usage0x0001",
            HidSnapshotFilter.Key(0x048D, 0xC985, 0xFF00, 0x0001));
    }
}
