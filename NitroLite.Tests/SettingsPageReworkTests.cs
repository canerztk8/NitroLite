using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NitroLite.Core;
using NitroLite.Desktop.ViewModels;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Guards the reworked services page: the two cards that fold, the merged Acer card, the startup rows that lost their
/// prose, and the support card that replaced the notes block.
/// </summary>
/// <remarks>
/// The page is mostly markup, so most of these are markup assertions. The three that are not — the fold defaults, the
/// tray coupling and the two tooltips — are view-model behaviour, and they are the ones a future edit is most likely to
/// break without noticing.
/// </remarks>
public class SettingsPageReworkTests
{
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

    private static string Markup() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "NitroLite.Desktop", "Views", "SettingsView.axaml"));

    private static string TempSettingsPath() =>
        Path.Combine(Path.GetTempPath(), $"nitrolite_settings_{Guid.NewGuid():N}.json");

    private static MainViewModel Create(
        IAutoStartService? autoStart = null,
        IRunAtStartupService? runAtStartup = null,
        Action<string>? urlOpener = null)
    {
        var path = TempSettingsPath();
        try
        {
            return new MainViewModel(
                new AppSettings(),
                hardware: null,
                platform: null,
                path,
                autoStart: autoStart,
                runAtStartup: runAtStartup,
                urlOpener: urlOpener);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    // =============================================================================================================
    // 1. The two cards that fold.
    // =============================================================================================================

    [Fact]
    public void BothCards_StartClosed()
    {
        using var vm = Create();

        // The Acer card is the longest thing in the app and the startup card is three switches nobody touches twice.
        // A page that opens with both of them open is a page that opens as a wall.
        Assert.False(vm.AcerSectionExpanded);
        Assert.False(vm.StartupSectionExpanded);
    }

    [Fact]
    public void BothCards_RememberBeingOpened()
    {
        using var vm = Create();

        vm.AcerSectionExpanded = true;
        vm.StartupSectionExpanded = true;

        Assert.True(vm.AcerSectionExpanded);
        Assert.True(vm.StartupSectionExpanded);
    }

    [Fact]
    public void TheMarkup_BindsBothFoldsToTheirHeaders()
    {
        var markup = Markup();

        Assert.Contains("IsChecked=\"{Binding AcerSectionExpanded, Mode=TwoWay}\"", markup, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding AcerSectionExpanded}\"", markup, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding StartupSectionExpanded, Mode=TwoWay}\"", markup, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding StartupSectionExpanded}\"", markup, StringComparison.Ordinal);
    }

    // =============================================================================================================
    // 2. The merged Acer card.
    // =============================================================================================================

    [Fact]
    public void TheAcerCard_IsOneCardForTheServicesTheToolsAndTheKey()
    {
        var markup = Markup();

        // One heading, three captions under it. The three used to be three cards with three headings.
        Assert.Contains("Labels.AcerHardware", markup, StringComparison.Ordinal);
        Assert.Contains("Labels.AcerServices", markup, StringComparison.Ordinal);
        Assert.Contains("Labels.RepairToolsGroup", markup, StringComparison.Ordinal);
        Assert.Contains("Labels.NitroKey", markup, StringComparison.Ordinal);

        // The old separate headings are gone.
        Assert.DoesNotContain("Labels.RepairTools}", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Labels.ToolsPageSubtitle", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAcerCard_HidesOnlyWhenItHasNothingToShow()
    {
        using var vm = Create();

        // No services, no tools, no key: the card is out of the layout rather than drawn empty.
        Assert.False(vm.AcerHardwareAvailable);
        Assert.Equal(vm.ServicesAvailable || vm.RepairToolsAvailable || vm.NitroKeyAvailable, vm.AcerHardwareAvailable);
    }

    [Fact]
    public void TheAcerCard_KeepsBothExplanationsInOneBadge()
    {
        using var vm = Create();

        // Two tooltips became one when the cards merged. Both sentences are still there, in the reader's language.
        Assert.Contains(vm.Labels.AcerServicesHint, vm.AcerHardwareHint, StringComparison.Ordinal);
        Assert.Contains(vm.Labels.RepairToolsHint, vm.AcerHardwareHint, StringComparison.Ordinal);
        Assert.Contains("\n\n", vm.AcerHardwareHint, StringComparison.Ordinal);
    }

    [Fact]
    public void TheServiceRows_PrintTheLabelAndTheStateButNotTheServiceName()
    {
        var markup = Markup();

        // The short service name under every row was the noise the page was asked to lose.
        Assert.DoesNotContain("Classes=\"mono\" Text=\"{Binding Name}\"", markup, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding Label}\"", markup, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding StateText}\"", markup, StringComparison.Ordinal);
    }

    // =============================================================================================================
    // 3. The Nitro key.
    // =============================================================================================================

    [Fact]
    public void TheNitroKey_KeepsEveryExplanationInItsBadge()
    {
        using var vm = Create();

        // The three switches and the live launcher state, all in one tooltip. Nothing was dropped when the prose left
        // the rows: it moved.
        Assert.Contains(vm.Labels.NitroKeyHint, vm.NitroKeyHelp, StringComparison.Ordinal);
        Assert.Contains(vm.Labels.RedirectNitroSenseHint, vm.NitroKeyHelp, StringComparison.Ordinal);
        Assert.Contains(vm.Labels.NitroSenseLauncherBlockHint, vm.NitroKeyHelp, StringComparison.Ordinal);
        Assert.Contains(vm.NitroSenseLauncherStateText, vm.NitroKeyHelp, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNitroKey_PrintsOnlyTheLauncherState()
    {
        var markup = Markup();

        // The one line the group still draws is the launcher's state, which is the one thing a user cannot work out
        // by looking at the switches.
        Assert.Contains("NitroSenseLauncherBlockStateText", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("NitroSenseLauncherStateText", markup, StringComparison.Ordinal);
    }

    // =============================================================================================================
    // 4. The startup card.
    // =============================================================================================================

    [Fact]
    public void TheStartupCard_OffersOneSignInEntryOnWindows()
    {
        using var vm = Create(autoStart: new FakeAutoStart(), runAtStartup: new FakeRunAtStartup());

        // The elevated entry is strictly better, so the plain one is not offered beside it.
        Assert.True(vm.AutoStartAvailable);
        Assert.True(vm.RunAtStartupAvailable);
        Assert.False(vm.ShowRunAtStartupRow);
    }

    [Fact]
    public void TheStartupCard_KeepsThePlainEntryOnLinux()
    {
        using var vm = Create(runAtStartup: new FakeRunAtStartup());

        // Linux has no elevated entry at all, so the plain one is the whole feature and hiding it would remove
        // autostart entirely.
        Assert.False(vm.AutoStartAvailable);
        Assert.True(vm.RunAtStartupAvailable);
        Assert.True(vm.ShowRunAtStartupRow);
    }

    [Fact]
    public void TheStartupCard_HasNoProseExceptTheRecommendation()
    {
        var markup = Markup();

        Assert.DoesNotContain("Labels.RunAtStartupHint", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Labels.StartupExclusiveHint", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Labels.StartMinimizedHint", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Labels.AtkMouseIntegrationHint", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Labels.AutoStartHint", markup, StringComparison.Ordinal);

        // The one caption that stays is the recommendation, in brackets, beside the row it recommends.
        Assert.Contains("Labels.Recommended", markup, StringComparison.Ordinal);
        Assert.Equal("(önerilen)", new MainViewModel(new AppSettings(), null, null, TempSettingsPath()).Labels.Recommended);
    }

    [Fact]
    public async Task TurningTheElevatedEntryOn_AlsoPutsTheAppInTheTray()
    {
        var autoStart = new FakeAutoStart();
        using var vm = Create(autoStart: autoStart);

        // The write guard is on until the first load finishes, so the switch is only live after it.
        await vm.InitializeAsync();

        Assert.False(vm.StartMinimizedOn);

        vm.AutoStartOn = true;

        // The write is fire-and-forget, but every await in it is on a completed task, so it has already finished.
        await Task.Yield();

        // The entry exists so the window does not appear at sign-in. A user who has to find a second switch for that
        // has been given half a feature.
        Assert.True(vm.StartMinimizedOn);
        Assert.True(autoStart.Enabled);
    }

    [Fact]
    public async Task TurningTheElevatedEntryOff_LeavesTheTrayChoiceAlone()
    {
        var autoStart = new FakeAutoStart();
        using var vm = Create(autoStart: autoStart);

        await vm.InitializeAsync();

        vm.StartMinimizedOn = true;
        vm.AutoStartOn = true;

        // The write is fire-and-forget, but every await in it is on a completed task, so it has already finished.
        await Task.Yield();
        vm.AutoStartOn = false;
        await Task.Yield();

        // Turning the entry off is not a reason to change how the app starts when the user starts it by hand.
        Assert.True(vm.StartMinimizedOn);
        Assert.False(autoStart.Enabled);
    }

    // =============================================================================================================
    // 5. The support card.
    // =============================================================================================================

    [Fact]
    public void TheSupportCard_ReplacedTheNotesBlock()
    {
        var markup = Markup();

        Assert.Contains("Labels.Support", markup, StringComparison.Ordinal);
        Assert.Contains("Labels.SupportBody", markup, StringComparison.Ordinal);
        Assert.Contains("SupportKofiCommand", markup, StringComparison.Ordinal);
        Assert.Contains("SupportGitHubCommand", markup, StringComparison.Ordinal);

        // The notes block is gone: it printed the probe's own diagnostics, which is a developer's note in a user's
        // window.
        Assert.DoesNotContain("Labels.Notes", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("NotesText", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSupportCard_SaysWhatTheProjectCost()
    {
        using var vm = Create();

        // The claim the card makes, in the source language. Every catalogue carries its own wording.
        Assert.Contains("100 dolar", vm.Labels.SupportBody, StringComparison.Ordinal);
        Assert.Contains("yüzlerce saat", vm.Labels.SupportBody, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSupportLinks_AreHttpsAndPointAtTheProject()
    {
        Assert.StartsWith("https://", SupportLinks.Kofi, StringComparison.Ordinal);
        Assert.StartsWith("https://", SupportLinks.Repository, StringComparison.Ordinal);
        Assert.StartsWith("https://", SupportLinks.Sponsors, StringComparison.Ordinal);

        Assert.Contains(UpdateService.Owner, SupportLinks.Repository, StringComparison.Ordinal);
        Assert.Contains(UpdateService.Repository, SupportLinks.Repository, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSupportButtons_OpenTheTwoAddresses()
    {
        var opened = new System.Collections.Generic.List<string>();
        using var vm = Create(urlOpener: opened.Add);

        vm.SupportKofiCommand.Execute(null);
        vm.SupportGitHubCommand.Execute(null);

        Assert.Equal(new[] { SupportLinks.Kofi, SupportLinks.Repository }, opened);
    }

    // =============================================================================================================
    // 6. The settings card.
    // =============================================================================================================

    [Fact]
    public void TheSettingsCard_HasNoProse()
    {
        var markup = Markup();

        Assert.DoesNotContain("Labels.NitroLiteSettingsHint", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Labels.ThemeCardsHint", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Labels.UiFontHint", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Labels.LanguageRestartHint", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSettingsCard_PutsTheTypefaceBesideTheLanguage()
    {
        var markup = Markup();

        var language = markup.IndexOf("Labels.Language", StringComparison.Ordinal);
        var font = markup.IndexOf("Labels.UiFont", StringComparison.Ordinal);
        var theme = markup.IndexOf("Labels.Theme}", StringComparison.Ordinal);

        Assert.True(language > 0, "the language picker is missing");
        Assert.True(font > language, "the typeface picker is not after the language picker");
        Assert.True(theme > font, "the palette grid is not under the two pickers");
    }

    [Fact]
    public void TheTypefacePicker_DrawsEveryFaceInItsOwnFace()
    {
        var markup = Markup();

        // The list items, and the closed box: a font list written in one font tells a reader nothing about the others.
        Assert.Contains("FontFamily=\"{Binding Stack}\"", markup, StringComparison.Ordinal);
        Assert.Contains("FontFamily=\"{Binding SelectedUiFont.Stack}\"", markup, StringComparison.Ordinal);
    }

    // =============================================================================================================
    // 7. The window grip.
    // =============================================================================================================

    [Fact]
    public void TheWindow_HasAResizeGrip()
    {
        var markup = File.ReadAllText(
            Path.Combine(RepoRoot(), "NitroLite.Desktop", "Views", "MainWindow.axaml"));

        // The window draws its own chrome, so there is no frame to grab; the grip is the frame.
        Assert.Contains("OnResizeGripPressed", markup, StringComparison.Ordinal);
        Assert.Contains("Cursor=\"BottomRightCorner\"", markup, StringComparison.Ordinal);
    }

    // =============================================================================================================
    // Fakes.
    // =============================================================================================================

    private sealed class FakeAutoStart : IAutoStartService
    {
        public bool Enabled { get; private set; }

        public Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default) => Task.FromResult(Enabled);

        public Task<bool> EnableAsync(CancellationToken cancellationToken = default)
        {
            Enabled = true;
            return Task.FromResult(true);
        }

        public Task<bool> DisableAsync(CancellationToken cancellationToken = default)
        {
            Enabled = false;
            return Task.FromResult(true);
        }
    }

    private sealed class FakeRunAtStartup : IRunAtStartupService
    {
        public bool IsSupported => true;

        public bool Enabled { get; private set; }

        public bool StartMinimized { get; private set; }

        public Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default) => Task.FromResult(Enabled);

        public Task<bool> SetEnabledAsync(bool enabled, bool startMinimized, CancellationToken cancellationToken = default)
        {
            Enabled = enabled;
            StartMinimized = startMinimized;
            return Task.FromResult(true);
        }
    }
}
