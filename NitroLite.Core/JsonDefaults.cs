using System.Text.Json;

namespace NitroLite.Core;

/// <summary>
/// The serializer options the app writes its files with.
/// </summary>
/// <remarks>
/// A <see cref="JsonSerializerOptions"/> instance is not cheap: the first use builds and caches a metadata contract
/// for every type it sees, and a fresh instance per call throws that cache away and rebuilds it. Every save path in
/// the app used to do exactly that — on the UI thread, once per settings change.
/// </remarks>
public static class JsonDefaults
{
    /// <summary>Indented output, for the files a user may open and read.</summary>
    public static JsonSerializerOptions Indented { get; } = new() { WriteIndented = true };
}
