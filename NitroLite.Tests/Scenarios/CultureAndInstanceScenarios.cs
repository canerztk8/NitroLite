using System.Globalization;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests.Scenarios;

/// <summary>
/// Category 6: the same code running on a Turkish or German Windows.
/// </summary>
/// <remarks>
/// The single-method tests run under whatever culture the machine has. These force <c>tr-TR</c> and
/// <c>de-DE</c> around the parsing and formatting paths, because those two are where a decimal comma and the
/// Turkish dotless i turn a working build into a wrong number or a failed lookup.
/// </remarks>
public class CultureAndInstanceScenarios
{
    private static T InCulture<T>(string culture, Func<T> body)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUi = CultureInfo.CurrentUICulture;
        try
        {
            var info = CultureInfo.GetCultureInfo(culture);
            CultureInfo.CurrentCulture = info;
            CultureInfo.CurrentUICulture = info;
            return body();
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUi;
        }
    }

    private static void InCulture(string culture, Action body) =>
        InCulture(culture, () => { body(); return 0; });

    // ---------------------------------------------------------------- number formatting

    [Theory]
    [InlineData("tr-TR")]
    [InlineData("de-DE")]
    public void TranslatedTemplates_FollowTheCultureOnlyWhenAskedTo(string culture)
    {
        // Loc.T formats with the current culture, Loc.TInv with the invariant one. Call sites that print a
        // measurement use the invariant form; this test is the contract they rely on.
        InCulture(culture, () =>
        {
            string separator = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
            Assert.Equal($"CPU 1{separator}5 W", Loc.T($"CPU {1.5:F1} W"));
            Assert.Equal("CPU 1.5 W", Loc.TInv($"CPU {1.5:F1} W"));
        });
    }

    // ---------------------------------------------------------------- language codes

    [Theory]
    [InlineData("tr-TR")]
    [InlineData("de-DE")]
    public void LanguageCodes_AreMatchedWithoutCultureFolding(string culture)
    {
        // "ID" is Indonesian. Under tr-TR, ToLower() turns the I into a dotless ı and the code stops matching,
        // which is why every comparison in Loc uses OrdinalIgnoreCase or ToLowerInvariant.
        InCulture(culture, () =>
        {
            string previous = Loc.Language;
            try
            {
                Loc.SetLanguage("ID");
                Assert.Equal("id", Loc.Language);

                Loc.SetLanguage("TR");
                Assert.Equal("tr", Loc.Language);

                Loc.SetLanguage("DE");
                Assert.Equal("de", Loc.Language);
            }
            finally
            {
                Loc.SetLanguage(previous);
            }
        });
    }

    [Fact]
    public void AnUnknownOrRegionalCode_FallsBackToEnglish()
    {
        // "tr-TR" is not one of the eight codes the catalogue carries. Falling back to English is the documented
        // behaviour; silently picking Turkish because the prefix matches would surprise a user who typed it.
        string previous = Loc.Language;
        try
        {
            Loc.SetLanguage("tr-TR");
            Assert.Equal(Loc.DefaultLanguage, Loc.Language);

            Loc.SetLanguage("ja-JP");
            Assert.Equal(Loc.DefaultLanguage, Loc.Language);

            Loc.SetLanguage(null);
            Assert.Equal(Loc.DefaultLanguage, Loc.Language);
        }
        finally
        {
            Loc.SetLanguage(previous);
        }
    }

    [Theory]
    [InlineData("tr-TR", "tr")]
    [InlineData("de-DE", "de")]
    [InlineData("ru-RU", "ru")]
    [InlineData("pt-BR", "pt")]
    [InlineData("hi-IN", "hi")]
    [InlineData("id-ID", "id")]
    [InlineData("es-ES", "es")]
    [InlineData("ja-JP", "en")]
    public void SystemLanguageDetection_MapsWindowsToTheCatalogue(string windows, string expected)
    {
        // A fresh install follows the Windows display language. Anything outside the eight falls back to English
        // rather than to Turkish, which is the source language but not a sensible default for a stranger.
        Assert.Equal(expected, Loc.DetectSystemLanguage(CultureInfo.GetCultureInfo(windows)));
    }

    // ---------------------------------------------------------------- command line

    [Theory]
    [InlineData("tr-TR")]
    [InlineData("de-DE")]
    [InlineData("en-US")]
    public void CommandLine_ParsesTheSameInEveryCulture(string culture)
    {
        // The CLI is what a shortcut or a script calls. A Turkish machine must read "--mode turbo" exactly like
        // an English one, and must not accept "50,5" as a fan percentage just because the culture uses commas.
        InCulture(culture, () =>
        {
            Assert.True(CliCommands.TryParse(new[] { "--mode", "TURBO" }, out var mode, out _));
            Assert.Equal(OperatingMode.Turbo, mode.Mode);

            Assert.True(CliCommands.TryParse(new[] { "--fan", "50" }, out var fan, out _));
            Assert.Equal(FanMode.Custom, fan.Fan);
            Assert.Equal(50, fan.FanPercent);

            Assert.True(CliCommands.TryParse(new[] { "--profile", "3" }, out var profile, out _));
            Assert.Equal(3, profile.MouseProfile);

            // A comma is not a decimal separator here: the value is a whole percentage.
            Assert.False(CliCommands.TryParse(new[] { "--fan", "50,5" }, out _, out _));
            Assert.False(CliCommands.TryParse(new[] { "--fan", "101" }, out _, out _));
        });
    }

    [Theory]
    [InlineData("tr-TR")]
    [InlineData("de-DE")]
    public void CommandLine_RejectsAnUnknownModeWithTheValueInTheMessage(string culture)
    {
        // The error text goes into a toast, so it has to name what the user typed.
        InCulture(culture, () =>
        {
            Assert.False(CliCommands.TryParse(new[] { "--mode", "hizli" }, out _, out var error));
            Assert.Contains("hizli", error);
        });
    }

    // ---------------------------------------------------------------- settings round trip

    [Theory]
    [InlineData("tr-TR")]
    [InlineData("de-DE")]
    public void SettingsRoundTrip_SurvivesACultureWithADecimalComma(string culture)
    {
        // System.Text.Json writes invariant, but the fan curve and the wear history carry numbers that a
        // culture-aware writer would turn into "65,5". A settings file written on a Turkish machine has to load
        // on an English one and the other way round.
        InCulture(culture, () =>
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "NitroLiteCulture_" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var settings = new AppSettings
                {
                    Language = "tr",
                    Theme = AppTheme.Dark,
                    CustomFanCpuPercent = 65,
                    FanCurveEnabled = true,
                    FanCurvePoints = new System.Collections.Generic.List<FanCurvePoint>
                    {
                        new(40, 30),
                        new(90, 100)
                    }
                };
                SettingsManager.SaveSettingsAtomic(path, settings);

                var text = System.IO.File.ReadAllText(path);
                Assert.DoesNotContain(",5", text);

                var loaded = SettingsManager.LoadSettings(path).Settings;
                Assert.Equal("tr", loaded.Language);
                Assert.Equal(AppTheme.Dark, loaded.Theme);
                Assert.Equal(2, loaded.FanCurvePoints!.Count);
                Assert.Equal(90, loaded.FanCurvePoints[1].TemperatureC);
            }
            finally
            {
                try { System.IO.File.Delete(path); } catch { }
            }
        });
    }

    // ---------------------------------------------------------------- single instance

    [Fact]
    public void ASingleInstanceMutex_IsHeldForTheLifetimeOfTheProcess()
    {
        // The name is what stops a second copy from writing to the EC at the same time. Two mutexes with the
        // same name cannot both be created as the initial owner, which is the whole mechanism.
        var name = @"Local\NitroLite_Scenario_" + Guid.NewGuid().ToString("N");
        using var first = new Mutex(true, name, out bool firstCreated);
        Assert.True(firstCreated);

        using var second = new Mutex(true, name, out bool secondCreated);
        Assert.False(secondCreated);
    }
}
