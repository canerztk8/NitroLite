namespace NitroLite.Core;

/// <summary>
/// Lets a one-shot setup run exactly once, even when two callers arrive at the same moment.
/// </summary>
/// <remarks>
/// <para>
/// A plain <c>bool</c> flag is enough when the only caller is a UI event, and it was what the window used. It is not
/// enough when the same setup is reachable from two places: a launch that starts in the tray calls the setup directly
/// because the window is never shown, and the window's own <c>Opened</c> handler calls it again the moment the user
/// brings the window back. Two passes subscribe the same handlers twice, build a second overlay window that nothing
/// closes, and replay the game-session recovery over a game that is still running.
/// </para>
/// <para>
/// <see cref="Interlocked.Exchange(ref int, int)"/> is what makes the second caller lose rather than both of them
/// reading <c>false</c> before either writes <c>true</c>.
/// </para>
/// </remarks>
public sealed class OneShotGate
{
    private int _entered;

    /// <summary>True once a caller has entered. Read-only: it never resets.</summary>
    public bool HasRun => Volatile.Read(ref _entered) != 0;

    /// <summary>
    /// True for the first caller only. Every later call returns false, including one that arrives while the first
    /// pass is still running.
    /// </summary>
    public bool TryEnter() => Interlocked.Exchange(ref _entered, 1) == 0;
}
