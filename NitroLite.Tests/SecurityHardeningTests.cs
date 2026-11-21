using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using NitroLite.Core;
using NitroLite.Core.Hardware.Wmi;
using NitroLite.Desktop;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Pins the P0 hardening pass: the handshake secret off the command line, the worker authenticating its caller by
/// process id, the task definition that cannot be swapped while the UAC prompt is up, the tool paths that are
/// resolved rather than searched, and the digest that decides whether an elevated helper may be launched.
/// <para>
/// Nothing here registers a real scheduled task, raises a real prompt or launches an elevated process: the process
/// runner, the elevation check, the executable path and the COM registrar are all injected.
/// </para>
/// </summary>
public class SecurityHardeningTests
{
    private const string UserSid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const string InstalledExe = @"C:\Program Files\NitroLite\NitroLite.Desktop.exe";

    // ---------------------------------------------------------------- SEC-01: no secret on the command line

    [Fact]
    public async Task Launcher_HandsTheNonceToThePipeNotTheCommandLine()
    {
        ProcessStartInfo? captured = null;
        string? connectedNonce = null;

        using var launcher = new WmiWorkerLauncher(
            executablePath: InstalledExe,
            start: _ =>
            {
                captured = _;
                return Process.Start(new ProcessStartInfo("cmd.exe", "/c exit")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
            },
            connect: (_, nonce, _, _, _) =>
            {
                connectedNonce = nonce;
                throw new InvalidOperationException("stop here");
            },
            isElevated: () => true,
            isTrustedLocation: _ => true);

        Assert.Null(await launcher.StartAsync());

        Assert.NotNull(captured);
        Assert.DoesNotContain("nonce", captured!.Arguments, StringComparison.OrdinalIgnoreCase);

        // The value still exists; it just travels over the pipe, where only the helper that received it can echo it.
        Assert.NotNull(connectedNonce);
        Assert.True(StartupMode.IsHexNonce(connectedNonce));
    }

    // ---------------------------------------------------------------- SEC-02: no autostart from a temp folder

    [Fact]
    public void UnsafeAutostartLocation_RejectsTempAndDownloads()
    {
        Assert.True(ElevatedStartupService.IsUnsafeAutostartLocation(
            Path.Combine(Path.GetTempPath(), "NitroLite.Desktop.exe")));

        Assert.True(ElevatedStartupService.IsUnsafeAutostartLocation(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads",
                "NitroLite.Desktop.exe")));

        Assert.False(ElevatedStartupService.IsUnsafeAutostartLocation(InstalledExe));
        Assert.False(ElevatedStartupService.IsUnsafeAutostartLocation(null));
    }

    [Fact]
    public async Task Enable_RefusesAnExecutableInATemporaryFolder()
    {
        var runner = new RecordingRunner();
        var service = new ElevatedStartupService(
            runner,
            isElevated: () => false,
            executablePath: () => Path.Combine(Path.GetTempPath(), "NitroLite.Desktop.exe"),
            userId: () => UserSid,
            registerTask: (_, _, _, _) => "not exercised");

        var result = await service.EnableAsync(startMinimized: false);

        Assert.Equal(ElevatedStartupOutcome.Failed, result.Outcome);
        Assert.Contains("temporary", service.LastError, StringComparison.OrdinalIgnoreCase);

        // Nothing may have been registered: the refusal happens before the purge and before the create.
        Assert.DoesNotContain(runner.Calls, c => c.Arguments.Contains("/Create", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- SEC-03: the definition cannot be swapped

    [Fact]
    public async Task Enable_HoldsTheTaskDefinitionSoItCannotBeReplacedDuringThePrompt()
    {
        string? tempPath = null;
        bool writerRefused = false;
        bool contentIntact = false;

        var runner = new CallbackRunner((_, arguments) =>
        {
            if (!arguments.Contains("/Create", StringComparison.Ordinal))
            {
                return new ProcessRunResult(0, "", "", Started: true);
            }

            // While schtasks is "reading" the file, try to replace it the way a same-user attacker would.
            try
            {
                using var write = new FileStream(tempPath!, FileMode.Truncate, FileAccess.Write, FileShare.None);
                write.WriteByte(0x41);
            }
            catch (IOException)
            {
                writerRefused = true;
            }

            contentIntact = File.ReadAllText(tempPath!, Encoding.Unicode)
                .Contains("<RunLevel>HighestAvailable</RunLevel>", StringComparison.Ordinal);

            return new ProcessRunResult(0, "", "", Started: true);
        });

        var service = new ElevatedStartupService(
            runner,
            isElevated: () => false,
            executablePath: () => InstalledExe,
            userId: () => UserSid,
            tempFilePath: () => tempPath ??= Path.Combine(Path.GetTempPath(), $"nitrolite-task-{Guid.NewGuid():N}.xml"),
            registerTask: (_, _, _, _) => "not exercised");

        var result = await service.EnableAsync(startMinimized: false);

        Assert.True(result.Applied);
        Assert.True(writerRefused, "the task definition was writable while schtasks was reading it");
        Assert.True(contentIntact, "the task definition changed before it was registered");
        Assert.False(File.Exists(tempPath));
    }

    [Fact]
    public async Task Enable_WhenAlreadyElevated_RegistersFromMemoryAndWritesNoFile()
    {
        string? tempPath = null;
        bool comCalled = false;

        var runner = new RecordingRunner();
        var service = new ElevatedStartupService(
            runner,
            isElevated: () => true,
            executablePath: () => InstalledExe,
            userId: () => UserSid,
            tempFilePath: () => tempPath ??= Path.Combine(Path.GetTempPath(), $"nitrolite-task-{Guid.NewGuid():N}.xml"),
            registerTask: (name, exe, arguments, userId) =>
            {
                comCalled = true;
                Assert.Equal(ElevatedStartupService.TaskName, name);
                Assert.Equal(InstalledExe, exe);
                Assert.Equal("--autostart", arguments);
                Assert.Equal(UserSid, userId);
                return null;
            });

        var result = await service.EnableAsync(startMinimized: false);

        Assert.True(result.Applied);
        Assert.True(comCalled);

        // The whole point of the COM path: no definition ever reaches the disk.
        Assert.Null(tempPath);
        Assert.DoesNotContain(runner.Calls, c => c.Arguments.Contains("/Create", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Enable_WhenComFails_FallsBackToSchtasks()
    {
        var runner = new RecordingRunner();
        var service = new ElevatedStartupService(
            runner,
            isElevated: () => true,
            executablePath: () => InstalledExe,
            userId: () => UserSid,
            tempFilePath: () => Path.Combine(Path.GetTempPath(), $"nitrolite-task-{Guid.NewGuid():N}.xml"),
            registerTask: (_, _, _, _) => "the COM server refused");

        var result = await service.EnableAsync(startMinimized: false);

        Assert.True(result.Applied);
        Assert.Contains(runner.Calls, c => c.Arguments.Contains("/Create", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- SEC-04: tools are resolved, not searched

    [Fact]
    public void ProcessRunner_ResolvesABareToolNameAgainstSystem32()
    {
        var resolved = ProcessRunner.ResolveExecutable("schtasks.exe");

        Assert.True(Path.IsPathRooted(resolved));
        Assert.Equal(Environment.SystemDirectory, Path.GetDirectoryName(resolved));
        Assert.Equal("schtasks.exe", Path.GetFileName(resolved));
    }

    [Fact]
    public void ProcessRunner_LeavesAnAbsolutePathAlone()
    {
        const string path = @"C:\Tools\custom.exe";
        Assert.Equal(path, ProcessRunner.ResolveExecutable(path));
    }

    [Fact]
    public void ProcessRunner_PinsTheWorkingDirectoryToTheToolFolder()
    {
        var resolved = ProcessRunner.ResolveExecutable("schtasks.exe");
        Assert.Equal(Environment.SystemDirectory, ProcessRunner.WorkingDirectoryFor(resolved));
    }

    // ---------------------------------------------------------------- SEC-05: only a known build is launched

    [Fact]
    public void IsKnownGood_RejectsAnythingThatIsNotTheOfficialBuild()
    {
        string path = Path.Combine(Path.GetTempPath(), $"wmc-{Guid.NewGuid():N}.exe");
        File.WriteAllText(path, "not the real tool");
        try
        {
            Assert.False(MemoryCleanerService.IsKnownGood(path));
            Assert.False(MemoryCleanerService.IsKnownGood(null));
            Assert.False(MemoryCleanerService.IsKnownGood(""));
            Assert.False(MemoryCleanerService.IsKnownGood(Path.Combine(Path.GetTempPath(), "does-not-exist.exe")));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void KnownGoodSha256_IsAWellFormedDigestList()
    {
        Assert.NotEmpty(MemoryCleanerService.KnownGoodSha256);
        foreach (var digest in MemoryCleanerService.KnownGoodSha256)
        {
            Assert.Equal(64, digest.Length);
            Assert.True(StartupMode.IsHexNonce(digest));
        }
    }

    [Fact]
    public void FindVerifiedTool_SkipsACopyThatIsNotAKnownBuild()
    {
        // The data folder copy wins the lookup order, but only a recognised digest may be launched.
        string folder = Path.Combine(AppPaths.DataDirectory, "tools");
        string portable = Path.Combine(folder, MemoryCleanerService.ToolFileName);
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(portable, "tampered");

            Assert.Equal(portable, MemoryCleanerService.FindTool());
            Assert.NotEqual(portable, MemoryCleanerService.FindVerifiedTool());
        }
        finally
        {
            try { File.Delete(portable); } catch { }
        }
    }

    [Fact]
    public async Task CleanMemory_RefusesACopyThatIsNotAKnownBuild()
    {
        string folder = Path.Combine(AppPaths.DataDirectory, "tools");
        string portable = Path.Combine(folder, MemoryCleanerService.ToolFileName);
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(portable, "tampered");

            // A real install elsewhere on the machine would be a legitimate answer, so only assert when there is
            // nothing else to fall back to.
            if (MemoryCleanerService.FindVerifiedTool() != null) return;

            var result = await new MemoryCleanerService().CleanMemoryAsync();

            Assert.True(result.ToolUntrusted);
            Assert.False(result.ToolMissing);
            Assert.False(result.Cleaned);
        }
        finally
        {
            try { File.Delete(portable); } catch { }
        }
    }

    // ---------------------------------------------------------------- SEC-06: no elevated helper from a shared folder

    [Fact]
    public async Task Launcher_RefusesAnExecutableInAFolderAnyAccountCanWrite()
    {
        bool started = false;

        using var launcher = new WmiWorkerLauncher(
            executablePath: @"C:\ProgramData\NitroLite\NitroLite.Desktop.exe",
            start: _ =>
            {
                started = true;
                return null;
            },
            isElevated: () => false,
            isTrustedLocation: _ => false);

        Assert.Null(await launcher.StartAsync());
        Assert.False(started);
        Assert.Contains("any local account", launcher.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, launcher.Attempts);
    }

    [Fact]
    public void TrustedHelperLocation_AcceptsAPortableInstallUnderTheProfile()
    {
        // A portable or Scoop build lives under the profile. Refusing it would break every build that does not ship
        // an installer, which is why the check is "can another account write here", not "is this Program Files".
        string portable = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NitroLite", "NitroLite.Desktop.exe");

        Assert.True(WmiWorkerLauncher.IsTrustedHelperLocation(portable));
    }

    [Fact]
    public void IsExecutableTrusted_ProfilePathIsTrusted_EmptyIsNot()
    {
        string profile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "NitroLite", "x.exe");
        Assert.True(DirectoryTrust.IsExecutableTrusted(profile));
        Assert.False(DirectoryTrust.IsExecutableTrusted(null));
        Assert.False(DirectoryTrust.IsExecutableTrusted(""));
    }

    [Fact]
    public void IsExecutableTrusted_RefusesAFileEveryoneCanOverwrite()
    {
        // A folder outside the profile (ProgramData is creatable by every user) with an explicit
        // "Everyone: full control" rule on the executable is exactly the swap-the-binary case.
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "NitroLiteTrustTest-" + Guid.NewGuid().ToString("N"));
        string exe = Path.Combine(dir, "NitroLite.exe");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(exe, "MZ");

            var fileSecurity = new FileInfo(exe).GetAccessControl(AccessControlSections.Access);
            fileSecurity.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            new FileInfo(exe).SetAccessControl(fileSecurity);

            Assert.False(DirectoryTrust.IsExecutableTrusted(exe));
            Assert.False(WmiWorkerLauncher.IsTrustedHelperLocation(exe));
        }
        finally
        {
            try { File.Delete(exe); Directory.Delete(dir); } catch { }
        }
    }

    [Fact]
    public void IsExecutableTrusted_AnExistingFileTheUserCreatedIsTrusted()
    {
        // What this models is a file the signed-in user created in a folder outside the profile whose ACL hands the
        // creator full control of its own files (the ProgramData default): nothing wider than the user can replace it,
        // so the answer is yes. It is deliberately not the C:\WinTools case — see the drive-root test below, where the
        // inherited Authenticated Users:Modify makes that install refused.
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "NitroLiteTrustTest-" + Guid.NewGuid().ToString("N"));
        string exe = Path.Combine(dir, "NitroLite.exe");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(exe, "MZ");
            Assert.True(DirectoryTrust.IsExecutableTrusted(exe));
            Assert.True(WmiWorkerLauncher.IsTrustedHelperLocation(exe));
        }
        finally
        {
            try { File.Delete(exe); Directory.Delete(dir); } catch { }
        }
    }

    [Fact]
    public void IsExecutableTrusted_RefusesAFolderCreatedDirectlyUnderADriveRoot()
    {
        // The install path from the AN16-61 report: a folder created directly under C:\. On a stock Windows the drive
        // root carries "Authenticated Users:(OI)(CI)(IO)(M)", so every file created beneath it inherits Modify and any
        // local account can replace the binary. The test builds that inherited right explicitly rather than writing to
        // C:\, because a test may not touch the drive root.
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "NitroLiteTrustTest-" + Guid.NewGuid().ToString("N"));
        string exe = Path.Combine(dir, "NitroLite.exe");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(exe, "MZ");

            var fileSecurity = new FileInfo(exe).GetAccessControl(AccessControlSections.Access);
            fileSecurity.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                FileSystemRights.Modify,
                AccessControlType.Allow));
            new FileInfo(exe).SetAccessControl(fileSecurity);

            Assert.False(DirectoryTrust.IsExecutableTrusted(exe));
            Assert.False(WmiWorkerLauncher.IsTrustedHelperLocation(exe));
        }
        finally
        {
            try { File.Delete(exe); Directory.Delete(dir); } catch { }
        }
    }

    [Fact]
    public async Task Launcher_LogsARefusedLocationOnceAndDoesNotRecheckItEveryTick()
    {
        // The launcher is asked again on every hardware read. Re-evaluating the ACLs and printing the same warning
        // each time is what turned the AN16-61 report into a page of identical lines, so the refusal is remembered.
        int checks = 0;
        string exe = Path.Combine(@"C:\ProgramData\NitroLite-" + Guid.NewGuid().ToString("N"), "NitroLite.exe");
        string before = AppLog.ReadAllText() ?? "";

        using var launcher = new WmiWorkerLauncher(
            executablePath: exe,
            start: _ => null,
            isElevated: () => false,
            isTrustedLocation: _ => { checks++; return false; });

        Assert.Null(await launcher.StartAsync());
        Assert.Null(await launcher.StartAsync());
        Assert.Equal(1, checks);
        Assert.Equal(0, launcher.Attempts);
        Assert.Contains("any local account", launcher.LastError, StringComparison.OrdinalIgnoreCase);

        string after = AppLog.ReadAllText() ?? "";
        string added = after.Length > before.Length ? after[before.Length..] : after;
        Assert.Equal(1, added.Split(exe).Length - 1);
    }

    [Fact]
    public async Task AppLog_KeepsEveryLineWhenManyThreadsWriteAtOnce()
    {
        // The writer is shared and the lock is the only thing serialising it. A lost line is a line missing from the
        // report that explains a crash, so the count is asserted rather than "it did not throw": Write swallows its
        // own exceptions, which means a race shows up as a missing line and nothing else.
        string marker = "concurrency-" + Guid.NewGuid().ToString("N");
        const int threads = 8;
        const int perThread = 50;

        await Task.WhenAll(Enumerable.Range(0, threads).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < perThread; i++) AppLog.Info(marker, $"{t}-{i}");
        })));

        AppLog.Flush();

        // The log rotates at a megabyte, and a rotation moves the earlier lines to .old. Both are read so the count
        // does not depend on how full the log happened to be when the test started.
        string text = AppLog.ReadAllText() ?? "";
        string rotated = AppLog.LogFilePath + ".old";
        if (File.Exists(rotated)) text += File.ReadAllText(rotated);

        Assert.Equal(threads * perThread, text.Split(marker).Length - 1);
    }

    [Fact]
    public async Task WmiWorkerClient_RefusesAPipeServedByAnotherProcess()
    {
        // The nonce proves the peer received the hello, not that it is the helper that was started: the pipe name is
        // on the worker's command line, which every process on the machine can read, so a same-user process can
        // create the pipe first and echo the value back. The kernel's answer for "who is serving this pipe" cannot
        // be forged by the process on that end, so it is what decides.
        string pipeName = "nitrolite-test-" + Guid.NewGuid().ToString("N");
        string nonce = WmiWorkerClient.GenerateNonce();

        using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        var serving = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            var hello = await WmiWorkerProtocol.ReadRequestAsync(server);
            await WmiWorkerProtocol.WriteAsync(server, new WmiWorkerResponse
            {
                Type = WmiWorkerProtocol.TypeWelcome,
                Ok = true,
                Nonce = hello.Request?.Nonce ?? ""
            });
        });

        // This test process is the server, so any other id is a different process.
        int otherPid = Environment.ProcessId == 4 ? 5 : 4;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WmiWorkerClient.ConnectAsync(pipeName, nonce, TimeSpan.FromSeconds(5), otherPid));

        Assert.Contains("not the helper that was started", ex.Message);
        await serving;
    }

    [Fact]
    public async Task WmiWorkerClient_AcceptsAPipeServedByTheProcessItStarted()
    {
        // The other half: the check must not refuse the real helper, or the elevated path would never connect.
        string pipeName = "nitrolite-test-" + Guid.NewGuid().ToString("N");
        string nonce = WmiWorkerClient.GenerateNonce();

        using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        var serving = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            var hello = await WmiWorkerProtocol.ReadRequestAsync(server);
            await WmiWorkerProtocol.WriteAsync(server, new WmiWorkerResponse
            {
                Type = WmiWorkerProtocol.TypeWelcome,
                Ok = true,
                Nonce = hello.Request?.Nonce ?? ""
            });
        });

        using var client = await WmiWorkerClient.ConnectAsync(
            pipeName, nonce, TimeSpan.FromSeconds(5), Environment.ProcessId);

        Assert.NotNull(client);
        await serving;
    }

    [Fact]
    public void DirectoryTrust_RecognisesTheProfileAndNotProgramFiles()
    {
        string profile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "NitroLite", "x.exe");

        Assert.True(DirectoryTrust.IsUnderUserProfile(profile));
        Assert.False(DirectoryTrust.IsUnderUserProfile(InstalledExe));
        Assert.False(DirectoryTrust.IsUnderUserProfile(null));
    }

    // ---------------------------------------------------------------- SEC-29: the per-session fallback

    [Fact]
    public void SingleInstanceGuard_SecondAcquireInTheSameProcessIsNotTheFirst()
    {
        using var first = SingleInstanceGuard.Acquire();

        // A real NitroLite owns the slot on a developer machine; there is nothing to assert in that case.
        if (!first.IsFirstInstance) return;

        using var second = SingleInstanceGuard.Acquire();
        Assert.False(second.IsFirstInstance);
    }

    // ---------------------------------------------------------------- SEC-30: a launch that the pipe refuses

    [Fact]
    public async Task WakeFile_BringsTheRunningWindowForwardWhenThePipeIsRefused()
    {
        // An elevated NitroLite owns a pipe a normal launch may not write to: "Access is denied" for every icon click
        // and no window. The wake file is the second path, and it is polled once a second.
        using var guard = SingleInstanceGuard.Acquire();
        var woken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        guard.ActivationRequested += () => woken.TrySetResult();

        guard.WatchForWakeRequests();

        Assert.True(SingleInstanceGuard.PostWakeRequest());
        // WaitAsync rather than WhenAny with a Delay: the timeout's timer is released as soon as the wake arrives, and a
        // missed wake fails the test with a TimeoutException that says so.
        await woken.Task.WaitAsync(TimeSpan.FromSeconds(8));

        Assert.True(woken.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public void WakeFile_AnsweredOnceIsNotOfferedAgain()
    {
        Assert.True(SingleInstanceGuard.PostWakeRequest());

        var stamp = SingleInstanceGuard.PendingWakeRequest(0);

        Assert.NotNull(stamp);
        // The window is already on its way forward; a second raise would fight the user for the foreground.
        Assert.Null(SingleInstanceGuard.PendingWakeRequest(stamp.Value));
    }

    [Fact]
    public void WakeFile_FromBeforeThisInstanceStartedIsIgnored()
    {
        Assert.True(SingleInstanceGuard.PostWakeRequest());

        // A request left by an earlier run belongs to a window that has already been answered, or to nothing at all.
        var later = DateTime.UtcNow.Ticks + TimeSpan.TicksPerMinute;

        Assert.Null(SingleInstanceGuard.PendingWakeRequest(later));
    }

    // ---------------------------------------------------------------- helpers

    private sealed class RecordingRunner : IProcessRunner
    {
        public List<(string FileName, string Arguments, bool Elevate)> Calls { get; } = new();

        public Task<ProcessRunResult> RunAsync(
            string fileName, string arguments, bool elevate, CancellationToken cancellationToken = default)
        {
            Calls.Add((fileName, arguments, elevate));
            return Task.FromResult(new ProcessRunResult(0, "", "", Started: true));
        }
    }

    private sealed class CallbackRunner : IProcessRunner
    {
        private readonly Func<string, string, ProcessRunResult> _answer;

        public CallbackRunner(Func<string, string, ProcessRunResult> answer) => _answer = answer;

        public List<(string FileName, string Arguments, bool Elevate)> Calls { get; } = new();

        public Task<ProcessRunResult> RunAsync(
            string fileName, string arguments, bool elevate, CancellationToken cancellationToken = default)
        {
            Calls.Add((fileName, arguments, elevate));
            return Task.FromResult(_answer(fileName, arguments));
        }
    }
}
