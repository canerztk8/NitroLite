using System.IO;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Pins the safety boundary a user report forced: on one Acer model the undocumented selector sweep
/// (<c>ProbeModeSelectors</c>, ~127 APGe/WMI pokes) hard-reset the machine when it ran inside "Capture state A".
/// A source-level guard because the real WMI path needs a live elevated worker and cannot run in a unit test: the
/// snapshot sources must simply not reference the sweep at all, so no future edit can wire it back in silently.
/// </summary>
public class SelectorSweepSafetyTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "NitroLite.sln")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void TheWmiSnapshotSourceNeverCallsTheSelectorSweep()
    {
        var path = Path.Combine(RepoRoot(), "NitroLite.Windows", "Core", "Hardware", "Snapshot", "WmiSnapshotSource.cs");
        var source = File.ReadAllText(path);

        Assert.DoesNotContain("ProbeModeSelectorsAsync", source);
        Assert.DoesNotContain("AddSelectorProbeAsync", source);
        Assert.DoesNotContain("ModeSelectorProbe.Summarize", source);
    }

    [Fact]
    public void TheSnapshotEntryPointNeverCallsTheSelectorSweep()
    {
        var path = Path.Combine(RepoRoot(), "NitroLite.Windows", "Core", "Hardware", "Snapshot", "WindowsHardwareSnapshot.cs");
        var source = File.ReadAllText(path);

        Assert.DoesNotContain("ProbeModeSelectors", source);
    }

    [Fact]
    public void OnlyTheConfirmedSweepActionReachesTheProbeBackend()
    {
        // The window may call the sweep only from the confirmation-gated command, so the view model's snapshot
        // methods must not reference it; the one allowed call site is RunSelectorSweepAsync.
        var path = Path.Combine(RepoRoot(), "NitroLite.Desktop", "ViewModels", "MainViewModel.Phase3.cs");
        var source = File.ReadAllText(path);

        var captureA = source.IndexOf("private async Task CaptureSnapshotAAsync()", StringComparison.Ordinal);
        var captureB = source.IndexOf("private async Task CaptureSnapshotBDiffAsync()", StringComparison.Ordinal);
        var captureCore = source.IndexOf("private async Task<HardwareSnapshot?> CaptureHardwareSnapshotAsync()", StringComparison.Ordinal);
        var sweep = source.IndexOf("private async Task RunSelectorSweepAsync()", StringComparison.Ordinal);
        Assert.True(captureA >= 0 && captureB > captureA && captureCore > captureB && sweep > captureCore);

        var between = source[captureA..sweep];
        Assert.DoesNotContain("ProbeModeSelectorsAsync", between);

        var sweepBody = source[sweep..];
        Assert.Contains("ProbeModeSelectorsAsync", sweepBody);
        Assert.Contains("ConfirmAsync", sweepBody);
    }
}
