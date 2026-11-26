using NitroLite.Core;
using NitroLite.Desktop.Mvvm;

namespace NitroLite.Desktop.ViewModels;

/// <summary>
/// Why the last hardware write was refused for lack of permission, in the window's own words. The Linux backend's
/// reasons are mapped onto this by the composition root, so the view model never names a Linux type.
/// </summary>
public enum PermissionDenialReason
{
    /// <summary>No write has been refused for lack of permission, or the platform cannot say why.</summary>
    None,

    /// <summary>The node is not writable and the udev rule is what would fix it.</summary>
    UdevRuleMissing,

    /// <summary>The root helper refused because this session is not active or not authorised yet.</summary>
    HelperNotAuthorized,

    /// <summary>The root helper did not answer.</summary>
    HelperUnreachable,

    /// <summary>The root helper answered but does not list this attribute yet; a rescan lets it probe again.</summary>
    NotInHelperList
}

/// <summary>
/// Platform answers the shared window needs but the platform-neutral interfaces do not carry. Every member is optional:
/// a platform that has nothing to say leaves it null and the window behaves as it did before.
/// </summary>
/// <remarks>
/// Filled in by the composition root (<see cref="AppHost"/>), which is the only place that sees the concrete backend.
/// A class of delegates rather than a new interface on the Linux types, because those live in a project the window
/// does not reference on Windows.
/// </remarks>
public sealed class PlatformExtras
{
    /// <summary>Why the last write was refused for lack of permission.</summary>
    public Func<PermissionDenialReason>? PermissionDenial { get; init; }

    /// <summary>True when the one-time access grant has already been installed on this machine.</summary>
    public Func<bool>? AccessGrantApplied { get; init; }

    /// <summary>False when the lighting path in use carries fixed colours only, so no animated effect is offered.</summary>
    public Func<bool>? SupportsLightingEffects { get; init; }

    /// <summary>True when the <c>predator_v4</c> option NitroLite wrote is in place, so its undo can be offered.</summary>
    public Func<bool>? PredatorV4Installed { get; init; }

    /// <summary>Takes the <c>predator_v4</c> option back out, behind the platform's password prompt.</summary>
    public Func<Task<PlatformSetupResult>>? DisablePredatorV4 { get; init; }
}

public sealed partial class MainViewModel
{
    private PlatformExtras? _platformExtras;

    /// <summary>
    /// The platform's extra answers (see <see cref="ViewModels.PlatformExtras"/>). Set by the composition root right after
    /// the view model is built; null in every test that does not ask for it.
    /// </summary>
    public PlatformExtras? PlatformExtras
    {
        get => _platformExtras;
        set
        {
            _platformExtras = value;
            RaiseLinuxPermissionState();
            ApplyLightingEffectSupport();
        }
    }

    // ---------------------------------------------------------------- a second user session

    private bool _hardwareWritesAllowed = true;

    /// <summary>
    /// False when another user session on this machine already runs NitroLite (fast user switching). The two copies
    /// would drive the same fans and the same EC, so this one only reads: no fan loop, no mode, boost or lighting
    /// writes, no game profiles. Set by the shell before the window opens; true by default, so tests are unaffected.
    /// </summary>
    public bool HardwareWritesAllowed
    {
        get => _hardwareWritesAllowed;
        set
        {
            if (_hardwareWritesAllowed == value) return;
            _hardwareWritesAllowed = value;
            if (!value) AppLog.Warn("This copy of NitroLite does not write to the hardware",
                "another user session on this machine already runs NitroLite and owns the fans");
            SyncFanLoop();
        }
    }

    /// <summary>
    /// True, with the reason on the banner, when this copy must not write. Every hardware write the user can start asks
    /// this first, and says why each time, so a click never looks like it was silently ignored.
    /// </summary>
    private bool WritesRefusedForSecondSession()
    {
        if (_hardwareWritesAllowed) return false;
        Status = Labels.SecondSessionReadOnly;
        return true;
    }

    // ---------------------------------------------------------------- capabilities that change after start-up

    private bool _capabilityRefreshBusy;
    private bool _capabilityRefreshAgain;

    /// <summary>
    /// The backend replaced its capabilities on its own (the Linux helper answered late, the session became active).
    /// Called by the composition root on the UI thread; re-reads everything the first load built, without resetting the
    /// read-back counters (that is what raised the event in the first place).
    /// </summary>
    public async Task OnBackendCapabilitiesChangedAsync()
    {
        if (_disposed || !_hardwareReady) return;

        try
        {
            await RefreshCapabilitiesAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Warn("The new capabilities could not be applied", ex.Message);
        }
    }

    /// <summary>
    /// Reads the capabilities again and rebuilds what depends on them: the mode list, the lighting effects and
    /// directions, the capability rows, the backend banner, the Linux setup card and the ready state, then the controls'
    /// values. The rescan used to refresh only some of these, so after "Grant access" the "no Acer node" banner and the
    /// read-only state stayed until a restart.
    /// </summary>
    /// <remarks>
    /// Serialised: a refresh asked for while one runs is run once more afterwards rather than interleaved with it.
    /// </remarks>
    private async Task RefreshCapabilitiesAsync()
    {
        var hardware = _hardware;
        if (hardware == null || _disposed) return;

        if (_capabilityRefreshBusy)
        {
            _capabilityRefreshAgain = true;
            return;
        }

        _capabilityRefreshBusy = true;
        try
        {
            do
            {
                _capabilityRefreshAgain = false;

                _capabilities = GateByProfile(await hardware.GetCapabilitiesAsync().ConfigureAwait(true));
                _capabilitiesRead = true;
                if (_disposed) return;

                if (_rescan != null) NeedsUdevRule = _rescan.Snapshot.NeedsUdevRule || _rescan.PermissionHintRaised;

                // The lists the first load built. Rebuilt with the write guard on: a selection that moves because its
                // list changed is not a user's click.
                RefreshModeVisibility();
                SyncControls(BuildDirectionChoices);
                ApplyLightingEffectSupport();

                Raise(nameof(CapabilitySummaryText));
                Raise(nameof(PowerModeHeading));
                Raise(nameof(ModeControlAvailable));
                Raise(nameof(HasModeLockNote));
                Raise(nameof(FanControlAvailable));
                Raise(nameof(LightingAvailable));
                Raise(nameof(LightingUnsupported));
                Raise(nameof(LightingControlsEnabled));
                Raise(nameof(CoolBoostAvailable));
                Raise(nameof(BacklightTimeoutAvailable));
                Raise(nameof(WinKeyLockAvailable));
                Raise(nameof(HasAnyExtra));
                Raise(nameof(NoExtrasAvailable));
                Raise(nameof(ShowExtrasCard));
                Raise(nameof(BatteryLimitAvailable));
                Raise(nameof(MuxAvailable));
                Raise(nameof(SchoolModeColumnSpan));
                Raise(nameof(HardwareReportText));
                Raise(nameof(TierDisplay));
                Raise(nameof(HasBackend));
                Raise(nameof(HasNoBackend));
                Raise(nameof(ShowNoBackendBanner));
                Raise(nameof(NoBackendDetail));
                Raise(nameof(ShowPredatorV4));
                Raise(nameof(ShowLinuwuSense));
                RaiseLinuxPermissionState();
                BuildCapabilityRows();

                // A backend that has become reachable since start-up makes the window usable without a restart.
                if (hardware.IsAvailable && !IsReady)
                {
                    IsReady = true;
                    if (Status == Labels.NoAcerNodes) Status = "";
                }

                await ReadStateAsync().ConfigureAwait(true);
            }
            while (_capabilityRefreshAgain && !_disposed);
        }
        finally
        {
            _capabilityRefreshBusy = false;
        }
    }

    // ---------------------------------------------------------------- the Linux permission card

    /// <summary>Why the last write was refused, or <see cref="PermissionDenialReason.None"/>.</summary>
    private PermissionDenialReason CurrentDenial
    {
        get
        {
            try
            {
                return _platformExtras?.PermissionDenial?.Invoke() ?? PermissionDenialReason.None;
            }
            catch (Exception ex)
            {
                AppLog.Warn("The permission state could not be read", ex.Message);
                return PermissionDenialReason.None;
            }
        }
    }

    /// <summary>True when the refusal is the root helper's, which the udev rule does not fix.</summary>
    private bool HelperDenied => CurrentDenial is PermissionDenialReason.HelperNotAuthorized
        or PermissionDenialReason.HelperUnreachable
        or PermissionDenialReason.NotInHelperList;

    /// <summary>True when the one-time grant has already been run on this machine.</summary>
    private bool AccessGrantApplied
    {
        get
        {
            try
            {
                return _platformExtras?.AccessGrantApplied?.Invoke() == true;
            }
            catch (Exception ex)
            {
                AppLog.Warn("The access grant could not be checked", ex.Message);
                return false;
            }
        }
    }

    /// <summary>The udev rule is what is missing: a node is not writable and the helper is not the one refusing.</summary>
    private bool UdevGrantNeeded => NeedsUdevRule && !HelperDenied;

    /// <summary>
    /// The Nitro key's device is unreadable, the grant has already run, and nothing else needs it: pressing the button
    /// again would change nothing (the desktop's session manager does not hand input devices to the user).
    /// </summary>
    private bool NitroKeyOnlyAfterGrant =>
        _platformSetup != null && !UdevGrantNeeded && _platformSetup.NeedsInputGroup && AccessGrantApplied;

    /// <summary>
    /// One line that explains a permission problem the grant button cannot fix, or an empty string when there is none.
    /// </summary>
    public string LinuxPermissionNote
    {
        get
        {
            if (_platformSetup == null) return "";

            return CurrentDenial switch
            {
                PermissionDenialReason.HelperNotAuthorized => Labels.LinuxHelperNotAuthorized,
                PermissionDenialReason.HelperUnreachable => Labels.LinuxHelperUnreachable,
                PermissionDenialReason.NotInHelperList => Labels.LinuxHelperNotListed,
                _ => NitroKeyOnlyAfterGrant ? Labels.LinuxNitroKeyAfterGrant : ""
            };
        }
    }

    public bool HasLinuxPermissionNote => LinuxPermissionNote.Length > 0;

    /// <summary>Raises everything the permission half of the Linux card is drawn from.</summary>
    private void RaiseLinuxPermissionState()
    {
        Raise(nameof(ShowGrantAccess));
        Raise(nameof(LinuxPermissionNote));
        Raise(nameof(HasLinuxPermissionNote));
        Raise(nameof(ShowPredatorV4Undo));
    }

    // ---------------------------------------------------------------- predator_v4, undone

    private AsyncCommand? _disablePredatorV4Command;

    /// <summary>Takes the <c>predator_v4</c> option back out.</summary>
    public AsyncCommand DisablePredatorV4Command => _disablePredatorV4Command ??= new AsyncCommand(DisablePredatorV4Async);

    /// <summary>True when NitroLite's <c>predator_v4</c> option file is in place, so its undo is offered.</summary>
    public bool ShowPredatorV4Undo
    {
        get
        {
            try
            {
                return _platformExtras?.DisablePredatorV4 != null && _platformExtras.PredatorV4Installed?.Invoke() == true;
            }
            catch (Exception ex)
            {
                AppLog.Warn("The predator_v4 option could not be checked", ex.Message);
                return false;
            }
        }
    }

    private async Task DisablePredatorV4Async()
    {
        if (_platformExtras?.DisablePredatorV4 is not { } disable || !LinuxSetupIdle) return;

        LinuxSetupIdle = false;
        try
        {
            var result = await disable().ConfigureAwait(true);
            LinuxSetupStatus = result.Message;
        }
        catch (Exception ex)
        {
            AppLog.Warn("The predator_v4 option could not be removed", ex.Message);
        }
        finally
        {
            LinuxSetupIdle = true;
            Raise(nameof(ShowPredatorV4Undo));
        }
    }

    // ---------------------------------------------------------------- lighting effects the path can run

    /// <summary>True while the effect list holds every effect; false while it holds the fixed-colour ones only.</summary>
    private bool? _effectsAnimated;

    /// <summary>
    /// Offers only the fixed-colour effects when the lighting path in use cannot animate (the per-zone node, the OpenRGB
    /// bridge). The page used to name an effect the keyboard was not playing.
    /// </summary>
    private void ApplyLightingEffectSupport()
    {
        bool animated;
        try
        {
            animated = _platformExtras?.SupportsLightingEffects?.Invoke() ?? true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("The lighting effects could not be checked", ex.Message);
            animated = true;
        }

        if (_effectsAnimated == animated && Effects.Count > 0) return;
        _effectsAnimated = animated;

        var current = SelectedEffect?.Id;
        IEnumerable<string> offered = animated ? LightingPolicy.AllEffects : StaticOnlyEffects;

        SyncControls(() =>
        {
            Effects.Clear();
            foreach (var effect in offered) Effects.Add(new EffectChoice(effect, LightingPolicy.EffectDisplayName(effect)));

            SelectedEffect = Effects.FirstOrDefault(e => string.Equals(e.Id, current, StringComparison.OrdinalIgnoreCase))
                             ?? Effects[0];
        });
    }

    /// <summary>What a fixed-colour lighting path can show: one colour per zone, or nothing.</summary>
    private static readonly string[] StaticOnlyEffects = { "STATIC", "OFF" };
}
