using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace NitroLite.Core;

public class GameDetectionService : IDisposable
{
    private readonly string _profilesFile;

    private readonly List<GameProfile> _profiles = new();
    private readonly System.Threading.Timer _scanTimer;
    private readonly object _stateLock = new();
    private GameProfile? _activeGameProfile;
    private int _activeProcessId = 0;
    private int _isScanning = 0;
    public string? ProfileLoadError { get; private set; }

    private bool _autoGameModeEnabled = true;

    public bool AutoGameModeEnabled
    {
        get
        {
            lock (_stateLock)
            {
                return _autoGameModeEnabled;
            }
        }
        set
        {
            GameProfile? endedProfile = null;
            lock (_stateLock)
            {
                if (_autoGameModeEnabled == value) return;
                _autoGameModeEnabled = value;

                // When disabled during active gameplay, cleanly terminate active session
                if (!value && _activeGameProfile != null)
                {
                    endedProfile = _activeGameProfile;
                    _activeGameProfile = null;
                    _activeProcessId = 0;
                }
            }

            if (endedProfile != null)
            {
                GameEnded?.Invoke(endedProfile);
            }
        }
    }

    public void SetActiveGameForTesting(GameProfile? profile, int pid)
    {
        lock (_stateLock)
        {
            _activeGameProfile = profile;
            _activeProcessId = pid;
        }
    }

    public void TriggerScanForTesting()
    {
        ScanProcesses();
    }

    public GameProfile? ActiveGameProfile
    {
        get
        {
            lock (_stateLock)
            {
                return _activeGameProfile;
            }
        }
    }

    public int ActiveProcessId
    {
        get
        {
            lock (_stateLock)
            {
                return _activeProcessId;
            }
        }
    }

    public bool IsGameActive
    {
        get
        {
            lock (_stateLock)
            {
                return _activeGameProfile != null;
            }
        }
    }

    public event Action<GameProfile>? GameStarted;
    public event Action<GameProfile>? GameEnded;

    public GameDetectionService() : this(AppPaths.GetDataFile("game_profiles.json")) { }

    internal GameDetectionService(string profilesFile)
    {
        _profilesFile = profilesFile;
        LoadProfiles();

        // Background high-performance thread timer (zero WPF UI thread blocking)
        _scanTimer = new System.Threading.Timer(OnScanTimerTick, null, TimeSpan.FromSeconds(2.0), TimeSpan.FromSeconds(2.0));
    }

    private void OnScanTimerTick(object? state)
    {
        ScanProcesses();
    }

    public List<GameProfile> GetProfiles()
    {
        lock (_profiles)
        {
            return _profiles.OrderBy(p => p.DisplayName).ToList();
        }
    }

    /// <summary>
    /// Looks a profile up by executable name. When <paramref name="installPath"/> is given only the exact
    /// (exe, path) pair matches, so two games that both ship a "Game.exe" stay separate.
    /// </summary>
    public GameProfile? GetProfile(string exeName, string? installPath = null)
    {
        var clean = Path.GetFileNameWithoutExtension(exeName).Trim();
        lock (_profiles)
        {
            if (!string.IsNullOrWhiteSpace(installPath))
            {
                return _profiles.FirstOrDefault(p =>
                    p.ExeName.Equals(clean, StringComparison.OrdinalIgnoreCase) && PathsEqual(p.InstallPath, installPath));
            }
            return _profiles.FirstOrDefault(p => p.ExeName.Equals(clean, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Compares two install paths, tolerating a missing or malformed one.</summary>
    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Finds the profile a save should update. A known install path is part of the identity; a profile that has
    /// no path yet adopts the incoming one instead of turning into a duplicate.
    /// </summary>
    private GameProfile? FindExistingProfile(string cleanExe, string? installPath)
    {
        if (!string.IsNullOrWhiteSpace(installPath))
        {
            var exact = _profiles.FirstOrDefault(p =>
                p.ExeName.Equals(cleanExe, StringComparison.OrdinalIgnoreCase) && PathsEqual(p.InstallPath, installPath));
            if (exact != null) return exact;

            return _profiles.FirstOrDefault(p =>
                p.ExeName.Equals(cleanExe, StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(p.InstallPath));
        }

        return _profiles.FirstOrDefault(p => p.ExeName.Equals(cleanExe, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Adds or updates a profile. Returns false if it could not be written to disk.</summary>
    public bool SaveProfile(GameProfile profile)
    {
        lock (_profiles)
        {
            if (ProfileLoadError != null) return false;
            var cleanExe = Path.GetFileNameWithoutExtension(profile.ExeName).Trim();
            profile.ExeName = cleanExe;

            if (string.IsNullOrWhiteSpace(profile.DisplayName))
            {
                profile.DisplayName = cleanExe;
            }

            var existing = FindExistingProfile(cleanExe, profile.InstallPath);
            if (existing != null)
            {
                existing.DisplayName = profile.DisplayName;
                existing.TargetMode = profile.TargetMode;
                existing.TargetCpuBoost = profile.TargetCpuBoost;
                existing.TargetFanMode = profile.TargetFanMode;
                existing.InstallPath = profile.InstallPath;
                existing.AutoApply = profile.AutoApply;
                existing.TargetAtkMouseProfile = profile.TargetAtkMouseProfile;
            }
            else
            {
                _profiles.Add(profile);
            }
            return PersistProfiles();
        }
    }

    public bool DeleteProfile(string exeName, string? installPath = null)
    {
        lock (_profiles)
        {
            var clean = Path.GetFileNameWithoutExtension(exeName).Trim();
            var item = FindExistingProfile(clean, installPath);
            if (item != null)
            {
                int index = _profiles.IndexOf(item);
                _profiles.RemoveAt(index);
                if (PersistProfiles()) return true;
                _profiles.Insert(index, item);
            }
            return false;
        }
    }

    public static bool IsJunkOrHelperExecutable(string exeName, string? fullPath = null)
    {
        if (string.IsNullOrWhiteSpace(exeName)) return true;
        var name = Path.GetFileNameWithoutExtension(exeName).Trim();
        var path = fullPath ?? "";

        // Known junk / helper / debug / installer / tool substrings
        string[] junkKeywords = new[]
        {
            // Anti-cheat installers, updaters, drivers and daemons
            "anticheat", "easyanticheat", "eaanticheat", "battleye", "beservice", 
            "vanguard", "ricochet", "punkbuster", "equ8", "ace-", "beclient",

            // Sub-processes, web views, crash reporters, redistributables & installers
            "cefsubprocess", "subprocess", "browser", "webview", "crash", "reporter", 
            "unins", "redist", "vcredist", "dxsetup", "prereq", "installer", "updater",
            "patcher", "downloader", "helper", "overlay", "support", "plugin",

            // Developer consoles, tools, test harnesses, config utilities, servers
            "vconsole", "console", "testapp", "benchmark", "editor", 
            "winmtr", "traceroute", "dedicated", "server", "gateway", "iigw",

            // Legacy binaries, trial executables, chess engines, extra wrappers
            "trial", "legacy_app", "stockfish"
        };

        foreach (var kw in junkKeywords)
        {
            if (name.Contains(kw, StringComparison.OrdinalIgnoreCase)) return true;
            if (path.Contains(kw, StringComparison.OrdinalIgnoreCase)) return true;
        }

        // Exact matches for generic utilities
        if (name.Equals("test", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("config", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("setup", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("settings", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("launcher", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("gamelauncher", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("bootstrapper", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Anti-cheat wrappers and launcher stubs
        if (name.EndsWith("_be", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("_zk", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("_eac", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("exec", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    // Programs that are always (or very often) running: a game profile for one of them would keep NitroLite in
    // game mode permanently, so they cannot be added by hand.
    private static readonly HashSet<string> NonGameExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "svchost", "dwm", "csrss", "winlogon", "lsass", "services", "smss", "wininit", "sihost",
        "taskmgr", "searchhost", "startmenuexperiencehost", "shellexperiencehost", "runtimebroker", "ctfmon",
        "conhost", "cmd", "powershell", "pwsh", "windowsterminal", "notepad", "mspaint",
        "chrome", "msedge", "firefox", "opera", "brave", "discord", "spotify", "teams", "ms-teams", "slack",
        "steam", "steamwebhelper", "epicgameslauncher", "battle.net", "eadesktop", "ubisoftconnect", "galaxyclient",
        "code", "devenv", "nitrolite", "nitrosense", "obs64", "onedrive",
        // Background tools that ship inside game libraries (Steam "common" folders) and stay running:
        // a Turbo profile for one of them would pin the machine to Turbo on the desktop.
        "wallpaper32", "wallpaper64", "ui32", "ui64", "vrmonitor", "vrserver", "vrcompositor", "vrwebhelper",
        "losslessscaling", "rtss", "rtsshooksloader64", "msiafterburner", "nvcontainer", "gamebar", "xboxgamebar",
        "medal", "overwolf", "obs32", "voicemod", "armourycrate", "icue", "signalrgb", "openrgb", "hwinfo64"
    };

    /// <summary>
    /// True for programs that are always running and must never get a game profile: a profile for one of
    /// them would keep the machine in game mode permanently. Accepts a bare name or a file name with ".exe".
    /// </summary>
    public static bool IsNonGameExecutable(string? exeName)
    {
        if (string.IsNullOrWhiteSpace(exeName)) return false;
        return NonGameExecutables.Contains(Path.GetFileNameWithoutExtension(exeName));
    }

    /// <summary>
    /// Checks a user-picked file before it becomes a game profile. Returns an error message, or null if it is fine.
    /// </summary>
    public static string? ValidateCustomGameExecutable(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return "Dosya bulunamadı.";
        if (!string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            return "Yalnızca .exe dosyaları oyun olarak eklenebilir.";

        var name = Path.GetFileNameWithoutExtension(path);
        if (IsNonGameExecutable(name))
            return $"'{name}.exe' bir oyun değil ve sürekli çalıştığı için oyun modunu kalıcı olarak açık tutar.";

        try
        {
            var full = Path.GetFullPath(path);
            var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(windowsDir) &&
                full.StartsWith(windowsDir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                return "Windows klasöründeki dosyalar oyun olarak eklenemez.";
        }
        catch
        {
            return "Dosya yolu okunamadı.";
        }
        return null;
    }

    /// <summary>
    /// True if <paramref name="processPath"/> may belong to <paramref name="profile"/>. When the profile has a known
    /// install path that still exists, a same-named exe elsewhere (another program, a copy) does not match.
    /// An unknown process path (access denied, protected game) is accepted so detection keeps working.
    /// </summary>
    public static bool IsPathCompatible(GameProfile profile, string? processPath)
    {
        if (string.IsNullOrWhiteSpace(profile.InstallPath) || string.IsNullOrWhiteSpace(processPath)) return true;
        try
        {
            if (!File.Exists(profile.InstallPath)) return true; // game moved/updated: fall back to the name
            return string.Equals(Path.GetFullPath(profile.InstallPath), Path.GetFullPath(processPath), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, int flags, System.Text.StringBuilder exeName, ref int size);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>Full image path of a process with the least access right Windows offers; null if not allowed.</summary>
    private static string? TryGetProcessPath(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    public static string PickBestGameExecutable(string dirName, List<string> candidateExes)
    {
        if (candidateExes.Count == 0) return "";
        if (candidateExes.Count == 1) return candidateExes[0];

        // 1. Direct match with folder name (e.g. dir="Overlord" -> "Overlord.exe")
        var exactMatch = candidateExes.FirstOrDefault(e => 
            Path.GetFileNameWithoutExtension(e).Equals(dirName, StringComparison.OrdinalIgnoreCase));
        if (exactMatch != null) return exactMatch;

        // 2. Shipping binary for Unreal Engine games (e.g. b1-Win64-Shipping.exe, VALORANT-Win64-Shipping.exe)
        var shipping = candidateExes.FirstOrDefault(e => 
            e.Contains("Shipping", StringComparison.OrdinalIgnoreCase) && 
            !e.Contains("CEF", StringComparison.OrdinalIgnoreCase));
        if (shipping != null) return shipping;

        // 3. Known canonical short game names (e.g. cs2, bfv, TslGame)
        var known = candidateExes.FirstOrDefault(e =>
        {
            var n = Path.GetFileNameWithoutExtension(e).ToLowerInvariant();
            return n == "cs2" || n == "bfv" || n == "tslgame" || n == "gta5" || n == "p5r" || n == "rainbowsix";
        });
        if (known != null) return known;

        // 4. File closest to root directory (least directory depth), then largest file size
        var sortedByDepth = candidateExes
            .OrderBy(e => e.Count(c => c == '\\' || c == '/'))
            .ThenByDescending(e =>
            {
                try { return new FileInfo(e).Length; } catch { return 0L; }
            })
            .ToList();

        return sortedByDepth[0];
    }

    public int SanitizeProfiles()
    {
        lock (_profiles)
        {
            int removed = _profiles.RemoveAll(p => IsJunkOrHelperExecutable(p.ExeName, p.InstallPath));

            // Deduplicate by clean game title: if multiple profiles have the same DisplayName,
            // retain only the single best primary executable
            var grouped = _profiles.GroupBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var group in grouped)
            {
                if (group.Count() > 1)
                {
                    var primary = group.OrderBy(p => GetExecutableRank(p.ExeName)).First();
                    foreach (var extra in group.Where(p => p != primary))
                    {
                        _profiles.Remove(extra);
                        removed++;
                    }
                }
            }

            if (removed > 0)
            {
                PersistProfiles();
            }
            return removed;
        }
    }

    private static int GetExecutableRank(string exeName)
    {
        var lower = exeName.ToLowerInvariant();
        if (lower == "cs2" || lower == "bfv" || lower == "tslgame" || lower == "balatro" || lower == "gta5" || lower == "p5r") return 0;
        if (lower.Contains("shipping")) return 1;
        if (!lower.Contains("_") && !lower.Contains("-")) return 2;
        return 3;
    }

    /// <summary>
    /// Every directory that may hold one game: Steam library "common" folders, Epic install locations and the
    /// usual per-drive game folders. Discovered from the registry and the launchers' own manifests instead of a
    /// hardcoded list, so a library on E: or a non-default Steam install is found too.
    /// </summary>
    private static List<string> CollectGameDirectories()
    {
        var roots = new List<string>();
        var direct = new List<string>();

        static void Add(List<string> list, string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string full;
            try { full = Path.GetFullPath(path); } catch { return; }
            if (!Directory.Exists(full)) return;
            if (!list.Contains(full, StringComparer.OrdinalIgnoreCase)) list.Add(full);
        }

        foreach (var common in GetSteamLibraryCommonFolders()) Add(roots, common);
        foreach (var install in GetEpicInstallLocations()) Add(direct, install);

        foreach (var drive in GetFixedDriveRoots())
        {
            Add(roots, Path.Combine(drive, "Games"));
            Add(roots, Path.Combine(drive, "SteamLibrary", "steamapps", "common"));
            Add(roots, Path.Combine(drive, "Epic Games"));
        }
        Add(roots, @"C:\Riot Games");
        Add(roots, @"C:\Program Files\Epic Games");

        var gameDirs = new List<string>(direct);
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint
        };
        foreach (var root in roots)
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(root, "*", options))
                {
                    if (!gameDirs.Contains(dir, StringComparer.OrdinalIgnoreCase)) gameDirs.Add(dir);
                }
            }
            catch { }
        }

        return gameDirs;
    }

    /// <summary>Steam install roots from the registry, with the classic paths as a fallback.</summary>
    private static List<string> GetSteamRoots()
    {
        var roots = new List<string>();

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            var trimmed = path.Trim().Trim('"');
            if (trimmed.Length == 0) return;
            if (!roots.Contains(trimmed, StringComparer.OrdinalIgnoreCase)) roots.Add(trimmed);
        }

        try
        {
            using var hkcu = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            Add(hkcu?.GetValue("SteamPath") as string);
        }
        catch { }
        try
        {
            using var hklm = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
            Add(hklm?.GetValue("InstallPath") as string);
        }
        catch { }
        try
        {
            using var hklm = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
            Add(hklm?.GetValue("InstallPath") as string);
        }
        catch { }

        Add(@"C:\Program Files (x86)\Steam");
        Add(@"C:\Program Files\Steam");
        return roots;
    }

    /// <summary>Every "steamapps\common" folder Steam knows about, including extra library drives.</summary>
    private static List<string> GetSteamLibraryCommonFolders()
    {
        var result = new List<string>();
        foreach (var root in GetSteamRoots())
        {
            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            string content;
            try
            {
                if (!File.Exists(vdf)) continue;
                content = File.ReadAllText(vdf);
            }
            catch { continue; }

            // Newer Steam writes "path" "D:\SteamLibrary"; older builds used the library index as the key.
            foreach (Match m in Regex.Matches(content, @"""(?:path|\d+)""\s+""([^""]+)"""))
            {
                var library = m.Groups[1].Value.Replace(@"\\", @"\");
                // App ids and sizes are also quoted number pairs; only real paths are worth probing.
                if (!library.Contains(':') && !library.StartsWith('/')) continue;
                var common = Path.Combine(library, "steamapps", "common");
                if (Directory.Exists(common) && !result.Contains(common, StringComparer.OrdinalIgnoreCase))
                    result.Add(common);
            }
        }
        return result;
    }

    /// <summary>Install locations from the Epic Games Launcher manifests (*.item).</summary>
    private static List<string> GetEpicInstallLocations()
    {
        var result = new List<string>();
        var manifests = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(manifests)) return result;

        string[] files;
        try { files = Directory.GetFiles(manifests, "*.item"); }
        catch { return result; }

        foreach (var file in files)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (!doc.RootElement.TryGetProperty("InstallLocation", out var element)) continue;
                var location = element.GetString();
                if (string.IsNullOrWhiteSpace(location)) continue;
                if (Directory.Exists(location) && !result.Contains(location, StringComparer.OrdinalIgnoreCase))
                    result.Add(location);
            }
            catch { }
        }
        return result;
    }

    /// <summary>Roots of the fixed drives, so a game library on any drive is scanned.</summary>
    private static List<string> GetFixedDriveRoots()
    {
        var result = new List<string>();
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch { return result; }

        foreach (var drive in drives)
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                result.Add(drive.RootDirectory.FullName);
            }
            catch { }
        }
        return result;
    }

    public int ScanAndAddInstalledGames()
    {
        if (ProfileLoadError != null) return 0;
        int addedCount = 0;
        // Scan folders
        foreach (var dir in CollectGameDirectories())
        {
            var dirName = Path.GetFileName(dir);
            if (dirName.Equals("Steamworks Shared", StringComparison.OrdinalIgnoreCase) ||
                dirName.Equals("Steam Controller Configs", StringComparison.OrdinalIgnoreCase) ||
                dirName.Equals("DirectX", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var allExes = Directory.GetFiles(dir, "*.exe", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    // One unreadable subfolder must not hide the whole game.
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint
                });
                var validCandidates = new List<string>();

                foreach (var exe in allExes)
                {
                    try
                    {
                        var fi = new FileInfo(exe);
                        if (fi.Length < 1024 * 1024) continue; // Skip small helper stubs (< 1MB)
                        var name = Path.GetFileNameWithoutExtension(exe);
                        if (IsJunkOrHelperExecutable(name, exe)) continue;
                        // Same rule as the manual picker: a program that is always running would keep
                        // the machine in game mode permanently.
                        if (IsNonGameExecutable(name)) continue;
                        validCandidates.Add(exe);
                    }
                    catch { }
                }

                if (validCandidates.Count > 0)
                {
                    // Pick the single authoritative canonical executable for this game directory
                    var bestExe = PickBestGameExecutable(dirName, validCandidates);
                    var bestName = Path.GetFileNameWithoutExtension(bestExe);
                    var friendlyName = CleanGameDisplayName(dirName, bestName);

                    lock (_profiles)
                    {
                        if (!_profiles.Any(p => p.ExeName.Equals(bestName, StringComparison.OrdinalIgnoreCase) ||
                                                p.DisplayName.Equals(friendlyName, StringComparison.OrdinalIgnoreCase)))
                        {
                            _profiles.Add(new GameProfile
                            {
                                ExeName = bestName,
                                DisplayName = friendlyName,
                                TargetMode = OperatingMode.Turbo,
                                TargetCpuBoost = DetermineOptimalCpuBoost(bestName),
                                TargetFanMode = FanMode.Auto,
                                InstallPath = bestExe,
                                AutoApply = true
                            });
                            addedCount++;
                        }
                    }
                }
            }
            catch { }
        }

        // Candidates are already junk-filtered and de-duplicated before they are added above.
        // Do not run SanitizeProfiles() here: it would silently delete profiles the user added by hand
        // (e.g. a game whose exe name contains "server", "console" or "launcher").
        if (addedCount > 0)
        {
            PersistProfiles();
        }

        return addedCount;
    }

    private static string CleanGameDisplayName(string dirName, string exeName)
    {
        if (!string.IsNullOrWhiteSpace(dirName) && dirName.Length > 2 && !dirName.Equals("bin", StringComparison.OrdinalIgnoreCase))
        {
            if (dirName.Contains("Counter-Strike", StringComparison.OrdinalIgnoreCase)) return "Counter-Strike 2";
            if (dirName.Contains("PUBG", StringComparison.OrdinalIgnoreCase)) return "PUBG: BATTLEGROUNDS";
            return dirName;
        }
        return exeName;
    }

    public static CpuBoostMode DetermineOptimalCpuBoost(string exeName)
    {
        // 2D lightweight games run cool and quiet at base clock
        if (exeName.Contains("balatro", StringComparison.OrdinalIgnoreCase))
        {
            return CpuBoostMode.Disabled;
        }

        // Fast esports shooters (CS2, Valorant, Siege, Apex) require high CPU draw-call throughput
        if (exeName.Contains("cs2", StringComparison.OrdinalIgnoreCase) ||
            exeName.Contains("valorant", StringComparison.OrdinalIgnoreCase) ||
            exeName.Contains("rainbowsix", StringComparison.OrdinalIgnoreCase) ||
            exeName.Contains("apex", StringComparison.OrdinalIgnoreCase))
        {
            return CpuBoostMode.Aggressive;
        }

        // AAA heavy titles benefit from balanced boost
        return CpuBoostMode.Efficient;
    }

    /// <summary>
    /// True when <paramref name="pid"/> is still running under <paramref name="exeName"/>. Reads the process list
    /// instead of opening a handle on the process, so an elevated or protected game stays observable. Returns true
    /// when the list itself cannot be read: dropping a live session on a guess would flap the mode.
    /// </summary>
    internal static bool IsProcessIdAliveByName(string exeName, int pid)
    {
        Process[]? named = null;
        try
        {
            named = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exeName));
            foreach (var candidate in named)
            {
                try
                {
                    if (candidate.Id == pid) return true;
                }
                catch { }
            }
            return false;
        }
        catch
        {
            return true;
        }
        finally
        {
            if (named != null)
            {
                for (int i = 0; i < named.Length; i++)
                {
                    try { named[i]?.Dispose(); } catch { }
                }
            }
        }
    }

    private void ScanProcesses()
    {
        if (Interlocked.CompareExchange(ref _isScanning, 1, 0) != 0) return;
        try
        {
            // FAST-PATH: If a game is ALREADY ACTIVE, monitor ONLY that specific process PID.
            // Takes ~0.05 ms and eliminates 100% of full-system process scanning while gaming!
            // Must execute even if AutoGameModeEnabled is toggled off to ensure clean exit detection!
            int activePid;
            GameProfile? currentProfile;
            lock (_stateLock)
            {
                activePid = _activeProcessId;
                currentProfile = _activeGameProfile;
            }

            if (currentProfile != null && activePid > 0)
            {
                bool isStillAlive = false;
                try
                {
                    using var activeProc = Process.GetProcessById(activePid);
                    // Verify process hasn't exited and PID wasn't recycled by an unrelated process
                    if (!activeProc.HasExited && activeProc.ProcessName.Equals(currentProfile.ExeName, StringComparison.OrdinalIgnoreCase))
                    {
                        isStillAlive = true;
                    }
                }
                catch (ArgumentException)
                {
                    // Process is no longer running (PID has exited)
                    isStillAlive = false;
                }
                catch
                {
                    // Access denied or a transient state. Assuming "alive" here pinned game mode forever when the
                    // game closed while its handle stayed unreadable, so fall back to a name lookup, which needs
                    // no handle on the game process.
                    isStillAlive = IsProcessIdAliveByName(currentProfile.ExeName, activePid);
                }

                if (isStillAlive)
                {
                    // Active game is running smoothly. Zero full-system scanning required!
                    return;
                }

                // Active game process has terminated
                bool shouldNotifyEnded = false;
                lock (_stateLock)
                {
                    if (_activeGameProfile != null && _activeProcessId == activePid)
                    {
                        _activeGameProfile = null;
                        _activeProcessId = 0;
                        shouldNotifyEnded = true;
                    }
                }

                if (shouldNotifyEnded)
                {
                    GameEnded?.Invoke(currentProfile);
                }
                return;
            }

        // SLOW-PATH (Only when NO game is active): Scan process list to detect newly launched games
        if (!AutoGameModeEnabled) return;

        Process[]? processes = null;
        try
        {
            processes = Process.GetProcesses();
            GameProfile? foundProfile = null;
            int foundPid = 0;

            lock (_profiles)
            {
                foreach (var p in processes)
                {
                    try
                    {
                        var name = p.ProcessName;
                        var cleanName = Path.GetFileNameWithoutExtension(name);
                        // A profile for a program that is always running (OBS, Steam, a browser) would pin the
                        // machine to game mode permanently. The scan and the manual picker already refuse these,
                        // but a profile saved by an older version can still be in game_profiles.json.
                        if (IsNonGameExecutable(cleanName)) continue;
                        var match = _profiles.FirstOrDefault(prof => 
                            prof.AutoApply && (
                                prof.ExeName.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                                prof.ExeName.Equals(cleanName, StringComparison.OrdinalIgnoreCase) ||
                                Path.GetFileNameWithoutExtension(prof.ExeName).Equals(cleanName, StringComparison.OrdinalIgnoreCase)
                            ));

                        // Name matched: confirm the location when the profile knows where the game is installed.
                        if (match != null && !IsPathCompatible(match, TryGetProcessPath(p.Id)))
                        {
                            match = null;
                        }

                        if (match != null)
                        {
                            foundProfile = match;
                            foundPid = p.Id;
                            break;
                        }
                    }
                    catch { }
                }
            }

            if (foundProfile != null && foundPid > 0)
            {
                bool shouldNotify = false;
                lock (_stateLock)
                {
                    if (_autoGameModeEnabled)
                    {
                        _activeGameProfile = foundProfile;
                        _activeProcessId = foundPid;
                        shouldNotify = true;
                    }
                }

                if (shouldNotify)
                {
                    GameStarted?.Invoke(foundProfile);
                }
            }
        }
        catch { }
        finally
        {
            // Fully dispose all Process instances to eliminate kernel process handle leaks
            if (processes != null)
            {
                for (int i = 0; i < processes.Length; i++)
                {
                    try { processes[i]?.Dispose(); } catch { }
                }
            }
        }
        }
        finally
        {
            Interlocked.Exchange(ref _isScanning, 0);
        }
    }

    private void LoadProfiles()
    {
        lock (_profiles)
        {
            _profiles.Clear();
            string json;
            try
            {
                json = File.ReadAllText(_profilesFile);
            }
            catch (FileNotFoundException) { json = ""; }
            catch (DirectoryNotFoundException) { json = ""; }
            catch (Exception ex)
            {
                // Unreadable (locked, permissions): keep the error and refuse to overwrite what cannot be read.
                ProfileLoadError = $"Oyun profilleri okunamadı: {ex.Message}";
                AppLog.Warn("Oyun profilleri okunamadı", $"{_profilesFile}: {ex}");
                App.WriteLog("Oyun profili dosyası", ex.Message);
                return;
            }

            // An empty file is a truncated write, not a profile list. It used to raise a JsonException and block
            // profile saving for good; there is no user data in it, so start from the defaults instead.
            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    var list = JsonSerializer.Deserialize<List<GameProfile>>(json);
                    if (list != null && list.All(p => p != null && !string.IsNullOrWhiteSpace(p.ExeName)))
                    {
                        _profiles.AddRange(list);
                        return;
                    }
                    throw new JsonException("Geçersiz oyun profili listesi.");
                }
                catch (Exception ex)
                {
                    // A corrupt file is kept as-is and saving stays blocked: it may hold profiles the user added.
                    ProfileLoadError = $"Oyun profilleri okunamadı: {ex.Message}";
                    AppLog.Warn("Oyun profilleri okunamadı", $"{_profilesFile}: {ex}");
                    App.WriteLog("Oyun profili dosyası", ex.Message);
                    return;
                }
            }

            // Default fallback popular game profiles
            AddDefaultProfiles();
            PersistProfiles();
        }
    }

    private void AddDefaultProfiles()
    {
        _profiles.Add(new GameProfile
        {
            ExeName = "cs2",
            DisplayName = "Counter-Strike 2",
            TargetMode = OperatingMode.Turbo,
            TargetCpuBoost = CpuBoostMode.Aggressive,
            TargetFanMode = FanMode.Auto
        });

        _profiles.Add(new GameProfile
        {
            ExeName = "VALORANT-Win64-Shipping",
            DisplayName = "Valorant",
            TargetMode = OperatingMode.Turbo,
            TargetCpuBoost = CpuBoostMode.Aggressive,
            TargetFanMode = FanMode.Auto
        });

        _profiles.Add(new GameProfile
        {
            ExeName = "bfv",
            DisplayName = "Battlefield V",
            TargetMode = OperatingMode.Turbo,
            TargetCpuBoost = CpuBoostMode.Aggressive,
            TargetFanMode = FanMode.Auto
        });

        _profiles.Add(new GameProfile
        {
            ExeName = "P5R",
            DisplayName = "Persona 5 Royal",
            TargetMode = OperatingMode.Extreme,
            TargetCpuBoost = CpuBoostMode.Efficient,
            TargetFanMode = FanMode.Auto
        });

        _profiles.Add(new GameProfile
        {
            ExeName = "TslGame",
            DisplayName = "PUBG: BATTLEGROUNDS",
            TargetMode = OperatingMode.Turbo,
            TargetCpuBoost = CpuBoostMode.Aggressive,
            TargetFanMode = FanMode.Max
        });

        _profiles.Add(new GameProfile
        {
            ExeName = "Balatro",
            DisplayName = "Balatro",
            TargetMode = OperatingMode.Default,
            TargetCpuBoost = CpuBoostMode.Disabled,
            TargetFanMode = FanMode.Auto
        });

        _profiles.Add(new GameProfile
        {
            ExeName = "Cyberpunk2077",
            DisplayName = "Cyberpunk 2077",
            TargetMode = OperatingMode.Turbo,
            TargetCpuBoost = CpuBoostMode.Aggressive,
            TargetFanMode = FanMode.Auto
        });

        _profiles.Add(new GameProfile
        {
            ExeName = "b1-Win64-Shipping",
            DisplayName = "Black Myth: Wukong",
            TargetMode = OperatingMode.Turbo,
            TargetCpuBoost = CpuBoostMode.Aggressive,
            TargetFanMode = FanMode.Auto
        });

        _profiles.Add(new GameProfile
        {
            ExeName = "GTA5",
            DisplayName = "Grand Theft Auto V",
            TargetMode = OperatingMode.Turbo,
            TargetCpuBoost = CpuBoostMode.Efficient,
            TargetFanMode = FanMode.Auto
        });

        _profiles.Add(new GameProfile
        {
            ExeName = "RainbowSix",
            DisplayName = "Tom Clancy's Rainbow Six Siege",
            TargetMode = OperatingMode.Turbo,
            TargetCpuBoost = CpuBoostMode.Aggressive,
            TargetFanMode = FanMode.Auto
        });
    }

    private bool PersistProfiles()
    {
        lock (_profiles)
        {
            if (ProfileLoadError != null) return false;
            var tempFile = _profilesFile + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                var json = JsonSerializer.Serialize(_profiles, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(tempFile, json);
                File.Move(tempFile, _profilesFile, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                App.WriteLog("Oyun profilleri kaydedilemedi", ex.Message);
                return false;
            }
            finally
            {
                try { File.Delete(tempFile); } catch { }
            }
        }
    }

    public void Dispose()
    {
        _scanTimer.Dispose();
    }
}
