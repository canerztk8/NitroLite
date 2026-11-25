using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Linux.Sysfs;

namespace NitroLite.Linux;

/// <summary>
/// <c>NitroLite --linux-helper</c>: the root helper's entry point, run by systemd from <c>nitrolite-helper.socket</c>.
/// </summary>
/// <remarks>
/// <para>
/// systemd owns the listening socket (<c>/run/nitrolite/helper.sock</c>) and hands it over as file descriptor 3 when
/// the first client connects; the helper serves, and exits after half a minute of silence. <c>--socket PATH</c> binds a
/// socket of its own instead, for running the helper by hand while developing.
/// </para>
/// <para>
/// The probe runs here, as root, to build the list of attributes the helper may write. Nothing a client sends can add
/// to that list.
/// </para>
/// </remarks>
public static class LinuxHelperHost
{
    private const int SystemdFirstFd = 3;

    public static int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        // systemctl stop/restart (a package upgrade, removal, or the uninstaller) sends SIGTERM. Without a handler the
        // runtime exits at once and the server's finally — the one that hands a manual fan back to the firmware — never
        // runs; the next helper starts with no memory of who owned the fans, so a fan left manual would stay manual.
        // The handler cancels the server instead, and the process exits normally once the hand-back is done.
        using var stop = new CancellationTokenSource();
        void Stop(PosixSignalContext context)
        {
            context.Cancel = true;
            try
            {
                stop.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already on the way out.
            }
        }

        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Stop);
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, Stop);

        try
        {
            var fs = RealSysfsFileSystem.Instance;
            var catalog = ProfileCatalog.Load();
            var capabilities = new LinuxCapabilityProbe(fs, catalog).Probe();
            var policy = LinuxHelperPolicy.For(capabilities, fs);
            Console.Error.WriteLine($"nitrolite-helper: {policy.Paths.Count} settings allowed for {capabilities.Identity.ModelCode}");

            using var listener = OpenListener(args);
            if (listener == null)
            {
                Console.Error.WriteLine("nitrolite-helper: no socket (start it through nitrolite-helper.socket, or pass --socket PATH)");
                return 2;
            }

            // The same probe, as root, again on a later "list": an attribute that appeared after start-up (a module
            // loaded, a driver that bound late) becomes writable on the app's rescan without restarting the helper.
            using var server = new LinuxHelperServer(listener, policy, fs, new PolkitCallerAuthorizer(),
                refreshPolicy: () => LinuxHelperPolicy.For(new LinuxCapabilityProbe(fs, catalog).Probe(), fs));
            server.RunAsync(stop.Token).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("nitrolite-helper: " + ex.Message);
            AppLog.Error("The NitroLite helper stopped", ex.ToString());
            return 1;
        }
    }

    /// <summary>The socket systemd passed, or one bound at <c>--socket PATH</c>, or null.</summary>
    private static Socket? OpenListener(string[] args)
    {
        if (IsSocketActivated())
        {
            return new Socket(new SafeSocketHandle(SystemdFirstFd, ownsHandle: true));
        }

        var index = Array.IndexOf(args, "--socket");
        if (index < 0 || index + 1 >= args.Length) return null;

        var path = args[index + 1];

        // This runs as root, and the path is typed by hand: only a stale socket from an earlier run is removed. A
        // regular file, a directory, a device or a symbolic link at that path is refused rather than deleted.
        switch (SocketPathState(path))
        {
            case PathKind.Missing:
                break;
            case PathKind.Socket:
                File.Delete(path);
                break;
            default:
                Console.Error.WriteLine($"nitrolite-helper: refusing --socket {path}: something other than a socket is there");
                return null;
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(path));
        socket.Listen(8);
        return socket;
    }

    internal enum PathKind
    {
        Missing,
        Socket,
        Other
    }

    /// <summary>
    /// What is at <paramref name="path"/>, without following a final symbolic link (<c>lstat</c>). Anything that cannot
    /// be determined is <see cref="PathKind.Other"/>, so the caller refuses it.
    /// </summary>
    internal static PathKind SocketPathState(string path)
    {
        if (!OperatingSystem.IsLinux()) return File.Exists(path) || Directory.Exists(path) ? PathKind.Other : PathKind.Missing;

        // struct stat on x86-64 keeps st_mode at byte 24; the generic layout arm64 uses keeps it at 16.
        var modeOffset = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => 24,
            Architecture.Arm64 => 16,
            _ => -1
        };
        if (modeOffset < 0) return PathKind.Other;

        var buffer = new byte[256];
        int result;
        try
        {
            result = LStat(path, buffer);
        }
        catch (EntryPointNotFoundException)
        {
            // glibc before 2.33 has no lstat symbol, only __lxstat with the structure version first (1 on x86-64, 0 on
            // arm64).
            try
            {
                result = LXStat(RuntimeInformation.ProcessArchitecture == Architecture.X64 ? 1 : 0, path, buffer);
            }
            catch (EntryPointNotFoundException)
            {
                return PathKind.Other;
            }
        }

        if (result != 0)
        {
            return Marshal.GetLastPInvokeError() == ENoEnt ? PathKind.Missing : PathKind.Other;
        }

        var mode = BitConverter.ToUInt32(buffer, modeOffset);
        return (mode & SIfMt) == SIfSock ? PathKind.Socket : PathKind.Other;
    }

    private const int ENoEnt = 2;
    private const uint SIfMt = 0xF000;    // S_IFMT
    private const uint SIfSock = 0xC000;  // S_IFSOCK

    [DllImport("libc", EntryPoint = "lstat", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int LStat([MarshalAs(UnmanagedType.LPStr)] string path, byte[] buffer);

    [DllImport("libc", EntryPoint = "__lxstat", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int LXStat(int version, [MarshalAs(UnmanagedType.LPStr)] string path, byte[] buffer);

    /// <summary>systemd's <c>LISTEN_PID</c>/<c>LISTEN_FDS</c> protocol: one descriptor, meant for this process.</summary>
    private static bool IsSocketActivated()
    {
        var pid = Environment.GetEnvironmentVariable("LISTEN_PID");
        var fds = Environment.GetEnvironmentVariable("LISTEN_FDS");
        return int.TryParse(pid, NumberStyles.Integer, CultureInfo.InvariantCulture, out var listenPid) &&
               listenPid == Environment.ProcessId &&
               int.TryParse(fds, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count >= 1;
    }
}
