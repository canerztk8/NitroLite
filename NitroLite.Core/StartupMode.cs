namespace NitroLite.Core;

/// <summary>Which of the two roles this process was started in.</summary>
public enum StartupModeKind
{
    /// <summary>The window, the tray icon and the telemetry loop.</summary>
    Normal = 0,

    /// <summary>
    /// The elevated helper that performs WMI writes on behalf of the window. It has no UI, no tray icon and no
    /// telemetry loop of its own.
    /// </summary>
    WmiWorker = 1
}

/// <summary>
/// What the command line asked this process to be.
/// <para>
/// This is parsed before the single-instance mutex, and that order is the whole point. The WMI helper is a second
/// process of the same executable, so the mutex would classify it as a duplicate: it would signal the running window
/// to come forward and exit, and the caller would then wait on a named pipe that nobody is listening to. Keeping the
/// decision in a pure method makes that ordering testable without starting a process.
/// </para>
/// <para>
/// The handshake secret is deliberately absent. It used to travel as <c>--nonce</c>, which put it in
/// <c>Win32_Process.CommandLine</c> where any process running as the same user could read it and then drive the
/// elevated helper. It now travels over the pipe itself, and the worker authenticates its caller by process id
/// instead — a value the caller cannot forge.
/// </para>
/// </summary>
public sealed record StartupMode
{
    /// <summary>Shortest nonce the handshake accepts. 32 hex characters is 128 bits, which is what the parent generates.</summary>
    public const int MinNonceLength = 32;

    public StartupModeKind Kind { get; init; } = StartupModeKind.Normal;

    /// <summary>Name of the named pipe the helper connects to. Empty unless this is the helper.</summary>
    public string PipeName { get; init; } = "";

    /// <summary>Process id of the window that started the helper. 0 when absent or unparsable.</summary>
    public int ParentProcessId { get; init; }

    public bool IsWmiWorker => Kind == StartupModeKind.WmiWorker;

    /// <summary>
    /// True when the helper was given everything it needs to be safe to run: a pipe to talk on and a parent whose
    /// process id it can check every connection against. A helper started without these would be an elevated process
    /// with no owner and no way to authenticate its caller, so it refuses to start instead.
    /// </summary>
    public bool IsValidWorkerRequest =>
        IsWmiWorker
        && IsValidPipeName(PipeName)
        && ParentProcessId > 0;

    /// <summary>Longest pipe name the helper accepts. The launcher's names are about 50 characters.</summary>
    public const int MaxPipeNameLength = 128;

    /// <summary>
    /// True when the name is a plain pipe name: 1..<see cref="MaxPipeNameLength"/> characters of ASCII letters,
    /// digits, '-', '_' and '.'. No separators, so the value cannot name a path, another namespace
    /// (<c>\\.\pipe\…</c>, <c>\\?\…</c>) or a pipe on another machine.
    /// </summary>
    public static bool IsValidPipeName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxPipeNameLength) return false;
        foreach (var c in name)
        {
            bool ok = c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.';
            if (!ok) return false;
        }

        return true;
    }

    /// <summary>
    /// Reads the role out of the command line. Unknown arguments are ignored: this only answers "am I the helper",
    /// and the rest of the command line belongs to <see cref="StartupOptions"/> and the CLI commands.
    /// </summary>
    public static StartupMode Parse(string[]? args)
    {
        if (args == null || args.Length == 0) return new StartupMode();

        bool isWorker = false;
        string pipe = "";
        int parentPid = 0;

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i]?.Trim();
            if (string.IsNullOrEmpty(arg)) continue;

            if (arg.Equals("--wmi-worker", StringComparison.OrdinalIgnoreCase))
            {
                isWorker = true;
                continue;
            }

            // Both "--pipe name" and "--pipe=name" are accepted: a launcher written by hand tends to use the first
            // form, a generated command line the second.
            // One pair of surrounding quotes is dropped: the launcher quotes the name, and a caller that splits the
            // command line itself (rather than letting Windows do it) hands the quotes through. The allow-list then
            // still refuses any quote left inside the name.
            if (TryReadValue(args, ref i, arg, "--pipe", out var pipeValue)) { pipe = StripQuotes(pipeValue); continue; }
            if (TryReadValue(args, ref i, arg, "--parent-pid", out var pidValue))
            {
                parentPid = int.TryParse(pidValue, out var parsed) && parsed > 0 ? parsed : 0;
            }
        }

        if (!isWorker) return new StartupMode();

        return new StartupMode
        {
            Kind = StartupModeKind.WmiWorker,
            PipeName = pipe,
            ParentProcessId = parentPid
        };
    }

    /// <summary>
    /// Reads the value of <paramref name="name"/>, either as the next argument or after an <c>=</c>. Advances
    /// <paramref name="index"/> past a consumed value so the loop does not read it again as a flag.
    /// </summary>
    private static bool TryReadValue(string[] args, ref int index, string arg, string name, out string value)
    {
        value = "";

        if (arg.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            // A following flag is not this flag's value: "--pipe --parent-pid 7" must leave the pipe empty rather
            // than name it "--parent-pid", which would then be used as a pipe name.
            if (index + 1 >= args.Length) return true;
            var next = args[index + 1]?.Trim() ?? "";
            if (next.StartsWith("--", StringComparison.Ordinal)) return true;
            value = next;
            index++;
            return true;
        }

        if (arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
        {
            value = arg[(name.Length + 1)..].Trim();
            return true;
        }

        return false;
    }

    private static string StripQuotes(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;

    /// <summary>True when the text is at least <see cref="MinNonceLength"/> characters of plain hexadecimal.</summary>
    public static bool IsHexNonce(string? nonce)
    {
        if (string.IsNullOrEmpty(nonce) || nonce.Length < MinNonceLength) return false;
        foreach (var c in nonce)
        {
            bool hex = c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!hex) return false;
        }

        return true;
    }
}
