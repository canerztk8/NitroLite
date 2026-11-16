using NitroLite.Core;

namespace NitroLite.Windows;

/// <summary>
/// The ATK mouse's onboard profiles, exposed to the shared window.
/// </summary>
/// <remarks>
/// <para>
/// A thin wrapper rather than a reimplementation: <see cref="AtkMouseService"/> owns the HID report, the checksum and
/// the retry ladder, and duplicating any of that here would give the app two places to fix a protocol bug.
/// </para>
/// <para>
/// <see cref="IsSupported"/> is answered from the last query rather than from "the class exists". The service is
/// created on every Windows machine, but only a machine with the mouse plugged in can answer, and a row that appeared
/// because the object was constructed would offer four buttons that write nowhere.
/// </para>
/// </remarks>
public sealed class AtkMouseProfileService : IMouseProfileService
{
    private readonly AtkMouseService _mouse;
    private readonly object _gate = new();
    private bool _supported;
    private int _current;

    public AtkMouseProfileService(AtkMouseService? mouse = null)
    {
        _mouse = mouse ?? new AtkMouseService();
    }

    /// <summary>True once the mouse has answered a query in this session.</summary>
    public bool IsSupported
    {
        get { lock (_gate) return _supported; }
    }

    /// <summary>The profile the mouse reported, or 0 when it has not answered.</summary>
    public int CurrentProfile
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>Switches the mouse to <paramref name="profile"/>. 0 means "leave it alone" and always succeeds.</summary>
    public Task<bool> ApplyProfileAsync(int profile, CancellationToken cancellationToken = default)
    {
        if (profile <= 0) return Task.FromResult(true);

        return Task.Run(() =>
        {
            try
            {
                bool ok = _mouse.SwitchProfile(profile);
                if (!ok) return false;

                lock (_gate)
                {
                    _supported = true;
                    _current = profile;
                }

                return true;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Fare profili uygulanamadı", $"Config {profile}: {ex.Message}");
                return false;
            }
        }, cancellationToken);
    }

    /// <summary>Asks the mouse which profile it is in. Null when it cannot say.</summary>
    public Task<int?> QueryCurrentProfileAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            try
            {
                var profile = _mouse.QueryCurrentProfile();
                lock (_gate)
                {
                    // A null answer is "the mouse did not reply", which is not the same as "profile 0".
                    if (profile is int value)
                    {
                        _supported = true;
                        _current = value;
                    }
                }

                return profile;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Fare profili okunamadı", ex.Message);
                return (int?)null;
            }
        }, cancellationToken);
}
