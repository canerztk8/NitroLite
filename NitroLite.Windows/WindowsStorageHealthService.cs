using NitroLite.Core;

namespace NitroLite.Windows;

/// <summary>
/// The drives, read through <see cref="StorageHealthService"/>.
/// </summary>
/// <remarks>
/// The WMI storage namespace is slow — a cold query walks every physical disk and every partition — so the read is
/// pushed onto a worker by the caller and the result is mapped here rather than being re-shaped in the view model.
/// </remarks>
public sealed class WindowsStorageHealthService : IStorageHealthService
{
    private readonly StorageHealthService _inner;

    public WindowsStorageHealthService(StorageHealthService? inner = null) => _inner = inner ?? new StorageHealthService();

    public bool IsSupported => true;

    public IReadOnlyList<StorageDrive> GetDrives()
    {
        List<StorageDriveInfo> raw;
        try
        {
            raw = _inner.GetDrives();
        }
        catch (Exception ex)
        {
            // A machine whose storage namespace is missing has no list, not a broken page.
            AppLog.Warn("The storage drives could not be read", ex.Message);
            return Array.Empty<StorageDrive>();
        }

        return raw
            .OrderBy(d => d.DriveLetter, StringComparer.OrdinalIgnoreCase)
            .Select(d => new StorageDrive(
                DriveLetter: string.IsNullOrWhiteSpace(d.DriveLetter) ? "" : d.DriveLetter.TrimEnd(':') + ":",
                Model: d.Model,
                BusType: d.BusType,
                TotalGigabytes: d.TotalCapacityGB,
                FreeGigabytes: d.FreeCapacityGB,
                UsedPercent: d.UsedPercent,
                TemperatureC: d.Temperature,
                HealthText: d.HealthDesc,
                Healthy: d.HealthStatus == 0))
            .ToList();
    }
}
