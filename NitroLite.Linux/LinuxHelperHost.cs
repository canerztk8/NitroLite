using System.Globalization;
using System.Net.Sockets;
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

        try
        {
            var fs = RealSysfsFileSystem.Instance;
            var capabilities = new LinuxCapabilityProbe(fs).Probe();
            var policy = LinuxHelperPolicy.For(capabilities, fs);
            Console.Error.WriteLine($"nitrolite-helper: {policy.Paths.Count} settings allowed for {capabilities.Identity.ModelCode}");

            using var listener = OpenListener(args);
            if (listener == null)
            {
                Console.Error.WriteLine("nitrolite-helper: no socket (start it through nitrolite-helper.socket, or pass --socket PATH)");
                return 2;
            }

            using var server = new LinuxHelperServer(listener, policy, fs, new PolkitCallerAuthorizer());
            server.RunAsync().GetAwaiter().GetResult();
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
        if (File.Exists(path)) File.Delete(path);

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(path));
        socket.Listen(8);
        return socket;
    }

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
