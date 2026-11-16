namespace NitroLite.Core;

/// <summary>
/// Battery charge mode as stored by Acer Care Center (AcerCCAgent "BatteryHealthy").
/// Values match NitroSense's BATTERY_CHARGE_MODE enum.
/// </summary>
/// <remarks>
/// The enum is part of the hardware contract (<c>IHardwareController.GetBatteryChargeModeAsync</c>), so it lives
/// here rather than in the WebSocket client that reads it on Windows.
/// </remarks>
public enum BatteryChargeMode
{
    /// <summary>Charging stops at ~80 % ("Optimized battery charging" / battery health mode).</summary>
    Optimized80 = 0,
    /// <summary>Charge to 100 %.</summary>
    Full = 1,
    /// <summary>Adaptive charging (not offered on every model).</summary>
    Adaptive = 2
}

/// <summary>
/// What the battery page should show for the charge limit. <see cref="Available"/> is false when the agent did not
/// answer, which is not the same as "the limit is off": the checkbox must then be disabled rather than claim a
/// limit nothing enforces.
/// </summary>
public readonly record struct ChargeLimitUiState(bool Available, bool Limited);
