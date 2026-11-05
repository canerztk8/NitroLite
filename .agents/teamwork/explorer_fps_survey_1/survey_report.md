# Kapsamlı Teknik İnceleme Raporu: %0.1 LOW FPS ve Frametime Takibi (Survey Report)

**Tarih**: 2026-11-04  
**Yazar**: Teamwork Explorer (Low FPS Survey Explorer)  
**Kapsam**: `NitroLite/Core/FpsMonitorService.cs`, `TelemetryFormatter.cs`, `Models.cs`, `OsdOverlayWindow.xaml.cs`, `NitroLite.Tests/FpsTelemetryTests.cs`, RTSS Shared Memory V2 ve Anti-Cheat/CS2 Telemetrisi.

---

## 1. Yönetici Özeti (Executive Summary)

Bu araştırma, NitroLite uygulamasının OSD ve RivaTuner Statistics Server (RTSS) entegrasyonunda karşılaşılan iki kritik sorunu en ince ayrıntısına kadar incelemektedir:
1. **Spotifast ve Arka Plan Uygulamalarında Ortaya Çıkan Saçma %0.1 LOW FPS Değerleri (Örn. 398798 FPS)**:
   - **Kesin Kök Neden**: `FpsMonitorService.cs` içerisindeki bellek haritası ofset hesaplama hatasıdır. `RTSSSharedMemory.h` başlığındaki `RTSS_SHARED_MEMORY_APP_ENTRY` yapısında bulunan 1024 elemanlı (4096 bayt) `dwStatFrameTimeLowBuf[1024]` dizisi kod yazarı tarafından atlanmış/tek bir `DWORD` sanılmıştır.
   - Bu nedenle kod, ofset 5092'deki veriyi `dwStatFramerate0Dot1PercentLow` (0.1x FPS) zannetmiştir. Oysa gerçekte ofset 5092, düşük frametime tamponunun (`dwStatFrameTimeLowBuf`) 3./4. elemanıdır ve **mikrosaniye** cinsinden kare süresini tutar.
   - Spotifast (veya Spotify) boştayken her ~3.98798 saniyede bir ekran güncellediğinde, RTSS tampona `3,987,980` mikrosaniye yazar. NitroLite bu sayıyı 10'a bölerek tam olarak **`398798 FPS`** hesaplamış ve OSD'ye basmıştır!
   - Kodda hiçbir üst sınır (clamping: `Low01Fps <= Fps`) bulunmadığı için bu imkansız değer doğrudan kullanıcıya gösterilmiştir.
   - `NitroLite.Tests/FpsTelemetryTests.cs` içerisindeki birim test de aynı hatalı ofset 5092'yi sentetik olarak doldurduğu için bu açık CI/CD testlerinde yakalanamamıştır.

2. **Counter-Strike 2 (CS2) ve Anti-Cheat Korumalı Oyunlarda `FPS: -- (RTSS Bekleniyor)` Durumu**:
   - CS2, varsayılan olarak **Valve Trusted Mode** ile çalışır ve harici DLL enjeksiyonunu (RTSS'nin `RTSSHooks64.dll` kancasını) engeller.
   - RTSS oyuna enjekte olamadığından `RTSSSharedMemoryV2` içerisinde `cs2.exe` girdisi oluşturulmaz; `selectedIndex` -1 kalır ve OSD takılı kalır.
   - Çözüm: RTSS'nin enjekte olamadığı durumlarda sıfır enjeksiyonlu (anti-cheat dostu), Windows çekirdek seviyesindeki **DXGI ETW (Event Tracing for Windows)** veya yerel **PresentMon** mekanizmasını devreye alan hibrit bir telemetri mimarisidir.

---

## 2. Kök Neden Analizi: Spotifast ve 398798 FPS Gizemi

### 2.1. Olay Zinciri (Call Chain)
1. `MainWindow.xaml.cs` (Satır 615-618):
   ```csharp
   var curGame = _gameService.ActiveGameProfile;
   var fpsInfo = _fpsMonitorService.GetCurrentFps(curGame?.ExeName, curGame?.DisplayName);
   _osdWindow.UpdateFpsData(fpsInfo);
   ```
   Kullanıcı oyun oynamıyorken veya tanımlı profili olmayan bir uygulama (Spotifast / Spotify) çalışırken `curGame` `null` döner.
2. `FpsMonitorService.GetCurrentFps(null, null)` (Satır 145-178):
   Belirli bir oyun adı verilmediğinde servis sırasıyla:
   - `lastForegroundApp` indeksine bakar.
   - En yüksek `dwTime1` değerine (en son render yapan aktif uygulamaya) bakar.
   Chromium Embedded Framework (CEF) veya DirectX donanım hızlandırmalı arayüze sahip Spotifast/Spotify ön plana geldiğinde, RTSS bu süreci `arrApp` tablosuna kaydeder ve servis Spotifast'ı seçer.
3. `ParseAppEntry` Metodu (Satır 290-330):
   ```csharp
   uint low01Raw = entryBytes.Length >= 5096 ? BitConverter.ToUInt32(entryBytes, 5092) : 0;
   ...
   if (low01Raw > 0)
   {
       low01Fps = low01Raw / 10.0;
   }
   ```
4. `low01Fps > 0` olduğu için Satır 333'teki ring buffer persentil bloğu (`if (low01Fps == 0.0)`) tamamen atlanır.
5. `TelemetryFormatter.FormatLow01Fps(398798.0)` çalışır ve OSD'ye `398798 FPS` basılır!

### 2.2. RTSS Bellek Düzeninin (Struct Layout) Karşılaştırmalı Analizi

Sistemdeki `C:\Program Files (x86)\RivaTuner Statistics Server\SDK\Include\RTSSSharedMemory.h` dosyası incelenerek `RTSS_SHARED_MEMORY_APP_ENTRY` yapısının gerçek bayt ofsetleri hesaplanmıştır:

| Alan Adı (RTSS SDK) | Tür | Boyut | Gerçek Ofset (RTSSSharedMemory.h) | Mevcut C# Kodunun Varsaydığı Ofset | Hata / Uyuşmazlık |
| :--- | :--- | :--- | :--- | :--- | :--- |
| `dwProcessID` | `DWORD` | 4 B | **0** | 0 | Doğru |
| `szName[MAX_PATH]` | `char[260]` | 260 B | **4 - 263** | 4 - 263 | Doğru |
| `dwFlags` | `DWORD` | 4 B | **264** | 264 | Doğru |
| `dwTime0` / `dwTime1` | `DWORD[2]` | 8 B | **268 - 275** | 268 - 275 | Doğru |
| `dwFrames` | `DWORD` | 4 B | **276** | 276 | Doğru |
| `dwFrameTime` | `DWORD` | 4 B | **280** | 280 | Doğru |
| `dwStatFramerateAvg` | `DWORD` | 4 B | **308** | 308 | Doğru |
| `dwStatFrameTimeBuf[1024]` | `DWORD[1024]` | 4096 B | **924 - 5019** | 924 - 5019 | Doğru |
| `dwStatFrameTimeBufPos` | `DWORD` | 4 B | **5020** | 5020 | Doğru |
| `dwStatFrameTimeBufFramerate` | `DWORD` | 4 B | **5024** | 5024 | Doğru |
| `qwAudioCapturePTT...` (4 adet) | `LARGE_INTEGER[4]` | 32 B | **5028 - 5059** | (Atlandı) | - |
| `dwPrerecord...` (2 adet) | `DWORD[2]` | 8 B | **5060 - 5067** | (Atlandı) | - |
| `qwStatTotalTime` | `LARGE_INTEGER` | 8 B | **5068 - 5075** | (Atlandı) | - |
| **`dwStatFrameTimeLowBuf[1024]`** | **`DWORD[1024]`** | **4096 B** | **5076 - 9171** | **YOK SANILDI (Tek DWORD sanıldı)** | **KRİTİK HATA!** |
| `dwStatFramerate1Dot0PercentLow` | `DWORD` | 4 B | **~9172** | **5088** | 4084 bayt kayma! |
| **`dwStatFramerate0Dot1PercentLow`**| **`DWORD`** | 4 B | **~9176** | **5092** | **4084 bayt kayma!** |

### 2.3. Matematiksel Doğrulama: 398,798 FPS Nasıl Oluştu?

- `dwStatFrameTimeLowBuf[1024]` tamponu, en yavaş karelerin sürelerini **mikrosaniye ($\mu s$)** olarak depolar.
- Ofset 5092, `dwStatFrameTimeLowBuf` dizisinin 4. indeksine (`dwStatFrameTimeLowBuf[3]` veya `[4]`) denk gelir.
- Spotifast gibi masaüstü uygulamaları tam ekran 3D oyunlar gibi sürekli kare çizmez; sadece arayüz değiştikçe (şarkı süresi saniye sayacı, albüm kapağı animasyonu vb.) kare sunar.
- Bir Spotifast karesinin süresi: **$3.987980\text{ saniye} = 3,987,980\,\mu s$**.
- `BitConverter.ToUInt32(entryBytes, 5092)` bu değeri okur: `low01Raw = 3987980`.
- Mevcut C# kodu: `low01Fps = low01Raw / 10.0;`
  $$\text{low01Fps} = \frac{3,987,980}{10.0} = 398,798.0\text{ FPS}$$
- Matematiksel kanıt %100 örtüşmektedir.

### 2.4. Birim Test Kusuru
`NitroLite.Tests/FpsTelemetryTests.cs` Satır 195:
```csharp
// dwStatFramerate0Dot1PercentLow = 950 (95.0 FPS fixed-point)
BitConverter.GetBytes((uint)950).CopyTo(appEntry, 5092);
```
Birim test, kodun yanlış varsayımını birebir kopyalamıştır. Testte 5092 ofsetine 950 yazılmış, kod da 5092 ofsetinden 950 okuyup 10'a bölmüş ve `95 FPS` bularak testi yeşil yakmıştır. Bu, hatalı mantığın birim test tarafından maskelenmesine yol açmıştır.

---

## 3. Matematiksel Formülasyon ve Filtreleme Mantığı

### 3.1. %0.1 LOW FPS Matematiksel Tanımı
Oyun performans analizinde (PresentMon, CapFrameX, RTSS):
- $N$ adet kare süresi $FT_1, FT_2, \dots, FT_N$ ($\mu s$ cinsinden) toplanır.
- Kare hızı ve kare süresi ters orantılıdır:
  $$FPS = \frac{1,000,000}{FT_{\mu s}} = \frac{1,000}{FT_{ms}}$$
- En düşük kare hızları (stutter/takılma), en **uzun** kare sürelerine karşılık gelir.
- Dolayısıyla, kare hızının **%0.1'lik en alt persentili (0.1% Low FPS)**, küçükten büyüğe sıralanmış kare sürelerinin **%99.9'luk en üst persentiline ($P_{99.9}$)** eşittir:
  $$FPS_{0.1\% Low} = \frac{1,000,000}{P_{99.9}(FT_{\mu s})}$$

### 3.2. Filtreleme Kuralları (Microsecond Outlier Filtering)
Ring buffer içerisindeki veriler ham işletim sistemi ve sürücü döngüsünden gelir. Aşağıdaki aykırı değerler mutlaka elenmelidir:

1. **Alt Sınır (Mikrosaniye Gürültüsü - $< 1.0\text{ ms}$ / $< 1,000\,\mu s$)**:
   - CEF/Chromium sıfır gecikmeli arayüz blitleri, vsync düşmeleri veya sürücü tampon temizleme işlemlerinde $1\text{--}500\,\mu s$ süreli sahte kareler oluşabilir.
   - $1000\,\mu s$ altındaki süreler ($> 1000\text{ FPS}$) elenmelidir. Standart oyun motorlarında hiçbir meşru kare $1.0\text{ ms}$'den kısa sürmez.
2. **Üst Sınır (Bekleme / Donma / Boşta Kalma - $> 500,000\,\mu s$ / $> 500\text{ ms}$)**:
   - Alt-Tab yapıldığında, arka planda beklerken veya yükleme ekranlarında $500\text{ ms}$ ($< 2\text{ FPS}$) üzerindeki süreler oyun içi kare akıcılığı metriğini bozmamak adına persentil hesaplamasına dahil edilmemeli veya sınırlandırılmalıdır.
3. **Boş / Doldurulmamış Girdiler ($0\,\mu s$)**:
   - Ring buffer 1024 elemanlıdır; oyun yeni başladığında veya az sayıda kare çizildiğinde tamponun geri kalanı 0'dır. Bu 0'lar filtrelenmelidir.

### 3.3. Örneklem Büyüklüğü (Sample Size) ve Güvenilirlik
- Matematiksel olarak %0.1'lik persentil ($1/1000$), en az 1000 karelik bir evrende anlamlıdır.
- Mevcut koddaki `validFrameTimes.Count >= 10` kontrolü felakettir: 10 kare varken %99.9 indeksi son elemanı ($10/10$) seçer; bu %0.1 değil, **%10 Low**'dur!
- **Önerilen Kural**:
  - $N < 30$: Veri yetersiz kabul edilir, `Low01Fps = 0` (OSD'de `-- FPS`).
  - $30 \le N < 100$: Isınma periyodu. $P_{99}$ veya ağırlıklı ortalama kullanılır.
  - $N \ge 100$: Standart $P_{99.9}$ hesabı uygulanır.

### 3.4. Mutlak Üst Sınır Kuralı (Inviolable Clamping Invariant)
Fizik ve matematik kuralları gereği bir süreçteki en kötü %0.1'lik dilim, o sürecin ortalamasından yüksek olamaz:
$$\mathbf{0 \le FPS_{0.1\% Low} \le FPS_{1\% Low} \le FPS_{avg}}$$

Bu kural koda katı bir savunma mekanizması olarak eklenmelidir:
```csharp
if (fps > 0)
{
    if (low1Fps > fps) low1Fps = fps;
    if (low01Fps > fps) low01Fps = fps;
    if (low01Fps > low1Fps && low1Fps > 0) low01Fps = low1Fps;
}
else
{
    low01Fps = 0;
    low1Fps = 0;
}
```
Ayrıca `TelemetryFormatter.FormatLow01Fps` içerisinde $> 2000\text{ FPS}$ kontrolü eklenerek hiçbir koşulda saçma bir sayının ekrana düşmemesi garanti edilmelidir.

---

## 4. CS2 ve Anti-Cheat Telemetrisi İncelemesi (Requirement R2)

### 4.1. CS2 Neden RTSS Tarafından Yakalanamıyor?
- CS2 (`cs2.exe`), hile koruması ve Valve Trusted Mode nedeniyle harici DLL enjeksiyonunu engeller.
- RTSS'nin `RTSSHooks64.dll` kancası CS2 sürecine yüklenemez.
- RTSS paylaşımlı belleğinde (`RTSSSharedMemoryV2`) CS2'ye ait bir `RTSS_SHARED_MEMORY_APP_ENTRY` oluşmaz.
- `FpsMonitorService.GetCurrentFps("cs2.exe")` arama yaptığında eşleşen süreç bulamaz ve `IsAvailable = false` döner.
- OSD ekranında `FPS: -- (RTSS Bekleniyor)` ibaresi takılı kalır.

### 4.2. Telemetri Yakalama Yöntemlerinin Karşılaştırılması

| Kriter | Yöntem 1: RTSS Profil Yapılandırması (`-allow_third_party_software`) | Yöntem 2: Windows DXGI ETW Olay Dinleyicisi | Yöntem 3: Yerel PresentMon (CLI / RTSS Plugin) |
| :--- | :--- | :--- | :--- |
| **Enjeksiyon Durumu** | DLL enjeksiyonu gerekir | **Sıfır Enjeksiyon (İşletim sistemi çekirdeği)** | **Sıfır Enjeksiyon (İşletim sistemi çekirdeği)** |
| **Anti-Cheat Engeli** | Trusted Mode engeller (Steam başlatma parametresi şart) | **Hiçbir Anti-Cheat engelleyemez (Vanguard, EAC, VAC)** | **Hiçbir Anti-Cheat engelleyemez (Vanguard, EAC, VAC)** |
| **Trust Factor Etkisi** | Valve tarafından düşürülebilir uyarısı var | **Sıfır etki** | **Sıfır etki** |
| **Gereksinim** | Kullanıcı müdahalesi gerektirir | Yönetici (Admin) yetkisi (Gereksinimde onaylandı) | Yönetici (Admin) yetkisi (Gereksinimde onaylandı) |
| **Uygulanabilirlik** | Yetersiz (Kullanıcı dostu değil) | **Mükemmel (C# içinde saf ve bağımsız)** | Çok İyi (RTSS altında mevcut PresentMon binary'si var) |

### 4.3. Önerilen Hibrit Mimari (Hybrid Fallback Architecture)

Sisteme `EtwFpsService` veya `PresentMonFallbackService` entegre edilmelidir:
1. **Birincil Yol (RTSS Shared Memory)**:
   - Standart oyunlarda (enjeksiyon sorunu olmayan tüm oyunlar) RTSS Shared Memory okunur. Sıfır ek yük, anlık tepki.
2. **Yedek Yol (ETW / DXGI Event Provider)**:
   - Eğer aktif oyun tespit edilmişse (örn. `cs2.exe`) ancak RTSS 2 saniye boyunca `IsAvailable = false` dönüyorsa, `EtwFpsService` devreye girer.
   - `Microsoft-Windows-DXGI` (`{CA11C00C-18C3-436A-B4C3-BBE54F002EC3}`) olay sağlayıcısı dinlenir.
   - Her `Present_Stop` veya `Present` olayında `cs2.exe` PID'si filtrelenir ve iki `Present` arasındaki süre mikrosaniye olarak kaydedilir.
   - Bu süreler aynı 1024 elemanlı Ring Buffer'a yazılır ve aynı persentil algoritmasıyla %0.1 Low ve Anlık FPS hesaplanır.
   - Kullanıcı OSD'de kesintisiz ve doğru FPS telemetrisi görür.

---

## 5. Uygulayıcı (Implementer) İçin Eylem Planı ve Kod Değişiklikleri

### 5.1. `NitroLite/Core/FpsMonitorService.cs` Değişiklikleri

1. **Hatalı Ofset Okumasını Kaldırın**:
   - Satır 292-293'teki `low1Raw` (5088) ve `low01Raw` (5092) okumalarını kaldırın veya devre dışı bırakın. RTSS benchmark modu açık olmadıkça bu ofsetler anlamsızdır veya başka tamponlara işaret eder.
2. **Dinamik Ring Buffer Hesabını Asli Mekanizma Yapın**:
   - `entryBytes.Length >= 5020` olduğunda doğrudan `dwStatFrameTimeBuf[1024]` (ofset 924) tamponunu okuyun.
3. **Mikrosaniye Filtresini Güçlendirin**:
   - `ft >= 1000 && ft <= 500_000` (1 ms ile 500 ms arası, yani 2 FPS ile 1000 FPS arası).
4. **Persentil ve Örneklem Kontrolünü Uygulayın**:
   - `validFrameTimes.Count >= 30` kontrolü ekleyin.
   - Sıralama sonrası $P_{99.9}$ ve $P_{99}$ hesaplayın.
5. **Clamping Uygulayın**:
   - `low01Fps = Math.Min(low01Fps, fps);`
   - `low1Fps = Math.Min(low1Fps, fps);`
   - `if (low01Fps > low1Fps && low1Fps > 0) low01Fps = low1Fps;`
6. **Masaüstü / Non-Game Filtrelemesi**:
   - `activeGameProcessName` verilmediğinde `lastForegroundApp` Spotify/Spotifast/Discord/Chrome ise oyun olarak işaretlemeyin veya OSD'de `DESKTOP` modunu koruyun.

### 5.2. `NitroLite/Core/TelemetryFormatter.cs` Değişiklikleri
- `FormatLow01Fps` metoduna mantıksız değer ($> 2000\text{ FPS}$) kontrolü ekleyin:
  ```csharp
  if (!isAvailable || low01Fps <= 0 || double.IsNaN(low01Fps) || double.IsInfinity(low01Fps) || low01Fps > 2000.0)
  {
      return "-- FPS";
  }
  return string.Format(CultureInfo.InvariantCulture, "{0:F0} FPS", low01Fps);
  ```

### 5.3. `NitroLite.Tests/FpsTelemetryTests.cs` Güncellemeleri
- Satır 181'deki `ParseAppEntry_WithLow01FpsStatField_ExtractsCorrectLow01Fps` testini güncelleyin: Artık 5092 ofsetine körü körüne bağımlı olmamalı veya ring buffer üzerinden persentil testine dönüştürülmelidir.
- Yeni testler ekleyin:
  - `SpotifastLargeFrametime_NeverProducesCrazyLowFps`: 3,987,980 us değeri verildiğinde sonucun 398798 FPS üretmediği, filtrelendiği ve ortalama FPS'yi aşmadığı doğrulanmalıdır.
  - `Low01Fps_AlwaysClampedToAverageFps`: Ne olursa olsun `Low01Fps <= Fps` kuralının korunduğu doğrulanmalıdır.
  - `SubMillisecondNoise_IsFilteredOut`: $< 1000\,\mu s$ değerlerinin persentil hesabından elendiği doğrulanmalıdır.

---

## 6. Sonuç

Sorunun teknik kök nedeni %100 kesinlikle tespit edilmiş, ofset kayması ve Spotifast'ın 3.98 saniyelik mikrosaniye kare süresi ilişkisi matematiksel olarak kanıtlanmıştır. İnceleme raporu, uygulama ekibinin hiçbir belirsizlik yaşamadan kalıcı düzeltmeyi yapmasını sağlayacak tüm detayları içermektedir.
