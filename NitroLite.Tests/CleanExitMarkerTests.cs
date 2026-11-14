using System.IO;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The marker is the only thing that tells a fresh process the previous one died with the fans pinned at a custom
/// speed. It had no test, and it is the difference between a crashed run leaving the fans at 30 % and the next start
/// handing them back to the firmware.
/// </summary>
public class CleanExitMarkerTests
{
    [Fact]
    public void AMarkerLeftBehindMeansThePreviousRunDidNotExitCleanly()
    {
        CleanExitMarker.MarkCleanExit();
        Assert.False(CleanExitMarker.PreviousRunWasUnclean());

        CleanExitMarker.MarkRunning();
        Assert.True(CleanExitMarker.PreviousRunWasUnclean());

        CleanExitMarker.MarkCleanExit();
        Assert.False(CleanExitMarker.PreviousRunWasUnclean());
    }

    [Fact]
    public void MarkingACleanExitTwiceIsHarmless()
    {
        // The exit path calls this from two places (the normal exit and the session-ending path), and whichever runs
        // second must not throw.
        CleanExitMarker.MarkCleanExit();
        CleanExitMarker.MarkCleanExit();
        Assert.False(CleanExitMarker.PreviousRunWasUnclean());
    }

    [Fact]
    public void TheMarkerLivesInTheDataFolder()
    {
        // Not next to the executable: that folder is read-only under Program Files, and the marker would then never
        // be written, so every start would look like a crash and reset the fans.
        Assert.Equal(Path.Combine(AppPaths.DataDirectory, CleanExitMarker.FileName), CleanExitMarker.FilePath);
    }
}
