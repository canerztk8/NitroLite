namespace NitroLite.Core;

/// <summary>
/// The outcome of one standby-list clean, in the terms the card reports it.
/// </summary>
/// <remarks>
/// The standby list is cached memory, not "in use" memory, so the used-RAM bar never moves when it is released.
/// Available memory is the figure that does move, which is why both ends are carried here: showing them before and
/// after is what makes the result checkable against Task Manager instead of asking the user to trust one number.
/// </remarks>
public sealed class MemoryCleanResult
{
    /// <summary>True when the standby list was actually released.</summary>
    public bool Cleaned { get; init; }

    /// <summary>Drop of the standby list caused by the clean, in megabytes.</summary>
    public double FreedMegabytes { get; init; }

    /// <summary>Available physical memory before the clean, in gigabytes.</summary>
    public double AvailableBeforeGigabytes { get; init; }

    /// <summary>Available physical memory after the clean, in gigabytes.</summary>
    public double AvailableGigabytes { get; init; }

    /// <summary>True when the helper tool is not installed; the card then offers the download page.</summary>
    public bool ToolMissing { get; init; }

    /// <summary>True when the elevation prompt was dismissed, so nothing ran.</summary>
    public bool ElevationRefused { get; init; }

    /// <summary>Technical failure text, not localized; the caller wraps it in a translated sentence.</summary>
    public string? Error { get; init; }

    /// <summary>A result for a platform that has no standby list to release.</summary>
    public static MemoryCleanResult Unsupported { get; } = new()
    {
        Cleaned = false,
        Error = "unsupported"
    };
}

/// <summary>
/// Where the standby-list helper is published.
/// </summary>
/// <remarks>
/// The URL lives in the platform-independent half because the card that offers the download is in the cross-platform
/// shell, which cannot see the Windows assembly the tool itself is launched from. One constant, so the two cannot
/// drift apart and send a user to a page that no longer exists.
/// </remarks>
public static class MemoryCleanerTool
{
    public const string ProjectUrl = "https://github.com/IgorMundstein/WinMemoryCleaner";

    public const string ReleasesUrl = "https://github.com/IgorMundstein/WinMemoryCleaner/releases/latest";
}

/// <summary>
/// Releases the operating system's standby (cached) memory.
/// </summary>
/// <remarks>
/// Windows has a standby list and a tool that empties it; Linux does not have the same list, so its implementation
/// reports <see cref="IsSupported"/> false and the card hides itself rather than offering a button that does nothing.
/// </remarks>
public interface IMemoryCleaner
{
    /// <summary>True when this platform can release the standby list at all.</summary>
    bool IsSupported { get; }

    /// <summary>Releases the standby list. Never throws: a failure comes back as a result with <c>Error</c> set.</summary>
    Task<MemoryCleanResult> CleanStandbyMemoryAsync(CancellationToken cancellationToken = default);
}
