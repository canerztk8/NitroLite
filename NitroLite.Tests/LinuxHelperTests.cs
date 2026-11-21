using System.IO;
using System.Net.Sockets;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Core.Hardware.Wmi;
using NitroLite.Linux;
using NitroLite.Linux.Sysfs;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The root helper: what it allows, how it decides who may ask, the protocol, the client, and the files that install
/// it. The policy is the security boundary, so most of these tests are about what it refuses.
/// </summary>
public class LinuxHelperTests
{
    private static ProfileCatalog Catalog() =>
        ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "NitroLite.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private const string Pwm1 = "/sys/class/hwmon/hwmon4/pwm1";
    private const string Pwm1Enable = "/sys/class/hwmon/hwmon4/pwm1_enable";

    /// <summary>An AN16-41 on Linux 7.4 with Linuwu-Sense's keyboard and limiter, every attribute root-only.</summary>
    private static InMemorySysfsFileSystem Machine()
    {
        var fs = new InMemorySysfsFileSystem();
        fs.WithFile("/sys/class/dmi/id/sys_vendor", "Acer\n");
        fs.WithFile("/sys/class/dmi/id/product_name", "Nitro AN16-41\n");
        fs.WithFile(LinuxPaths.PlatformProfile, "balanced\n");
        fs.WithFile(LinuxPaths.PlatformProfileChoices, "low-power quiet balanced balanced-performance performance\n");
        fs.WithFile(LinuxPaths.CpuFreqBoost, "1\n");

        const string acer = "/sys/class/hwmon/hwmon4";
        fs.WithDirectory(acer);
        fs.WithFile(acer + "/name", "acer\n");
        fs.WithFile(acer + "/fan1_input", "2400\n");
        fs.WithFile(Pwm1, "0\n");
        fs.WithFile(Pwm1Enable, "2\n");

        var sense = LinuxPaths.Combine(LinuxPaths.LinuwuModuleRoot, LinuxPaths.NitroSense);
        fs.WithDirectory(sense);
        fs.WithFile(LinuxPaths.Combine(sense, "battery_limiter"), "0\n");
        var keyboard = LinuxPaths.Combine(sense, LinuxPaths.FourZoneKeyboard);
        fs.WithDirectory(keyboard);
        fs.WithFile(LinuxPaths.Combine(keyboard, "per_zone_mode"), "00E5FF,00E5FF,00E5FF,00E5FF,55\n");

        foreach (var path in new[] { "/sys/class/dmi/id/product_name", LinuxPaths.PlatformProfile, LinuxPaths.CpuFreqBoost, Pwm1, Pwm1Enable,
                     LinuxPaths.Combine(sense, "battery_limiter"), LinuxPaths.Combine(keyboard, "per_zone_mode") })
        {
            fs.DenyWrite(path);
        }

        return fs;
    }

    private static LinuxHelperPolicy Policy(InMemorySysfsFileSystem fs) =>
        LinuxHelperPolicy.For(new LinuxCapabilityProbe(fs, Catalog()).Probe(), fs);

    /// <summary>The same tree, but writable: what the helper sees as root.</summary>
    private static InMemorySysfsFileSystem AsRoot()
    {
        var fs = Machine();
        var root = new InMemorySysfsFileSystem();
        foreach (var path in new[]
                 {
                     "/sys/class/dmi/id/sys_vendor", "/sys/class/dmi/id/product_name", LinuxPaths.PlatformProfile,
                     LinuxPaths.PlatformProfileChoices, LinuxPaths.CpuFreqBoost, "/sys/class/hwmon/hwmon4/name",
                     "/sys/class/hwmon/hwmon4/fan1_input", Pwm1, Pwm1Enable
                 })
        {
            root.WithFile(path, fs.Peek(path)!);
        }

        root.WithDirectory("/sys/class/hwmon/hwmon4");
        return root;
    }

    private sealed class Allow : ICallerAuthorizer
    {
        public bool Result { get; init; } = true;
        public int Calls { get; private set; }
        public bool IsAuthorized(Socket client) { Calls++; return Result; }
    }

    // ---------------------------------------------------------------- the policy

    [Fact]
    public void Policy_ListsExactlyTheAttributesTheProbeFound()
    {
        var policy = Policy(Machine());

        Assert.Contains(LinuxPaths.PlatformProfile, policy.Paths);
        Assert.Contains(Pwm1, policy.Paths);
        Assert.Contains(Pwm1Enable, policy.Paths);
        Assert.Contains(LinuxPaths.CpuFreqBoost, policy.Paths);
        Assert.DoesNotContain(LinuxPaths.PlatformProfileChoices, policy.Paths); // read-only by nature
        Assert.DoesNotContain("/sys/class/dmi/id/product_name", policy.Paths);
    }

    [Theory]
    [InlineData("/etc/shadow")]
    [InlineData("/sys/firmware/acpi/../acpi/platform_profile")]
    [InlineData("/sys/firmware/acpi/platform_profile/")]
    [InlineData("/sys/firmware/acpi//platform_profile")]
    [InlineData("/sys/class/hwmon/hwmon4/pwm3")]
    [InlineData("")]
    public void Policy_RefusesAnyPathNotExactlyOnTheList(string path) =>
        Assert.Equal("not-allowed", Policy(Machine()).Check(path, "1"));

    [Theory]
    [InlineData(LinuxPaths.PlatformProfile, "performance", null)]
    [InlineData(LinuxPaths.PlatformProfile, "turbo", "invalid-value")]
    [InlineData(LinuxPaths.PlatformProfile, "performance\nquiet", "invalid-value")]
    [InlineData(Pwm1Enable, "1", null)]
    [InlineData(Pwm1Enable, "3", "invalid-value")]
    [InlineData(Pwm1, "200", null)]
    [InlineData(Pwm1, "0", "invalid-value")]   // a stopped fan in manual mode
    [InlineData(Pwm1, "40", "invalid-value")]  // below the app's 30 % floor
    [InlineData(Pwm1, "256", "invalid-value")]
    [InlineData(Pwm1, "-5", "invalid-value")]
    [InlineData(Pwm1, " 200", "invalid-value")]
    [InlineData(LinuxPaths.CpuFreqBoost, "0", null)]
    [InlineData(LinuxPaths.CpuFreqBoost, "yes", "invalid-value")]
    public void Policy_ChecksEveryValue(string path, string value, string? expected) =>
        Assert.Equal(expected, Policy(Machine()).Check(path, value));

    [Fact]
    public void Policy_KeyboardLinesMustParse()
    {
        var policy = Policy(Machine());
        var keyboard = LinuxPaths.Combine(LinuxPaths.Combine(LinuxPaths.Combine(LinuxPaths.LinuwuModuleRoot,
            LinuxPaths.NitroSense), LinuxPaths.FourZoneKeyboard), "per_zone_mode");

        Assert.Null(policy.Check(keyboard, "FF0000,00FF00,0000FF,FFFFFF,80"));
        Assert.Equal("invalid-value", policy.Check(keyboard, "rm -rf /"));
        Assert.Equal("invalid-value", policy.Check(keyboard, new string('A', LinuxHelperPolicy.MaxValueLength + 1)));
    }

    // ---------------------------------------------------------------- the server

    [Fact]
    public async Task Server_WritesAnAllowedValue_AndRefusesTheRest()
    {
        var root = AsRoot();
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        using var server = new LinuxHelperServer(listener, Policy(Machine()), root, new Allow());

        Assert.Equal("{\"ok\":true}", await server.HandleAsync("{\"op\":\"write\",\"path\":\"" + Pwm1Enable + "\",\"value\":\"1\"}"));
        Assert.Equal("1", root.Peek(Pwm1Enable)?.Trim());

        Assert.Contains("not-allowed", await server.HandleAsync("{\"op\":\"write\",\"path\":\"/etc/passwd\",\"value\":\"x\"}"));
        Assert.Contains("invalid-value", await server.HandleAsync("{\"op\":\"write\",\"path\":\"" + Pwm1 + "\",\"value\":\"0\"}"));
        Assert.Contains("bad-request", await server.HandleAsync("not json"));
        Assert.Contains("bad-request", await server.HandleAsync("{\"op\":\"exec\",\"value\":\"id\"}"));
        Assert.Contains(Pwm1, await server.HandleAsync("{\"op\":\"list\"}"));
    }

    [Fact]
    public async Task ClientAndServer_OverARealUnixSocket()
    {
        var socketPath = Path.Combine(Path.GetTempPath(), $"nl-{Guid.NewGuid():N}.sock");
        var root = AsRoot();
        var authorizer = new Allow();

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(4);

        using var server = new LinuxHelperServer(listener, Policy(Machine()), root, authorizer, TimeSpan.FromSeconds(30));
        using var stop = new CancellationTokenSource();
        var serving = server.RunAsync(stop.Token);

        try
        {
            using var client = new LinuxHelperClient(socketPath);
            Assert.Contains(Pwm1Enable, client.Allowed);

            Assert.True(client.Write(Pwm1Enable, "0\n").Ok);
            Assert.Equal("0", root.Peek(Pwm1Enable)?.Trim());

            Assert.True(client.Write("/etc/passwd", "x").NeedsUdevRule); // refused, reported as "no permission"
            Assert.Equal(1, authorizer.Calls); // once per connection, not per request
        }
        finally
        {
            await stop.CancelAsync();
            await serving;
            File.Delete(socketPath);
        }
    }

    [Fact]
    public async Task Server_RefusesEveryRequestFromAnUnauthorizedCaller()
    {
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        var socketPath = Path.Combine(Path.GetTempPath(), $"nl-{Guid.NewGuid():N}.sock");
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(4);
        var root = AsRoot();

        using var server = new LinuxHelperServer(listener, Policy(Machine()), root, new Allow { Result = false });
        using var stop = new CancellationTokenSource();
        var serving = server.RunAsync(stop.Token);
        try
        {
            using var client = new LinuxHelperClient(socketPath);
            Assert.Empty(client.Allowed);
            Assert.False(client.Write(Pwm1Enable, "0").Ok);
            Assert.Equal("2", root.Peek(Pwm1Enable)?.Trim());
        }
        finally
        {
            await stop.CancelAsync();
            await serving;
            File.Delete(socketPath);
        }
    }

    [Fact]
    public async Task Server_ExitsByItselfWhenIdle()
    {
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        var socketPath = Path.Combine(Path.GetTempPath(), $"nl-{Guid.NewGuid():N}.sock");
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(4);
        try
        {
            using var server = new LinuxHelperServer(listener, Policy(Machine()), AsRoot(), new Allow(), TimeSpan.FromMilliseconds(200));
            var run = server.RunAsync();
            Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10))));
        }
        finally
        {
            File.Delete(socketPath);
        }
    }

    // ---------------------------------------------------------------- the app's side

    [Fact]
    public async Task HelperBackedFileSystem_RoutesOnlyRefusedWritesOfAllowedPaths()
    {
        var socketPath = Path.Combine(Path.GetTempPath(), $"nl-{Guid.NewGuid():N}.sock");
        var user = Machine();
        var root = AsRoot();

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(4);
        using var server = new LinuxHelperServer(listener, Policy(Machine()), root, new Allow(), TimeSpan.FromSeconds(30));
        using var stop = new CancellationTokenSource();
        var serving = server.RunAsync(stop.Token);
        try
        {
            using var client = new LinuxHelperClient(socketPath);
            var fs = new HelperBackedFileSystem(user, client);

            Assert.True(fs.CanWrite(Pwm1Enable)); // through the helper, so no udev rule is needed for it
            Assert.True(fs.WriteAllText(Pwm1Enable, "0\n").Ok);
            Assert.Equal("0", root.Peek(Pwm1Enable)?.Trim());
            Assert.Equal("2", user.Peek(Pwm1Enable)?.Trim()); // the user's own tree was never written

            Assert.False(fs.CanWrite("/sys/class/dmi/id/product_name"));
        }
        finally
        {
            await stop.CancelAsync();
            await serving;
            File.Delete(socketPath);
        }
    }

    [Fact]
    public async Task Probe_WithTheHelper_NeedsNoUdevRule()
    {
        var socketPath = Path.Combine(Path.GetTempPath(), $"nl-{Guid.NewGuid():N}.sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(4);

        var machine = Machine();
        var policy = LinuxHelperPolicy.For(new LinuxCapabilityProbe(machine, Catalog()).Probe(), machine);
        using var server = new LinuxHelperServer(listener, policy, AsRoot(), new Allow(), TimeSpan.FromSeconds(30));
        using var stop = new CancellationTokenSource();
        var serving = server.RunAsync(stop.Token);
        try
        {
            using var client = new LinuxHelperClient(socketPath);
            var caps = new LinuxCapabilityProbe(new HelperBackedFileSystem(Machine(), client), Catalog()).Probe();
            Assert.False(caps.NeedsUdevRule);
            Assert.False(caps.ModeViaPowerProfiles);
        }
        finally
        {
            await stop.CancelAsync();
            await serving;
            File.Delete(socketPath);
        }
    }

    // ---------------------------------------------------------------- polkit

    [Theory]
    [InlineData("1234 (bash) S 1 1234 1234 0 -1 4194560 100 0 0 0 1 2 0 0 20 0 1 0 98765 1000 100", "98765")]
    [InlineData("1234 (we ird) name)) S 1 1234 1234 0 -1 4194560 100 0 0 0 1 2 0 0 20 0 1 0 55555 1000 100", "55555")]
    [InlineData("garbage", null)]
    public void StartTime_IsField22CountedFromTheLastParenthesis(string stat, string? expected) =>
        Assert.Equal(expected, PolkitCallerAuthorizer.StartTime(stat));

    private sealed class Runner : ICommandRunner
    {
        public int Exit { get; init; }
        public List<IReadOnlyList<string>> Calls { get; } = new();
        public string? Find(string name) => name == "pkcheck" ? "/usr/bin/pkcheck" : null;
        public CommandResult Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout)
        {
            Calls.Add(arguments);
            return new CommandResult(Exit, "", "");
        }
    }

    [Fact]
    public void Polkit_IsAskedWithPidStartTimeAndUid()
    {
        var fs = new InMemorySysfsFileSystem();
        fs.WithFile("/proc/4321/stat", "4321 (NitroLite) S 1 4321 4321 0 -1 0 0 0 0 0 0 0 0 0 20 0 1 0 777 0 0");
        var runner = new Runner();

        Assert.True(new PolkitCallerAuthorizer(runner, fs).IsAuthorized(4321, 1000));
        Assert.Equal(new[] { "--action-id", PolkitCallerAuthorizer.Action, "--process", "4321,777,1000" }, runner.Calls.Single());

        Assert.False(new PolkitCallerAuthorizer(new Runner { Exit = 1 }, fs).IsAuthorized(4321, 1000));
        Assert.False(new PolkitCallerAuthorizer(runner, fs).IsAuthorized(9999, 1000)); // no such process
    }

    // ---------------------------------------------------------------- the installed files

    [Fact]
    public void ServiceUnit_IsConfinedAndKeepsSysWritable()
    {
        var unit = File.ReadAllText(Path.Combine(RepoRoot(), "packaging", "linux", "nitrolite-helper.service.in"));

        Assert.Contains("ExecStart=@APP_DIR@/NitroLite.Desktop --linux-helper", unit);
        Assert.Contains("ProtectSystem=strict", unit);
        Assert.Contains("ReadWritePaths=/sys", unit);
        Assert.Contains("ProtectHome=yes", unit);
        Assert.Contains("NoNewPrivileges=yes", unit);
        Assert.Contains("PrivateNetwork=yes", unit);
        // ProtectKernelTunables would mount /sys read-only and the helper could not do its one job.
        Assert.DoesNotContain("\nProtectKernelTunables", unit);
    }

    [Fact]
    public void PolkitPolicy_AllowsOnlyTheActiveLocalSession()
    {
        var policy = File.ReadAllText(Path.Combine(RepoRoot(), "packaging", "linux", "io.github.canerztk8.nitrolite.policy"));

        Assert.Contains($"action id=\"{PolkitCallerAuthorizer.Action}\"", policy);
        Assert.Contains("<allow_active>yes</allow_active>", policy);
        Assert.Contains("<allow_inactive>no</allow_inactive>", policy);
        Assert.Contains("<allow_any>no</allow_any>", policy);
    }

    [Fact]
    public void Socket_IsWhereTheClientLooks()
    {
        var socket = File.ReadAllText(Path.Combine(RepoRoot(), "packaging", "linux", "nitrolite-helper.socket"));
        Assert.Contains("ListenStream=" + LinuxHelperClient.DefaultSocketPath, socket);
    }
}
