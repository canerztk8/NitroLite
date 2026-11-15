using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using NitroLite.Core;

namespace NitroLite;

public partial class MainWindow
{
    /// <summary>
    /// Releases the working set after the window is hidden.
    /// <para>
    /// This used to run a blocking, compacting, aggressive generation-2 collection and then empty the working set.
    /// Both halves made the app slower, not lighter: the collection froze the UI thread for a few hundred
    /// milliseconds on every minimise, and emptying the working set only meant every page had to be faulted back
    /// in the next time the window was shown. The runtime's own collector already does this correctly, so the only
    /// thing left is the working-set trim, which is cheap and does not touch the heap.
    /// </para>
    /// <para>
    /// Returns what the kernel said. The old signature took <c>int</c> for the two size arguments, which on x64
    /// passed 0x00000000FFFFFFFF instead of the all-ones value that means "trim every page": the call succeeded
    /// and trimmed nothing. The arguments are <c>nint</c> now, so -1 really is -1.
    /// </para>
    /// </summary>
    public static bool TrimMemory()
    {
        try
        {
            return SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
        }
        catch
        {
            return false;
        }
    }

    private async void BtnCleanRamCache_Click(object sender, RoutedEventArgs e)
    {
        if (_ramCleanBusy) return;
        _ramCleanBusy = true;
        if (BtnCleanRamCache != null) BtnCleanRamCache.Opacity = 0.45;
        try
        {
            _ramCleanResultShown = false;
            SetRamCleanStatus(Loc.T(" (Boşaltılıyor…)"), "TextSecondary");
            var res = await _memoryService.CleanMemoryAsync();
            ShowMemoryCleanResult(res);
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T($"Bekleme belleği boşaltılamadı: {ex.Message}"), false);
        }
        finally
        {
            _ramCleanBusy = false;
            await RefreshRamCleanStateAsync(keepResultText: true);
        }
    }

    /// <summary>Opens the official WinMemoryCleaner release page; only shown when the bundled copy is missing.</summary>
    private void BtnGetMemoryCleaner_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(MemoryCleanerService.ToolReleasesUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T($"Bağlantı açılamadı: {ex.Message}"), false);
        }
    }

    private void ShowMemoryCleanResult(MemoryCleanerResult res)
    {
        if (res.ToolMissing)
        {
            ShowToast(Loc.T("WinMemoryCleaner kurulu değil. Buton, resmi indirme sayfasını açar."), false);
            SetRamCleanStatus(Loc.T(" (Araç kurulu değil)"), "TextSecondary");
            return;
        }
        if (res.ElevationRefused)
        {
            ShowToast(Loc.T("Yönetici izni verilmedi; bekleme belleği boşaltılmadı."), false);
            SetRamCleanStatus(Loc.T(" (İzin verilmedi)"), "TextSecondary");
            return;
        }
        if (!res.Cleaned)
        {
            ShowToast(Loc.T($"Bekleme belleği boşaltılamadı: {res.Error}"), false);
            SetRamCleanStatus(Loc.T(" (Başarısız)"), "AccentRed");
            return;
        }

        _ramCleanResultShown = true;
        string freedStr = res.FreedMegabytes >= 1024
            ? FormattableString.Invariant($"{res.FreedMegabytes / 1024.0:F1} GB")
            : FormattableString.Invariant($"{res.FreedMegabytes:F0} MB");
        if (res.FreedMegabytes <= 0)
        {
            ShowToast(Loc.T("Bekleme belleği zaten temiz (boşaltılacak önbellek yok)."), isSuccess: true, trayBalloon: false);
            SetRamCleanStatus(Loc.T(" (Zaten temiz)"), "AccentGreen");
        }
        else
        {
            // The standby list is cached memory, not "in use" memory, so the used-RAM bar never moves. Available
            // memory is the figure that does move, and showing it before and after is what makes the result
            // checkable against Task Manager instead of asking the user to trust one number.
            string before = FormattableString.Invariant($"{res.AvailableBeforeGigabytes:F1}");
            string after = FormattableString.Invariant($"{res.AvailableGigabytes:F1}");
            ShowToast(Loc.T($"Önbellek boşaltıldı: {freedStr} serbest bırakıldı, kullanılabilir bellek {before} → {after} GB."), isSuccess: true, trayBalloon: false);
            SetRamCleanStatus(Loc.T($" ({freedStr} serbest bırakıldı)"), "AccentGreen");
        }
        UpdateMemoryListText();
    }

    private void SetRamCleanStatus(string text, string brushKey)
    {
        if (TxtRamCleanStatus == null) return;
        TxtRamCleanStatus.Text = text;
        TxtRamCleanStatus.Foreground = (SolidColorBrush)FindResource(brushKey);
    }

    /// <summary>Points the card at whichever state the machine is in: tool present, or a link to get it.</summary>
    private Task RefreshRamCleanStateAsync(bool keepResultText = false)
    {
        bool installed = MemoryCleanerService.FindTool() != null;

        if (BtnCleanRamCache != null) BtnCleanRamCache.Opacity = 1.0;
        if (BtnGetMemoryCleaner != null)
            BtnGetMemoryCleaner.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;

        if (!(keepResultText && _ramCleanResultShown))
        {
            if (installed) SetRamCleanStatus(Loc.T(" (Hazır)"), "AccentGreen");
            else SetRamCleanStatus(Loc.T(" (Araç kurulu değil)"), "TextSecondary");
        }
        UpdateMemoryListText();
        return Task.CompletedTask;
    }

    /// <summary>Live standby/free sizes in the RAM card (readable without administrator rights).</summary>
    private void UpdateMemoryListText()
    {
        if (TxtRamCleanNote == null) return;
        var lists = MemoryCleanerService.ReadMemoryLists();
        string sizes = lists.HasValue
            ? Loc.TInv($"Şu an bekleme listesinde {lists.Value.StandbyGigabytes:F1} GB, boşta {lists.Value.FreeGigabytes:F1} GB var. ")
            : "";
        TxtRamCleanNote.Text = sizes + Loc.T("Windows'un önbellekte tuttuğu (bekleme listesi) belleği boş belleğe çevirir; çalışan programların belleğine dokunmaz, bu yüzden kullanılan RAM çubuğu değişmez. ")
                               + Loc.T("Oyunun kendi bellek sızıntısını geri vermez; boş bellek azaldığında oluşan takılmalara karşı işe yarar.");
    }
}
