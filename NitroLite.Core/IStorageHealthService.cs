namespace NitroLite.Core;

/// <summary>
/// One drive as the storage card lists it: the letter it is mounted under, what it is, and how healthy it is.
/// </summary>
/// <remarks>
/// <see cref="TemperatureC"/> is null rather than zero when the drive does not report one, because 0 °C is a real
/// temperature and a drive that answers nothing must not be drawn as a frozen one.
/// </remarks>
public sealed record StorageDrive(
    string DriveLetter,
    string Model,
    string BusType,
    double TotalGigabytes,
    double FreeGigabytes,
    double UsedPercent,
    int? TemperatureC,
    string HealthText,
    bool Healthy)
{
    /// <summary>The bracketed letter the card prints, e.g. <c>[C:]</c>.</summary>
    public string LetterTag => DriveLetter.Length > 0 ? $"[{DriveLetter}]" : "[--]";

    /// <summary>Free space in the terms the card prints, e.g. <c>412,3 GB boş</c>.</summary>
    public string FreeText => $"{FreeGigabytes:F1} / {TotalGigabytes:F1} GB";
}

/// <summary>
/// Lists the machine's drives and what S.M.A.R.T. says about them.
/// </summary>
/// <remarks>
/// Windows reads the storage namespace through WMI; Linux has no equivalent that works without root on every
/// distribution, so its implementation reports <see cref="IsSupported"/> false and the card hides itself rather than
/// offering a list that is always empty.
/// </remarks>
public interface IStorageHealthService
{
    /// <summary>True when this platform can enumerate drives with their health.</summary>
    bool IsSupported { get; }

    /// <summary>Every fixed drive, ordered by letter. Never throws; an unreadable drive is simply absent.</summary>
    IReadOnlyList<StorageDrive> GetDrives();
}
