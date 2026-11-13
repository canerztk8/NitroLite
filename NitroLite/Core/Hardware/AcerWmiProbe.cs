using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>One line for the report: registered but silent, working, or absent.</summary>
    public string Summary
    {
        get
        {
            if (!ClassPresent) return "not registered";
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

    /// <summary>
    /// Inspects the class and invokes one harmless getter. Safe to call on any machine: a missing class, a denied
    /// namespace and a failing method all come back as data rather than exceptions.
    /// </summary>
    public static AcerWmiProbeResult Read()
    {
        ManagementClass? cls = null;
        try
        {
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

            // Prefer a getter that takes no arguments: it cannot be rejected for a malformed input structure, so a
            // failure there is unambiguous evidence that the provider itself is not answering.
            var target = methods.FirstOrDefault(m => m.Equals("GetGamingFanTable", StringComparison.Ordinal))
                         ?? methods.FirstOrDefault(m => IsParameterless(cls, m))
                         ?? "";

            if (target.Length == 0)
            {
                return new AcerWmiProbeResult
                {
                    ClassPresent = true,
                    ProviderVersion = providerVersion,
                    Methods = methods
                };
            }

            try
            {
                using var inParams = cls.GetMethodParameters(target);
                using var outParams = cls.InvokeMethod(target, inParams, null);
                return new AcerWmiProbeResult
                {
                    ClassPresent = true,
                    ProviderVersion = providerVersion,
                    Methods = methods,
                    CallMethod = target,
                    CallSucceeded = true
                };
            }
            catch (ManagementException ex)
            {
                return new AcerWmiProbeResult
                {
                    ClassPresent = true,
                    ProviderVersion = providerVersion,
                    Methods = methods,
                    CallMethod = target,
                    CallSucceeded = false,
                    CallError = $"{ex.ErrorCode} (0x{(int)ex.ErrorCode:X8})"
                };
            }
            catch (Exception ex)
            {
                return new AcerWmiProbeResult
                {
                    ClassPresent = true,
                    ProviderVersion = providerVersion,
                    Methods = methods,
                    CallMethod = target,
                    CallSucceeded = false,
                    CallError = ex.GetType().Name
                };
            }
        }
        finally
        {
            cls.Dispose();
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

    /// <summary>True when the method declares no in-parameters, so calling it needs no crafted input structure.</summary>
    private static bool IsParameterless(ManagementClass cls, string methodName)
    {
        try
        {
            foreach (MethodData m in cls.Methods)
            {
                if (!m.Name.Equals(methodName, StringComparison.Ordinal)) continue;
                return m.InParameters == null || m.InParameters.Properties.Count == 0;
            }
        }
        catch { }
        return false;
    }
}
