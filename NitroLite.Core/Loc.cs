using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace NitroLite.Core;

/// <summary>One selectable UI language.</summary>
public sealed record LocLanguage(string Code, string NativeName, string Culture);

/// <summary>
/// UI localization, gettext style: the Turkish text written in the code and XAML is the lookup key, and each
/// language is an embedded catalogue (Lang/&lt;code&gt;.json) mapping that text to its translation.
/// <para>
/// English is the default language; Turkish needs no catalogue (the key is the text). A text missing from the
/// selected catalogue falls back to English and then to the source text, and LocalizationCatalogTests keeps every
/// catalogue complete so that fallback is never what a user sees.
/// </para>
/// <para>
/// Call sites stay as they are: <c>Loc.T("Kapalı")</c> looks the literal up, and <c>Loc.T($"CPU %{value}")</c>
/// goes through <see cref="LocInterpolation"/>, which looks up the template "CPU %{0}" and formats the translated
/// template ("CPU {0}%") with the same arguments.
/// </para>
/// </summary>
public static class Loc
{
    public const string SourceLanguage = "tr";
    public const string DefaultLanguage = "en";

    public static readonly IReadOnlyList<LocLanguage> Languages = new[]
    {
        new LocLanguage("en", "English", "en-US"),
        new LocLanguage("tr", "Türkçe", "tr-TR"),
        new LocLanguage("de", "Deutsch", "de-DE"),
        new LocLanguage("es", "Español", "es-ES"),
        new LocLanguage("ru", "Русский", "ru-RU"),
        new LocLanguage("pt", "Português", "pt-BR"),
        new LocLanguage("hi", "हिन्दी", "hi-IN"),
        new LocLanguage("id", "Bahasa Indonesia", "id-ID")
    };

    private static readonly object Gate = new();
    private static IReadOnlyDictionary<string, string> _current = new Dictionary<string, string>();
    private static IReadOnlyDictionary<string, string> _english = new Dictionary<string, string>();
    private static string _language = SourceLanguage;

    /// <summary>Set NITROLITE_LOC_DEBUG=1 to collect every text that had no translation (see <see cref="Misses"/>).</summary>
    private static readonly bool TrackMisses = Environment.GetEnvironmentVariable("NITROLITE_LOC_DEBUG") == "1";
    private static readonly ConcurrentDictionary<string, byte> MissSet = new();

    /// <summary>
    /// The active language code. It starts as the source language, so code that runs before
    /// <see cref="SetLanguage"/> (and the test suite) sees the original text.
    /// </summary>
    public static string Language => _language;

    public static IReadOnlyCollection<string> Misses => MissSet.Keys.ToArray();

    public static bool IsSupported(string? code) =>
        !string.IsNullOrWhiteSpace(code) && Languages.Any(l => l.Code.Equals(code, StringComparison.OrdinalIgnoreCase));

    /// <summary>The supported language matching the Windows display language, or English.</summary>
    public static string DetectSystemLanguage(CultureInfo? uiCulture = null)
    {
        var two = (uiCulture ?? CultureInfo.CurrentUICulture).TwoLetterISOLanguageName;
        return IsSupported(two) ? two.ToLowerInvariant() : DefaultLanguage;
    }

    /// <summary>
    /// The language to start in. A stored choice wins. A settings file written before v1.2 has no language field and
    /// its user has been reading Turkish, so it stays Turkish; a fresh install follows the Windows display language.
    /// <para>
    /// The caller has to store the result: on the next launch a fresh install and a pre-v1.2 file look identical
    /// (both have no language), and the second case must stay Turkish. Without the write, an English install would
    /// switch to Turkish on its second run.
    /// </para>
    /// </summary>
    public static string ResolveStartupLanguage(string? stored, bool freshInstall, CultureInfo? uiCulture = null)
    {
        if (IsSupported(stored)) return stored!.ToLowerInvariant();
        return freshInstall ? DetectSystemLanguage(uiCulture) : SourceLanguage;
    }

    /// <summary>
    /// Selects the UI language. Unknown codes select English. Number formats are not switched: NitroLite shows
    /// measurements with a decimal point in every language (TelemetryFormatter), and where the percent sign goes
    /// ("%50" or "50%") is part of each translated template.
    /// </summary>
    public static void SetLanguage(string? code)
    {
        var lang = Languages.FirstOrDefault(l => l.Code.Equals(code, StringComparison.OrdinalIgnoreCase))
                   ?? Languages.First(l => l.Code == DefaultLanguage);
        lock (Gate)
        {
            _english = LoadCatalog(DefaultLanguage);
            _current = lang.Code == SourceLanguage ? new Dictionary<string, string>()
                     : lang.Code == DefaultLanguage ? _english
                     : LoadCatalog(lang.Code);
            _language = lang.Code;
        }

    }

    /// <summary>Reads an embedded catalogue; empty when the language has none (the source language).</summary>
    public static IReadOnlyDictionary<string, string> LoadCatalog(string code)
    {
        try
        {
            var asm = typeof(Loc).Assembly;
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith($".Lang.{code}.json", StringComparison.OrdinalIgnoreCase));
            if (name == null) return new Dictionary<string, string>();
            using var stream = asm.GetManifestResourceStream(name)!;
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new Dictionary<string, string>();
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }

    /// <summary>True when <paramref name="source"/> has an entry in the active catalogue (always true in the source language).</summary>
    public static bool Has(string source) => _language == SourceLanguage || _current.ContainsKey(source);

    /// <summary>Translates a fixed text.</summary>
    public static string T(string? source)
    {
        if (string.IsNullOrEmpty(source) || _language == SourceLanguage) return source ?? "";
        return Lookup(source);
    }

    /// <summary>Translates an interpolated text; the arguments are formatted into the translated template.</summary>
    public static string T(LocInterpolation text) => text.Resolve();

    /// <summary>Like <see cref="T(LocInterpolation)"/>, with the arguments formatted culture-independently ("55.5", never "55,5").</summary>
    public static string TInv(LocInterpolation text) => text.Resolve(CultureInfo.InvariantCulture);

    /// <summary>
    /// Translated text for use inside a single-quoted PowerShell string (the elevated scripts report errors with
    /// <c>throw '…'</c>): apostrophes are doubled so a translation such as "n'a pas" cannot end the string.
    /// </summary>
    public static string Ps(string source) => T(source).Replace("'", "''");

    /// <inheritdoc cref="Ps(string)"/>
    public static string Ps(LocInterpolation text) => text.Resolve().Replace("'", "''");

    /// <summary>Translates <paramref name="source"/> when a translation exists; otherwise null. Used for XAML text.</summary>
    public static string? TryT(string? source)
    {
        if (string.IsNullOrEmpty(source) || _language == SourceLanguage) return null;
        if (_current.TryGetValue(source, out var hit)) return hit;
        return _english.TryGetValue(source, out var en) ? en : null;
    }

    internal static string Lookup(string source)
    {
        if (_current.TryGetValue(source, out var hit)) return hit;
        if (TrackMisses) MissSet.TryAdd(source, 0);
        return _english.TryGetValue(source, out var en) ? en : source;
    }

    internal static string Format(string template, object?[] args, IFormatProvider? provider = null)
    {
        provider ??= CultureInfo.CurrentCulture;
        var translated = _language == SourceLanguage ? template : Lookup(template);
        try
        {
            return string.Format(provider, translated, args);
        }
        catch (FormatException)
        {
            // A broken translation (bad placeholder) must never take a message away: show the original. The
            // original can fail too when a call site passes a value its own format specifier cannot take, and
            // even then the message is worth more than an exception.
            try { return string.Format(provider, template, args); }
            catch (FormatException) { return template; }
        }
    }
}

/// <summary>
/// Collects an interpolated string as a format template plus arguments instead of a finished string, so the
/// template can be translated before the values go in: <c>$"CPU %{value:F0}"</c> becomes "CPU %{0:F0}" + [value].
/// The compiler picks this over <see cref="string"/> for any <c>$"…"</c> passed where a <see cref="LocInterpolation"/>
/// overload exists; plain literals keep using the string overload.
/// </summary>
[InterpolatedStringHandler]
public sealed class LocInterpolation
{
    private readonly StringBuilder _template;
    private readonly object?[] _args;
    private int _count;

    public LocInterpolation(int literalLength, int formattedCount)
    {
        _template = new StringBuilder(literalLength + formattedCount * 4);
        _args = new object?[formattedCount];
    }

    public void AppendLiteral(string value) => _template.Append(value.Replace("{", "{{").Replace("}", "}}"));

    public void AppendFormatted<T>(T value) => Add(value, null, 0);
    public void AppendFormatted<T>(T value, string? format) => Add(value, format, 0);
    public void AppendFormatted<T>(T value, int alignment) => Add(value, null, alignment);
    public void AppendFormatted<T>(T value, int alignment, string? format) => Add(value, format, alignment);

    private void Add(object? value, string? format, int alignment)
    {
        _template.Append('{').Append(_count);
        if (alignment != 0) _template.Append(',').Append(alignment);
        if (!string.IsNullOrEmpty(format)) _template.Append(':').Append(format);
        _template.Append('}');
        _args[_count++] = value;
    }

    /// <summary>The format template the catalogue is keyed by, e.g. "CPU %{0:F0}".</summary>
    public string Template => _template.ToString();

    public string Resolve(IFormatProvider? provider = null) => Loc.Format(_template.ToString(), _args, provider);

    public override string ToString() => Resolve();
}
