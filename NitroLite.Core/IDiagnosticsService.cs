namespace NitroLite.Core;

/// <summary>
/// Packs the log, the hardware report, the capability snapshot and the settings into one zip.
/// </summary>
/// <remarks>
/// <para>
/// This is the answer to "it does not work": one file to attach, instead of walking the user through four separate
/// exports and hoping they pick the right one. The bundle is written into the data folder and its path is returned so
/// the card can name the file it just made.
/// </para>
/// <para>
/// The privacy rule is the same one the hardware report follows: no serial number, no machine UUID, no user name and
/// no personal file path. The log is included because it is the only artefact that says what the app tried.
/// </para>
/// </remarks>
public interface IDiagnosticsService
{
    /// <summary>True when this platform can build a bundle.</summary>
    bool IsSupported { get; }

    /// <summary>The folder the bundle is written into, and the one the "open logs" button opens.</summary>
    string DataDirectory { get; }

    /// <summary>Writes the bundle and returns its full path. Throws only when the folder itself is unwritable.</summary>
    string CreateBundle();
}
