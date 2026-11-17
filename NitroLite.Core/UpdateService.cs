using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace NitroLite.Core;

/// <summary>A newer release found on GitHub, with everything needed to fetch and verify it.</summary>
/// <param name="HtmlUrl">
/// The release's own page on GitHub, as the API reported it. Null when the payload carried none, which is why
/// <see cref="UpdateService.ReleaseNotesUrl"/> exists rather than this being read directly.
/// </param>
public sealed record UpdateInfo(
    Version Version,
    string Tag,
    string AssetName,
    string DownloadUrl,
    string? Sha256,
    long SizeBytes,
    string? HtmlUrl = null)
{
    public string DisplayVersion => $"v{Version.Major}.{Version.Minor}.{Version.Build}";
}

/// <summary>
/// What the startup recovery pass found: the files it put back from a backup, the leftovers it swept, and the
/// files it could not repair.
/// </summary>
/// <remarks>
/// <see cref="Failed"/> is not cosmetic. A file listed there still has its only surviving copy sitting next to it
/// under a <c>.old</c> or <c>.new</c> name, which is why the sweep is skipped entirely when the list is not empty.
/// </remarks>
public sealed record UpdateRecoveryReport(
    IReadOnlyList<string> Restored,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Failed)
{
    /// <summary>True when the install folder needed no repair and held no leftovers.</summary>
    public bool IsClean => Restored.Count == 0 && Removed.Count == 0 && Failed.Count == 0;
}

/// <summary>
/// Checks GitHub for a newer release, downloads it, verifies the SHA-256 GitHub publishes for the asset, and swaps
/// the running executable.
/// <para>
/// Trust rests on HTTPS to api.github.com plus the digest: NitroLite is unsigned, so a compromised GitHub account
/// could publish a malicious build and this check would accept it. Two-factor authentication on the account is the
/// real protection, not this code.
/// </para>
/// <para>
/// Nothing here runs during a game session, and nothing runs at all in a development build: the caller opens the
/// release page instead.
/// </para>
/// </summary>
public static class UpdateService
{
    public const string Owner = "canerztk8";
    public const string Repository = "NitroLite";

    /// <summary>
    /// The files the release package is allowed to contain, as paths relative to the package root. Anything else is
    /// refused, not extracted. WinMemoryCleaner lives in its own folder because the app looks for it there.
    /// </summary>
    public static readonly string[] PackageFiles =
    {
        "NitroLite.exe",
        "LICENSE",
        "THIRD-PARTY-NOTICES.md",
        "tools/WinMemoryCleaner.exe",
        "tools/WinMemoryCleaner-LICENSE.txt",
        "tools/WinMemoryCleaner-source.zip"
    };

    public static string LatestReleaseApi => $"https://api.github.com/repos/{Owner}/{Repository}/releases/latest";
    public static string ReleasePageUrl => $"https://github.com/{Owner}/{Repository}/releases/latest";
    public static string NewIssueUrl => $"https://github.com/{Owner}/{Repository}/issues/new";

    /// <summary>
    /// True when <paramref name="url"/> is an absolute https address with a host. The scheme check is the point:
    /// the address comes out of a JSON payload and is handed straight to the shell, so anything that is not https
    /// (<c>file:</c>, <c>http:</c>, a bare path, a <c>javascript:</c> URL) must never reach the browser.
    /// </summary>
    /// <remarks>
    /// This is the check for a link the user opens. Anything the app itself fetches goes through
    /// <see cref="IsTrustedDownloadUrl"/> instead, which pins the host as well.
    /// </remarks>
    public static bool IsSafeHttpsUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Length > 0;

    /// <summary>
    /// The only hosts the updater will fetch from. Exact matches, never suffixes: a suffix check would accept
    /// <c>evil-github.com</c>, and the point of the list is that a payload cannot steer the download elsewhere.
    /// </summary>
    /// <remarks>
    /// <c>objects.githubusercontent.com</c> and <c>release-assets.githubusercontent.com</c> are here because GitHub
    /// answers a release download with a redirect to one of them; without them every update would be refused.
    /// </remarks>
    public static readonly string[] TrustedHosts =
    {
        "github.com",
        "api.github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com"
    };

    /// <summary>True when <paramref name="host"/> is one of <see cref="TrustedHosts"/>.</summary>
    public static bool IsTrustedHost(string? host) =>
        host is { Length: > 0 } && TrustedHosts.Contains(host, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="url"/> is an absolute https address on a trusted host. Every hop of a download is
    /// checked against this, including each redirect: a redirect is the one place an address can change after it was
    /// validated, and the default <see cref="HttpClient"/> follows one to any host at all.
    /// </summary>
    public static bool IsTrustedDownloadUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && IsTrustedHost(uri.Host);

    /// <summary>
    /// The page the release-notes button opens: the release's own page when GitHub reported a usable one, and the
    /// repository's releases list otherwise. A pending update always has a page to show, so the button never opens
    /// nothing.
    /// </summary>
    public static string ReleaseNotesUrl(UpdateInfo? info) =>
        info?.HtmlUrl is { } url && IsSafeHttpsUrl(url) ? url : ReleasePageUrl;

    /// <summary>
    /// True when NitroLite runs from the published single-file package. A development build has a real assembly
    /// path, and updating it would replace a build the developer is working on.
    /// <para>
    /// The empty string is the signal, not a fallback: the single-file host has no assembly on disk, so
    /// <see cref="System.Reflection.Assembly.Location"/> returns "". IL3000 warns about exactly that, which is why
    /// the warning is suppressed here rather than worked around.
    /// </para>
    /// </summary>
#pragma warning disable IL3000 // Assembly.Location is empty in a single-file app; that is the test.
    public static bool IsPackaged => string.IsNullOrEmpty(typeof(UpdateService).Assembly.Location);
#pragma warning restore IL3000

    /// <summary>The version this build reports, from the project file.</summary>
    public static Version CurrentVersion =>
        typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0);

    public static string CurrentVersionText
    {
        get
        {
            var v = CurrentVersion;
            return v.Build > 0 ? $"v{v.Major}.{v.Minor}.{v.Build}" : $"v{v.Major}.{v.Minor}";
        }
    }

    private static readonly HttpClient Http = CreateClient();

    /// <summary>Largest package the updater will download. The real one is a few hundred megabytes.</summary>
    public const long MaxDownloadBytes = 1024L * 1024 * 1024;

    /// <summary>How many redirects one fetch may follow before it is refused.</summary>
    private const int MaxRedirects = 5;

    private static HttpClient CreateClient()
    {
        // Redirects are followed by hand, because the host has to be checked again at every hop. With the default
        // handler a redirect from github.com to anywhere at all is followed silently, so the address that was
        // validated is not the address that gets fetched.
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        // GitHub rejects an API request without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NitroLite", CurrentVersionText.TrimStart('v')));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    /// <summary>
    /// Fetches <paramref name="url"/>, following redirects by hand so the host can be checked again at every hop.
    /// </summary>
    private static Task<HttpResponseMessage> SendAsync(
        string url, HttpCompletionOption completionOption, CancellationToken cancellationToken) =>
        SendAsync(url, completionOption, IsTrustedDownloadUrl, cancellationToken);

    /// <inheritdoc cref="SendAsync(string, HttpCompletionOption, CancellationToken)"/>
    /// <param name="isTrusted">
    /// The host check. The public path passes <see cref="IsTrustedDownloadUrl"/>; the tests pass their own so a
    /// loopback server can stand in for GitHub without the production allowlist being widened to allow it.
    /// </param>
    internal static async Task<HttpResponseMessage> SendAsync(
        string url, HttpCompletionOption completionOption, Func<string?, bool> isTrusted,
        CancellationToken cancellationToken)
    {
        var current = url;
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            if (!isTrusted(current))
                throw new InvalidOperationException($"Refusing to fetch from an untrusted address: {current}");

            HttpResponseMessage response;
            var request = new HttpRequestMessage(HttpMethod.Get, current);
            try
            {
                response = await Http.SendAsync(request, completionOption, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                request.Dispose();
            }

            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;

            var location = response.Headers.Location;
            response.Dispose();
            if (location == null)
                throw new HttpRequestException("The server answered with a redirect and no location.");

            // A relative Location is resolved against the address that was just checked, so it stays on that host.
            current = location.IsAbsoluteUri
                ? location.ToString()
                : new Uri(new Uri(current, UriKind.Absolute), location).ToString();
        }

        throw new HttpRequestException($"The server redirected more than {MaxRedirects} times.");
    }

    /// <summary>
    /// Asks GitHub for the newest release. Returns null when this build is already current, when the release has no
    /// package for this platform, or when the answer cannot be understood. Network failures throw, so the caller can
    /// tell "no update" from "could not ask".
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(LatestReleaseApi, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParseRelease(document.RootElement, CurrentVersion);
    }

    /// <summary>Reads a release payload. Split out so the parsing can be tested without a network call.</summary>
    public static UpdateInfo? ParseRelease(JsonElement release, Version current)
    {
        if (release.ValueKind != JsonValueKind.Object) return null;
        if (!release.TryGetProperty("tag_name", out var tagElement) || tagElement.ValueKind != JsonValueKind.String) return null;
        var tag = tagElement.GetString() ?? "";
        if (!TryParseTag(tag, out var version)) return null;
        if (version <= Normalize(current)) return null;

        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;

        // The release's own page, for the release-notes button. Optional: a payload without it still updates, the
        // button just falls back to the releases list.
        var htmlUrl = release.TryGetProperty("html_url", out var h) && h.ValueKind == JsonValueKind.String
            ? h.GetString() : null;

        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object) continue;
            var name = asset.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            if (name == null || !name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase)) continue;

            var url = asset.TryGetProperty("browser_download_url", out var u) && u.ValueKind == JsonValueKind.String
                ? u.GetString() : null;
            if (string.IsNullOrWhiteSpace(url)) continue;

            // The package is downloaded and then executed. Only https on a GitHub host is acceptable: a file:// or
            // http:// address in the payload would either fail outright or fetch something the published digest was
            // never computed over, and the digest is the only thing standing between the user and a tampered build.
            if (!IsTrustedDownloadUrl(url)) continue;

            long size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var parsedSize) ? parsedSize : 0;

            // GitHub publishes "sha256:<hex>" for release assets. Without it there is nothing to verify against, and
            // an unverified executable is not something this app should install by itself.
            string? sha = null;
            if (asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String)
            {
                var digest = d.GetString() ?? "";
                const string prefix = "sha256:";
                if (digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    sha = digest[prefix.Length..].Trim().ToLowerInvariant();
            }

            return new UpdateInfo(version, tag, name, url!, sha, size, htmlUrl);
        }

        return null;
    }

    /// <summary>Accepts "v1.2.0", "1.2.0" and "v1.2".</summary>
    public static bool TryParseTag(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag)) return false;
        var text = tag.Trim().TrimStart('v', 'V');
        if (!Version.TryParse(text, out var parsed)) return false;
        version = Normalize(parsed);
        return true;
    }

    /// <summary>Drops the revision so 1.2.0.0 and 1.2.0 compare equal.</summary>
    public static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0));

    /// <summary>
    /// Downloads the package to a temporary file, reporting 0..1 progress. The caller verifies it before use. A
    /// failed or interrupted download leaves nothing behind.
    /// </summary>
    public static Task<string> DownloadAsync(
        UpdateInfo info, IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        DownloadAsync(info.DownloadUrl, info.SizeBytes, progress, cancellationToken);

    /// <inheritdoc cref="DownloadAsync(UpdateInfo, IProgress{double}?, CancellationToken)"/>
    /// <remarks>
    /// The address and the expected size on their own, for a caller that holds an <see cref="UpdateCheckResult"/>
    /// rather than an <see cref="UpdateInfo"/>. The two are the only fields the download reads.
    /// </remarks>
    public static Task<string> DownloadAsync(
        string url, long sizeBytes, IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        DownloadAsync(url, sizeBytes, progress, IsTrustedDownloadUrl, cancellationToken);

    /// <inheritdoc cref="DownloadAsync(string, long, IProgress{double}?, CancellationToken)"/>
    internal static async Task<string> DownloadAsync(
        string url, long sizeBytes, IProgress<double>? progress, Func<string?, bool> isTrusted,
        CancellationToken cancellationToken)
    {
        var target = Path.Combine(Path.GetTempPath(), $"NitroLite-update-{Guid.NewGuid():N}.zip");
        try
        {
            using var response = await SendAsync(url, HttpCompletionOption.ResponseHeadersRead, isTrusted, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            // A server can declare any length it likes, and the copy loop is what actually fills the disk. Both are
            // checked: the declared length refuses an obviously oversized package before a byte is written, and the
            // running total catches a response that lies about it.
            var declared = response.Content.Headers.ContentLength;
            if (declared > MaxDownloadBytes) throw TooLarge();

            var total = declared ?? sizeBytes;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long copied = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    copied += read;
                    if (copied > MaxDownloadBytes) throw TooLarge();

                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    if (total > 0) progress?.Report(Math.Clamp((double)copied / total, 0, 1));
                }
            }

            return target;
        }
        catch
        {
            CleanUp(target);
            throw;
        }
    }

    private static InvalidDataException TooLarge() =>
        new($"The package is larger than {MaxDownloadBytes / (1024 * 1024)} MB.");

    /// <summary>SHA-256 of a file, lower-case hex.</summary>
    public static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>True when the file's SHA-256 matches the digest GitHub published for the asset.</summary>
    public static bool VerifyDigest(string path, string? expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256)) return false;
        try
        {
            return string.Equals(ComputeSha256(path), expectedSha256.Trim().ToLowerInvariant(), StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Extracts the package into a staging folder and returns it. Only the files in <see cref="PackageFiles"/> are
    /// written, and every entry is checked to land inside the staging folder, so a crafted archive cannot escape it.
    /// </summary>
    /// <summary>Largest single file the updater will write out of a package.</summary>
    private const long MaxEntryBytes = 512L * 1024 * 1024;

    /// <summary>Largest total the updater will write out of a package.</summary>
    private const long MaxPackageBytes = 1024L * 1024 * 1024;

    public static string ExtractToStaging(string zipPath)
    {
        var staging = Path.Combine(Path.GetTempPath(), $"NitroLite-stage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            ExtractInto(staging, zipPath);
            return staging;
        }
        catch
        {
            // A refused package must not leave its folder behind. Every failed update used to add an empty
            // NitroLite-stage-* to %TEMP% that nothing ever swept, so the folder is removed before the reason
            // travels up to the caller.
            CleanUp(staging);
            throw;
        }
    }

    /// <summary>Unpacks a package into an already created staging folder, or throws with the reason.</summary>
    private static void ExtractInto(string staging, string zipPath)
    {
        var stagingRoot = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(zipPath);

        // The release script archives the staging folder itself, so entries sit under "NitroLite/". Find the folder
        // that holds NitroLite.exe and use it as the root; a flat archive has an empty prefix.
        var exeEntry = archive.Entries.FirstOrDefault(e =>
            string.Equals(Path.GetFileName(e.FullName), "NitroLite.exe", StringComparison.OrdinalIgnoreCase));
        if (exeEntry == null) throw new InvalidDataException("NitroLite.exe is not in the package.");
        var prefix = exeEntry.FullName[..^"NitroLite.exe".Length];

        // A zip declares its uncompressed size, and a hostile or truncated one can declare gigabytes. The real
        // package is a few hundred megabytes, so anything past the cap is refused before a byte is written: a full
        // system drive is a worse outcome than a failed update.
        long totalBytes = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                continue;
            if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal)) continue;

            var relative = entry.FullName[prefix.Length..].Replace('\\', '/');
            if (relative.Length == 0) continue;
            if (!PackageFiles.Contains(relative, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException($"Unexpected file in the package: {relative}");

            if (entry.Length > MaxEntryBytes)
                throw new InvalidDataException($"Package entry is too large: {relative} ({entry.Length} bytes)");

            var destination = Path.GetFullPath(Path.Combine(staging, relative));
            if (!destination.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Package entry escapes the staging folder: {relative}");

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);

            // The declared length is what the archive claims, and a crafted one can claim a few kilobytes while
            // expanding to gigabytes. The size that matters is the one on the file that was just written, and the
            // running total is what the package cap is applied to.
            long written = new FileInfo(destination).Length;
            if (written > MaxEntryBytes)
                throw new InvalidDataException($"Package entry is too large: {relative} ({written} bytes)");

            totalBytes += written;
            if (totalBytes > MaxPackageBytes)
                throw new InvalidDataException($"Package expands to more than {MaxPackageBytes / (1024 * 1024)} MB.");
        }

        foreach (var required in new[] { "NitroLite.exe" })
        {
            if (!File.Exists(Path.Combine(staging, required)))
                throw new InvalidDataException($"The package does not contain {required}.");
        }
    }

    /// <summary>
    /// Copies the staged files over the install folder, keeping the package's folder layout. Returns the path of the
    /// new executable.
    /// </summary>
    /// <remarks>
    /// The install is a two-phase commit, because a half-applied update is worse than a refused one: the old code
    /// copied the data files first and swapped the executable last, so a failure in between left a new LICENSE and
    /// a new WinMemoryCleaner next to the old NitroLite.exe, and nothing put them back.
    /// <list type="number">
    /// <item>Prepare: every staged file is copied next to its destination under a <c>.new</c> name. A failure here
    /// deletes those copies and leaves the install folder exactly as it was.</item>
    /// <item>Commit: each destination is moved aside to <c>.old</c> and its <c>.new</c> takes its place. A failure
    /// rolls every completed swap back.</item>
    /// <item>Clean up: the <c>.old</c> files go, except the executable's, which this process is still running
    /// from; <see cref="CleanUpPreviousVersion"/> deletes that one at the next start.</item>
    /// </list>
    /// </remarks>
    public static string InstallFromStaging(string staging, string installDirectory)
    {
        // Only a folder this class created may be installed from. Without this, a caller that passed the wrong path
        // (or a path an attacker could write to) would copy arbitrary files over the install folder.
        var stagingFull = Path.GetFullPath(staging);
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        if (!stagingFull.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(stagingFull).StartsWith("NitroLite-stage-", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The staging folder is not a NitroLite staging folder.");
        }

        var target = Path.Combine(installDirectory, "NitroLite.exe");

        var staged = Directory.GetFiles(staging, "*", SearchOption.AllDirectories)
            .Select(file => (Source: file, Relative: Path.GetRelativePath(staging, file)))
            .ToList();

        if (!staged.Any(f => string.Equals(f.Relative, "NitroLite.exe", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The staging folder does not contain NitroLite.exe.");

        // Phase 1 — prepare. Nothing in the install folder is touched yet.
        var prepared = new List<(string Pending, string Destination)>();
        try
        {
            foreach (var (source, relative) in staged)
            {
                var destination = Path.Combine(installDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var pending = destination + ".new";
                TryDelete(pending);
                File.Copy(source, pending, overwrite: true);
                prepared.Add((pending, destination));
            }
        }
        catch
        {
            foreach (var (pending, _) in prepared) TryDelete(pending);
            throw;
        }

        // Phase 2 — commit. Each pair is recorded before the second move, so a failure between the two moves is
        // still rolled back.
        var committed = new List<(string Destination, string Backup)>();
        try
        {
            foreach (var (pending, destination) in prepared)
            {
                var backup = destination + ".old";
                // A stale backup from an earlier run is in the way. The executable's may still be the image this
                // process runs from, so it is the one case where a failed delete is tolerated.
                if (File.Exists(backup))
                {
                    if (string.Equals(destination, target, StringComparison.OrdinalIgnoreCase)) TryDelete(backup);
                    else File.Delete(backup);
                }

                if (File.Exists(destination)) File.Move(destination, backup);
                committed.Add((destination, backup));
                File.Move(pending, destination);
            }
        }
        catch
        {
            foreach (var (destination, backup) in Enumerable.Reverse(committed))
            {
                try
                {
                    if (File.Exists(destination)) File.Delete(destination);
                    if (File.Exists(backup)) File.Move(backup, destination);
                }
                catch (Exception ex)
                {
                    // A rollback that fails leaves the install folder in the state this whole method exists to
                    // prevent, so it has to be in the log even though the original exception is what the user sees.
                    AppLog.Error("Güncelleme geri alınamadı", $"{destination}: {ex.Message}");
                }
            }
            foreach (var (pending, _) in prepared) TryDelete(pending);
            throw;
        }

        // Phase 3 — clean up. The executable's backup stays until the next start.
        foreach (var (destination, backup) in committed)
        {
            if (string.Equals(destination, target, StringComparison.OrdinalIgnoreCase)) continue;
            TryDelete(backup);
        }

        return target;
    }

    /// <summary>Deletes a file if it is there. Returns true only when something was actually removed.</summary>
    private static bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Deletes the <c>.old</c> and <c>.new</c> files an update leaves behind. Called once at startup: by then the
    /// process that owned the executable has exited, so the delete succeeds and the install folder does not keep a
    /// stale copy. Only the package's own files are swept, so a user file that happens to end in <c>.old</c> is
    /// never touched.
    /// </summary>
    public static void CleanUpPreviousVersion() => SweepLeftovers(AppContext.BaseDirectory);

    /// <summary>
    /// Repairs an install folder an interrupted update left behind, then sweeps what is left over.
    /// </summary>
    /// <remarks>
    /// The commit phase moves each destination aside to <c>.old</c> and then moves its <c>.new</c> into place. A
    /// crash, a power cut or a forced shutdown between those two moves leaves the destination missing while the
    /// only good copy sits under <c>.old</c>; the old sweep then deleted that copy and the file was gone for good.
    /// This pass puts it back instead, and only sweeps once nothing is left half-restored.
    /// <para>
    /// A destination that exists but is empty counts as missing: a truncated copy is what a power cut during the
    /// copy leaves, and running from it is no better than running from nothing.
    /// </para>
    /// </remarks>
    public static UpdateRecoveryReport RecoverInterruptedUpdate() => RecoverInterruptedUpdate(AppContext.BaseDirectory);

    /// <inheritdoc cref="RecoverInterruptedUpdate()"/>
    public static UpdateRecoveryReport RecoverInterruptedUpdate(string installDirectory)
    {
        var restored = new List<string>();
        var failed = new List<string>();

        foreach (var relative in PackageFiles)
        {
            var path = Path.Combine(installDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            if (IsUsable(path)) continue;

            var backup = path + ".old";
            var pending = path + ".new";

            // The backup is the last build that worked, so it wins over the half-copied new file.
            if (IsUsable(backup) && TryMove(backup, path)) { restored.Add(path); continue; }
            if (IsUsable(pending) && TryMove(pending, path)) { restored.Add(path); continue; }

            // Nothing usable to restore from. An empty leftover is garbage and the sweep below takes it; a usable
            // one that could not be moved is the only copy left, so it stays and the file is reported.
            if (IsUsable(backup) || IsUsable(pending)) failed.Add(path);
        }

        // Sweeping after a failed restore would delete the copy this method exists to preserve.
        var removed = failed.Count == 0 ? SweepLeftovers(installDirectory) : new List<string>();

        if (restored.Count > 0)
            AppLog.Warn("Yarım kalan güncelleme kurtarıldı", string.Join(", ", restored));
        if (failed.Count > 0)
            AppLog.Error("Yarım kalan güncelleme kurtarılamadı", string.Join(", ", failed));

        return new UpdateRecoveryReport(restored, removed, failed);
    }

    /// <summary>Deletes every <c>.old</c> and <c>.new</c> the package owns and reports the ones that went.</summary>
    private static List<string> SweepLeftovers(string installDirectory)
    {
        var removed = new List<string>();
        foreach (var relative in PackageFiles)
        {
            var path = Path.Combine(installDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            foreach (var suffix in new[] { ".old", ".new" })
            {
                var leftover = path + suffix;
                if (TryDelete(leftover)) removed.Add(leftover);
            }
        }
        return removed;
    }

    /// <summary>True when the file exists and holds something; a zero-byte file is a failed copy, not a file.</summary>
    private static bool IsUsable(string path)
    {
        try { return File.Exists(path) && new FileInfo(path).Length > 0; }
        catch { return false; }
    }

    /// <summary>Moves a backup into place, reporting rather than throwing so one bad file cannot stop the pass.</summary>
    private static bool TryMove(string source, string destination)
    {
        try
        {
            File.Move(source, destination, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Güncelleme dosyası geri konamadı", $"{source}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Starts the freshly installed executable. The caller shuts this process down afterwards.</summary>
    public static void StartInstalled(string executablePath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory
        });
    }

    /// <summary>Removes a staging folder and a downloaded package, ignoring failures.</summary>
    public static void CleanUp(params string?[] paths)
    {
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                else if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }
    }

    /// <summary>
    /// The prefilled GitHub issue body: version, Windows build and laptop model, nothing that identifies the user.
    /// The labels follow the UI language, because the person filing the report is the one reading this page; the
    /// language line tells the maintainer which language the report arrived in.
    /// </summary>
    public static string BuildIssueBody(string version, string windows, string model, string language)
    {
        string happened = Loc.T("Ne oldu");
        string expected = Loc.T("Ne bekliyordun");
        string steps = Loc.T("Nasıl tekrar üretilir");
        string laptop = Loc.T("Dizüstü");
        string languageLabel = Loc.T("Dil");

        var lines = new List<string>
        {
            $"**{happened}**",
            "",
            "",
            $"**{expected}**",
            "",
            "",
            $"**{steps}**",
            "1. ",
            "",
            "---",
            $"NitroLite: {version}",
            $"Windows: {windows}",
            $"{laptop}: {model}",
            $"{languageLabel}: {language}"
        };
        return string.Join("\n", lines);
    }

    /// <summary>The issue URL with the title and body filled in, ready for the browser.</summary>
    public static string BuildIssueUrl(string title, string body) =>
        $"{NewIssueUrl}?title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(body)}";

    /// <summary>
    /// Supplies the machine model for the prefilled issue. Set once by the platform at startup: Windows reads it from
    /// <c>Win32_ComputerSystem</c>, Linux from the DMI files, and the shared code below never has to know which.
    /// </summary>
    /// <remarks>
    /// A delegate rather than a direct read because this file is compiled into the platform-independent assembly, and a
    /// <c>System.Management</c> call there would drag the WMI stack into a Linux process. Null is a legitimate state —
    /// a test that never sets it gets the localised "unknown" — so the body never carries an empty field.
    /// </remarks>
    public static Func<string?>? MachineModelProvider { get; set; }

    /// <summary>
    /// The laptop model, for the prefilled issue. A localised "unknown" when the platform cannot answer, so the body
    /// never carries an empty field.
    /// </summary>
    public static string GetMachineModel()
    {
        try
        {
            var text = MachineModelProvider?.Invoke()?.Trim();
            if (!string.IsNullOrEmpty(text)) return text;
        }
        catch
        {
            // A provider that throws is a machine whose model could not be read, which is what the fallback says.
        }

        return Loc.T("Bilinmiyor");
    }
}
