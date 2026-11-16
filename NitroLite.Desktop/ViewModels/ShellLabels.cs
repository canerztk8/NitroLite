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

    // ---------------------------------------------------------------- sign-in launch
    public string AutoStart => Loc.T("Görev Zamanlayıcı ile UAC'siz otomatik başlatma");
    public string AutoStartHint => Loc.T("Oturum açılışında yükseltilmiş olarak, soru sorulmadan başlar.");
    public string AutoStartEnabled => Loc.T("Otomatik başlatma açıldı.");
    public string AutoStartDisabled => Loc.T("Otomatik başlatma kapatıldı.");
    public string AutoStartFailed => Loc.T("Otomatik başlatma değiştirilemedi.");

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
    public string DeleteProfileTitle => Loc.T("Profili sil");
    public string DeleteProfileConfirm(string name) => Loc.T($"'{name}' profili silinsin mi? Bu işlem geri alınamaz.");
    public string GameSessionRecovered => Loc.T("Yarım kalan oyun oturumu geri alındı; önceki mod ve fan ayarları geri yüklendi.");

    // ---------------------------------------------------------------- mouse

    public string MouseCaption => Loc.T("FARE");
    public string MouseProfiles => Loc.T("Fare profili");
    public string MouseProfilesHint => Loc.T("ATK farenin kendi belleğindeki yapılandırmalar. Değiştirme seçilirse dokunulmaz.");
    public string MousePassive => Loc.T("Değiştirme (Pasif)");
    public string MousePassiveApplied => Loc.T("Fare profili değiştirilmedi.");
    public string MouseProfileApplied(int profile) => Loc.T($"Fare profili uygulandı: Config {profile}");
    public string MouseProfileFailed => Loc.T("Fare profili uygulanamadı; cihaz yanıt vermedi.");
    public string DefaultMouseProfile => Loc.T("Varsayılan Fare Profili");

    // ---------------------------------------------------------------- fan curve presets

    public string CurvePresets => Loc.T("ŞABLON");

    // ---------------------------------------------------------------- MUX and desktop restore

    public string GpuPendingDiscrete => Loc.T("Harici GPU seçildi. Değişiklik yeniden başlatmadan sonra etkin olur.");
    public string GpuPendingHybrid => Loc.T("Hibrit mod seçildi. Değişiklik yeniden başlatmadan sonra etkin olur.");
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

    // ---------------------------------------------------------------- tray
    public string TrayShow => Loc.T("Pencereyi Göster");
    public string TrayCleanExit => Loc.T("Temiz Çıkış");
    public string TrayQuickMode => Loc.T("Hızlı mod");
    public string TrayCpuBoost => Loc.T("CPU Boost");
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
    public string HideToTray => Loc.T("Tepsiye gizle");

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

    public string GameFanCurve => Loc.T("Bu oyun için özel fan eğrisi");

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
    public string FanControl => Loc.T("Fan Kontrolü");
    public string FanSegmentCustom => Loc.T("Özel");
    public string FanSegmentCurve => Loc.T("Eğri");
    public string FanProtection => Loc.T("Fan arızasında otomatik koruma (Eco moduna geç)");
    public string GpuAndCpu => Loc.T("GPU & CPU");
    public string CpuBoostCaption => Loc.T("CPU BOOST");
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
    public string TelemetryCharts => Loc.T("60 Saniyelik Telemetri");
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
    public string SystemMemory => Loc.T("Sistem Belleği");
    public string SystemTemperature => Loc.T("Sistem Sıcaklığı");
    public string Cores => Loc.T("Çekirdek");
    public string Threads => Loc.T("İş parçacığı");
    public string Temperature => Loc.T("Sıcaklık");
    public string PackagePower => Loc.T("Paket Gücü");
    public string Usage => Loc.T("Kullanım");
    public string ClockSpeed => Loc.T("Saat Frekansı");
    public string Vram => Loc.T("VRAM");
    public string MemoryCleanTitle => Loc.T("Bekleme Belleğini Boşaltma");
    public string MemoryCleanHint => Loc.T("Windows'un bekleme listesindeki önbelleği serbest bırakır; çalışan uygulamalar kapanmaz.");
    public string Cleaning => Loc.T("Boşaltılıyor…");
    public string CleanStandby => Loc.T("Bekleme Belleğini Boşalt");
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
    public string ModeTrace => Loc.T("Mod İzleme");
    public string ModeTraceHint => Loc.T("Çalışma modu değişimleri, sıcaklık şeridinin üzerinde işaretlenir.");
    public string ChartEmptyHint => Loc.T("Grafik için henüz yeterli örnek yok; birkaç saniye içinde dolacak.");

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
    public string ZonePreviewNote => Loc.T("Bu efekt renk kullanmaz; bölgeler gökkuşağı gradyanıyla gösterilir.");
    public string WinKeyLock => Loc.T("WinKey Kilidi");
    public string OpenRgbStatus => Loc.T("OpenRGB durumu");
    public string BacklightTimeoutHint => Loc.T("30 saniye boşta kaldığında aydınlatmayı kapat");
    public string BrightnessLevel(int level, int max) => Loc.T($"Parlaklık: {level} / {max}");
    public string SpeedLevel(int level, int max) => Loc.T($"Efekt Hızı: {level} / {max}");
}
