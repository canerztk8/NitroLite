using System.IO;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The commit phase of an update moves each destination aside to <c>.old</c> and then moves its <c>.new</c> into
/// place. A crash between those two moves used to leave the destination missing while the only good copy sat under
/// <c>.old</c>, and the old startup sweep then deleted it. These tests pin the repair that replaced that sweep.
/// </summary>
public class UpdateRecoveryTests : IDisposable
{
    private readonly string _root;

    public UpdateRecoveryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "NitroLite-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string PathOf(string relative) =>
        Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));

    private string Write(string relative, string content)
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void AHealthyInstallIsLeftAloneAndItsLeftoversAreSwept()
    {
        var exe = Write("NitroLite.exe", "new build");
        var license = Write("LICENSE", "new licence");
        var exeOld = Write("NitroLite.exe.old", "previous build");
        var licenseOld = Write("LICENSE.old", "previous licence");
        var licenseNew = Write("LICENSE.new", "staged licence");

        var report = UpdateService.RecoverInterruptedUpdate(_root);

        Assert.Empty(report.Restored);
        Assert.Empty(report.Failed);
        Assert.Equal("new build", File.ReadAllText(exe));
        Assert.Equal("new licence", File.ReadAllText(license));
        Assert.False(File.Exists(exeOld));
        Assert.False(File.Exists(licenseOld));
        Assert.False(File.Exists(licenseNew));
        Assert.Contains(exeOld, report.Removed);
        Assert.Contains(licenseOld, report.Removed);
        Assert.Contains(licenseNew, report.Removed);
    }

    [Fact]
    public void AMissingDestinationIsRestoredFromItsBackupInsteadOfLosingIt()
    {
        // The exact shape a crash between the two moves leaves: destination gone, .old holds the last good build.
        var license = PathOf("LICENSE");
        var backup = Write("LICENSE.old", "previous licence");

        var report = UpdateService.RecoverInterruptedUpdate(_root);

        Assert.Equal(new[] { license }, report.Restored);
        Assert.Empty(report.Failed);
        Assert.Equal("previous licence", File.ReadAllText(license));
        Assert.False(File.Exists(backup));
    }

    [Fact]
    public void AnEmptyDestinationCountsAsMissing()
    {
        // A power cut during the copy leaves a zero-byte file, which is no better than no file at all.
        var license = Write("LICENSE", "");
        Write("LICENSE.old", "previous licence");

        var report = UpdateService.RecoverInterruptedUpdate(_root);

        Assert.Single(report.Restored);
        Assert.Equal("previous licence", File.ReadAllText(license));
    }

    [Fact]
    public void TheBackupWinsOverTheHalfCopiedNewFile()
    {
        var license = PathOf("LICENSE");
        Write("LICENSE.old", "previous licence");
        Write("LICENSE.new", "staged licence");

        var report = UpdateService.RecoverInterruptedUpdate(_root);

        Assert.Single(report.Restored);
        Assert.Equal("previous licence", File.ReadAllText(license));
        Assert.False(File.Exists(PathOf("LICENSE.old")));
        Assert.False(File.Exists(PathOf("LICENSE.new")));
    }

    [Fact]
    public void TheStagedFileIsUsedWhenThereIsNoBackup()
    {
        var license = PathOf("LICENSE");
        Write("LICENSE.new", "staged licence");

        var report = UpdateService.RecoverInterruptedUpdate(_root);

        Assert.Single(report.Restored);
        Assert.Equal("staged licence", File.ReadAllText(license));
        Assert.False(File.Exists(PathOf("LICENSE.new")));
    }

    [Fact]
    public void AnEmptyBackupIsNotRestoredOverNothing()
    {
        var license = PathOf("LICENSE");
        Write("LICENSE.old", "");

        var report = UpdateService.RecoverInterruptedUpdate(_root);

        Assert.Empty(report.Restored);
        Assert.Empty(report.Failed);
        Assert.False(File.Exists(license));
        // The empty leftover is garbage, so the sweep takes it.
        Assert.False(File.Exists(PathOf("LICENSE.old")));
    }

    [Fact]
    public void AFileWithNoCopyAnywhereIsNotReportedAsAFailure()
    {
        // tools/WinMemoryCleaner.exe is optional: a user who deleted it must not see a recovery error every start.
        var report = UpdateService.RecoverInterruptedUpdate(_root);

        Assert.True(report.IsClean);
        Assert.Empty(report.Restored);
        Assert.Empty(report.Removed);
        Assert.Empty(report.Failed);
    }

    [Fact]
    public void AUserFileThatHappensToEndInOldIsNeverTouched()
    {
        var notes = Write("my-notes.old", "keep me");
        Write("NitroLite.exe", "new build");

        UpdateService.RecoverInterruptedUpdate(_root);

        Assert.True(File.Exists(notes));
        Assert.Equal("keep me", File.ReadAllText(notes));
    }

    [Fact]
    public void EveryPackageFileIsCoveredByTheRepair()
    {
        // The repair is only as good as its list: a file the package installs but the repair does not know about
        // would still be lost by an interrupted update.
        foreach (var relative in UpdateService.PackageFiles)
        {
            var path = PathOf(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".old", "previous " + relative);
        }

        var report = UpdateService.RecoverInterruptedUpdate(_root);

        Assert.Equal(UpdateService.PackageFiles.Length, report.Restored.Count);
        foreach (var relative in UpdateService.PackageFiles)
        {
            Assert.Equal("previous " + relative, File.ReadAllText(PathOf(relative)));
        }
    }

    [Fact]
    public void TheRepairIsIdempotent()
    {
        Write("LICENSE.old", "previous licence");

        var first = UpdateService.RecoverInterruptedUpdate(_root);
        var second = UpdateService.RecoverInterruptedUpdate(_root);

        Assert.Single(first.Restored);
        Assert.True(second.IsClean);
    }

    [Fact]
    public void TheReportIsCleanOnlyWhenNothingHappened()
    {
        Assert.True(new UpdateRecoveryReport([], [], []).IsClean);
        Assert.False(new UpdateRecoveryReport(["a"], [], []).IsClean);
        Assert.False(new UpdateRecoveryReport([], ["a"], []).IsClean);
        Assert.False(new UpdateRecoveryReport([], [], ["a"]).IsClean);
    }
}
