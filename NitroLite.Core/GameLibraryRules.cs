using System.IO;

namespace NitroLite.Core;

/// <summary>
/// The platform-independent half of game detection: which file in a game folder is the game, which names are helpers,
/// and which boost mode a title wants.
/// </summary>
/// <remarks>
/// <para>
/// Only the folder discovery differs between Windows and Linux — the registry and the Epic manifests on one, the Steam
/// roots under the home directory on the other. Everything after that is the same reasoning, so it lives here once
/// instead of twice, and a rule fixed on one platform is fixed on both.
/// </para>
/// <para>
/// The junk list is deliberately long. A Steam <c>common</c> folder holds the game, its anti-cheat installer, its crash
/// reporter, three redistributables and a launcher stub, and a profile for any of those would pin the machine to Turbo
/// on the desktop. The cost of a false positive is one game the user has to add by hand; the cost of a false negative
/// is a laptop that never leaves game mode.
/// </para>
/// </remarks>
public static class GameLibraryRules
{
    /// <summary>
    /// Files smaller than this are launcher stubs, not games. A real executable is megabytes; a stub that only starts
    /// another process is a few kilobytes.
    /// </summary>
    public const long MinimumGameExecutableBytes = 1024 * 1024;

    /// <summary>Substrings that mark a file as a helper, an installer, a crash reporter or a developer tool.</summary>
    private static readonly string[] JunkKeywords =
    [
        // Anti-cheat installers, updaters, drivers and daemons
        "anticheat", "easyanticheat", "eaanticheat", "battleye", "beservice",
        "vanguard", "ricochet", "punkbuster", "equ8", "ace-", "beclient",

        // Sub-processes, web views, crash reporters, redistributables and installers
        "cefsubprocess", "subprocess", "browser", "webview", "crash", "reporter",
        "unins", "redist", "vcredist", "dxsetup", "prereq", "installer", "updater",
        "patcher", "downloader", "helper", "overlay", "support", "plugin",

        // Developer consoles, tools, test harnesses, config utilities, servers
        "vconsole", "console", "testapp", "benchmark", "editor",
        "winmtr", "traceroute", "dedicated", "server", "gateway", "iigw",

        // Legacy binaries, trial executables, chess engines, extra wrappers
        "trial", "legacy_app", "stockfish"
    ];

    /// <summary>Names that are generic utilities rather than games, matched exactly.</summary>
    private static readonly string[] JunkExactNames =
    [
        "test", "config", "setup", "settings", "launcher", "gamelauncher", "bootstrapper"
    ];

    /// <summary>
    /// True for a file that is not the game itself: an anti-cheat installer, a crash reporter, a redistributable, a
    /// launcher stub or a developer tool.
    /// </summary>
    /// <param name="exeName">The file name, with or without a directory and an extension.</param>
    /// <param name="fullPath">
    /// The whole path, when the caller has it. The keywords are matched against it too, because a helper often sits in
    /// a folder that names it (<c>EasyAntiCheat\EasyAntiCheat.exe</c>) while the file name alone is ambiguous.
    /// </param>
    public static bool IsJunkOrHelperExecutable(string? exeName, string? fullPath = null)
    {
        if (string.IsNullOrWhiteSpace(exeName)) return true;

        var name = Path.GetFileNameWithoutExtension(exeName).Trim();
        var path = fullPath ?? "";

        foreach (var keyword in JunkKeywords)
        {
            if (name.Contains(keyword, StringComparison.OrdinalIgnoreCase)) return true;
            if (path.Contains(keyword, StringComparison.OrdinalIgnoreCase)) return true;
        }

        foreach (var exact in JunkExactNames)
        {
            if (name.Equals(exact, StringComparison.OrdinalIgnoreCase)) return true;
        }

        // Anti-cheat wrappers and launcher stubs: the game's own binary never carries these suffixes.
        return name.EndsWith("_be", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("_zk", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("_eac", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("exec", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True for a program that is always running and must never own a game profile: a profile for one of them would
    /// keep the machine in game mode permanently.
    /// </summary>
    public static bool IsNonGameExecutable(string? exeName) => GameProfileService.IsNonGameExecutable(exeName);

    /// <summary>
    /// Checks a file the user picked before it becomes a game profile. Returns the reason it was refused, or null when
    /// it is fine.
    /// </summary>
    /// <param name="path">The file the picker returned.</param>
    /// <param name="systemDirectory">
    /// The operating system's own folder, when the platform has one. A file under it is never a game, and adding one
    /// would put a system process in the detection table. Null on a platform without the concept.
    /// </param>
    public static string? ValidateCustomExecutable(string? path, string? systemDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return Loc.T("Dosya bulunamadı.");

        if (!string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            return Loc.T("Yalnızca .exe dosyaları oyun olarak eklenebilir.");

        var name = Path.GetFileNameWithoutExtension(path);
        if (IsNonGameExecutable(name))
            return Loc.T($"'{name}.exe' bir oyun değil ve sürekli çalıştığı için oyun modunu kalıcı olarak açık tutar.");

        try
        {
            var full = Path.GetFullPath(path);
            if (!string.IsNullOrEmpty(systemDirectory))
            {
                var root = systemDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (root.Length > 0 &&
                    full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return Loc.T("Windows klasöründeki dosyalar oyun olarak eklenemez.");
                }
            }
        }
        catch
        {
            return Loc.T("Dosya yolu okunamadı.");
        }

        return null;
    }

    /// <summary>
    /// True when <paramref name="processPath"/> may belong to <paramref name="profile"/>. When the profile has a known
    /// install path that still exists, a same-named executable elsewhere (another program, a copy) does not match. An
    /// unknown process path — access denied, a protected game — is accepted so detection keeps working.
    /// </summary>
    public static bool IsPathCompatible(GameProfile profile, string? processPath)
    {
        if (string.IsNullOrWhiteSpace(profile.InstallPath) || string.IsNullOrWhiteSpace(processPath)) return true;

        try
        {
            // A game that was moved or updated falls back to the name: a stale path must not stop detection.
            if (!File.Exists(profile.InstallPath)) return true;
            return string.Equals(
                Path.GetFullPath(profile.InstallPath),
                Path.GetFullPath(processPath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Picks the one executable in a game folder that is the game, from the candidates that survived the junk filter.
    /// </summary>
    /// <remarks>
    /// The order matters and is the order a person would use: the file named after the folder, then the Unreal
    /// <c>-Shipping</c> binary, then a known short name, then whatever sits closest to the root and is largest. A
    /// folder with a hundred candidates is normal — an Unreal game ships its editor, its server and its crash handler
    /// beside the game — so "largest" alone would pick the wrong one.
    /// </remarks>
    public static string PickBestGameExecutable(string dirName, IReadOnlyList<string> candidateExes)
    {
        if (candidateExes.Count == 0) return "";
        if (candidateExes.Count == 1) return candidateExes[0];

        var exactMatch = candidateExes.FirstOrDefault(e =>
            Path.GetFileNameWithoutExtension(e).Equals(dirName, StringComparison.OrdinalIgnoreCase));
        if (exactMatch != null) return exactMatch;

        var shipping = candidateExes.FirstOrDefault(e =>
            e.Contains("Shipping", StringComparison.OrdinalIgnoreCase) &&
            !e.Contains("CEF", StringComparison.OrdinalIgnoreCase));
        if (shipping != null) return shipping;

        var known = candidateExes.FirstOrDefault(e =>
        {
            var n = Path.GetFileNameWithoutExtension(e).ToLowerInvariant();
            return n is "cs2" or "bfv" or "tslgame" or "gta5" or "p5r" or "rainbowsix";
        });
        if (known != null) return known;

        return candidateExes
            .OrderBy(e => e.Count(c => c == '\\' || c == '/'))
            .ThenByDescending(e =>
            {
                try { return new FileInfo(e).Length; } catch { return 0L; }
            })
            .First();
    }

    /// <summary>
    /// The title a scanned folder should be listed under. The folder name is usually the game, but a few launchers use
    /// a folder name that is not what the game is called, and <c>bin</c> is never a title.
    /// </summary>
    public static string CleanGameDisplayName(string? dirName, string exeName)
    {
        if (!string.IsNullOrWhiteSpace(dirName) && dirName.Length > 2 &&
            !dirName.Equals("bin", StringComparison.OrdinalIgnoreCase))
        {
            if (dirName.Contains("Counter-Strike", StringComparison.OrdinalIgnoreCase)) return "Counter-Strike 2";
            if (dirName.Contains("PUBG", StringComparison.OrdinalIgnoreCase)) return "PUBG: BATTLEGROUNDS";
            return dirName;
        }

        return exeName;
    }

    /// <summary>
    /// The boost behaviour a title wants. A 2D card game runs cool and quiet at base clock; an esports shooter needs
    /// the draw-call throughput; everything else gets the balanced middle.
    /// </summary>
    public static CpuBoostMode DetermineOptimalCpuBoost(string? exeName)
    {
        if (string.IsNullOrWhiteSpace(exeName)) return CpuBoostMode.Efficient;

        if (exeName.Contains("balatro", StringComparison.OrdinalIgnoreCase)) return CpuBoostMode.Disabled;

        if (exeName.Contains("cs2", StringComparison.OrdinalIgnoreCase) ||
            exeName.Contains("valorant", StringComparison.OrdinalIgnoreCase) ||
            exeName.Contains("rainbowsix", StringComparison.OrdinalIgnoreCase) ||
            exeName.Contains("apex", StringComparison.OrdinalIgnoreCase))
        {
            return CpuBoostMode.Aggressive;
        }

        return CpuBoostMode.Efficient;
    }

    /// <summary>
    /// How good a candidate a profile's executable is when two profiles claim the same title. Lower is better: the
    /// canonical short name first, then the Unreal shipping binary, then a plain name, then anything with a separator
    /// in it (which is usually a helper the filter missed).
    /// </summary>
    public static int GetExecutableRank(string? exeName)
    {
        var lower = (exeName ?? "").ToLowerInvariant();
        if (lower is "cs2" or "bfv" or "tslgame" or "balatro" or "gta5" or "p5r") return 0;
        if (lower.Contains("shipping")) return 1;
        if (!lower.Contains('_') && !lower.Contains('-')) return 2;
        return 3;
    }

    /// <summary>
    /// Removes the profiles that should never have existed — a helper, an installer, a duplicate of a title that is
    /// already listed — and returns how many went.
    /// </summary>
    /// <remarks>
    /// Deliberately not called by the scan. The scan already filters its own candidates, and running this afterwards
    /// would silently delete a profile the user added by hand for a game whose name happens to contain "server" or
    /// "console". It belongs on the load path, where a settings file written by an older build is cleaned up.
    /// </remarks>
    public static int SanitizeProfiles(IList<GameProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        var removed = 0;
        for (int i = profiles.Count - 1; i >= 0; i--)
        {
            var profile = profiles[i];
            if (profile == null || IsJunkOrHelperExecutable(profile.ExeName, profile.InstallPath))
            {
                profiles.RemoveAt(i);
                removed++;
            }
        }

        // Two profiles for one title: keep the one whose executable is the better candidate.
        foreach (var group in profiles
                     .Where(p => p != null)
                     .GroupBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
                     .ToList())
        {
            if (group.Count() <= 1) continue;

            var primary = group.OrderBy(p => GetExecutableRank(p.ExeName)).First();
            foreach (var extra in group.Where(p => !ReferenceEquals(p, primary)).ToList())
            {
                profiles.Remove(extra);
                removed++;
            }
        }

        return removed;
    }

    /// <summary>
    /// True when a file is worth considering as a game at all: big enough to be a program rather than a stub, and not
    /// a helper or a program that is always running.
    /// </summary>
    public static bool IsGameCandidate(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < MinimumGameExecutableBytes) return false;

            var name = Path.GetFileNameWithoutExtension(path);
            if (IsJunkOrHelperExecutable(name, path)) return false;
            return !IsNonGameExecutable(name);
        }
        catch
        {
            // One unreadable file must not hide the whole folder.
            return false;
        }
    }
}
