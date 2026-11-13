using System;
using System.IO;
using System.Linq;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Finds the release package that <c>scripts/publish-release.ps1</c> writes, so a test can check the updater against
/// the real archive instead of a hand-built one. Skips when no package has been built.
/// </summary>
internal static class ReleasePackage
{
    public static string? Find()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NitroLite.sln")))
            {
                var artifacts = Path.Combine(directory.FullName, "artifacts");
                if (!Directory.Exists(artifacts)) return null;
                return Directory.GetFiles(artifacts, "NitroLite-v*-win-x64.zip")
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
            }

            directory = directory.Parent;
        }

        return null;
    }
}

/// <summary>Runs only when a release package exists; otherwise the test is skipped with a reason.</summary>
public sealed class ReleasePackageFactAttribute : FactAttribute
{
    public ReleasePackageFactAttribute()
    {
        if (ReleasePackage.Find() == null)
            Skip = "No release package in artifacts/; run scripts/publish-release.ps1 first.";
    }
}
