using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NitroLite.Core;
using NitroLite.Core.Hardware.Diagnostics;
using NitroLite.Core.Hardware.Verification;
using NitroLite.Core.Hardware.Wmi;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The one promise the diagnostic client makes: it can never break the app, and it can never carry an identifier.
/// </summary>
/// <remarks>
/// <para>
/// The client exists for the boards that answer a write with success and then do nothing, so its normal operating
/// environment is a machine whose firmware is already misbehaving. A send that threw out of that path would turn a
/// lighting annoyance into a crash, which is why the suppression is pinned by a test rather than by a comment.
/// </para>
/// <para>
/// The payload shape is pinned too. The endpoint is a Worker that parses these seven names; a report that quietly grew
/// a field — or lost one — would be discovered only when somebody needed it.
/// </para>
/// </remarks>
public class HardwareDiagnosticClientTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private static readonly HardwareDiagnosticIdentity KnownMachine = new()
    {
        Model = "AN16-61",
        Board = "EV9_SKF",
        BiosVersion = "1.53",
        AvailableWmiMethods = new[] { "GetGamingMiscSetting", "SetGamingMiscSetting", "SetGamingRgbKb" }
    };

    /// <summary>A handler that fails the way an offline machine fails, and counts the attempt.</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            throw new HttpRequestException("The network is down.");
        }
    }

    /// <summary>A handler that answers the way a broken deployment answers.</summary>
    private sealed class RefusingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }

    /// <summary>A handler that keeps the body it was given, so the payload can be read back exactly as it was sent.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource<string> _body =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string> Body => _body.Task;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var text = request.Content == null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);

            _body.TrySetResult(text);
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }
    }

    private static HardwareDiagnosticReport Report(HardwareDiagnosticIdentity identity) => new()
    {
        Model = identity.Model,
        Board = identity.Board,
        BiosVersion = identity.BiosVersion,
        FailedControl = "wmi.muxMode",
        AttemptedTransport = HardwareDiagnosticClient.WmiTransport,
        AvailableWmiMethods = identity.AvailableWmiMethods,
        TimestampUtc = Timestamp
    };

    [Fact]
    public async Task ANetworkFailureIsSuppressedAndTheAttemptWasMade()
    {
        var handler = new ThrowingHandler();
        using var http = new HttpClient(handler);
        var client = new HardwareDiagnosticClient(
            identity: () => KnownMachine,
            http: http,
            endpoint: new Uri("https://diagnostics.invalid/report"),
            clock: () => Timestamp);

        // No exception, no result, no log: the call returns normally and the failure dies with the request.
        await client.SendAsync(Report(KnownMachine));

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task AnErrorAnswerIsSuppressedToo()
    {
        var handler = new RefusingHandler();
        using var http = new HttpClient(handler);
        var client = new HardwareDiagnosticClient(
            identity: () => KnownMachine,
            http: http,
            endpoint: new Uri("https://diagnostics.invalid/report"),
            clock: () => Timestamp);

        await client.SendAsync(Report(KnownMachine));

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task TheFailurePathSendsTheScrubbedSevenFieldPayload()
    {
        var handler = new CapturingHandler();
        var identity = KnownMachine with { Board = "Serial=ABC12345" };
        using var http = new HttpClient(handler);
        var client = new HardwareDiagnosticClient(
            identity: () => identity,
            http: http,
            endpoint: new Uri("https://diagnostics.invalid/report"),
            clock: () => Timestamp);

        // The production entry point, not SendAsync: this goes through the background dispatch, the identity read and
        // the scrub, which is the whole path a mismatch takes.
        client.ReadBackMismatch(ReadBackPolicy.MuxMode);

        var body = await handler.Body.WaitAsync(TimeSpan.FromSeconds(5));
        using var document = JsonDocument.Parse(body);
        var names = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();

        Assert.Equal(
            new[]
            {
                "model",
                "board",
                "biosVersion",
                "failedControl",
                "attemptedTransport",
                "availableWmiMethods",
                "timestampUtc"
            },
            names);

        Assert.Equal("AN16-61", document.RootElement.GetProperty("model").GetString());
        Assert.Equal("Serial=<redacted>", document.RootElement.GetProperty("board").GetString());
        Assert.Equal("1.53", document.RootElement.GetProperty("biosVersion").GetString());
        Assert.Equal("wmi.muxMode", document.RootElement.GetProperty("failedControl").GetString());
        Assert.Equal("WMI", document.RootElement.GetProperty("attemptedTransport").GetString());
        Assert.Equal(
            new[] { "GetGamingMiscSetting", "SetGamingMiscSetting", "SetGamingRgbKb" },
            document.RootElement.GetProperty("availableWmiMethods")
                .EnumerateArray()
                .Select(node => node.GetString() ?? "")
                .ToArray());

        // Always UTC, so a report from a machine in any time zone sorts beside the others.
        Assert.Equal(Timestamp, document.RootElement.GetProperty("timestampUtc").GetDateTimeOffset());
    }

    [Fact]
    public void ThePayloadHoldsNoFieldThatCouldNameTheOwner()
    {
        var json = JsonSerializer.Serialize(
            Report(KnownMachine),
            HardwareDiagnosticJsonContext.Default.HardwareDiagnosticReport);

        // The type cannot carry a serial, an account or an address; the assertion is the guard against somebody
        // adding one to the report later without thinking about what the endpoint is.
        var text = json.ToLowerInvariant();
        foreach (var forbidden in new[] { "serial", "username", "user", "hostname", "machineid", "macaddress", "ipaddress" })
        {
            Assert.DoesNotContain(forbidden, text);
        }
    }

    [Fact]
    public async Task TheLightingExhaustionNamesTheHidTransport()
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var client = new HardwareDiagnosticClient(
            identity: () => KnownMachine,
            http: http,
            endpoint: new Uri("https://diagnostics.invalid/report"),
            clock: () => Timestamp);

        client.LightingFallbackExhausted("AN16-61", "1.53");

        var body = await handler.Body.WaitAsync(TimeSpan.FromSeconds(5));
        using var document = JsonDocument.Parse(body);

        Assert.Equal("keyboard.lighting", document.RootElement.GetProperty("failedControl").GetString());
        Assert.Equal("HID", document.RootElement.GetProperty("attemptedTransport").GetString());
    }

    /// <summary>A sink that records instead of sending, for the policy-level trigger tests.</summary>
    private sealed class RecordingSink : IHardwareDiagnosticSink
    {
        public List<string> Controls { get; } = new();

        public List<string> Lighting { get; } = new();

        public void ReadBackMismatch(string control) => Controls.Add(control);

        public void LightingFallbackExhausted(string? modelCode, string? biosVersion) =>
            Lighting.Add($"{modelCode}|{biosVersion}");
    }

    [Fact]
    public async Task APolicyMismatchReachesTheSinkExactlyOnce()
    {
        var sink = new RecordingSink();

        // Three attempts in the policy, one failed control at the end: the report is about the control, not the retry.
        var policy = new ReadBackPolicy(
            new AppSettings(),
            _ => Task.CompletedTask,
            new SessionVerification(),
            sink);

        var result = await policy.ApplyAsync(
            ReadBackPolicy.MuxMode,
            () => Task.FromResult(true),
            () => Task.FromResult<int?>(0),
            observed => observed == 1,
            "DiscreteGpu");

        Assert.False(result.Applied);
        Assert.Equal(new[] { ReadBackPolicy.MuxMode }, sink.Controls);
        Assert.Empty(sink.Lighting);
    }

    [Fact]
    public async Task AConfirmedWriteSaysNothing()
    {
        var sink = new RecordingSink();
        var policy = new ReadBackPolicy(
            new AppSettings(),
            _ => Task.CompletedTask,
            new SessionVerification(),
            sink);

        var result = await policy.ApplyAsync(
            ReadBackPolicy.MuxMode,
            () => Task.FromResult(true),
            () => Task.FromResult<int?>(1),
            observed => observed == 1,
            "DiscreteGpu");

        Assert.True(result.Applied);
        Assert.Empty(sink.Controls);
        Assert.Empty(sink.Lighting);
    }

    [Fact]
    public async Task ATransportThatWasNeverThereSaysNothing()
    {
        var sink = new RecordingSink();
        var policy = new ReadBackPolicy(
            new AppSettings(),
            _ => Task.CompletedTask,
            new SessionVerification(),
            sink);

        var result = await policy.ApplyAsync(
            ReadBackPolicy.MuxMode,
            () => throw new WmiTransportUnavailableException("the helper is not running"),
            () => Task.FromResult<int?>(0),
            observed => observed == 1,
            "DiscreteGpu");

        Assert.True(result.TransportMissing);
        Assert.Empty(sink.Controls);
    }
}
