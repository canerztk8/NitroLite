using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NitroLite.Core.Hardware.Wmi;

namespace NitroLite.Core.Hardware;

/// <summary>
/// The model table: which sensor layout, fan ceiling and known-broken methods belong to which Acer laptop.
/// <para>
/// Two files feed it. <c>profiles.json</c> is embedded in the assembly and read-only; <c>profiles.user.json</c> sits
/// in the data folder and is optional. The user file exists so a new model can be supported by adding a few lines
/// instead of shipping a build, and it is validated as strictly as the embedded one — plus one extra rule: it may
/// not claim every Acer machine, because a catch-all in a hand-edited file would silently replace the shipped
/// default for every model.
/// </para>
/// <para>
/// A rejected record is dropped and logged, never applied half-way. The machine then falls back to the embedded
/// profile for its model, or to the catch-all, which is the same state it would be in without the user file.
/// </para>
/// </summary>
public sealed class ProfileCatalog
{
    /// <summary>Highest fan speed any profile may declare. Above this the number is a typo, not a fan.</summary>
    public const int MinReferenceRpm = 1000;
    public const int MaxReferenceRpm = 12000;

    /// <summary>
    /// The WMID_Gaming methods a profile may name as broken. A whitelist rather than free text: a typo in a method
    /// name would otherwise disable nothing and look like it worked, and an arbitrary string has no business in a
    /// file that decides which hardware calls are skipped.
    /// </summary>
    public static readonly IReadOnlySet<string> KnownMethods = new HashSet<string>(StringComparer.Ordinal)
    {
        "GetCPUOverclockingProfile", "SetCPUOverclockingProfile",
        "GetGamingFanBehavior", "SetGamingFanBehavior",
        "GetGamingFanSpeed", "SetGamingFanSpeed",
        "GetGamingFanTable", "SetGamingFanTable",
        "GetGamingKBBacklight", "SetGamingKBBacklight",
        "GetGamingLED", "SetGamingLED",
        "GetGamingLEDBehavior", "SetGamingLEDBehavior",
        "GetGamingLEDColor", "SetGamingLEDColor",
        "GetGamingMiscSetting", "SetGamingMiscSetting",
        "GetGamingProfile", "SetGamingProfile",
        "GetGamingProfileSetting", "SetGamingProfileSetting",
        "GetGamingRgbKb", "SetGamingRgbKb",
        "GetGamingSysInfo"
    };

    private static readonly Regex IdPattern = new(@"^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.Compiled);

    /// <summary>
    /// Acer model codes: two to eight alphanumerics, then one or more dash-separated suffixes. The first segment is
    /// eight rather than six because real codes reach that length — <c>PHIN16S-71</c> is seven — and a pattern that
    /// rejected a genuine code would drop the profile for that machine without saying why.
    /// </summary>
    private static readonly Regex ModelCodePattern = new(@"^[A-Z0-9]{2,8}(-[A-Z0-9]{2,4})*$", RegexOptions.Compiled);

    private readonly List<ModelProfile> _profiles;
    private readonly List<string> _rejections;

    private ProfileCatalog(List<ModelProfile> profiles, List<string> rejections, SensorLayout defaultSensors)
    {
        _profiles = profiles;
        _rejections = rejections;
        DefaultSensors = defaultSensors;
    }

    /// <summary>Every profile that survived validation, user profiles first.</summary>
    public IReadOnlyList<ModelProfile> Profiles => _profiles;

    /// <summary>One line per rejected record, for the log and the hardware report.</summary>
    public IReadOnlyList<string> Rejections => _rejections;

    /// <summary>The layout used when a profile does not override it.</summary>
    public SensorLayout DefaultSensors { get; }

    /// <summary>Path of the optional user file. Exposed so the report can say whether one was found.</summary>
    public static string UserProfilePath => AppPaths.GetDataFile("profiles.user.json");

    /// <summary>
    /// Path of the downloaded table. It sits between the user file and the embedded one: it is newer than what this
    /// build was released with, but the person at the keyboard still overrules both.
    /// </summary>
    public static string RemoteProfilePath => AppPaths.GetDataFile("profiles.remote.json");

    /// <summary>
    /// Loads the embedded table and, when present, the user file. Never throws: a missing or broken file leaves the
    /// embedded table in place, which is the state a fresh install is in.
    /// </summary>
    public static ProfileCatalog Load()
    {
        var rejections = new List<string>();
        var embedded = ReadEmbeddedJson();
        var user = ReadOptional(UserProfilePath, "profiles.user.json", rejections);
        var remote = ReadOptional(RemoteProfilePath, "profiles.remote.json", rejections);

        return Build(embedded, user, remote, rejections);
    }

    /// <summary>Reads an optional profile file. A failure is recorded and treated as "no file".</summary>
    private static string? ReadOptional(string path, string fileName, List<string> rejections)
    {
        try
        {
            return File.Exists(path) ? BoundedTextFile.ReadAllText(path) : null;
        }
        catch (Exception ex)
        {
            rejections.Add($"{fileName} okunamadı: {ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>
    /// Builds a catalogue from two JSON documents. Split out from <see cref="Load"/> so the validation rules can be
    /// tested without touching the file system or the assembly.
    /// </summary>
    public static ProfileCatalog FromJson(string? embeddedJson, string? userJson, out IReadOnlyList<string> rejections)
        => FromJson(embeddedJson, userJson, null, out rejections);

    /// <summary>
    /// The same, with the downloaded table in the middle: user first, then remote, then embedded. A tie is won by
    /// whichever document was parsed first, which is why the order of the three blocks below is the priority order.
    /// </summary>
    public static ProfileCatalog FromJson(
        string? embeddedJson, string? userJson, string? remoteJson, out IReadOnlyList<string> rejections)
    {
        var collected = new List<string>();
        var catalog = Build(embeddedJson, userJson, remoteJson, collected);
        rejections = collected;
        return catalog;
    }

    /// <summary>
    /// Checks a whole document on its own, without merging it into a catalogue. The remote refresh uses this: a
    /// download is either entirely acceptable or it is not written to disk at all, because half a table from one
    /// release mixed with half from another is worse than the table this build already carries.
    /// </summary>
    public static bool Validate(string json, out int profileCount, out IReadOnlyList<string> rejections)
        => Validate(json, isUserFile: true, out profileCount, out rejections);

    /// <summary>
    /// The same check with the catch-all rule made explicit: a downloaded table is held to the user-file rule (no
    /// <c>anyAcer</c>), while the embedded table is allowed the one catch-all it ships with.
    /// </summary>
    public static bool Validate(
        string json, bool isUserFile, out int profileCount, out IReadOnlyList<string> rejections)
    {
        var collected = new List<string>();
        var defaultSensors = SensorLayout.Default;
        var fileName = isUserFile ? "profiles.remote.json" : "profiles.json";
        var parsed = ParseDocument(json, fileName, isUserFile, collected, ref defaultSensors);
        profileCount = parsed.Count;

        foreach (var profile in parsed)
        {
            var problem = DescribeImplausible(profile);
            if (problem != null) collected.Add($"{fileName}: {profile.Id}: {problem}");
        }

        if (parsed.Count == 0 && collected.Count == 0)
            collected.Add($"{fileName}: hiç profil yok");

        rejections = collected;
        return collected.Count == 0 && parsed.Count > 0;
    }

    /// <summary>Lowest ceiling a profile may declare. Below this a fan command could never reach any speed.</summary>
    private const int MinimumFanCeiling = 1000;

    /// <summary>Highest ceiling a profile may declare, the same window the sensor plausibility check uses.</summary>
    private const int MaximumFanCeiling = 12000;

    /// <summary>Highest sensor id the firmware enum is known to reach.</summary>
    private const int MaximumSensorId = 63;

    /// <summary>
    /// Why a parsed profile is not plausible, or null when it is. A downloaded table can name a fan ceiling or a
    /// sensor id that no machine has; those are the fields a wrong value would turn into a fan command or a reading
    /// from the wrong register, so they are bounded before the file is kept.
    /// </summary>
    private static string? DescribeImplausible(ModelProfile profile)
    {
        if (profile.MaxCpuFanRpm is < MinimumFanCeiling or > MaximumFanCeiling)
            return $"fan ceiling {profile.MaxCpuFanRpm} RPM is outside {MinimumFanCeiling}..{MaximumFanCeiling}";

        if (profile.MaxGpuFanRpm is < MinimumFanCeiling or > MaximumFanCeiling)
            return $"fan ceiling {profile.MaxGpuFanRpm} RPM is outside {MinimumFanCeiling}..{MaximumFanCeiling}";

        var sensors = profile.Sensors;
        foreach (var id in new[]
                 {
                     sensors.CpuTempSensor, sensors.GpuTempSensor, sensors.Sys1TempSensor,
                     sensors.CpuFanRpmSensor, sensors.GpuFanRpmSensor
                 })
        {
            if (id < 0 || id > MaximumSensorId)
                return $"sensor id {id} is outside 0..{MaximumSensorId}";
        }

        return null;
    }

    private static ProfileCatalog Build(string? embeddedJson, string? userJson, List<string> rejections)
        => Build(embeddedJson, userJson, null, rejections);

    private static ProfileCatalog Build(
        string? embeddedJson, string? userJson, string? remoteJson, List<string> rejections)
    {
        var defaultSensors = SensorLayout.Default;
        var profiles = new List<ModelProfile>();

        // The user file is parsed first so its profiles win a tie: someone who wrote one is trying to correct the
        // shipped table for their own machine.
        if (!string.IsNullOrWhiteSpace(userJson))
        {
            var parsed = ParseDocument(userJson!, "profiles.user.json", isUserFile: true, rejections, ref defaultSensors);
            profiles.AddRange(parsed);
        }

        // The downloaded table is newer than the embedded one but older than anything the user wrote.
        if (!string.IsNullOrWhiteSpace(remoteJson))
        {
            var parsed = ParseDocument(
                remoteJson!, "profiles.remote.json", isUserFile: true, rejections, ref defaultSensors);
            profiles.AddRange(parsed);
        }

        if (!string.IsNullOrWhiteSpace(embeddedJson))
        {
            var parsed = ParseDocument(embeddedJson!, "profiles.json", isUserFile: false, rejections, ref defaultSensors);
            profiles.AddRange(parsed);
        }

        return new ProfileCatalog(profiles, rejections, defaultSensors);
    }

    private static List<ModelProfile> ParseDocument(
        string json, string fileName, bool isUserFile, List<string> rejections, ref SensorLayout defaultSensors)
    {
        var result = new List<ModelProfile>();

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            rejections.Add($"{fileName}: geçersiz JSON ({ex.Message})");
            return result;
        }

        if (root is not JsonObject rootObject)
        {
            rejections.Add($"{fileName}: kök nesne değil");
            return result;
        }

        var unknownRoot = rootObject.Select(p => p.Key)
            .Where(k => k is not ("schemaVersion" or "defaultSensorLayout" or "profiles"))
            .ToList();
        if (unknownRoot.Count > 0)
        {
            rejections.Add($"{fileName}: bilinmeyen alan(lar) {string.Join(", ", unknownRoot)}");
            return result;
        }

        if (rootObject["schemaVersion"] is not JsonValue versionNode ||
            !versionNode.TryGetValue<int>(out var version) || version != 1)
        {
            rejections.Add($"{fileName}: desteklenmeyen schemaVersion");
            return result;
        }

        if (rootObject["defaultSensorLayout"] is JsonObject layoutNode)
        {
            var layout = ParseSensors(layoutNode, fileName, rejections);
            if (layout != null) defaultSensors = layout;
        }

        if (rootObject["profiles"] is not JsonArray profileArray)
        {
            rejections.Add($"{fileName}: profiles dizisi yok");
            return result;
        }

        foreach (var node in profileArray)
        {
            if (node is not JsonObject profileObject)
            {
                rejections.Add($"{fileName}: profil nesne değil");
                continue;
            }

            var profile = ParseProfile(profileObject, fileName, isUserFile, defaultSensors, rejections);
            if (profile != null) result.Add(profile);
        }

        return result;
    }

    private static ModelProfile? ParseProfile(
        JsonObject node, string fileName, bool isUserFile, SensorLayout defaultSensors, List<string> rejections)
    {
        var id = ReadString(node, "id");
        var label = string.IsNullOrWhiteSpace(id) ? "(kimliksiz)" : id;

        var unknown = node.Select(p => p.Key)
            .Where(k => k is not ("id" or "match" or "sensors" or "maxFanRpm" or "muxSwitch" or "fourZoneKeyboard"
                or "nitroHidKeyboard" or "ecMmioProfile" or "kbZoneArm" or "unsupportedMethods" or "notes"))
            .ToList();
        if (unknown.Count > 0)
        {
            rejections.Add($"{fileName}: {label} bilinmeyen alan(lar) {string.Join(", ", unknown)}");
            return null;
        }

        if (string.IsNullOrWhiteSpace(id) || !IdPattern.IsMatch(id))
        {
            rejections.Add($"{fileName}: {label} kimlik küçük harf, rakam ve tire olmalı");
            return null;
        }

        if (node["match"] is not JsonObject matchNode)
        {
            rejections.Add($"{fileName}: {label} match nesnesi yok");
            return null;
        }

        var unknownMatch = matchNode.Select(p => p.Key)
            .Where(k => k is not ("modelCodes" or "boardPatterns" or "familyPatterns" or "anyAcer"))
            .ToList();
        if (unknownMatch.Count > 0)
        {
            rejections.Add($"{fileName}: {label} match içinde bilinmeyen alan(lar) {string.Join(", ", unknownMatch)}");
            return null;
        }

        var anyAcer = ReadBool(matchNode, "anyAcer") ?? false;
        if (anyAcer && isUserFile)
        {
            // A catch-all in a hand-edited file would replace the shipped default for every model, including the
            // ones the user does not own. Refused rather than silently ignored.
            rejections.Add($"{fileName}: {label} anyAcer kullanıcı dosyasında kullanılamaz");
            return null;
        }

        var modelCodes = ReadStringArray(matchNode, "modelCodes");
        var boardPatterns = ReadStringArray(matchNode, "boardPatterns");
        var familyPatterns = ReadStringArray(matchNode, "familyPatterns");

        if (!anyAcer && modelCodes.Count == 0 && boardPatterns.Count == 0 && familyPatterns.Count == 0)
        {
            rejections.Add($"{fileName}: {label} hiçbir eşleşme kuralı yok");
            return null;
        }

        foreach (var code in modelCodes)
        {
            if (!ModelCodePattern.IsMatch(code))
            {
                rejections.Add($"{fileName}: {label} geçersiz model kodu '{code}'");
                return null;
            }
        }

        var sensors = defaultSensors;
        if (node["sensors"] is JsonObject sensorNode)
        {
            var parsed = ParseSensors(sensorNode, fileName, rejections);
            if (parsed == null)
            {
                rejections.Add($"{fileName}: {label} sensör düzeni geçersiz");
                return null;
            }

            sensors = parsed;
        }

        int maxCpu = 7000, maxGpu = 7000;
        if (node["maxFanRpm"] is JsonObject rpmNode)
        {
            var unknownRpm = rpmNode.Select(p => p.Key).Where(k => k is not ("cpu" or "gpu")).ToList();
            if (unknownRpm.Count > 0)
            {
                rejections.Add($"{fileName}: {label} maxFanRpm içinde bilinmeyen alan(lar) {string.Join(", ", unknownRpm)}");
                return null;
            }

            maxCpu = ReadInt(rpmNode, "cpu") ?? maxCpu;
            maxGpu = ReadInt(rpmNode, "gpu") ?? maxGpu;
            if (maxCpu is < MinReferenceRpm or > MaxReferenceRpm || maxGpu is < MinReferenceRpm or > MaxReferenceRpm)
            {
                rejections.Add($"{fileName}: {label} referans RPM {MinReferenceRpm}..{MaxReferenceRpm} dışında");
                return null;
            }
        }

        var unsupported = ReadStringArray(node, "unsupportedMethods");
        var unknownMethod = unsupported.FirstOrDefault(m => !KnownMethods.Contains(m));
        if (unknownMethod != null)
        {
            rejections.Add($"{fileName}: {label} bilinmeyen WMI metodu '{unknownMethod}'");
            return null;
        }

        return new ModelProfile
        {
            Id = id,
            ModelCodes = modelCodes,
            BoardPatterns = boardPatterns,
            FamilyPatterns = familyPatterns,
            AnyAcer = anyAcer,
            Sensors = sensors,
            MaxCpuFanRpm = maxCpu,
            MaxGpuFanRpm = maxGpu,
            MuxSwitch = ReadBool(node, "muxSwitch"),
            FourZoneKeyboard = ReadBool(node, "fourZoneKeyboard"),
            NitroHidKeyboard = ReadBool(node, "nitroHidKeyboard") ?? false,
            EcMmioProfile = ReadBool(node, "ecMmioProfile") ?? false,
            KbZoneArm = ReadBool(node, "kbZoneArm") ?? false,
            UnsupportedMethods = unsupported,
            Notes = ReadString(node, "notes"),
            Source = isUserFile ? "user" : "embedded"
        };
    }

    private static SensorLayout? ParseSensors(JsonObject node, string fileName, List<string> rejections)
    {
        var unknown = node.Select(p => p.Key)
            .Where(k => k is not ("cpuTempSensor" or "cpuFanRpmSensor" or "sys1TempSensor" or "gpuFanRpmSensor"
                or "gpuTempSensor" or "verified" or "supported"))
            .ToList();
        if (unknown.Count > 0)
        {
            rejections.Add($"{fileName}: sensör düzeninde bilinmeyen alan(lar) {string.Join(", ", unknown)}");
            return null;
        }

        var layout = new SensorLayout
        {
            CpuTempSensor = ReadInt(node, "cpuTempSensor") ?? AcerSensorIds.CpuTemperature,
            CpuFanRpmSensor = ReadInt(node, "cpuFanRpmSensor") ?? AcerSensorIds.CpuFanSpeed,
            Sys1TempSensor = ReadInt(node, "sys1TempSensor") ?? AcerSensorIds.SystemTemperature,
            GpuFanRpmSensor = ReadInt(node, "gpuFanRpmSensor") ?? AcerSensorIds.GpuFanSpeed,
            GpuTempSensor = ReadInt(node, "gpuTempSensor") ?? AcerSensorIds.GpuTemperature,
            Verified = ReadBool(node, "verified") ?? false,
            Supported = ReadBool(node, "supported") ?? true
        };

        return layout.IsValid ? layout : null;
    }

    /// <summary>
    /// Picks the profile for this machine. A model-code hit beats a board or family hit, and a user profile beats an
    /// embedded one at the same level; the catch-all is the last resort. Returns a synthetic profile when nothing
    /// matches, so the caller never has to handle null.
    /// </summary>
    public ModelProfile Match(DeviceIdentity identity)
    {
        var byCode = _profiles.FirstOrDefault(p => !p.AnyAcer && p.MatchesModelCode(identity));
        if (byCode != null) return byCode;

        var byPattern = _profiles.FirstOrDefault(p => !p.AnyAcer && p.Matches(identity));
        if (byPattern != null)
        {
            // A family or board hit is a guess about a model nobody measured: the reference machine's "verified"
            // flag describes a read-back taken on that machine, not on this one, and reporting it here would make
            // the hardware report claim a measurement that was never taken.
            return byPattern.Sensors.Verified
                ? byPattern with { Sensors = byPattern.Sensors with { Verified = false } }
                : byPattern;
        }

        // The catch-all still has to claim the machine: a Dell laptop must not be handed the Acer default profile,
        // because its fan ceilings and sensor layout would then be reported as if they applied to it.
        var catchAll = _profiles.FirstOrDefault(p => p.AnyAcer && p.Matches(identity));
        if (catchAll != null) return catchAll;

        return new ModelProfile
        {
            Id = "unknown",
            Sensors = DefaultSensors,
            Source = "none",
            Notes = "Hiçbir profil eşleşmedi."
        };
    }

    /// <summary>Reads the embedded table out of the assembly. Returns "" when the resource is missing.</summary>
    public static string ReadEmbeddedJson()
    {
        try
        {
            var assembly = typeof(ProfileCatalog).Assembly;
            var name = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(".profiles.json", StringComparison.OrdinalIgnoreCase));
            if (name == null) return "";
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream == null) return "";
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch
        {
            return "";
        }
    }

    private static string ReadString(JsonObject node, string key) =>
        node[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    private static int? ReadInt(JsonObject node, string key) =>
        node[key] is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    private static bool? ReadBool(JsonObject node, string key) =>
        node[key] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    private static List<string> ReadStringArray(JsonObject node, string key)
    {
        var result = new List<string>();
        if (node[key] is not JsonArray array) return result;
        foreach (var item in array)
        {
            if (item is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
                result.Add(text.Trim());
        }

        return result;
    }
}
