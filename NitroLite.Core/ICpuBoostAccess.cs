namespace NitroLite.Core;

/// <summary>
/// The CPU boost (processor performance boost mode) contract, as the school-mode state machine needs it: read the
/// current mode for one power source, and write it for the adapter and the battery independently.
/// </summary>
/// <remarks>
/// The Windows implementation is <c>CpuPowerService</c>, which reaches the active power scheme through
/// <c>powrprof.dll</c>. It stays in the WPF project; this interface is what lets school mode be compiled, and its
/// undo record tested, on a machine that has no power scheme at all.
/// </remarks>
public interface ICpuBoostAccess
{
    /// <summary>
    /// The exact boost mode stored for <paramref name="isAc"/>, or null when the setting is absent or unreadable.
    /// "Exact" matters: the friendly <c>QueryBoostMode</c> collapses several stored values into one label, and the
    /// undo record has to put back the value that was actually there.
    /// </summary>
    CpuBoostMode? QueryBoostModeExact(bool isAc);

    /// <summary>
    /// Writes the boost mode for the adapter and/or the battery. False when the scheme refused the write.
    /// </summary>
    Task<bool> SetBoostModeAsync(CpuBoostMode mode, bool targetAc = true, bool targetDc = true);
}
