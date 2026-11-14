# NitroLite v1.2 — durum

Son güncelleme: 2026-11-13. Plan tamamlandı; **GitHub'a hiçbir şey gönderilmedi.**

## Bitti

| # | Adım | Not |
|---|---|---|
| 1 | Hata taraması | `caf0545` |
| 2 | 8 dil (en ana + tr, de, es, ru, pt, hi, id) | 837 anahtar × 7 katalog, tamlık testleri |
| 3 | Koyu tema | `Themes/Light.xaml`, `Themes/Dark.xaml` |
| 4 | Uygulama içi güncelleme | `Core/UpdateService.cs` + 24 test |
| 5 | Ayarlar kartı, hata bildir, imza | Görsel olarak doğrulandı |
| 6 | `scripts/stats.ps1` | Sahte `gh` ile uçtan uca denendi |
| 7 | Sürüm 1.2.0, README, paket | `README.md` (İngilizce) + `README.tr.md` |

Test durumu: **1089 geçti, 0 başarısız, 2 atlandı** (canlı donanım testleri).

## İkinci tur (v1.2 kapsamı dışı bırakılanlar hariç her şey)

Hariç tutulanlar: canlı tema değişimi, LCD Overdrive, sıcaklık uyarısı, dışa aktarılabilir sağlık raporu.

| # | İş | Not |
|---|---|---|
| 1.1 | Tepsi menüsü çevirisi | |
| 1.2 | Klavye odak görünürlüğü | `FocusRing` tüm kontrol stillerinde; Popup ve Hyperlink için ayrı tetikleyici |
| 1.3 | Güncelleyici atomikliği | |
| 1.4 | OSD ölü parametreleri | `Models.cs` yalnızca `OsdEnabled`, `OsdCorner`, `OsdMonitor` |
| 1.5 | Debloat korumalı servis assert'i | |
| 1.6 | Kritik boş catch'lere log | |
| 2.1 | Yazılım fan eğrisi | `Core/FanCurve.cs`, 6 nokta, 3 °C histerezis, min bekleme |
| 2.2 | Oyun başına fan eğrisi | `TargetFanCurveEnabled` / `TargetFanCurvePoints` |
| 3.1 | CLI | `--mode`, `--fan`, `--profile`; çıkış kodları 0/1/2/3 |
| 3.2 | ~~VisionCare / renk sıcaklığı~~ | **Kaldırıldı** (`759ac55`): servis, testler, ayar alanları, kart, 11 katalog anahtarı |
| 3.4 | Çeviri tutarlılık denetimi | |
| 3.4b | Almanca yerleşim kusurları | `e114723` |
| 3.5 | Kod imzalama | Sertifika yok; README'de "Trust and code signing" bölümü |

## Son turda düzeltilenler

- **Güncelleyici paket uyumsuzluğu** (`92dc6d7`): `PackageFiles` StandbyPurge exe'sini kökte arıyordu, paket onu `StandbyPurge/` altına koyuyor. Her gerçek güncelleme `InvalidDataException` ile ölecekti.
- **Yarım indirme geçici dosya sızıntısı**: `DownloadAsync` artık hatada temizliyor.
- **`NitroLite.exe.old` birikmesi**: `UpdateService.CleanUpPreviousVersion()`, açılışta çağrılıyor.
- **Taze kurulumun dili** (`7c38d5c`): İngilizce kurulum `language: null` yazıyordu, ikinci açılışta Türkçeye düşüyordu. `Loc.ResolveStartupLanguage` + `App.PersistStartupLanguage`.
- **Taşan ComboBox'lar** (`1bf6c76`): `CmbGpuMode`, `CmbChartMetric`, `CmbDefaultAtkMouseProfile` sabit genişlikten `MinWidth`/`MaxWidth`'e geçti; Rusça, Portekizce, Endonezce etiketler kesilmiyor.
- **Hata bildirimi gövdesi** (`efae4ce`): başlık çevriliyordu ama gövde sabit İngilizceydi. Üç bölüm başlığı ve `Dizüstü`/`Dil` etiketleri artık katalogdan geliyor; `GetMachineModel` de "unknown" yerine `Bilinmiyor` anahtarı kullanıyor. Almanca, Rusça ve Hintçe'de tarayıcıda doğrulandı.
- **Bozuk profil listesi mesajı** (`6d07574`): `GameDetectionService` kendi `JsonException`'ını Türkçe atıyordu ve bu metin `ProfileLoadError` içinde kullanıcıya ulaşıyordu. Artık `Loc.T` üzerinden geçiyor, anahtar 7 katalogda var.
- **Uygulama ikonu** (`8581555`): eski neon şimşek yerine kayıt şeridi (sismograf izi). `paper-deep` zemin, `ink` iz, `vermilion` nokta. 10 kare: 16/20/24/32/40/48/64 DIB, 96/128/256 PNG. DIB/PNG ayrımı zorunlu: `Icon.ToBitmap()` PNG karelerde atıyor, tepsi ikonu 32x32 DIB okuyor. `DESIGN.md`'ye `## Icon` bölümü eklendi.
- **Almanca yerleşim** (`e114723`): kenar çubuğu 200 px / ~148 logical px içerik olduğu için Almanca etiketler iki satıra kırılıyordu; 7 katalogda 4 nav etiketi kısaltıldı. `CheckBox`/`RadioButton` şablonları yatay `StackPanel` kullanıyordu; yatay StackPanel çocuklarını sonsuz genişlikle ölçtüğü için içindeki `TextWrapping` hiç sarmıyordu. 2 sütunlu `Grid`'e (`Auto`/`*`) çevrildi — bu, uygulamadaki **tüm** onay kutularını ve radyo düğmelerini düzeltti. Sayfa/kart başlıklarındaki tek hücreli Grid'ler 2 sütuna (`*`/`Auto`) alındı. `TextTrimming` yerine `TextWrapping`. RGB profil ComboBox'ına "Profil seçilmedi" yer tutucusu.
- **IL3000** (`dd30b30`): `IsPackaged` bilerek `Assembly.Location`'ın boş olmasına bakıyor; uyarı özellik üzerinde bastırıldı, release derlemesi yine uyarısız.

## Üçüncü tur (`759ac55`)

| # | İş | Not |
|---|---|---|
| 1 | Tema: **Sistem varsayılanı** | `AppTheme.System = 2`; `ThemeManager.Resolve` `HKCU\...\Themes\Personalize\AppsUseLightTheme` okur (0 = koyu). Saklanan değer çözülmüş palet değil, tercihin kendisi. 6 yeni test. |
| 2 | Bileşen sıcaklık matrisi | `ShowTemperature` okuma yoksa satırı gizler; `-- °C` yok. Pil sıcaklık satırı tamamen kaldırıldı (`BatteryStatusInfo.TemperatureCelsius` hiçbir yerde set edilmiyordu). `StorageDriveInfo.IsTemperatureEstimated` kaldırıldı (hep `false` idi). Disk sıcaklığı yönetici izni ister; yeni P/Invoke **eklenmedi**. |
| 3 | VisionCare kaldırıldı | Servis + 251 satır test + kart + ayar alanları + 11 anahtar + README bölümleri. `grep` temiz. |
| 4 | Bekleme belleği sonucu | `MemoryCleanerResult.AvailableBeforeGigabytes`; mesaj "X serbest bırakıldı, kullanılabilir bellek A → B GB". Mekanizma **aynı** (standby listesi en iyisi); working-set kırpma eklenmedi. |
| 5 | Mod grupları | `ModePolicy.VisibleModeGroups`: Turbo/Performans adaptörde, Eco pilde. Etkin satır grubu gizli olsa da görünür kalır. 7 test. |
| 6 | Ayarlar kartı geri bildirim satırı | İki düğme metinle aynı Grid hücresindeydi, İspanyolca ve Hintçe'de üstüne biniyordu. `*`/`Auto` sütunlarına alındı. |

Doğrulama: `dotnet build` 0 uyarı 0 hata; `dotnet test` 977/977; CLI `--mode balanced --fan auto` exit 0, `--mode bogus` exit 3; `theme: 2` ile canlı açılış koyu geldi (Windows koyu); de/es/hi ekran görüntüleri kontrol edildi.

## Paket

`artifacts/NitroLite-v1.2.0-win-x64.zip` — 67.6 MB, SHA-256 `25CF3B1E66EC0722E3651767205B252B0762C1DD791A82AECFFB571C6CC5D7AE`.
İçerik: `NitroLite/LICENSE`, `NitroLite/NitroLite.exe`, `NitroLite/THIRD-PARTY-NOTICES.md`, `NitroLite/tools/WinMemoryCleaner.exe`, `NitroLite/tools/WinMemoryCleaner-LICENSE.txt`, `NitroLite/tools/WinMemoryCleaner-source.zip`. Bu liste `UpdateService.PackageFiles` ile birebir aynı.

Paketlenmiş exe boş klasörde denendi: v1.2 gösteriyor, İngilizce varsayılan, koyu tema çalışıyor, `--mode turbo --fan max` exit 0, `--mode bogus` exit 3. Güncelleme butonu görünmüyor (v1.1.0 daha eski, doğru davranış).

## Dördüncü tur: evrensel donanım (tüm Nitro serisi)

Hedef: uygulamayı tek test cihazına göre değil, Nitro 5/16/17 ve Nitro V 15/16'nın tamamına uyumlu hale getirmek.
Yaklaşım: sabit model veritabanı yerine **canlı yetenek sorgulama**. Bilinmeyen bir modelde de çalışır, çünkü
hiçbir şey varsayılmıyor; servis neyi bildirirse arayüz onu gösteriyor.

| # | İş | Commit | Not |
|---|---|---|---|
| 1 | Donanım kimliği | `6fe629f` | `Hardware/HardwareDetector.cs`, `SmbiosProbe.cs`, `DeviceIdentity.cs`: model kodu (AN16-41), anakart, BIOS, CPU üreticisi |
| 2 | Çalışma modu yeteneği | `f3f9653` | `SUPPORT_OPERATING_MODE_CAPABILITY`; olmayan mod butonu hiç görünmüyor |
| 3 | Klavye yeteneği | `e4b5894` | `KeyboardCapability`: bölge sayısı + efekt listesi; tek bölgeli klavyede bölge kartı gizli |
| 4 | Pil şarj sınırı | `abf87a7` | Care Center yoksa kutu kapalı + neden yazıyor |
| 5 | Efekt listesi cihazdan | `2272710` | `LightingPolicy.OfferedEffects`; sabit liste kaldırıldı |
| 6 | Yetenek özeti | `81d7a9b` | `DeviceCapabilities` + raporda CAPABILITIES bölümü |
| 7 | README uyumluluk | `1299460` | Sürücü/EC garantisi, donanım raporu, model bildirme |
| 8 | Servis yoksa uyarı | `06e8f50` | Acer servisi yoksa içerik üstünde kalıcı şerit; 3 ardışık hatadan sonra çıkar |

**Bilinçli olarak yapılmayan:** `device_profiles.json`. Statik tablo, tabloda olmayan yeni bir modelde yanlış
yetenek gösterir; canlı sorgulama her zaman doğruyu söyler. Tablo yalnızca sorgulanamayan şeyler için gerekirdi,
öyle bir alan kalmadı.

**Fan güvenliği:** özel fan eğrisi EC register'ına yazmıyor; Acer servisinin kendi fan API'sini kullanıyor.
`WinRing0`, `DeviceIoControl`, EC erişimi yok — anti-cheat ile çakışma riski sıfır.

**CPU güç limiti:** `CpuPowerService` Windows güç planı `PerfBoostMode` ayarını kullanıyor; AMD ve Intel'de aynı
şekilde çalışır. RyzenAdj veya SMU erişimi yok.

Doğrulama: 1089 test geçti; 5 sayfa × 2 tema ekran görüntüsü; Rusça ve Hintçe batarya sayfası (Kiril ve
Devanagari taşma yok); paketlenmiş exe boş klasörde `--hardware-report`, `--mode balanced --fan auto` (exit 0),
`--mode bogus` (exit 3).

## Yapılmayan tek şey

`git push` ve v1.2.0 release yayını. Kullanıcı "gönder" dediğinde:

```powershell
git push origin main
gh release create v1.2.0 artifacts/NitroLite-v1.2.0-win-x64.zip `
    --title "NitroLite v1.2.0" --notes-file <notlar>
```

Not: v1.1.0 kullanıcıları bu sürümü bir kez elle indirmek zorunda; onlarda güncelleyici yok.

## Bilinen sınırlar

- Çevirileri yazar yaptı; anadili konuşan biri kontrol etmedi. README'de düzeltme issue'su isteniyor.
- NitroLite imzasız. Güncelleme güveni HTTPS + GitHub'ın yayınladığı SHA-256 özetine dayanıyor.
- Güncelleme yalnızca release paketiyle çalışır; geliştirme derlemesinde buton release sayfasını açar.
- Yükseltilmiş WinMemoryCleaner yolu hiç test edilemedi (geliştirme ortamı yönetici değil).
- `TxtBatRemainingTime` kartın sağ kenarına sıfır boşlukla yaslanıyor; metin tam, kesik değil.
