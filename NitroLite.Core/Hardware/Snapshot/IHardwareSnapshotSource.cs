namespace NitroLite.Core.Hardware.Snapshot;

/// <summary>
/// One part of a capture: the Acer WMI selectors, or the vendor HID feature reports. Split from the service so the
/// Windows-only P/Invoke and the WMI worker stay in <c>NitroLite.Windows</c> while the ordering, the scrubbing and the
/// failure handling stay testable on any platform.
/// </summary>
public interface IHardwareSnapshotSource
{
    /// <summary>The section the entries are filed under, e.g. <c>wmi</c> or <c>hid</c>.</summary>
    string Section { get; }

    /// <summary>
    /// Every readable entry this source can produce. Throwing is allowed: the service turns a throwing source into a
    /// single <c>unavailable</c> entry rather than losing the rest of the capture.
    /// </summary>
    Task<IReadOnlyList<HardwareSnapshotEntry>> CaptureAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Runs the sources and builds one <see cref="HardwareSnapshot"/>. Read-only by construction: the only thing it can do
/// with hardware is ask a source to read it.
/// </summary>
public static class HardwareSnapshotService
{
    /// <summary>
    /// Captures every source in order. A source that throws, or a selector inside it that throws, becomes an
    /// <c>unavailable: …</c> entry: a capture that failed on one selector is still worth sending, and the entry that
    /// says so is the evidence that the selector exists and refused.
    /// </summary>
    public static async Task<HardwareSnapshot> CaptureAsync(
        string? model,
        string? board,
        IEnumerable<IHardwareSnapshotSource> sources,
        DateTimeOffset? capturedAt = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var entries = new List<HardwareSnapshotEntry>();
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (source == null) continue;

            try
            {
                var captured = await source.CaptureAsync(cancellationToken).ConfigureAwait(false);
                if (captured == null || captured.Count == 0)
                {
                    // Not a failure: this machine has no interface the source is allowed to read. Saying so is what
                    // tells a triager the difference between "no lighting controller" and "the controller refused".
                    entries.Add(HardwareSnapshotEntry.Unavailable(source.Section, "capture", "no matching interface"));
                    continue;
                }

                entries.AddRange(captured);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                entries.Add(HardwareSnapshotEntry.Unavailable(source.Section, "capture", Describe(ex)));
            }
        }

        return new HardwareSnapshot(model, board, entries, capturedAt);
    }

    /// <summary>
    /// <c>ManagementException: …</c> — the type and the message, which is what a maintainer reads. Public because a
    /// source that reads several selectors in one go, such as the selector sweep, guards them itself.
    /// </summary>
    public static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    /// <summary>
    /// A guard for one selector inside a source: the value, or an <c>unavailable</c> entry naming what went wrong. The
    /// delegate is awaited inside the guard so a worker round trip that times out is handled like a WMI call that threw.
    /// </summary>
    public static async Task<HardwareSnapshotEntry> ReadAsync(
        string section,
        string key,
        Func<Task<string?>> read,
        string missing = "not read")
    {
        try
        {
            var value = await read().ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(value)
                ? new HardwareSnapshotEntry(section, key, missing)
                : new HardwareSnapshotEntry(section, key, value.Trim());
        }
        catch (Exception ex)
        {
            return HardwareSnapshotEntry.Unavailable(section, key, Describe(ex));
        }
    }
}
