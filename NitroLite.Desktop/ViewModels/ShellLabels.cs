using NitroLite.Core;

namespace NitroLite.Desktop.ViewModels;

/// <summary>
/// Every fixed text the window shows, in one object.
/// </summary>
/// <remarks>
/// <para>
/// The alternative — a <c>Loc.T</c> call per XAML label through a markup extension — resolves once at load time, so
/// the language selector would need a restart to do anything. Here the whole object is replaced when the language
/// changes, which raises one <c>PropertyChanged</c> and repaints every label at once.
/// </para>
/// <para>
/// The Turkish text is the key, exactly as in the Windows front end: <see cref="Loc.T"/> looks it up in the active
/// catalogue and falls back to the key, so a missing translation shows Turkish rather than an empty control.
/// </para>
/// </remarks>
public sealed class ShellLabels
{
    // ---------------------------------------------------------------- shell
    public string AppTitle => "NitroLite";
    public string Dashboard => Loc.T("Kontrol Paneli");
    public string Lighting => Loc.T("Aydınlatma ve ekstralar");
    public string Settings => Loc.T("Ayarlar");
    public string Ready => Loc.T("Hazır");
    public string NotConnected => Loc.T("Bağlı değil");
    public string NoBackend => Loc.T("Donanım arka ucu yok");
    public string NoBackendDetail => Loc.T("Bu sürüm Linux sysfs arka ucunu kullanır. Windows'ta NitroLite (WPF) sürümünü kullanın.");
    public string NoAcerNodes => Loc.T("Bu makinede Acer donanım düğümü bulunamadı.");

    // ---------------------------------------------------------------- dashboard
    public string ThermalAndFans => Loc.T("Sıcaklık ve fan");
    public string Cpu => Loc.T("CPU");
    public string Gpu => Loc.T("GPU");
    public string Sys => Loc.T("Sistem (SYS1)");
    public string FanRpm => Loc.T("Fan devri");
    public string Measured => Loc.T("ölçüldü");
    public string Derived => Loc.T("tahmini");
    public string OperatingMode => Loc.T("Çalışma modu");
    public string FanMode => Loc.T("Fan modu");
    public string Auto => Loc.T("Otomatik");
    public string Max => Loc.T("Maksimum");
    public string CustomCurve => Loc.T("Özel eğri");
    public string CustomFanSpeed => Loc.T("Özel fan hızı");
    public string CpuFanPercent => Loc.T("CPU fan yüzdesi");
    public string GpuFanPercent => Loc.T("GPU fan yüzdesi");
    public string FanCurve => Loc.T("FAN EĞRİSİ");
    public string EnableCurve => Loc.T("Eğriyi etkinleştir");
    public string ApplyCurve => Loc.T("Eğriyi uygula");
    public string ThermalFloors => Loc.T("Termal güvenlik tabanları");
    public string ThermalFloorSummary => Loc.T("85 °C → %60 · 90 °C → %80 · 95 °C → %100");
    public string Apply => Loc.T("Uygula");

    // ---------------------------------------------------------------- lighting
    public string KeyboardLighting => Loc.T("Klavye Aydınlatması");
    public string Effect => Loc.T("Efekt");
    public string Brightness => Loc.T("Parlaklık");
    public string Speed => Loc.T("Hız");
    public string Direction => Loc.T("Yön");
    public string ZoneColors => Loc.T("Bölge renkleri");
    public string Zone1 => Zone(1);
    public string Zone2 => Zone(2);
    public string Zone3 => Zone(3);
    public string Zone4 => Zone(4);
    public string PrimaryColor => Loc.T("Ana renk");
    public string TurnOff => Loc.T("Kapat");
    public string Extras => Loc.T("Ekstralar");
    public string CoolBoost => Loc.T("CoolBoost");
    public string LcdOverdrive => Loc.T("LCD Overdrive");
    public string BacklightTimeout => Loc.T("Arka ışık zaman aşımı (30 sn)");
    public string BatteryLimit => Loc.T("Pil şarj sınırı (%80)");
    public string NotOnThisMachine => Loc.T("Bu cihazda yok");

    // ---------------------------------------------------------------- settings
    public string Device => Loc.T("Cihaz");
    public string Model => Loc.T("Model");
    public string Bios => Loc.T("BIOS");
    public string Tier => Loc.T("Katman");
    public string CapabilitySummary => Loc.T("Yetenek özeti");
    public string Language => Loc.T("Dil");
    public string DataFolder => Loc.T("Veri klasörü");
    public string Notes => Loc.T("Notlar");
    public string Rescan => Loc.T("Yeniden tara");
    public string UdevNeeded => Loc.T("udev kuralı gerekli");
    public string UdevInstruction => Loc.T("sysfs düğümlerine yazmak için udev kuralını kurun:");
    public string CopyCommand => Loc.T("Komutu kopyala");
    public string Copied => Loc.T("Kopyalandı");
    public string LanguageRestartHint => Loc.T("Dil değişikliği hemen uygulanır.");

    // ---------------------------------------------------------------- theme and charts
    public string Theme => Loc.T("Tema");
    public string ThemeApplied => Loc.T("Tema seçimi hemen uygulanır.");
    public string LastMinute => Loc.T("Son 60 saniye");

    // ---------------------------------------------------------------- MUX switch
    public string MuxSwitch => Loc.T("MUX Switch");
    public string GpuMode => Loc.T("GPU modu");
    public string GpuHybrid => Loc.T("Hibrit (Optimus)");
    public string GpuDiscrete => Loc.T("Harici GPU (Discrete)");
    public string MuxHint => Loc.T("Geçişten sonra oturumu kapatıp açmanız gerekebilir.");

    // ---------------------------------------------------------------- school mode
    public string SchoolMode => Loc.T("Okul modu");
    public string SchoolModeHint => Loc.T("Pil ömrünü en üst düzeye çıkarır: %40 parlaklık, 60 Hz, CPU Boost kapalı.");
    public string SchoolModeApplied => Loc.T("Okul modu uygulandı.");
    public string SchoolModeDisabled => Loc.T("Okul modu kapatıldı; önceki ayarlar geri yüklendi.");
    public string SchoolModeRefused => Loc.T("Okul modu uygulanamadı: geri alma kaydı yazılamadı.");
    public string SchoolModePartial => Loc.T("Okul modu kısmen uygulandı");
    public string SchoolModeRecovered => Loc.T("Yarım kalan okul modu geri alındı.");

    // ---------------------------------------------------------------- game detection
    public string GameMode => Loc.T("Oyun modu");
    public string GameIdle => Loc.T("Oyun yok; kullanıcı ayarları geçerli.");
    public string AutoGameMode => Loc.T("Otomatik oyun modu");
    public string AutoGameModeHint => Loc.T("Tanımlı bir oyun açıldığında moda ve fan eğrisine geçer, kapandığında geri döner.");

    /// <summary>"Oyun profili uygulandı: Counter-Strike 2".</summary>
    public string GameStarted(string name) => Loc.T($"Oyun profili uygulandı: {name}");

    /// <summary>"Oyun kapandı; önceki ayarlara dönüldü: Counter-Strike 2".</summary>
    public string GameEnded(string name) => Loc.T($"Oyun kapandı; önceki ayarlara dönüldü: {name}");

    // ---------------------------------------------------------------- RGB profiles
    public string RgbProfiles => Loc.T("RGB profilleri");
    public string ProfileName => Loc.T("Profil adı");
    public string SaveProfile => Loc.T("Profili kaydet");
    public string DeleteProfile => Loc.T("Profili sil");
    public string ApplyProfile => Loc.T("Profili uygula");
    public string ProfileNameRequired => Loc.T("Profil adı gerekli.");
    public string BuiltInProfileLocked => Loc.T("Fabrika profili silinemez.");
    public string Deleted => Loc.T("Silindi");
    public string DeleteFailed => Loc.T("Silinemedi");

    // ---------------------------------------------------------------- hardware report
    public string HardwareReport => Loc.T("Donanım raporu");
    public string HardwareReportHint => Loc.T("Seri numarası, UUID, kullanıcı adı ve kişisel dosya yolu içermez.");
    public string CopyReport => Loc.T("Raporu kopyala");
    public string ReportCopied => Loc.T("Donanım raporu panoya kopyalandı.");

    // ---------------------------------------------------------------- tray
    public string TrayShow => Loc.T("Pencereyi Göster");
    public string TrayCleanExit => Loc.T("Temiz Çıkış");
    public string TrayQuickMode => Loc.T("Hızlı mod");
    public string MinimizedToTray => Loc.T("Arka planda çalışıyor. Kapatmak için sistem tepsisinden Temiz Çıkış'ı kullanın.");
    public string CleanExitDone => Loc.T("Fanlar otomatiğe alındı; çıkılıyor.");
    public string CleanExitPartial => Loc.T("Fanlar otomatiğe alınamadı; yine de çıkılıyor.");

    // ---------------------------------------------------------------- capability names
    public string CapFanControl => Loc.T("Fan kontrolü");
    public string CapOperatingMode => Loc.T("Çalışma modu kontrolü");
    public string CapMux => Loc.T("MUX Switch");
    public string CapBatteryLimit => Loc.T("Pil şarj sınırı");
    public string CapKeyboard => Loc.T("Klavye aydınlatma bölgeleri");
    public string CapBacklightTimeout => Loc.T("Arka ışık zaman aşımı");
    public string CapCoolBoost => Loc.T("CoolBoost");
    public string CapLcdOverdrive => Loc.T("LCD overdrive");
    public string CapGpuOverclock => Loc.T("GPU hız aşırtma");

    // ---------------------------------------------------------------- status
    public string Applied => Loc.T("Uygulandı");
    public string WriteFailed => Loc.T("Yazma başarısız");
    public string LightingFailed => Loc.T("Aydınlatma ayarı uygulanamadı");
    public string CurveEnabled => Loc.T("Fan eğrisi etkin: fan hızı CPU sıcaklığını izliyor.");
    public string CurveDisabled => Loc.T("Fan eğrisi kapandı; fanlar otomatik moda döndü.");

    /// <summary>"güvenlik tabanı %60" for an anchor that sits on or above a floor.</summary>
    public string SafetyFloor(int percent) => Loc.T($"güvenlik tabanı %{percent}");

    /// <summary>"Sıcaklık 70 °C → fan %55" for the curve preview line.</summary>
    public string CurvePoint(int temperatureC, int percent) => Loc.T($"Sıcaklık {temperatureC} °C → fan %{percent}");

    /// <summary>"Bölge 1" for a zone colour row.</summary>
    public string Zone(int index) => Loc.T($"Bölge {index}");
}
