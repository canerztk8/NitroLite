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
/// </summary>
public sealed record StartupMode
{
    /// <summary>Shortest nonce the helper accepts. 32 hex characters is 128 bits, which is what the parent generates.</summary>
    public const int MinNonceLength = 32;

    public StartupModeKind Kind { get; init; } = StartupModeKind.Normal;

    /// <summary>Name of the named pipe the helper connects to. Empty unless this is the helper.</summary>
    public string PipeName { get; init; } = "";

    /// <summary>Process id of the window that started the helper. 0 when absent or unparsable.</summary>
    public int ParentProcessId { get; init; }

    /// <summary>Single-use handshake value the parent generated. Empty unless this is the helper.</summary>
    public string Nonce { get; init; } = "";

    public bool IsWmiWorker => Kind == StartupModeKind.WmiWorker;

    /// <summary>
    /// True when the helper was given everything it needs to be safe to run: a pipe to talk on, a parent to watch,
    /// and a nonce long enough to be a real secret. A helper started without these would be an elevated process
    /// with no owner and no way to authenticate its caller, so it refuses to start instead.
    /// </summary>
    public bool IsValidWorkerRequest =>
        IsWmiWorker
        && !string.IsNullOrWhiteSpace(PipeName)
        && ParentProcessId > 0
        && IsHexNonce(Nonce);

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
        string nonce = "";

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
            if (TryReadValue(args, ref i, arg, "--pipe", out var pipeValue)) { pipe = pipeValue; continue; }
            if (TryReadValue(args, ref i, arg, "--nonce", out var nonceValue)) { nonce = nonceValue; continue; }
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
            ParentProcessId = parentPid,
            Nonce = nonce
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
            // A following flag is not this flag's value: "--pipe --nonce abc" must leave the pipe empty rather than
            // name it "--nonce", which would then be used as a pipe name.
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
