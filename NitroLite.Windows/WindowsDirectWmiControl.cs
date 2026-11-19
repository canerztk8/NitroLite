using NitroLite.Core;
using NitroLite.Core.Hardware.Wmi;

namespace NitroLite.Windows;

/// <summary>
/// The direct-WMI backend, exposed as the one row the settings page carries.
/// </summary>
/// <remarks>
/// The hybrid controller already owns the decision of which backend answers and whether the user declined the prompt;
/// this adapter only turns that into the two strings and the one call the shared window understands.
/// </remarks>
public sealed class WindowsDirectWmiControl : IDirectWmiControl
{
    private readonly HybridHardwareController _hybrid;

    public WindowsDirectWmiControl(HybridHardwareController hybrid) =>
        _hybrid = hybrid ?? throw new ArgumentNullException(nameof(hybrid));

    public bool IsAvailable => _hybrid.DirectWmi?.IsAvailable == true;

    public string StatusText
    {
        get
        {
            if (_hybrid.UserDeclinedElevation) return Loc.T("Yönetici izni verilmedi; Acer servisi kullanılıyor");
            return IsAvailable ? Loc.T("Doğrudan WMI bağlı") : Loc.T("Acer servisi kullanılıyor");
        }
    }

    public async Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _hybrid.StartDirectWmiAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Direct WMI could not be started", ex.ToString());
            return false;
        }
    }
}
