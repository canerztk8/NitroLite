using System.Globalization;
using NitroLite.Core.Hardware.Diagnostics;
using NitroLite.Core.Hardware.Verification;

namespace NitroLite.Core.Hardware.Wmi;

/// <summary>
/// What one write plus its read-back ended up doing.
/// <para>
/// <see cref="Applied"/> is the only field the caller has to act on. The rest exists so the log and the settings page
/// can say <i>why</i> a control stopped working: "the firmware accepted the write and then reported the old value"
/// is a different problem from "the worker is gone", and the user deserves to be told which one they have.
/// </para>
/// </summary>
public sealed record ReadBackResult
{
    /// <summary>The firmware reported the value that was written.</summary>
    public bool Applied { get; init; }

    /// <summary>How many write/read cycles it took. 1 when the first read-back matched.</summary>
    public int Attempts { get; init; }

    /// <summary>The capability was already off, so nothing was written.</summary>
    public bool Skipped { get; init; }

    /// <summary>This capability is now off for the rest of the session.</summary>
    public bool DisabledForSession { get; init; }

    /// <summary>This capability has now failed three sessions in a row and is off until a rescan.</summary>
    public bool PermanentlyDisabled { get; init; }

    /// <summary>
    /// True when the write never reached the hardware because no transport exists at all (the elevated worker
    /// was not started, the user declined, or the install folder is untrusted). This is not a capability failure:
    /// it must not increment the session counters, or a machine with no backend would burn its three sessions in
    /// one afternoon and end up permanently disabled for a reason that has nothing to do with the firmware.
    /// </summary>
    public bool TransportMissing { get; init; }

    /// <summary>Human-readable reason, empty on success.</summary>
    public string Error { get; init; } = "";

    /// <summary>The capability that was written, so a caller can put the result beside its own control.</summary>
    public string Control { get; init; } = "";

    /// <summary>The value the machine reported after the write, empty when it reported nothing.</summary>
    public string Observed { get; init; } = "";

    /// <summary>
    /// True when the value was actually read back. False on a write that landed but could not be checked — the caller
    /// must not turn that into "applied" (see <see cref="Unverifiable"/>).
    /// </summary>
    public bool Verified { get; init; }

    /// <summary>
    /// The write was accepted and this machine has no way to read the setting back. Neither a success nor a failure:
    /// the session counters are untouched and the capability stays enabled, but the caller must not claim the change.
    /// </summary>
    public bool Unverifiable { get; init; }

    public static ReadBackResult Success(int attempts) =>
        new() { Applied = true, Verified = true, Attempts = attempts };

    /// <summary>The write was accepted and there is no read-back on this machine.</summary>
    public static ReadBackResult Unverified(string control, string reason) => new()
    {
        Control = control,
        Unverifiable = true,
        Skipped = true,
        Attempts = 1,
        Error = string.IsNullOrWhiteSpace(reason) ? "no read-back for this setting" : reason
    };

    public static ReadBackResult AlreadyDisabled() => new() { Skipped = true, Error = "capability is disabled" };

    /// <summary>No transport, with the reason the launcher gave when it had one.</summary>
    public static ReadBackResult NoTransport(string? reason = null) => new()
    {
        Skipped = true,
        TransportMissing = true,
        Error = string.IsNullOrWhiteSpace(reason)
            ? "no WMI transport: the elevated worker is not running"
            : reason
    };}

/// <summary>
/// The rule that makes a direct-WMI write trustworthy: never believe the status byte, always read the value back.
/// <para>
/// Acer's firmware answers <c>0x00</c> to a write it did not apply. That is not a bug in the encoder — the EC accepts
/// the command, queues it, and then ignores it because a service owns the register, because the machine is on
/// battery, or because the model simply does not implement that path. The only way to tell "written" from "accepted
/// and dropped" is to ask again.
/// </para>
/// <para>
/// The escalation is deliberately slow. Three attempts inside one session, then the capability is off for that
/// session. Only three <i>consecutive sessions</i> of failure switch it off for good, because a single bad session is
/// usually the EC being busy across a sleep or a resume, and permanently killing a working feature over that would be
/// worse than the bug. A successful read-back resets the counter, so an intermittent fault never accumulates.
/// </para>
/// </summary>
public sealed class ReadBackPolicy
{
    /// <summary>Write/read cycles before a capability is considered failed for this session.</summary>
    public const int MaxAttempts = 3;

    /// <summary>Consecutive failed sessions before a capability is switched off permanently.</summary>
    public const int SessionsBeforePermanentDisable = 3;

    /// <summary>Pause between a write and its read-back. The EC needs a moment to apply the value.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// Pause between the accepted write and the <i>first</i> read-back.
    /// <para>
    /// Longer than <see cref="RetryDelay"/> on purpose. The MUX and the charge limit are not register writes: the
    /// first goes through the embedded controller and is staged for the next boot, the second is handled by Acer Care
    /// Center's agent, and both report the previous value for a moment after accepting the command. Reading 120 ms
    /// later turned a write that had landed into a "did not change" and then into a disabled feature.
    /// </para>
    /// </summary>
    public static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(320);

    /// <summary>Fan behaviour (Auto / Max / Custom) on both fans.</summary>
    public const string FanBehavior = "wmi.fanBehavior";

    /// <summary>Fixed fan percentage.</summary>
    public const string FanSpeed = "wmi.fanSpeed";

    /// <summary>Platform power profile.</summary>
    public const string OperatingMode = "wmi.operatingMode";

    /// <summary>MUX switch.</summary>
    public const string MuxMode = "wmi.muxMode";

    /// <summary>Battery charge limit through WMID_GUID5.</summary>
    public const string BatteryLimit = "wmi.batteryLimit";

    /// <summary>Keyboard backlight colour through <c>SetGamingRgbKb</c>.</summary>
    public const string KeyboardLighting = "wmi.keyboardLighting";

    /// <summary>
    /// The Windows-key lock through <c>SetGamingProfile</c>. The firmware has no getter for this setting, so the
    /// control is written and reported as unverifiable rather than confirmed (see <see cref="ApplyUnverifiableAsync{T}"/>).
    /// </summary>
    public const string WinKeyLock = "wmi.winKeyLock";

    /// <summary>LCD overdrive, which does have a getter through <c>GetGamingProfile(0)</c>.</summary>
    public const string LcdOverdrive = "wmi.lcdOverdrive";

    /// <summary>CoolBoost, which does have a getter through <c>APGeAction.GetFunction(0x207)</c>.</summary>
    public const string CoolBoost = "wmi.coolBoost";

    /// <summary>
    /// Every capability this policy tracks, in the order the hardware report lists them. Kept here rather than in the
    /// report so a new capability cannot be added to the policy and forgotten by the diagnostics.
    /// </summary>
    public static IReadOnlyList<string> AllCapabilities { get; } = new[]
    {
        FanBehavior,
        FanSpeed,
        OperatingMode,
        MuxMode,
        BatteryLimit,
        KeyboardLighting,
        WinKeyLock
    };

    private readonly AppSettings _settings;
    private readonly Func<TimeSpan, Task> _delay;
    private readonly SessionVerification _verification;

    /// <summary>
    /// Told about a mismatch, or null when nothing is listening. Null is the default and the only value every test
    /// passes, so the policy's behaviour is unchanged by the diagnostics existing.
    /// </summary>
    private readonly IHardwareDiagnosticSink? _diagnostics;

    /// <summary>Capabilities switched off for this process's lifetime only.</summary>
    private readonly HashSet<string> _sessionDisabled = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Capabilities whose session counter has already been incremented. Without this a capability that fails twice in
    /// one session would count as two sessions and reach the permanent threshold in a single afternoon.
    /// </summary>
    private readonly HashSet<string> _countedThisSession = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <paramref name="delay"/> is injectable so the tests do not spend 320 ms per case waiting for a pause that only
    /// exists to give the EC time it does not need in a fake. <paramref name="verification"/> is the session ledger the
    /// UI and the hardware report read; it defaults to the process-wide instance. <paramref name="diagnostics"/> is
    /// told about a mismatch and about nothing else; it defaults to null, which is what every host that has not
    /// deliberately built one gets.
    /// </summary>
    public ReadBackPolicy(
        AppSettings settings,
        Func<TimeSpan, Task>? delay = null,
        SessionVerification? verification = null,
        IHardwareDiagnosticSink? diagnostics = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _delay = delay ?? (interval => Task.Delay(interval));
        _verification = verification ?? SessionVerification.Shared;
        _diagnostics = diagnostics;
    }

    /// <summary>True when the capability must not be written at all: off for this session, or off for good.</summary>
    public bool IsDisabled(string capability) =>
        _sessionDisabled.Contains(capability) || IsPermanentlyDisabled(capability);

    /// <summary>True when three consecutive sessions have failed and only a rescan can bring the capability back.</summary>
    public bool IsPermanentlyDisabled(string capability) =>
        _settings.DisabledCapabilities.Contains(capability, StringComparer.OrdinalIgnoreCase);

    /// <summary>How many consecutive sessions this capability has failed. Zero when it has never failed.</summary>
    public int ConsecutiveSessionFailures(string capability) =>
        _settings.CapabilitySessionFailures.TryGetValue(capability, out var count) ? count : 0;

    /// <summary>
    /// Clears every counter and every permanent disable. Wired to the settings page's rescan button, because a user
    /// who has just fixed the underlying problem must not have to edit a JSON file to get their fan control back.
    /// </summary>
    public void ResetAll()
    {
        _settings.CapabilitySessionFailures.Clear();
        _settings.DisabledCapabilities.Clear();
        _sessionDisabled.Clear();
        _countedThisSession.Clear();
    }

    /// <summary>
    /// Writes, reads back, and retries. <paramref name="readBack"/> returns the value the firmware reports now, and
    /// <paramref name="matches"/> decides whether that is the value that was asked for.
    /// </summary>
    /// <param name="requested">
    /// The value being written, as the log should show it (for example <c>DiscreteGpu</c>). Optional because a caller that
    /// writes a percentage or a colour already puts the value in its own log line; the note uses it to say what was
    /// asked for when the answer disagreed.
    /// </param>
    public async Task<ReadBackResult> ApplyAsync<T>(
        string capability,
        Func<Task<bool>> write,
        Func<Task<T?>> readBack,
        Func<T, bool> matches,
        string requested = "") where T : struct
    {
        if (IsDisabled(capability)) return ReadBackResult.AlreadyDisabled();

        var lastError = "";
        var lastObserved = "";
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                if (!await write().ConfigureAwait(false))
                {
                    lastError = "the worker refused the write";
                }
                else
                {
                    // The first read gets the settle delay: the EC, and the Care Center agent behind the charge
                    // limit, both answer with the old value for a moment after accepting a command.
                    await _delay(attempt == 1 ? SettleDelay : RetryDelay).ConfigureAwait(false);

                    var observed = await readBack().ConfigureAwait(false);
                    if (observed.HasValue && matches(observed.Value))
                    {
                        // A working capability must never carry a failure count into the next session.
                        _settings.CapabilitySessionFailures.Remove(capability);
                        _countedThisSession.Remove(capability);
                        _verification.Confirm(capability);

                        return ReadBackResult.Success(attempt) with
                        {
                            Control = capability,
                            Observed = Show(observed.Value)
                        };
                    }

                    lastObserved = observed.HasValue ? Show(observed.Value) : "";
                    lastError = observed.HasValue
                        ? $"read-back returned {Show(observed.Value)}, which is not the value that was written"
                        : "read-back returned nothing";
                }
            }
            catch (WmiTransportUnavailableException ex)
            {
                // Nothing reached the hardware, so there is no firmware answer to judge and no session to count.
                // This is the branch that keeps a machine without a backend from disabling its own features.
                _verification.Mark(new VerificationNote
                {
                    Control = capability,
                    Verdict = ControlVerdict.TransportMissing,
                    Detail = ex.Message
                });

                return ReadBackResult.NoTransport(ex.Message);
            }
            catch (Exception ex)
            {
                lastError = $"{ex.GetType().Name}: {ex.Message}";
            }
        }

        return Fail(capability, lastError, lastObserved, requested);
    }

    /// <summary>
    /// Writes a control that this machine may have no read-back for, and tells the caller which of the two happened.
    /// <para>
    /// The Windows-key lock is the reason this exists. <c>SetGamingProfile</c> accepts it and answers status zero, and
    /// the firmware has no getter: <c>GetGamingProfile</c> with the lock's selector echoes the selector back, and
    /// <c>GetGamingProfile(0)</c> answers the LCD-overdrive word whose bit 24 means "overdrive is supported". Reading
    /// either as the lock state would be inventing a measurement. So a missing answer is
    /// <see cref="ControlVerdict.Unverifiable"/> — the write is not called applied, the capability is not counted as
    /// broken, and the UI gets a hint instead of a false "Applied" or a false failure.
    /// </para>
    /// </summary>
    public async Task<ReadBackResult> ApplyUnverifiableAsync<T>(
        string capability,
        string requested,
        Func<Task<bool>> write,
        Func<Task<T?>> readBack,
        Func<T, bool> matches) where T : struct
    {
        if (IsDisabled(capability)) return ReadBackResult.AlreadyDisabled();

        try
        {
            if (!await write().ConfigureAwait(false))
            {
                return Fail(capability, "the worker refused the write", "", requested);
            }
        }
        catch (WmiTransportUnavailableException ex)
        {
            _verification.Mark(new VerificationNote
            {
                Control = capability,
                Verdict = ControlVerdict.TransportMissing,
                Detail = ex.Message
            });

            return ReadBackResult.NoTransport(ex.Message);
        }
        catch (Exception ex)
        {
            return Fail(capability, $"{ex.GetType().Name}: {ex.Message}", "", requested);
        }

        await _delay(SettleDelay).ConfigureAwait(false);

        T? observed;
        try
        {
            observed = await readBack().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A read that threw is not a read that disagreed. Nothing was confirmed, so the control is reported as
            // unverifiable rather than reverted on evidence that never arrived.
            observed = null;
            AppLog.Info("The read-back for an unverifiable control failed", $"{capability}: {ex.Message}");
        }

        if (!observed.HasValue)
        {
            var reason = "this machine has no read-back for the setting";
            _verification.Mark(new VerificationNote
            {
                Control = capability,
                Verdict = ControlVerdict.Unverifiable,
                Requested = requested,
                Detail = reason
            });

            return ReadBackResult.Unverified(capability, reason) with { Control = capability };
        }

        if (matches(observed.Value))
        {
            _settings.CapabilitySessionFailures.Remove(capability);
            _countedThisSession.Remove(capability);
            _verification.Confirm(capability);

            return ReadBackResult.Success(1) with
            {
                Control = capability,
                Observed = Show(observed.Value)
            };
        }

        var mismatch = $"read-back returned {Show(observed.Value)} after writing {requested}";
        return Fail(capability, mismatch, Show(observed.Value), requested);
    }

    /// <summary>
    /// Records a failed session and decides whether the capability is now off for the session or for good.
    /// </summary>
    private ReadBackResult Fail(string capability, string error, string observed = "", string requested = "")
    {
        _sessionDisabled.Add(capability);

        _verification.Mark(new VerificationNote
        {
            Control = capability,
            Verdict = ControlVerdict.Mismatch,
            Requested = requested,
            Observed = observed,
            Detail = error
        });

        // The one outcome the diagnostics exist for. Everything else the policy records is either a success, a
        // machine that cannot answer, or a transport that was never there; a mismatch is the firmware contradicting
        // itself, which is the quirk worth knowing about. The call returns immediately: it queues background work
        // and cannot fail in a way the write path would see.
        _diagnostics?.ReadBackMismatch(capability);

        // One increment per session, however many writes failed inside it.
        if (_countedThisSession.Add(capability))
        {
            var count = ConsecutiveSessionFailures(capability) + 1;
            _settings.CapabilitySessionFailures[capability] = count;

            if (count >= SessionsBeforePermanentDisable && !IsPermanentlyDisabled(capability))
            {
                _settings.DisabledCapabilities.Add(capability);
                AppLog.Warn("The direct WMI capability was disabled permanently",
                    $"{capability}: read-back failed in {count} consecutive sessions ({error})");

                return new ReadBackResult
                {
                    Control = capability,
                    Observed = observed,
                    Attempts = MaxAttempts,
                    DisabledForSession = true,
                    PermanentlyDisabled = true,
                    Error = error
                };
            }
        }

        AppLog.Warn("The direct WMI capability was disabled for this session",
            $"{capability}: read-back did not match in {MaxAttempts} attempts ({error})");

        return new ReadBackResult
        {
            Control = capability,
            Observed = observed,
            Attempts = MaxAttempts,
            DisabledForSession = true,
            Error = error
        };
    }

    /// <summary>
    /// Formats a read-back value for the log. Invariant culture, because the same string goes into the hardware
    /// report, which is read by whoever triages the issue and not by the machine's owner.
    /// </summary>
    private static string Show<T>(T value) where T : struct =>
        value is IFormattable formattable
            ? formattable.ToString(null, CultureInfo.InvariantCulture)
            : value.ToString() ?? "";
}
