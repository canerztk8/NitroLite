using System.Collections;
using System.IO;
using System.Resources;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Guards the theme cards' preview screenshots: every concrete palette has one, it is embedded in the assembly, and
/// it is a real capture rather than a placeholder.
/// </summary>
/// <remarks>
/// <c>MainWindow.LoadThemePreview</c> builds the pack URI from <c>AppTheme.ToString().ToLowerInvariant()</c> and falls
/// back to the old swatch strip when the image will not load. That fallback is deliberate — a card is better than an
/// empty frame — but it also means a renamed enum member or a dropped asset fails silently, on the one machine that
/// opens the settings page. These tests turn that into a red build.
/// </remarks>
public class ThemePreviewAssetTests
{
    /// <summary>The palettes that have a picture of their own. <see cref="AppTheme.System"/> shows a resolved one.</summary>
    private static readonly AppTheme[] Palettes =
    {
        AppTheme.Light,
        AppTheme.Dark,
        AppTheme.MidnightOled,
        AppTheme.SlateSteel,
        AppTheme.AmberConsole,
        AppTheme.NordFrost
    };

    public static IEnumerable<object[]> ConcretePalettes => Palettes.Select(theme => new object[] { theme });

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "NitroLite.sln")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>The name the card looks up, derived the same way the code derives it.</summary>
    private static string AssetName(AppTheme theme) => theme.ToString().ToLowerInvariant();

    [Theory]
    [MemberData(nameof(ConcretePalettes))]
    public void EveryPalette_HasAPreviewScreenshotInTheRepository(AppTheme theme)
    {
        var path = Path.Combine(RepoRoot(), "NitroLite", "Assets", "themes", AssetName(theme) + ".jpg");

        Assert.True(File.Exists(path), $"Missing theme preview: {path}. Run scripts/theme-previews.ps1.");

        // A capture that failed halfway leaves a file behind; a real 400px screenshot is ~17 KB.
        var length = new FileInfo(path).Length;
        Assert.True(length > 5_000, $"{path} is only {length} bytes, which is not a usable screenshot.");
    }

    [Theory]
    [MemberData(nameof(ConcretePalettes))]
    public void EveryPalette_PreviewIsEmbeddedUnderThePackUriTheCardAsksFor(AppTheme theme)
    {
        // The previews are WPF Resource items, so they stay in the WPF assembly even though AppTheme itself moved
        // to NitroLite.Core. Asking the enum's assembly would look in the wrong place and find nothing.
        using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("NitroLite.g.resources");
        Assert.NotNull(stream);

        using var reader = new ResourceReader(stream!);
        var keys = reader.Cast<DictionaryEntry>()
            .Select(entry => (string)entry.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The pack URI "pack://application:,,,/Assets/themes/x.jpg" resolves to this key. A file on disk that the
        // csproj does not embed is exactly the silent failure this test exists for.
        Assert.Contains($"assets/themes/{AssetName(theme)}.jpg", keys);
    }

    [Fact]
    public void EveryConcretePalette_IsCoveredByTheseTests()
    {
        // Adding a palette without a screenshot would otherwise pass every test above by simply not being listed.
        var covered = Palettes.ToHashSet();
        var expected = ThemeCatalog.All.Where(theme => theme != AppTheme.System).ToHashSet();

        Assert.Equal(expected.OrderBy(t => t), covered.OrderBy(t => t));
    }
}
