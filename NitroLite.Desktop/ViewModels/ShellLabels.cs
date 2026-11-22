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
    public string NoBackendDetail => Loc.T("Donanım arka ucu yanıt vermedi. Linux'ta acer-wmi veya Linuwu-Sense modülünün yüklü olduğunu, Windows'ta uygulamanın yönetici olarak çalıştığını denetleyin.");
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

    // The one line under each fan card's title.
    public string FanAutoHint => Loc.T("Firmware sıcaklığa göre kendi hızını seçer.");
    public string FanMaxHint => Loc.T("Fanlar sürekli en yüksek devirde döner.");
    public string FanCustomHint => Loc.T("Hızı aşağıdaki eğri belirler; eğriyi sen çizersin.");
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
    public string BatteryLimitHint => Loc.T("Pil %80'e ulaştığında şarj durur; pil ömrünü uzatır.");
    public string NotOnThisMachine => Loc.T("Bu cihazda yok");

    // ---------------------------------------------------------------- settings
    public string Device => Loc.T("Cihaz");
    public string Model => Loc.T("Model");
    public string Bios => Loc.T("BIOS");
    public string Tier => Loc.T("Katman");
    public string TierNone => Loc.T("Yok / Desteklenmiyor");
    public string TierAcerWmi => Loc.T("acer-wmi (çekirdek modülü)");
    public string TierLinuwuSense => Loc.T("Linuwu-Sense (topluluk modülü)");
    public string TierBoth => Loc.T("acer-wmi + Linuwu-Sense");
    public string TierWmi => Loc.T("Doğrudan WMI (yükseltilmiş yardımcı)");
    public string TierAassvc => Loc.T("Acer hizmeti (AASSvc)");
    public string CapabilitySummary => Loc.T("Yetenek özeti");
    public string Language => Loc.T("Dil");
    public string DataFolder => Loc.T("Veri klasörü");
    public string Rescan => Loc.T("Yeniden tara");
    public string UdevNeeded => Loc.T("udev kuralı gerekli");
    public string UdevInstruction => Loc.T("Ya da bu komutu terminalde çalıştırın:");
    public string LinuxSetupTitle => Loc.T("Linux kurulumu");
    public string LinuxGrantHint => Loc.T("Uygulama çalışma modu, klavye ve Nitro tuşu gibi ayarları değiştirebilmek için bir kez izin istiyor. Düğme yönetici şifrenizi sorar; ardından oturumu kapatıp yeniden açın.");
    public string LinuxGrantButton => Loc.T("Etkinleştir");
    public string LinuxPredatorV4Title => Loc.T("Fan devri ve Acer modları (deneysel)");
    public string LinuxPredatorV4Hint => Loc.T("Çekirdek bu modeli henüz tanımıyor. acer-wmi modülünün predator_v4 seçeneğini açmak fan devrini ve Acer'in çalışma modlarını getirebilir. Yeniden başlatma gerekir; sorun çıkarsa /etc/modprobe.d/nitrolite-acer.conf dosyasını silmek yeterli.");
    public string LinuxPredatorV4Button => Loc.T("predator_v4'ü aç");
    public string LinuxLinuwuTitle => Loc.T("Fan hızı, klavye RGB ve şarj sınırı");
    public string LinuxLinuwuHint => Loc.T("Bu ayarlar Linux'ta yalnızca Linuwu-Sense çekirdek modülüyle değiştirilebilir. Modül kurulduğunda uygulama onu kendiliğinden bulur.");
    public string LinuxLinuwuButton => Loc.T("Linuwu-Sense'i aç");
    public string TrayUnavailableHint => Loc.T("Bu masaüstü tepsi simgelerini göstermiyor (GNOME'da AppIndicator eklentisi gerekir). Pencereyi kapatmak onu küçültür ve uygulama açık kalır; tepside başlatma yerine pencere açılır.");
    public string LinuxCaptureTitle => Loc.T("Bu laptopu desteklememize yardım edin");
    public string LinuxCaptureHint => Loc.T("Uygulamanın okuduğu donanım bilgilerini tek bir dosyaya kaydeder ve GitHub'da bir kayıt açar. Seri numarası, bilgisayar adı ya da kullanıcı adı içermez. Gönderilen her laptop, aynı modeldeki herkes için desteği iyileştirir.");
    public string LinuxCaptureButton => Loc.T("Linux bilgilerini gönder");
    public string LinuxCaptureFailed => Loc.T("Kayıt dosyası yazılamadı.");
    public string LinuxCaptureSent(string file) => Loc.T($"{file} kaydedildi. Açılan klasördeki dosyayı GitHub sayfasına sürükleyin.");
    public string CopyCommand => Loc.T("Komutu kopyala");
    public string Copied => Loc.T("Kopyalandı");
    public string LanguageRestarting => Loc.T("Dil değişti; NitroLite yeni dille yeniden açılıyor.");

    // ---------------------------------------------------------------- theme and charts
    public string Theme => Loc.T("Tema");
    public string ThemeApplied => Loc.T("Tema seçimi hemen uygulanır.");
    public string UiFont => Loc.T("Yazı tipi");
    public string FontApplied => Loc.T("Yazı tipi hemen uygulanır.");
    public string LastMinute => Loc.T("Seçili metriğin son 60 saniyesi");

    // ---------------------------------------------------------------- sign-in launch
    public string AutoStart => Loc.T("Görev Zamanlayıcı ile UAC'siz otomatik başlatma");
    public string AutoStartEnabled => Loc.T("Otomatik başlatma açıldı.");
    public string AutoStartDisabled => Loc.T("Otomatik başlatma kapatıldı.");
    public string AutoStartFailed => Loc.T("Otomatik başlatma değiştirilemedi.");

    public string AutoStartRefusedDownloadFolder => Loc.T(
        "NitroLite İndirilenler ya da geçici bir klasörden çalışıyor, bu yüzden otomatik başlatmaya eklenmiyor. NitroLite'ı Program Files'a ya da %LOCALAPPDATA%\\NitroLite klasörüne taşıyıp tekrar deneyin.");

    public string AutoStartRefusedWritableFolder => Loc.T(
        "NitroLite başka hesapların değiştirebileceği bir klasörde, bu yüzden otomatik başlatmaya eklenmiyor. NitroLite'ı Program Files'a ya da %LOCALAPPDATA%\\NitroLite klasörüne taşıyıp tekrar deneyin.");

    public string AutoRefreshRate => Loc.T("Pilde otomatik 60 Hz'e düşür");
    public string AutoRefreshRateHint => Loc.T("Pil gücüne geçildiğinde panel 60 Hz'e iner; prize takılınca önceki hız geri gelir.");
    public string RefreshRateLowered => Loc.T("Pil gücünde panel 60 Hz'e düşürüldü.");
    public string RefreshRateRestored => Loc.T("Prize takıldı, önceki yenileme hızı geri getirildi.");
    public string RefreshRateFailed => Loc.T("Ekran yenileme hızı değiştirilemedi.");

    // ---------------------------------------------------------------- MUX switch
    public string MuxSwitch => Loc.T("MUX Switch");
    public string GpuMode => Loc.T("GPU modu");
    public string GpuHybrid => Loc.T("Hibrit (Optimus)");
    public string GpuDiscrete => Loc.T("Harici GPU (Discrete)");
    public string GpuAutomatic => Loc.T("Otomatik Seçim (DDS / Advanced Optimus)");
    public string MuxHint => Loc.T("Geçişten sonra oturumu kapatıp açmanız gerekebilir.");

    /// <summary>
    /// Shown under the MUX row on a machine that has no switch. The row stays visible and disabled rather than
    /// disappearing: a feature that vanishes reads as a bug, one that is greyed out with a reason reads as an answer.
    /// </summary>
    public string MuxUnavailableNote => Loc.T("Bu cihazda MUX anahtarı yok; seçim devre dışı.");

    // ---------------------------------------------------------------- school mode
    public string SchoolMode => Loc.T("Okul modu");
    public string SchoolModeHint => Loc.T("Pil ömrünü en üst düzeye çıkarır: %40 parlaklık, 60 Hz, CPU Boost kapalı.");

    /// <summary>
    /// The state beside the School Mode switch. A switch alone says what pressing it would do, not what is true now;
    /// the WPF page carried the same word inside the checkbox, and it reads correctly in both positions.
    /// </summary>
    public string SchoolModeStateOn => Loc.T("Etkin");
    public string SchoolModeStateOff => Loc.T("Kapalı");
    public string SchoolModeApplied => Loc.T("Okul modu uygulandı.");
    public string SchoolModeDisabled => Loc.T("Okul modu kapatıldı; önceki ayarlar geri yüklendi.");
    public string SchoolModeRefused => Loc.T("Okul modu uygulanamadı: geri alma kaydı yazılamadı.");
    public string SchoolModePartial => Loc.T("Okul modu kısmen uygulandı");
    public string SchoolModeRecovered => Loc.T("Yarım kalan okul modu geri alındı.");

    // ---------------------------------------------------------------- game detection
    public string GameMode => Loc.T("Oyun modu");
    public string GameIdle => Loc.T("Oyun yok; kullanıcı ayarları geçerli.");
    public string AutoGameMode => Loc.T("Otomatik oyun modu");

    // ---------------------------------------------------------------- game profiles page
    public string GameProfiles => Loc.T("Oyun Profilleri");
    public string GameProfilesHint => Loc.T("Bir oyun açıldığında hangi moda ve fan ayarına geçileceğini buradan tanımla.");
    public string NewGameExe => Loc.T("Süreç adı");
    public string NewGameName => Loc.T("Görünen ad (isteğe bağlı)");
    public string AddGame => Loc.T("Oyun ekle");
    public string TargetMode => Loc.T("Hedef mod");
    public string TargetFanMode => Loc.T("Hedef fan modu");
    public string NoGameProfiles => Loc.T("Henüz tanımlı oyun yok.");
    public string GameExeRequired => Loc.T("Süreç adı gerekli.");
    public string GameAlreadyDefined => Loc.T("Bu süreç için zaten bir kural var.");
    public string GameAdded(string name) => Loc.T($"Oyun eklendi: {name}");
    public string GameDeleted(string name) => Loc.T($"Oyun silindi: {name}");

    /// <summary>"Oyun profili uygulandı: Counter-Strike 2".</summary>
    public string GameStarted(string name) => Loc.T($"Oyun profili uygulandı: {name}");

    /// <summary>"Oyun kapandı; önceki ayarlara dönüldü: Counter-Strike 2".</summary>
    public string GameEnded(string name) => Loc.T($"Oyun kapandı; önceki ayarlara dönüldü: {name}");

    // ---------------------------------------------------------------- game library

    public string ScanGames => Loc.T("Yüklü Oyunları Tara");
    public string ScanningGames => Loc.T("Taranıyor…");
    public string AddGameFromFile => Loc.T("Oyun Ekle (.exe)");
    public string GamesScanned(int count) => Loc.T($"{count} yeni oyun profili eklendi.");
    public string NoNewGames => Loc.T("Yeni yüklü oyun tespit edilmedi.");
    public string GameScanFailed => Loc.T("Oyun kitaplığı taranamadı.");
    public string GameScanUnavailable => Loc.T("Bu platformda oyun kitaplığı taraması yok.");
    public string GameToConfigure => Loc.T("Yapılandırılacak Oyun");
    public string GameExePath => Loc.T("Çalıştırılabilir dosya");
    public string GameTargets => Loc.T("Oyun hedefleri");
    public string GameCpuBoost => Loc.T("CPU Boost Davranışı");
    public string GameFanProfile => Loc.T("Fan Profili");
    public string GameOwnCurve => Loc.T("Bu oyun için özel fan eğrisi");
    public string CopyDesktopCurve => Loc.T("Masaüstü eğrisini kopyala");
    public string GameCurveCopied(string name) => Loc.T($"Masaüstü eğrisi kopyalandı: {name}");

    /// <summary>The caption above the six per-game curve anchors.</summary>
    public string GameFanCurveCaption => Loc.T("OYUN FAN EĞRİSİ");

    /// <summary>The switch that decides whether a rule is applied on its own when the game starts.</summary>
    public string GameAutoApply => Loc.T("Bu oyun açılınca profili uygula");

    public string GameAutoApplyTooltip => Loc.T("Yalnızca bu oyun için geçerli. Kapalıysa oyun algılanır ama bu profili kendiliğinden uygulamaz; elle çalıştırılabilir.");

    public string DeleteProfileTitle => Loc.T("Profili sil");
    public string DeleteProfileConfirm(string name) => Loc.T($"'{name}' profili silinsin mi? Bu işlem geri alınamaz.");
    public string GameSessionRecovered => Loc.T("Yarım kalan oyun oturumu geri alındı; önceki mod ve fan ayarları geri yüklendi.");

    /// <summary>
    /// Shown when the record that undoes a game session could not be written. The session still starts, so the
    /// message says what is at risk rather than claiming the game did not launch.
    /// </summary>
    public string GameSessionRecoveryNotSaved =>
        Loc.T("Yarım kalan oyun oturumu kaydı diske yazılamadı; uygulama çökerse mod ve fan geri alınamayabilir.");

    /// <summary>Shown when the settings file was unreadable and the backup took its place.</summary>
    public string SettingsRestoredFromBackup =>
        Loc.T("Ayarlar dosyası okunamadı; önceki ayarlar yedekten geri yüklendi.");

    /// <summary>Shown when the settings file was unreadable and there was no backup to fall back to.</summary>
    public string SettingsResetToDefaults =>
        Loc.T("Ayarlar dosyası okunamadı ve yedek yok; uygulama varsayılan ayarlarla başladı.");

    // ---------------------------------------------------------------- mouse

    public string MouseCaption => Loc.T("FARE");
    public string MouseProfiles => Loc.T("Fare profili");
    public string MousePassive => Loc.T("Değiştirme (Pasif)");
    public string MousePassiveApplied => Loc.T("Fare profili değiştirilmedi.");
    public string MouseProfileApplied(int profile) => Loc.T($"Fare profili uygulandı: Config {profile}");

    public string MouseDefaultAfterGame(string game) => Loc.T($"Varsayılan fare profili kaydedildi; {game} kapanınca uygulanacak.");
    public string MouseProfileFailed => Loc.T("Fare profili uygulanamadı; cihaz yanıt vermedi.");
    public string DefaultMouseProfile => Loc.T("Varsayılan Fare Profili");

    // ---------------------------------------------------------------- fan curve presets

    public string CurvePresets => Loc.T("ŞABLON");

    // ---------------------------------------------------------------- MUX and desktop restore

    public string GpuPendingDiscrete => Loc.T("Harici GPU seçildi. Değişiklik yeniden başlatmadan sonra etkin olur.");
    public string GpuPendingHybrid => Loc.T("Hibrit mod seçildi. Değişiklik yeniden başlatmadan sonra etkin olur.");
    public string GpuPendingAutomatic => Loc.T("Otomatik seçim kaydedildi. Değişiklik yeniden başlatmadan sonra etkin olur.");

    /// <summary>
    /// Shown under the MUX row when the backend refused the mode. Automatic is the one that gets refused: the Linux
    /// module has no third state, and a Windows machine whose capability bitmask omits it has no third state either.
    /// The sentence names the two modes that do work rather than leaving the user to guess which one to pick.
    /// </summary>
    public string GpuAutomaticNotWritable => Loc.T("Otomatik seçim bu cihazda yazılamıyor; firmware bu modu kendisi yönetiyor. Hibrit veya Harici GPU seçin.");

    /// <summary>
    /// The status line after a MUX write. "Uygulandı" is the wrong word for a setting the firmware only reads at
    /// POST, and it is what made a staged switch look like one that had already happened.
    /// </summary>
    public string GpuModeStaged => Loc.T("GPU modu kaydedildi; yeniden başlatınca etkin olur.");
    public string GpuPendingBadge => Loc.T("YENİDEN BAŞLAT");
    public string DesktopRestoreDone => Loc.T("Masaüstü ayarları yeniden uygulandı.");
    public string DesktopRestoreFailed => Loc.T("Masaüstü ayarları hâlâ uygulanamıyor.");
    public string TrayRetryDesktop => Loc.T("Masaüstü ayarlarını yeniden dene");

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
    public string ReportCompatibility => Loc.T("Raporu GitHub'da paylaş");
    public string CompatibilityReportOpened => Loc.T("Donanım raporu panoya kopyalandı; GitHub'da yeni sorun açıldı.");

    public string CompatibilityReportFailed(string error) => Loc.T($"Uyumluluk raporu paylaşılamadı: {error}");

    // ---------------------------------------------------------------- NitroSense snapshot diff
    public string HelperFolderWarningTitle => Loc.T("Donanım kontrolü kapalı: klasör güvenli değil");

    public string HelperFolderWarningBody => Loc.T(
        "NitroLite, başka hesapların değiştirebileceği bir klasörden çalışıyor; bu yüzden yönetici yardımcısını başlatmıyor. Fan, mod ve ışık kontrolleri için NitroLite'ı Program Files'a ya da %LOCALAPPDATA%\\NitroLite klasörüne taşıyın veya yönetici olarak başlatın.");

    public string SnapshotDiffTitle => Loc.T("NitroSense anlık görüntü farkı");

    public string SnapshotDiffHint => Loc.T("NitroSense'te başlangıç ayarını yap ve A'yı yakala; ayarı değiştirdikten sonra B'yi yakala. Fark ve rapor panoya kopyalanır.");

    public string SnapshotCaptureA => Loc.T("Durum A'yı yakala");
    public string SnapshotCaptureB => Loc.T("Durum B'yi yakala ve karşılaştır");

    public string SnapshotCapturedA(int entries) => Loc.T(
        $"Durum A alındı ({entries} kayıt). Şimdi NitroSense'te ayarı değiştir ve B'yi yakala.");

    public string SnapshotDiffCaptured(int changes) => Loc.T(
        $"Fark yakalandı ({changes} değişiklik); panoya kopyalandı.");

    public string SnapshotDiffSame => Loc.T(
        "İki anlık görüntü arasında fark yok; rapor yine de panoya kopyalandı.");

    public string SnapshotDiffFailed(string error) => Loc.T($"Anlık görüntü alınamadı: {error}");

    /// <summary>
    /// The inline hint under a control this session could not confirm. The control names come from the hardware report
    /// vocabulary, so they stay English in every language — they are the same words the issue will be triaged with.
    /// </summary>
    public string VerificationHint(string controls) => Loc.T($"Doğrulanamayan denetimler: {controls}");

    // ---------------------------------------------------------------- tray
    public string TrayShow => Loc.T("Pencereyi Göster");
    public string TrayCleanExit => Loc.T("Temiz Çıkış");
    public string TrayQuickMode => Loc.T("Hızlı mod");
    public string TrayCpuBoost => Loc.T("CPU Boost");
    public string CleanExitDone => Loc.T("Fanlar otomatiğe alındı; çıkılıyor.");
    public string CleanExitPartial => Loc.T("Fanlar otomatiğe alınamadı; yine de çıkılıyor.");

    // ---------------------------------------------------------------- capability names
    public string CapFanControl => Loc.T("Fan kontrolü");
    public string CapOperatingMode => Loc.T("Çalışma modu kontrolü");
    public string CapMux => Loc.T("MUX Switch");
    public string CapBatteryLimit => Loc.T("Pil şarj sınırı");
    public string CapKeyboard => Loc.T("Klavye aydınlatma bölgeleri");
    public string CapBacklightTimeout => Loc.T("Arka ışık zaman aşımı");
    public string CapGpuOverclock => Loc.T("GPU hız aşırtma");

    // ---------------------------------------------------------------- status
    public string Applied => Loc.T("Uygulandı");
    public string WriteFailed => Loc.T("Yazma başarısız");
    public string LightingFailed => Loc.T("Aydınlatma ayarı uygulanamadı");
    public string CurveEnabled => Loc.T("Fan eğrisi etkin: fan hızı CPU sıcaklığını izliyor.");
    public string CurveDisabled => Loc.T("Fan eğrisi kapandı; fanlar otomatik moda döndü.");

    /// <summary>Raised when a fan is confirmed stopped while its die is hot.</summary>
    public string FanStallDetected => Loc.T("Fan durmuş görünüyor: sıcak bir çipte fan dönmüyor. Makineyi kapatıp fanı kontrol edin.");

    /// <summary>"güvenlik tabanı %60" for an anchor that sits on or above a floor.</summary>
    public string SafetyFloor(int percent) => Loc.T($"güvenlik tabanı %{percent}");

    /// <summary>"Sıcaklık 70 °C → fan %55" for the curve preview line.</summary>
    public string CurvePoint(int temperatureC, int percent) => Loc.T($"Sıcaklık {temperatureC} °C → fan %{percent}");

    /// <summary>
    /// "Bölge 1 (Sol)" for a zone colour row. The position is part of the name because the deck above the palette is
    /// four bars in a row: "Bölge 3" alone leaves the user counting from the left to find out which bar it is.
    /// </summary>
    public string Zone(int index) => index switch
    {
        1 => Loc.T("Bölge 1 (Sol)"),
        2 => Loc.T("Bölge 2 (Orta Sol)"),
        3 => Loc.T("Bölge 3 (Orta Sağ)"),
        4 => Loc.T("Bölge 4 (Sayısal)"),
        _ => Loc.T($"Bölge {index}")
    };

    // ---------------------------------------------------------------- shell chrome
    //
    // The printed captions of the WPF window: small, uppercase, faint. They are catalogue keys like every other label,
    // so a translated window does not keep a Turkish rail.

    public string NavCaption => Loc.T("GEZİNTİ");
    public string SystemCaption => Loc.T("SİSTEM");
    public string BatteryCaption => Loc.T("PİL");
    public string ModeCaption => Loc.T("MOD");
    public string ActiveBadge => Loc.T("AKTİF");
    public string BoostCaption => Loc.T("BOOST");
    public string FanCaption => Loc.T("FAN");
    public string GameCaption => Loc.T("OYUN");

    /// <summary>
    /// The signature under the rail. Deliberately not a catalogue key: it is the author's line, and a translated
    /// signature would be a different signature. The heart is drawn in the accent colour beside it.
    /// </summary>
    public string Minimize => Loc.T("Küçült");
    public string Maximize => Loc.T("Büyüt");
    public string ResizeHint => Loc.T("Sürükleyerek boyutlandır");
    public string HideToTray => Loc.T("Tepsiye gizle");

    /// <summary>
    /// The rail's resize handle. Four transparent pixels have no affordance of their own, so the tooltip is the only
    /// place the double-click reset is discoverable.
    /// </summary>
    public string RailSplitterHint => Loc.T("Kenar çubuğu genişliğini sürükleyerek ayarla; çift tıkla varsayılana döndür");

    public string SignaturePrefix => "Made with love by ·";

    public string SignatureName => "Caner";

    public string SignatureLinkHint => Loc.T("Projeyi GitHub'da aç");

    // ---------------------------------------------------------------- Phase 1 additions

    /// <summary>The caption over the ready-made curve shapes.</summary>
    public string CurvePresetsCaption => Loc.T("ŞABLON");

    /// <summary>The chip that says a MUX write needs a restart before it means anything.</summary>
    public string RebootRequired => Loc.T("YENİDEN BAŞLAT");

    /// <summary>The per-game target captions, which the WPF card printed above each picker.</summary>
    public string WorkingMode => Loc.T("Çalışma Modu");

    public string FanProfile => Loc.T("Fan Profili");

    // ---------------------------------------------------------------- the five pages

    public string NavModes => Loc.T("Çalışma Modları");
    public string NavMonitoring => Loc.T("Donanım İzleme & OSD");
    public string NavLighting => Loc.T("Klavye Aydınlatması");
    public string NavBattery => Loc.T("Pil & Termal Sağlık");
    public string NavTools => Loc.T("Sistem Servisleri & Araçlar");

    // The page headings are longer than the rail labels, exactly as they were in the WPF window: the rail has to fit
    // five items in 225px, the page has the whole width.
    public string MonitoringPageTitle => Loc.T("Donanım Telemetrisi ve OSD Göstergesi");
    public string LightingPageTitle => Loc.T("Klavye RGB Aydınlatma Kontrolü");
    public string BatteryPageTitle => Loc.T("Pil Şarj Sınırı ve Sağlığı");
    public string ToolsPageTitle => Loc.T("Sistem Servisleri ve Başlangıç Ayarları");

    // ---------------------------------------------------------------- page 1: modes and fans

    public string Last60Seconds => Loc.T("60 SANİYE");
    public string RangeCaption => Loc.T("ARALIK");
    public string NowCaption => Loc.T("ŞİMDİ");
    public string HideCaption => Loc.T("GİZLE");
    public string ShowCaption => Loc.T("GÖSTER");
    public string WaitingForRecording => Loc.T("Kayıt bekleniyor");
    public string PowerModes => Loc.T("Güç Modları");

    /// <summary>
    /// The line above the mode tiles when the backend cannot be reached. The page stays readable and the note says
    /// which controls are off and why, instead of the whole page going grey with no explanation.
    /// </summary>
    public string ModeLockNote => Loc.T("Donanım arka ucu yanıt vermediği için mod değiştirme devre dışı. Okuma ve diğer ayarlar çalışmaya devam eder.");
    public string FanControl => Loc.T("Fan Kontrolü");
    public string FanSegmentCustom => Loc.T("Özel");
    public string FanSegmentCurve => Loc.T("Eğri");
    public string FanProtection => Loc.T("Fan arızasında otomatik koruma (Eco moduna geç)");
    public string GpuAndCpu => Loc.T("GPU & CPU");
    public string CpuBoostCaption => Loc.T("CPU BOOST");

    /// <summary>
    /// The long explanation behind the CPU BOOST badge. The badge used to carry the caption itself, which told the
    /// user nothing they could not already read beside it.
    /// </summary>
    public string CpuBoostHint => Loc.T("Windows güç planındaki işlemci saat frekansı davranışı. Agresif en yüksek frekansa çıkar, Verimli boost'u açık tutar, Devre Dışı taban frekansta kalır.");
    public string BoostAggressive => Loc.T("Agresif");
    public string BoostEfficient => Loc.T("Verimli");
    public string BoostDisabled => Loc.T("Devre Dışı");
    public string ApplyMode => Loc.T("Modu Uygula");
    public string GameStatusIdle => Loc.T("Durum: Beklemede (Aktif oyun yok)");
    public string AutoGameDetect => Loc.T("Otomatik oyun algılamayı etkinleştir");
    public string QuickGame => Loc.T("Hızlı oyun seçici");

    // The one-line label under a compact mode tile. Shorter than the hint the tall card carried, because the tile is
    // a quarter of the width.
    public string ModeShortQuiet => Loc.T("Hafif kullanım");
    public string ModeShortBalanced => Loc.T("Günlük önerilen");
    public string ModeShortPerformance => Loc.T("Yüksek oyun gücü");
    public string ModeShortTurbo => Loc.T("Maksimum performans");
    public string ModeShortEco => Loc.T("Pil ömrü odaklı");

    // ---------------------------------------------------------------- page 2: monitoring

    public string Temperatures => Loc.T("Sıcaklıklar");
    public string FanSpeeds => Loc.T("Fan Devirleri");
    public string BatteryAndPower => Loc.T("Pil ve Güç");
    public string TelemetryCharts => Loc.T("Telemetri Geçmiş Grafiği");
    public string WindowsOnly => Loc.T("Yalnızca Windows");

    // ---------------------------------------------------------------- page 3: lighting

    public string ColorPalette => Loc.T("Hazır renkler");
    public string ZonePreview => Loc.T("4 bölge önizleme");

    // ---------------------------------------------------------------- page 4: battery and thermal

    public string BatteryStatus => Loc.T("Pil durumu");
    public string FanCurveEditor => Loc.T("Özel fan eğrisi");
    public string PluggedIn => Loc.T("Prize takılı");
    public string OnBattery => Loc.T("Pilde");
    public string Unknown => Loc.T("Bilinmiyor");
    public string ThermalSafety => Loc.T("Termal güvenlik");
    public string ThermalSafetyHint => Loc.T("Bu tabanların altına inilmez; sıcaklık yükseldikçe fan hızı en az bu değere çıkar.");

    // ---------------------------------------------------------------- page 5: services and tools

    public string DeviceCapabilities => Loc.T("Cihaz & Yetenek Özeti");
    public string Appearance => Loc.T("Görünüm");
    // ---------------------------------------------------------------- page 2: the monitoring cards

    public string Processor => Loc.T("İşlemci");
    public string GraphicsCard => Loc.T("Ekran Kartı");

    /// <summary>
    /// "Sistem Belleği (RAM)". The parenthetical is the WPF page's own wording, kept because "Sistem Belleği" alone
    /// reads as a second temperature on a page that already has one.
    /// </summary>
    public string SystemMemory => Loc.T("Sistem Belleği (RAM)");

    public string SystemTemperature => Loc.T("Sistem Sıcaklığı");
    public string Cores => Loc.T("Çekirdek");
    public string Threads => Loc.T("İş parçacığı");

    // The row labels carry their colon, as the WPF page wrote them: a label and its figure sit on one line, and the
    // colon is what makes the figure read as the answer rather than as a second label.
    public string Temperature => Loc.T("Sıcaklık:");
    public string PackagePower => Loc.T("Paket Güç Tüketimi:");
    public string TgpPower => Loc.T("TGP Güç Tüketimi:");
    public string CpuUsage => Loc.T("İşlemci Kullanımı:");
    public string GpuUsage => Loc.T("GPU Kullanımı:");
    public string ClockSpeed => Loc.T("Saat Frekansı:");
    public string Vram => Loc.T("VRAM Kullanımı:");

    /// <summary>
    /// The system-temperature badge. The sensor's position is not published by Acer, so the tooltip says exactly that
    /// rather than borrowing the thermal-floor summary, which answers a different question.
    /// </summary>
    public string SysTemperatureHint => Loc.T("Sensörün konumu Acer tarafından belirtilmiyor.");

    public string MemoryCleanTitle => Loc.T("Bekleme Belleğini Boşaltma");
    public string MemoryCleanHint => Loc.T("İşletim sisteminin önbelleğe aldığı belleği serbest bırakır; çalışan uygulamalar kapanmaz.");
    public string Cleaning => Loc.T("Boşaltılıyor…");
    public string CleanStandby => Loc.T("Bekleme Belleğini Boşalt");
    public string GetMemoryCleaner => Loc.T("WinMemoryCleaner'ı indir (ücretsiz, GPL-3.0)");
    public string GetMemoryCleanerHint => Loc.T("WinMemoryCleaner kurulu değil. Buton, resmi indirme sayfasını açar.");
    public string MemoryCleanToolMissing => Loc.T("Bellek temizleme aracı bulunamadı.");
    public string MemoryCleanElevationRefused => Loc.T("Yönetici izni verilmedi; bekleme belleği boşaltılamadı.");
    public string MemoryCleanFailed => Loc.T("Bekleme belleği boşaltılamadı.");
    public string MemoryCleanAlreadyClean => Loc.T("Boşaltılacak bekleme belleği yoktu.");
    public string MemoryCleanFreed(string freed, double before, double after) =>
        Loc.T($"Bellek boşaltıldı: {freed} serbest bırakıldı ({before:F1} GB → {after:F1} GB)");

    // ---------------------------------------------------------------- page 2: the chart and the mode trace

    public string ChartMetric => Loc.T("Grafik ölçütü");
    public string Current => Loc.T("Mevcut");
    public string Minimum => Loc.T("Min");
    public string Maximum => Loc.T("Maks");
    public string Average => Loc.T("Ortalama");
    public string ModeChangeHint => Loc.T("Çalışma modu değişimleri grafikte dikey çizgiyle işaretlenir.");
    public string ChartEmptyHint => Loc.T("Grafik için henüz yeterli örnek yok; birkaç saniye içinde dolacak.");

    /// <summary>
    /// The chart's badge. A gap in the trace is not a zero reading, and a user who sees the line dip has to be told
    /// which of the two it is; the empty-state sentence answers a different question and belongs in the plot itself.
    /// </summary>
    public string ChartGapHint => Loc.T("Ölçüm olmayan anlar (ör. ekran kartı uykudayken) grafiğe ve değerlere dahil edilmez.");

    // ---------------------------------------------------------------- page 2: the on-screen display

    public string Osd => Loc.T("Ekran Üstü Gösterge (OSD)");
    public string OsdHint => Loc.T("Oyun içinde sıcaklık, yük ve fan devrini küçük bir katmanda gösterir.");
    public string ChangePosition => Loc.T("Konum Değiştir");
    public string OsdCornerTopLeft => Loc.T("Sol üst");
    public string OsdCornerTopRight => Loc.T("Sağ üst");
    public string OsdCornerBottomLeft => Loc.T("Sol alt");
    public string OsdCornerBottomRight => Loc.T("Sağ alt");
    public string OsdPosition(string corner, int monitor) => Loc.T($"Konum: {corner} · Monitör {monitor}");

    // ---------------------------------------------------------------- page 3: the lighting controls

    public string Effects => Loc.T("Efektler");
    public string EffectDirection => Loc.T("Efekt Yönü");
    public string EffectSpeed => Loc.T("Efekt Hızı");
    public string CustomColor => Loc.T("Özel Renk…");
    public string ApplyToAllZones => Loc.T("Tüm Bölgelere Uygula");
    public string TurnOffLighting => Loc.T("Aydınlatmayı Kapat");

    /// <summary>The same button once the page is in blackout: what it does is bring the lights back.</summary>
    public string TurnOnLighting => Loc.T("Aydınlatmayı Aç");

    /// <summary>The blackout panel that covers the page while the keyboard is off.</summary>
    public string LightingBlackoutTitle => Loc.T("Aydınlatma kapalı");
    public string LightingBlackoutHint => Loc.T("Klavye ışıkları kapalı. Aydınlatmayı geri açtığınızda sayfa yeniden kullanılabilir.");
    public string LightingUnsupportedTitle => Loc.T("Bu makinede klavye ışığına erişilemiyor");
    public string LightingUnsupportedHint => Loc.T("Linux çekirdeği bu klavyenin ışığına bir yol açmıyor. Linuwu-Sense modülü ya da çalışan bir OpenRGB gerekiyor; Ayarlar sayfasındaki Linux kartı ne yapılacağını gösterir.");
    public string RestoreLighting => Loc.T("Aydınlatmayı geri aç");

    /// <summary>
    /// The 30-second timeout's short caption in the page's top-right corner. The sentence that explains it is the
    /// toggle's tooltip, because a hint that long would push the title off the line.
    /// </summary>
    public string BacklightTimeoutShort => Loc.T("30 sn");

    public string ZonePreviewNote => Loc.T("Bu efekt renk kullanmaz; bölgeler gökkuşağı gradyanıyla gösterilir.");

    /// <summary>The same line while an automatic mode owns the deck: the bars are the keyboard, not the palette.</summary>
    public string ZonePreviewAutomatic => Loc.T("Bölgeler klavyenin şu anda gösterdiği rengi gösterir; bu rengi otomatik mod seçiyor, elle seçtiğiniz renk değil.");
    public string WinKeyLock => Loc.T("WinKey Kilidi");
    public string OpenRgbStatus => Loc.T("OpenRGB durumu");
    public string BacklightTimeoutHint => Loc.T("30 saniye boşta kaldığında aydınlatmayı kapat");

    /// <summary>The temp-reactive keyboard card: the title, and the one line that explains what it does.</summary>
    public string ReactiveLighting => Loc.T("Temp-Reactive Lighting");
    public string ReactiveLightingHint => Loc.T("Klavye rengi seçtiğiniz sensörün sıcaklığına göre değişir: hazır rampalardan birini seçin, sabit bir renk için tek renk seçeneğini kullanın ya da Özel'de her sıcaklığa kendi renginizi verin.");
    /// <summary>The heading over the two sensor rows.</summary>
    public string ReactiveSensorsTitle => Loc.T("Hangi sensör boyuyor");

    /// <summary>The processor row's switch, read out by a screen reader.</summary>
    public string ReactiveCpuSwitch => Loc.T("İşlemci sıcaklığı klavyeyi boyasın");

    /// <summary>The graphics row's switch.</summary>
    public string ReactiveGpuSwitch => Loc.T("Ekran kartı sıcaklığı klavyeyi boyasın");

    /// <summary>What the two split chips do, in one sentence under them.</summary>
    public string ReactiveSplitHint => Loc.T("Yarı yarıya: klavyenin solu CPU, sağı GPU renginde kalır. Geçişli: renk dört bölge boyunca soldan sağa karışır.");

    /// <summary>The processor chip in the ramp header, read out by a screen reader.</summary>
    public string ReactiveRampForCpu => Loc.T("Rampayı işlemci yarısı için düzenle");

    /// <summary>The graphics chip in the ramp header.</summary>
    public string ReactiveRampForGpu => Loc.T("Rampayı ekran kartı yarısı için düzenle");

    /// <summary>
    /// The text on the processor's ramp chip. It says which half of the keyboard the ramp paints rather than just "CPU",
    /// because the two big sensor tiles a few lines above already say CPU and GPU: two more buttons with the same two
    /// words read as duplicates of them, not as "which ramp am I editing".
    /// </summary>
    public string ReactiveRampCpuChip => Loc.T("CPU yarısı");

    /// <inheritdoc cref="ReactiveRampCpuChip"/>
    public string ReactiveRampGpuChip => Loc.T("GPU yarısı");

    public string ReactiveSensorsHint => Loc.T("CPU tek başına tüm klavyeyi boyar. CPU + GPU açıkken klavyenin solu CPU, sağı GPU sıcaklığını izler.");
    public string ReactiveNoSensor => Loc.T("Sensör seçilmedi");
    public string ReactiveProfiles => Loc.T("Sıcaklık rampaları");
    public string ReactiveSolidHint => Loc.T("Sıcaklık ne olursa olsun bu renk sabit kalır; sıcaklığa göre değişen bir renk için rampalardan birini seçin.");
    public string ReactiveSplitMode => Loc.T("Bölünme");
    public string ReactiveSplitEven => Loc.T("Yarı yarıya");
    public string ReactiveSplitBlend => Loc.T("Geçişli");
    public string ReactiveStops => Loc.T("Renk skalası");
    public string Profile => Loc.T("Profil");
    public string ReactiveCustomHint => Loc.T("Her satır bir sıcaklık ve o sıcaklıktaki renk: klavye soğuktan sıcağa bu renkler arasında geçiş yapar.");
    public string AddReactiveStop => Loc.T("Sıcaklık noktası ekle");

    /// <summary>
    /// The charging card: a second automatic colour, driven by the adapter rather than by a sensor. The two lines of
    /// state are the whole explanation — while the adapter is in the keys show this colour, and on battery they show
    /// the user's own.
    /// </summary>
    public string ChargingColor => Loc.T("Şarjda Klavye Rengi");
    public string ChargingColorHint => Loc.T("Adaptör takılıyken klavye bu rengi gösterir; pilde kendi renginiz geri gelir.");
    public string ChargingColorSwatches => Loc.T("Şarj rengi");

    /// <summary>
    /// The two automatic cards carry their own brightness, so each slider says what it dims: three sliders all labelled
    /// "Brightness" on one page read as one control repeated, and the wrong one is easy to move.
    /// </summary>
    public string ReactiveBrightnessTitle => Loc.T("Isı-tepkili parlaklık");
    public string ChargingBrightnessTitle => Loc.T("Şarj rengi parlaklığı");

    /// <summary>
    /// Said when the profile list lands on its custom entry: picking it is not a write, and a picker that answers
    /// nothing at all reads as broken.
    /// </summary>
    public string CustomProfileStatus => Loc.T("Özel: klavyede kayıtlı bir profil değil, elle seçtiğiniz renkler var.");
    public string ChargingColorActive => Loc.T("Şarjda · klavye bu renkte");
    public string ChargingColorWaiting => Loc.T("Pilde · adaptör takılınca bu renk");

    /// <summary>
    /// The same line on a machine whose 80 % charge limit has parked the pack at the cap: plugged in, and deliberately not
    /// charging. The colour is on the keys exactly as it is below the cap, so the sentence says what the adapter is doing
    /// rather than claiming a charge that stopped on purpose.
    /// </summary>
    public string ChargingColorAtLimit(int percent) => Loc.T($"Adaptörde · %{percent} şarj sınırı · klavye bu renkte");
    public string Sil => Loc.T("Sil");

    /// <summary>Shown under the fan curve editor now that the curve applies as it is dragged.</summary>
    public string CurveAutoApplyHint => Loc.T("Eğri anında uygulanır; ayrı bir uygula düğmesi yok.");

    /// <summary>
    /// The figure beside the brightness slider. The label on the left already says "Parlaklık", so repeating it here
    /// would print the word twice on one line; what the right-hand side adds is the number, and — at zero — the fact
    /// that zero means off rather than dim.
    /// </summary>
    public string BrightnessValue(int level, int max) =>
        level <= 0 ? Loc.T("0 / 9 (Kapalı)") : $"{level} / {max}";

    /// <summary>The figure beside the speed slider, on the same rule as <see cref="BrightnessValue"/>.</summary>
    public string SpeedValue(int level, int max) => $"{level} / {max}";

    // ---------------------------------------------------------------- page 4: the battery and the thermal contract

    public string PowerFlow => Loc.T("Anlık Güç Akışı");
    public string RemainingTime => Loc.T("Kalan Süre");
    public string BatteryProtection => Loc.T("Pil Tasarrufu");
    public string DcProfile => Loc.T("Pilde Çalışma Güç Profili (DC Modu)");
    public string Watchdog => Loc.T("Pil Bekçisi (Watchdog)");
    public string WatchdogHint => Loc.T("Pil %80 sınırını aştığında uyarı bandı gösterir; firmware'e yazmaz.");
    public string CapacityAndWear => Loc.T("Batarya Kapasite ve Aşınma Durumu");
    public string BatteryHealth => Loc.T("Pil Sağlığı");
    public string Wear => Loc.T("Aşınma");
    public string FullCharge => Loc.T("Tam Şarj");
    public string Voltage => Loc.T("Pil Gerilimi");
    public string Chemistry => Loc.T("Kimya");
    public string WearTrend => Loc.T("Aşınma Trendi");
    public string ComponentTemperatures => Loc.T("Bileşen Sıcaklık Matrisi");
    public string Disk1 => Loc.T("Disk 1");
    public string Disk2 => Loc.T("Disk 2");
    public string BusType => Loc.T("Veri yolu");
    public string FreeSpace => Loc.T("Boş alan");
    public string Health => Loc.T("Sağlık");
    public string StorageDrives => Loc.T("Depolama Sürücüleri");
    public string NoStorageDrives => Loc.T("Depolama bilgisi okunamadı.");

    public string OnAdapter => Loc.T("Adaptörde");
    public string OnAdapterCharging => Loc.T("Adaptörde · şarj oluyor");
    public string OnAdapterHeld => Loc.T("Adaptörde · sınırda bekliyor");
    public string OnAdapterIdle => Loc.T("Adaptörde · şarj tamam");
    public string Discharging => Loc.T("Deşarj");
    public string Charging => Loc.T("Şarj");
    public string RemainingUnknown => Loc.T("Hesaplanıyor…");
    public string OverchargeMessage(int percent) =>
        Loc.T($"Pil %{percent} seviyesinde ve şarj sınırı kapalı. Pil ömrü için %80 sınırını açın.");

    public string CapacityUnreadable => Loc.T("Kapasite okunamadı");
    public string HealthGood => Loc.T("İyi");
    public string HealthWorn => Loc.T("Aşınmış");
    public string HealthReplace => Loc.T("Değiştirilmeli");
    public string WearLow => Loc.T("Düşük aşınma");
    public string WearMedium => Loc.T("Orta aşınma");
    public string WearHigh => Loc.T("Yüksek aşınma");
    public string FactoryCapacity(string value) => Loc.T($"Fabrika {value}");
    public string CycleCount(int cycles) => Loc.T($"Döngü: {cycles}");
    public string Lithium => Loc.T("Lityum");
    public string CellCountFromVoltage(int cells) => Loc.T($"{cells} hücre (gerilimden)");
    public string Battery => Loc.T("Pil");

    public string WearNoSamples => Loc.T("Henüz ölçüm yok. NitroLite iki haftada bir ölçüm kaydeder; eğri birkaç ölçümden sonra görünür.");
    public string WearOneSample => Loc.T("Tek ölçüm var. Eğri için en az iki ölçüm gerekir; NitroLite iki haftada bir ölçüm kaydeder.");
    public string WearFirstSample(string date, double wear) => Loc.T($"İlk ölçüm {date}: %{wear:F1} aşınma");
    public string WearChangeSummary(int days, string change) => Loc.T($"{days} günde {change} puan");
    public string WearNeedsAWeek => Loc.T("Eğilim için en az bir hafta ölçüm gerekir.");
    public string WearRecalibrated => Loc.T("Pil yeniden kalibre olmuş; aşınma azalmış görünüyor.");
    public string WearPerYear(double perYear) => Loc.T($"Yılda yaklaşık %{perYear:F1}");
    public string ClearHistory => Loc.T("Geçmişi sil");
    public string ClearHistoryConfirm => Loc.T("Pil aşınma geçmişi silinsin mi? Bu işlem geri alınamaz.");
    public string ClearHistoryFailed => Loc.T("Pil geçmişi silinemedi.");
    public string ClearHistoryDone => Loc.T("Pil aşınma geçmişi silindi.");

    public string RetrustCertificate => Loc.T("Yeni sertifikaya güven");
    public string RetrustCertificateNote => Loc.T("Acer Care Center'in sertifikası değişmiş; bağlantı güvenlik için reddedildi. Acer güncellemesinden sonra bu normaldir.");
    public string RetrustCertificateDone => Loc.T("Sertifika sabitlemesi sıfırlandı; şarj sınırı yeniden okunuyor.");
    public string RetrustCertificateFailed => Loc.T("Sertifika sabitlemesi silinemedi; veri klasörü yazılamıyor olabilir.");

    // ---------------------------------------------------------------- page 5: the services and the tools

    public string Running => Loc.T("Çalışıyor");
    public string Stopped => Loc.T("Durdu");
    public string AcerHardware => Loc.T("Acer arka planı ve sistem donanımı");
    public string AcerServices => Loc.T("Acer arka plan servisleri");
    public string AcerServicesHint => Loc.T("NitroLite'ın kullanmadığı güncelleme ve telemetri servisleri. Acer ajanı ve aydınlatma servisi korunur.");
    public string DisableServices => Loc.T("Devre Dışı Bırak");
    public string DisableServicesConfirm => Loc.T("Acer arka plan servisleri devre dışı bırakılsın mı? Bu işlem geri alınabilir.");
    public string ServicesDisabled => Loc.T("Acer arka plan servisleri devre dışı bırakıldı.");
    public string ServicesFailed(string error) => Loc.T($"Servis işlemi başarısız: {error}");
    public string UnknownError => Loc.T("Bilinmeyen hata");
    public string RestoreServices => Loc.T("Servisleri Geri Yükle");
    public string RestoreServicesConfirm => Loc.T("Acer servisleri eski başlangıç türlerine döndürülsün mü?");
    public string ServicesRestored => Loc.T("Acer servisleri geri yüklendi.");
    public string NitroSense => Loc.T("NitroSense");
    public string KillNitroSense => Loc.T("NitroSense'i Kapat");
    public string KillNitroSenseConfirm => Loc.T("NitroSense ve yardımcı süreçleri kapatılsın mı?");
    public string NitroSenseAlreadyClosed => Loc.T("NitroSense zaten kapalı.");
    public string NitroSenseClosed => Loc.T("NitroSense kapatıldı.");
    public string NitroSenseCloseFailed => Loc.T("NitroSense kapatılamadı.");
    public string NoAcerServices => Loc.T("Yüklü Acer arka plan servisi bulunamadı.");

    /// <summary>The banner shown while the capability probe is still running.</summary>
    public string HardwareScanning => Loc.T("Taranıyor…");
    public string HardwareScanningDetail => Loc.T("Donanım taranıyor. Denetimler tarama bitince açılır.");

    public string RepairToolsGroup => Loc.T("Onarım araçları");
    public string RepairToolsHint => Loc.T("Servis kayıtlarını ve MUX değerini yeniden yazar; hiçbiri donanım ayarını değiştirmez.");
    public string RepairConfirm => Loc.T("Bu onarım aracı çalıştırılsın mı?");
    public string RepairRunning => Loc.T("Onarım çalışıyor…");
    public string RepairFailed(string error) => Loc.T($"Onarım başarısız: {error}");
    public string FixLighting => Loc.T("Klavye RGB Servisini Onar");
    public string FixAcerServices => Loc.T("Acer & NitroSense Servislerini Onar");
    public string RestoreMux => Loc.T("MUX Switch Kaydını Onar");
    public string WindowsOptimization => Loc.T("Windows & Sistem Optimizasyonu");
    public string Optimize => Loc.T("Optimize Et");
    public string RestoreOptimize => Loc.T("Geri Al");
    public string DirectWmi => Loc.T("Doğrudan WMI arka ucu");
    public string StartCheck => Loc.T("Kontrolü başlat");
    public string DirectWmiStarted => Loc.T("Doğrudan WMI arka ucu başlatıldı.");
    public string DirectWmiDeclined => Loc.T("Yönetici izni verilmedi; Acer servisi kullanılıyor.");
    public string DirectWmiFailed(string error) => Loc.T($"Doğrudan WMI başlatılamadı: {error}");

    public string StartupAndIntegration => Loc.T("Başlangıç ve Entegrasyon");
    public string RunAtStartup => Loc.T("Windows açılışında otomatik çalıştır");
    public string StartMinimized => Loc.T("Windows açılışında tepside başlat");
    public string AtkMouseIntegration => Loc.T("ATK Fare Entegrasyonu");

    /// <summary>The one word the startup card adds to a row, in brackets after its title.</summary>
    public string Recommended => Loc.T("(önerilen)");

    // The Nitro key. One switch, no shortcut to record: the button is physical and has one meaning.
    public string NitroKey => Loc.T("Nitro tuşu");
    public string NitroKeyToggle => Loc.T("Nitro tuşu ile NitroLite'ı aç");
    public string NitroKeyHint =>
        Loc.T("Kapalıyken donanım tuşu yok sayılır. Açıkken tuş NitroLite'ı açar; NitroSense açılırsa kapatılır.");

    // The second half of the same claim: the key opens NitroLite, and NitroSense does not get to open as well.
    public string RedirectNitroSense => Loc.T("NitroSense'i NitroLite'a yönlendir");
    public string RedirectNitroSenseHint =>
        Loc.T("Açıkken NitroSense penceresi ve izin uyarısı kapatılır, tuş NitroLite'ı öne getirir.");

    // What the launcher is doing right now, folded into the Nitro key's tooltip.
    public string NitroSenseLauncherNoKey =>
        Loc.T("Bu makinede Nitro tuşu yok; yönlendirme devre dışı.");
    public string NitroSenseLauncherRedirecting =>
        Loc.T("NitroSense açık; tuş NitroLite'a yönlendiriliyor ve NitroSense kapatılıyor.");
    public string NitroSenseLauncherIdle =>
        Loc.T("NitroSense kapalı; tuş NitroLite'ı öne getirir.");
    public string NitroSenseLauncherRunning =>
        Loc.T("NitroSense açık; yönlendirme kapalı, ikisi de çalışır.");
    public string NitroSenseLauncherStopped =>
        Loc.T("NitroSense kapalı; yönlendirme kapalı.");

    // The permanent answer: the Acer hardware service starts the launcher on the key press, so renaming the launcher
    // is what stops the key opening NitroSense while NitroLite is not running at all.
    public string NitroSenseLauncherBlockTitle => Loc.T("NitroSense başlatıcısı");
    public string NitroSenseLauncherBlockHint =>
        Loc.T("Acer donanım servisinin tuşa basınca NitroSense'i başlatmasını engeller. NitroLite kapalıyken de geçerlidir; yönetici izni ister ve geri alınabilir.");
    public string NitroSenseLauncherBlock => Loc.T("Kalıcı olarak kapat");
    public string NitroSenseLauncherUnblock => Loc.T("Geri aç");
    public string NitroSenseLauncherBlockedState =>
        Loc.T("Başlatıcı devre dışı: NitroLite kapalıyken de tuş NitroSense'i açamaz.");
    public string NitroSenseLauncherActiveState =>
        Loc.T("Başlatıcı etkin: NitroLite kapalıyken tuş NitroSense'i açar.");
    public string NitroSenseLauncherBlockConfirm =>
        Loc.T("NitroSense başlatıcısı yeniden adlandırılacak; tuş artık NitroSense'i açamaz. Devam edilsin mi?");
    public string NitroSenseLauncherUnblockConfirm =>
        Loc.T("NitroSense başlatıcısı geri konacak; tuş yeniden NitroSense'i açar. Devam edilsin mi?");
    public string NitroSenseLauncherBlockedDone =>
        Loc.T("NitroSense başlatıcısı devre dışı bırakıldı; tuş artık NitroSense'i açmaz.");
    public string NitroSenseLauncherUnblockedDone =>
        Loc.T("NitroSense başlatıcısı geri açıldı.");
    public string NitroSenseLauncherBlockFailed(string error) =>
        Loc.T($"NitroSense başlatıcısı değiştirilemedi: {error}");

    // The emergency rollback switch for the hardware layer.
    public string UseDirectWmiBackend => Loc.T("Doğrudan WMI arka ucunu kullan");
    public string UseDirectWmiBackendHint =>
        Loc.T("Kapatıldığında tüm okuma ve yazmalar Acer servis soketine döner. Yeniden başlatma gerekmez.");
    public string DirectWmiBackendOn => Loc.T("Doğrudan WMI arka ucu etkin.");
    public string DirectWmiBackendOff => Loc.T("Doğrudan WMI arka ucu kapalı; Acer servisi kullanılıyor.");

    // The bottom card: language and theme, where the WPF page kept them.
    public string NitroLiteSettings => Loc.T("NitroLite Ayarları");

    public string UpdatesAndDiagnostics => Loc.T("Güncelleme, Teşhis ve Yedekleme");

    /// <summary>The heading over the report and bundle buttons, which used to be a third unlabelled row.</summary>
    public string DiagnosticsGroup => Loc.T("Teşhis ve rapor");
    public string AutoUpdateCheck => Loc.T("Güncellemeleri otomatik denetle");
    public string Version => Loc.T("Sürüm");
    public string CheckNow => Loc.T("Şimdi denetle");
    public string Update => Loc.T("Güncelle");
    public string ReleaseNotes => Loc.T("Sürüm Notları");
    public string ReleaseNotesHint => Loc.T("Bu sürümün GitHub sayfasını tarayıcıda açar");
    public string UpdateCurrent => Loc.T("Sürüm güncel.");
    public string UpdateFound(string version) => Loc.T($"Yeni sürüm: {version}");
    public string UpdateManual => Loc.T("Bu derleme kendini güncelleyemez; sürüm sayfası açıldı.");
    public string UpdateDownloading => Loc.T("Güncelleme indiriliyor…");
    public string UpdateProgress(double fraction) =>
        Loc.T($"Güncelleme indiriliyor… %{Math.Round(fraction * 100)}");
    public string UpdateInstalled => Loc.T("Güncelleme kuruldu; uygulamayı yeniden başlatın.");
    public string UpdateFailed(string error) => Loc.T($"Güncelleme kurulamadı: {error}");
    public string UpdateTo(string version) => Loc.T($"{version} sürümüne güncelle");
    public string BugReportTitle => Loc.T("NitroLite hata bildirimi");
    public string BugReportFailed(string error) => Loc.T($"Hata bildirimi açılamadı: {error}");
    public string ReportBug => Loc.T("Hata bildir");
    public string OpenLogs => Loc.T("Günlük klasörünü aç");
    public string ExportReport => Loc.T("Raporu dışa aktar");
    public string ReportNotReady => Loc.T("Donanım raporu henüz hazır değil.");
    public string ReportExported(string name) => Loc.T($"Rapor kaydedildi: {name}");
    public string ReportExportFailed(string error) => Loc.T($"Rapor kaydedilemedi: {error}");
    public string CreateBundle => Loc.T("Paketi oluştur");
    public string LastBundle(string name) => Loc.T($"Son paket: {name}");
    public string BundleCreated(string name) => Loc.T($"Teşhis paketi oluşturuldu: {name}");
    public string BundleFailed(string error) => Loc.T($"Teşhis paketi oluşturulamadı: {error}");
    public string Backup => Loc.T("Yedekleme");
    public string CreateBackup => Loc.T("Yedekle");
    public string BackupSaved(string name) => Loc.T($"Yedek kaydedildi: {name}");
    public string BackupFailed => Loc.T("Yedek oluşturulamadı.");
    public string BackupFailedDetail(string error) => Loc.T($"Yedek oluşturulamadı: {error}");
    public string RestoreBackup => Loc.T("Geri yükle");
    public string RestoreBackupConfirm => Loc.T("Yedek geri yüklensin mi? Geçerli ayarların üzerine yazılır.");
    public string BackupRestored => Loc.T("Yedek geri yüklendi. Değişiklikler için uygulamayı yeniden başlatın.");
    public string BackupRestoreFailed => Loc.T("Yedek geri yüklenemedi.");
    public string BackupRestoreFailedDetail(string error) => Loc.T($"Yedek geri yüklenemedi: {error}");
    public string LinkFailed(string error) => Loc.T($"Bağlantı açılamadı: {error}");

    // ---------------------------------------------------------------- the support card

    // The last card on the page. It replaced a "Notes" block that printed the probe's own diagnostics, which was a
    // developer's note in a user's window; this one says who wrote the app and what it cost, which is the only thing
    // that belongs at the bottom of a settings page.
    public string Support => Loc.T("Destek");
    // One literal, not a concatenation: the catalogue-coverage test reads the argument of Loc.T, and a split string
    // would be scanned as its first fragment and reported as a key no catalogue carries.
    public string SupportBody => Loc.T("NitroLite tek bir öğrencinin projesi: 100 doların üzerinde cepten harcama, yüzlerce saat emek ve her sürüm kendi dizüstü bilgisayarımda denendi. Her hâlükârda ücretsiz ve MIT lisanslı kalacak. Zamanınızdan bir şey kurtardıysa bir kahve güzel bir teşekkür olur; bir yıldız ya da hata bildirimi de en az onun kadar değerli.");
    public string SupportKofi => Loc.T("Ko-fi'de bana destek ol");
    public string SupportGitHub => Loc.T("GitHub'da yıldızla");
    public string SupportSponsors => Loc.T("GitHub Sponsors onay sürecinde.");

    // ---------------------------------------------------------------- the first-run walkthrough

    public string OnboardingStepFormat(int step, int total) => Loc.T($"Adım {step} / {total}");
    public string OnboardingHardwareTitle => Loc.T("Donanımınız");
    public string OnboardingHardwareBody => Loc.T("Bu makinede bulunan Acer düğümleri ve desteklenen özellikler aşağıda listelendi. Desteklenmeyen satırlar gizlenir.");
    public string OnboardingProfileTitle => Loc.T("Profil");
    public string OnboardingProfileBody => Loc.T("Önerilen başlangıç profili: Dengeli mod, otomatik fan ve %80 şarj sınırı. Tamamla dediğinizde bu ayarlar uygulanır.");
    public string OnboardingStartupTitle => Loc.T("Başlangıç");
    public string OnboardingStartupBody => Loc.T("NitroLite'ın Windows açılışında yönetici yetkisiyle, UAC sormadan başlamasını istiyor musunuz?");
    public string OnboardingRecommended => Loc.T("Dengeli · Otomatik fan · %80 şarj sınırı");
    public string OnboardingSkip => Loc.T("Atla");
    public string OnboardingBack => Loc.T("Geri");
    public string OnboardingNext => Loc.T("İleri");
    public string OnboardingFinish => Loc.T("Tamamla");
    public string OnboardingSection => Loc.T("İlk Kurulum");
    public string OnboardingReopen => Loc.T("İlk kurulum sihirbazını yeniden aç");
}
