namespace NitroLite.Core;

/// <summary>
/// The processor performance boost mode, as Windows stores it in the active power scheme.
/// </summary>
/// <remarks>
/// The numeric values are the on-disk contract: they are the <c>PerfBoostMode</c> values the power scheme holds, and
/// the school-mode undo record writes them back verbatim. The enum lives here rather than next to
/// <c>CpuPowerService</c> because the state machine that records and restores it is platform-independent.
/// </remarks>
public enum CpuBoostMode
{
    Disabled = 0,    // Windows PerfBoostMode 0: no boost, stays at base clock (~3.8 GHz on the 7840HS)
    Enabled = 1,     // Windows default boost
    Aggressive = 2,  // Windows PerfBoostMode 2: boost as high as the CPU allows
    Efficient = 4    // Windows PerfBoostMode 4 "Efficient Aggressive": boost on, efficiency-biased; no fixed frequency cap
}
