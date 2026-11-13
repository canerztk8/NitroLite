using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

public class GameDetectionServiceTests
{
    [Theory]
    [InlineData("[]", false)]
    [InlineData("{broken", true)]
    public void ExistingEmptyOrInvalidProfileFileIsPreserved(string content, bool invalid)
    {
        string file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, content);
            using var service = new GameDetectionService(file);
            Assert.Empty(service.GetProfiles());
            Assert.Equal(invalid, service.ProfileLoadError != null);
            if (invalid) Assert.False(service.SaveProfile(new GameProfile { ExeName = "sample" }));
            Assert.Equal(content, File.ReadAllText(file));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void GameProfile_JsonSerialization_RoundtripsAllPropertiesAccurately()
    {
        var profile = new GameProfile
        {
            ExeName = "witcher3",
            DisplayName = "The Witcher 3: Wild Hunt",
            TargetMode = OperatingMode.Turbo,
            TargetCpuBoost = CpuBoostMode.Efficient,
            TargetFanMode = FanMode.Auto,
            InstallPath = @"D:\Games\The Witcher 3\bin\x64\witcher3.exe",
            AutoApply = true
        };

        var json = JsonSerializer.Serialize(profile);
        var deserialized = JsonSerializer.Deserialize<GameProfile>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(profile.ExeName, deserialized.ExeName);
        Assert.Equal(profile.DisplayName, deserialized.DisplayName);
        Assert.Equal(profile.TargetMode, deserialized.TargetMode);
        Assert.Equal(profile.TargetCpuBoost, deserialized.TargetCpuBoost);
        Assert.Equal(profile.TargetFanMode, deserialized.TargetFanMode);
        Assert.Equal(profile.InstallPath, deserialized.InstallPath);
        Assert.Equal(profile.AutoApply, deserialized.AutoApply);
    }

    [Fact]
    public void ProfileManagement_SaveGetAndDelete_OperatesCleanly()
    {
        using var service = new GameDetectionService();
        string testExe = "test_game_" + Guid.NewGuid().ToString("N");

        var profile = new GameProfile
        {
            ExeName = testExe + ".exe", // Should strip extension
            DisplayName = "Test Game Automation",
            TargetMode = OperatingMode.Extreme,
            TargetCpuBoost = CpuBoostMode.Disabled,
            TargetFanMode = FanMode.Max,
            AutoApply = true
        };

        try
        {
            service.SaveProfile(profile);

            // Retrieve by plain name, uppercase, or with .exe
            var retrieved1 = service.GetProfile(testExe);
            Assert.NotNull(retrieved1);
            Assert.Equal("Test Game Automation", retrieved1.DisplayName);

            var retrieved2 = service.GetProfile(testExe.ToUpperInvariant());
            Assert.NotNull(retrieved2);

            var retrieved3 = service.GetProfile(testExe + ".exe");
            Assert.NotNull(retrieved3);

            // Update profile
            retrieved1.DisplayName = "Updated Game Name";
            service.SaveProfile(retrieved1);

            var updated = service.GetProfile(testExe);
            Assert.NotNull(updated);
            Assert.Equal("Updated Game Name", updated.DisplayName);

            // Delete profile
            bool deleted = service.DeleteProfile(testExe);
            Assert.True(deleted);

            var afterDelete = service.GetProfile(testExe);
            Assert.Null(afterDelete);
        }
        finally
        {
            service.DeleteProfile(testExe);
        }
    }

    [Fact]
    public void ProcessDisposalSafety_ProcessIterationPattern_SafelyDisposesAllHandles()
    {
        // Simulate obtaining processes and ensuring every instance is disposed in a finally block
        Process[]? procs = null;
        try
        {
            procs = Process.GetProcesses();
            Assert.NotEmpty(procs);
        }
        finally
        {
            if (procs != null)
            {
                for (int i = 0; i < procs.Length; i++)
                {
                    try { procs[i]?.Dispose(); } catch { }
                }
            }
        }
    }

    [Fact]
    public void GameStateProperties_ThreadSafetyAndConsistency()
    {
        using var service = new GameDetectionService();

        // ActiveGameProfile null initially implies IsGameActive == false
        Assert.False(service.IsGameActive);

        // Run multi-threaded queries on properties to verify no lock contention deadlocks
        Parallel.For(0, 100, i =>
        {
            var p = service.ActiveGameProfile;
            bool active = service.IsGameActive;
            var list = service.GetProfiles();
            Assert.NotNull(list);
        });
    }

    [Fact]
    public void FastPath_EsportsProfiles_DefaultToAggressiveBoost()
    {
        using var service = new GameDetectionService();
        var cs2Profile = service.GetProfile("cs2");
        if (cs2Profile != null)
        {
            Assert.Equal(CpuBoostMode.Aggressive, cs2Profile.TargetCpuBoost);
        }

        var valProfile = service.GetProfile("VALORANT-Win64-Shipping");
        if (valProfile != null)
        {
            Assert.Equal(CpuBoostMode.Aggressive, valProfile.TargetCpuBoost);
        }
    }

    [Theory]
    [InlineData("EAAntiCheat.Installer", true)]
    [InlineData("EAAntiCheat.Installer.Tool", true)]
    [InlineData("EasyAntiCheat_Setup", true)]
    [InlineData("BEService_x64", true)]
    [InlineData("vconsole2", true)]
    [InlineData("csgo_legacy_app", true)]
    [InlineData("UnrealCEFSubProcess_3071", true)]
    [InlineData("WinMTR", true)]
    [InlineData("bfvTrial", true)]
    [InlineData("stockfish-windows-x86-64", true)]
    [InlineData("Config", true)]
    [InlineData("Setup", true)]
    [InlineData("CrashReportClient", true)]
    [InlineData("UnityCrashHandler64", true)]
    [InlineData("TslGame_BE", true)]
    [InlineData("TslGame_ZK", true)]
    [InlineData("ExecPubg", true)]
    [InlineData("Game_eac", true)]
    [InlineData("cs2", false)]
    [InlineData("bfv", false)]
    [InlineData("Balatro", false)]
    [InlineData("b1-Win64-Shipping", false)]
    [InlineData("TslGame", false)]
    [InlineData("VALORANT-Win64-Shipping", false)]
    [InlineData("witcher3", false)]
    [InlineData("Overlord", false)]
    public void IsJunkOrHelperExecutable_IdentifiesJunkVsLegitimateGamesCorrectly(string exeName, bool expectedIsJunk)
    {
        bool actual = GameDetectionService.IsJunkOrHelperExecutable(exeName);
        Assert.Equal(expectedIsJunk, actual);
    }

    [Fact]
    public void PickBestGameExecutable_SelectsDirectNameMatchOverOtherCandidates()
    {
        var candidates = new System.Collections.Generic.List<string>
        {
            @"D:\Games\Overlord\Config.exe",
            @"D:\Games\Overlord\Overlord.exe"
        };
        var best = GameDetectionService.PickBestGameExecutable("Overlord", candidates);
        Assert.Equal(@"D:\Games\Overlord\Overlord.exe", best);
    }

    [Fact]
    public void PickBestGameExecutable_PrioritizesShippingBinaryForUnrealEngineGames()
    {
        var candidates = new System.Collections.Generic.List<string>
        {
            @"D:\Steam\steamapps\common\BlackMythWukong\b1.exe",
            @"D:\Steam\steamapps\common\BlackMythWukong\b1\Binaries\Win64\b1-Win64-Shipping.exe"
        };
        var best = GameDetectionService.PickBestGameExecutable("BlackMythWukong", candidates);
        Assert.Equal(@"D:\Steam\steamapps\common\BlackMythWukong\b1\Binaries\Win64\b1-Win64-Shipping.exe", best);
    }

    [Fact]
    public void PickBestGameExecutable_PrioritizesKnownCanonicalNames()
    {
        var candidates = new System.Collections.Generic.List<string>
        {
            @"D:\Steam\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe",
            @"D:\Steam\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\csgo.exe"
        };
        var best = GameDetectionService.PickBestGameExecutable("Counter-Strike Global Offensive", candidates);
        Assert.Equal(@"D:\Steam\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe", best);
    }

    [Fact]
    public void SanitizeProfiles_PurgesJunkAndDeduplicatesIdenticalGameTitles()
    {
        using var service = new GameDetectionService();

        // Add a known junk profile and duplicate profiles
        service.SaveProfile(new GameProfile
        {
            ExeName = "EAAntiCheat.Installer.Tool",
            DisplayName = "Battlefield V",
            TargetMode = OperatingMode.Turbo
        });

        service.SaveProfile(new GameProfile
        {
            ExeName = "bfv",
            DisplayName = "Battlefield V",
            TargetMode = OperatingMode.Turbo
        });

        // Run sanitize
        int removed = service.SanitizeProfiles();
        Assert.True(removed > 0);

        var profiles = service.GetProfiles();
        // The junk should be gone
        Assert.Null(service.GetProfile("EAAntiCheat.Installer.Tool"));
        // The real executable should remain
        var bfv = service.GetProfile("bfv");
        Assert.NotNull(bfv);
        Assert.Equal("Battlefield V", bfv.DisplayName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TruncatedProfileFileFallsBackToDefaultsInsteadOfBlockingSaves(string content)
    {
        // A 0-byte game_profiles.json used to raise a JsonException, set ProfileLoadError and make every later
        // SaveProfile return false with no way back. There is no user data in an empty file, so it must heal.
        string file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, content);
            using var service = new GameDetectionService(file);

            Assert.Null(service.ProfileLoadError);
            Assert.NotEmpty(service.GetProfiles());
            Assert.True(service.SaveProfile(new GameProfile { ExeName = "sample", DisplayName = "Sample" }));
            Assert.NotNull(service.GetProfile("sample"));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void UnusableProfileListReportsTheReasonInTheSelectedLanguage()
    {
        // The file parses but holds no usable entry, so the service raises its own message. That message
        // reaches the user inside ProfileLoadError, so it has to follow the selected language.
        string file = Path.GetTempFileName();
        string original = Loc.Language;
        try
        {
            File.WriteAllText(file, "[{\"ExeName\":\"\"}]");
            Loc.SetLanguage("de");
            using var service = new GameDetectionService(file);

            Assert.NotNull(service.ProfileLoadError);
            Assert.Contains("Ungültige Spielliste", service.ProfileLoadError);
        }
        finally
        {
            Loc.SetLanguage(original);
            File.Delete(file);
        }
    }

    [Fact]
    public void MissingProfileFileStartsFromDefaultsAndStaysWritable()
    {
        string file = Path.Combine(Path.GetTempPath(), "nitrolite-profiles-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using var service = new GameDetectionService(file);
            Assert.Null(service.ProfileLoadError);
            Assert.NotEmpty(service.GetProfiles());
            Assert.True(File.Exists(file));
        }
        finally { try { File.Delete(file); } catch { } }
    }

    [Fact]
    public void IsProcessIdAliveByName_FindsTheCurrentProcessAndRejectsAStalePid()
    {
        // The fast path falls back to this when the game's handle is unreadable. It must not report a closed game
        // as alive (that pinned game mode forever) and must not report a live one as gone.
        using var self = Process.GetCurrentProcess();
        Assert.True(GameDetectionService.IsProcessIdAliveByName(self.ProcessName, self.Id));
        Assert.True(GameDetectionService.IsProcessIdAliveByName(self.ProcessName + ".exe", self.Id));

        // A PID that cannot belong to the current process name.
        Assert.False(GameDetectionService.IsProcessIdAliveByName("nitrolite-no-such-game", self.Id));
    }

    [Theory]
    [InlineData("obs64")]
    [InlineData("obs64.exe")]
    [InlineData("OBS64")]
    [InlineData("steam")]
    [InlineData("chrome.exe")]
    [InlineData("discord")]
    [InlineData("msiafterburner")]
    public void IsNonGameExecutable_RejectsAlwaysRunningPrograms(string exeName)
    {
        // A profile for one of these would keep the machine in game mode permanently.
        Assert.True(GameDetectionService.IsNonGameExecutable(exeName));
    }

    [Theory]
    [InlineData("cs2")]
    [InlineData("Cyberpunk2077.exe")]
    [InlineData("b1-Win64-Shipping")]
    [InlineData("Overlord")]
    [InlineData("")]
    [InlineData(null)]
    public void IsNonGameExecutable_AcceptsRealGamesAndEmptyInput(string? exeName)
    {
        Assert.False(GameDetectionService.IsNonGameExecutable(exeName));
    }
}
