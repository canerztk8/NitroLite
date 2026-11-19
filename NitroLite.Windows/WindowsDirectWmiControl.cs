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
            if (IsAvailable) return Loc.T("Doğrudan WMI bağlı");

            // This row used to say "the Acer service is in use" whenever the helper was not connected, including on a
            // machine where the service answered nothing at all. On the AN16-61 report that sentence was the reason
            // the app looked healthy while every page was empty, so a refusal now says what was refused and why.
            return _hybrid.DirectWmiRefusal switch
            {
                WmiWorkerRefusal.UserDeclined => Loc.T("Yönetici izni verilmedi; Acer servisi kullanılıyor"),
                WmiWorkerRefusal.UntrustedLocation => Loc.T(
                    "Klasör güvenli değil: NitroLite'ı yönetici olarak başlatın ya da Program Files'a kurun."),
                WmiWorkerRefusal.StartFailed or WmiWorkerRefusal.HandshakeFailed => Loc.T(
                    "Doğrudan WMI başlatılamadı"),
                _ => Loc.T("Acer servisi kullanılıyor")
            };
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
