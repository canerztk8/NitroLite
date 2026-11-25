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
/// <param name="Applied">
/// What the game's profile actually did to the machine. Null unless <paramref name="Change"/> is Started. The caller
/// needs it because the target and the result can differ: on battery the mode is clamped, and a write can be refused.
/// </param>
public sealed record GameDetectionResult(
    GameDetectionChange Change,
    GameProfile? Profile,
    GameProfile? Previous,
    GameSessionSnapshot? Snapshot = null,
    GameApplyOutcome? Applied = null)
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

    /// <summary>The exact boost value stored for the adapter before the game, or null when it was not readable.</summary>
    public CpuBoostMode? AcBoost { get; set; }

    /// <summary>The exact boost value stored for the battery before the game, or null when it was not readable.</summary>
    public CpuBoostMode? DcBoost { get; set; }

    /// <summary>
    /// True once the session has written the adapter's boost value. Only a value the session changed is put back: a
    /// restore of a value nobody touched would overwrite whatever else changed it meanwhile.
    /// </summary>
    public bool AcBoostWritten { get; set; }

    /// <summary>True once the session has written the battery's boost value.</summary>
    public bool DcBoostWritten { get; set; }

    /// <summary>
    /// True when an apply left the battery value alone because school mode owned it. If school mode is switched off
    /// mid-game and a later apply does write the battery value, the value captured at the start is school mode's, not
    /// the user's, so it is read again just before that first write.
    /// </summary>
    public bool DcBoostSkipped { get; set; }

    /// <summary>The adapter value to put back after the game: null when the session never wrote it or it is unknown.</summary>
    public CpuBoostMode? RestorableAcBoost => AcBoostWritten ? AcBoost : null;

    /// <summary>The battery value to put back after the game: null when the session never wrote it or it is unknown.</summary>
    public CpuBoostMode? RestorableDcBoost => DcBoostWritten ? DcBoost : null;
}

/// <summary>What one apply of a game's profile actually did.</summary>
/// <param name="Mode">The operating mode that was asked of the hardware: the profile's target, clamped on battery.</param>
/// <param name="ModeWritten">False when the mode write was refused, threw, or did not happen.</param>
/// <param name="FanWritten">False when the fan write was refused, threw, or did not happen.</param>
/// <param name="BoostWritten">
/// Null when no boost value was due (no boost control on this machine, or school mode owns the only switch); false when
/// the write was refused or threw.
/// </param>
public sealed record GameApplyOutcome(OperatingMode Mode, bool ModeWritten, bool FanWritten, bool? BoostWritten)
{
    /// <summary>True when at least one setting the profile asked for did not reach the machine.</summary>
    public bool IsPartial => !ModeWritten || !FanWritten || BoostWritten == false;
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

    /// <summary>Set by <see cref="StopWriting"/>; checked before every hardware write.</summary>
    private volatile bool _writesStopped;

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

    /// <summary>
    /// The processor boost control a game's <see cref="GameProfile.TargetCpuBoost"/> is written through. The
    /// composition root hands over the same instance the dashboard's boost picker uses, so the two read and write one
    /// object. Null, or a control that reports itself unsupported, means no boost value is ever written.
    /// </summary>
    /// <remarks>
    /// Settable rather than init-only because the composition root builds the boost control after the backend that
    /// constructs this service. It is set once, before the first poll.
    /// </remarks>
    public ICpuBoostAccess? CpuBoost { get; set; }

    /// <summary>
    /// False when the boost control has one switch rather than an adapter and a battery value (Linux: a single
    /// <c>cpufreq/boost</c> node). A write "for the adapter only" then reaches the battery too, so while school mode owns
    /// the battery value the game writes no boost at all, and a restore is one write instead of two.
    /// </summary>
    public bool CpuBoostPerPowerSource { get; set; } = true;

    /// <summary>What the last apply did. Null before the first session.</summary>
    public GameApplyOutcome? LastApply { get; private set; }

    /// <summary>
    /// Called when a game has been detected and its pre-game state captured, before anything is written: the host
    /// writes its crash-recovery record here. <see cref="GameSessionSnapshot.RestorableAcBoost"/> and
    /// <see cref="GameSessionSnapshot.RestorableDcBoost"/> already name the boost values the apply is about to write.
    /// </summary>
    /// <remarks>
    /// Run on the synchronization context <see cref="PollAsync"/> was called from (the UI thread in the window), so
    /// the host's settings save runs on the same thread as all its other saves; inline when there is none. The poll
    /// waits for it to finish before the first write.
    /// </remarks>
    public Action<GameProfile, GameSessionSnapshot>? BeforeApply { get; set; }

    /// <summary>Runs <paramref name="action"/> on <paramref name="context"/> and completes when it has run.</summary>
    private static Task RunOnCallerAsync(SynchronizationContext? context, Action action)
    {
        if (context == null)
        {
            action();
            return Task.CompletedTask;
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Post(_ =>
        {
            try
            {
                action();
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        }, null);
        return done.Task;
    }

    /// <summary>
    /// One detection pass. Starts a session when a configured game appears, ends it when the game exits, and does
    /// nothing in between. Never throws: a scanner that fails is a pass with no processes.
    /// </summary>
    public async Task<GameDetectionResult> PollAsync()
    {
        // Read before the first await: it is the caller's thread (the UI thread in the window), which is where
        // BeforeApply runs. PollAsync must therefore be awaited, never blocked on, while a BeforeApply is set.
        var caller = SynchronizationContext.Current;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var active = ActiveProfile;

            if (active != null)
            {
                // The session ends when the game is gone, and also when the user switches automatic game mode off
                // while playing: leaving the override in place would make the switch look broken.
                // The same for the game's own "apply when the game starts" switch: turning it off mid-game used to
                // leave Performance in place until the game closed.
                if (_settings.AutoGameModeEnabled && IsStillWanted(active) &&
                    await IsStillRunningAsync(active).ConfigureAwait(false))
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
            return match == null
                ? GameDetectionResult.Nothing
                : await StartAsync(match.Value.Profile, caller).ConfigureAwait(false);
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
    /// Writes the running game's mode, boost and fan targets again, after its profile was edited while the game runs.
    /// What the write did is in <see cref="LastApply"/>. Returns
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

            await ApplyAsync(active, Snapshot).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Gives the battery's boost value back to what it was before the game, because something else is about to own it.
    /// Called right before school mode is switched on mid-game. True when a value was written back.
    /// </summary>
    /// <remarks>
    /// School mode backs up the battery value it finds and restores that backup when it is switched off. If the game's
    /// value were still in place, the backup would be the game's, and switching school mode off after the game would
    /// leave the game's boost in the power plan for good — the end of the session cannot put the battery value back
    /// while school mode owns it. Handing it back first makes school mode's backup the user's own value. The session
    /// then no longer counts the battery value as its own, so the end restores nothing for it; on a single-switch
    /// control (Linux) that is the whole switch.
    /// </remarks>
    public async Task<bool> ReleaseBatteryBoostAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var snapshot = Snapshot;
            if (ActiveProfile is not { } active || snapshot == null || !snapshot.DcBoostWritten) return false;
            if (_writesStopped || UsableBoost is not { } boost) return false;

            // From here the battery value is not the session's any more, whether or not the write below lands. A later
            // apply after school mode is switched off again re-reads it before writing (DcBoostSkipped).
            snapshot.DcBoostSkipped = true;
            snapshot.DcBoostWritten = false;

            CpuBoostMode? value;
            bool targetAc;
            if (CpuBoostPerPowerSource)
            {
                value = snapshot.DcBoost;
                targetAc = false;
            }
            else
            {
                value = snapshot.AcBoost ?? snapshot.DcBoost;
                snapshot.AcBoostWritten = false;
                targetAc = true;
            }

            // An unknown pre-game value cannot be given back; the end of the session would not have restored it either.
            if (value is not { } previous) return false;

            return await WriteAsync(active.DisplayName, "CPU boost hand-back " + previous,
                () => boost.SetBoostModeAsync(previous, targetAc: targetAc, targetDc: true)).ConfigureAwait(false);
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

    /// <summary>
    /// Stops this service from writing to the hardware for the rest of the process. Called by the clean exit once it
    /// has stopped waiting for <see cref="StopAsync"/>: a session stuck in a slow call would otherwise go on to write
    /// the pre-game fan mode, or the game's Custom speed, after the exit had handed the fans back to the firmware.
    /// Detection keeps answering; only the writes stop. A write already in progress is not interrupted.
    /// </summary>
    public void StopWriting() => _writesStopped = true;

    /// <summary>True once <see cref="StopWriting"/> has been called.</summary>
    public bool WritesStopped => _writesStopped;

    /// <summary>
    /// True while the running game still has a rule that asks to be applied: it is still in the list and its
    /// auto-apply switch is still on.
    /// </summary>
    private bool IsStillWanted(GameProfile active)
    {
        foreach (var profile in _settings.GameProfiles)
        {
            if (profile == null) continue;
            if (!string.Equals(profile.ExeName, active.ExeName, StringComparison.OrdinalIgnoreCase)) continue;
            return profile.AutoApply;
        }

        return false;
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

    private async Task<GameDetectionResult> StartAsync(GameProfile profile, SynchronizationContext? caller)
    {
        var snapshot = await CaptureAsync().ConfigureAwait(false);

        lock (_stateLock)
        {
            _active = profile;
            _snapshot = snapshot;
        }

        // The crash record has to exist before the first write: a process that dies between the boost write and a
        // record written afterwards leaves the game's boost in the power plan with nothing to undo it. The values the
        // apply is about to write are marked first, so the record names exactly those.
        if (BeforeApply is { } beforeApply && _hardware != null)
        {
            PlanBoost(snapshot);
            try
            {
                await RunOnCallerAsync(caller, () => beforeApply(profile, snapshot)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The session still starts: refusing to apply a game's profile because a record could not be written
                // would be the worse trade, and the host reports the failed save itself.
                AppLog.Warn("The game session record could not be prepared", ex.Message);
            }
        }

        var applied = await ApplyAsync(profile, snapshot).ConfigureAwait(false);
        return new GameDetectionResult(GameDetectionChange.Started, profile, null, snapshot, applied);
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

        if (snapshot != null) await RestoreAsync(snapshot, profile).ConfigureAwait(false);
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

        // The exact stored values, not the friendly label: the restore has to put back what was actually there. A value
        // that cannot be read stays null and is then never written back, because writing a guess is worse than leaving
        // the game's value in place.
        if (UsableBoost is { } boost)
        {
            snapshot.AcBoost = ReadBoost(boost, isAc: true);
            snapshot.DcBoost = ReadBoost(boost, isAc: false);
        }

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

    /// <summary>
    /// Writes the game's target mode, boost and fan behaviour, and reports what landed. Null when there is no hardware
    /// to write to: the service is then detection-only and writes nothing, the boost included.
    /// </summary>
    /// <remarks>
    /// Each setting is written on its own: a refused mode used to abort the fan write as well, and a refusal of any of
    /// them was silent. Now each one that does not land is logged by name and reported in the outcome, so the caller
    /// can say the profile was applied partially instead of showing a card the machine is not in.
    /// </remarks>
    private async Task<GameApplyOutcome?> ApplyAsync(GameProfile profile, GameSessionSnapshot? snapshot)
    {
        var hardware = _hardware;
        if (hardware == null) return null;

        var target = profile.TargetMode;
        if (PowerState != null)
        {
            var (onAc, percent) = PowerState();
            target = ModePolicy.ClampForPower(target, onAc, percent);
        }

        bool modeWritten = await WriteAsync(profile.DisplayName, "operating mode " + target,
            () => hardware.SetOperatingModeAsync(target)).ConfigureAwait(false);

        bool? boostWritten = await ApplyBoostAsync(profile, snapshot).ConfigureAwait(false);

        // A curve is driven by the caller's telemetry loop; all this has to do is put the fans under software control so
        // that loop's writes are the ones that take effect. A fixed Custom rule is the user's own slider speed: written
        // without the percentages it used to get the backend's default speed instead.
        bool fanWritten = profile.TargetFanCurveEnabled || profile.TargetFanMode == FanMode.Custom
            ? await WriteAsync(profile.DisplayName, "fan mode Custom",
                () => hardware.SetFanModeAsync(
                    FanMode.Custom,
                    _settings.CustomFanCpuPercent,
                    _settings.CustomFanGpuPercent)).ConfigureAwait(false)
            : await WriteAsync(profile.DisplayName, "fan mode " + profile.TargetFanMode,
                () => hardware.SetFanModeAsync(profile.TargetFanMode)).ConfigureAwait(false);

        var outcome = new GameApplyOutcome(target, modeWritten, fanWritten, boostWritten);
        LastApply = outcome;
        return outcome;
    }

    /// <summary>
    /// Writes the game's boost value. Null when none was due: no usable boost control, or school mode owns the only
    /// switch there is.
    /// </summary>
    /// <remarks>
    /// School mode holds the battery value at Disabled while it is on, and a game must not undo that: the battery value
    /// is then left alone and only the adapter's is written. On a single-switch control the adapter write would reach
    /// the battery too, so nothing is written at all.
    /// </remarks>
    private async Task<bool?> ApplyBoostAsync(GameProfile profile, GameSessionSnapshot? snapshot)
    {
        if (PlanBoost(snapshot) is not { } plan) return null;
        var (boost, targetDc) = plan;

        return await WriteAsync(profile.DisplayName, "CPU boost " + profile.TargetCpuBoost,
            () => boost.SetBoostModeAsync(profile.TargetCpuBoost, targetAc: true, targetDc: targetDc)).ConfigureAwait(false);
    }

    /// <summary>
    /// Decides which boost values the next apply writes, and marks them in <paramref name="snapshot"/> as the session's.
    /// Null when no boost write is due. Idempotent, so the start can run it before the crash record is written and the
    /// apply can run it again.
    /// </summary>
    private (ICpuBoostAccess Boost, bool TargetDc)? PlanBoost(GameSessionSnapshot? snapshot)
    {
        if (_writesStopped || UsableBoost is not { } boost) return null;

        bool schoolOwnsBattery = _settings.SchoolModeEnabled;
        if (schoolOwnsBattery && !CpuBoostPerPowerSource)
        {
            if (snapshot != null) snapshot.DcBoostSkipped = true;
            return null;
        }

        bool targetDc = !schoolOwnsBattery;
        if (snapshot != null)
        {
            if (!targetDc)
            {
                snapshot.DcBoostSkipped = true;
            }
            else if (snapshot.DcBoostSkipped && !snapshot.DcBoostWritten)
            {
                // School mode was switched off since the start: the value captured then was school mode's, and school
                // mode has since put the user's back. That one is what the end of the session has to restore.
                snapshot.DcBoost = ReadBoost(boost, isAc: false);
                if (!CpuBoostPerPowerSource && !snapshot.AcBoostWritten) snapshot.AcBoost = ReadBoost(boost, isAc: true);
            }

            // Marked before the write: a write that throws or answers false may still have changed one of the two
            // values, and restoring a value that did not change is a harmless rewrite of the same number.
            snapshot.AcBoostWritten = true;
            if (targetDc) snapshot.DcBoostWritten = true;
        }

        return (boost, targetDc);
    }

    /// <summary>
    /// One game-profile write. False when it was refused, threw, or was not made because writes were stopped; a refusal
    /// or a throw is logged with the game and the setting, a stop is not (the app is exiting).
    /// </summary>
    private async Task<bool> WriteAsync(string game, string setting, Func<Task<bool>> write)
    {
        if (_writesStopped) return false;

        try
        {
            if (await write().ConfigureAwait(false)) return true;
            AppLog.Warn("A game profile setting was not applied", $"{game}: {setting} was refused");
        }
        catch (Exception ex)
        {
            AppLog.Warn("A game profile setting was not applied", $"{game}: {setting}: {ex.Message}");
        }

        return false;
    }

    /// <summary>Puts back everything the session replaced.</summary>
    private async Task RestoreAsync(GameSessionSnapshot snapshot, GameProfile profile)
    {
        var hardware = _hardware;
        if (hardware == null) return;

        await RestoreWriteAsync(profile.DisplayName, "operating mode",
            () => hardware.SetOperatingModeAsync(snapshot.Mode)).ConfigureAwait(false);

        if (!_writesStopped)
        {
            await RestoreBoostAsync(
                UsableBoost,
                snapshot.RestorableAcBoost,
                snapshot.RestorableDcBoost,
                CpuBoostPerPowerSource,
                _settings.SchoolModeEnabled,
                () => _writesStopped).ConfigureAwait(false);
        }

        await RestoreWriteAsync(profile.DisplayName, "fan mode",
            () => hardware.SetFanModeAsync(
                snapshot.Fan,
                snapshot.CustomCpuPercent,
                snapshot.CustomGpuPercent)).ConfigureAwait(false);
    }

    private async Task RestoreWriteAsync(string game, string setting, Func<Task<bool>> write)
    {
        if (_writesStopped) return;

        try
        {
            if (!await write().ConfigureAwait(false))
                AppLog.Warn("The post-game settings could not be restored", $"{game}: {setting} was refused");
        }
        catch (Exception ex)
        {
            AppLog.Warn("The post-game settings could not be restored", $"{game}: {setting}: {ex.Message}");
        }
    }

    /// <summary>
    /// Puts back the boost values a game session wrote. Shared with the crash recovery, so the end of a session and
    /// the next launch after a crash follow one rule. True when every write that was due landed.
    /// </summary>
    /// <param name="boost">The boost control; null or unsupported means nothing is written.</param>
    /// <param name="ac">The adapter value to put back, or null to leave the adapter alone.</param>
    /// <param name="dc">The battery value to put back, or null to leave the battery alone.</param>
    /// <param name="perPowerSource">False for a single-switch control (Linux), where AC and DC are one value.</param>
    /// <param name="schoolModeOn">
    /// True while school mode is on. It owns the battery value then — it may have been switched on mid-game — so the
    /// battery is not touched, and on a single-switch control nothing is.
    /// </param>
    /// <param name="writesStopped">Checked before each write; true stops the rest.</param>
    public static async Task<bool> RestoreBoostAsync(
        ICpuBoostAccess? boost,
        CpuBoostMode? ac,
        CpuBoostMode? dc,
        bool perPowerSource,
        bool schoolModeOn,
        Func<bool>? writesStopped = null)
    {
        if (boost == null || !IsUsable(boost)) return true;

        if (schoolModeOn)
        {
            dc = null;
            if (!perPowerSource) ac = null;
        }

        if (!perPowerSource)
        {
            // One switch: AC and DC were read from the same node, so they are the same value. Writing it twice would
            // be the same write twice; writing two different ones would flip it, so only one is ever written.
            var value = ac ?? dc;
            return value is not { } single ||
                   await RestoreBoostWriteAsync(boost, single, true, true, writesStopped).ConfigureAwait(false);
        }

        if (ac is { } both && dc == both)
            return await RestoreBoostWriteAsync(boost, both, true, true, writesStopped).ConfigureAwait(false);

        bool ok = true;
        if (ac is { } acValue)
            ok &= await RestoreBoostWriteAsync(boost, acValue, true, false, writesStopped).ConfigureAwait(false);
        if (dc is { } dcValue)
            ok &= await RestoreBoostWriteAsync(boost, dcValue, false, true, writesStopped).ConfigureAwait(false);
        return ok;
    }

    private static async Task<bool> RestoreBoostWriteAsync(
        ICpuBoostAccess boost, CpuBoostMode mode, bool targetAc, bool targetDc, Func<bool>? writesStopped)
    {
        if (writesStopped?.Invoke() == true) return false;

        string source = targetAc && targetDc ? "AC+DC" : targetAc ? "AC" : "DC";
        try
        {
            if (await boost.SetBoostModeAsync(mode, targetAc, targetDc).ConfigureAwait(false)) return true;
            AppLog.Warn("The CPU boost could not be restored after the game", $"{source} {mode} was refused");
        }
        catch (Exception ex)
        {
            AppLog.Warn("The CPU boost could not be restored after the game", $"{source} {mode}: {ex.Message}");
        }

        return false;
    }

    /// <summary>The boost control when one is wired and the machine has one; otherwise null.</summary>
    private ICpuBoostAccess? UsableBoost => CpuBoost is { } boost && IsUsable(boost) ? boost : null;

    private static bool IsUsable(ICpuBoostAccess boost)
    {
        try
        {
            return boost.IsSupported;
        }
        catch (Exception ex)
        {
            AppLog.Warn("CPU boost support could not be read", ex.Message);
            return false;
        }
    }

    private static CpuBoostMode? ReadBoost(ICpuBoostAccess boost, bool isAc)
    {
        try
        {
            return boost.QueryBoostModeExact(isAc);
        }
        catch (Exception ex)
        {
            AppLog.Warn("The pre-game CPU boost could not be read", ex.Message);
            return null;
        }
    }
}
