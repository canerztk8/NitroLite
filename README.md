# NitroLite 🚀

[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Windows 11](https://img.shields.io/badge/Platform-Windows_11-0078D4?logo=windows11&logoColor=white)](https://www.microsoft.com/windows)
[![Tests](https://img.shields.io/badge/Tests-759-10B981)](#-testler)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**NitroLite**, Acer Nitro dizüstü bilgisayarlar (öncelikle **Acer Nitro 16 AN16-41**, Ryzen 7840HS + RTX 4070) için NitroSense'in yerini alan, hafif ve açık kaynaklı bir kontrol merkezidir. NitroSense'in kullandığı Acer servisleriyle (AcerAgentService, Acer Care Center) aynı protokolleri kullanır; ağır Electron arayüzünü çalıştırmaz.

**Ölçülen kaynak kullanımı** (AN16-41, tepside, Release): ~74 MB çalışma kümesi, boşta bir çekirdeğin yaklaşık %0,6'sı. Simge durumundayken telemetri 3 saniyede bir okunur.

---

## 🌟 Özellikler

### 1. Çalışma Modları ve Fan
- **5 güç modu:** Sessiz, Dengeli, Performans, Turbo ve Eco. Komutlar `AcerAgentService` (AASSvc, `127.0.0.1:46933`) üzerinden NitroSense ile aynı protokolle gönderilir.
- **Fan:** Otomatik eğri, maksimum devir veya özel sabit hız (CPU ve GPU fanı ayrı ayrı %30–100).
- **Mod tuşu:** Klavyedeki mod/Turbo tuşu NitroSense gibi modlar arasında geçiş yapar (adaptörde Dengeli → Performans → Turbo → Sessiz; pilde Dengeli ↔ Sessiz).
- Mod okunamazsa arayüz "Bilinmiyor" gösterir; varsayılan bir mod uydurmaz.

### 2. İşlemci Boost Yönetimi
- Windows güç planındaki işlemci boost ayarını (PerfBoostMode) değiştirir: Agresif, Verimli veya Devre dışı. Devre dışı modda işlemci temel saat hızının üzerine çıkmaz; sıcaklık ve fan gürültüsü düşer.

### 3. GPU Modu (MUX)
- NitroSense gibi, servisin bildirdiği modlar (`SUPPORT_GPU_MODE_CAPABILITY`) arasından seçim yapılır: Otomatik, Optimus, Yalnızca NVIDIA. Yeni mod yeniden başlatmadan sonra geçerli olur; NitroLite isterseniz yeniden başlatmayı da başlatır. Servis mod bildirmezse seçim kilitli kalır. NitroLite kayıt defterindeki MUX ayarlarını değiştirmez.

### 4. Oyun Algılama ve Oyuna Özel Profiller
- Kurulu oyunları (Steam ve yaygın kütüphane klasörleri) tarar; çalışan oyunu 2 saniyede bir kontrol eder.
- Her oyun için çalışma modu, CPU boost, fan ve fare profili tanımlanabilir. Oyundan çıkınca masaüstü ayarlarına dönülür.

### 5. ATK / VXE Fare Entegrasyonu (isteğe bağlı)
- HID üzerinden Config 1–4 arasında profil değiştirme, oyuna özel profil ve oyun bitince varsayılan profile dönüş.

### 6. Pil
- **%80 şarj sınırı:** Acer Care Center (`AcerCCAgent`, `wss://localhost:4343`) üzerinden NitroSense ile aynı şekilde ayarlanır ve geri okunarak doğrulanır. Care Center kurulu değilse bu seçenek çalışmaz.
- **Telemetri:** Anlık şarj/deşarj gücü, tahmini kalan süre, tam şarj kapasitesi ve aşınma oranı. Windows'un bildirmediği değerler "(tahmini)" etiketiyle gösterilir.
- **Pilde otomatik Eco:** Pile geçince düşük güç modu ve (isteğe bağlı) ana ekranda 60 Hz; adaptör takılınca önceki ayarlar geri yüklenir. Uygulama çökerse bu durum bir sonraki açılışta onarılır.

### 6b. Bekleme Belleğini Boşaltma
- Windows'un önbellekte tuttuğu bekleme listesini boş belleğe çevirir; çalışan programların belleğine dokunmaz. Kartta anlık bekleme / boş bellek miktarı görünür.
- İlk kullanımda bir kerelik kurulum (tek UAC onayı), sonra butona her basışta sormadan çalışır.
- İsteğe bağlı otomatik mod: gerçek boş bellek 1 GB'ın altına düşüp bekleme listesi 1 GB'ı aşınca, en fazla 5 dakikada bir (oyun sırasında da).

### 7. OSD (Ekran Üstü Gösterge)
- Üç satır: CPU (sıcaklık, tahmini güç, frekans, kullanım), GPU (sıcaklık, güç, frekans, VRAM), RAM ve fan.
- Oyuna DLL enjekte etmez; yalnızca her zaman üstte duran şeffaf bir penceredir. Dört köşeye yerleştirilebilir.

### 8. Klavye RGB (4 bölge)
- Statik, nefes, dalga, neon, kaydırma, zoom, meteor ve pırıltı efektleri; kendi profillerinizi kaydetme.
- Uykudan dönüşte ve oturum açılışında ayar yeniden uygulanır. 30 saniyelik boşta kapanma ayarı cihazdan okunur.

### 9. Sağlık Sekmesi
- Pil, SSD ve termal durum kartları. SMART erişimi olmayan disk sıcaklıkları ve termal macun/fan yatağı tahminleri "(tahmini)" olarak işaretlidir.

### Acer servisi kapalıysa
AASSvc yanıt vermezse NitroLite, Windows ve NVIDIA sürücüsünden okunabilen verileri (CPU kullanımı, RAM, GPU) göstermeye devam eder. Yalnızca serviste bulunan sensörler "Servis yok" olarak işaretlenir.

---

## 🏗️ Yapı

```
NitroLite
├── Core/
│   ├── AcerAgentClient.cs          # AASSvc TCP protokolü (mod, fan, GPU, RGB, telemetri, olaylar)
│   ├── CareCenterClient.cs         # Acer Care Center WebSocket istemcisi (%80 şarj sınırı)
│   ├── AtkMouseService.cs          # ATK / VXE fare HID protokolü
│   ├── BatteryWatchdogService.cs   # Pil durumu, sağlık ve deşarj telemetrisi
│   ├── CpuPowerService.cs          # Windows güç planı CPU boost ayarı
│   ├── PowerAutomationService.cs   # Adaptör/pil geçişleri ve Eco kurtarma
│   ├── GameDetectionService.cs     # Oyun tarama ve algılama
│   ├── HardwareMonitorService.cs   # NVML (GPU) ve Windows yedek telemetrisi
│   ├── StorageHealthService.cs     # Disk sağlık ve doluluk bilgisi
│   ├── ThermalHealthService.cs     # Termal geçmiş ve tahminler
│   ├── DebloatService.cs           # Acer arka plan servislerini kapatma / geri yükleme, otomatik başlatma
│   ├── MemoryCleanerService.cs     # Bekleme belleği ölçümü ve boşaltma
│   ├── StandbyPurgeTask.cs         # Sormadan boşaltma için bir kerelik yönetici görevi
│   ├── AppPaths.cs                 # Veri klasörü ve sistem aracı yolları
│   └── TelemetryFormatter.cs       # Değer biçimlendirme ve "tahmini" etiketleri
├── OsdOverlayWindow.xaml           # OSD penceresi
├── MainWindow.xaml                 # Ana arayüz
└── App.xaml                        # Tek örnek kontrolü ve hata kaydı
```

**Veri konumu:** Ayarlar, oyun profilleri, RGB profilleri, termal geçmiş ve günlük dosyası `%LOCALAPPDATA%\NitroLite` altında tutulur. Eski sürümlerin exe yanındaki dosyaları ilk açılışta buraya kopyalanır.

---

## 📥 Kurulum (indir ve çalıştır)

1. **[Releases](https://github.com/canerztk8/NitroLite/releases/latest)** sayfasından `NitroLite-v1.1.0-win-x64.zip` dosyasını indirin.
2. Zip'i **kalıcı bir klasöre** çıkarın, örneğin `C:\Users\<kullanıcı>\Programlar\NitroLite`. "İndirilenler" klasöründe bırakmayın: "Windows açılışında otomatik çalıştır" seçeneği exe'nin bulunduğu yeri hatırlar.
3. `NitroLite\NitroLite.exe` dosyasını açın. Kurulum sihirbazı yok, **.NET kurmanız gerekmez**; çalışma zamanı exe'nin içindedir.
4. Windows "**Windows bilgisayarınızı korudu**" (SmartScreen) derse **Ek bilgi → Yine de çalıştır**'a tıklayın. NitroLite dijital olarak imzalanmamış açık kaynaklı bir uygulama olduğu için bu uyarı çıkar.

Pencereyi kapatınca NitroLite sistem tepsisinde çalışmaya devam eder. Tamamen kapatmak için tepsi simgesine sağ tıklayıp **Çıkış**'ı seçin.

### Gereksinimler
- **Windows 11** (Windows 10 x64 da çalışır), 64 bit.
- **Acer servisleri:** `AcerAgentService` (mod, fan, RGB, telemetri) ve isteğe bağlı olarak Acer Care Center (%80 şarj sınırı). Bunlar NitroSense ile birlikte gelir. NitroSense arayüzünün açık olması gerekmez, ama kaldırılmamış olmalıdır.
- **Test edilen donanım:** Acer Nitro 16 AN16-41. Diğer Nitro / Predator modellerinde servisin desteklediğine göre bazı özellikler kilitli kalabilir.

### Yönetici izni ne zaman istenir?
NitroLite normal kullanıcı olarak çalışır. Windows yalnızca şu işlemlerde onay (UAC) sorar:
- **Bekleme belleğini boşaltma:** İlk kullanımda bir kerelik kurulum. Sonrasında sormadan çalışır. Kurulum, yalnızca bu işi yapan küçük programı `C:\Program Files\NitroLite\StandbyPurge` klasörüne koyar ve `\NitroLite\StandbyPurge` adlı bir Windows görevi kaydeder.
- **Acer arka plan servislerini kapatma / geri yükleme.**

### Güncelleme
NitroLite'ı tepsiden kapatın, yeni zip'i aynı klasöre çıkarıp dosyaların üzerine yazın. Ayarlarınız `%LOCALAPPDATA%\NitroLite` altında durduğu için korunur. Bekleme belleği aracı değiştiyse RAM kartı "Kurulum güncellenmeli" der ve bir kez onay ister.

### Kaldırma
1. RAM kartındaki **"Sormadan çalıştırma kurulumunu kaldır"** bağlantısına tıklayın (kurulum yaptıysanız).
2. Sistem Servisleri sayfasında **"Windows açılışında otomatik çalıştır"** seçeneğini kapatın. Servisleri kapattıysanız **"Servisleri Geri Yükle"**'ye basın.
3. NitroLite'ı tepsiden kapatıp klasörünü silin. Ayarları da silmek için `%LOCALAPPDATA%\NitroLite` klasörünü silin.

---

## 🛠️ Kaynaktan Derleme

```powershell
git clone https://github.com/canerztk8/NitroLite.git
cd NitroLite

dotnet test NitroLite.Tests/NitroLite.Tests.csproj
dotnet build -c Release NitroLite/NitroLite.csproj
```

Çalıştırılabilir dosya: `NitroLite/bin/Release/net10.0-windows/NitroLite.exe` (bu derleme [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) ister).

**İndirilebilir paketi üretmek için:**

```powershell
powershell -ExecutionPolicy Bypass -File scripts\publish-release.ps1
```

`artifacts\NitroLite-v<sürüm>-win-x64.zip` oluşur. İçinde .NET gerektirmeyen tek dosyalık `NitroLite.exe` ve Native AOT ile derlenmiş bekleme belleği aracı vardır. Betik, .NET çalışma zamanını yamalı sürüme (10.0.14, CVE-2026-111497 düzeltmesi) sabitler. Native AOT için Visual Studio Build Tools'ta "C++ ile masaüstü geliştirme" bileşeni gerekir.

---

## 🧪 Testler

759 test: protokol paketleri, Eco/oyun geçişleri, kurtarma senaryoları, biçimlendirme, sınır değerler ve eşzamanlılık.

```powershell
dotnet test NitroLite.Tests/NitroLite.Tests.csproj
```

- Testler kendi geçici veri klasörünü kullanır (`NITROLITE_DATA_DIR`); gerçek ayarlarınıza dokunmaz.
- Gerçek sistemi değiştiren 6 test (bekleme belleğini boşaltma, bağlı farenin profilini değiştirme, güç planı ayarı) varsayılan olarak atlanır. Bunları çalıştırmak için:

```powershell
$env:NITROLITE_ENABLE_LIVE_HARDWARE_TESTS = "1"; dotnet test NitroLite.Tests/NitroLite.Tests.csproj
```

---

## 📄 Lisans

Bu proje [MIT Lisansı](LICENSE) kapsamındadır.
