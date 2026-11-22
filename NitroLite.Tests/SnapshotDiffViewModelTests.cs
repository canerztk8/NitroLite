using System.IO;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Core.Hardware.Snapshot;
using NitroLite.Desktop;
using NitroLite.Desktop.ViewModels;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The window half of the 2-step NitroSense diff: the second button stays locked until A is in hand, and the diff that
/// reaches the clipboard and the issue is the one the pair produced.
/// </summary>
public class SnapshotDiffViewModelTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 11, 19, 19, 6, 36, TimeSpan.Zero);
    private static readonly DateTimeOffset AfterChange = new(2026, 11, 19, 19, 12, 36, TimeSpan.Zero);

    /// <summary>A platform that can take a snapshot: one selector, answered from a queue the test fills.</summary>
    private sealed class FakePlatform(params string[] values) : IPlatformSnapshot, IHardwareSnapshotBackend
    {
        private readonly Queue<string> _values = new(values);

        public int Captures { get; private set; }

        public DeviceIdentity Identity { get; } = new()
        {
            Manufacturer = "Acer",
            Model = "Nitro AN16-61",
            ModelCode = "AN16-61",
            BoardProduct = "EV9_SKF",
            BiosVersion = "1.53"
        };

        public string Tier => "wmi";
        public bool NeedsUdevRule => false;
        public IReadOnlyList<string> Notes => Array.Empty<string>();
        public bool NegativeAnswersAreConclusive => false;

        public string BuildHardwareReport(HardwareReportContext? context = null) =>
            "# NitroLite hardware report\nModel code: AN16-61";

        public Task<HardwareSnapshot> CaptureSnapshotAsync(CancellationToken cancellationToken = default)
        {
            Captures++;
            var value = _values.Count > 0 ? _values.Dequeue() : "0";
            return Task.FromResult(new HardwareSnapshot(
                "AN16-61",
                "EV9_SKF",
                [new HardwareSnapshotEntry("wmi", "deviceState.muxRaw", value)],
                Captures == 1 ? Baseline : AfterChange));
        }
    }

    private static (MainViewModel Vm, FakePlatform Platform, Phase3Fakes.UrlCapturer Urls) Create(params string[] values)
    {
        var platform = new FakePlatform(values);
        var urls = new Phase3Fakes.UrlCapturer();
        var path = Path.Combine(Path.GetTempPath(), $"nitrolite-snapshot-{Guid.NewGuid():N}.json");
        var vm = new MainViewModel(new AppSettings(), null, platform, path, urlOpener: urls.Capture);
        return (vm, platform, urls);
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25).ConfigureAwait(true);
        }

        Assert.True(condition(), "the view model did not reach the expected state in time");
    }

    [Fact]
    public async Task TheSecondButtonStaysLockedUntilStateAIsCaptured()
    {
        var (vm, platform, urls) = Create("0", "1");
        var previousClipboard = ClipboardService.Setter;
        var copied = new List<string>();
        ClipboardService.Setter = copied.Add;

        try
        {
            using (vm)
            {
                Assert.True(vm.SnapshotDiffAvailable);
                Assert.False(vm.SnapshotBaselineReady);
                Assert.False(vm.CaptureSnapshotBDiffCommand.CanExecute(null));
                Assert.True(vm.CaptureSnapshotACommand.CanExecute(null));

                vm.CaptureSnapshotACommand.Execute(null);
                await WaitForAsync(() => vm.SnapshotBaselineReady);

                Assert.Equal(1, platform.Captures);
                Assert.Equal(vm.Labels.SnapshotCapturedA(1), vm.SnapshotDiffStatus);
                Assert.True(vm.CaptureSnapshotBDiffCommand.CanExecute(null));

                await vm.CaptureSnapshotBDiffCommand.ExecuteAsync(null);

                // The complete report can be pasted into the existing support conversation.
                Assert.Equal(vm.Labels.SnapshotDiffCaptured(1), vm.SnapshotDiffStatus);
                Assert.Single(copied);
                Assert.Contains("~ wmi.deviceState.muxRaw = 0 -> 1", copied[0], StringComparison.Ordinal);
                Assert.Contains("# NitroLite hardware report", copied[0], StringComparison.Ordinal);

                Assert.Empty(urls.OpenedUrls);
            }
        }
        finally
        {
            ClipboardService.Setter = previousClipboard;
        }
    }

    [Fact]
    public async Task ACaptureThatMovedNothingSaysSoInsteadOfClaimingADifference()
    {
        var (vm, _, urls) = Create("0", "0");
        var previousClipboard = ClipboardService.Setter;
        var copied = new List<string>();
        ClipboardService.Setter = copied.Add;

        try
        {
            using (vm)
            {
                vm.CaptureSnapshotACommand.Execute(null);
                await WaitForAsync(() => vm.SnapshotBaselineReady);

                await vm.CaptureSnapshotBDiffCommand.ExecuteAsync(null);

                Assert.Equal(vm.Labels.SnapshotDiffSame, vm.SnapshotDiffStatus);
                Assert.Single(copied);
                Assert.Contains(HardwareSnapshotDiff.NoDifferencesNote, copied[0], StringComparison.Ordinal);
            }
        }
        finally
        {
            ClipboardService.Setter = previousClipboard;
        }
    }

    [Fact]
    public void APlatformWithoutTheSnapshotBackendOffersNeitherButton()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nitrolite-snapshot-{Guid.NewGuid():N}.json");
        using var vm = new MainViewModel(new AppSettings(), null, null, path);

        Assert.False(vm.SnapshotDiffAvailable);
        Assert.False(vm.CaptureSnapshotACommand.CanExecute(null));
        Assert.False(vm.CaptureSnapshotBDiffCommand.CanExecute(null));

        // A click that reaches the command anyway (a stale binding) is refused by the command itself, so nothing is
        // captured and no status line is written.
        vm.CaptureSnapshotACommand.Execute(null);
        Assert.False(vm.SnapshotBaselineReady);
        Assert.False(vm.HasSnapshotDiffStatus);
    }
}
