using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NitroLite.Core;
using NitroLite.Desktop.ViewModels;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Guards the three things this round changed: a language change restarts the shell, the wheel no longer edits a
/// closed picker, and the support card wears Ko-fi's own blue button under a warmer sentence.
/// </summary>
/// <remarks>
/// The restart is a delegate rather than a process, which is what makes it assertable here: the test counts the
/// requests instead of watching for a second window. The wheel and the button are markup, so those assertions read
/// the files, the same way the settings-page tests do.
/// </remarks>
public class LanguageRestartAndSupportTests
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

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(parts.Prepend(RepoRoot()).ToArray()));

    private static string TempSettingsPath() =>
        Path.Combine(Path.GetTempPath(), $"nitrolite_restart_{Guid.NewGuid():N}.json");

    /// <summary>Runs a body with a view model, then puts the static language back and deletes the settings file.</summary>
    private static async Task WithViewModelAsync(Func<MainViewModel, Task> body)
    {
        var original = Loc.Language;
        var path = TempSettingsPath();
        try
        {
            using var vm = new MainViewModel(new AppSettings(), hardware: null, platform: null, path);
            await body(vm);
        }
        finally
        {
            Loc.SetLanguage(original);
            try { File.Delete(path); } catch { }
        }
    }

    // =============================================================================================================
    // 1. A language change restarts the shell.
    // =============================================================================================================

    [Fact]
    public void TheRestartFlag_IsTheOneTheShellPasses()
    {
        var source = Read("NitroLite.Desktop", "Program.cs");

        // The flag is a constant on an internal type, so the assertion is on the source. It has to be a long option:
        // a single-dash flag would collide with the worker's own switches.
        Assert.Contains("RestartArgument = \"--restart\"", source, StringComparison.Ordinal);
        Assert.Contains("AcquireWhenFree", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangingTheLanguage_AsksTheShellToRestart()
    {
        await WithViewModelAsync(async vm =>
        {
            var calls = 0;
            vm.RestartRequest = () =>
            {
                calls++;
                return Task.CompletedTask;
            };

            var target = Loc.Languages.First(l => l.Code != Loc.Language);
            vm.SelectedLanguage = target;
            await vm.RestartTask;

            Assert.Equal(1, calls);
            Assert.Equal(target.Code, Loc.Language);
            Assert.Equal(vm.Labels.LanguageRestarting, vm.Status);
            Assert.True(vm.RestartTask.IsCompletedSuccessfully);
        });
    }

    [Fact]
    public async Task ChangingTheLanguage_WithoutARestartHook_StillSaysWhatHappened()
    {
        await WithViewModelAsync(async vm =>
        {
            // A host that cannot restart — a test, or a build with no process path — must not throw from a setter.
            Assert.Null(vm.RestartRequest);

            vm.SelectedLanguage = Loc.Languages.First(l => l.Code != Loc.Language);
            await vm.RestartTask;

            Assert.Equal(vm.Labels.LanguageRestarting, vm.Status);
            Assert.True(vm.RestartTask.IsCompletedSuccessfully);
        });
    }

    [Fact]
    public async Task AFailingRestart_IsSwallowedRatherThanLeftToFault()
    {
        await WithViewModelAsync(async vm =>
        {
            var ran = false;
            vm.RestartRequest = () =>
            {
                ran = true;
                throw new InvalidOperationException("no process path");
            };

            vm.SelectedLanguage = Loc.Languages.First(l => l.Code != Loc.Language);
            await vm.RestartTask;

            // The setter cannot surface an exception, so the failure is logged and the task completes. A faulted task
            // here would be an unobserved exception that takes the process down at some later collection.
            Assert.True(ran);
            Assert.True(vm.RestartTask.IsCompletedSuccessfully);
            Assert.Equal(vm.Labels.LanguageRestarting, vm.Status);
        });
    }

    [Fact]
    public void TheRestart_ReusesTheExitPathSoTheFansAreHandedBack()
    {
        var source = Read("NitroLite.Desktop", "App.axaml.cs");

        // The new copy is started before the old one leaves — a process cannot start its successor afterwards — and
        // the old one then leaves through the same ExitAsync the tray uses, so the fan handback runs exactly once.
        Assert.Contains("Program.RestartArgument", source, StringComparison.Ordinal);
        Assert.Contains("ExitAsync(desktop)", source, StringComparison.Ordinal);
        Assert.Contains("RestartRequest", source, StringComparison.Ordinal);
    }

    // =============================================================================================================
    // 2. The wheel no longer edits a closed picker.
    // =============================================================================================================

    [Fact]
    public void TheWheel_IsTakenBeforeAClosedComboBoxSeesIt()
    {
        var source = Read("NitroLite.Desktop", "Views", "MainWindow.axaml.cs");

        // Tunnel, not bubble: the combo box has to be stopped before it handles the event, and a bubbling handler
        // would run after the selection had already moved.
        Assert.Contains(
            "AddHandler(PointerWheelChangedEvent, OnPreviewWheel, RoutingStrategies.Tunnel)",
            source,
            StringComparison.Ordinal);
        Assert.Contains("IsDropDownOpen: false", source, StringComparison.Ordinal);
        Assert.Contains("e.Handled = true", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWheel_StillScrollsThePage()
    {
        var source = Read("NitroLite.Desktop", "Views", "MainWindow.axaml.cs");

        // Taking the event without forwarding it would leave a dead zone over every picker: the page would stop
        // scrolling exactly where the user is trying to scroll past.
        Assert.Contains("FindAncestorOfType<ScrollViewer>", source, StringComparison.Ordinal);
        Assert.Contains("scroller.Offset = new Vector(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void NoView_HandlesTheWheelOnItsOwn()
    {
        // One tunnel handler on the window covers all 28 pickers. A per-view handler would be a second mechanism to
        // keep in step, and the next picker added would silently miss it.
        var views = Directory.GetFiles(Path.Combine(RepoRoot(), "NitroLite.Desktop", "Views"), "*.axaml");
        Assert.NotEmpty(views);

        foreach (var view in views)
        {
            var markup = File.ReadAllText(view);
            Assert.DoesNotContain("PointerWheelChanged", markup, StringComparison.Ordinal);
        }
    }

    // =============================================================================================================
    // 3. The Ko-fi button and the sentence above it.
    // =============================================================================================================

    [Fact]
    public void TheKofiButton_IsTheBlueBrandButton()
    {
        var markup = Read("NitroLite.Desktop", "Views", "SettingsView.axaml");

        var start = markup.IndexOf("Classes=\"kofi\"", StringComparison.Ordinal);
        Assert.True(start > 0, "the support card has no Ko-fi button");

        var end = markup.IndexOf("</Button>", start, StringComparison.Ordinal);
        Assert.True(end > start, "the Ko-fi button is not closed");

        var button = markup[start..end];

        // The cup is drawn, not downloaded: the app is offline-first and a remote image would be a blank square on a
        // machine with no network.
        Assert.Contains("<Path", button, StringComparison.Ordinal);
        Assert.Contains("Labels.SupportKofi", button, StringComparison.Ordinal);
        Assert.Contains("SupportKofiCommand", button, StringComparison.Ordinal);
        Assert.DoesNotContain("http", button, StringComparison.Ordinal);
    }

    [Fact]
    public void TheKofiButton_KeepsTheBrandBlueInEveryTheme()
    {
        var markup = Read("NitroLite.Desktop", "App.axaml");

        Assert.Contains("Selector=\"Button.kofi\"", markup, StringComparison.Ordinal);
        Assert.Contains("#29ABE0", markup, StringComparison.Ordinal);

        // A DynamicResource here would repaint the button in the palette's accent, and the mark would stop being the
        // mark. The literal is the point, so the style must not reach for a theme brush.
        var start = markup.IndexOf("Selector=\"Button.kofi\"", StringComparison.Ordinal);
        var end = markup.IndexOf("</Style>", start, StringComparison.Ordinal);
        Assert.DoesNotContain("DynamicResource", markup[start..end], StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSupportCopy_InvitesInsteadOfInstructing()
    {
        await WithViewModelAsync(vm =>
        {
            // The labels are read through the static catalogue, so the source language has to be the one in force.
            Loc.SetLanguage(Loc.SourceLanguage);

            // The old line read "If it's useful to you you can buy me a coffee", which is an instruction dressed as a
            // favour. The new one offers the coffee as one way to say thanks and puts a star on the same footing.
            Assert.Contains("bir kahve güzel bir teşekkür olur", vm.Labels.SupportBody, StringComparison.Ordinal);
            Assert.Contains("bir yıldız", vm.Labels.SupportBody, StringComparison.Ordinal);
            Assert.DoesNotContain("bana bir kahve al", vm.Labels.SupportBody, StringComparison.Ordinal);
            Assert.DoesNotContain("buy me a coffee", vm.Labels.SupportBody, StringComparison.OrdinalIgnoreCase);

            Assert.Equal("Ko-fi'de bana destek ol", vm.Labels.SupportKofi);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void EveryCatalogue_CarriesTheThreeRewrittenLines()
    {
        var keys = new[]
        {
            "Dil değişti; NitroLite yeni dille yeniden açılıyor.",
            "Ko-fi'de bana destek ol",
            "NitroLite tek bir öğrencinin projesi: 100 doların üzerinde cepten harcama, yüzlerce saat emek ve her sürüm " +
            "kendi dizüstü bilgisayarımda denendi. Her hâlükârda ücretsiz ve MIT lisanslı kalacak. Zamanınızdan bir şey " +
            "kurtardıysa bir kahve güzel bir teşekkür olur; bir yıldız ya da hata bildirimi de en az onun kadar değerli."
        };

        foreach (var code in new[] { "en", "de", "es", "hi", "id", "pt", "ru" })
        {
            var catalogue = Loc.LoadCatalog(code);
            Assert.NotEmpty(catalogue);

            foreach (var key in keys)
            {
                Assert.True(catalogue.ContainsKey(key), $"{code} is missing the key: {key[..Math.Min(40, key.Length)]}");
                Assert.False(string.IsNullOrWhiteSpace(catalogue[key]), $"{code} left {key[..Math.Min(40, key.Length)]} empty");
            }

            // The English button says what Ko-fi's own widget says, so the mark and the label agree.
            if (code == "en")
            {
                Assert.Equal("Support me on Ko-fi", catalogue[keys[1]]);
            }
        }
    }

    [Fact]
    public void EveryCatalogue_CarriesTheRestartLogLines()
    {
        // The three lines the restart writes to the log. They are new keys, so a catalogue that missed them would show
        // Turkish in the log of every other language — the one place a user looks when the restart misbehaves.
        var keys = new[]
        {
            "Yeniden başlatma başlatılamadı",
            "Yeniden başlatma isteği gönderilemedi",
            "Yeniden başlatma için çalıştırılabilir dosya bulunamadı"
        };

        foreach (var code in new[] { "en", "de", "es", "hi", "id", "pt", "ru" })
        {
            var catalogue = Loc.LoadCatalog(code);
            foreach (var key in keys)
            {
                Assert.True(catalogue.ContainsKey(key), $"{code} is missing the key: {key}");
                Assert.False(string.IsNullOrWhiteSpace(catalogue[key]), $"{code} left {key} empty");
            }
        }
    }

    [Fact]
    public void TheOldWording_IsGoneFromEveryCatalogue()
    {
        var retired = new[]
        {
            "Dil değişikliği hemen uygulanır.",
            "Ko-fi'de destekle"
        };

        foreach (var code in new[] { "en", "de", "es", "hi", "id", "pt", "ru" })
        {
            var catalogue = Loc.LoadCatalog(code);
            foreach (var key in retired)
            {
                Assert.False(catalogue.ContainsKey(key), $"{code} still carries the retired key: {key}");
            }
        }
    }
}
