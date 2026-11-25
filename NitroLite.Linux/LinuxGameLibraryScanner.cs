using System.IO;
using NitroLite.Core;

namespace NitroLite.Linux;

/// <summary>
/// The installed-game library on Linux: the Steam roots under the home directory, plus the Proton titles inside them.
/// </summary>
/// <remarks>
/// <para>
/// Only the folder discovery is Linux-specific. Which file in a folder is the game, which names are helpers and which
/// boost mode a title wants are the same questions on both platforms, so they live in
/// <see cref="GameLibraryRules"/> and this class supplies the folders.
/// </para>
/// <para>
/// A native Linux game has no extension and a Proton title is a Windows <c>.exe</c>, so both are candidates. The
/// executable bit is what separates a game from the data files beside it, and it is checked rather than assumed: a
/// Steam <c>common</c> folder holds hundreds of <c>.so</c> and <c>.pak</c> files that are not programs.
/// </para>
/// <para>
/// The home directory is a constructor argument rather than <see cref="Environment.GetFolderPath"/> so a test can point
/// the scan at a temporary tree instead of the machine it runs on.
/// </para>
/// </remarks>
public sealed class LinuxGameLibraryScanner : IGameLibraryScanner
{
    /// <summary>Folder names under a Steam <c>common</c> directory that are not games.</summary>
    private static readonly string[] NotAGameFolders =
    [
        "Steamworks Shared", "SteamLinuxRuntime", "SteamLinuxRuntime_sniper",
        "SteamLinuxRuntime_soldier", "Steam Controller Configs", "DirectX"
    ];

    private readonly string _home;

    /// <param name="homeDirectory">The user's home directory. Null reads it from the environment.</param>
    public LinuxGameLibraryScanner(string? homeDirectory = null)
    {
        _home = string.IsNullOrWhiteSpace(homeDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : homeDirectory;
    }

    /// <summary>Every game this machine has installed, one entry per game folder.</summary>
    public IReadOnlyList<ScannedGame> ScanInstalledGames()
    {
        var found = new List<ScannedGame>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in ExpandLibraryRoots(CollectGameDirectories()))
        {
            var dirName = Path.GetFileName(dir);
            if (string.IsNullOrWhiteSpace(dirName)) continue;
            if (NotAGameFolders.Any(skip => dirName.Equals(skip, StringComparison.OrdinalIgnoreCase))) continue;

            var candidates = new List<string>();
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint
                }))
                {
                    if (IsGameExecutable(file)) candidates.Add(file);
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("The game folder could not be read", $"{dir}: {ex.Message}");
                continue;
            }

            if (candidates.Count == 0) continue;

            var bestExe = GameLibraryRules.PickBestGameExecutable(dirName, candidates);
            if (string.IsNullOrWhiteSpace(bestExe)) continue;

            var bestName = Path.GetFileNameWithoutExtension(bestExe);
            var friendlyName = GameLibraryRules.CleanGameDisplayName(dirName, bestName);

            if (!seen.Add(bestName)) continue;

            found.Add(new ScannedGame(bestName, friendlyName, bestExe));
        }

        return found;
    }

    /// <summary>
    /// Checks a file the user picked. A native Linux build has no extension, so the <c>.exe</c> rule the Windows
    /// picker applies would refuse every game on this platform; the executable bit is the equivalent test.
    /// </summary>
    public string? ValidateCustomExecutable(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return Loc.T("Dosya bulunamadı.");

        var name = Path.GetFileNameWithoutExtension(path);
        if (GameLibraryRules.IsNonGameExecutable(name))
            return Loc.T($"'{name}' bir oyun değil ve sürekli çalıştığı için oyun modunu kalıcı olarak açık tutar.");

        if (!IsExecutable(path))
            return Loc.T("Seçilen dosya çalıştırılabilir değil.");

        return null;
    }

    /// <summary>The boost behaviour a title wants.</summary>
    public CpuBoostMode DetermineOptimalCpuBoost(string exeName) =>
        GameLibraryRules.DetermineOptimalCpuBoost(exeName);

    /// <summary>
    /// The game folders inside each library root: its direct subfolders, minus the ones that are not games and the
    /// ones that are roots of their own (<c>~/Games/Steam</c> inside <c>~/Games</c>).
    /// </summary>
    /// <remarks>
    /// The scan picks one executable per folder, so handing it a whole Steam <c>common</c> folder made the library a
    /// single "game" named "common" and lost every other title in it.
    /// </remarks>
    internal static List<string> ExpandLibraryRoots(IEnumerable<string> roots)
    {
        var games = new List<string>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint
        };

        var rootList = roots.ToList();
        var rootSet = new HashSet<string>(rootList.Select(r => Path.TrimEndingDirectorySeparator(r)), StringComparer.OrdinalIgnoreCase);

        foreach (var root in rootList)
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(root, "*", options))
                {
                    var name = Path.GetFileName(dir);
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (rootSet.Contains(Path.TrimEndingDirectorySeparator(dir))) continue;
                    if (NotAGameFolders.Any(skip => name.Equals(skip, StringComparison.OrdinalIgnoreCase))) continue;
                    games.Add(dir);
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("The game library could not be read", $"{root}: {ex.Message}");
            }
        }

        return games.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The library roots, each holding one game per subfolder: the Steam roots under the home directory, the
    /// <c>common</c> folder of every library recorded in <c>libraryfolders.vdf</c>, and the usual game folders.
    /// </summary>
    public List<string> CollectGameDirectories()
    {
        var dirs = new List<string>();

        foreach (var root in GetSteamRoots())
        {
            var common = Path.Combine(root, "steamapps", "common");
            if (Directory.Exists(common)) dirs.Add(common);
        }

        dirs.AddRange(GetSteamLibraryCommonFolders());

        foreach (var extra in new[] { "Games", "Games/Steam", "Games/Epic" })
        {
            var candidate = Path.Combine(_home, extra);
            if (Directory.Exists(candidate)) dirs.Add(candidate);
        }

        return dirs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The Steam install roots. Both layouts exist in the wild: the older <c>~/.steam/steam</c> and the newer
    /// <c>~/.local/share/Steam</c>, and a machine can have either or both.
    /// </summary>
    public List<string> GetSteamRoots()
    {
        var roots = new List<string>();

        foreach (var candidate in new[]
                 {
                     Path.Combine(_home, ".steam", "steam"),
                     Path.Combine(_home, ".local", "share", "Steam"),
                     Path.Combine(_home, ".steam", "root"),
                     Path.Combine(_home, "snap", "steam", "common", ".local", "share", "Steam")
                 })
        {
            if (Directory.Exists(candidate)) roots.Add(candidate);
        }

        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The <c>common</c> folder of every Steam library, read from <c>libraryfolders.vdf</c>. A second library on another
    /// drive is only recorded there, so the default roots alone would miss it.
    /// </summary>
    public List<string> GetSteamLibraryCommonFolders()
    {
        var folders = new List<string>();

        foreach (var root in GetSteamRoots())
        {
            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;

            try
            {
                var text = BoundedTextFile.ReadAllText(vdf);
                foreach (System.Text.RegularExpressions.Match match in
                         System.Text.RegularExpressions.Regex.Matches(text, "\"(?:path|\\d+)\"\\s+\"([^\"]+)\""))
                {
                    var library = match.Groups[1].Value.Replace(@"\\", @"\");

                    // The value is a path out of a text file the user can edit, so it is validated before it becomes
                    // a directory probe: a relative path would resolve against the working directory, and a UNC path
                    // would open an SMB session.
                    if (!SteamLibraryPaths.TryNormalize(library, out var normalized)) continue;

                    var common = Path.Combine(normalized, "steamapps", "common");
                    if (Directory.Exists(common)) folders.Add(common);
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("The Steam library file could not be read", $"{vdf}: {ex.Message}");
            }
        }

        return folders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// True when a file is worth considering as a game: a program rather than a data file, big enough not to be a
    /// launcher stub, and not a helper or a program that is always running.
    /// </summary>
    private static bool IsGameExecutable(string path)
    {
        if (!IsExecutable(path)) return false;

        // A Proton title is a Windows executable, so the size floor and the junk filter apply to it unchanged.
        return GameLibraryRules.IsGameCandidate(path);
    }

    /// <summary>True when the file carries any execute bit.</summary>
    private static bool IsExecutable(string path)
    {
        // The test suite runs this class on Windows, where the Unix mode call is not available at all. The extension
        // is the only answer that platform can give, and it is the right one for a Proton title.
        if (OperatingSystem.IsWindows())
            return string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase);

        try
        {
            var mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch
        {
            // A file system without Unix modes (a mounted NTFS volume) cannot answer; the extension is the fallback.
            return string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase);
        }
    }
}
