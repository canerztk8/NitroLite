namespace NitroLite.Core;

/// <summary>
/// The plain "start with Windows" entry: one value under <c>HKCU\...\Run</c>, no elevation, no prompt.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="IAutoStartService"/> on purpose. That one registers a Task Scheduler entry that starts the
/// app elevated and costs a UAC prompt to turn on; this one is the ordinary Run key every Windows application writes,
/// and it is what a user means by "start with Windows". Offering only the elevated variant would make the cheap one
/// unreachable, and offering only this one would leave the direct-WMI backend unusable at sign-in.
/// </para>
/// <para>
/// The two are mutually exclusive by construction: turning one on removes the other, because two registrations would
/// start NitroLite twice at logon. A platform with no such key supplies no implementation and the card hides.
/// </para>
/// </remarks>
public interface IRunAtStartupService
{
    /// <summary>True when this platform has a per-user sign-in entry.</summary>
    bool IsSupported { get; }

    /// <summary>True when the entry exists right now.</summary>
    Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes or removes the entry. The minimised flag is baked into the command line, so the tray behaviour is decided
    /// at registration time rather than by a second setting the app has to read at launch.
    /// </summary>
    Task<bool> SetEnabledAsync(bool enabled, bool startMinimized, CancellationToken cancellationToken = default);
}
