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

    /// <summary>Human-readable reason, empty on success.</summary>
    public string Error { get; init; } = "";

    public static ReadBackResult Success(int attempts) => new() { Applied = true, Attempts = attempts };

    public static ReadBackResult AlreadyDisabled() => new() { Skipped = true, Error = "capability is disabled" };
}

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

    private readonly AppSettings _settings;
    private readonly Func<TimeSpan, Task> _delay;

    /// <summary>Capabilities switched off for this process's lifetime only.</summary>
    private readonly HashSet<string> _sessionDisabled = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Capabilities whose session counter has already been incremented. Without this a capability that fails twice in
    /// one session would count as two sessions and reach the permanent threshold in a single afternoon.
    /// </summary>
    private readonly HashSet<string> _countedThisSession = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <paramref name="delay"/> is injectable so the tests do not spend 360 ms per case waiting for a pause that only
    /// exists to give the EC time it does not need in a fake.
    /// </summary>
    public ReadBackPolicy(AppSettings settings, Func<TimeSpan, Task>? delay = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _delay = delay ?? (interval => Task.Delay(interval));
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
    public async Task<ReadBackResult> ApplyAsync<T>(
        string capability,
        Func<Task<bool>> write,
        Func<Task<T?>> readBack,
        Func<T, bool> matches) where T : struct
    {
        if (IsDisabled(capability)) return ReadBackResult.AlreadyDisabled();

        var lastError = "";
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
                    await _delay(RetryDelay).ConfigureAwait(false);

                    var observed = await readBack().ConfigureAwait(false);
                    if (observed.HasValue && matches(observed.Value))
                    {
                        // A working capability must never carry a failure count into the next session.
                        _settings.CapabilitySessionFailures.Remove(capability);
                        _countedThisSession.Remove(capability);
                        return ReadBackResult.Success(attempt);
                    }

                    lastError = observed.HasValue
                        ? $"read-back returned {observed.Value}, which is not the value that was written"
                        : "read-back returned nothing";
                }
            }
            catch (Exception ex)
            {
                lastError = $"{ex.GetType().Name}: {ex.Message}";
            }
        }

        return Fail(capability, lastError);
    }

    /// <summary>
    /// Records a failed session and decides whether the capability is now off for the session or for good.
    /// </summary>
    private ReadBackResult Fail(string capability, string error)
    {
        _sessionDisabled.Add(capability);

        // One increment per session, however many writes failed inside it.
        if (_countedThisSession.Add(capability))
        {
            var count = ConsecutiveSessionFailures(capability) + 1;
            _settings.CapabilitySessionFailures[capability] = count;

            if (count >= SessionsBeforePermanentDisable && !IsPermanentlyDisabled(capability))
            {
                _settings.DisabledCapabilities.Add(capability);
                AppLog.Warn("Doğrudan WMI yeteneği kalıcı olarak kapatıldı",
                    $"{capability}: {count} ardışık oturumda geri okuma başarısız ({error})");

                return new ReadBackResult
                {
                    Attempts = MaxAttempts,
                    DisabledForSession = true,
                    PermanentlyDisabled = true,
                    Error = error
                };
            }
        }

        AppLog.Warn("Doğrudan WMI yeteneği bu oturum için kapatıldı",
            $"{capability}: {MaxAttempts} denemede geri okuma eşleşmedi ({error})");

        return new ReadBackResult
        {
            Attempts = MaxAttempts,
            DisabledForSession = true,
            Error = error
        };
    }
}
