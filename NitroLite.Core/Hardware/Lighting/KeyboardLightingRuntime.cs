namespace NitroLite.Core.Hardware.Lighting;

/// <summary>
/// The process's keyboard lighting transport memory, shared by the controller that writes colours and the report that
/// describes them.
/// </summary>
/// <remarks>
/// <para>
/// A static because the decision belongs to the process, not to a call: "<c>AN16-61|1.53</c> answers over HID" is
/// learned once and has to survive the controller instance being replaced by a rescan. The window builds a fresh
/// hybrid controller on a rescan, and a per-instance cache would make the first colour after every rescan pay the
/// three failing WMI round trips again.
/// </para>
/// <para>
/// Tests construct their own <see cref="KeyboardLightingRouter"/>; this holder exists only so the two consumers that
/// are not test-friendly (the controller and the hardware report) can reach the same one.
/// </para>
/// </remarks>
public static class KeyboardLightingRuntime
{
    /// <summary>The process-wide router.</summary>
    public static KeyboardLightingRouter Router { get; } = new();
}
