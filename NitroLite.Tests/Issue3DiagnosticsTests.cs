using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Core.Hardware.Wmi;
using NitroLite.Desktop.ViewModels;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Issue 3 (AN16-61): the report carries the MUX selector's current word, and the dashboard says when the hardware
/// helper was refused because of the install folder.
/// </summary>
public class Issue3DiagnosticsTests
{
    private sealed class FakeDirectWmi : IDirectWmiControl
    {
        public bool IsAvailable { get; set; }
        public bool Refused { get; set; }
        public string StatusText => Refused ? "folder" : "service";
        public bool HelperFolderRefused => Refused;
        public Task<bool> StartAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    [Fact]
    public void TheReport_DecodesAMuxWordInRange_AndPrintsOneOutsideItAsItIs()
    {
        var optimus = AcerWmiProtocol.EncodeMuxMode(GpuOperatingMode.Optimus);

        Assert.Equal($"0x{optimus:X4} -> Optimus", HardwareReport.MuxWordText(optimus));
        Assert.Equal("0x0001 (not a mode this build maps)", HardwareReport.MuxWordText(0x0001));
        Assert.Equal("not read this session", HardwareReport.MuxWordText(null));
    }

    [Fact]
    public async Task TheDashboard_ShowsTheFolderWarning_OnlyWhileTheHelperIsRefusedForIt()
    {
        var settings = new AppSettings();
        var wmi = new FakeDirectWmi { Refused = true };
        using var vm = new MainViewModel(settings, null, null, AppPaths.GetDataFile("settings.json"), directWmi: wmi);
        await vm.InitializeAsync();

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await vm.RefreshTelemetryAsync();
        Assert.True(vm.ShowHelperFolderWarning);
        Assert.Contains(nameof(MainViewModel.ShowHelperFolderWarning), raised);
        Assert.False(string.IsNullOrWhiteSpace(vm.Labels.HelperFolderWarningBody));

        // Moved or started as administrator: the warning goes away on the next tick.
        wmi.Refused = false;
        await vm.RefreshTelemetryAsync();
        Assert.False(vm.ShowHelperFolderWarning);
    }

    [Fact]
    public async Task NoDirectWmi_NoWarning()
    {
        using var vm = new MainViewModel(new AppSettings(), null, null, AppPaths.GetDataFile("settings.json"));
        await vm.InitializeAsync();
        await vm.RefreshTelemetryAsync();

        Assert.False(vm.ShowHelperFolderWarning);
    }
}
