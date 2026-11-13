using System;

namespace NitroLite.Core.Hardware;

/// <summary>
/// The single place the rest of the app asks "which machine is this". Caches the SMBIOS read for the session so
/// the window, the report and the CLI all see the same answer without paying for four WMI queries each time.
/// </summary>
public static class HardwareDetector
{
    private static readonly object Gate = new();
    private static DeviceIdentity? _cached;
    private static DeviceIdentity? _lastEmpty;
    private static DateTime _lastEmptyUtc = DateTime.MinValue;

    /// <summary>
    /// An empty read is retried at most this often. WMI can be slow to answer during logon, and caching that first
    /// empty answer for the whole session would leave the machine unidentified until the user restarted.
    /// </summary>
    private static readonly TimeSpan EmptyRetryInterval = TimeSpan.FromSeconds(30);

    /// <summary>The current machine. Never null; check <see cref="DeviceIdentity.IsEmpty"/> for "could not read".</summary>
    public static DeviceIdentity Current
    {
        get
        {
            lock (Gate)
            {
                if (_cached != null) return _cached;

                var now = DateTime.UtcNow;
                if (_lastEmptyUtc != DateTime.MinValue && now - _lastEmptyUtc < EmptyRetryInterval)
                    return _lastEmpty ?? new DeviceIdentity();

                var identity = SmbiosProbe.Read();
                if (!identity.IsEmpty)
                {
                    _cached = identity;
                    return identity;
                }

                _lastEmptyUtc = now;
                _lastEmpty = identity;
                return identity;
            }
        }
    }

    /// <summary>Drops the cache so the next read hits WMI again. Tests use this; nothing in the app calls it.</summary>
    public static void ResetForTesting()
    {
        lock (Gate)
        {
            _cached = null;
            _lastEmpty = null;
            _lastEmptyUtc = DateTime.MinValue;
        }
    }
}
