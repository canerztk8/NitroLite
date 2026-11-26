using NitroLite.Core;
using NitroLite.Desktop;
using Xunit;

namespace NitroLite.Tests;

public class NitroKeyListenerSwitchTests
{
    private sealed class CountingListener : INitroKeyListener
    {
        public bool IsSupported => true;
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public bool Running { get; private set; }
        public event EventHandler? NitroKeyPressed { add { } remove { } }
        public void Start() { Starts++; Running = true; }
        public void Stop() { Stops++; Running = false; }
        public void Dispose() { }
    }

    [Fact]
    public async Task TurningTheSettingOff_StopsTheListener_AndOnStartsItAgain()
    {
        var listener = new CountingListener();
        var sw = new NitroKeyListenerSwitch(listener);

        sw.Apply(true);
        await sw.Idle;
        Assert.True(listener.Running);

        sw.Apply(false);
        await sw.Idle;
        Assert.False(listener.Running);

        sw.Apply(true);
        await sw.Idle;
        Assert.True(listener.Running);
        Assert.Equal(2, listener.Starts);
        Assert.Equal(1, listener.Stops);
    }

    [Fact]
    public async Task RepeatingTheSameValue_DoesNotStartOrStopTwice()
    {
        var listener = new CountingListener();
        var sw = new NitroKeyListenerSwitch(listener);

        sw.Apply(true);
        sw.Apply(true);
        sw.Apply(false);
        sw.Apply(false);
        await sw.Idle;

        Assert.Equal(1, listener.Starts);
        Assert.Equal(1, listener.Stops);
    }

    [Fact]
    public async Task OffFromTheStart_NeverStartsTheListener()
    {
        var listener = new CountingListener();
        var sw = new NitroKeyListenerSwitch(listener);

        sw.Apply(false);
        await sw.Idle;

        Assert.Equal(0, listener.Starts);
        Assert.Equal(0, listener.Stops);
    }

    [Fact]
    public async Task OnOffThenALiveReloadStop_ThenOn_StartsTheListenerAgain()
    {
        // The listener is on, the user turns the setting off, predator_v4 is applied live (it only stops the listener,
        // the setting being off), and the setting goes on again: the switch must still start it.
        var listener = new CountingListener();
        var sw = new NitroKeyListenerSwitch(listener);

        sw.Apply(true);
        sw.Apply(false);
        await sw.Idle;
        listener.Stop();

        sw.Apply(true);
        await sw.Idle;

        Assert.True(listener.Running);
        Assert.Equal(2, listener.Starts);
    }
}
