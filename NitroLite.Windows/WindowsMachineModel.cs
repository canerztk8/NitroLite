namespace NitroLite.Windows;

/// <summary>
/// The machine model, for the prefilled GitHub issue.
/// </summary>
/// <remarks>
/// This is the one WMI read the shared update code cannot do itself: <c>NitroLite.Core</c> is compiled into the Linux
/// build, and a <c>System.Management</c> call there would drag the WMI stack into a process that has no COM. The
/// composition root hands this delegate to <see cref="NitroLite.Core.UpdateService.MachineModelProvider"/> once.
/// </remarks>
public static class WindowsMachineModel
{
    /// <summary>Manufacturer and model, or null when WMI cannot answer.</summary>
    public static string? Read()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Manufacturer, Model FROM Win32_ComputerSystem");
            using var results = searcher.Get();
            foreach (System.Management.ManagementObject item in results)
            {
                // ManagementObject wraps a native COM object; the collection does not dispose the items in it.
                using (item)
                {
                    var manufacturer = (item["Manufacturer"]?.ToString() ?? "").Trim();
                    var model = (item["Model"]?.ToString() ?? "").Trim();
                    var text = string.Join(" ", new[] { manufacturer, model }.Where(part => part.Length > 0));
                    if (text.Length > 0) return text;
                }
            }
        }
        catch
        {
            // A machine whose WMI namespace is broken has no model to report; the caller falls back to "unknown".
        }

        return null;
    }
}
