# NitroLite — Optimizasyon ve Sertleştirme Özeti

Bu dosya 1.2.6 sürümü öncesinde yapılan otonom performans, sağlamlık ve test genişletme
çalışmasının kaydıdır. Depoya **eklenmedi**; bilinçli olarak untracked bırakıldı.

Tarih: 2026-11-18 · Dal: `main` · Ölçüm makinesi: Acer Nitro 16 AN16-41 (Ryzen 7 7840HS + RTX 4070)

---

## 1. Yapılan Commit'ler

Dokuz commit, hepsi `origin/main` üzerinde. Her biri dört kapıdan (`net10.0`,
`net10.0-windows`, `NitroLite.sln -c Release`, `dotnet test`) `-warnaserror` ile 0 hata /
0 uyarı geçtikten sonra atıldı.

| # | Hash | Başlık | Ne değişti |
|---|---|---|---|
| 1 | `92ec012` | `fix(startup): close the two races the probe left open` | `AppHost.cs`: `Lazy<CapabilitySnapshot?>` + ateşle-unut `Task.Run` yerine `Task.Run<CapabilitySnapshot?>` ve `GetAwaiter().GetResult()` ile yerel `Probe()`. `MainViewModel.cs`: prob await'inden sonra `if (_disposed) return;`, `finally` bloğu artık `InitializePhase3Async` / `_suppressWrite` / `SyncFanLoop()` üçlüsünü `if (!_disposed)` içinde çalıştırıyor. `Phase3Fakes.cs`: `StubHardware.ConnectCount`. Yeni `ProbeStressTests.cs` (8 test). |
| 2 | `14f925f` | `perf(linux): stop the telemetry tick from allocating its way through hwmon` | `LinuxHwmon.cs`: span tabanlı `TryParseIndexedName`, yerinde sıralama, elle `Find` döngüsü, tembel liste, yeni `Read(ISysfsFileSystem, List<LinuxHwmonDevice>, string root)` aşırı yüklemesi. `LinuxSysfsHardwareController.cs`: `_hwmonBuffer`. `RealSysfsFileSystem.cs`: `FileStream` artık `bufferSize: 1`. Yeni `LinuxTelemetryAllocationTests.cs` (7 test). |
| 3 | `ea8346c` | `perf(ui): cache the brushes the lighting page binds through` | `HexToBrushConverter.cs`: `ConcurrentDictionary<string, IBrush>` önbelleği, `MaxEntries = 256`, büyük/küçük harf duyarsız, başarısız ayrıştırma önbelleğe alınmıyor, `public int CachedCount`. Yeni `HexToBrushConverterTests.cs` (8 test). |
| 4 | `a779635` | `perf(i18n): stop re-reading the English catalogue, and translate the rows that were never translated` | `Loc.cs`: `LoadCatalog` artık `ConcurrentDictionary<string, IReadOnlyDictionary<string,string>> CatalogCache` üzerinden önbellekliyor; eski gövde `ReadCatalog` oldu. Ayrıca `en.json` içinde yarım Türkçe kalmış **34 satır** bulundu ve düzeltildi — diğer altı katalog bunları birebir kopyalamıştı. Yeni `LocalizationCompletenessTests.cs` (7 test). |
| 5 | `7fd6b54` | `perf(wmi): frame the worker protocol through one pooled buffer instead of three arrays` | `WmiWorkerProtocol.cs`: `PooledFrameWriter` (uzunluk öneki için 4 bayt ayıran özel `IBufferWriter<byte>`), `WriteAsync`, `ReadRequestAsync` + yeni `WmiWorkerReadResult(Request, Closed)` record struct, `ReadResponseAsync`, `ReadFrameAsync`. Kablo formatı **değişmedi**. `WmiWorkerClient.cs` / `WmiWorkerServer.cs` çağrı yerleri güncellendi. Yeni `WmiWorkerProtocolTests.cs` (17 test). |
| 6 | `75ef5b9` | `fix(fan): clamp the floor's output so a nonsense target cannot reach the firmware` | `ManualFanFloor.EffectivePercent` artık `Math.Clamp(Math.Max(targetPercent, FloorAt(temperatureC)), 0, 100)`. Önceden hedef `500` verildiğinde `500` dönüyordu. |
| 7 | `62cad85` | `test(chaos): cover suspend, concurrent commands, corrupt files and a moving /sys tree` | Yeni `ChaosAndConcurrencyTests.cs` (52 test, ~18 s), dört bölge: uyku/uyanma, eşzamanlı komutlar, bozuk dosyalar, hareket eden `/sys` ağacı. |
| 8 | `e44160b` | `ci(linux): prove the trim step still succeeds for linux-x64` | `ci.yml`: `staging/NitroLite-linux-x64-trimcheck` altına kırpılmış Linux publish'i çalıştırıp ikili dosyanın üretildiğini doğrulayan adım. |
| 9 | `a2a9512` | `docs: record the measured footprint and the test count for 1.2.6` | `README.md`: ölçülen ayak izi paragrafı gerçek sayılarla değişti, test sayısı dört yerde `2491 → 2603`. `CHANGELOG.md`: yeni `### Performance` bölümü ve `### Tests` altına yeni paketler. |

Önceki oturumdan gelen ve bu oturumda doğrulanan commit'ler: `56b11c0` (E — arka plan
yetenek probu), `a116d2c` (G — `PublishTrimmed`), `eee73dd` (font lisansı paket dışına),
`0b105ea` (README).

---

## 2. Önce → Son Karşılaştırma

Beş bağımsız soğuk açılış, 180 saniyelik tepsi CPU ölçümü, 200 saniye oturmuş tepsi RAM'i.
Hepsi `scripts/publish-release.ps1` ile üretilen gerçek tek dosya `.exe` üzerinde.

| Metrik | Önce | Faz 1 | Faz 3+4 (E & G) | **Final** |
|---|---|---|---|---|
| `.exe` boyutu | 42.79 MB | 42.79 MB | 24.3 MB | **23,747 KB (24.3 MB)** |
| İndirme zip'i | — | — | 18.6 MB | **18.6 MB** |
| Soğuk açılış → pencere | 5.6–6.8 s | 2.96 / 2.96 / 3.10 s | 3.29 / 3.49 / 3.74 s | **3361 / 3468 / 3480 / 3515 / 3416 ms — ort. 3448 ms** |
| Süreç görünme anı | — | — | — | **257–436 ms — ort. 300 ms** |
| Pencere açık WS / private | 274–279 MB | 278.7 / 207 MB | 231.1 / 179.5 MB | **234 / 178.2 MB** |
| Tepsi oturmuş WS / private | 72.8–88.5 MB | 72.5 / 203 MB | 74.1 / 172.9 MB, 1293 handle | **75.3 / 177.2 MB, 1318 handle** |
| Tepsi WS (yalnız 60 s sonra) | — | — | — | **137.2 MB** |
| Tepsi CPU (180 s) | 3.75–3.95 % | 3.52 % | 3.07 % | **3.36 % (tek çekirdeğin)** |
| Test sayısı | 2493 | 2496 / 2500 | 2500 / 2504 | **2603 toplam / 2599 geçen / 4 atlanan** |

Tepsi CPU'nun Task Manager karşılığı: 3.36 % ÷ 16 mantıksal çekirdek ≈ **%0.21**.

### Tahsis ölçümleri (mikro, testlerle sabitlendi)

| Yol | Önce | Sonra |
|---|---|---|
| Linux `hwmon` telemetri tick'i (gerçek `/sys`) | 19,232 B | **6,656 B** |
| Linux `hwmon` telemetri tick'i (bellek içi sahte) | — | **1,992 B** |
| `Loc.SetLanguage` | 657,804 B | **88 B** |
| WMI worker yazma | 432 B | **168 B** |
| WMI worker okuma | 896 B | **704 B** |

---

## 3. Eklenen Testler ve Kapatılan Riskler

| Paket | Test | Neyi garanti ediyor |
|---|---|---|
| `ProbeStressTests.cs` | 8 | Yavaş WMI cevabı, eksik sensör ve prob sürerken açılıp kapanan pencere. Prob `Task`'ı artık `Lazy` değil, dolayısıyla ilk erişimde fırlayan istisna yutulmuyor; dispose edilmiş view model prob döndükten sonra Phase3'ü başlatmıyor. |
| `LinuxTelemetryAllocationTests.cs` | 7 | Linux telemetri tick'inin ayırma bütçesi. Regresyon test olarak kırmızı düşüyor, sadece yavaşlama olarak görünmüyor. |
| `HexToBrushConverterTests.cs` | 8 | Aynı renk ikinci kez ayrıştırılmıyor, önbellek 256 girdide sınırlanıyor, bozuk hex önbelleğe yazılmıyor. |
| `LocalizationCompletenessTests.cs` | 7 | Sekiz katalog da tam **1658 anahtar**, anahtar kümeleri birebir aynı, hiçbir İngilizce satırda Türkçe harf yok. Dil değiştirme tahsis bütçesi. |
| `WmiWorkerProtocolTests.cs` | 17 | Havuzlanmış çerçeveleme gidiş-dönüşü, yarım çerçeve, bozuk uzunluk öneki, kapanmış akış. Kablo formatının değişmediğini doğruluyor. |
| `ChaosAndConcurrencyTests.cs` | 52 | Uyku/uyanma sonrası fan döngüsünün birikmemesi, sınırlı `StopAndWait`, boşluktan sonra termal taban; 6 iş parçacığından 254 komut; dispose yarışı; bozuk `profiles.user.json`; `.tmp` bırakmayan atomik yazım; yeniden numaralanan `hwmon`, `ENOENT`, `EACCES`; 31 model profilinin şema ve kendini eşleme tutarlılığı. |
| `ci.yml` trim adımı | — | Linux `-p:PublishTrimmed=true` yolunun sessizce kırılmadığını her push'ta kanıtlıyor. |

Toplam: **+99 test** (2504 → 2603).

---

## 4. Kalan Bilinen Sınırlar ve Sonraki Sürüm Önerileri

### Dürüst notlar

- **Açılış kazancının bir kısmı makine durumundan geliyor.** 5.6–6.8 s'den ~3.0 s'ye iniş,
  dört Faz 1 değişikliğinin açıklayabileceğinden büyük. Prob 59 ms, `CreateServices` 235 ms
  ölçüldü — yani denetim raporunun "prob 5–6 saniyenin sebebi" iddiası yanlıştı. Gerçek
  sebep büyük olasılıkla Defender ve disk önbelleği durumu. Sayı yine de gerçek ölçüm.
- **WMI worker %0 CPU gösteriyor**, dolayısıyla WMI tahsis kazancı boşta CPU'ya yansımıyor.
  Kazanç gerçek ama ölçülebilir bir kullanıcı etkisi yok; yük altında GC baskısını azaltıyor.
- **Tepsi çalışma kümesi oturma süresine bağlı.** 60 saniye sonra 137 MB, 200 saniye sonra
  75 MB. README ikisini de yazıyor; tek bir sayı vermek yanıltıcı olurdu.
- **`OFL.txt` paketleme hatası** (`eee73dd`) font commit'inden kalmaydı ve 1.2.6 sürümünü
  tamamen bloke edecekti. Hiçbir birim testi yakalayamazdı çünkü izin listesi kontrolü
  yalnızca paketleme sırasında çalışıyor.

### Bilinçli olarak dokunulmayanlar

- **`I` — Inter fontu kaldı.** Sekiz dilde glif kapsamı riskini almadık.
- **`J` — `TrimMemory` tek seferlik çağrı olarak kaldı.** Döngüye almak sabit disk sayfası
  hatasına ve takılmalara yol açar; sahte optimizasyon.
- **Linux paketi kırpılmıyor.** Kırpılmış Linux ikilisi 29 MB (kırpılmamış 83 MB) ama bu
  makinede çalıştırılamıyor. Çalışma zamanı doğrulanmadan gönderilmedi; CI yalnızca kırpma
  adımının başarılı olduğunu kanıtlıyor.

### Sonraki sürüm için

1. **Kırpılmış Linux ikilisini gerçek bir Linux makinesinde veya konteynerde duman testinden
   geçir**, sonra paketi 83 MB'tan 29 MB'a indir.
2. **`System.Management` bağımlılığını gözden geçir.** Trimming'in en büyük engelileyicisi o;
   WMI çağrılarını dar bir COM arayüzüne taşımak hem boyutu hem kırpma güvenliğini iyileştirir.
3. **Tepsi CPU'sunu 3.36 %'den aşağı çekmek için tek bir birleşik zamanlayıcı.** Şu an
   telemetri, oyun algılama ve Nitro tuşu ayrı uyanmalar üretiyor.
4. **`ReadyToRun` + `PublishSingleFile` sıkıştırmasını ölç.** Açılış süresini 3.4 s'den
   aşağı çekebilir; boyut/başlangıç dengesini gerçek ölçümle karar vermek gerek.
5. **`HardwareMatrixAndChaosTests` içindeki 5 kasa profilini 31 profile çıkar.** Şu an 31
   profilin şema tutarlılığı test ediliyor ama her biri için tam UI turu yok.
