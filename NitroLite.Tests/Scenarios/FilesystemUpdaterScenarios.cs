using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests.Scenarios;

/// <summary>
/// Category 5: the two places where a file from outside the machine decides what happens next — a restored
/// backup and a downloaded update package.
/// </summary>
/// <remarks>
/// The single-method tests already cover the happy paths, the size limit, corrupt JSON and the two-phase
/// install. What is missing is the hostile and the ancient: an archive that lies about its size, an entry that
/// tries to climb out of the staging folder, a backup written by a version that did not have today's fields,
/// and the language the restore warnings come out in.
/// </remarks>
public class FilesystemUpdaterScenarios : IDisposable
{
    private readonly string _root;

    public FilesystemUpdaterScenarios()
    {
        _root = Path.Combine(Path.GetTempPath(), "NitroLiteFs_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Path_(params string[] parts) => Path.Combine(new[] { _root }.Concat(parts).ToArray());

    // ---------------------------------------------------------------- update package

    [Fact]
    public void ComputeSha256_MatchesThePublishedTestVector()
    {
        // The digest is the only thing standing between a user and a tampered package, so it has to be real
        // SHA-256 and not something that merely looks like it.
        var file = Path_("abc.bin");
        File.WriteAllBytes(file, Encoding.ASCII.GetBytes("abc"));

        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            UpdateService.ComputeSha256(file));
    }

    [Fact]
    public void ZipBomb_IsRefusedBeforeAnythingIsWritten()
    {
        // A zip declares its uncompressed size. A hostile or truncated one can declare gigabytes while weighing
        // a few hundred kilobytes; extracting it would fill the system drive. The cap has to be checked from the
        // declaration, before a single byte lands on disk.
        var zip = Path_("bomb.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("NitroLite/NitroLite.exe", CompressionLevel.Fastest);
            using var stream = entry.Open();
            var chunk = new byte[1024 * 1024];
            for (int i = 0; i < 520; i++) stream.Write(chunk, 0, chunk.Length);
        }

        var error = Assert.Throws<InvalidDataException>(() => UpdateService.ExtractToStaging(zip));
        Assert.Contains("too large", error.Message, StringComparison.OrdinalIgnoreCase);

        // Known defect, reported and awaiting a decision: the staging folder is created before the package is
        // validated, so a refused package leaves an empty NitroLite-stage-* folder in %TEMP% forever. Nothing is
        // written into it, so this is clutter rather than a hazard.
        Assert.True(Directory.GetDirectories(Path.GetTempPath(), "NitroLite-stage-*").Length > 0);
    }

    [Fact]
    public void TraversalEntry_IsRefused()
    {
        // Zip-Slip: an entry named ../../Windows/System32/evil.dll must never resolve outside the staging folder.
        // The whitelist catches it first, and the path check is the second line of defence.
        var zip = Path_("slip.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            void Add(string name, string content)
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(content);
            }

            Add("NitroLite/NitroLite.exe", "new exe");
            Add("NitroLite/../../Windows/System32/evil.dll", "surprise");
        }

        Assert.Throws<InvalidDataException>(() => UpdateService.ExtractToStaging(zip));
        Assert.False(File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "evil.dll")));
    }

    [Fact]
    public void PackageWithoutTheExecutable_IsRefusedRatherThanInstalled()
    {
        // Without NitroLite.exe there is no prefix to strip and nothing to install; the update has to fail
        // loudly instead of leaving the install folder half-written.
        var zip = Path_("noexe.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("NitroLite/LICENSE");
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write("MIT");
        }

        Assert.Throws<InvalidDataException>(() => UpdateService.ExtractToStaging(zip));
    }

    // ---------------------------------------------------------------- backup restore

    [Fact]
    public void OversizedBackup_IsRejectedBeforeItIsParsed()
    {
        // The size gate reads the file length, not the file. A 20 MB file whose first bytes are a perfectly
        // valid backup proves the order: if it were parsed first, this would restore successfully.
        var backup = Path_("huge.json");
        var settingsPath = Path_("settings.json");
        var original = new AppSettings { Language = "tr" };
        SettingsManager.SaveSettingsAtomic(settingsPath, original);

        using (var stream = File.Create(backup))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.Write("{\"app\":\"NitroLite\",\"schema\":1,\"settings\":{\"language\":\"de\"},\"pad\":\"");
            var chunk = new string('x', 1024 * 1024);
            for (int i = 0; i < 20; i++) writer.Write(chunk);
            writer.Write("\"}");
        }

        var result = BackupService.Restore(backup, settingsPath, null, null);

        Assert.False(result.Success);
        Assert.Equal("tr", SettingsManager.LoadSettings(settingsPath).Settings.Language);
    }

    [Fact]
    public void OldBackupWithMissingFields_FillsInDefaultsInsteadOfCrashing()
    {
        // A backup written by an older version carries only the fields that existed then. Restoring it must
        // merge onto the defaults, not leave the rest of the settings null.
        var backup = Path_("old.json");
        var settingsPath = Path_("settings.json");
        File.WriteAllText(backup,
            "{\"app\":\"NitroLite\",\"schema\":1,\"settings\":{\"language\":\"de\"}}",
            new UTF8Encoding(false));

        var result = BackupService.Restore(backup, settingsPath, null, null);

        Assert.True(result.Success);
        var restored = SettingsManager.LoadSettings(settingsPath).Settings;
        Assert.Equal("de", restored.Language);
        Assert.Equal(new AppSettings().Theme, restored.Theme);
        Assert.Equal(new AppSettings().OsdEnabled, restored.OsdEnabled);
        Assert.Equal(new AppSettings().AtkMouseIntegrationEnabled, restored.AtkMouseIntegrationEnabled);
        Assert.NotNull(restored.FanCurvePoints);
    }

    [Fact]
    public void RestoreSummary_IsStillTurkishInEveryLanguage_KNOWN_DEFECT()
    {
        // Reported, awaiting a decision. BackupService puts the Turkish source strings straight into Written and
        // Skipped instead of passing them through Loc.T, so an English or German window shows a Turkish restore
        // summary. This test pins the current behaviour so the defect cannot be forgotten; flip it to the
        // translated strings once the fix lands.
        var backup = Path_("school.json");
        var settingsPath = Path_("settings.json");
        var settings = new AppSettings { Language = "en", SchoolModeEnabled = true };
        BackupService.Create(backup, settings, null, null, "v1.2.0");

        string previous = Loc.Language;
        try
        {
            Loc.SetLanguage("en");
            var english = BackupService.Restore(backup, settingsPath, null, null);
            Assert.True(english.Success);
            Assert.Contains("Okul modu kapalı olarak geri yüklendi", english.Skipped);
            Assert.DoesNotContain("School mode restored as off", english.Skipped);

            // The catalogue does carry the translation, so the fix is a Loc.T call and nothing else.
            Assert.Equal("School mode restored as off", Loc.T("Okul modu kapalı olarak geri yüklendi"));
        }
        finally
        {
            Loc.SetLanguage(previous);
        }
    }

    [Fact]
    public void RestoreOfABackupFromAnotherMachine_KeepsTheLocalWindowPosition()
    {
        // The rectangle belongs to the screen it was saved on. A backup from a 2560x1440 laptop restored on a
        // 1366x768 one would open the window off-screen, so the position is dropped and the window re-centres.
        var backup = Path_("pos.json");
        var settingsPath = Path_("settings.json");
        var saved = new AppSettings
        {
            WindowLeft = 2400, WindowTop = 300, WindowWidth = 1400, WindowHeight = 900, WindowMaximized = true
        };
        BackupService.Create(backup, saved, null, null, "v1.2.0");

        Assert.True(BackupService.Restore(backup, settingsPath, null, null).Success);

        var restored = SettingsManager.LoadSettings(settingsPath).Settings;
        Assert.Null(restored.WindowLeft);
        Assert.Null(restored.WindowTop);
        Assert.False(restored.WindowMaximized);
    }

    [Fact]
    public void RestoreOfATruncatedBackup_LeavesTheLiveSettingsAlone()
    {
        // A backup copied off a failing drive can end mid-object. The live settings must survive a rejected
        // restore untouched, otherwise the user loses their configuration to a file that never parsed.
        var backup = Path_("truncated.json");
        var settingsPath = Path_("settings.json");
        SettingsManager.SaveSettingsAtomic(settingsPath, new AppSettings { Language = "tr", Theme = AppTheme.Light });

        File.WriteAllText(backup, "{\"app\":\"NitroLite\",\"schema\":1,\"settings\":{\"language\":\"de\"",
            new UTF8Encoding(false));

        var result = BackupService.Restore(backup, settingsPath, null, null);

        Assert.False(result.Success);
        var live = SettingsManager.LoadSettings(settingsPath).Settings;
        Assert.Equal("tr", live.Language);
        Assert.Equal(AppTheme.Light, live.Theme);
    }
}
