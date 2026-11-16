namespace NitroLite.Core;

/// <summary>The outcome of one repair or optimisation, in the terms the card reports it.</summary>
public sealed record ToolResult(bool Success, string Message)
{
    public static ToolResult Ok(string message) => new(true, message);

    public static ToolResult Failed(string message) => new(false, message);

    /// <summary>A result for a platform that has no such tool.</summary>
    public static ToolResult Unsupported { get; } = new(false, "unsupported");
}

/// <summary>
/// The repair and optimisation tools: the three service repairs and the reversible Windows optimisation.
/// </summary>
/// <remarks>
/// <para>
/// These are the buttons a user presses when something is already wrong, so each one reports what it did in a
/// sentence rather than returning a bare bool. A repair that half-worked has to say which half.
/// </para>
/// <para>
/// The optimisation is reversible by design: <see cref="OptimizeAsync"/> records every start type and setting it
/// changes, and <see cref="RestoreOptimizeAsync"/> puts them back. A machine with no such tools reports
/// <see cref="IsSupported"/> false and the card hides itself.
/// </para>
/// </remarks>
public interface ISystemToolsService
{
    /// <summary>True when this platform has repair or optimisation tools.</summary>
    bool IsSupported { get; }

    /// <summary>Re-points the keyboard lighting service at the current driver store and restarts it.</summary>
    Task<ToolResult> FixKeyboardLightingAsync(CancellationToken cancellationToken = default);

    /// <summary>Re-points the Acer agent and lighting services at the current driver package.</summary>
    Task<ToolResult> FixAcerServicesAsync(CancellationToken cancellationToken = default);

    /// <summary>Verifies the MUX registry value and clears the stale logon tasks around it.</summary>
    Task<ToolResult> RestoreMuxSwitchAsync(CancellationToken cancellationToken = default);

    /// <summary>Applies the reversible Windows optimisation.</summary>
    Task<ToolResult> OptimizeAsync(CancellationToken cancellationToken = default);

    /// <summary>Puts back everything <see cref="OptimizeAsync"/> changed.</summary>
    Task<ToolResult> RestoreOptimizeAsync(CancellationToken cancellationToken = default);
}
