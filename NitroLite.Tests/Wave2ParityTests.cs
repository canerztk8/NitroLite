using System.IO;
using System.Text;
using NitroLite.Core;
using NitroLite.Desktop;
using NitroLite.Desktop.Converters;
using NitroLite.Desktop.Themes;
using NitroLite.Desktop.ViewModels;
using NitroLite.Windows;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The second wave: the Nitro key's three window shapes, the settings page without its duplicate game editor, and the
/// six theme preview cards.
/// </summary>
/// <remarks>
/// <para>
/// The redirect tests are the ones that matter most. On a model whose button the Acer service stack owns, the press
/// produces no event NitroLite can subscribe to — the only evidence is a window coming to the front, and the window is
/// not always NitroSense's own. A packaged build puts it under <c>ApplicationFrameHost</c>, and a machine whose driver
/// is unreachable gets a <c>#32770</c> permission dialog instead. A watcher that only looked at the process name saw
/// none of those and reported nothing, which is exactly the "the key does nothing" symptom.
/// </para>
/// <para>
/// The page tests read the markup rather than a rendered window: the duplicate editor was a second copy of controls
/// that already exist on the modes page, and the only way it can come back is someone pasting it in again.
/// </para>
/// </remarks>
public class Wave2ParityTests
{
    // =============================================================================================================
    // 1. The Nitro key: three shapes of window, one press.
    // =============================================================================================================

    private static ForegroundWindowInfo Window(
        int processId,
        string processName,
        string className = "",
        string title = "",
        string content = "",
        IntPtr handle = default) =>
        new(processId, processName, className, title, content, handle);

    [Fact]
    public void NitroSenseRedirect_CatchesApplicationFrameHostAndErrorDialog()
    {
        // 1. The app's own process, and any process the Acer stack runs under. The prefix is what catches a build
        //    that renames the executable without the list having to learn the new name.
        Assert.Equal(NitroSenseWindowKind.Process,
            NitroSenseWatcher.Classify(Window(100, "NitroSense"), processIsNitroSense: false));
        Assert.Equal(NitroSenseWindowKind.Process,
            NitroSenseWatcher.Classify(Window(101, "NitroSenseLauncher"), processIsNitroSense: false));
        Assert.Equal(NitroSenseWindowKind.Process,
            NitroSenseWatcher.Classify(Window(102, "SenseOverlay"), processIsNitroSense: true));

        // 2. A packaged build. The window belongs to ApplicationFrameHost, so the process name says nothing and the
        //    title is the only evidence there is.
        Assert.Equal(NitroSenseWindowKind.ApplicationFrameHost,
            NitroSenseWatcher.Classify(
                Window(200, "ApplicationFrameHost", title: "NitroSense"), processIsNitroSense: false));
        Assert.Equal(NitroSenseWindowKind.ApplicationFrameHost,
            NitroSenseWatcher.Classify(
                Window(201, "applicationframehost", title: "NitroSense for Notebook"),
                processIsNitroSense: false));

        // A packaged app that is not NitroSense is not the key.
        Assert.Equal(NitroSenseWindowKind.None,
            NitroSenseWatcher.Classify(
                Window(202, "ApplicationFrameHost", title: "Calculator"), processIsNitroSense: false));

        // 3. The permission dialog. #32770 is the class of every dialog on Windows, so the text is what narrows it.
        Assert.Equal(NitroSenseWindowKind.ErrorDialog,
            NitroSenseWatcher.Classify(
                Window(300, "NitroSense", className: "#32770", title: "NitroSenseforNotebook"),
                processIsNitroSense: false));

        // The sentence that identifies the prompt is usually in a child label, not in the title.
        Assert.Equal(NitroSenseWindowKind.ErrorDialog,
            NitroSenseWatcher.Classify(
                Window(301, "NitroSense", className: "#32770", title: "Hata",
                    content: "NitroSense sürücüsüne erişilemedi."),
                processIsNitroSense: false));

        // A dialog that is not Acer's is not the key.
        Assert.Equal(NitroSenseWindowKind.None,
            NitroSenseWatcher.Classify(
                Window(302, "explorer", className: "#32770", title: "Klasör erişimi reddedildi"),
                processIsNitroSense: false));

        // Nothing in front is nothing to do.
        Assert.Equal(NitroSenseWindowKind.None,
            NitroSenseWatcher.Classify(ForegroundWindowInfo.None, processIsNitroSense: false));
    }

    [Fact]
    public void NitroSenseRedirect_ReportsThePackagedWindowAndRaisesTheShell()
    {
        var window = Window(200, "ApplicationFrameHost", title: "NitroSense");
        using var watcher = new NitroSenseWatcher(
            foregroundWindow: () => window,
            isNitroSense: _ => false);

        int activations = 0;
        watcher.NitroSenseActivated += (_, _) => activations++;

        watcher.Poll();
        watcher.Poll();

        // The same window staying in front is one press, not two.
        Assert.Equal(1, activations);
    }

    [Fact]
    public void NitroSenseRedirect_ClosesThePermissionDialogWithOneMessage()
    {
        var dialog = Window(300, "NitroSense", className: "#32770",
            title: "NitroSenseforNotebook", handle: new IntPtr(0x1234));

        var closed = new List<IntPtr>();
        using var watcher = new NitroSenseWatcher(
            foregroundWindow: () => dialog,
            isNitroSense: _ => false,
            closeWindow: closed.Add);

        int activations = 0;
        watcher.NitroSenseActivated += (_, _) => activations++;

        watcher.Poll();

        // The dialog is a window, not the app: one WM_CLOSE is the whole fix, and the press still brings the shell
        // forward because the key was pressed either way.
        Assert.Equal(new[] { new IntPtr(0x1234) }, closed);
        Assert.Equal(1, activations);

        // A second poll of the same window is not a second press, so the dialog is not asked to close twice.
        watcher.Poll();
        Assert.Single(closed);
    }

    [Fact]
    public void NitroSenseRedirect_LeavesAnUnrelatedWindowAlone()
    {
        var closed = new List<IntPtr>();
        using var watcher = new NitroSenseWatcher(
            foregroundWindow: () => Window(400, "explorer", className: "#32770", title: "Klasör",
                handle: new IntPtr(9)),
            isNitroSense: _ => false,
            closeWindow: closed.Add);

        int activations = 0;
        watcher.NitroSenseActivated += (_, _) => activations++;

        watcher.Poll();

        Assert.Empty(closed);
        Assert.Equal(0, activations);
    }

    [Fact]
    public void NitroSenseRedirect_KnowsTheWindowClassesItLooksFor()
    {
        Assert.True(NitroSenseWatcher.IsApplicationFrameHost("ApplicationFrameHost"));
        Assert.True(NitroSenseWatcher.IsApplicationFrameHost("applicationframehost"));
        Assert.False(NitroSenseWatcher.IsApplicationFrameHost("NitroSense"));

        Assert.True(NitroSenseWatcher.IsDialogClass("#32770"));
        Assert.False(NitroSenseWatcher.IsDialogClass("#32769"));

        Assert.True(NitroSenseWatcher.IsNitroSenseProcessName("NitroSense"));
        Assert.True(NitroSenseWatcher.IsNitroSenseProcessName("NitroSenseLauncher"));
        Assert.True(NitroSenseWatcher.IsNitroSenseProcessName("NitroSenseSomethingNew"));
        Assert.False(NitroSenseWatcher.IsNitroSenseProcessName("SenseOverlay"));
        Assert.False(NitroSenseWatcher.IsNitroSenseProcessName(""));

        Assert.True(NitroSenseWatcher.MentionsNitroSense("NitroSenseforNotebook"));
        Assert.True(NitroSenseWatcher.MentionsNitroSense("nitrosense"));
        Assert.False(NitroSenseWatcher.MentionsNitroSense("Acer Care Center"));
        Assert.False(NitroSenseWatcher.MentionsNitroSense(null));
    }

    [Fact]
    public void NitroSenseRedirect_IsOnByDefaultAndSurvivesAReload()
    {
        // The key has always taken the button away from NitroSense here, so the default has to keep that behaviour:
        // a settings file written before the switch existed must not silently hand the key back.
        Assert.True(new AppSettings().RedirectNitroSenseEnabled);

        var path = TempSettingsPath();
        try
        {
            using (var vm = new MainViewModel(new AppSettings(), null, null, path))
            {
                Assert.True(vm.RedirectNitroSenseEnabled);

                vm.RedirectNitroSenseEnabled = false;
            }

            Assert.False(SettingsManager.LoadSettingsWithFallback(path).RedirectNitroSenseEnabled);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void NitroSenseRedirect_IsOnlyOfferedWhileTheKeyIsOn()
    {
        using var vm = new MainViewModel(new AppSettings(), null, null, TempSettingsPath());

        Assert.True(vm.CanRedirectNitroSense);

        vm.NitroKeyEnabled = false;
        Assert.False(vm.CanRedirectNitroSense);

        vm.NitroKeyEnabled = true;
        Assert.True(vm.CanRedirectNitroSense);
    }

    [Fact]
    public void NitroSenseLauncherState_SaysWhatTheKeyWillDo()
    {
        using var vm = new MainViewModel(new AppSettings(), null, null, TempSettingsPath());

        // No listener was injected, so this machine has no key: the line says so rather than claiming a redirect.
        Assert.Equal(vm.Labels.NitroSenseLauncherNoKey, vm.NitroSenseLauncherStateText);
    }

    // =============================================================================================================
    // 2. The settings page: one game editor, and the Nitro key card still there.
    // =============================================================================================================

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

    private static string SettingsViewMarkup() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "NitroLite.Desktop", "Views", "SettingsView.axaml"), Encoding.UTF8);

    [Fact]
    public void NavTools_RemovesDuplicateGameEditor_PreservesNitroKeyCard()
    {
        var markup = SettingsViewMarkup();

        // The duplicate editor is gone. Every one of these was a control the modes page already owns, and a second
        // copy meant two places to keep in step and two places for a user to look.
        Assert.DoesNotContain("AutoGameModeOn", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("AddGameProfileCommand", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("DeleteSelectedGameCommand", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("NewGameExe", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("GameProfileRow", markup, StringComparison.Ordinal);

        // The Nitro key card is not the duplicate and must stay: it is the only place the two switches live.
        Assert.Contains("Labels.NitroKey", markup, StringComparison.Ordinal);
        Assert.Contains("NitroKeyEnabled", markup, StringComparison.Ordinal);
        Assert.Contains("RedirectNitroSenseEnabled", markup, StringComparison.Ordinal);
        Assert.Contains("CanRedirectNitroSense", markup, StringComparison.Ordinal);

        // The two rows the audit found missing.
        Assert.Contains("UseDirectWmiBackend", markup, StringComparison.Ordinal);
        Assert.Contains("NitroSenseLauncherStateText", markup, StringComparison.Ordinal);

        // The theme picker is the card grid, not a combo box.
        Assert.Contains("ThemeCards", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedItem=\"{Binding SelectedTheme}\"", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void NavTools_PutsLanguageAndThemeAtTheBottom()
    {
        var markup = SettingsViewMarkup();

        // The WPF page ended with "NitroLite Ayarları"; the Avalonia one opened with it. Language and theme are the
        // settings a user changes once, so they belong under the tools the page exists for.
        var settings = markup.IndexOf("Labels.NitroLiteSettings", StringComparison.Ordinal);
        var device = markup.IndexOf("Labels.DeviceCapabilities", StringComparison.Ordinal);
        var services = markup.IndexOf("Labels.AcerServices", StringComparison.Ordinal);

        Assert.True(settings > 0, "the NitroLite Ayarları card is missing");
        Assert.True(device > 0, "the device card is missing");
        Assert.True(settings > device, "language and theme are still above the device card");
        Assert.True(settings > services, "language and theme are still above the services card");

        // The old top-of-page appearance card is gone rather than duplicated.
        Assert.DoesNotContain("Labels.Appearance", markup, StringComparison.Ordinal);
    }

    // =============================================================================================================
    // 3. The theme cards: six palettes, one click.
    // =============================================================================================================

    [Fact]
    public void ThemeCards_RenderAllSixPalettesAndSwitchTheme()
    {
        var applier = new RecordingThemeApplier();
        var settings = new AppSettings();
        var path = TempSettingsPath();

        try
        {
            using var vm = new MainViewModel(settings, null, null, path, themeApplier: applier);

            // One card per palette, built from the catalogue rather than written into the markup.
            Assert.Equal(ThemeCatalog.All.Count, vm.ThemeCards.Count);
            Assert.Equal(ThemeCatalog.All, vm.ThemeCards.Select(c => c.Theme));

            // The six concrete palettes each carry a screenshot and five swatches. "System" is a preference, not a
            // palette: it shows whatever it resolves to, so it is not counted here.
            var concrete = vm.ThemeCards.Where(c => c.Theme != AppTheme.System).ToList();
            Assert.Equal(6, concrete.Count);

            foreach (var card in concrete)
            {
                Assert.Equal(5, card.Swatches.Count);
                Assert.All(card.Swatches, hex => Assert.Matches("^#[0-9A-F]{6}$", hex));
                Assert.False(string.IsNullOrWhiteSpace(card.Label));
            }

            // Every palette has its own screenshot on disk, and the warm-cream one is the file called "light".
            foreach (var card in concrete)
            {
                var asset = Path.Combine(RepoRoot(), "NitroLite.Desktop", "Assets", "themes",
                    ThemePreviewConverter.AssetName(card.Theme) + ".jpg");
                Assert.True(File.Exists(asset), $"missing preview for {card.Theme}: {asset}");
            }

            Assert.Equal("light", ThemePreviewConverter.AssetName(AppTheme.WarmCream));
            Assert.Equal("midnightoled", ThemePreviewConverter.AssetName(AppTheme.MidnightOled));

            // The stored preference is what paints a card as chosen.
            Assert.True(vm.ThemeCards.Single(c => c.Theme == AppTheme.WarmCream).IsSelected);

            // The constructor paints the stored palette once, so the click is measured from there rather than from an
            // empty list.
            Assert.Equal(new[] { AppTheme.WarmCream }, applier.Applied);

            // Clicking a card applies the palette and writes the choice, and the accent frame moves with it.
            var midnight = vm.ThemeCards.Single(c => c.Theme == AppTheme.MidnightOled);
            midnight.SelectCommand.Execute(null);

            Assert.Equal(new[] { AppTheme.WarmCream, AppTheme.MidnightOled }, applier.Applied);
            Assert.Equal(AppTheme.MidnightOled, settings.Theme);
            Assert.True(midnight.IsSelected);
            Assert.False(vm.ThemeCards.Single(c => c.Theme == AppTheme.WarmCream).IsSelected);

            // Clicking the card that is already chosen is not an action: nothing is applied and nothing is written.
            midnight.SelectCommand.Execute(null);
            Assert.Equal(2, applier.Applied.Count);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void ThemeCards_AreRebuiltInTheNewLanguage()
    {
        using var vm = new MainViewModel(new AppSettings(), null, null, TempSettingsPath());

        var before = vm.ThemeCards.Single(c => c.Theme == AppTheme.Dark).Label;

        try
        {
            Loc.SetLanguage("en");
            vm.SelectedLanguage = Loc.Languages.Single(l => l.Code == "en");

            var after = vm.ThemeCards.Single(c => c.Theme == AppTheme.Dark).Label;

            Assert.NotEqual(before, after);
            Assert.Equal(Loc.T(ThemeCatalog.DisplayNameKey(AppTheme.Dark)), after);

            // The selection survives the rebuild: changing the language is not a theme change.
            Assert.True(vm.ThemeCards.Single(c => c.Theme == AppTheme.WarmCream).IsSelected);
        }
        finally
        {
            Loc.SetLanguage(Loc.SourceLanguage);
        }
    }

    [Fact]
    public void ThemeCards_EveryPaletteCarriesFiveDistinctSwatches()
    {
        foreach (var theme in ThemeCatalog.All)
        {
            var swatches = ThemePalette.SwatchHexes(theme);

            Assert.Equal(5, swatches.Count);
            Assert.All(swatches, hex => Assert.Matches("^#[0-9A-F]{6}$", hex));

            // Page, card, rule, ink and accent: a palette whose card and page were the same colour would show a
            // swatch strip that reads as four boxes, which is the one thing the strip exists to avoid.
            Assert.NotEqual(swatches[0], swatches[1]);
        }
    }

    [Fact]
    public void ThemeCards_AreRegisteredAsAnAvaloniaResource()
    {
        // A card whose screenshot is not in the assembly shows an empty frame on every machine, and nothing throws.
        var csproj = File.ReadAllText(
            Path.Combine(RepoRoot(), "NitroLite.Desktop", "NitroLite.Desktop.csproj"), Encoding.UTF8);

        Assert.Contains("Assets\\themes\\*.jpg", csproj, StringComparison.Ordinal);
        Assert.Contains("AvaloniaResource", csproj, StringComparison.Ordinal);
    }

    // =============================================================================================================
    // 4. The direct-WMI rollback switch.
    // =============================================================================================================

    [Fact]
    public void UseDirectWmiBackend_IsOnByDefaultAndSurvivesAReload()
    {
        Assert.True(new AppSettings().UseDirectWmiBackend);

        var path = TempSettingsPath();
        try
        {
            using (var vm = new MainViewModel(new AppSettings(), null, null, path))
            {
                Assert.True(vm.UseDirectWmiBackend);

                vm.UseDirectWmiBackend = false;
            }

            Assert.False(SettingsManager.LoadSettingsWithFallback(path).UseDirectWmiBackend);
        }
        finally
        {
            Delete(path);
        }
    }

    // =============================================================================================================
    // Fixtures.
    // =============================================================================================================

    private static string TempSettingsPath() =>
        Path.Combine(Path.GetTempPath(), "nitrolite-wave2-" + Guid.NewGuid().ToString("N"), "settings.json");

    private static void Delete(string path)
    {
        var folder = Path.GetDirectoryName(path);
        if (folder != null && Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    /// <summary>Records what the view model asked the window to paint, without needing a window.</summary>
    private sealed class RecordingThemeApplier : IThemeApplier
    {
        public List<AppTheme> Applied { get; } = new();

        public void Apply(AppTheme theme) => Applied.Add(theme);
    }
}
