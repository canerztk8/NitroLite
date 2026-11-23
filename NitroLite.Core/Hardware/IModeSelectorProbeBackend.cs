using System.Text.Json.Nodes;

namespace NitroLite.Core.Hardware;

/// <summary>
/// A platform that can run the full selector sweep on explicit request: every undocumented WMI selector of the Acer
/// gaming classes, answered or not. Separate from <see cref="IHardwareSnapshotBackend"/> on purpose, because the two
/// have opposite safety profiles: a snapshot must be safe to take at any time and reads only documented selectors,
/// while the sweep pokes subfunctions whose behaviour nobody has confirmed — on one reported Acer model it
/// hard-reset the machine. The sweep therefore runs only behind an explicit yes/no confirmation, never as part of
/// a capture and never from a polling loop.
/// </summary>
public interface IModeSelectorProbeBackend
{
    /// <summary>
    /// Runs the sweep and returns the worker's raw answer, or null with the refusal when no worker is connected.
    /// Starting a worker is not attempted: a diagnostics button must not raise an elevation prompt, so the sweep
    /// answers "start direct WMI first" when the worker is not already running.
    /// </summary>
    Task<(JsonObject? Sweep, string Refusal)> ProbeModeSelectorsAsync();
}
