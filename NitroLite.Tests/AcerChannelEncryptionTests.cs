using System.IO;
using System.Text.RegularExpressions;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Core.Hardware.Wmi;
using NitroLite.Windows;
using NitroLite.Windows.Core;
using NitroLite.Windows.Core.Hardware;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// SupportAES: when Acer's agent encrypts its TCP channel NitroLite cannot speak to it, so the client must say "not
/// connected" at once instead of timing out, and the report and the dump must say why. The rescan button must also
/// clear every cache that could keep a stale answer alive.
/// </summary>
public class AcerChannelEncryptionTests
{
    private static AcerRegistrySnapshot Registry(int? supportAes, bool keyPresent = true) =>
        new() { KeyPresent = keyPresent, SupportAes = supportAes };

    private static AcerAgentClient Client(int port, AcerRegistrySnapshot registry) =>
        new("127.0.0.1", port, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(300),
            TimeSpan.FromMilliseconds(300), () => registry);

    // ---------------------------------------------------------------- the registry value

    [Theory]
    [InlineData(null, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void Registry_OnlyANonZeroSupportAes_MeansEncrypted(int? value, bool expected) =>
        Assert.Equal(expected, Registry(value).ChannelEncrypted);

    // ---------------------------------------------------------------- the client

    [Fact]
    public async Task Encrypted_NeverOpensASocket_AndReportsNotConnected()
    {
        int before = DeadEndService.Connections;
        using var client = Client(DeadEndService.Port, Registry(1));

        Assert.True(client.ChannelEncrypted);
        Assert.False(await client.EnsureConnectedAsync());
        Assert.False(client.IsConnected);
        Assert.Equal(before, DeadEndService.Connections);
    }

    [Fact]
    public async Task Encrypted_CapabilitiesComeBackAsNoAnswer()
    {
        int before = DeadEndService.Connections;
        using var client = Client(DeadEndService.Port, Registry(1));
        using var controller = new AgentHardwareController(client, new CareCenterClient());

        var caps = await controller.GetCapabilitiesAsync();

        Assert.False(caps.ServiceAvailable);
        Assert.False(controller.IsAvailable);
        Assert.Equal(before, DeadEndService.Connections);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public async Task NotEncrypted_StillConnects(int? supportAes)
    {
        using var service = new AgentCapabilityDumpTests.EchoService(function =>
            $"{{\"request\":\"{function}\",\"result\":\"0\",\"data\":1}}");
        using var client = Client(service.Port, Registry(supportAes));

        Assert.False(client.ChannelEncrypted);
        Assert.True(await client.EnsureConnectedAsync());
    }

    // ---------------------------------------------------------------- the dump

    [Fact]
    public async Task Dump_SaysTheChannelIsEncrypted_AndAsksNothing()
    {
        using var service = new AgentCapabilityDumpTests.EchoService(_ => "{\"result\":\"0\"}");
        using var client = Client(service.Port, Registry(1));
        using var output = new StringWriter();

        Assert.Equal(1, await AgentCapabilityDump.RunAsync(client, output));

        Assert.Contains("encrypted", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SupportAES", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(service.Packets);
    }

    // ---------------------------------------------------------------- the report

    [Fact]
    public void Report_HasAChannelRow_WhenEncrypted()
    {
        var probe = new CapabilitySnapshot { CacheKey = "X|1", Registry = Registry(1) };

        var markdown = HardwareReport.BuildMarkdown(probe);

        Assert.Contains("| Acer service channel | encrypted (SupportAES set); NitroLite uses WMI |", markdown);
    }

    [Fact]
    public void Report_ContextOverridesTheProbe_ForACachedSnapshot()
    {
        var probe = new CapabilitySnapshot { CacheKey = "X|1", FromCache = true };

        var encrypted = HardwareReport.BuildMarkdown(probe, context: new HardwareReportContext { AcerChannelEncrypted = true });
        var plain = HardwareReport.BuildMarkdown(probe, context: new HardwareReportContext { AcerChannelEncrypted = false });
        var unknown = HardwareReport.BuildMarkdown(probe);

        Assert.Contains("| Acer service channel | encrypted", encrypted);
        Assert.Contains("| Acer service channel | plaintext |", plain);
        Assert.Contains("| Acer service channel | not read (cached snapshot) |", unknown);
    }

    [Fact]
    public void Report_NeverPrintsTheAesKey()
    {
        var markdown = HardwareReport.BuildMarkdown(
            new CapabilitySnapshot { CacheKey = "X|1", Registry = Registry(1) });

        Assert.DoesNotContain("AESkey", markdown, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- rescan

    [Fact]
    public void Rescan_ClearsTheSavedCapabilityCache()
    {
        var settings = new AppSettings { HardwareCapabilityCache = new HardwareCapabilityCache { Key = "A|1" } };

        CapabilityRescan.Reset(settings, null);

        Assert.Null(settings.HardwareCapabilityCache);
    }

    [Fact]
    public void Rescan_SnapshotRunsTheRescanCallback_AndKeepsTheReadBackReset()
    {
        var settings = new AppSettings();
        settings.CapabilitySessionFailures[ReadBackPolicy.FanSpeed] = 2;
        var readBack = new ReadBackPolicy(settings, _ => Task.CompletedTask);
        int runs = 0;
        var snapshot = new WindowsPlatformSnapshot(new DeviceIdentity(), () => null, () => "none", readBack,
            onRescan: () => runs++);

        ((IRescannableBackend)snapshot).ResetReadBackCounters();

        Assert.Equal(1, runs);
        Assert.Empty(settings.CapabilitySessionFailures);
    }

    [Fact]
    public void Rescan_IsWiredFromTheSettingsCommandToBothCaches()
    {
        var root = FindRoot();
        var appHost = File.ReadAllText(Path.Combine(root, "NitroLite.Desktop", "AppHost.cs"));
        var viewModel = File.ReadAllText(Path.Combine(root, "NitroLite.Desktop", "ViewModels", "MainViewModel.cs"));

        // AppHost hands the snapshot a callback that clears both caches and starts a fresh probe...
        Assert.Matches(new Regex(@"new WindowsPlatformSnapshot\([^;]*onRescan:"), appHost);
        Assert.Contains("CapabilityRescan.Reset(settings, agent)", appHost);
        // Without the fresh probe the WMI/registry snapshot of the session would survive the rescan.
        Assert.Contains("probeTask = StartProbe()", appHost);
        // ...and the settings command runs the reset and then saves the (now empty) cache atomically.
        Assert.Matches(new Regex(@"RescanAsync\(\)[\s\S]*?ResetReadBackCounters[\s\S]*?SaveSettings\(\)[\s\S]*?RefreshCapabilitiesAsync"),
            viewModel);
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "NitroLite.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
