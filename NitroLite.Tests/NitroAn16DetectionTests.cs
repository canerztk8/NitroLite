using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Core.Hardware.Wmi;
using NitroLite.Windows;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// What the AN16-61 report proved, pinned so it cannot come back.
/// <para>
/// Three separate refusals stacked up on that machine and each one hid the next. NitroLite was installed under
/// <c>C:\WinTools</c>, so the elevated helper was refused as an untrusted location — including when the user
/// restarted the app as administrator, which is the workaround the refusal message itself suggested. Acer's service
/// accepted the socket and answered the <c>VERSION</c> greeting with nothing, which was read as "no service" even
/// though NitroSense on the same machine talks to it. With both paths gone the app had no backend, and every page
/// said nothing detected.
/// </para>
/// <para>
/// Nothing here launches an elevated process, opens a real Acer service or writes to hardware: the launcher takes an
/// injected start/connect pair, the trust check is a delegate, and the service is a socket on an ephemeral port.
/// </para>
/// </summary>
public class NitroAn16DetectionTests
{
    private const string PortableExe = @"C:\WinTools\NitroLite\NitroLite.exe";

    private static Func<TimeSpan, Task> NoDelay => _ => Task.CompletedTask;

    // ---------------------------------------------------------------- the install-location refusal

    [Fact]
    public async Task Launcher_AnAlreadyElevatedParent_StartsTheWorkerEvenFromAnUntrustedFolder()
    {
        // The launch does not hand out a privilege the process does not already hold, so the folder's ACLs are not a
        // boundary here. Refusing anyway is what made "restart it as administrator" do nothing on the reported machine.
        int trustChecks = 0;
        ProcessStartInfo? captured = null;

        using var launcher = new WmiWorkerLauncher(
            executablePath: PortableExe,
            start: psi =>
            {
                captured = psi;
                return System.Diagnostics.Process.Start(new ProcessStartInfo("cmd.exe", "/c exit")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
            },
            connect: (_, _, _, _, _) => throw new InvalidOperationException("stop here"),
            isElevated: () => true,
            isTrustedLocation: _ =>
            {
                trustChecks++;
                return false;
            });

        Assert.Null(await launcher.StartAsync());

        Assert.Equal(1, launcher.Attempts);
        Assert.Equal(0, trustChecks);
        Assert.Equal(WmiWorkerRefusal.HandshakeFailed, launcher.Refusal);
        Assert.NotNull(captured);

        // No runas verb: the child inherits the token, so there is no prompt to raise.
        Assert.True(string.IsNullOrEmpty(captured!.Verb));
        Assert.False(captured.UseShellExecute);
    }

    [Fact]
    public async Task Launcher_AnUnelevatedParent_StillRefusesTheUntrustedFolder()
    {
        // The other half of the rule, and the one that has to keep working: this launch would turn a file any local
        // account can replace into an administrator process.
        bool started = false;

        using var launcher = new WmiWorkerLauncher(
            executablePath: PortableExe,
            start: _ =>
            {
                started = true;
                return null;
            },
            isElevated: () => false,
            isTrustedLocation: _ => false);

        Assert.Null(await launcher.StartAsync());

        Assert.False(started);
        Assert.Equal(0, launcher.Attempts);
        Assert.Equal(WmiWorkerRefusal.UntrustedLocation, launcher.Refusal);
        Assert.Contains("as administrator", launcher.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("any local account", launcher.LastError, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- a lost transport is not a firmware fault

    [Fact]
    public async Task ReadBack_AWriteWithNoTransport_IsNotCountedAgainstTheCapability()
    {
        var settings = new AppSettings();
        var policy = new ReadBackPolicy(settings, NoDelay);
        int writes = 0;

        var result = await policy.ApplyAsync(
            ReadBackPolicy.FanBehavior,
            write: () =>
            {
                writes++;
                return Task.FromException<bool>(new WmiTransportUnavailableException("the worker was not started"));
            },
            readBack: () => Task.FromResult<FanMode?>(FanMode.Auto),
            matches: value => value == FanMode.Auto);

        Assert.True(result.TransportMissing);
        Assert.False(result.DisabledForSession);
        Assert.False(result.PermanentlyDisabled);
        Assert.False(policy.IsDisabled(ReadBackPolicy.FanBehavior));
        Assert.Equal(0, policy.ConsecutiveSessionFailures(ReadBackPolicy.FanBehavior));
        Assert.Empty(settings.CapabilitySessionFailures);

        // One attempt, not three: a transport that is not there does not become one by asking again.
        Assert.Equal(1, writes);
    }

    [Fact]
    public async Task WmiController_RefusedInstallLocation_LeavesEveryCapabilityEnabled()
    {
        // The reported machine, at the controller: the helper is refused, so nothing is written — and three sessions
        // of that used to switch fan and mode control off permanently for a firmware fault that never happened.
        var settings = new AppSettings();

        using var launcher = new WmiWorkerLauncher(
            executablePath: PortableExe,
            start: _ => null,
            isElevated: () => false,
            isTrustedLocation: _ => false);

        using var controller = new WmiHardwareController(launcher, new ReadBackPolicy(settings, NoDelay));

        Assert.False(await controller.SetFanModeAsync(FanMode.Auto));
        Assert.False(await controller.SetOperatingModeAsync(OperatingMode.Turbo));

        Assert.Empty(settings.CapabilitySessionFailures);
        Assert.Empty(settings.DisabledCapabilities);
        Assert.Equal(WmiWorkerRefusal.UntrustedLocation, controller.Refusal);
    }

    // ---------------------------------------------------------------- the settings row tells the truth

    [Fact]
    public async Task DirectWmiStatus_NamesTheRefusalInsteadOfClaimingTheServiceAnswers()
    {
        using var launcher = new WmiWorkerLauncher(
            executablePath: PortableExe,
            start: _ => null,
            isElevated: () => false,
            isTrustedLocation: _ => false);

        using var wmi = new WmiHardwareController(launcher, new ReadBackPolicy(new AppSettings(), NoDelay));
        using var hybrid = new HybridHardwareController(
            new Phase3Fakes.StubHardware(),
            wmiFactory: () => wmi,
            serviceHealthy: () => Task.FromResult(false));

        Assert.False(await hybrid.StartDirectWmiAsync());

        var control = new WindowsDirectWmiControl(hybrid);
        Assert.Equal(WmiWorkerRefusal.UntrustedLocation, hybrid.DirectWmiRefusal);
        Assert.Contains("Program Files", control.StatusText);
        Assert.NotEqual(Loc.T("Acer servisi kullanılıyor"), control.StatusText);
    }

    [Fact]
    public async Task HardwareReport_SaysWhichBackendAnswersAndWhyTheOtherDoesNot()
    {
        // The report is what gets pasted into an issue. Before this it named no backend at all, which is how a report
        // of "nothing detected" arrived with no way to tell a silent service from a refused helper.
        using var launcher = new WmiWorkerLauncher(
            executablePath: PortableExe,
            start: _ => null,
            isElevated: () => false,
            isTrustedLocation: _ => false);

        using var wmi = new WmiHardwareController(launcher, new ReadBackPolicy(new AppSettings(), NoDelay));
        using var hybrid = new HybridHardwareController(
            new Phase3Fakes.StubHardware { IsAvailable = false },
            wmiFactory: () => wmi,
            serviceHealthy: () => Task.FromResult(false));

        Assert.False(await hybrid.StartDirectWmiAsync());

        var state = hybrid.DescribeBackend();
        Assert.Contains("AASSvc not connected", state);
        Assert.Contains("refused: install folder is writable by other accounts", state);

        var snapshot = new CapabilitySnapshot
        {
            CacheKey = "AN16-61|1.15",
            ProfileId = "nitro-16-an16-61",
            ProfileSource = "embedded",
            Sensors = new SensorLayout { Supported = true, Verified = false }
        };

        var markdown = HardwareReport.BuildMarkdown(snapshot, backend: state);
        Assert.Contains("| Backend | |", markdown);
        Assert.Contains(state, markdown);
    }

    // ---------------------------------------------------------------- the service greeting is a hint

    [Fact]
    public async Task Agent_RefusedGreeting_StillUsesTheServiceWhenARealReadAnswers()
    {
        // Exactly the reported shape: the socket accepts, VERSION is answered with nothing, and the telemetry read
        // works. NitroSense never sends VERSION either, which is why it worked on that machine.
        using var service = new FakeAcerService(packetId => packetId == 10 ? MonitorReply : null);
        using var client = NewClient(service, handshakeTimeout: TimeSpan.FromMilliseconds(300));

        Assert.True(await client.EnsureConnectedAsync());

        var data = await client.GetMonitorDataAsync();
        Assert.NotNull(data);
        Assert.Equal(55, data!.CpuTemperature);

        // The read that proved the service usable is the poll that was going to happen anyway, not a second one.
        Assert.Equal(new uint[] { 0, 10 }, service.Requests.ToArray());
    }

    [Fact]
    public async Task Agent_AnsweredGreeting_DoesNotAskTwice()
    {
        using var service = new FakeAcerService(packetId => packetId == 0
            ? "{\"request\":\"VERSION\",\"result\":\"0\"}"
            : MonitorReply);
        using var client = NewClient(service, handshakeTimeout: TimeSpan.FromMilliseconds(300));

        Assert.True(await client.EnsureConnectedAsync());
        Assert.Equal(new uint[] { 0 }, service.Requests.ToArray());
    }

    [Fact]
    public async Task Agent_SilentService_IsReportedUnhealthyAndIsNotRetriedEveryTick()
    {
        using var service = new FakeAcerService(_ => null);
        using var client = NewClient(service, handshakeTimeout: TimeSpan.FromMilliseconds(200));

        Assert.False(await client.EnsureConnectedAsync());
        Assert.Equal(2, service.Requests.Count); // the greeting and the read that replaced it

        // The second call inside the backoff window must not open another socket: that is what turned one broken
        // service into a wall of identical warnings in the report.
        Assert.False(await client.EnsureConnectedAsync());
        Assert.Equal(2, service.Requests.Count);
    }

    [Fact]
    public async Task Agent_A_SilentService_BacksOffInsteadOfRetryingOnceASecond()
    {
        // The report is a wall of identical lines because every attempt reset the failure streak to "first failure",
        // which meant a one-second delay, forever. A streak that grows turns the same silence into 1 s, 2 s, 4 s...
        using var service = new FakeAcerService(_ => null);
        TimeSpan short_ = TimeSpan.FromMilliseconds(120);
        using var client = NewClient(service, short_, short_);

        Assert.False(await client.EnsureConnectedAsync());
        Assert.Equal(2, service.Requests.Count);

        // The first refusal earns a one-second delay, so this attempt is allowed.
        await Task.Delay(1100);
        Assert.False(await client.EnsureConnectedAsync());
        Assert.Equal(4, service.Requests.Count);

        // That was the second failure, which earns two seconds: nothing may be sent half a second later.
        await Task.Delay(500);
        Assert.False(await client.EnsureConnectedAsync());
        Assert.Equal(4, service.Requests.Count);
    }

    [Fact]
    public void DescribeReply_SaysNothingOnOneLineAndStaysShort()
    {
        Assert.Equal("(no reply)", AcerAgentClient.DescribeReply(null));
        Assert.Equal("(no reply)", AcerAgentClient.DescribeReply("   "));
        Assert.Equal("{ \"a\": 1 }", AcerAgentClient.DescribeReply("{\n  \"a\":  1\n}"));

        var longReply = AcerAgentClient.DescribeReply(new string('x', 400));
        Assert.Equal(160, longReply.Length);
        Assert.EndsWith("...", longReply);
    }

    // ---------------------------------------------------------------- helpers

    private static AcerAgentClient NewClient(FakeAcerService service, TimeSpan handshakeTimeout,
        TimeSpan? requestTimeout = null) =>
        new("127.0.0.1", service.Port,
            connectTimeout: TimeSpan.FromSeconds(2),
            handshakeTimeout: handshakeTimeout,
            requestTimeout: requestTimeout);

    private const string MonitorReply =
        "{\"request\":\"GET_MONITOR_DATA\",\"result\":\"0\",\"data\":{\"CPU_TEMPERATURE\":55,\"CPU_FANSPEED\":2400}}";

    /// <summary>
    /// A stand-in for AASSvc on an ephemeral loopback port. It reads the framed requests the client actually sends
    /// (<c>ACER</c>, the packet id, then JSON) and answers each with whatever the test's delegate returns; null means
    /// silence, which is the behaviour the greeting on the reported machine had.
    /// </summary>
    private sealed class FakeAcerService : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Func<uint, string?> _answer;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public FakeAcerService(Func<uint, string?> answer)
        {
            _answer = answer;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(AcceptLoopAsync);
        }

        public int Port { get; }

        /// <summary>The packet ids the client sent, in order. Packet 0 is the VERSION greeting, 10 the telemetry read.</summary>
        public ConcurrentQueue<uint> Requests { get; } = new();

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                    await ServeAsync(client).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using var stream = client.GetStream();
            var pending = new List<byte>();
            var buffer = new byte[4096];

            while (!_cts.IsCancellationRequested)
            {
                int read;
                try
                {
                    read = await stream.ReadAsync(buffer, _cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
                {
                    return;
                }

                if (read <= 0) return;
                pending.AddRange(buffer.Take(read));

                while (TryTakePacket(pending, out uint packetId))
                {
                    Requests.Enqueue(packetId);

                    if (_answer(packetId) is not { } reply) continue;

                    try
                    {
                        var bytes = Encoding.UTF8.GetBytes(reply);
                        await stream.WriteAsync(bytes, _cts.Token).ConfigureAwait(false);
                        await stream.FlushAsync(_cts.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
                    {
                        return;
                    }
                }
            }
        }

        /// <summary>Removes one complete request from <paramref name="pending"/>, or returns false if none is whole yet.</summary>
        private static bool TryTakePacket(List<byte> pending, out uint packetId)
        {
            packetId = 0;
            if (pending.Count < 9) return false;

            if (pending[0] != 'A' || pending[1] != 'C' || pending[2] != 'E' || pending[3] != 'R')
            {
                // Not a frame this test knows how to read; drop it so it cannot wedge the loop.
                pending.Clear();
                return false;
            }

            var all = pending.ToArray();
            if (!AcerAgentClient.TryFindJsonObject(all.AsSpan(8), out _, out int end)) return false;

            packetId = BitConverter.ToUInt32(all, 4);
            pending.RemoveRange(0, 8 + end);
            return true;
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            try { _listener.Stop(); } catch { }
            try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { }
            _cts.Dispose();
        }
    }
}
