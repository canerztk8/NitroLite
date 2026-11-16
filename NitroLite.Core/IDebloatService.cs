namespace NitroLite.Core;

/// <summary>One Acer background service, as the services card lists it.</summary>
public sealed record AcerServiceEntry(
    string Name,
    string DisplayName,
    string Status,
    string StartType,
    bool IsDisabled)
{
    /// <summary>The name the card prints: the friendly one when the registry has it, the short one otherwise.</summary>
    public string Label => DisplayName.Length > 0 ? DisplayName : Name;
}

/// <summary>The outcome of one service operation, in the terms the card reports it.</summary>
public sealed record DebloatResult(bool Success, string? Error)
{
    public static DebloatResult Ok { get; } = new(true, null);

    public static DebloatResult Failed(string? error) => new(false, error);

    /// <summary>A result for a platform that has no Acer services to manage.</summary>
    public static DebloatResult Unsupported { get; } = new(false, "unsupported");
}

/// <summary>
/// Manages the Acer background services and NitroSense.
/// </summary>
/// <remarks>
/// <para>
/// The services this touches are the ones NitroLite does not use: the update agents, the care-center helpers and the
/// telemetry tasks. The Acer agent, the Care Center client and the lighting service are deliberately left alone,
/// because disabling them would break the hardware control the rest of the app depends on.
/// </para>
/// <para>
/// Every operation is reversible: the start types are recorded before they are changed, and
/// <see cref="RestoreServicesAsync"/> puts them back. A machine with no such services reports
/// <see cref="IsSupported"/> false and the card hides itself.
/// </para>
/// </remarks>
public interface IDebloatService
{
    /// <summary>True when this platform has Acer background services to manage.</summary>
    bool IsSupported { get; }

    /// <summary>True when a recorded start-type backup exists, so a restore can put the exact previous state back.</summary>
    bool HasRestorableBackup { get; }

    /// <summary>The installed services and their live state. Never throws.</summary>
    IReadOnlyList<AcerServiceEntry> GetServiceStatuses();

    /// <summary>True when a NitroSense process is running right now.</summary>
    bool IsNitroSenseRunning();

    /// <summary>Stops and disables the background services, recording their previous start types first.</summary>
    Task<DebloatResult> DisableBackgroundServicesAsync(CancellationToken cancellationToken = default);

    /// <summary>Puts the recorded start types back, or the factory defaults when there is no backup.</summary>
    Task<DebloatResult> RestoreServicesAsync(CancellationToken cancellationToken = default);

    /// <summary>Closes NitroSense and its helper processes.</summary>
    Task<DebloatResult> KillNitroSenseAsync(CancellationToken cancellationToken = default);
}
