using System.Management;
using System.Text.RegularExpressions;

namespace NitroLite.Core.Hardware;

/// <summary>
/// What the Acer WMI interface looks like on this machine, and whether it actually answers.
/// <para>
/// This exists to settle one question with evidence instead of assumption: on some Nitro models the
/// <c>root\WMI</c> <c>AcerGamingFunction</c> class is registered and enumerable, yet every method call fails. That
/// distinction decides whether a future capability layer can use WMI as its primary path or must stay on the Acer
/// service. The probe only reads: it enumerates the class and invokes one parameterless getter.
/// </para>
/// </summary>
public sealed record AcerWmiProbeResult
{
    /// <summary>True when <c>root\WMI</c> exposes an <c>AcerGamingFunction</c> class.</summary>
    public bool ClassPresent { get; init; }

    /// <summary>Provider version reported by WMI, e.g. "2.88". Empty when the class is absent.</summary>
    public string ProviderVersion { get; init; } = "";

    /// <summary>Method names the class advertises. Empty when the class is absent or enumeration failed.</summary>
    public IReadOnlyList<string> Methods { get; init; } = Array.Empty<string>();

    /// <summary>The method the probe actually invoked, or "" when it invoked none.</summary>
    public string CallMethod { get; init; } = "";

    /// <summary>True when that call returned without an error.</summary>
    public bool CallSucceeded { get; init; }

    /// <summary>The WMI error code and message when the call failed, e.g. "WBEM_E_INVALID_METHOD_PARAMETERS (0x8004103A)".</summary>
    public string CallError { get; init; } = "";

    /// <summary>True when the probe did not finish in time. A hung provider is a distinct outcome from a missing one.</summary>
    public bool TimedOut { get; init; }

    /// <summary>
    /// True when the class carries the <c>dynamic</c> qualifier, which means its methods live on instances rather
    /// than on the class object. On such a class a class-level <c>InvokeMethod</c> fails with
    /// <c>WBEM_E_INVALID_METHOD_PARAMETERS</c> no matter what parameters are passed, so that error says nothing
    /// about the provider being broken.
    /// </summary>
    public bool DynamicClass { get; init; }

    /// <summary>
    /// True when the class advertises instances but enumerating them was refused. On an AN16-41 this is what an
    /// unelevated session gets: the class is registered and enumerable, every instance read returns Access Denied.
    /// The capability may well exist; the probe simply was not allowed to ask.
    /// </summary>
    public bool InstanceAccessDenied { get; init; }

    /// <summary>One line for the report: registered but silent, working, or absent.</summary>
    public string Summary
    {
        get
        {
            if (TimedOut) return "probe timed out";
            if (!ClassPresent) return "not registered";
            if (InstanceAccessDenied)
                return $"registered ({Methods.Count} methods, dynamic), instance access denied: needs elevation";
            if (string.IsNullOrEmpty(CallMethod)) return $"registered ({Methods.Count} methods), not called";
            return CallSucceeded
                ? $"registered, {CallMethod} answered"
                : $"registered ({Methods.Count} methods), {CallMethod} failed: {CallError}";
        }
    }
}

/// <summary>Read-only inspection of the Acer WMI surface. Never writes, never throws.</summary>
public static class AcerWmiProbe
{
    private const string WmiNamespace = @"root\WMI";
    private const string ClassName = "AcerGamingFunction";

    /// <summary>How many getters the probe tries before giving up. Each one can cost a few hundred milliseconds.</summary>
    private const int MaxProbeCalls = 6;

    /// <summary>
    /// Inspects the class and invokes one harmless getter. Safe to call on any machine: a missing class, a denied
    /// namespace and a failing method all come back as data rather than exceptions.
    /// </summary>
    public static AcerWmiProbeResult Read()
    {
        ManagementClass? cls = null;
        try
        {
            // ManagementScope is not IDisposable in System.Management; the COM object is released by the finalizer.
            var scope = new ManagementScope(WmiNamespace);
            scope.Connect();
            cls = new ManagementClass(scope, new ManagementPath(ClassName), null);

            // Touching ClassPath forces the class to be resolved; a missing class throws here rather than later.
            _ = cls.ClassPath.ClassName;
        }
        catch
        {
            cls?.Dispose();
            return new AcerWmiProbeResult { ClassPresent = false };
        }

        try
        {
            var methods = new List<string>();
            try
            {
                foreach (MethodData m in cls.Methods)
                {
                    if (!string.IsNullOrWhiteSpace(m.Name)) methods.Add(m.Name);
                }
            }
            catch { }

            methods.Sort(StringComparer.Ordinal);

            var providerVersion = "";
            try
            {
                // There is no Version qualifier on this class; the provider stamps it into the description instead:
                // "Class used to Gaming Function, Version 2.88".
                providerVersion = ExtractVersion(cls.Qualifiers["Description"]?.Value?.ToString());
            }
            catch { }

            // AcerGamingFunction is a dynamic class: its methods belong to instances, not to the class object. A
            // class-level InvokeMethod therefore fails with WBEM_E_INVALID_METHOD_PARAMETERS (0x8004102F) whatever
            // parameters are passed, and an earlier version of this probe read that as "the provider is broken".
            // It is not: the same call on an instance works, but only from an elevated session. Reading the
            // qualifier lets the report say which of the two situations it is looking at.
            bool dynamicClass = false;
            try { dynamicClass = cls.Qualifiers["dynamic"]?.Value is bool b && b; }
            catch { }

            // Only getters are tried: a setter would change the machine, and this probe is read-only. The first one
            // that answers settles the question.
            var candidates = methods
                .Where(m => m.StartsWith("Get", StringComparison.Ordinal))
                .Take(MaxProbeCalls)
                .ToList();

            if (candidates.Count == 0)
            {
                return new AcerWmiProbeResult
                {
                    ClassPresent = true,
                    ProviderVersion = providerVersion,
                    Methods = methods,
                    DynamicClass = dynamicClass
                };
            }

            string lastMethod = "";
            string lastError = "";
            foreach (var candidate in candidates)
            {
                lastMethod = candidate;
                try
                {
                    using var inParams = cls.GetMethodParameters(candidate);
                    using var outParams = cls.InvokeMethod(candidate, inParams, null);
                    return new AcerWmiProbeResult
                    {
                        ClassPresent = true,
                        ProviderVersion = providerVersion,
                        Methods = methods,
                        DynamicClass = dynamicClass,
                        CallMethod = candidate,
                        CallSucceeded = true
                    };
                }
                catch (ManagementException ex)
                {
                    lastError = $"{ex.ErrorCode} (0x{(int)ex.ErrorCode:X8})";
                }
                catch (Exception ex)
                {
                    lastError = ex.GetType().Name;
                }
            }

            // Every class-level call failed. On a dynamic class that is expected, so the useful question becomes
            // whether an instance can be reached at all: that is the difference between "no rights" and "no
            // hardware", and only the first one is worth retrying elevated.
            bool instanceDenied = false;
            if (dynamicClass)
            {
                try
                {
                    foreach (ManagementObject instance in cls.GetInstances())
                    {
                        instance.Dispose();
                        break;
                    }
                }
                catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
                {
                    instanceDenied = true;
                }
                catch (UnauthorizedAccessException)
                {
                    instanceDenied = true;
                }
                catch { }
            }

            return new AcerWmiProbeResult
            {
                ClassPresent = true,
                ProviderVersion = providerVersion,
                Methods = methods,
                DynamicClass = dynamicClass,
                InstanceAccessDenied = instanceDenied,
                CallMethod = lastMethod,
                CallSucceeded = false,
                CallError = lastError
            };
        }
        finally
        {
            cls.Dispose();
        }
    }

    /// <summary>
    /// Runs <see cref="Read"/> on a worker thread with a hard timeout. The failure this probe exists to detect is a
    /// provider that hangs instead of returning an error, and a synchronous call would hang the caller with it: the
    /// hardware report and the settings page both call this.
    /// </summary>
    public static AcerWmiProbeResult ReadWithTimeout(TimeSpan timeout)
    {
        try
        {
            var task = Task.Run(Read);
            if (task.Wait(timeout)) return task.Result;
            return new AcerWmiProbeResult { TimedOut = true, CallError = "probe timed out" };
        }
        catch (AggregateException ex)
        {
            return new AcerWmiProbeResult { CallError = ex.GetBaseException().GetType().Name };
        }
        catch (Exception ex)
        {
            return new AcerWmiProbeResult { CallError = ex.GetType().Name };
        }
    }

    /// <summary>
    /// Pulls "2.88" out of "Class used to Gaming Function, Version 2.88". Returns "" when the description carries no
    /// version, so the report can leave the field out instead of printing an empty label.
    /// </summary>
    public static string ExtractVersion(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return "";
        var match = VersionPattern.Match(description);
        return match.Success ? match.Groups[1].Value : "";
    }

    private static readonly Regex VersionPattern = new(
        @"Version\s+([0-9]+(?:\.[0-9]+)*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
