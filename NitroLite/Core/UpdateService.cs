using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NitroLite.Core;

/// <summary>A newer release found on GitHub, with everything needed to fetch and verify it.</summary>
public sealed record UpdateInfo(
    Version Version,
    string Tag,
    string AssetName,
    string DownloadUrl,
    string? Sha256,
    long SizeBytes)
{
    public string DisplayVersion => $"v{Version.Major}.{Version.Minor}.{Version.Build}";
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
    /// refused, not extracted. The helper lives in its own folder because the app looks for it there.
    /// </summary>
    public static readonly string[] PackageFiles =
    {
        "NitroLite.exe",
        "StandbyPurge/NitroLite.StandbyPurge.exe",
        "StandbyPurge/native-aot.marker",
        "LICENSE"
    };

    public static string LatestReleaseApi => $"https://api.github.com/repos/{Owner}/{Repository}/releases/latest";
    public static string ReleasePageUrl => $"https://github.com/{Owner}/{Repository}/releases/latest";
    public static string NewIssueUrl => $"https://github.com/{Owner}/{Repository}/issues/new";

    /// <summary>
    /// True when NitroLite runs from the published single-file package. A development build has a real assembly
    /// path, and updating it would replace a build the developer is working on.
    /// </summary>
    public static bool IsPackaged => string.IsNullOrEmpty(typeof(UpdateService).Assembly.Location);

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

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // GitHub rejects an API request without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NitroLite", CurrentVersionText.TrimStart('v')));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    /// <summary>
    /// Asks GitHub for the newest release. Returns null when this build is already current, when the release has no
    /// package for this platform, or when the answer cannot be understood. Network failures throw, so the caller can
    /// tell "no update" from "could not ask".
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var response = await Http.GetAsync(LatestReleaseApi, cancellationToken).ConfigureAwait(false);
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

        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object) continue;
            var name = asset.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            if (name == null || !name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase)) continue;

            var url = asset.TryGetProperty("browser_download_url", out var u) && u.ValueKind == JsonValueKind.String
                ? u.GetString() : null;
            if (string.IsNullOrWhiteSpace(url)) continue;

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

            return new UpdateInfo(version, tag, name, url!, sha, size);
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
    public static async Task<string> DownloadAsync(
        UpdateInfo info, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var target = Path.Combine(Path.GetTempPath(), $"NitroLite-update-{Guid.NewGuid():N}.zip");
        try
        {
            using var response = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? info.SizeBytes;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long copied = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    copied += read;
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
    public static string ExtractToStaging(string zipPath)
    {
        var staging = Path.Combine(Path.GetTempPath(), $"NitroLite-stage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        var stagingRoot = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(zipPath);

        // The release script archives the staging folder itself, so entries sit under "NitroLite/". Find the folder
        // that holds NitroLite.exe and use it as the root; a flat archive has an empty prefix.
        var exeEntry = archive.Entries.FirstOrDefault(e =>
            string.Equals(Path.GetFileName(e.FullName), "NitroLite.exe", StringComparison.OrdinalIgnoreCase));
        if (exeEntry == null) throw new InvalidDataException("NitroLite.exe is not in the package.");
        var prefix = exeEntry.FullName[..^"NitroLite.exe".Length];

        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || entry.FullName.EndsWith("\\", StringComparison.Ordinal))
                continue;
            if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal)) continue;

            var relative = entry.FullName[prefix.Length..].Replace('\\', '/');
            if (relative.Length == 0) continue;
            if (!PackageFiles.Contains(relative, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException($"Unexpected file in the package: {relative}");

            var destination = Path.GetFullPath(Path.Combine(staging, relative));
            if (!destination.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Package entry escapes the staging folder: {relative}");

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }

        foreach (var required in new[] { "NitroLite.exe", "StandbyPurge/NitroLite.StandbyPurge.exe" })
        {
            if (!File.Exists(Path.Combine(staging, required)))
                throw new InvalidDataException($"The package does not contain {required}.");
        }

        return staging;
    }

    /// <summary>
    /// Copies the staged files over the install folder, keeping the package's folder layout. The running executable
    /// cannot be overwritten, so it is renamed to <c>.old</c> first and renamed back if the copy fails. Returns the
    /// path of the new executable.
    /// </summary>
    public static string InstallFromStaging(string staging, string installDirectory)
    {
        var target = Path.Combine(installDirectory, "NitroLite.exe");
        var backup = target + ".old";

        // Everything except the executable can be replaced in place. Sub-folders (the StandbyPurge helper) are
        // recreated so the app finds them where it expects them.
        foreach (var file in Directory.GetFiles(staging, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(staging, file);
            if (string.Equals(relative, "NitroLite.exe", StringComparison.OrdinalIgnoreCase)) continue;

            var destination = Path.Combine(installDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }

        if (File.Exists(backup))
        {
            try { File.Delete(backup); } catch { }
        }

        File.Move(target, backup);
        try
        {
            File.Copy(Path.Combine(staging, "NitroLite.exe"), target, overwrite: false);
        }
        catch
        {
            // Put the old executable back so the user is never left without a working NitroLite.
            try
            {
                if (File.Exists(target)) File.Delete(target);
                File.Move(backup, target);
            }
            catch { }
            throw;
        }

        return target;
    }

    /// <summary>
    /// Deletes the <c>NitroLite.exe.old</c> an update leaves behind. Called once at startup: by then the process
    /// that owned the file has exited, so the delete succeeds and the install folder does not keep a stale copy.
    /// </summary>
    public static void CleanUpPreviousVersion()
    {
        try
        {
            var backup = Path.Combine(AppContext.BaseDirectory, "NitroLite.exe.old");
            if (File.Exists(backup)) File.Delete(backup);
        }
        catch { }
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
    /// </summary>
    public static string BuildIssueBody(string version, string windows, string model, string language)
    {
        var lines = new List<string>
        {
            "**What happened**",
            "",
            "",
            "**What you expected**",
            "",
            "",
            "**Steps to reproduce**",
            "1. ",
            "",
            "---",
            $"NitroLite: {version}",
            $"Windows: {windows}",
            $"Laptop: {model}",
            $"Language: {language}"
        };
        return string.Join("\n", lines);
    }

    /// <summary>The issue URL with the title and body filled in, ready for the browser.</summary>
    public static string BuildIssueUrl(string title, string body) =>
        $"{NewIssueUrl}?title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(body)}";

    /// <summary>
    /// The laptop model Windows reports, for the prefilled issue. "unknown" when WMI cannot answer, so the body
    /// never carries an empty field.
    /// </summary>
    public static string GetMachineModel()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Manufacturer, Model FROM Win32_ComputerSystem");
            foreach (System.Management.ManagementObject item in searcher.Get())
            {
                var manufacturer = (item["Manufacturer"]?.ToString() ?? "").Trim();
                var model = (item["Model"]?.ToString() ?? "").Trim();
                var text = string.Join(" ", new[] { manufacturer, model }.Where(part => part.Length > 0));
                if (text.Length > 0) return text;
            }
        }
        catch { }
        return "unknown";
    }
}
