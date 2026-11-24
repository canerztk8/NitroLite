using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Regression tests for the G3 security audit: the ancestor walk of the elevated-launch trust check, the single-handle
/// update verification, the game-profile safety copy on restore, the PowerShell quote escape and the profile-path
/// redaction.
/// </summary>
public sealed class AuditG3SecurityTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "nl_g3_" + Guid.NewGuid().ToString("N"));

    public AuditG3SecurityTests()
    {
        Directory.CreateDirectory(_temp);
    }

    public void Dispose()
    {
        SystemToolsService.ElevatedRunnerSeam = null;
        SystemToolsService.CurrentStateSeam = null;
        try { File.Delete(SystemToolsService.OptimizeBackupPath); } catch { }
        try { Directory.Delete(_temp, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string Path_(string name) => Path.Combine(_temp, name);

    // ---------------------------------------------------------------- a: every ancestor of an elevated executable

    /// <summary>
    /// ProgramData\NitroLiteTrustTest-*\app\NitroLite.exe, with one extra rule written on the grandparent only. The
    /// rule does not inherit, so the file and its own folder are as clean as the ProgramData default makes them: the
    /// only hole is the one on the grandparent, which is what the old file-and-parent check could not see.
    /// </summary>
    private static bool TrustWithGrandparentRule(FileSystemRights rights, out string? reason)
    {
        string grandparent = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "NitroLiteTrustTest-" + Guid.NewGuid().ToString("N"));
        string parent = Path.Combine(grandparent, "app");
        string exe = Path.Combine(parent, "NitroLite.exe");
        try
        {
            Directory.CreateDirectory(parent);
            File.WriteAllText(exe, "MZ");

            // Clean before the rule: the same layout without it is trusted, so the refusal below is the rule's doing.
            Assert.True(DirectoryTrust.IsExecutableTrusted(exe, out var cleanReason), cleanReason);

            var info = new DirectoryInfo(grandparent);
            var security = info.GetAccessControl(AccessControlSections.Access);
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                rights,
                InheritanceFlags.None,
                PropagationFlags.None,
                AccessControlType.Allow));
            info.SetAccessControl(security);

            return DirectoryTrust.IsExecutableTrusted(exe, out reason);
        }
        finally
        {
            try { File.Delete(exe); } catch { }
            try { Directory.Delete(parent); } catch { }
            try { Directory.Delete(grandparent); } catch { }
        }
    }

    [Fact]
    public void IsExecutableTrusted_RefusesACleanFileUnderAGrandparentAuthenticatedUsersCanModify()
    {
        // Modify includes DELETE: any local account can rename the grandparent away and put a folder of the same name,
        // with its own NitroLite.exe, in its place.
        bool trusted = TrustWithGrandparentRule(FileSystemRights.Modify, out var reason);

        Assert.False(trusted);
        Assert.NotNull(reason);
        Assert.Contains("NitroLiteTrustTest-", reason, StringComparison.Ordinal);
        Assert.Contains("Authenticated Users", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void IsExecutableTrusted_RefusesAGrandparentThatLetsAnyoneDeleteTheFolderBelowIt()
    {
        // FILE_DELETE_CHILD on the grandparent removes or renames the parent whatever the parent's own ACL says.
        Assert.False(TrustWithGrandparentRule(FileSystemRights.DeleteSubdirectoriesAndFiles, out _));
    }

    [Fact]
    public void IsExecutableTrusted_RefusesAGrandparentWhosePermissionsAnyoneCanRewrite()
    {
        Assert.False(TrustWithGrandparentRule(FileSystemRights.ChangePermissions, out _));
    }

    [Fact]
    public void IsExecutableTrusted_ACreateFoldersGrantOnAGrandparentIsNotARefusal()
    {
        // Every stock C:\ grants Authenticated Users "create folders" on itself. Counting that would refuse Program
        // Files: adding a sibling folder replaces nothing.
        Assert.True(TrustWithGrandparentRule(FileSystemRights.AppendData, out var reason), reason);
    }

    // ---------------------------------------------------------------- f: the updater verifies what it unpacks

    private string BuildPackage(string exeContent = "new exe")
    {
        var zip = Path_("package-" + Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        foreach (var (name, content) in new[] { ("NitroLite/NitroLite.exe", exeContent), ("NitroLite/LICENSE", "MIT") })
        {
            var entry = archive.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
        return zip;
    }

    private static string Sha256Of(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private string InstallFolder()
    {
        var install = Path_("install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "NitroLite.exe"), "old exe");
        return install;
    }

    [Fact]
    public void TryExtractVerifiedToStaging_AWrongDigestExtractsNothing()
    {
        var zip = BuildPackage();
        var before = Directory.GetDirectories(Path.GetTempPath(), "NitroLite-stage-*").Length;

        Assert.False(UpdateService.TryExtractVerifiedToStaging(zip, new string('0', 64), out var staged));
        Assert.Null(staged);
        Assert.False(UpdateService.TryExtractVerifiedToStaging(zip, null, out _));
        Assert.Equal(before, Directory.GetDirectories(Path.GetTempPath(), "NitroLite-stage-*").Length);
    }

    [Fact]
    public void TryExtractVerifiedToStaging_RecordsADigestForEveryStagedFile_AndTheInstallUsesThem()
    {
        var zip = BuildPackage();
        Assert.True(UpdateService.TryExtractVerifiedToStaging(zip, Sha256Of(zip).ToUpperInvariant(), out var staged));
        Assert.NotNull(staged);
        try
        {
            Assert.Equal(Sha256Of(Path.Combine(staged.Directory, "NitroLite.exe")), staged.Digests["NitroLite.exe"]);
            Assert.Equal(Sha256Of(Path.Combine(staged.Directory, "LICENSE")), staged.Digests["LICENSE"]);

            var install = InstallFolder();
            var target = UpdateService.InstallFromStaging(staged, install);

            Assert.Equal("new exe", File.ReadAllText(target));
            Assert.Equal("MIT", File.ReadAllText(Path.Combine(install, "LICENSE")));
        }
        finally { UpdateService.CleanUp(staged?.Directory); }
    }

    [Fact]
    public void InstallFromStaging_RefusesAStagedFileThatChangedAfterItWasVerified()
    {
        // The staging folder is in %TEMP%. A same-user process that rewrites the staged executable between the
        // extraction and the copy must not get its file into the install folder.
        var zip = BuildPackage();
        Assert.True(UpdateService.TryExtractVerifiedToStaging(zip, Sha256Of(zip), out var staged));
        Assert.NotNull(staged);
        try
        {
            File.WriteAllText(Path.Combine(staged.Directory, "NitroLite.exe"), "planted exe");
            var install = InstallFolder();

            var error = Assert.Throws<InvalidDataException>(() => UpdateService.InstallFromStaging(staged, install));
            Assert.Contains("NitroLite.exe", error.Message, StringComparison.Ordinal);

            // The prepare phase failed, so the install folder is exactly as it was.
            Assert.Equal("old exe", File.ReadAllText(Path.Combine(install, "NitroLite.exe")));
            Assert.False(File.Exists(Path.Combine(install, "NitroLite.exe.old")));
            Assert.Empty(Directory.GetFiles(install, "*.new", SearchOption.AllDirectories));
        }
        finally { UpdateService.CleanUp(staged?.Directory); }
    }

    [Fact]
    public void InstallFromStaging_CopiesOnlyThePackageFiles_NotWhateverElseIsInTheStagingFolder()
    {
        // A DLL dropped next to the staged executable would be loaded by the next start of NitroLite.exe from its own
        // folder. Only the allowlisted names are copied.
        var staging = Path.Combine(Path.GetTempPath(), "NitroLite-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            File.WriteAllText(Path.Combine(staging, "NitroLite.exe"), "new exe");
            File.WriteAllText(Path.Combine(staging, "version.dll"), "planted");
            Directory.CreateDirectory(Path.Combine(staging, "extra"));
            File.WriteAllText(Path.Combine(staging, "extra", "evil.exe"), "planted");

            var install = InstallFolder();
            UpdateService.InstallFromStaging(staging, install);

            Assert.Equal("new exe", File.ReadAllText(Path.Combine(install, "NitroLite.exe")));
            Assert.False(File.Exists(Path.Combine(install, "version.dll")));
            Assert.False(Directory.Exists(Path.Combine(install, "extra")));
        }
        finally { UpdateService.CleanUp(staging); }
    }

    [Fact]
    public void InstallFromStaging_RefusesAPackageFileTheVerifiedExtractionDidNotWrite()
    {
        var zip = BuildPackage();
        Assert.True(UpdateService.TryExtractVerifiedToStaging(zip, Sha256Of(zip), out var staged));
        Assert.NotNull(staged);
        try
        {
            // THIRD-PARTY-NOTICES.md is an allowlisted name, but this package did not carry it.
            File.WriteAllText(Path.Combine(staged.Directory, "THIRD-PARTY-NOTICES.md"), "planted");
            var install = InstallFolder();

            Assert.Throws<InvalidDataException>(() => UpdateService.InstallFromStaging(staged, install));
            Assert.Equal("old exe", File.ReadAllText(Path.Combine(install, "NitroLite.exe")));
            Assert.False(File.Exists(Path.Combine(install, "THIRD-PARTY-NOTICES.md")));
        }
        finally { UpdateService.CleanUp(staged?.Directory); }
    }

    // ---------------------------------------------------------------- g: game profiles get a safety copy

    [Fact]
    public void Restore_SetsTheLiveGameProfilesAsideBeforeOverwritingThem()
    {
        var settingsPath = Path_("settings.json");
        File.WriteAllText(settingsPath, """{"language":"de"}""");

        var backedUpProfiles = Path_("profiles-in-backup.json");
        File.WriteAllText(backedUpProfiles, """[{"exe_name":"old.exe","display_name":"From the backup"}]""");

        var backupPath = Path_("backup.json");
        Assert.True(BackupService.Create(backupPath, new AppSettings { Language = "es" },
            gameProfilesPath: backedUpProfiles, rgbPresetsPath: null, appVersion: "test").Success);

        // The profiles the user has now, which the restore is about to replace.
        var liveProfiles = Path_("game_profiles.json");
        File.WriteAllText(liveProfiles, """[{"exe_name":"current.exe","display_name":"The live rule"}]""");

        var result = BackupService.Restore(backupPath, settingsPath, liveProfiles, rgbPresetsPath: null);
        Assert.True(result.Success, result.Error);

        var copy = Assert.Single(Directory.GetFiles(
            _temp, Path.GetFileName(liveProfiles) + BackupService.PreRestoreSuffix + "-*"));
        Assert.Contains("current.exe", File.ReadAllText(copy), StringComparison.Ordinal);
        Assert.Contains("old.exe", File.ReadAllText(liveProfiles), StringComparison.Ordinal);

        // The settings copy is still taken as before.
        Assert.Single(Directory.GetFiles(_temp, Path.GetFileName(settingsPath) + BackupService.PreRestoreSuffix + "-*"));
    }

    // ---------------------------------------------------------------- h: PowerShell single-quote escape

    [Theory]
    [InlineData("O'Brien", "O''Brien")]
    [InlineData("O\u2019Brien", "O\u2019\u2019Brien")]
    [InlineData("\u2018a\u201Ab\u201B", "\u2018\u2018a\u201A\u201Ab\u201B\u201B")]
    [InlineData("C:\\Users\\plain\\x.log", "C:\\Users\\plain\\x.log")]
    [InlineData("", "")]
    public void PsQuote_DoublesEveryCharacterPowerShellReadsAsASingleQuote(string input, string expected)
    {
        Assert.Equal(expected, Loc.PsQuote(input));
    }

    [Fact]
    public void Ps_UsesTheSameEscapeForTranslatedText()
    {
        // Turkish is the source language, so T returns the text itself and only the escape is under test.
        string original = Loc.Language;
        try
        {
            Loc.SetLanguage("tr");
            Assert.Equal("O\u2019\u2019Brien yok", Loc.Ps("O\u2019Brien yok"));
        }
        finally { Loc.SetLanguage(original); }
    }

    [Fact]
    public void PsQuote_ACurlyApostropheCannotEndTheString_InRealPowerShell()
    {
        // The value a profile folder called O’Brien puts in an elevated script. Unescaped, the curly quote closes the
        // string and "Brien'" is parsed as code; escaped, PowerShell reads the original seven characters back.
        const string value = "O\u2019Brien";
        var script = "$v = '" + Loc.PsQuote(value) + "'\r\n" +
                     "if ($v.Length -eq 7 -and [int]$v[1] -eq 0x2019) { exit 0 } else { exit 3 }\r\n";
        var path = Path_("quote.ps1");
        File.WriteAllText(path, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var psi = new ProcessStartInfo
        {
            FileName = WindowsPaths.WindowsPowerShell,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        }.WithArguments("-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", path);

        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(30000);
        Assert.True(p.ExitCode == 0, $"PowerShell did not read the escaped value back: exit {p.ExitCode} {stderr}");
    }

    // ---------------------------------------------------------------- i: profile-path redaction

    [Fact]
    public void Redaction_ReplacesAProfileNameThatContainsASpace()
    {
        var scrubbed = Redaction.Scrub(@"C:\Users\Zorblat Quimby\AppData\Local\NitroLite\settings.json");

        Assert.Equal(@"C:\Users\<user>\AppData\Local\NitroLite\settings.json", scrubbed);
        Assert.DoesNotContain("Quimby", scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public void Redaction_ReplacesAJsonEscapedProfilePath()
    {
        // settings.json goes into the diagnostics archive scrubbed, and JSON doubles every backslash.
        const string json = "{\"path\":\"C:\\\\Users\\\\zorblat\\\\AppData\\\\Local\\\\NitroLite\"}";

        var scrubbed = Redaction.Scrub(json);

        Assert.DoesNotContain("zorblat", scrubbed, StringComparison.Ordinal);
        Assert.Contains("C:\\\\Users\\\\<user>\\\\AppData", scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public void Redaction_KeepsAnApostropheInsideTheName_AndStopsAtTheOneThatClosesAQuotedPath()
    {
        var inside = Redaction.Scrub(@"C:\Users\Zorblat O'Quimby\AppData\x.log");
        Assert.DoesNotContain("Quimby", inside, StringComparison.Ordinal);
        Assert.Equal(@"C:\Users\<user>\AppData\x.log", inside);

        var quoted = Redaction.Scrub(@"'C:\Users\zorblat' could not be read");
        Assert.Equal(@"'C:\Users\<user>' could not be read", quoted);
    }

    // ---------------------------------------------------------------- d / e: PC optimize, unelevated undo record

    private static SystemToolsService.OptimizeSnapshot Snapshot(
        string sysMain = "Automatic", int hags = 1, params string[] readableTasks) =>
        new(
            new Dictionary<string, string> { ["SysMain"] = sysMain },
            hags,
            readableTasks.ToDictionary(name => name, _ => "Ready"));

    [Fact]
    public async Task OptimizePcAsync_WritesTheUndoRecordBeforeElevating_AndTheScriptWritesNoFile()
    {
        try { File.Delete(SystemToolsService.OptimizeBackupPath); } catch { }
        SystemToolsService.CurrentStateSeam = () => Snapshot("Automatic", 1, "Consolidator", "UsbCeip");

        string? backupAtElevation = null;
        string? script = null;
        SystemToolsService.ElevatedRunnerSeam = s =>
        {
            script = s;
            backupAtElevation = File.Exists(SystemToolsService.OptimizeBackupPath)
                ? File.ReadAllText(SystemToolsService.OptimizeBackupPath)
                : null;
            return Task.FromResult(0);
        };

        var result = await SystemToolsService.OptimizePcAsync();

        Assert.True(result.Success, result.Message);
        Assert.NotNull(backupAtElevation);
        var record = SystemToolsService.ParseOptimizeBackup(backupAtElevation!);
        Assert.Equal("Automatic", record.Services["SysMain"]);
        Assert.Equal(1, record.Hags);
        Assert.Equal(2, record.Tasks.Count);

        // The elevated script must not write into the user's data folder at all.
        Assert.NotNull(script);
        Assert.DoesNotContain("Out-File", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("optimize_backup", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SystemToolsService.OptimizeBackupPath, script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OptimizePcAsync_KeepsTheFirstUndoRecord_AndOnlyDisablesTasksItCouldRecord()
    {
        File.WriteAllText(SystemToolsService.OptimizeBackupPath,
            """{"services":{"SysMain":"Automatic"},"hags":-1,"tasks":[]}""");
        // Already optimized: SysMain reads Disabled now. Only Consolidator's state can be read.
        SystemToolsService.CurrentStateSeam = () => Snapshot("Disabled", 2, "Consolidator");

        string? script = null;
        SystemToolsService.ElevatedRunnerSeam = s => { script = s; return Task.FromResult(0); };

        await SystemToolsService.OptimizePcAsync();

        var record = SystemToolsService.ParseOptimizeBackup(File.ReadAllText(SystemToolsService.OptimizeBackupPath));
        Assert.Equal("Automatic", record.Services["SysMain"]);
        Assert.Equal(2, record.Hags);
        Assert.Contains("$tasksToDisable = @('Consolidator')", script, StringComparison.Ordinal);
        Assert.DoesNotContain("'UsbCeip'", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestoreOptimizePcAsync_PassesTheRecordAsData_AndNeverOpensTheFileElevated()
    {
        File.WriteAllText(SystemToolsService.OptimizeBackupPath,
            """{"services":{"SysMain":"Automatic"},"hags":-1,"tasks":[],"extra":"ignored"}""");

        string? script = null;
        SystemToolsService.ElevatedRunnerSeam = s => { script = s; return Task.FromResult(1); };

        await SystemToolsService.RestoreOptimizePcAsync();

        Assert.NotNull(script);
        Assert.DoesNotContain(SystemToolsService.OptimizeBackupPath, script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Get-Content", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"SysMain\":\"Automatic\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("ignored", script, StringComparison.Ordinal);
    }

    // Shape of real `schtasks /query /tn ... /xml` output. An enabled task carries no <Enabled> element at all.
    private const string EnabledTaskXml = """
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <URI>\Microsoft\Windows\Customer Experience Improvement Program\Consolidator</URI>
          </RegistrationInfo>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <StartWhenAvailable>true</StartWhenAvailable>
          </Settings>
          <Actions Context="LocalSystem">
            <Exec><Command>%SystemRoot%\System32\wsqmcons.exe</Command></Exec>
          </Actions>
        </Task>
        """;

    private const string DisabledTaskXml = """
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <Enabled>false</Enabled>
          </Settings>
        </Task>
        """;

    [Fact]
    public void ParseTaskEnabled_ATaskWithNoEnabledElementIsEnabled()
    {
        // schtasks omits <Enabled> for an enabled task (the schema default is true). Reading that as "unknown" left
        // the CEIP tasks unrecorded, so Optimize stopped disabling them.
        Assert.True(SystemToolsService.ParseTaskEnabled(EnabledTaskXml, exitCode: 0));
    }

    [Fact]
    public void ParseTaskEnabled_ADisabledTaskSaysSo()
    {
        Assert.False(SystemToolsService.ParseTaskEnabled(DisabledTaskXml, exitCode: 0));
    }

    [Fact]
    public void ParseTaskEnabled_AFailedQueryOrNonTaskOutputIsUnknown()
    {
        Assert.Null(SystemToolsService.ParseTaskEnabled(EnabledTaskXml, exitCode: 1));
        Assert.Null(SystemToolsService.ParseTaskEnabled("", exitCode: 0));
        Assert.Null(SystemToolsService.ParseTaskEnabled("ERROR: The system cannot find the file specified.", exitCode: 0));
    }

    [Fact]
    public async Task EveryServiceWaitInTheElevatedScriptsIsBounded()
    {
        // Start-Service and Stop-Service wait without a limit for a service stuck pending, and a script that never
        // returns never reaches the finally block that starts AASSvc again. The scripts shadow both with bounded
        // functions.
        string? script = null;
        SystemToolsService.ElevatedRunnerSeam = s => { script = s; return Task.FromResult(0); };

        await SystemToolsService.FixAcerServicesAsync();

        Assert.NotNull(script);
        int startFunction = script!.IndexOf("function Start-Service", StringComparison.Ordinal);
        int stopFunction = script.IndexOf("function Stop-Service", StringComparison.Ordinal);
        int firstUse = script.IndexOf("Stop-Service -Name 'AASSvc'", StringComparison.Ordinal);
        Assert.True(startFunction >= 0 && stopFunction >= 0, "the bounded service functions are missing");
        Assert.True(startFunction < firstUse && stopFunction < firstUse, "the functions must be defined before use");
        Assert.Contains("WaitForStatus('Running', [TimeSpan]::FromSeconds(", script, StringComparison.Ordinal);
        Assert.Contains("WaitForStatus('Stopped', [TimeSpan]::FromSeconds(", script, StringComparison.Ordinal);
    }
}
