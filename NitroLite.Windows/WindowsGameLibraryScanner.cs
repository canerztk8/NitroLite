using System.IO;
using Microsoft.Win32;
using NitroLite.Core;

namespace NitroLite.Windows;

/// <summary>
/// The installed-game library on Windows: Steam, Epic and the usual per-drive game folders.
/// </summary>
/// <remarks>
/// <para>
/// Only the folder discovery is Windows-specific. Which file in a folder is the game, which names are helpers and
/// which boost mode a title wants are the same questions on both platforms, so they live in
/// <see cref="GameLibraryRules"/> and this class supplies the folders.
/// </para>
/// <para>
/// The libraries are discovered from the launchers' own records — the Steam registry keys and
/// <c>libraryfolders.vdf</c>, the Epic manifests — rather than from a hard-coded list, so a library on <c>E:</c> or a
/// Steam install outside <c>Program Files</c> is found too. A hard-coded list would silently miss every machine that
/// did not take the default path, which is most of them.
/// </para>
/// </remarks>
public sealed class WindowsGameLibraryScanner : IGameLibraryScanner
{
    /// <summary>Folder names under a library root that are not games.</summary>
    private static readonly string[] NotAGameFolders =
    [
        "Steamworks Shared", "Steam Controller Configs", "DirectX", "Riot Client", "Launcher"
    ];

    /// <summary>Every game this machine has installed, one entry per game folder.</summary>
    public IReadOnlyList<ScannedGame> ScanInstalledGames() => ScanGameFolders(CollectGameDirectories());

    /// <summary>One entry per folder in <paramref name="gameDirectories"/>; each folder is one game.</summary>
    internal static IReadOnlyList<ScannedGame> ScanGameFolders(IEnumerable<string> gameDirectories)
    {
        var found = new List<ScannedGame>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in gameDirectories)
        {
            var dirName = Path.GetFileName(dir);
            if (string.IsNullOrWhiteSpace(dirName)) continue;
            if (NotAGameFolders.Any(skip => dirName.Equals(skip, StringComparison.OrdinalIgnoreCase))) continue;

            string[] allExes;
            try
            {
                allExes = Directory.GetFiles(dir, "*.exe", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    // One unreadable subfolder must not hide the whole game.
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint
                });
            }
            catch (Exception ex)
            {
                AppLog.Warn("The game folder could not be read", $"{dir}: {ex.Message}");
                continue;
            }

            var candidates = allExes.Where(GameLibraryRules.IsGameCandidate).ToList();
            if (candidates.Count == 0) continue;

            var bestExe = GameLibraryRules.PickBestGameExecutable(dirName, candidates);
            if (string.IsNullOrWhiteSpace(bestExe)) continue;

            var bestName = Path.GetFileNameWithoutExtension(bestExe);
            var friendlyName = GameLibraryRules.CleanGameDisplayName(dirName, bestName);

            // Two libraries can hold the same title; the first one found wins, as the WPF scan did.
            if (!seen.Add(bestName)) continue;

            found.Add(new ScannedGame(bestName, friendlyName, bestExe));
        }

        return found;
    }

    /// <summary>Checks a file the user picked before it becomes a game profile.</summary>
    public string? ValidateCustomExecutable(string path) =>
        GameLibraryRules.ValidateCustomExecutable(path, Environment.GetFolderPath(Environment.SpecialFolder.Windows));

    /// <summary>The boost behaviour a title wants.</summary>
    public CpuBoostMode DetermineOptimalCpuBoost(string exeName) =>
        GameLibraryRules.DetermineOptimalCpuBoost(exeName);

    /// <summary>
    /// Every directory that holds one game: each subfolder of a library root (<see cref="CollectLibraryRoots"/>) and
    /// every Epic install location, which the manifest already names per game.
    /// </summary>
    /// <remarks>
    /// The roots themselves used to be returned here, and the scan picks one executable per folder — so a whole Steam
    /// library became a single "game" named after its <c>common</c> folder, and every other title in it was lost.
    /// </remarks>
    public static List<string> CollectGameDirectories()
    {
        var dirs = ExpandLibraryRoots(CollectLibraryRoots());
        dirs.AddRange(GetEpicInstallLocations());
        return dirs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The game folders inside each library root: its direct subfolders, minus the ones that are not games.</summary>
    internal static List<string> ExpandLibraryRoots(IEnumerable<string> roots)
    {
        var games = new List<string>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint
        };

        // A root nested in another root (X:\Games\SteamLibrary\...) is a library of its own, not a game of the outer one.
        var rootList = roots.ToList();
        var rootSet = new HashSet<string>(rootList.Select(r => r.TrimEnd('\\', '/')), StringComparer.OrdinalIgnoreCase);

        foreach (var root in rootList)
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(root, "*", options))
                {
                    var name = Path.GetFileName(dir);
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (rootSet.Contains(dir.TrimEnd('\\', '/'))) continue;
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
    /// The folders that hold many games, one per subfolder: Steam library <c>common</c> folders and the usual per-drive
    /// game folders.
    /// </summary>
    public static List<string> CollectLibraryRoots()
    {
        var dirs = new List<string>();

        foreach (var root in GetSteamRoots())
        {
            var common = Path.Combine(root, "steamapps", "common");
            if (Directory.Exists(common)) dirs.Add(common);
        }

        dirs.AddRange(GetSteamLibraryCommonFolders());

        foreach (var drive in GetFixedDriveRoots())
        {
            foreach (var name in new[] { "Games", Path.Combine("SteamLibrary", "steamapps", "common"), "Epic Games" })
            {
                var candidate = Path.Combine(drive, name);
                if (Directory.Exists(candidate)) dirs.Add(candidate);
            }
        }

        // The two launchers that install outside Program Files by default.
        foreach (var extra in new[] { @"C:\Riot Games", @"C:\Program Files\Epic Games" })
        {
            if (Directory.Exists(extra)) dirs.Add(extra);
        }

        return dirs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The Steam install roots, from the registry and then from the two default paths.</summary>
    public static List<string> GetSteamRoots()
    {
        var roots = new List<string>();

        // The registry values are writable by the user (HKCU) and are probed as directories, so they get the same
        // check as a library path out of libraryfolders.vdf: a drive-letter local path, nothing else.
        void Add(string? path)
        {
            if (!SteamLibraryPaths.TryNormalize(path, out var normalized)) return;
            if (Directory.Exists(normalized)) roots.Add(normalized);
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            Add(key?.GetValue("SteamPath") as string);
        }
        catch (Exception ex) { AppLog.Warn("The Steam registry entry could not be read", ex.Message); }

        foreach (var (hive, path) in new (RegistryKey, string)[]
                 {
                     (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam"),
                     (Registry.LocalMachine, @"SOFTWARE\Valve\Steam")
                 })
        {
            try
            {
                using var key = hive.OpenSubKey(path);
                Add(key?.GetValue("InstallPath") as string);
            }
            catch (Exception ex) { AppLog.Warn("The Steam registry entry could not be read", ex.Message); }
        }

        Add(@"C:\Program Files (x86)\Steam");
        Add(@"C:\Program Files\Steam");

        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The <c>common</c> folder of every Steam library, read from <c>libraryfolders.vdf</c>. A second library on another
    /// drive is only recorded there, so the registry alone would miss it.
    /// </summary>
    public static List<string> GetSteamLibraryCommonFolders()
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

    /// <summary>The install locations Epic recorded in its own manifests.</summary>
    public static List<string> GetEpicInstallLocations()
    {
        var locations = new List<string>();

        var manifests = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");

        if (!Directory.Exists(manifests)) return locations;

        try
        {
            foreach (var file in Directory.GetFiles(manifests, "*.item"))
            {
                try
                {
                    using var document = System.Text.Json.JsonDocument.Parse(BoundedTextFile.ReadAllText(file));
                    if (document.RootElement.TryGetProperty("InstallLocation", out var location) &&
                        // The manifest is a file other programs write; a UNC or device path would open a network
                        // session on the probe, so only a local drive path is followed.
                        SteamLibraryPaths.TryNormalize(location.GetString(), out var path) &&
                        Directory.Exists(path))
                    {
                        locations.Add(path);
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Epic manifest could not be read", $"{file}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("The Epic manifest folder could not be read", ex.Message);
        }

        return locations.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The roots of the fixed drives, so a library on <c>D:</c> is found without being asked for.</summary>
    public static List<string> GetFixedDriveRoots()
    {
        var roots = new List<string>();

        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType == DriveType.Fixed && drive.IsReady) roots.Add(drive.RootDirectory.FullName);
                }
                catch (Exception ex) { AppLog.Warn("The drive could not be read", ex.Message); }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("The drive list could not be read", ex.Message);
        }

        return roots;
    }
}
