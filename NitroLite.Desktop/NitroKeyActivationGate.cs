namespace NitroLite.Desktop;

/// <summary>
/// Decides whether a Nitro-key press should bring the window forward, and does it.
/// </summary>
/// <remarks>
/// <para>
/// The listener knows nothing about windows and the window knows nothing about hardware, so the decision lives here:
/// one press, one check of the setting, one activation. Keeping it out of <see cref="App"/> is what makes it
/// assertable — the alternative is a test that has to build an Avalonia window to prove that a disabled switch does
/// nothing.
/// </para>
/// <para>
/// The setting is read on every press rather than captured once, because the user can turn the switch off while the
/// app is in the tray and the next press must respect that without a restart.
/// </para>
/// </remarks>
public sealed class NitroKeyActivationGate
{
    private readonly Func<bool> _isEnabled;
    private readonly Action _activate;

    /// <param name="isEnabled">Read on every press; false means the press is ignored.</param>
    /// <param name="activate">What "come forward" means: show, un-minimise, activate, re-assert the lighting.</param>
    public NitroKeyActivationGate(Func<bool> isEnabled, Action activate)
    {
        _isEnabled = isEnabled ?? throw new ArgumentNullException(nameof(isEnabled));
        _activate = activate ?? throw new ArgumentNullException(nameof(activate));
    }

    /// <summary>How many presses were acted on. Exposed so a test can count activations without a window.</summary>
    public int Activations { get; private set; }

    /// <summary>
    /// Handles one press. Returns true when the window was asked to come forward, false when the setting is off.
    /// </summary>
    public bool HandlePress()
    {
        if (!_isEnabled()) return false;

        Activations++;
        _activate();
        return true;
    }
}
