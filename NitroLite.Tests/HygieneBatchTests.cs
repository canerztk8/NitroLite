using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Core.Hardware.Wmi;
using NitroLite.Linux;
using NitroLite.Linux.Sysfs;
using Xunit;

namespace NitroLite.Tests;

/// <summary>Small hygiene fixes: metadata cap, log sharing, history quarantine, user-name scrub, Linux write gate.</summary>
[Collection("RealAppLog")]
public class HygieneBatchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NitroLiteHyg_" + Guid.NewGuid().ToString("N"));

    public HygieneBatchTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    // ---------------------------------------------------------------- SEC-13

    private static string Big() => "{\"pad\":\"" + new string('a', 2 * 1024 * 1024) + "\"}";

    [Fact]
    public async Task ReleaseMetadata_LargerThanTheCap_IsRejected()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(Big()))
        };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            UpdateService.ReadMetadataAsync(response, CancellationToken.None));
    }

    [Fact]
    public async Task ReleaseMetadata_LargerThanTheCap_IsRejectedEvenWithoutADeclaredLength()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(Big())))
        };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            UpdateService.ReadMetadataAsync(response, CancellationToken.None));
    }

    [Fact]
    public async Task ReleaseMetadata_WithinTheCap_Parses()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"tag_name\":\"v1.2.3\"}")
        };
        using var doc = await UpdateService.ReadMetadataAsync(response, CancellationToken.None);
        Assert.Equal("v1.2.3", doc.RootElement.GetProperty("tag_name").GetString());
    }

    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    [Fact]
    public async Task ReleaseMetadata_StalledBody_TimesOutInsteadOfHanging()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) };

        var started = DateTime.UtcNow;
        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            UpdateService.ReadMetadataAsync(response, TimeSpan.FromMilliseconds(200), CancellationToken.None));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ReleaseMetadata_CallerCancellation_IsStillACancellation()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) };
        using var cts = new CancellationTokenSource(100);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            UpdateService.ReadMetadataAsync(response, TimeSpan.FromSeconds(30), cts.Token));
    }

    // ---------------------------------------------------------------- DATA-12

    [Fact]
    public void PendingMarker_IsWrittenAndCountsUp()
    {
        var install = Path.Combine(_dir, "install");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "NitroLite.exe"), "new");
        File.WriteAllText(Path.Combine(install, "NitroLite.exe.old"), "old");
        var marker = Path.Combine(install, UpdateService.PendingUpdateMarkerName);
        File.WriteAllText(marker, "0");

        Assert.Equal(PendingUpdateState.Pending, UpdateService.RecordStartupAttempt(install));
        Assert.Equal("1", File.ReadAllText(marker).Trim());
        Assert.Empty(Directory.GetFiles(install, "*.tmp*"));
    }

    // ---------------------------------------------------------------- DATA-18

    [Fact]
    public void AppLog_WriterHandle_AllowsTheFileToBeRenamedWhileOpen()
    {
        // Rotation moves the file while another process (the elevated worker) may hold the same log. A handle opened
        // without FileShare.Delete makes that move fail for the whole session.
        AppLog.Info("hygiene-share-probe", "x");
        var log = AppLog.LogFilePath;
        var moved = log + ".probe-" + Guid.NewGuid().ToString("N");

        File.Move(log, moved);
        try
        {
            AppLog.Info("hygiene-share-probe-2", "x");

            // The size check runs once per 64 KB of text. When it notices the file is gone from the path, the writer
            // must reopen it instead of writing into the moved-aside copy for the rest of the session.
            AppLog.Info("hygiene-reopen-probe", new string('x', 70 * 1024));
            AppLog.Info("hygiene-reopen-after", "x");
            Assert.True(File.Exists(log));
            Assert.Contains("hygiene-reopen-after", AppLog.ReadAllText() ?? "");
        }
        finally
        {
            if (!File.Exists(log)) File.Move(moved, log);
            else
            {
                try { File.Delete(moved); } catch { }
            }
        }
    }

    // ---------------------------------------------------------------- DATA-14

    [Fact]
    public void BatteryWearHistory_UnreadableFile_IsSetAsideNotOverwritten()
    {
        var path = Path.Combine(_dir, "battery_wear_history.json");
        File.WriteAllText(path, "{ this is not json");

        var history = new BatteryWearHistory(path);
        Assert.Empty(history.GetSamples());
        history.TryRecord(new BatteryHealthDetails
        {
            DesignCapacityMWh = 90000,
            FullChargeCapacityMWh = 86400,
            HealthPercent = 96,
            WearLevelPercent = 4
        }, new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc));

        var bad = Assert.Single(Directory.GetFiles(_dir, "battery_wear_history.json.bad-*"));
        Assert.Equal("{ this is not json", File.ReadAllText(bad));
        Assert.Single(new BatteryWearHistory(path).GetSamples());
    }

    // ---------------------------------------------------------------- SEC-27

    [Theory]
    [InlineData("client account 'jdoe42' rejected", "client account '<user>' rejected")]
    [InlineData(@"CORP\jdoe42 signed in", @"CORP\<user> signed in")]
    [InlineData("jdoe42@corp.example logged", "<user>@corp.example logged")]
    [InlineData("JDOE42 and jdoe42", "<user> and <user>")]
    [InlineData("notjdoe42 stays", "notjdoe42 stays")]
    [InlineData("jdoe42s stays", "jdoe42s stays")]
    [InlineData("already <user> here", "already <user> here")]
    public void ScrubUserName_ReplacesTheWholeWordAccountName(string input, string expected)
    {
        Assert.Equal(expected, Redaction.ScrubUserName(input, "jdoe42"));
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("")]
    [InlineData(null)]
    public void ScrubUserName_IgnoresAShortOrMissingName(string? name)
    {
        Assert.Equal("ab and ab", Redaction.ScrubUserName("ab and ab", name));
    }

    [Fact]
    public void ScrubUserName_DoesNotRewriteAnExistingPlaceholderForAUserNamedUser()
    {
        Assert.Equal(@"C:\Users\<user>\x", Redaction.ScrubUserName(@"C:\Users\<user>\x", "user"));
    }

    [Theory]
    [InlineData("account 'jdoe42' rejected", "account '<user>' rejected")]
    [InlineData("jdoe42@corp", "<user>@corp")]
    public void Scrub_SlashFreeText_StillScrubsTheAccountName(string input, string expected)
    {
        Assert.Equal(expected, Redaction.Scrub(input, "jdoe42"));
    }

    [Theory]
    [InlineData("Administrator")]
    [InlineData("nitro")]
    [InlineData("Predator")]
    [InlineData("windows")]
    [InlineData("Owner")]
    public void Scrub_AccountNamedLikeACommonWord_IsLeftAlone(string account)
    {
        var text = $"Predator Nitro windows system {account} model";
        Assert.Equal(text, Redaction.Scrub(text, account));
    }

    // ---------------------------------------------------------------- RACE-06 / RACE-08

    private sealed class BlockingFs(InMemorySysfsFileSystem inner) : ISysfsFileSystem
    {
        public ManualResetEventSlim Entered { get; } = new(false);
        public ManualResetEventSlim Release { get; } = new(false);
        public bool Block { get; set; }
        private int _writes;
        public int Writes => Volatile.Read(ref _writes);

        public bool FileExists(string path) => inner.FileExists(path);
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public string? ReadAllText(string path) => inner.ReadAllText(path);
        public bool CanWrite(string path) => inner.CanWrite(path);
        public IReadOnlyList<string> EnumerateDirectories(string path) => inner.EnumerateDirectories(path);
        public IReadOnlyList<string> EnumerateFiles(string path) => inner.EnumerateFiles(path);

        public SysfsWriteResult WriteAllText(string path, string value)
        {
            Interlocked.Increment(ref _writes);
            if (Block)
            {
                Entered.Set();
                Release.Wait(TimeSpan.FromSeconds(20));
            }
            return inner.WriteAllText(path, value);
        }
    }

    private static (LinuxSysfsHardwareController Controller, BlockingFs Fs) BlockingController()
    {
        var mem = new InMemorySysfsFileSystem();
        mem.WithFile("/sys/class/dmi/id/product_name", "Nitro AN16-41\n");
        mem.WithFile("/sys/class/dmi/id/board_name", "AN16-41\n");
        mem.WithFile("/sys/class/dmi/id/sys_vendor", "Acer\n");
        mem.WithFile("/sys/class/dmi/id/bios_version", "V1.27\n");
        mem.WithFile("/sys/class/dmi/id/bios_vendor", "Insyde\n");
        mem.WithFile("/proc/cpuinfo", "vendor_id\t: AuthenticAMD\nmodel name\t: AMD Ryzen 7 7840HS\n");
        mem.WithFile(LinuxPaths.PlatformProfile, "balanced\n");
        mem.WithFile(LinuxPaths.PlatformProfileChoices, "quiet balanced balanced-performance performance\n");
        mem.WithFile("/sys/class/power_supply/BAT1/charge_control_end_threshold", "100\n");
        var sense = LinuxPaths.Combine(LinuxPaths.LinuwuModuleRoot, LinuxPaths.NitroSense);
        mem.WithDirectory(LinuxPaths.AcpiPlatformRoot);
        mem.WithDirectory(sense);
        mem.WithFile(LinuxPaths.Combine(sense, "cool_boost"), "0\n");

        var fs = new BlockingFs(mem);
        var catalog = ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _);
        var caps = new LinuxCapabilityProbe(mem, catalog).Probe();
        var controller = new LinuxSysfsHardwareController(
            fs, caps, new ReadBackPolicy(new AppSettings(), _ => Task.CompletedTask));
        return (controller, fs);
    }

    [Fact]
    public async Task WriteNodeAsync_BlockingHelper_DoesNotBlockTheCaller()
    {
        var (controller, fs) = BlockingController();
        using var owned = controller;
        fs.Block = true;

        var call = controller.SetCoolBoostAsync(true);
        try
        {
            Assert.True(fs.Entered.Wait(TimeSpan.FromSeconds(10)), "the write never reached the file system");
            Assert.False(call.IsCompleted);
        }
        finally
        {
            fs.Release.Set();
        }

        await call;
    }

    [Fact]
    public async Task Dispose_WhileAWriteHoldsTheGate_DoesNotThrowWhenItReleases()
    {
        var (controller, fs) = BlockingController();
        fs.Block = true;

        // SetOperatingModeAsync goes through ApplyAsync, so it holds _writeGate while the file system blocks. If
        // Dispose disposed the gate, the Release in that write's finally would throw ObjectDisposedException.
        var call = controller.SetOperatingModeAsync(OperatingMode.Quiet);
        Assert.True(fs.Entered.Wait(TimeSpan.FromSeconds(10)));

        controller.Dispose();
        fs.Release.Set();

        var ex = await Record.ExceptionAsync(() => call);
        Assert.IsNotType<ObjectDisposedException>(ex);
    }

    [Fact]
    public async Task CoolBoostWrite_WaitsBehindAWriteThatHoldsTheGate()
    {
        var (controller, fs) = BlockingController();
        using var owned = controller;
        fs.Block = true;

        var first = controller.SetOperatingModeAsync(OperatingMode.Quiet);
        Assert.True(fs.Entered.Wait(TimeSpan.FromSeconds(10)));

        var second = controller.SetCoolBoostAsync(true);
        await Task.Delay(300);
        Assert.Equal(1, fs.Writes);
        Assert.False(second.IsCompleted);

        fs.Release.Set();
        await first;
        await second;
        Assert.True(fs.Writes >= 2);
    }
}
