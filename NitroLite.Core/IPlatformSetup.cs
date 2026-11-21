namespace NitroLite.Core;

/// <summary>What the kernel could still offer this machine, and the one step that would unlock it.</summary>
public enum KernelSetupHint
{
    /// <summary>Nothing to suggest: the features are there, or this is not an Acer gaming laptop.</summary>
    None = 0,

    /// <summary>
    /// The loaded <c>acer-wmi</c> has the <c>predator_v4</c> switch and it is off. Turning it on is a one-line modprobe
    /// option and gives fan tachometers and the Acer operating modes on models the driver's quirk table does not list.
    /// </summary>
    EnablePredatorV4 = 1,

    /// <summary>
    /// Fan speed, keyboard RGB, the charge limit and the other controls need the out-of-tree Linuwu-Sense module; the
    /// kernel offers no path to them on this machine.
    /// </summary>
    InstallLinuwuSense = 2
}

/// <summary>How one setup step ended, with the sentence the settings page shows.</summary>
public sealed record PlatformSetupResult(bool Ok, string Message);

/// <summary>
/// The one-time steps that give an unprivileged desktop app access to the hardware, run with a single password prompt.
/// </summary>
/// <remarks>
/// Linux only today. Windows elevates a helper process per session instead, so it has nothing to set up once.
/// </remarks>
public interface IPlatformSetup
{
    /// <summary>True when a graphical password prompt (<c>pkexec</c>) is available to run the steps.</summary>
    bool CanElevate { get; }

    /// <summary>True when this user is not in the <c>input</c> group, so the Nitro key cannot be heard.</summary>
    bool NeedsInputGroup { get; }

    /// <summary>The next kernel-side step worth suggesting.</summary>
    KernelSetupHint KernelHint { get; }

    /// <summary>
    /// What the mainline kernel does for this exact model, for example "Linux 7.4 supports it fully", or null when
    /// there is nothing specific to say.
    /// </summary>
    string? ModelNote { get; }

    /// <summary>
    /// Installs the udev rule, creates the <c>nitrolite</c> group and adds this user to it and to <c>input</c>. One
    /// password prompt. Takes effect after the user logs out and back in.
    /// </summary>
    Task<PlatformSetupResult> GrantAccessAsync();

    /// <summary>Writes <c>options acer_wmi predator_v4=1</c> to modprobe.d. Takes effect after a restart.</summary>
    Task<PlatformSetupResult> EnablePredatorV4Async();

    /// <summary>
    /// Records this machine for a bug report, the way <c>tools/linux-capture</c> does, into
    /// <paramref name="directory"/>. Returns the file's path, or null when it could not be written. Reads only.
    /// </summary>
    Task<string?> WriteCaptureAsync(string directory);

    /// <summary>Two or three lines about this machine for the top of the issue: model, kernel, distribution.</summary>
    string CaptureSummary { get; }
}
