using System;
using System.Threading.Tasks;
using NitroLite.Core.Hardware.Lighting;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The WMI-to-HID fallback for the four-zone keyboard, and the per-machine memory that makes it cheap.
/// </summary>
/// <remarks>
/// The failure this covers was measured twice: on an AN16-61 and an ANV16S-41 the Acer WMI methods answer status 0,
/// the LEDs do not move, and the read-back policy correctly disables the capability for the session. The user is left
/// with a lighting tab that says "Write failed" while the same keyboard can be set by another program. These tests pin
/// the two halves of the fix: one HID attempt after a WMI write that did not take, and the answer remembered per
/// Model|BIOS so the next colour change does not pay for three failing round trips.
/// </remarks>
public class KeyboardLightingTransportTests
{
    private static readonly KeyboardLightingFrame Red =
        KeyboardLightingFrame.Create("STATIC", 100, 4, 1, "#FF0000", null);

    private sealed class StubWriter : IHidLightingWriter
    {
        private readonly bool _accepts;

        public StubWriter(bool accepts = true, string description = "ENE KB5130 0x0CF2:0x5130")
        {
            _accepts = accepts;
            Description = description;
        }

        public string Description { get; }

        public string Protocol => "ene-kb5130";

        public bool CanWrite { get; set; } = true;

        public string? Refusal { get; set; }

        public bool ThrowOnWrite { get; set; }

        public int Writes { get; private set; }

        public bool Disposed { get; private set; }

        public bool TryWrite(KeyboardLightingFrame frame, out string? error)
        {
            Writes++;
            if (ThrowOnWrite) throw new InvalidOperationException("the controller went away");

            error = _accepts ? null : "the controller refused the report";
            return _accepts;
        }

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public async Task WmiFailureFallsBackToHidAndRemembersIt()
    {
        var router = new KeyboardLightingRouter();
        var writer = new StubWriter();
        var key = KeyboardLightingRouter.CacheKey("AN16-61", "1.53");

        var outcome = await router.ApplyAsync(
            "AN16-61", "1.53", Red,
            () => Task.FromResult(false),
            () => writer);

        Assert.True(outcome.Applied);
        Assert.Equal(KeyboardLightingTransport.Hid, outcome.Transport);
        Assert.True(outcome.FellBackToHid);
        Assert.Contains("ENE KB5130", outcome.Detail, StringComparison.Ordinal);
        Assert.Equal(1, writer.Writes);
        Assert.Equal(KeyboardLightingTransport.Hid, router.CachedTransport(key));
    }

    [Fact]
    public async Task CachedHidSkipsWmiOnTheNextRequest()
    {
        var router = new KeyboardLightingRouter();
        var writer = new StubWriter();
        int wmiCalls = 0;

        Task<bool> Wmi()
        {
            wmiCalls++;
            return Task.FromResult(false);
        }

        var first = await router.ApplyAsync("AN16-61", "1.53", Red, Wmi, () => writer);
        var second = await router.ApplyAsync("AN16-61", "1.53", Red, Wmi, () => writer);

        Assert.True(first.Applied);
        Assert.True(second.Applied);
        Assert.Equal(KeyboardLightingTransport.Hid, second.Transport);
        Assert.Contains("cached", second.Detail, StringComparison.Ordinal);
        Assert.Equal(1, wmiCalls);
    }

    [Fact]
    public async Task WmiSuccessNeverProbesHid()
    {
        var router = new KeyboardLightingRouter();
        int probes = 0;

        var outcome = await router.ApplyAsync(
            "AN16-61", "1.53", Red,
            () => Task.FromResult(true),
            () =>
            {
                probes++;
                return new StubWriter();
            });

        Assert.True(outcome.Applied);
        Assert.Equal(KeyboardLightingTransport.Wmi, outcome.Transport);
        Assert.False(outcome.ProbeRan);
        Assert.Equal(0, probes);
        Assert.Equal(
            KeyboardLightingTransport.Wmi,
            router.CachedTransport(KeyboardLightingRouter.CacheKey("AN16-61", "1.53")));
    }

    [Fact]
    public async Task NoControllerIsReportedRatherThanThrown()
    {
        var router = new KeyboardLightingRouter();

        var outcome = await router.ApplyAsync(
            "AN16-61", "1.53", Red,
            () => Task.FromResult(false),
            () => null);

        Assert.False(outcome.Applied);
        Assert.Equal(KeyboardLightingTransport.Unknown, outcome.Transport);
        Assert.Contains("no vendor HID lighting controller", outcome.Detail, StringComparison.Ordinal);
        Assert.True(outcome.ProbeRan);
    }

    [Fact]
    public async Task AControllerWithoutADocumentedFrameIsNamedAndNotWritten()
    {
        var router = new KeyboardLightingRouter();
        var writer = new StubWriter { CanWrite = false, Refusal = "no documented Acer lighting frame for this vendor" };

        var outcome = await router.ApplyAsync(
            "AN16-61", "1.53", Red,
            () => Task.FromResult(false),
            () => writer);

        Assert.False(outcome.Applied);
        Assert.Contains("no documented Acer lighting frame", outcome.Detail, StringComparison.Ordinal);
        Assert.Equal(0, writer.Writes);
    }

    [Fact]
    public async Task AWriterThatThrowsDoesNotEscape()
    {
        var router = new KeyboardLightingRouter();
        var writer = new StubWriter(accepts: true) { ThrowOnWrite = true };

        var outcome = await router.ApplyAsync(
            "AN16-61", "1.53", Red,
            () => Task.FromResult(false),
            () => writer);

        Assert.False(outcome.Applied);
        Assert.Contains("threw", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExceptionFromWmiIsTreatedAsAFailedWrite()
    {
        var router = new KeyboardLightingRouter();
        var writer = new StubWriter();

        var outcome = await router.ApplyAsync(
            "AN16-61", "1.53", Red,
            () => throw new InvalidOperationException("the worker is gone"),
            () => writer);

        Assert.True(outcome.Applied);
        Assert.Equal(KeyboardLightingTransport.Hid, outcome.Transport);
        Assert.Contains("InvalidOperationException", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALostControllerFallsBackToWmiAndForgetsTheCacheEntry()
    {
        var router = new KeyboardLightingRouter();
        var key = KeyboardLightingRouter.CacheKey("AN16-61", "1.53");

        await router.ApplyAsync("AN16-61", "1.53", Red, () => Task.FromResult(false), () => new StubWriter());
        Assert.Equal(KeyboardLightingTransport.Hid, router.CachedTransport(key));

        // The keyboard came back on WMI — a driver reinstall, a firmware update — so HID is refused and WMI answers.
        var outcome = await router.ApplyAsync(
            "AN16-61", "1.53", Red,
            () => Task.FromResult(true),
            () => new StubWriter(accepts: false));

        Assert.True(outcome.Applied);
        Assert.Equal(KeyboardLightingTransport.Wmi, outcome.Transport);
        Assert.Equal(KeyboardLightingTransport.Wmi, router.CachedTransport(key));
    }

    [Fact]
    public async Task TheCacheStaysBounded()
    {
        var router = new KeyboardLightingRouter();

        for (int i = 0; i <= KeyboardLightingRouter.MaximumCachedMachines; i++)
        {
            await router.ApplyAsync(
                $"AN16-{i:D2}", "1.00", Red,
                () => Task.FromResult(true),
                () => null);
        }

        Assert.Null(router.CachedTransport(KeyboardLightingRouter.CacheKey("AN16-00", "1.00")));
        Assert.Equal(
            KeyboardLightingTransport.Wmi,
            router.CachedTransport(KeyboardLightingRouter.CacheKey(
                $"AN16-{KeyboardLightingRouter.MaximumCachedMachines:D2}", "1.00")));
        Assert.Equal(
            $"{KeyboardLightingRouter.MaximumCachedMachines} machine(s) remembered",
            router.DescribeCacheSize());
    }

    [Fact]
    public void TheCacheKeyIsStableAndCaseInsensitive()
    {
        Assert.Equal("AN16-61|1.53", KeyboardLightingRouter.CacheKey(" an16-61 ", "1.53"));
        Assert.Equal("unknown|unknown", KeyboardLightingRouter.CacheKey(null, null));

        // An unidentifiable machine must not share the entry of an identified one.
        Assert.NotEqual(KeyboardLightingRouter.CacheKey("AN16-61", "1.53"), KeyboardLightingRouter.CacheKey("", "1.53"));
    }

    [Fact]
    public async Task DescribeNamesTheTransportWithoutAColour()
    {
        var router = new KeyboardLightingRouter();
        await router.ApplyAsync("AN16-61", "1.53", Red, () => Task.FromResult(false), () => new StubWriter());

        var lines = router.Describe();

        Assert.Single(lines);
        Assert.StartsWith("AN16-61|1.53: hid", lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain("FF0000", lines[0], StringComparison.OrdinalIgnoreCase);
    }
}
