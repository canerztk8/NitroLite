namespace NitroLite.Core.Hardware.Verification;

/// <summary>
/// What this session has actually confirmed about the machine's hardware controls.
/// <para>
/// One place, because the answer has to be the same in three places at once: the status line after a write, the
/// inline hint that offers a compatibility report, and <c>--hardware-report</c>. A control that was written and never
/// read back must not look applied anywhere, and a control whose read-back contradicted the write must say so in the
/// report instead of being rediscovered by the next person reading an issue.
/// </para>
/// <para>
/// Only the latest note per control is kept. The history of a remote session is in the log; a settings page that
/// listed every attempt of an afternoon would be unreadable.
/// </para>
/// </summary>
public sealed class SessionVerification
{
    private readonly object _gate = new();
    private readonly Dictionary<string, VerificationNote> _notes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The process-wide instance. The desktop app does not pass one around, and the elevated worker keeps its own
    /// (empty) copy, so a shared instance cannot leak one machine's result into another's.
    /// </summary>
    public static SessionVerification Shared { get; } = new();

    /// <summary>Raised after any note changes, on whatever thread changed it.</summary>
    public event EventHandler? Changed;

    /// <summary>Records the outcome of one write. A <see cref="ControlVerdict.Confirmed"/> verdict clears the note.</summary>
    public void Mark(VerificationNote note)
    {
        ArgumentNullException.ThrowIfNull(note);

        bool changed;
        lock (_gate)
        {
            if (note.Verdict == ControlVerdict.Confirmed)
            {
                changed = _notes.Remove(note.Control);
            }
            else
            {
                changed = !_notes.TryGetValue(note.Control, out var existing) || existing != note;
                _notes[note.Control] = note;
            }
        }

        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Records a control as verified, clearing whatever was recorded before.</summary>
    public void Confirm(string control) =>
        Mark(new VerificationNote { Control = control, Verdict = ControlVerdict.Confirmed });

    /// <summary>The note for one control, or null when it has not been written yet or it was confirmed.</summary>
    public VerificationNote? For(string control)
    {
        lock (_gate) return _notes.TryGetValue(control, out var note) ? note : null;
    }

    /// <summary>Every note that needs the user's attention, in a stable order.</summary>
    public IReadOnlyList<VerificationNote> NeedsAttention()
    {
        lock (_gate)
        {
            return _notes.Values
                .Where(note => note.NeedsAttention)
                .OrderBy(note => note.Control, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    /// <summary>True when at least one control was written and could not be confirmed.</summary>
    public bool HasUnverified => NeedsAttention().Count > 0;

    /// <summary>How many controls are unconfirmed, for the report's summary line.</summary>
    public int UnverifiedCount => NeedsAttention().Count;

    /// <summary>One line per unconfirmed control, for the hardware report. Empty when everything was confirmed.</summary>
    public string Describe()
    {
        var notes = NeedsAttention();
        if (notes.Count == 0) return "";

        return string.Join(Environment.NewLine, notes.Select(note => note.Describe()));
    }

    /// <summary>Forgets everything. Wired to the settings page's rescan, like the read-back counters.</summary>
    public void Reset()
    {
        bool changed;
        lock (_gate)
        {
            changed = _notes.Count > 0;
            _notes.Clear();
        }

        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The name the report uses for a capability key. English, like the rest of the report: it is read by whoever
    /// triages the issue, not by the machine's owner.
    /// </summary>
    public static string LabelFor(string control) => control switch
    {
        "wmi.fanBehavior" => "Fan behaviour",
        "wmi.fanSpeed" => "Fan speed",
        "wmi.operatingMode" => "Operating mode",
        "wmi.muxMode" => "MUX switch",
        "wmi.batteryLimit" => "Battery charge limit",
        "wmi.keyboardLighting" => "Keyboard lighting",
        "wmi.winKeyLock" => "Windows-key lock",
        "wmi.lcdOverdrive" => "LCD overdrive",
        "wmi.coolBoost" => "CoolBoost",
        "wmi.backlightTimeout" => "Backlight timeout",
        _ => control
    };
}
