using System.Net.Http;

namespace NitroLite.Core.Hardware;

/// <summary>What one attempt to refresh the downloaded profile table did.</summary>
/// <param name="Saved">True when a validated table was written to <see cref="RemoteProfiles.CachePath"/>.</param>
/// <param name="Reason">One line for the log: why nothing was written, or how many profiles were kept.</param>
/// <param name="ProfileCount">How many profiles the accepted document carried.</param>
public sealed record RemoteProfileResult(bool Saved, string Reason, int ProfileCount)
{
    /// <summary>Nothing was written, for this reason.</summary>
    public static RemoteProfileResult Skipped(string reason) => new(false, reason, 0);
}

/// <summary>
/// Downloads the profile table published with the project and keeps it between the user's own file and the one this
/// build shipped with.
/// </summary>
/// <remarks>
/// <para>
/// The point is that a laptop nobody had when the binary was built can be supported without a new release. A table
/// published to the project's own repository is fetched once at startup, validated as strictly as the embedded one,
/// and stored atomically; the catalogue then reads three documents in priority order — user, downloaded, embedded.
/// </para>
/// <para>
/// Everything about the fetch is deliberately conservative. It is HTTPS to one host, it is bounded in size and time, it
/// never throws and never blocks the window, and a failure of any kind leaves the previous table exactly where it was.
/// A machine that is offline, behind a proxy that answers HTML, or pointing at a repository that has been renamed sees
/// no difference at all: the app starts with the profiles it already had.
/// </para>
/// <para>
/// The validation is the part that matters. A profile names fan ceilings and sensor ids, and those turn into fan
/// commands and register reads; a plausible-looking but wrong value is exactly the kind of thing this project refuses
/// to apply on a guess. So a downloaded document is accepted only when every record passes the embedded table's own
/// rules, carries at least one profile, and stays inside the plausibility window.
/// <para>
/// The published table is the project's own document, so it is allowed the catch-all record the embedded table also
/// carries. Refusing it would reject every real download, which is what the first live run of this code did; the
/// protections that matter are the pinned host, the size cap and the plausibility window, and a hand-edited user file
/// is still held to the stricter rule.
/// </para>
/// </para>
/// </remarks>
public static class RemoteProfiles
{
    /// <summary>The published table. The path is the one in this repository.</summary>
    public const string Source =
        "https://raw.githubusercontent.com/canerztk8/NitroLite/main/NitroLite.Core/Hardware/Profiles/profiles.json";

    /// <summary>Largest document this will keep. The real table is a few tens of kilobytes.</summary>
    public const int MaximumBytes = 512 * 1024;

    /// <summary>How long a refresh may take before it is abandoned.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private static readonly object Gate = new();
    private static HttpClient? _client;

    /// <summary>Where the accepted table is kept. Same folder as the user file.</summary>
    public static string CachePath => ProfileCatalog.RemoteProfilePath;

    /// <summary>The one host this will talk to, and only over HTTPS.</summary>
    public static bool IsTrustedSource(Uri source) =>
        source != null &&
        source.Scheme == Uri.UriSchemeHttps &&
        string.Equals(source.Host, "raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Fetches and installs the published table. Never throws: every failure is a <see cref="RemoteProfileResult"/> that
    /// says nothing was written.
    /// </summary>
    public static async Task<RemoteProfileResult> RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!Uri.TryCreate(Source, UriKind.Absolute, out var uri) || !IsTrustedSource(uri))
                return RemoteProfileResult.Skipped("the profile source is not the trusted host");

            using var response = await Client
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return RemoteProfileResult.Skipped($"the profile source answered {(int)response.StatusCode}");

            var declared = response.Content.Headers.ContentLength;
            if (declared is > MaximumBytes)
                return RemoteProfileResult.Skipped($"the profile source offered {declared} bytes");

            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (text.Length > MaximumBytes)
                return RemoteProfileResult.Skipped($"the profile source returned {text.Length} characters");

            return Install(text);
        }
        catch (OperationCanceledException)
        {
            return RemoteProfileResult.Skipped("the refresh was cancelled or timed out");
        }
        catch (Exception ex)
        {
            return RemoteProfileResult.Skipped($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Validates a document and, when it is entirely acceptable, writes it to <paramref name="destination"/> (the cache
    /// by default) in one atomic step. A rejected document leaves whatever was there untouched.
    /// </summary>
    public static RemoteProfileResult Install(string? json, string? destination = null)
    {
        if (string.IsNullOrWhiteSpace(json))
            return RemoteProfileResult.Skipped("the downloaded document was empty");

        if (!ProfileCatalog.Validate(json, out var count, out var rejections))
        {
            var first = rejections.Count > 0 ? rejections[0] : "no profiles";
            return RemoteProfileResult.Skipped($"the downloaded table was rejected ({first})");
        }

        try
        {
            AtomicFile.WriteAllText(string.IsNullOrWhiteSpace(destination) ? CachePath : destination, json);
            return new RemoteProfileResult(true, $"{count} profiles kept", count);
        }
        catch (Exception ex)
        {
            return RemoteProfileResult.Skipped($"the table could not be written ({ex.GetType().Name})");
        }
    }

    /// <summary>The shared client, created once and only when a refresh actually runs.</summary>
    private static HttpClient Client
    {
        get
        {
            lock (Gate)
            {
                return _client ??= new HttpClient { Timeout = RequestTimeout };
            }
        }
    }
}
