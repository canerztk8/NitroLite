using System.Text.Json;
using NitroLite.Core;

namespace NitroLite.Linux;

/// <summary>
/// The desktop's own power-profile service: <c>power-profiles-daemon</c>, or <c>tuned-ppd</c>, which offers the same
/// D-Bus interface.
/// </summary>
/// <remarks>
/// <para>
/// This is what lets an unprivileged user change the operating mode with nothing installed. Ubuntu, Fedora, Mint, Pop
/// and most desktop distributions ship one of the two, and both own <c>/sys/firmware/acpi/platform_profile</c>: they
/// write it as root and polkit lets the active session ask them to, with no password. Writing the attribute directly
/// needs the udev rule; asking the daemon does not.
/// </para>
/// <para>
/// The bus is reached through <c>busctl</c>, which every systemd distribution has, so the app carries no D-Bus library.
/// <c>powerprofilesctl</c> is the fallback for a machine without <c>busctl</c>. The daemon offers three profiles —
/// <c>power-saver</c>, <c>balanced</c>, <c>performance</c> — which is coarser than the kernel's list, so a writable
/// <c>platform_profile</c> is still preferred when the udev rule is installed.
/// </para>
/// </remarks>
public interface IPowerProfiles
{
    /// <summary>True when a daemon answered on the system bus.</summary>
    bool IsAvailable { get; }

    /// <summary>The profiles the daemon offers, in its own names. Empty when it did not answer.</summary>
    IReadOnlyList<string> Profiles { get; }

    /// <summary>The active profile, or null when the daemon did not answer.</summary>
    string? GetActive();

    /// <summary>Asks the daemon to switch. True when it accepted the request.</summary>
    bool SetActive(string profile);
}

/// <summary>The real client, over <c>busctl</c> with <c>powerprofilesctl</c> as the fallback.</summary>
public sealed class PowerProfilesDaemon : IPowerProfiles
{
    public const string PowerSaver = "power-saver";
    public const string Balanced = "balanced";
    public const string Performance = "performance";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// What the start-up probe may spend in total. It runs while the first window is being built, and a system bus
    /// that does not answer would otherwise cost every timeout in turn — two endpoints, two forms each, then the CLI.
    /// </summary>
    private static readonly TimeSpan ProbeBudget = TimeSpan.FromSeconds(4);

    /// <summary>The current name (0.20 and later, and tuned-ppd), then the original one.</summary>
    private static readonly (string Service, string Path, string Interface)[] Endpoints =
    {
        ("org.freedesktop.UPower.PowerProfiles", "/org/freedesktop/UPower/PowerProfiles", "org.freedesktop.UPower.PowerProfiles"),
        ("net.hadess.PowerProfiles", "/net/hadess/PowerProfiles", "net.hadess.PowerProfiles")
    };

    private readonly ICommandRunner _runner;
    private readonly string? _busctl;
    private readonly string? _ctl;
    private readonly (string Service, string Path, string Interface)? _endpoint;

    public PowerProfilesDaemon(ICommandRunner? runner = null)
    {
        _runner = runner ?? ProcessCommandRunner.Instance;
        _busctl = _runner.Find("busctl");
        _ctl = _runner.Find("powerprofilesctl");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        if (_busctl != null)
        {
            foreach (var endpoint in Endpoints)
            {
                if (clock.Elapsed > ProbeBudget) break;
                var active = ReadActive(endpoint, () => clock.Elapsed <= ProbeBudget);
                if (active == null) continue;

                _endpoint = endpoint;
                Profiles = ReadProfiles(endpoint);
                break;
            }
        }

        if (_endpoint == null && _ctl != null && clock.Elapsed <= ProbeBudget)
        {
            var result = _runner.Run(_ctl, GetArgs, Timeout);
            if (result.Ok && !string.IsNullOrWhiteSpace(result.Output)) Profiles = DefaultProfiles;
        }

        IsAvailable = Profiles.Count > 0;
    }

    private static readonly string[] DefaultProfiles = { PowerSaver, Balanced, Performance };

    private static readonly string[] GetArgs = { "get" };

    public bool IsAvailable { get; }

    public IReadOnlyList<string> Profiles { get; } = Array.Empty<string>();

    public string? GetActive()
    {
        if (_endpoint is { } endpoint) return ReadActive(endpoint);
        if (_ctl == null) return null;

        var result = _runner.Run(_ctl, GetArgs, Timeout);
        return result.Ok && !string.IsNullOrWhiteSpace(result.Output) ? result.Output.Trim() : null;
    }

    public bool SetActive(string profile)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(profile)) return false;

        CommandResult result;
        if (_endpoint is { } endpoint)
        {
            result = _runner.Run(_busctl!, new[]
            {
                "--system", "set-property", endpoint.Service, endpoint.Path, endpoint.Interface, "ActiveProfile", "s", profile
            }, Timeout);
        }
        else
        {
            result = _runner.Run(_ctl!, new[] { "set", profile }, Timeout);
        }

        if (!result.Ok) AppLog.Warn("The power profile daemon refused the change", $"{profile}: {result.Error}");
        return result.Ok;
    }

    private string? ReadActive((string Service, string Path, string Interface) endpoint, Func<bool>? mayContinue = null)
    {
        var json = _runner.Run(_busctl!, new[]
        {
            "--system", "--json=short", "get-property", endpoint.Service, endpoint.Path, endpoint.Interface, "ActiveProfile"
        }, Timeout);
        if (json.Ok)
        {
            var value = ParseJsonString(json.Output);
            if (value != null) return value;
        }

        // systemd before 240 has no --json; its plain form is `s "balanced"`.
        if (mayContinue != null && !mayContinue()) return null;
        var plain = _runner.Run(_busctl!, new[]
        {
            "--system", "get-property", endpoint.Service, endpoint.Path, endpoint.Interface, "ActiveProfile"
        }, Timeout);
        return plain.Ok ? ParsePlainString(plain.Output) : null;
    }

    private IReadOnlyList<string> ReadProfiles((string Service, string Path, string Interface) endpoint)
    {
        var json = _runner.Run(_busctl!, new[]
        {
            "--system", "--json=short", "get-property", endpoint.Service, endpoint.Path, endpoint.Interface, "Profiles"
        }, Timeout);

        var profiles = json.Ok ? ParseProfiles(json.Output) : Array.Empty<string>();
        return profiles.Count > 0 ? profiles : DefaultProfiles;
    }

    /// <summary><c>{"type":"s","data":"balanced"}</c> to <c>balanced</c>. Null for anything else.</summary>
    public static string? ParseJsonString(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        try
        {
            using var doc = JsonDocument.Parse(output);
            return doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String
                ? data.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary><c>s "balanced"</c> to <c>balanced</c>. Null for anything else.</summary>
    public static string? ParsePlainString(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var text = output.Trim();
        if (!text.StartsWith("s ", StringComparison.Ordinal)) return null;

        var value = text[2..].Trim().Trim('"');
        return value.Length == 0 ? null : value;
    }

    /// <summary>The <c>Profile</c> names out of the <c>aa{sv}</c> the daemon publishes.</summary>
    public static IReadOnlyList<string> ParseProfiles(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return Array.Empty<string>();
        try
        {
            using var doc = JsonDocument.Parse(output);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            var names = new List<string>();
            foreach (var entry in data.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                if (!entry.TryGetProperty("Profile", out var profile)) continue;

                var name = profile.ValueKind switch
                {
                    JsonValueKind.String => profile.GetString(),
                    JsonValueKind.Object when profile.TryGetProperty("data", out var inner) &&
                                              inner.ValueKind == JsonValueKind.String => inner.GetString(),
                    _ => null
                };

                if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name)) names.Add(name);
            }

            return names;
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    // ---------------------------------------------------------------- mode mapping

    /// <summary>The daemon's three profiles as NitroLite modes, in the order the UI lists them.</summary>
    public static IReadOnlyList<OperatingMode> ToModes(IReadOnlyList<string> profiles)
    {
        var modes = new List<OperatingMode>();
        if (profiles.Contains(PowerSaver)) modes.Add(OperatingMode.Quiet);
        if (profiles.Contains(Balanced)) modes.Add(OperatingMode.Default);
        if (profiles.Contains(Performance)) modes.Add(OperatingMode.Turbo);
        return modes;
    }

    /// <summary>The daemon profile closest to <paramref name="mode"/>.</summary>
    public static string ToProfile(OperatingMode mode) => mode switch
    {
        OperatingMode.Quiet or OperatingMode.Eco => PowerSaver,
        OperatingMode.Extreme or OperatingMode.Turbo => Performance,
        _ => Balanced
    };

    /// <summary>One daemon profile as a mode, or null when it is not one of the three.</summary>
    public static OperatingMode? ToMode(string? profile) => profile?.Trim() switch
    {
        PowerSaver => OperatingMode.Quiet,
        Balanced => OperatingMode.Default,
        Performance => OperatingMode.Turbo,
        _ => null
    };
}

/// <summary>No daemon. The probe's default in tests, and what a machine without one gets.</summary>
public sealed class NoPowerProfiles : IPowerProfiles
{
    public static NoPowerProfiles Instance { get; } = new();

    public bool IsAvailable => false;

    public IReadOnlyList<string> Profiles => Array.Empty<string>();

    public string? GetActive() => null;

    public bool SetActive(string profile) => false;
}
