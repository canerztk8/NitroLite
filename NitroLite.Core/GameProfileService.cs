using NitroLite.Core.Hardware;

namespace NitroLite.Core;

/// <summary>What one detection pass decided.</summary>
public enum GameDetectionChange
{
    /// <summary>Nothing changed: no game started and none ended.</summary>
    None = 0,

    /// <summary>A configured game appeared and its profile was applied.</summary>
    Started = 1,

    /// <summary>The running game disappeared and the user's own settings were put back.</summary>
    Ended = 2
}

/// <summary>The outcome of one <see cref="GameProfileService.PollAsync"/> call.</summary>
/// <param name="Change">Whether a session started, ended, or nothing happened.</param>
/// <param name="Profile">The game that started. Null unless <paramref name="Change"/> is Started.</param>
/// <param name="Previous">The game that ended. Null unless <paramref name="Change"/> is Ended.</param>
/// <param name="Snapshot">
/// What the session replaced. On Started it is the state the game is overriding; on Ended it is the state that was
/// just written back, which is what the caller needs to put its own controls back in step.
/// </param>
public sealed record GameDetectionResult(
    GameDetectionChange Change,
    GameProfile? Profile,
    GameProfile? Previous,
    GameSessionSnapshot? Snapshot = null)
{
    public static GameDetectionResult Nothing { get; } = new(GameDetectionChange.None, null, null);

    public bool IsTransition => Change != GameDetectionChange.None;
}

/// <summary>
/// Everything a game session replaced, so closing the game can put the machine back exactly as the user had it.
/// </summary>
/// <remarks>
/// Captured from the hardware and the settings file at the moment the game starts, not from what the app last wrote:
/// the user may have changed the mode from the tray, from the keyboard key or from another tool, and the value that
/// has to come back is the one that was actually in effect.
/// </remarks>
public sealed class GameSessionSnapshot
{
    public OperatingMode Mode { get; set; } = OperatingMode.Default;
    public FanMode Fan { get; set; } = FanMode.Auto;
    public int CustomCpuPercent { get; set; } = 50;
    public int CustomGpuPercent { get; set; } = 50;
    public bool CurveEnabled { get; set; }
    public List<FanCurvePoint>? CurvePoints { get; set; }
    public DateTime CapturedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Watches the process table for a configured game and moves the machine into that game's profile while it runs.
/// </summary>
/// <remarks>
/// <para>
/// The detection half is platform-independent on purpose: it asks an <see cref="IProcessScanner"/> for the process
/// list and matches it against the rules in <see cref="AppSettings.GameProfiles"/>. On Linux the scanner reads
/// <c>/proc</c>; a test supplies a fixed table. Nothing here knows what a process table looks like.
/// </para>
/// <para>
/// The apply half is deliberately conservative. A game profile is a temporary override, so the state it replaces is
/// captured first and written back when the game exits — including the fan curve, which is a software loop the caller
/// drives and therefore has to be told which shape to follow while the game runs
/// (<see cref="ActiveCurvePoints"/>).
/// </para>
/// <para>
/// A profile that would pin the machine to a high-power mode on battery is clamped through
/// <see cref="ModePolicy.ClampForPower"/> when the caller supplies a power state. Without one the target is used
/// as written: guessing "on battery" would silently downgrade every game on a desktop replacement.
/// </para>
/// </remarks>
// CA1001: the only disposable field is a SemaphoreSlim, and SemaphoreSlim allocates a kernel handle only when
// AvailableWaitHandle is read. Nothing here reads it, so Dispose() would set a flag and release nothing. Making the
// class IDisposable would push a using onto every caller — including the tests that build one per assertion — for no
// resource that is actually held.
#pragma warning disable CA1001
public sealed class GameProfileService
#pragma warning restore CA1001
{
    /// <summary>
    /// Programs that are always running. A profile for one of them would keep the machine in game mode for the whole
    /// session, so they are refused even when a settings file carries one.
    /// </summary>
    private static readonly HashSet<string> NonGameExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "systemd", "init", "kthreadd", "bash", "sh", "zsh", "fish", "login", "sshd", "dbus-daemon", "pipewire",
        "wireplumber", "pulseaudio", "Xorg", "Xwayland", "gnome-shell", "kwin_wayland", "plasmashell", "mutter",
        "chrome", "chromium", "firefox", "msedge", "brave", "discord", "spotify", "slack", "teams", "code",
        "steam", "steamwebhelper", "epicgameslauncher", "heroic", "lutris", "bottles", "gamemode", "mangohud",
        "obs", "obs64", "nitrolite", "nitrolite.desktop", "openrgb", "corectrl", "htop", "btop", "nvtop"
    };

    private readonly IProcessScanner _scanner;
    private readonly AppSettings _settings;
    private readonly IHardwareController? _hardware;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();

    private GameProfile? _active;
    private GameSessionSnapshot? _snapshot;

    /// <summary>
    /// <paramref name="hardware"/> is optional so the detection half can be tested on its own. Without it the service
    /// still reports transitions; it simply has nothing to write them to.
    /// </summary>
    public GameProfileService(IProcessScanner scanner, AppSettings settings, IHardwareController? hardware = null)
    {
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _hardware = hardware;
    }

    /// <summary>The game whose profile is in effect, or null when the machine is on the user's own settings.</summary>
    public GameProfile? ActiveProfile
    {
        get { lock (_stateLock) return _active; }
    }

    public bool IsGameActive => ActiveProfile != null;

    /// <summary>What the session replaced. Null when no game is running.</summary>
    public GameSessionSnapshot? Snapshot
    {
        get { lock (_stateLock) return _snapshot; }
    }

    /// <summary>
    /// The curve the fans should follow while the game runs, or null when the game did not ask for its own shape and
    /// the user's curve stays in charge.
    /// </summary>
    public IReadOnlyList<FanCurvePoint>? ActiveCurvePoints
    {
        get
        {
            var profile = ActiveProfile;
            if (profile is not { TargetFanCurveEnabled: true }) return null;
            var points = FanCurve.Sanitize(profile.TargetFanCurvePoints);
            return points.Count > 0 ? points : null;
        }
    }

    /// <summary>
    /// The machine's power state, when the caller can supply it. Null means "not known", and then a high-power target
    /// is used as written rather than being downgraded on a guess.
    /// </summary>
    public Func<(bool OnAc, int? BatteryPercent)>? PowerState { get; set; }

    /// <summary>Raised after a game's profile has been applied.</summary>
    public event Action<GameProfile>? GameStarted;

    /// <summary>Raised after the user's own settings have been put back.</summary>
    public event Action<GameProfile>? GameEnded;

    /// <summary>
    /// One detection pass. Starts a session when a configured game appears, ends it when the game exits, and does
    /// nothing in between. Never throws: a scanner that fails is a pass with no processes.
    /// </summary>
    public async Task<GameDetectionResult> PollAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var active = ActiveProfile;

            if (active != null)
            {
                // The session ends when the game is gone, and also when the user switches automatic game mode off
                // while playing: leaving the override in place would make the switch look broken.
                if (_settings.AutoGameModeEnabled && await IsStillRunningAsync(active).ConfigureAwait(false))
                    return GameDetectionResult.Nothing;
                return await EndAsync(active).ConfigureAwait(false);
            }

            if (!_settings.AutoGameModeEnabled) return GameDetectionResult.Nothing;

            // The process table is not read at all when no profile could match it. A user with no game rules — the
            // common case — was paying for a full enumeration of every process on the machine, once a second, for an
            // answer that could only ever be "nothing".
            var candidates = AutoApplyProfiles();
            if (candidates.Count == 0) return GameDetectionResult.Nothing;

            var match = await FindRunningGameAsync(candidates).ConfigureAwait(false);
            return match == null ? GameDetectionResult.Nothing : await StartAsync(match.Value.Profile).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Game detection error", ex.Message);
            return GameDetectionResult.Nothing;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Writes the running game's mode and fan targets again, after its profile was edited while the game runs. Returns
    /// false, and writes nothing, when <paramref name="exeName"/> is not the game in session. The pre-game snapshot is
    /// kept: the session still ends by putting back what was there before the game, not before the edit.
    /// </summary>
    public async Task<bool> ReapplyActiveAsync(string exeName)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var active = ActiveProfile;
            if (active == null || !string.Equals(active.ExeName, exeName, StringComparison.OrdinalIgnoreCase)) return false;

            await ApplyAsync(active).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Ends an active session without waiting for the game to exit. Used when the user turns automatic game mode off
    /// or asks for a clean exit.
    /// </summary>
    public async Task<GameDetectionResult> StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var active = ActiveProfile;
            return active == null ? GameDetectionResult.Nothing : await EndAsync(active).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>True when the profile's executable is still in the process table.</summary>
    private async Task<bool> IsStillRunningAsync(GameProfile profile)
    {
        Hint([profile]);

        foreach (var process in await ScanAsync().ConfigureAwait(false))
        {
            if (process.Matches(profile.ExeName)) return true;
        }

        return false;
    }

    /// <summary>
    /// Tells the scanner which executables this pass can match before it reads the table.
    /// </summary>
    /// <remarks>
    /// On Windows the scan opens a handle on every process to ask for its image path, which is the whole cost of a
    /// pass. The matcher only ever compares against the names handed over here, so a scanner that understands the hint
    /// can skip the rest. It is a hint: a scanner that ignores it returns the same table, and a null hint means the
    /// caller has no names to offer rather than "match nothing".
    /// </remarks>
    private void Hint(IEnumerable<GameProfile?> profiles)
    {
        var names = new List<string>();
        foreach (var profile in profiles)
        {
            if (profile == null || string.IsNullOrWhiteSpace(profile.ExeName)) continue;
            names.Add(profile.ExeName.Trim());
        }

        _scanner.ExecutablesOfInterest = names.Count == 0 ? null : names;
    }

    /// <summary>
    /// The profiles that could match a running process, in the order the settings file lists them.
    /// </summary>
    /// <remarks>
    /// Read on the caller's thread, before the scan. The settings list is edited from the UI thread, and the scan runs
    /// on another one; taking the snapshot first is what keeps the two from touching the same list at once.
    /// </remarks>
    private List<GameProfile> AutoApplyProfiles()
    {
        var candidates = new List<GameProfile>();
        foreach (var profile in _settings.GameProfiles)
        {
            if (profile == null || !profile.AutoApply) continue;
            if (string.IsNullOrWhiteSpace(profile.ExeName)) continue;
            if (IsNonGameExecutable(profile.ExeName)) continue;
            candidates.Add(profile);
        }

        return candidates;
    }

    /// <summary>The first configured game that is running, in the order the settings file lists them.</summary>
    private async Task<(GameProfile Profile, RunningProcess Process)?> FindRunningGameAsync(
        IReadOnlyList<GameProfile> candidates)
    {
        Hint(candidates);

        var processes = await ScanAsync().ConfigureAwait(false);
        if (processes.Count == 0) return null;

        foreach (var profile in candidates)
        {
            foreach (var process in processes)
            {
                if (process.Matches(profile.ExeName)) return (profile, process);
            }
        }

        return null;
    }

    /// <summary>
    /// The process table, read off the calling thread.
    /// </summary>
    /// <remarks>
    /// The scan opens a handle per process and asks each one for its image path, which is tens of milliseconds on a
    /// busy machine. It used to run on the UI thread inside the telemetry tick, so every second the window froze for
    /// as long as the enumeration took.
    /// </remarks>
    private Task<IReadOnlyList<RunningProcess>> ScanAsync() => Task.Run(Scan);

    private IReadOnlyList<RunningProcess> Scan()
    {
        try
        {
            return _scanner.Scan() ?? Array.Empty<RunningProcess>();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Process list could not be read", ex.Message);
            return Array.Empty<RunningProcess>();
        }
    }

    /// <summary>True for a program that is always running and must never own a game profile.</summary>
    public static bool IsNonGameExecutable(string? exeName)
    {
        if (string.IsNullOrWhiteSpace(exeName)) return false;
        var name = exeName.Trim();
        int slash = name.LastIndexOf('/');
        if (slash >= 0) name = name[(slash + 1)..];
        int dot = name.LastIndexOf('.');
        if (dot > 0) name = name[..dot];
        return NonGameExecutables.Contains(name);
    }

    private async Task<GameDetectionResult> StartAsync(GameProfile profile)
    {
        var snapshot = await CaptureAsync().ConfigureAwait(false);

        lock (_stateLock)
        {
            _active = profile;
            _snapshot = snapshot;
        }

        await ApplyAsync(profile).ConfigureAwait(false);
        GameStarted?.Invoke(profile);
        return new GameDetectionResult(GameDetectionChange.Started, profile, null, snapshot);
    }

    private async Task<GameDetectionResult> EndAsync(GameProfile profile)
    {
        GameSessionSnapshot? snapshot;
        lock (_stateLock)
        {
            snapshot = _snapshot;
            _active = null;
            _snapshot = null;
        }

        if (snapshot != null) await RestoreAsync(snapshot).ConfigureAwait(false);
        GameEnded?.Invoke(profile);
        return new GameDetectionResult(GameDetectionChange.Ended, null, profile, snapshot);
    }

    /// <summary>Reads the state the game is about to replace.</summary>
    private async Task<GameSessionSnapshot> CaptureAsync()
    {
        var snapshot = new GameSessionSnapshot
        {
            Mode = _settings.LastOperatingMode ?? OperatingMode.Default,
            Fan = FanMode.Auto,
            CustomCpuPercent = _settings.CustomFanCpuPercent,
            CustomGpuPercent = _settings.CustomFanGpuPercent,
            CurveEnabled = _settings.FanCurveEnabled,
            CurvePoints = FanCurve.Sanitize(_settings.FanCurvePoints)
        };

        if (_hardware == null) return snapshot;

        try
        {
            if (await _hardware.GetOperatingModeAsync().ConfigureAwait(false) is OperatingMode mode) snapshot.Mode = mode;
            if (await _hardware.GetFanModeAsync().ConfigureAwait(false) is FanMode fan) snapshot.Fan = fan;
        }
        catch (Exception ex)
        {
            // A read that fails leaves the settings-file values in place, which is the best available answer.
            AppLog.Warn("The pre-game state could not be read", ex.Message);
        }

        return snapshot;
    }

    /// <summary>Writes the game's target mode and fan behaviour.</summary>
    private async Task ApplyAsync(GameProfile profile)
    {
        if (_hardware == null) return;

        var target = profile.TargetMode;
        if (PowerState != null)
        {
            var (onAc, percent) = PowerState();
            target = ModePolicy.ClampForPower(target, onAc, percent);
        }

        try
        {
            await _hardware.SetOperatingModeAsync(target).ConfigureAwait(false);

            if (profile.TargetFanCurveEnabled)
            {
                // The curve itself is driven by the caller's telemetry loop; all this has to do is put the fans under
                // software control so that loop's writes are the ones that take effect.
                await _hardware.SetFanModeAsync(
                    FanMode.Custom,
                    _settings.CustomFanCpuPercent,
                    _settings.CustomFanGpuPercent).ConfigureAwait(false);
            }
            else
            {
                await _hardware.SetFanModeAsync(profile.TargetFanMode).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Game profile could not be applied", $"{profile.DisplayName}: {ex.Message}");
        }
    }

    /// <summary>Puts back everything the session replaced.</summary>
    private async Task RestoreAsync(GameSessionSnapshot snapshot)
    {
        if (_hardware == null) return;

        try
        {
            await _hardware.SetOperatingModeAsync(snapshot.Mode).ConfigureAwait(false);
            await _hardware.SetFanModeAsync(
                snapshot.Fan,
                snapshot.CustomCpuPercent,
                snapshot.CustomGpuPercent).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn("The post-game settings could not be restored", ex.Message);
        }
    }
}
