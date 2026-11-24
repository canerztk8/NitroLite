using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Core.Hardware.Wmi;
using NitroLite.Linux;
using NitroLite.Linux.Sysfs;
using Xunit;

namespace NitroLite.Tests;

/// <summary>Audit group G4: the verified Linux defects, each with the test that would have caught it.</summary>
public class AuditG4LinuxTests
{
    private static ProfileCatalog Catalog() =>
        ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _);

    private static InMemorySysfsFileSystem Machine()
    {
        var fs = new InMemorySysfsFileSystem();
        fs.WithFile("/sys/class/dmi/id/sys_vendor", "Acer\n");
        fs.WithFile("/sys/class/dmi/id/product_name", "Nitro AN16-41\n");
        fs.WithFile("/sys/class/dmi/id/board_name", "Sierra_PEH\n");
        fs.WithFile("/proc/sys/kernel/osrelease", "7.4.0-1-generic\n");
        fs.WithDirectory(LinuxPaths.AcerWmiModule);
        fs.WithFile(LinuxPaths.PlatformProfile, "balanced\n");
        fs.WithFile(LinuxPaths.PlatformProfileChoices, "low-power quiet balanced balanced-performance performance\n");
        return fs;
    }

    private static InMemorySysfsFileSystem KernelFan()
    {
        var fs = Machine();
        const string acer = "/sys/class/hwmon/hwmon4";
        fs.WithDirectory(acer);
        fs.WithFile(acer + "/name", "acer\n");
        fs.WithFile(acer + "/temp1_input", "61000\n");
        fs.WithFile(acer + "/fan1_input", "2436\n");
        fs.WithFile(acer + "/fan2_input", "2409\n");
        fs.WithFile(acer + "/pwm1", "0\n");
        fs.WithFile(acer + "/pwm2", "0\n");
        fs.WithFile(acer + "/pwm1_enable", "2\n");
        fs.WithFile(acer + "/pwm2_enable", "2\n");
        return fs;
    }

    private static LinuxSysfsHardwareController Controller(InMemorySysfsFileSystem fs, AppSettings? settings = null) =>
        new(fs, new LinuxCapabilityProbe(fs, Catalog()).Probe(),
            new ReadBackPolicy(settings ?? new AppSettings(), _ => Task.CompletedTask));

    private sealed class Runner : ICommandRunner
    {
        public List<(string File, IReadOnlyList<string> Args)> Calls { get; } = new();
        public string? Find(string name) => name switch { "pkexec" => "/usr/bin/pkexec", "sh" => "/bin/sh", _ => null };

        public CommandResult Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout)
        {
            Calls.Add((fileName, arguments.ToArray()));
            return new CommandResult(0, "", "");
        }
    }

    // ---------------------------------------------------------------- a. the grant script never trusts the user's file

    [Fact]
    public void GrantScript_CopiesTheRuleToARootOnlyDirectoryAndChecksItsDigestBeforeInstalling()
    {
        var script = LinuxPlatformSetup.GrantScript;

        Assert.DoesNotContain("install -m 644 \"$rule\"", script);
        Assert.Contains("install -m 644 \"$dir/rule\" /etc/udev/rules.d/99-nitrolite.rules", script);
        Assert.Contains("mktemp -d", script);
        Assert.Contains("sha256sum -c --status", script);

        // The digest check sits between the copy and the install, so nothing unverified can reach /etc.
        Assert.True(script.IndexOf("cp -- \"$rule\"", StringComparison.Ordinal) <
                    script.IndexOf("sha256sum -c", StringComparison.Ordinal));
        Assert.True(script.IndexOf("sha256sum -c", StringComparison.Ordinal) <
                    script.IndexOf("install -m 644", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Setup_PassesTheDigestOfTheRuleItJustRead_AsTheThirdArgument()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nl-rule-{Guid.NewGuid():N}.rules");
        File.WriteAllText(path, "SUBSYSTEM==\"x\", MODE=\"0660\"\n");
        try
        {
            var runner = new Runner();
            var caps = new LinuxCapabilityProbe(Machine(), Catalog()).Probe();
            var setup = new LinuxPlatformSetup(caps, () => path, runner, new InMemorySysfsFileSystem(), user: "u");

            Assert.True((await setup.GrantAccessAsync()).Ok);

            var call = Assert.Single(runner.Calls);
            Assert.Equal(path, call.Args[5]);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(), call.Args[6]);
            Assert.Equal(64, call.Args[6].Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Setup_WithAnEmbeddedDigest_ASwappedFileDoesNotMatchWhatRootChecks()
    {
        const string trusted = "SUBSYSTEM==\"x\", MODE=\"0660\"\n";
        var path = Path.Combine(Path.GetTempPath(), $"nl-rule-{Guid.NewGuid():N}.rules");
        File.WriteAllText(path, "SUBSYSTEM==\"x\", RUN+=\"/tmp/evil\"\n"); // swapped before the click
        try
        {
            var trustedDigest = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(trusted))).ToLowerInvariant();
            var runner = new Runner();
            var caps = new LinuxCapabilityProbe(Machine(), Catalog()).Probe();
            var setup = new LinuxPlatformSetup(caps, () => path, runner, new InMemorySysfsFileSystem(), user: "u",
                ruleDigest: () => trustedDigest);

            await setup.GrantAccessAsync();

            var args = Assert.Single(runner.Calls).Args;
            Assert.Equal(trustedDigest, args[6]);
            // Root will hash its copy of the swapped file, which cannot equal the embedded digest.
            Assert.NotEqual(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(), args[6]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Setup_AnEmbeddedDigestProviderReturningNull_PassesAnEmptyDigest()
    {
        var runner = new Runner();
        var caps = new LinuxCapabilityProbe(Machine(), Catalog()).Probe();
        var setup = new LinuxPlatformSetup(caps, () => "/x/r", runner, new InMemorySysfsFileSystem(), user: "u",
            ruleDigest: () => null);

        await setup.GrantAccessAsync();

        Assert.Equal("", Assert.Single(runner.Calls).Args[6]);
    }

    [Fact]
    public async Task Setup_AnUnreadableRule_GivesAnEmptyDigest_WhichTheScriptRefuses()
    {
        var runner = new Runner();
        var caps = new LinuxCapabilityProbe(Machine(), Catalog()).Probe();
        var setup = new LinuxPlatformSetup(caps, () => "/nonexistent/99-nitrolite.rules", runner,
            new InMemorySysfsFileSystem(), user: "u");

        await setup.GrantAccessAsync();

        Assert.Equal("", Assert.Single(runner.Calls).Args[6]);
        Assert.Contains("case \"$sha\" in ''|*[!0-9a-f]*) exit 3", LinuxPlatformSetup.GrantScript);
    }

    // ---------------------------------------------------------------- b. the helper client does not cache a failure

    private sealed class Gate : ICallerAuthorizer
    {
        public volatile bool Allow;
        public bool IsAuthorized(Socket client) => Allow;
    }

    private static (Socket Listener, string Path) Listen()
    {
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        var path = Path.Combine(Path.GetTempPath(), $"nl-g4-{Guid.NewGuid():N}.sock");
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(8);
        return (listener, path);
    }

    private static LinuxHelperPolicy PolicyFor(InMemorySysfsFileSystem fs) =>
        LinuxHelperPolicy.For(new LinuxCapabilityProbe(fs, Catalog()).Probe(), fs);

    [Fact]
    public void HelperClient_WithNoSocket_IsUnreachable_NotNotAuthorized()
    {
        using var client = new LinuxHelperClient(Path.Combine(Path.GetTempPath(), $"nl-none-{Guid.NewGuid():N}.sock"));

        Assert.Empty(client.Allowed);
        Assert.Equal(HelperState.Unreachable, client.State);
    }

    [Fact]
    public async Task HelperClient_NotAuthorizedIsNotCached_AndIsToldApartFromAnEmptyList()
    {
        var (listener, socketPath) = Listen();
        using var _ = listener;
        var fs = KernelFan();
        var gate = new Gate();
        using var server = new LinuxHelperServer(listener, PolicyFor(fs), fs, gate);
        using var stop = new CancellationTokenSource();
        var serving = server.RunAsync(stop.Token);
        try
        {
            long now = 1_000;
            using var client = new LinuxHelperClient(socketPath, () => Interlocked.Read(ref now));

            Assert.Empty(client.Allowed);
            Assert.Equal(HelperState.NotAuthorized, client.State);

            // Inside the backoff nothing is asked again, even once the session is authorized.
            gate.Allow = true;
            Assert.Empty(client.Allowed);
            Assert.Equal(HelperState.NotAuthorized, client.State);

            // After the backoff (a fake clock, no wall-clock wait) the next ask sees the authorized session.
            Interlocked.Add(ref now, 60_000);
            Assert.NotEmpty(client.Allowed);
            Assert.Equal(HelperState.Ready, client.State);
        }
        finally
        {
            await stop.CancelAsync();
            await serving;
            File.Delete(socketPath);
        }
    }

    // ---------------------------------------------------------------- f. authorization is off the accept loop

    private sealed class BlockFirst : ICallerAuthorizer
    {
        private int _calls;
        public ManualResetEventSlim Release { get; } = new(false);
        public ManualResetEventSlim Entered { get; } = new(false);

        public bool IsAuthorized(Socket client)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Entered.Set();
                Release.Wait(TimeSpan.FromSeconds(30));
            }

            return true;
        }
    }

    [Fact]
    public async Task Server_ASlowAuthorization_DoesNotStallTheNextClient()
    {
        var (listener, socketPath) = Listen();
        using var _ = listener;
        var fs = KernelFan();
        var authorizer = new BlockFirst();
        using var server = new LinuxHelperServer(listener, PolicyFor(fs), fs, authorizer);
        using var stop = new CancellationTokenSource();
        var serving = server.RunAsync(stop.Token);
        using var squatter = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            squatter.Connect(new UnixDomainSocketEndPoint(socketPath));
            Assert.True(authorizer.Entered.Wait(TimeSpan.FromSeconds(15))); // inside the first, blocked, authorization

            using var client = new LinuxHelperClient(socketPath);
            var answered = Task.Run(() => client.Allowed.Count);
            Assert.True(await Task.WhenAny(answered, Task.Delay(20000)) == answered, "the second client was held up");
            Assert.True(await answered > 0);
        }
        finally
        {
            authorizer.Release.Set();
            await stop.CancelAsync();
            await serving;
            File.Delete(socketPath);
        }
    }

    [Fact]
    public async Task Server_AnIdleAuthorizedConnection_IsClosed_SoItCannotHoldASlotForever()
    {
        var (listener, socketPath) = Listen();
        using var _ = listener;
        var fs = KernelFan();
        using var server = new LinuxHelperServer(listener, PolicyFor(fs), fs, new Gate { Allow = true },
            connectionIdleTimeout: TimeSpan.FromMilliseconds(200));
        using var stop = new CancellationTokenSource();
        var serving = server.RunAsync(stop.Token);
        using var idle = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
        {
            ReceiveTimeout = 20000
        };
        try
        {
            idle.Connect(new UnixDomainSocketEndPoint(socketPath));

            // Sends nothing; the server closes it, which reads as end of stream (0 bytes).
            var read = await Task.Run(() => idle.Receive(new byte[16]));
            Assert.Equal(0, read);
        }
        finally
        {
            await stop.CancelAsync();
            await serving;
            File.Delete(socketPath);
        }
    }

    // ---------------------------------------------------------------- c. a failed pwm write does not leave the fan manual

    [Fact]
    public async Task KernelFan_WhenThePwmWriteFails_TheEnablesGoBackToAutomatic()
    {
        var fs = KernelFan();
        fs.DenyWrite("/sys/class/hwmon/hwmon4/pwm1");
        using var controller = Controller(fs);

        Assert.False(await controller.SetFanModeAsync(FanMode.Custom, 60, 40));

        Assert.Equal("2", fs.Peek("/sys/class/hwmon/hwmon4/pwm1_enable")?.Trim());
        Assert.Equal("2", fs.Peek("/sys/class/hwmon/hwmon4/pwm2_enable")?.Trim());
        // It really did go manual first, and was handed back after.
        var enable1 = fs.Writes.Where(w => w.Path.EndsWith("/pwm1_enable", StringComparison.Ordinal)).Select(w => w.Value.Trim());
        Assert.Equal(new[] { "1", "2" }, enable1);
    }

    // ---------------------------------------------------------------- d. time_to_empty_now is seconds

    private const string Bat = "/sys/class/power_supply/BAT0";

    [Theory]
    [InlineData("7200", 120)]
    [InlineData("90", 1)]
    [InlineData("0", 0)]
    [InlineData("-1", 0)]
    public async Task Battery_TimeToEmpty_IsSecondsConvertedToMinutes(string raw, int minutes)
    {
        var fs = new InMemorySysfsFileSystem();
        fs.WithFile(Bat + "/type", "Battery\n");
        fs.WithFile(Bat + "/status", "Discharging\n");
        fs.WithFile(Bat + "/capacity", "50\n");
        fs.WithFile(Bat + "/time_to_empty_now", raw + "\n");

        using var service = new LinuxBatteryHealthService(fs,
            historyPath: Path.Combine(Path.GetTempPath(), $"nl-wear-{Guid.NewGuid():N}.json"));
        var snapshot = await service.ReadAsync();

        Assert.Equal(minutes, snapshot.Status.EstimatedRemainingMinutes);
    }

    // ---------------------------------------------------------------- e. Custom(100,100) reads back as Max

    [Fact]
    public async Task LinuwuFan_CustomHundredHundred_IsAppliedEvenThoughItReadsBackAsMax()
    {
        var fs = Machine();
        var sense = LinuxPaths.Combine(LinuxPaths.LinuwuModuleRoot, LinuxPaths.NitroSense);
        fs.WithDirectory(LinuxPaths.AcpiPlatformRoot);
        fs.WithDirectory(sense);
        fs.WithFile(LinuxPaths.Combine(sense, "fan_speed"), "0,0\n");
        var settings = new AppSettings();
        using var controller = Controller(fs, settings);

        Assert.True(await controller.SetFanModeAsync(FanMode.Custom, 100, 100));

        Assert.Equal("100,100", fs.Peek(LinuxPaths.Combine(sense, "fan_speed"))?.Trim());
        Assert.Empty(settings.CapabilitySessionFailures);
    }

    // ---------------------------------------------------------------- g. self-test write-back

    private sealed class NoCommands : ICommandRunner
    {
        public string? Find(string name) => null;
        public CommandResult Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout) => CommandResult.NotRun("no");
    }

    [Fact]
    public void SelfTest_WritesTheActiveChargeTypeName_NotTheBracketedText_AndNeverTheMux()
    {
        const string charge = "/sys/class/power_supply/BAT1/charge_types";
        var fs = Machine();
        fs.WithFile(charge, "[Standard] Long Life\n");
        var sense = LinuxPaths.Combine(LinuxPaths.LinuwuModuleRoot, LinuxPaths.NitroSense);
        fs.WithDirectory(LinuxPaths.AcpiPlatformRoot);
        fs.WithDirectory(sense);
        fs.WithFile(LinuxPaths.Combine(sense, "dgpu_mode"), "0\n");

        var report = new LinuxSelfTest(fs, new NoCommands()).Run();

        Assert.Contains(fs.Writes, w => w.Path == charge && w.Value.Trim() == "Standard");
        Assert.DoesNotContain(fs.Writes, w => w.Value.Contains('['));
        Assert.DoesNotContain(fs.Writes, w => w.Path.EndsWith("/dgpu_mode", StringComparison.Ordinal));
        Assert.DoesNotContain("[FAIL] charge limit", report);
    }

    // ---------------------------------------------------------------- i. no amd_pstate switching

    [Fact]
    public async Task CpuBoost_AmdPstateStatusAlone_IsNotASupportedNode()
    {
        var fs = new InMemorySysfsFileSystem();
        fs.WithFile(LinuxPaths.AmdPstateStatus, "active\n");

        var boost = new LinuxCpuBoostAccess(fs);

        Assert.False(boost.IsSupported);
        Assert.Null(boost.Node);
        Assert.False(await boost.SetBoostModeAsync(NitroLite.Core.CpuBoostMode.Disabled));
        Assert.Equal("active", fs.Peek(LinuxPaths.AmdPstateStatus)?.Trim());
    }
}
