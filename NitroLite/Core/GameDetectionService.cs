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
    private static readonly string ProfilesFile = AppPaths.GetDataFile("game_profiles.json");

    private readonly List<GameProfile> _profiles = new();
    private readonly System.Threading.Timer _scanTimer;
    private readonly object _stateLock = new();
    private GameProfile? _activeGameProfile;
    private int _activeProcessId = 0;
    private int _isScanning = 0;

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

    public GameDetectionService()
    {
        LoadProfiles();

        // Move recursive filesystem game discovery to background task to guarantee instant UI startup
        Task.Run(() =>
        {
            try
            {
                ScanAndAddInstalledGames();
            }
            catch { }
        });

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

    public GameProfile? GetProfile(string exeName)
    {
        var clean = Path.GetFileNameWithoutExtension(exeName).Trim();
        lock (_profiles)
        {
            return _profiles.FirstOrDefault(p => p.ExeName.Equals(clean, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Adds or updates a profile. Returns false if it could not be written to disk.</summary>
    public bool SaveProfile(GameProfile profile)
    {
        lock (_profiles)
        {
            var cleanExe = Path.GetFileNameWithoutExtension(profile.ExeName).Trim();
            profile.ExeName = cleanExe;

            if (string.IsNullOrWhiteSpace(profile.DisplayName))
            {
                profile.DisplayName = cleanExe;
            }

            var existing = _profiles.FirstOrDefault(p => p.ExeName.Equals(cleanExe, StringComparison.OrdinalIgnoreCase));
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

    public bool DeleteProfile(string exeName)
    {
        lock (_profiles)
        {
            var clean = Path.GetFileNameWithoutExtension(exeName).Trim();
            var item = _profiles.FirstOrDefault(p => p.ExeName.Equals(clean, StringComparison.OrdinalIgnoreCase));
            if (item != null)
            {
                _profiles.Remove(item);
                PersistProfiles();
                return true;
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
        "code", "devenv", "nitrolite", "nitrosense", "obs64", "onedrive"
    };

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
        if (NonGameExecutables.Contains(name))
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

    public int ScanAndAddInstalledGames()
    {
        int addedCount = 0;
        var searchRoots = new List<string>
        {
            @"C:\Program Files (x86)\Steam\steamapps\common",
            @"D:\SteamLibrary\steamapps\common",
            @"D:\Games",
            @"C:\Riot Games",
            @"C:\Program Files\Epic Games",
            @"C:\Games"
        };

        // Also check if Steam has other libraries via libraryfolders.vdf
        try
        {
            var vdfPath = @"C:\Program Files (x86)\Steam\steamapps\libraryfolders.vdf";
            if (File.Exists(vdfPath))
            {
                var content = File.ReadAllText(vdfPath);
                var matches = Regex.Matches(content, @"""path""\s+""([^""]+)""");
                foreach (Match m in matches)
                {
                    var p = m.Groups[1].Value.Replace(@"\\", @"\");
                    var common = Path.Combine(p, "steamapps", "common");
                    if (Directory.Exists(common) && !searchRoots.Contains(common, StringComparer.OrdinalIgnoreCase))
                    {
                        searchRoots.Add(common);
                    }
                }
            }
        }
        catch { }

        // Scan folders
        foreach (var root in searchRoots)
        {
            if (!Directory.Exists(root)) continue;

            try
            {
                var subDirs = Directory.GetDirectories(root);
                foreach (var dir in subDirs)
                {
                    var dirName = Path.GetFileName(dir);
                    if (dirName.Equals("Steamworks Shared", StringComparison.OrdinalIgnoreCase) ||
                        dirName.Equals("Steam Controller Configs", StringComparison.OrdinalIgnoreCase) ||
                        dirName.Equals("DirectX", StringComparison.OrdinalIgnoreCase))
                        continue;

                    try
                    {
                        var allExes = Directory.GetFiles(dir, "*.exe", SearchOption.AllDirectories);
                        var validCandidates = new List<string>();

                        foreach (var exe in allExes)
                        {
                            try
                            {
                                var fi = new FileInfo(exe);
                                if (fi.Length < 1024 * 1024) continue; // Skip small helper stubs (< 1MB)
                                var name = Path.GetFileNameWithoutExtension(exe);
                                if (IsJunkOrHelperExecutable(name, exe)) continue;
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
                    // Access denied or transient state; assume alive to prevent false toggling
                    isStillAlive = true;
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
            try
            {
                if (File.Exists(ProfilesFile))
                {
                    var json = File.ReadAllText(ProfilesFile);
                    var list = JsonSerializer.Deserialize<List<GameProfile>>(json);
                    if (list != null && list.Count > 0)
                    {
                        // Load user profiles as-is; never prune them silently on startup.
                        _profiles.AddRange(list);
                        return;
                    }
                }
            }
            catch { }

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
        try
        {
            var json = JsonSerializer.Serialize(_profiles, new JsonSerializerOptions { WriteIndented = true });
            var tempFile = ProfilesFile + ".tmp";
            File.WriteAllText(tempFile, json);
            File.Move(tempFile, ProfilesFile, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _scanTimer.Dispose();
    }
}
