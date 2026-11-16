using System.Diagnostics;
using System.IO;
using NitroLite.Core;
using NitroLite.Desktop;
using NitroLite.Desktop.ViewModels;
using NitroLite.Linux;
using NitroLite.Windows;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The hardware Nitro key: one physical button, two platform routes, one event.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here touches a real device. The Windows listener is asserted through its published code sets, the Linux
/// listener through a fake <c>/dev/input</c> tree, and the window activation through the gate the shell uses — which is
/// the point of the gate existing: the alternative is a test that has to build an Avalonia window to prove that a
/// disabled switch does nothing.
/// </para>
/// <para>
/// The debounce is driven by an injected clock. A test that slept 400 ms to prove a 400 ms window would be a test
/// nobody runs, and it would still be flaky on a loaded machine.
/// </para>
/// </remarks>
public class NitroKeyTests
{
    // =============================================================================================================
    // Fixtures.
    // =============================================================================================================

    /// <summary>A listener whose support and events the test decides.</summary>
    private sealed class FakeNitroKeyListener : INitroKeyListener
    {
        public bool IsSupported { get; set; }

        public bool Started { get; private set; }

        public event EventHandler? NitroKeyPressed;

        public void Start() => Started = true;

        public void Stop() => Started = false;

        public void Raise() => NitroKeyPressed?.Invoke(this, EventArgs.Empty);

        public void Dispose() { }
    }

    /// <summary>One <c>/dev/input/event*</c> device, fed from a queue instead of a kernel.</summary>
    private sealed class FakeEvdevDevice : IEvdevDevice
    {
        private readonly Queue<byte[]> _batches = new();
        private readonly object _gate = new();

        public FakeEvdevDevice(string name, params int[] keys)
        {
            Name = name;
            Keys = keys;
        }

        public string Name { get; }

        public int[] Keys { get; }

        public int EventSize => 24;

        public bool SupportsKey(int code) => Array.IndexOf(Keys, code) >= 0;

        public void Enqueue(params byte[][] batches)
        {
            lock (_gate)
            {
                foreach (var batch in batches) _batches.Enqueue(batch);
            }
        }

        public int Read(Span<byte> buffer)
        {
            lock (_gate)
            {
                // An empty queue reads as a closed device, which is what ends the listener's loop in a test.
                if (_batches.Count == 0) return 0;

                var next = _batches.Dequeue();
                next.CopyTo(buffer);
                return next.Length;
            }
        }

        public void Dispose() { }
    }

    private sealed class FakeEvdevSource : IEvdevSource
    {
        private readonly IEvdevDevice[] _devices;

        public FakeEvdevSource(params IEvdevDevice[] devices) => _devices = devices;

        public IReadOnlyList<IEvdevDevice> OpenAll() => _devices;
    }

    /// <summary>One <c>struct input_event</c> per tuple, packed at the offset a 64-bit build uses.</summary>
    private static byte[] Events(params (ushort Type, ushort Code, int Value)[] events)
    {
        const int eventSize = 24;
        var buffer = new byte[eventSize * events.Length];

        for (int i = 0; i < events.Length; i++)
        {
            int offset = (i * eventSize) + (eventSize - 8);
            BitConverter.GetBytes(events[i].Type).CopyTo(buffer, offset);
            BitConverter.GetBytes(events[i].Code).CopyTo(buffer, offset + 2);
            BitConverter.GetBytes(events[i].Value).CopyTo(buffer, offset + 4);
        }

        return buffer;
    }

    private static string TempSettingsPath() =>
        Path.Combine(Path.GetTempPath(), $"nitrolite_nitrokey_{Guid.NewGuid():N}.json");

    private static MainViewModel CreateViewModel(INitroKeyListener? nitroKey, AppSettings? settings = null) =>
        new(settings ?? new AppSettings(), null, null, TempSettingsPath(), nitroKey: nitroKey);

    // =============================================================================================================
    // 1. The debounce.
    // =============================================================================================================

    [Fact]
    public void NitroKeyDebounce_SuppressesDuplicateWithin400ms()
    {
        // The clock is in Stopwatch ticks, which is what the guard compares against, so the test advances it by the
        // same unit the production clock uses rather than by milliseconds.
        long ticksPerMillisecond = Stopwatch.Frequency / 1000;
        long now = 0;

        var guard = new NitroKeyDebounceGuard(400, () => now);

        Assert.True(guard.ShouldAccept(), "the first press must always pass");

        now += 399 * ticksPerMillisecond;
        Assert.False(guard.ShouldAccept());

        now += 1 * ticksPerMillisecond;
        Assert.True(guard.ShouldAccept(), "a press 400 ms after the last accepted one is a new press");
    }

    [Fact]
    public void NitroKeyDebounce_AcceptsAfterTheWindowAndAfterAReset()
    {
        long ticksPerMillisecond = Stopwatch.Frequency / 1000;
        long now = 0;

        var guard = new NitroKeyDebounceGuard(400, () => now);

        Assert.True(guard.ShouldAccept());
        now += 1000 * ticksPerMillisecond;
        Assert.True(guard.ShouldAccept());

        // Reset is what a listener calls when it restarts, so the first press after a restart is never swallowed.
        guard.Reset();
        Assert.True(guard.ShouldAccept());
    }

    [Fact]
    public void NitroKeyDebounce_DefaultWindowIsFourHundredMilliseconds()
    {
        Assert.Equal(400, new NitroKeyDebounceGuard().WindowMilliseconds);
        Assert.Equal(400, NitroKeyDebounceGuard.DefaultWindowMilliseconds);
    }

    // =============================================================================================================
    // 2. The window activation.
    // =============================================================================================================

    [Fact]
    public void NitroKey_OpensAndActivatesWindowWhenEnabled()
    {
        int activations = 0;
        var gate = new NitroKeyActivationGate(() => true, () => activations++);

        Assert.True(gate.HandlePress());
        Assert.Equal(1, activations);
        Assert.Equal(1, gate.Activations);
    }

    [Fact]
    public void NitroKey_IgnoredWhenDisabled()
    {
        int activations = 0;
        var gate = new NitroKeyActivationGate(() => false, () => activations++);

        Assert.False(gate.HandlePress());
        Assert.Equal(0, activations);
        Assert.Equal(0, gate.Activations);
    }

    [Fact]
    public void NitroKey_ReadsTheSettingOnEveryPress()
    {
        // The switch can be turned off while the app is in the tray, so the gate must not capture the answer once.
        bool enabled = false;
        int activations = 0;
        var gate = new NitroKeyActivationGate(() => enabled, () => activations++);

        Assert.False(gate.HandlePress());

        enabled = true;
        Assert.True(gate.HandlePress());
        Assert.Equal(1, activations);
    }

    // =============================================================================================================
    // 3. The settings row.
    // =============================================================================================================

    [Fact]
    public void NitroKeyToggle_HiddenWhenUnsupported()
    {
        // No listener at all: a platform that could not build one.
        Assert.False(CreateViewModel(null).NitroKeyAvailable);

        // A listener that looked and found nothing: a desktop, a non-Acer laptop, a Linux user outside the input group.
        Assert.False(CreateViewModel(new FakeNitroKeyListener { IsSupported = false }).NitroKeyAvailable);

        // The machine has the button, so the row exists.
        Assert.True(CreateViewModel(new FakeNitroKeyListener { IsSupported = true }).NitroKeyAvailable);
    }

    [Fact]
    public void NitroKeyEnabled_DefaultsToTrueAndPersists()
    {
        Assert.True(new AppSettings().NitroKeyEnabled);

        var path = TempSettingsPath();
        var settings = new AppSettings();
        var vm = new MainViewModel(settings, null, null, path, nitroKey: new FakeNitroKeyListener { IsSupported = true });

        Assert.True(vm.NitroKeyEnabled);

        vm.NitroKeyEnabled = false;
        Assert.False(settings.NitroKeyEnabled);

        var reloaded = SettingsManager.LoadSettings(path, path + ".bak", null).Settings;
        Assert.False(reloaded.NitroKeyEnabled);
    }

    // =============================================================================================================
    // 4. The Linux route.
    // =============================================================================================================

    [Fact]
    public void LinuxNitroKeyListener_ReportsUnsupportedWithoutAnAcerDevice()
    {
        var keyboard = new FakeEvdevDevice("AT Translated Set 2 keyboard", 30, 31);
        using var listener = new LinuxNitroKeyListener(new FakeEvdevSource(keyboard));

        Assert.False(listener.IsSupported);
        Assert.Empty(listener.DeviceNames);
    }

    [Fact]
    public void LinuxNitroKeyListener_RaisesOnKeyProg1()
    {
        var device = new FakeEvdevDevice(LinuxNitroKeyListener.AcerDeviceName, EvdevEvent.KeyProg1);
        device.Enqueue(Events((EvdevEvent.TypeKey, EvdevEvent.KeyProg1, EvdevEvent.ValuePress)));

        using var listener = new LinuxNitroKeyListener(new FakeEvdevSource(device));
        Assert.True(listener.IsSupported);
        Assert.Equal(new[] { LinuxNitroKeyListener.AcerDeviceName }, listener.DeviceNames);

        using var raised = new ManualResetEventSlim(false);
        listener.NitroKeyPressed += (_, _) => raised.Set();
        listener.Start();

        Assert.True(raised.Wait(TimeSpan.FromSeconds(5)), "the press was never raised");
    }

    [Fact]
    public void LinuxNitroKeyListener_RaisesOnKeyProg3AndIgnoresTheRelease()
    {
        var device = new FakeEvdevDevice(LinuxNitroKeyListener.AcerDeviceName, EvdevEvent.KeyProg3);
        device.Enqueue(
            Events((EvdevEvent.TypeKey, EvdevEvent.KeyProg3, 0)),
            Events((EvdevEvent.TypeKey, EvdevEvent.KeyProg3, EvdevEvent.ValuePress)));

        using var listener = new LinuxNitroKeyListener(new FakeEvdevSource(device));

        int presses = 0;
        using var raised = new ManualResetEventSlim(false);
        listener.NitroKeyPressed += (_, _) =>
        {
            Interlocked.Increment(ref presses);
            raised.Set();
        };

        listener.Start();

        Assert.True(raised.Wait(TimeSpan.FromSeconds(5)), "the press was never raised");
        Assert.Equal(1, presses);
    }

    [Fact]
    public void LinuxNitroKeyListener_CollapsesADoubleReport()
    {
        // The same physical press reported twice, which is what a driver that emits both a press and a repeat looks
        // like. One press must be one event.
        var device = new FakeEvdevDevice(LinuxNitroKeyListener.AcerDeviceName, EvdevEvent.KeyProg1);
        device.Enqueue(
            Events((EvdevEvent.TypeKey, EvdevEvent.KeyProg1, EvdevEvent.ValuePress)),
            Events((EvdevEvent.TypeKey, EvdevEvent.KeyProg1, EvdevEvent.ValuePress)));

        using var listener = new LinuxNitroKeyListener(new FakeEvdevSource(device));

        int presses = 0;
        listener.NitroKeyPressed += (_, _) => Interlocked.Increment(ref presses);
        listener.Start();

        // The fake device reads as closed once its queue is empty, so the loop has finished by the time this returns.
        Thread.Sleep(200);
        Assert.Equal(1, presses);
    }

    [Theory]
    [InlineData(24)]
    [InlineData(16)]
    public void EvdevEvent_ParsesBothWordSizes(int eventSize)
    {
        var buffer = new byte[eventSize];
        int offset = eventSize - 8;
        BitConverter.GetBytes((ushort)EvdevEvent.TypeKey).CopyTo(buffer, offset);
        BitConverter.GetBytes((ushort)EvdevEvent.KeyProg1).CopyTo(buffer, offset + 2);
        BitConverter.GetBytes(EvdevEvent.ValuePress).CopyTo(buffer, offset + 4);

        Assert.True(EvdevEvent.TryParse(buffer, eventSize, out var parsed));
        Assert.True(parsed.IsNitroKeyPress);
    }

    [Fact]
    public void EvdevEvent_RejectsAShortBuffer()
    {
        Assert.False(EvdevEvent.TryParse(new byte[4], 24, out _));
    }

    // =============================================================================================================
    // 5. The Windows route.
    // =============================================================================================================

    [Fact]
    public void WindowsNitroKeyListener_AcceptsTheLaunchAppKeysAndConsumerUsages()
    {
        // VK_LAUNCH_APP1 and VK_LAUNCH_APP2 are what some models map the button to; the consumer usages behind them
        // are what the HID report carries.
        Assert.Contains((ushort)0xB6, WindowsNitroKeyListener.NitroKeyVirtualKeys);
        Assert.Contains((ushort)0xB7, WindowsNitroKeyListener.NitroKeyVirtualKeys);
        Assert.Contains((ushort)0x0194, WindowsNitroKeyListener.NitroKeyUsages);
        Assert.Contains((ushort)0x0195, WindowsNitroKeyListener.NitroKeyUsages);
    }

    [Fact]
    public void WindowsNitroKeyListener_DoesNotTreatEveryAcerEventAsTheNitroKey()
    {
        // The same WMI class carries the fan-mode and lighting keys. Opening the window for those would be a bug the
        // user could not turn off, so the accepted codes are a set rather than "any event".
        Assert.NotEmpty(WindowsNitroKeyListener.NitroKeyEventCodes);
        Assert.DoesNotContain(0u, WindowsNitroKeyListener.NitroKeyEventCodes);
    }
}
