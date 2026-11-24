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

    /// <summary>
    /// Reads an embedded catalogue; empty when the language has none (the source language).
    /// </summary>
    /// <remarks>
    /// Cached, because the catalogues are embedded resources that cannot change while the process runs and
    /// <see cref="SetLanguage"/> used to re-read and re-parse the English one on every call. The settings page calls
    /// it once per language change, and the English catalogue is also the fallback every other language reads through,
    /// so the same file was being deserialized again and again for an answer that never differs.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> LoadCatalog(string code)
    {
        if (CatalogCache.TryGetValue(code, out var cached)) return cached;

        var loaded = ReadCatalog(code);
        CatalogCache[code] = loaded;
        return loaded;
    }

    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> CatalogCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, string> ReadCatalog(string code)
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
    public static string T(LocInterpolation text) => text.Resolve(CultureInfo.CurrentCulture);

    /// <summary>Like <see cref="T(LocInterpolation)"/>, with the arguments formatted culture-independently ("55.5", never "55,5").</summary>
    public static string TInv(LocInterpolation text) => text.Resolve(CultureInfo.InvariantCulture);

    /// <summary>
    /// Translated text for use inside a single-quoted PowerShell string (the elevated scripts report errors with
    /// <c>throw '…'</c>): apostrophes are doubled so a translation such as "n'a pas" cannot end the string.
    /// </summary>
    public static string Ps(string source) => PsQuote(T(source));

    /// <inheritdoc cref="Ps(string)"/>
    public static string Ps(LocInterpolation text) => PsQuote(text.Resolve(CultureInfo.CurrentCulture));

    /// <summary>
    /// <paramref name="text"/> made safe to place between single quotes in a PowerShell script, without translating
    /// it. Use this for paths and other data; <see cref="Ps(string)"/> is the same escape for translated text.
    /// <para>
    /// PowerShell treats four Unicode characters as single quotes as well as the ASCII one: U+2018 and U+2019 (the
    /// curly quotes an autocorrecting keyboard produces, as in <c>O’Brien</c>), U+201A and U+201B. Doubling only the
    /// ASCII apostrophe left a profile folder or a translation containing any of the others able to end the string
    /// and run the rest as code — in a script that runs elevated. Each of the five is doubled, which PowerShell reads
    /// back as one literal character of the same kind.
    /// </para>
    /// </summary>
    public static string PsQuote(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var sb = new System.Text.StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            sb.Append(c);
            if (c is '\'' or '‘' or '’' or '‚' or '‛') sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Translates <paramref name="source"/> when a translation exists; otherwise null. Used for XAML text.</summary>
    public static string? TryT(string? source)
    {
        if (string.IsNullOrEmpty(source) || _language == SourceLanguage) return null;
        if (_current.TryGetValue(source, out var hit)) return hit;
        return _english.TryGetValue(source, out var en) ? en : null;
    }

    /// <summary>
    /// The English text for a value that is already a catalogue key or a label the catalogue produced, whatever the
    /// window is showing. A log line is read by whoever triages the report rather than by the user, so it must not
    /// follow the UI language: this resolves the English row for a key, and maps an already-translated label back to
    /// the key it came from first. Works before <see cref="SetLanguage"/> has run.
    /// </summary>
    public static string English(string? source)
    {
        if (string.IsNullOrEmpty(source)) return source ?? "";

        var english = LoadCatalog(DefaultLanguage);
        if (english.TryGetValue(source, out var hit)) return hit;

        // The caller may have handed over a label that is already translated — the name of a control, the title of a
        // dialog — so find the key it came from and answer with that key's English row.
        if (ReverseCatalog(_language).TryGetValue(source, out var key) && english.TryGetValue(key, out var fromKey))
            return fromKey;

        return source;
    }

    /// <summary>
    /// Like <see cref="English(string)"/>, with the arguments formatted culture-independently ("55.5", never "55,5"):
    /// the same line has to read the same in Istanbul and in Berlin.
    /// </summary>
    public static string English(LocInterpolation text)
    {
        var template = text.Template;
        var english = LoadCatalog(DefaultLanguage);
        return text.ResolveWith(english.TryGetValue(template, out var hit) ? hit : template, CultureInfo.InvariantCulture);
    }

    internal static string Lookup(string source)
    {
        if (_current.TryGetValue(source, out var hit)) return hit;
        if (TrackMisses) MissSet.TryAdd(source, 0);
        return _english.TryGetValue(source, out var en) ? en : source;
    }

    /// <summary>
    /// The catalogue for <paramref name="code"/> read backwards — translated text to the key it came from — so a label
    /// that has already been translated can be turned back into its English row. Cached for the same reason the
    /// catalogues are: the answer cannot change while the process runs.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ReverseCatalog(string code)
    {
        return ReverseCache.GetOrAdd(code, static c =>
        {
            var reverse = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in LoadCatalog(c))
            {
                // First key wins: two keys that share a translation are indistinguishable once translated, and the
                // one the catalogue lists first is the one a call site is most likely to have used.
                reverse.TryAdd(pair.Value, pair.Key);
            }

            return reverse;
        });
    }

    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> ReverseCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Formats the arguments into a template that has already been resolved.</summary>
    internal static string FormatTemplate(string template, object?[] args, IFormatProvider? provider)
    {
        try
        {
            return string.Format(provider, template, args);
        }
        catch (FormatException)
        {
            // A broken translation (bad placeholder) must never take a message away: show the template as it is.
            return template;
        }
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

    /// <summary>Formats the collected arguments into a template the caller has already resolved.</summary>
    internal string ResolveWith(string template, IFormatProvider? provider) => Loc.FormatTemplate(template, _args, provider);

    public override string ToString() => Resolve(CultureInfo.CurrentCulture);
}
