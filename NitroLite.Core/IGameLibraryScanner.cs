namespace NitroLite.Core;

/// <summary>One game the library scan found, reduced to what a profile needs.</summary>
/// <param name="ExeName">The executable's file name without a directory, which is what detection matches on.</param>
/// <param name="DisplayName">The friendly title the profile list shows.</param>
/// <param name="InstallPath">The full path to the executable, or empty when the scan could not read one.</param>
public sealed record ScannedGame(string ExeName, string DisplayName, string InstallPath);

/// <summary>
/// Finds the games installed on this machine and checks a file the user picked by hand.
/// </summary>
/// <remarks>
/// <para>
/// The scan half is the only part of game detection that has to know where a platform keeps its libraries: the
/// registry and the Epic manifests on Windows, the Steam roots under the home directory on Linux. Everything after
/// that — which executable in a folder is the game, which names are helpers, which boost mode a title wants — is the
/// same reasoning on both, so it lives in <see cref="GameLibraryRules"/> and this interface only supplies the folders.
/// </para>
/// <para>
/// An interface rather than a direct call so the Game Profiles page can be tested against a fixed library: a test that
/// had to install a game to check "the scan added a profile" would never run in CI.
/// </para>
/// </remarks>
public interface IGameLibraryScanner
{
    /// <summary>
    /// Every game this machine has installed, one entry per game folder. Empty when no library was found, which is
    /// the normal answer on a machine with no launcher installed.
    /// </summary>
    IReadOnlyList<ScannedGame> ScanInstalledGames();

    /// <summary>
    /// Checks a file the user picked before it becomes a game profile. Returns the reason it was refused, or null
    /// when it is fine.
    /// </summary>
    string? ValidateCustomExecutable(string path);

    /// <summary>
    /// The boost behaviour a title wants. A 2D card game runs cool at base clock; an esports shooter needs the
    /// draw-call throughput. The answer is a starting point the user can override per game.
    /// </summary>
    CpuBoostMode DetermineOptimalCpuBoost(string exeName);
}
