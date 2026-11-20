using System.Text.Json.Serialization;

namespace NitroLite.Core.Hardware.Diagnostics;

/// <summary>
/// The source-generated serializer for <see cref="HardwareDiagnosticReport"/>.
/// </summary>
/// <remarks>
/// <para>
/// Generated rather than reflected so the payload survives <c>PublishTrimmed</c> with <c>TrimMode=partial</c>: the
/// generator emits a <c>JsonTypeInfo</c> for the one type this assembly sends, and the trimmer keeps exactly the
/// members that metadata names. A <c>JsonSerializer.Serialize(report)</c> call with no options would instead need the
/// report's properties by reflection, and <c>IL2026</c> is the warning that says so.
/// </para>
/// <para>
/// <c>internal</c> because nothing outside this assembly serialises a report. The test suite can still reach it
/// through the project's <c>InternalsVisibleTo</c>, which is how the payload-shape test pins the wire contract.
/// </para>
/// </remarks>
[JsonSerializable(typeof(HardwareDiagnosticReport))]
internal sealed partial class HardwareDiagnosticJsonContext : JsonSerializerContext
{
}
