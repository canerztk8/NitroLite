using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NitroLite.Core.Hardware.Snapshot;

namespace NitroLite.Core.Hardware.Diagnostics;

/// <summary>
/// Sends one small JSON report when a hardware control could not be confirmed, and is silent about everything else.
/// </summary>
/// <remarks>
/// <para>
/// This exists because a handful of 2025-era Acer boards advertise a selector, answer success, and then do nothing.
/// The local read-back already catches the lie and disables the control, which is the right behaviour for the person
/// using the app — but it leaves the maintainer with no evidence about the machine that produced it. One POST per
/// distinct failure per run is enough to learn which boards behave this way, and small enough to stay invisible.
/// </para>
/// <para>
/// Nothing about the feature is allowed to affect the feature it observes. The send is dispatched to the thread pool
/// and its failure is swallowed at the outermost frame: no exception reaches a caller, no status is shown, no setting
/// is written and no UI element exists. An app that crashed on a diagnostics POST would be worse than an app with no
/// diagnostics at all, and a network that is down is the normal case for the machines this is aimed at.
/// </para>
/// <para>
/// The payload is built from <see cref="HardwareDiagnosticIdentity"/>, which cannot hold a serial, an account name or
/// a network address. Every text value is additionally run through <see cref="SnapshotRedaction.Scrub"/>, so a
/// firmware that answers a board string with an identifier has it removed before the value leaves the process.
/// </para>
/// </remarks>
public sealed class HardwareDiagnosticClient : IHardwareDiagnosticSink
{
    /// <summary>The transport named when a firmware read-back contradicted the write.</summary>
    public const string WmiTransport = "WMI";

    /// <summary>The transport named when the keyboard lighting fallback chain ran out.</summary>
    public const string HidTransport = "HID";

    /// <summary>
    /// The control name used for the lighting chain. Not a <c>ReadBackPolicy</c> capability: the HID write never
    /// passes through that policy, so borrowing <c>wmi.keyboardLighting</c> would misreport which layer failed.
    /// </summary>
    public const string KeyboardLightingControl = "keyboard.lighting";

    /// <summary>
    /// How many distinct failures a single run remembers. A colour slider dragged across the spectrum can exhaust the
    /// fallback on every step, and a report per step would be a flood rather than a diagnostic. The cap also bounds
    /// the set for a process that is left running for weeks.
    /// </summary>
    public const int MaximumRememberedFailures = 32;

    /// <summary>
    /// How many method names a report carries. The schema probe returns a dozen on a healthy board; the cap is for a
    /// provider that answers something strange, so one machine cannot make the payload unbounded.
    /// </summary>
    public const int MaximumReportedMethods = 64;

    /// <summary>The error endpoint. One URL, in one place, so it can be changed without hunting call sites.</summary>
    public static readonly Uri DefaultEndpoint =
        new("https://nitro-telemetry.canerozturk.workers.dev/report");

    /// <summary>How long a report may take before it is abandoned. Nothing waits on it either way.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private static readonly HttpClient SharedHttp = CreateHttpClient();

    private readonly Func<HardwareDiagnosticIdentity> _identity;
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly Func<DateTimeOffset> _clock;

    private readonly object _gate = new();
    private readonly HashSet<string> _remembered = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <paramref name="identity"/> is asked at the moment of a failure, not at construction: the capability probe runs
    /// in the background at startup, and a report sent before it finished should say "no methods known" rather than
    /// the empty answer a field captured at launch would give forever.
    /// <paramref name="http"/> is not owned by this instance and is not disposed by it.
    /// </summary>
    public HardwareDiagnosticClient(
        Func<HardwareDiagnosticIdentity>? identity = null,
        HttpClient? http = null,
        Uri? endpoint = null,
        Func<DateTimeOffset>? clock = null)
    {
        _identity = identity ?? (static () => new HardwareDiagnosticIdentity());
        _http = http ?? SharedHttp;
        _endpoint = endpoint ?? DefaultEndpoint;
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);
    }

    /// <summary>A control ended in <c>ControlVerdict.Mismatch</c>. Fire and forget; returns as soon as the work is queued.</summary>
    public void ReadBackMismatch(string control)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(control);
        Report(control, WmiTransport, modelCode: null, biosVersion: null);
    }

    /// <summary>Both lighting transports were tried and neither applied the colour.</summary>
    public void LightingFallbackExhausted(string? modelCode, string? biosVersion) =>
        Report(KeyboardLightingControl, HidTransport, modelCode, biosVersion);

    /// <summary>
    /// Sends one report and swallows every failure. Public so the test suite can await a delivery instead of racing a
    /// background task; the production path reaches the same code through <see cref="Report"/>.
    /// </summary>
    public async Task SendAsync(HardwareDiagnosticReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        try
        {
            await PostAsync(report).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The whole point. A DNS failure, a refused connection, a TLS problem, a 500 from the Worker, a payload
            // the endpoint does not like: none of them is the user's problem and none of them may become one.
        }
    }

    private void Report(string control, string transport, string? modelCode, string? biosVersion)
    {
        if (!Remember(control, transport)) return;

        // The report is built and sent entirely inside the background work, so a throwing identity provider or a
        // slow serializer cannot delay the write path that called here, and the catch covers all of it.
        _ = Task.Run(async () =>
        {
            try
            {
                var report = BuildReport(control, transport, modelCode, biosVersion);
                await PostAsync(report).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Silent by requirement. There is no log line because the log is read by the user and this is not
                // their concern; the failure of a diagnostic is not a diagnostic.
            }
        });
    }

    /// <summary>
    /// True the first time a given control and transport are reported in this process. Kept per control rather than
    /// per call because the interesting question is "which board fails this control", and the answer does not become
    /// more true with repetition.
    /// </summary>
    private bool Remember(string control, string transport)
    {
        var key = $"{control}|{transport}";

        lock (_gate)
        {
            if (_remembered.Contains(key)) return false;
            if (_remembered.Count >= MaximumRememberedFailures) return false;

            _remembered.Add(key);
            return true;
        }
    }

    private HardwareDiagnosticReport BuildReport(
        string control,
        string transport,
        string? modelCode,
        string? biosVersion)
    {
        HardwareDiagnosticIdentity identity;
        try
        {
            identity = _identity() ?? new HardwareDiagnosticIdentity();
        }
        catch (Exception)
        {
            // A report with an empty model is still worth sending when the failure itself is the news; a report that
            // never leaves because the identity read threw is worth nothing.
            identity = new HardwareDiagnosticIdentity();
        }

        return new HardwareDiagnosticReport
        {
            Model = Clean(string.IsNullOrWhiteSpace(modelCode) ? identity.Model : modelCode),
            Board = Clean(identity.Board),
            BiosVersion = Clean(string.IsNullOrWhiteSpace(biosVersion) ? identity.BiosVersion : biosVersion),
            FailedControl = Clean(control),
            AttemptedTransport = transport,
            AvailableWmiMethods = Clean(identity.AvailableWmiMethods),
            TimestampUtc = _clock().ToUniversalTime()
        };
    }

    private async Task PostAsync(HardwareDiagnosticReport report)
    {
        var json = JsonSerializer.Serialize(
            report,
            HardwareDiagnosticJsonContext.Default.HardwareDiagnosticReport);

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        request.Content = content;

        // The answer is deliberately not read. A 202, a 400 or a 500 all mean the same thing here: the failure has
        // been reported or it has not, and either way the app carries on without saying so.
        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// One text field, scrubbed and bounded. The scrub is the same one the public snapshot uses, so an identifier a
    /// firmware put in a board string cannot reach the endpoint through this report.
    /// </summary>
    private static string Clean(string? value) =>
        SnapshotRedaction.Scrub(value ?? "").Trim();

    /// <summary>
    /// The method list, scrubbed, de-duplicated and capped. An empty list is preserved as empty: it says the probe
    /// found no class, which is different from a report that omitted the field.
    /// </summary>
    private static IReadOnlyList<string> Clean(IReadOnlyList<string>? methods)
    {
        if (methods == null || methods.Count == 0) return Array.Empty<string>();

        var cleaned = new List<string>(Math.Min(methods.Count, MaximumReportedMethods));
        foreach (var method in methods)
        {
            var text = Clean(method);
            if (text.Length == 0) continue;
            if (cleaned.Contains(text, StringComparer.Ordinal)) continue;

            cleaned.Add(text);
            if (cleaned.Count >= MaximumReportedMethods) break;
        }

        return cleaned;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = RequestTimeout };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NitroLite", null));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }
}
