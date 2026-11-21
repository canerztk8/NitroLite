using NitroLite.Linux;
using Xunit;

namespace NitroLite.Tests;

/// <summary>Whether the desktop shows tray icons, from the session bus.</summary>
public class LinuxTrayProbeTests
{
    private sealed class Runner : ICommandRunner
    {
        private readonly string? _busctl;
        private readonly CommandResult _answer;

        public Runner(string? busctl, CommandResult answer)
        {
            _busctl = busctl;
            _answer = answer;
        }

        public IReadOnlyList<string>? LastArgs { get; private set; }
        public string? Find(string name) => name == "busctl" ? _busctl : null;

        public CommandResult Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout)
        {
            LastArgs = arguments;
            return _answer;
        }
    }

    [Fact]
    public void AHostOnTheSessionBus_MeansATray()
    {
        var runner = new Runner("/usr/bin/busctl", new CommandResult(0, "b true", ""));
        Assert.True(LinuxTrayProbe.IsAvailable(runner));
        Assert.Contains("org.kde.StatusNotifierWatcher", runner.LastArgs!);
        Assert.Contains("--user", runner.LastArgs!);
    }

    [Fact]
    public void NoHost_MeansNoTray() =>
        Assert.False(LinuxTrayProbe.IsAvailable(new Runner("/usr/bin/busctl", new CommandResult(0, "b false", ""))));

    [Fact]
    public void AQuestionThatCannotBeAsked_AssumesATray()
    {
        Assert.True(LinuxTrayProbe.IsAvailable(new Runner(null, default)));
        Assert.True(LinuxTrayProbe.IsAvailable(new Runner("/usr/bin/busctl", new CommandResult(1, "", "no bus"))));
    }
}
