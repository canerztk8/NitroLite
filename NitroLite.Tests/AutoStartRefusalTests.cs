using System.IO;
using NitroLite.Core;
using NitroLite.Desktop.ViewModels;
using NitroLite.Windows.Core;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Issue 11: the app ran out of Downloads and the elevated autostart was refused, correctly, but the settings page
/// only said it "could not be changed". A refusal the user can fix by moving the app now says so.
/// </summary>
public class AutoStartRefusalTests
{
    private sealed class RefusingAutoStart : IAutoStartService
    {
        public AutoStartRefusal Refusal { get; init; }
        public Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> EnableAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> DisableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public AutoStartRefusal LastRefusal => Refusal;
    }

    private static MainViewModel Create(IAutoStartService autoStart) =>
        new(new AppSettings(), hardware: null, platform: null,
            Path.Combine(Path.GetTempPath(), $"nitrolite_autostart_{Guid.NewGuid():N}.json"), autoStart: autoStart);

    [Theory]
    [InlineData(AutoStartRefusal.DownloadOrTempFolder)]
    [InlineData(AutoStartRefusal.WritableFolder)]
    [InlineData(AutoStartRefusal.None)]
    public async Task ARefusedSwitch_SaysWhyAndWhatToDo(AutoStartRefusal refusal)
    {
        using var vm = Create(new RefusingAutoStart { Refusal = refusal });
        await vm.InitializeAsync();

        vm.AutoStartOn = true;
        await WaitForAsync(() => !string.IsNullOrEmpty(vm.AutoStartStatus));

        var expected = refusal switch
        {
            AutoStartRefusal.DownloadOrTempFolder => vm.Labels.AutoStartRefusedDownloadFolder,
            AutoStartRefusal.WritableFolder => vm.Labels.AutoStartRefusedWritableFolder,
            _ => vm.Labels.AutoStartFailed
        };
        Assert.Equal(expected, vm.AutoStartStatus);
    }

    [Fact]
    public async Task TheService_NamesTheDownloadFolderRefusal()
    {
        var service = new ElevatedStartupService(
            new NoopRunner(),
            isElevated: () => false,
            executablePath: () => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "NitroLite", "NitroLite.Desktop.exe"),
            userId: () => "S-1-5-21-1111111111-2222222222-3333333333-1001",
            registerTask: (_, _, _, _) => "not exercised");

        var result = await service.EnableAsync(startMinimized: false);

        Assert.Equal(ElevatedStartupOutcome.Failed, result.Outcome);
        Assert.Equal(AutoStartRefusal.DownloadOrTempFolder, service.LastRefusal);
    }

    private sealed class NoopRunner : IProcessRunner
    {
        public Task<ProcessRunResult> RunAsync(
            string fileName, string arguments, bool elevate, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProcessRunResult(0, "", "", Started: true));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int i = 0; i < 100 && !condition(); i++) await Task.Delay(20);
    }
}
