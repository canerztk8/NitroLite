using System.IO;
using System.Runtime.CompilerServices;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

internal static class TestDataIsolation
{
    /// <summary>
    /// Runs before any test touches NitroLite types: redirects every data file (settings, game profiles,
    /// RGB presets, thermal history, debloat backup) to a throw-away folder so tests never read or overwrite
    /// the real user data in %LOCALAPPDATA%\NitroLite.
    /// </summary>
    /// <summary>
    /// Deletes a throw-away folder, retrying while a debounced write is still finishing.
    /// </summary>
    /// <remarks>
    /// A view model saves on a 350-400 ms debounce, so a test that changes a control and then deletes its folder can
    /// catch the write in flight: <see cref="AtomicFile"/> holds the temporary file open for the length of the write,
    /// and a delete that lands in that window fails with "used by another process". The folder is disposable and the
    /// write is not a defect, so the delete waits for it instead of failing the test.
    /// </remarks>
    internal static void DeleteFolder(string path)
    {
        for (int attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(25);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(25);
            }
        }

        // A folder that still cannot be deleted is left for the sweep in RedirectAppDataToTempFolder.
        try { Directory.Delete(path, recursive: true); } catch { }
    }

    [ModuleInitializer]
    internal static void RedirectAppDataToTempFolder()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppPaths.DataDirOverrideVariable))) return;
        // The test host is usually terminated by the runner before ProcessExit runs, so also remove folders that
        // earlier runs left behind (older than an hour, so a parallel run's folder is never touched).
        try
        {
            foreach (var old in Directory.GetDirectories(Path.GetTempPath(), "NitroLiteTests_*"))
            {
                if (DateTime.Now - Directory.GetLastWriteTime(old) > TimeSpan.FromHours(1))
                {
                    try { Directory.Delete(old, recursive: true); } catch { }
                }
            }
        }
        catch { }

        var dir = Path.Combine(Path.GetTempPath(), "NitroLiteTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable(AppPaths.DataDirOverrideVariable, dir);
        // Remove the throw-away folder when the test process ends so test runs do not pile up in %TEMP%.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        };
    }
}

public class AppPathsTests
{
    [Fact]
    public void DataFiles_AreRedirectedAwayFromRealUserData()
    {
        var localAppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NitroLite");
        var settings = AppPaths.GetDataFile("settings.json");

        Assert.StartsWith(Environment.GetEnvironmentVariable(AppPaths.DataDirOverrideVariable)!, settings, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(localAppData, settings, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'), Path.GetDirectoryName(settings)!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SystemTools_ResolveToSystem32()
    {
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "powercfg.exe"), WindowsPaths.SystemTool("powercfg.exe"));
        Assert.EndsWith(@"WindowsPowerShell\v1.0\powershell.exe", WindowsPaths.WindowsPowerShell, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UniqueTempFile_IsNotPredictable()
    {
        var a = AppPaths.UniqueTempFile("nitrolite_debloat", ".log");
        var b = AppPaths.UniqueTempFile("nitrolite_debloat", ".log");
        Assert.NotEqual(a, b);
        Assert.StartsWith(Path.GetTempPath(), a, StringComparison.OrdinalIgnoreCase);
    }
}
