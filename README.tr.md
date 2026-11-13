# NitroLite

[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Windows 11](https://img.shields.io/badge/Platform-Windows_11-0078D4?logo=windows11&logoColor=white)](https://www.microsoft.com/windows)
[![Tests](https://img.shields.io/badge/Tests-1086-10B981)](#-testler)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**NitroLite**, Acer Nitro dizüstü bilgisayarlar (geliştirme **Acer Nitro 16 AN16-41**, Ryzen 7840HS + RTX 4070
üzerinde yapıldı) için NitroSense'in yerini alan, hafif ve açık kaynaklı bir kontrol merkezidir. NitroSense'in
kullandığı Acer servisleriyle (`AcerAgentService`, Acer Care Center) aynı protokolleri kullanır; ağır Electron
arayüzünü çalıştırmaz.

Geliştirildiği bilgisayara bağlı değildir: açılışta modeli, işlemci üreticisini ve Acer servisinin donanım için
bildirdiği yetenekleri okur, sonra yalnızca bu dizüstünün gerçekten sahip olduğu denetimleri gösterir. Bkz.
[Uyumluluk](#uyumluluk).

**Ölçülen kaynak kullanımı** (AN16-41, tepside, Release): ~74 MB çalışma kümesi, boşta bir çekirdeğin yaklaşık
%0,6'sı. Simge durumundayken telemetri 3 saniyede bir okunur.

*English README: [README.md](README.md)*

---

## Özellikler

### 1. Çalışma modları ve fan
- **Beş güç modu:** Sessiz, Dengeli, Performans, Turbo ve Eco. Komutlar `AcerAgentService` (AASSvc,
  `127.0.0.1:46933`) üzerinden NitroSense ile aynı protokolle gönderilir.
- **Fan:** Otomatik eğri, maksimum devir, özel sabit hız (CPU ve GPU fanı ayrı ayrı %30–100) veya kendi çizdiğiniz
  **yazılım fan eğrisi**.
- **Yazılım fan eğrisi:** 40 °C ile 90 °C arasında altı nokta; her nokta bir sıcaklığı fan hızına eşler. NitroLite
  işlemci sıcaklığını birkaç saniyede bir okur ve fanı karşılık gelen hıza taşır. 3 °C histerezis ve en az bekleme
  süresi, bir basamağa oturan sıcaklıkta fanın inip çıkmasını engeller. Eğri, kaydırıcılarla her zaman aynı olan bir
  çizgi olarak çizilir ve o anki çalışma noktası üzerinde işaretlenir. Varsayılan eğri 70 °C altında stok otomatik
  eğriden daha sessiz, 80 °C üstünde daha yüksektir.
- **Mod tuşu:** Klavyedeki mod/Turbo tuşu NitroSense gibi modlar arasında geçiş yapar (adaptörde Dengeli → Performans
  → Turbo → Sessiz; pilde Dengeli ↔ Sessiz).
- Mod okunamazsa arayüz "Bilinmiyor" gösterir; varsayılan bir mod uydurmaz.

### 2. İşlemci boost yönetimi
- Windows güç planındaki işlemci boost ayarını (PerfBoostMode) değiştirir: Agresif, Verimli veya Devre dışı. Devre
  dışı modda işlemci temel saat hızının üzerine çıkmaz; sıcaklık ve fan gürültüsü düşer.

### 3. GPU modu (MUX)
- NitroSense gibi, servisin bildirdiği modlar (`SUPPORT_GPU_MODE_CAPABILITY`) arasından seçim yapılır: Otomatik,
  Optimus, Yalnızca NVIDIA. Yeni mod yeniden başlatmadan sonra geçerli olur; NitroLite isterseniz yeniden başlatmayı
  da başlatır. Servis mod bildirmezse seçim kilitli kalır. NitroLite kayıt defterindeki MUX ayarlarını değiştirmez.

### 4. Oyun algılama ve oyuna özel profiller
- Kurulu oyunları (Steam ve yaygın kütüphane klasörleri) tarar; çalışan oyunu 2 saniyede bir kontrol eder.
- Her oyun için çalışma modu, CPU boost, fan ve fare profili tanımlanabilir. Oyundan çıkınca masaüstü ayarlarına
  dönülür.
- Bir oyun sabit hız yerine **kendi fan eğrisini** de taşıyabilir; böylece ağır bir oyun masaüstünden daha yüksek bir
  eğri alır. Kapatılırsa yukarıdaki fan ayarı geçerli olur.

### 5. ATK / VXE fare entegrasyonu (isteğe bağlı)
- HID üzerinden Config 1–4 arasında profil değiştirme, oyuna özel profil ve oyun bitince varsayılan profile dönüş.

### 6. Pil
- **%80 şarj sınırı:** Acer Care Center (`AcerCCAgent`, `wss://localhost:4343`) üzerinden NitroSense ile aynı şekilde
  ayarlanır ve geri okunarak doğrulanır. Care Center kurulu değilse bu seçenek çalışmaz.
- **Telemetri:** Anlık şarj/deşarj gücü, tahmini kalan süre, tam şarj kapasitesi ve aşınma oranı. Windows'un
  bildirmediği değerler "(tahmini)" etiketiyle gösterilir.
- **Pilde otomatik Eco:** Pile geçince düşük güç modu ve (isteğe bağlı) ana ekranda 60 Hz; adaptör takılınca önceki
  ayarlar geri yüklenir. Uygulama çökerse bu durum bir sonraki açılışta onarılır.

### 6b. Bekleme belleğini boşaltma
- Windows'un önbellekte tuttuğu bekleme listesini boş belleğe çevirir; çalışan programların belleğine dokunmaz.
  Kartta anlık bekleme / boş bellek miktarı görünür; sonuç, gerçekten değişen sayıyı yani kullanılabilir belleğin
  öncesini ve sonrasını bildirir. Kullanılan RAM çubuğu değişmez, çünkü önbellekteki sayfalar hiçbir zaman
  "kullanımda" sayılmadı.
- Boşaltmayı [WinMemoryCleaner](https://github.com/IgorMundstein/WinMemoryCleaner) yapar. Araç paketin içinde
  `tools\` klasöründe gelir, kurulum gerekmez. Her basışta Windows yönetici izni sorar. Farklı bir sürüm denemek
  isterseniz kendi `WinMemoryCleaner.exe` dosyanızı `%LOCALAPPDATA%\NitroLite\tools\` klasörüne koyun; o kopya
  paketlenmiş olanın yerine geçer.
- Araç yönetici izni ister, bu yüzden Windows her basışta UAC sorar.

### 7. OSD (ekran üstü gösterge)
- Üç satır: CPU (sıcaklık, tahmini güç, frekans, kullanım), GPU (sıcaklık, güç, frekans, VRAM), RAM ve fan.
- Oyuna DLL enjekte etmez; yalnızca her zaman üstte duran şeffaf bir penceredir. Dört köşeye yerleştirilebilir.

### 8. Klavye RGB
- Bölge sayısı ve efekt listesi klavyenin kendisinden okunur; tek bölgeli veya tek renkli bir model, işe
  yaramayan dört bölgeli seçici yerine gerçekten kullanabildiği denetimleri görür.
- Statik, nefes, dalga, neon, kaydırma, zoom, meteor ve pırıltı efektleri; kendi profillerinizi kaydetme.
- Uykudan dönüşte ve oturum açılışında ayar yeniden uygulanır. 30 saniyelik boşta kapanma ayarı cihazdan okunur.

### 9. Sağlık sekmesi
- Pil, SSD ve termal durum kartları. SMART erişimi olmayan disk sıcaklıkları ve termal macun/fan yatağı tahminleri
  "(tahmini)" olarak işaretlidir.

### 10. Entegre sistem ve onarım araçları
Harici `.bat`, `.ps1` veya ayrı `.exe` araçlarına gerek kalmadan, tüm Acer ve Windows bakım/onarım işlemleri
doğrudan **Sistem Servisleri ve Araçlar** sekmesinden tek tıkla yürütülür:
- **Klavye RGB servisini onar:** Sürücü deposundaki güncel `AcerLightingService.exe` yolunu kayıt defterine bağlar ve
  servisleri onarır.
- **Acer & NitroSense servislerini onar:** Sürücü deposundaki `AcerCentralService` ve `AcerLightingService` yollarını
  günceller, çakışan süreçleri sonlandırır ve servisleri yeniden başlatır.
- **MUX Switch kaydını onar:** `discrete_gpu_support = 1` anahtarını doğrular, eski kalıcı oturum görevlerini temizler
  ve Quick Access servisini başlatır.
- **Windows & sistem optimizasyonu:** HAGS (GPU donanım hızlandırma), NVMe TRIM, telemetri görevlerini devre dışı
  bırakma ve arka plan servis temizliği uygular. Değiştirdiği her ayarı (`optimize_backup.json`) yedekler;
  **Geri Al** düğmesi servis başlangıç türlerini, HAGS değerini ve telemetri görevlerini yedekten geri yükler.
- **Komut satırı desteği:** `NitroLite.exe` üzerinden doğrudan `--fix-lighting`, `--fix-services`, `--restore-mux`,
  `--optimize-pc` veya `--restore-optimize` bayraklarıyla komut satırından da çalıştırılabilir.
  Sistem ayarlarını değiştiren komutlar onay ister: `NitroLite.exe --optimize-pc --yes`.

### Acer servisi kapalıysa
AASSvc yanıt vermezse NitroLite, Windows ve NVIDIA sürücüsünden okunabilen verileri (CPU kullanımı, RAM, GPU)
göstermeye devam eder. Yalnızca serviste bulunan sensörler "Servis yok" olarak işaretlenir.

---

## Uyumluluk

NitroLite tek bir modele değil, Acer Nitro serisinin tamamına (Nitro 5, 16 ve 17; Nitro V 15 ve 16; Intel ve AMD)
yönelir. Donanımla ilgili hiçbir şey sabit yazılmamıştır: uygulama sorar, cevabın kapsamadığı denetimi gizler.

| Ne | Cevap nereden geliyor | Cevap yoksa |
|---|---|---|
| Model, aile, BIOS, işlemci üreticisi | SMBIOS (`Win32_ComputerSystem`, `Win32_BaseBoard`, `Win32_BIOS`) | cihaz bloğu SMBIOS cevap vermedi diye yazar |
| Çalışma modları | `SUPPORT_OPERATING_MODE_CAPABILITY` | tüm modlar görünür kalır, çünkü başarısız sorgu modun olmadığının kanıtı değildir |
| Klavye bölgeleri ve efektleri | `GET_CONNECTED_DEVICE` | dört bölgeli düzen kalır, aynı sebeple |
| GPU modu (MUX) | `SUPPORT_GPU_MODE_CAPABILITY` | mod değiştirme notla kapatılır, asla tahmin edilmez |
| %80 şarj sınırı | Acer Care Center (`AcerCCAgent`) | onay kutusu kapatılır ve sebebini yazar |
| Fan eğrisi | Acer servisinin kendi sabit hız komutu | eğri kendini kapatır ve bunu söyler |

**Çekirdek sürücü yok, EC kaydına yazma yok.** Fan ve aydınlatma denetimi NitroSense'in zaten kullandığı Acer
servisi üzerinden, işlemci hız artışı Windows güç planı üzerinden yapılır. Burada `WinRing0` veya başka bir
sürücü yüklenmez; Vanguard, FACEIT ve EAC'nin itiraz edeceği bir şey yoktur.

### Modeliniz kapsam dışıysa

**Ayarlar → Donanım raporu** düğmesi veri klasörüne `hardware-report-<zaman>.txt` ve `.json` yazar ve klasörü
açar. Rapor model kodunu, BIOS'u, Acer servislerinin durumunu, klavye raporunu, çalışma modlarını, GPU modu
yeteneğini ve Acer WMI sınıfının cevap verip vermediğini listeler. Bir GitHub issue'suna ekleyin: "fan kaydırıcısı
hiçbir şey yapmıyor" ile düzeltme arasındaki fark budur. Seri numarası, kullanıcı adı, dosya yolu veya günlük
içeriği barındırmaz.

Aynı rapor pencere olmadan da alınır: `NitroLite.exe --hardware-report` (JSON için `--json`, iki dosyayı bir
klasöre yazmak için `--out <klasör>`).

---

## Diller

Arayüz İngilizce yazılmış ve yedi dile daha çevrilmiştir. **Ayarlar → Dil** bölümünden seçin; NitroLite yeni dille
açılmak için yaklaşık bir saniyede kendini yeniden başlatır.

| | | | |
|---|---|---|---|
| English | Türkçe | Deutsch | Español |
| Русский | Português | हिन्दी | Bahasa Indonesia |

- İlk açılışta Windows görüntüleme dili bu sekizden biriyse o kullanılır, değilse İngilizce.
- Sayılar, yüzdeler ve tarihler seçilen dile göre biçimlendirilir.
- Teknik terimler (Turbo, Boost, OSD, MUX) çevrilmeden bırakılmıştır.
- Çevirileri yazar yaptı, anadili konuşan biri kontrol etmedi. Dilinizde bir kelime yanlış okunuyorsa lütfen dil ve
  doğru metinle [çeviri düzeltmesi için issue açın](https://github.com/canerztk8/NitroLite/issues/new?title=Translation%20correction&labels=translation).

## Tema

**Ayarlar → Tema** üç seçenek sunar: **Sistem varsayılanı**, açık "kâğıt" teması ve aynı vurgu renklerinden kurulmuş
koyu tema. Sistem varsayılanı Windows'un uygulama temasını izler ve açılışta çözülür. OSD her durumda koyudur. Dil
gibi, tema da hızlı yeniden başlatmayla uygulanır.

## Güncelleme

NitroLite açılıştan 30 saniye sonra ve ardından 6 saatte bir GitHub'da yeni sürüm olup olmadığını denetler; bu denetim
ayarlardan kapatılabilir. Yeni sürüm varsa sol üstteki sürüm etiketinin yanında **Güncelle v1.x.x** düğmesi çıkar.
Tek tıkla paket indirilir, SHA-256 özeti GitHub'ın bildirdiği özetle karşılaştırılır, exe değiştirilir ve NitroLite
yeniden başlar.

- Oyun oturumu sırasında hiçbir şey indirilmez.
- Geliştirme derlemesi kendini güncellemez; düğme release sayfasını açar.
- Klasöre yazılamıyorsa NitroLite bunu söyler ve release sayfasını açar.
- NitroLite imzasızdır; güven GitHub'a HTTPS ve özet doğrulamasına dayanır. GitHub hesabı ele geçirilirse bu koruma
  yetmez — hesapta iki adımlı doğrulama gerçek korumadır.

Elle de güncelleyebilirsiniz: NitroLite'ı tepsiden kapatın, yeni zip'i aynı klasöre çıkarıp dosyaların üzerine yazın.
Ayarlarınız `%LOCALAPPDATA%\NitroLite` altında durduğu için korunur.

## Hata bildirimi

**Ayarlar → Hata bildir** düğmesi, sürüm, Windows sürümü, dizüstü modeli ve dil bilgisi doldurulmuş olarak bir GitHub
issue sayfası açar. Göndermeden önce metni görürsünüz ve değiştirebilirsiniz. Kullanıcı adı, dosya yolu veya günlük
içeriği eklenmez. Yanındaki **Günlük klasörünü aç** bağlantısı, bir bölümü kendiniz yapıştırmak isterseniz günlüğe
götürür. **Donanım raporu** ise [Uyumluluk](#uyumluluk) bölümünde anlatılan yetenek raporunu yazar; sorun modelinizde
eksik veya işlevsiz bir özellikle ilgiliyse onu ekleyin.

---

## Yapı

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
│   ├── DebloatService.cs           # Acer arka plan servisleri, otomatik başlatma
│   ├── MemoryCleanerService.cs     # Bekleme belleği ölçümü; WinMemoryCleaner'ı çalıştırır
│   ├── SystemToolsService.cs       # Klavye, Acer servisi, MUX ve Windows onarım motoru
│   ├── Loc.cs / LocXaml.cs         # Çeviri katalogları ve {loc:T} işaretleme uzantısı
│   ├── ThemeManager.cs             # Açık ve koyu paletler
│   ├── UpdateService.cs            # GitHub sürüm denetimi, özet doğrulama, exe değişimi
│   ├── AppPaths.cs                 # Veri klasörü ve sistem aracı yolları
│   ├── TelemetryFormatter.cs       # Değer biçimlendirme ve "(tahmini)" etiketleri
│   └── Hardware/                   # Model kimliği ve yetenek sorgulama
│       ├── DeviceIdentity.cs       # Model kodu, aile, BIOS ve işlemci üreticisi ayrıştırma
│       ├── SmbiosProbe.cs          # Hata fırlatmayan salt okunur SMBIOS sorguları
│       ├── HardwareDetector.cs     # Önbellekli kimlik; SMBIOS sustuysa yeniden dener
│       ├── AcerWmiProbe.cs         # AcerGamingFunction varlığı ve metot listesi
│       ├── KeyboardCapability.cs   # GET_CONNECTED_DEVICE üzerinden bölgeler ve efektler
│       ├── DeviceCapabilities.cs   # Servisin bildirdiği her şey, tek kayıtta
│       └── HardwareReport.cs       # Kullanıcının issue'ya eklediği metin ve JSON raporu
├── Lang/                           # en, tr, de, es, ru, pt, hi, id
├── Themes/                         # Light.xaml, Dark.xaml
├── OsdOverlayWindow.xaml           # OSD penceresi
├── MainWindow.xaml                 # Ana arayüz
└── App.xaml                        # Tek örnek kontrolü, CLI bayrakları ve hata kaydı
```

**Veri konumu:** Ayarlar, oyun profilleri, RGB profilleri, termal geçmiş ve günlük dosyası
`%LOCALAPPDATA%\NitroLite` altında tutulur. Eski sürümlerin exe yanındaki dosyaları ilk açılışta buraya kopyalanır.

---

## Kurulum (indir ve çalıştır)

1. **[Releases](https://github.com/canerztk8/NitroLite/releases/latest)** sayfasından
   `NitroLite-v1.2.0-win-x64.zip` dosyasını indirin.
2. Zip'i **kalıcı bir klasöre** çıkarın, örneğin `C:\Users\<kullanıcı>\Programlar\NitroLite`. "İndirilenler"
   klasöründe bırakmayın: "Windows açılışında otomatik çalıştır" seçeneği exe'nin bulunduğu yeri hatırlar.
3. `NitroLite\NitroLite.exe` dosyasını açın. Kurulum sihirbazı yok, **.NET kurmanız gerekmez**; çalışma zamanı
   exe'nin içindedir.
4. Windows "**Windows bilgisayarınızı korudu**" (SmartScreen) derse **Ek bilgi → Yine de çalıştır**'a tıklayın.
   NitroLite dijital olarak imzalanmamış açık kaynaklı bir uygulama olduğu için bu uyarı çıkar.

Pencereyi kapatınca NitroLite sistem tepsisinde çalışmaya devam eder. Tamamen kapatmak için tepsi simgesine sağ
tıklayıp **Çıkış**'ı seçin.

### Gereksinimler
- **Windows 11** (Windows 10 x64 da çalışır), 64 bit.
- **Acer servisleri:** `AcerAgentService` (mod, fan, RGB, telemetri) ve isteğe bağlı olarak Acer Care Center (%80 şarj
  sınırı). Bunlar NitroSense ile birlikte gelir. NitroSense arayüzünün açık olması gerekmez, ama kaldırılmamış
  olmalıdır.
- **Test edilen donanım:** Acer Nitro 16 AN16-41. Diğer Nitro / Predator modellerinde servisin desteklediğine göre
  bazı özellikler kilitli kalabilir.

### Yönetici izni ne zaman istenir?
NitroLite normal kullanıcı olarak çalışır. Windows yalnızca şu işlemlerde onay (UAC) sorar:
- **Bekleme belleğini boşaltma:** WinMemoryCleaner bekleme listesini boşaltmak için yönetici izni ister, bu yüzden
  Windows her basışta sorar.
- **Acer arka plan servislerini kapatma / geri yükleme.**

### Kaldırma
1. Sistem Servisleri sayfasında **"Windows açılışında otomatik çalıştır"** seçeneğini kapatın. Servisleri
   kapattıysanız **"Servisleri Geri Yükle"**'ye basın.
2. NitroLite'ı tepsiden kapatıp klasörünü silin. Ayarları da silmek için `%LOCALAPPDATA%\NitroLite` klasörünü silin.
   Paketle gelen WinMemoryCleaner klasörle birlikte silinir; geride ayrıca bir şey kalmaz.

---

## Güven ve kod imzalama

**NitroLite kod imzalı değil.** SmartScreen'in kabul ettiği bir Windows kod imzalama sertifikası yılda birkaç yüz
dolar tutar ve kayıtlı bir şirket ister; bu projede ikisi de yok. Bu yüzden exe'de yayıncı imzası yoktur ve Windows
ilk çalıştırmada "Windows kişisel bilgisayarınızı korudu" uyarısını gösterir. Bu uyarı eksik imza hakkındadır,
dosyada bulunan bir şey hakkında değil.

Bunun yerine, yazara güvenmeden şunları kontrol edebilirsiniz:

| Kontrol | Nasıl |
|---|---|
| İndirme bu depodan geldi | Releases sayfasındaki bağlantı `github.com/canerztk8/NitroLite` adresini gösterir; taşıma GitHub'a HTTPS'tir. |
| Dosya yayınlanan dosya | `Get-FileHash NitroLite-v1.2.0-win-x64.zip -Algorithm SHA256` çıktısını GitHub'ın sürümde listelediği özetle karşılaştırın. |
| Kaynak ikiliyle aynı | Etiketten kendiniz derleyin; `scripts\publish-release.ps1` sürümü üreten betiğin aynısıdır. |
| Güncelleyici kısa yol almıyor | İndirilen paketin SHA-256'sını GitHub'ın yayınladığı özetle karşılaştırır, tutmazsa exe'yi değiştirmez. |

Dürüst sınır: bu GitHub hesabı ele geçirilirse saldırgan bir sürümü ve onun özetini birlikte yayınlayabilir, özet
kontrolü geçer. Sizi gerçekte koruyan şey imza değil, hesapta açık iki adımlı doğrulamadır. İmzalı bir ikili
gerekiyorsa kaynaktan derleyip kendi sertifikanızla imzalayın.

---

## Kaynaktan derleme

```powershell
git clone https://github.com/canerztk8/NitroLite.git
cd NitroLite

dotnet test NitroLite.Tests/NitroLite.Tests.csproj
dotnet build -c Release NitroLite/NitroLite.csproj
```

Çalıştırılabilir dosya: `NitroLite/bin/Release/net10.0-windows/NitroLite.exe` (bu derleme
[.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) ister).

**İndirilebilir paketi üretmek için:**

```powershell
powershell -ExecutionPolicy Bypass -File scripts\publish-release.ps1
```

`artifacts\NitroLite-v<sürüm>-win-x64.zip` oluşur. İçinde .NET gerektirmeyen tek dosyalık `NitroLite.exe` vardır.
Betik, .NET çalışma zamanını yamalı sürüme (10.0.14, CVE-2026-111497 düzeltmesi) sabitler ve bu sürüme
başvurmayan bir derlemeyi paketlemeyi reddeder.

---

## Testler

977 test: protokol paketleri, Eco/oyun geçişleri, kurtarma senaryoları, biçimlendirme, sınır değerler ve eşzamanlılık.

```powershell
dotnet test NitroLite.Tests/NitroLite.Tests.csproj
```

- Testler kendi geçici veri klasörünü kullanır (`NITROLITE_DATA_DIR`); gerçek ayarlarınıza dokunmaz.
- Gerçek sistemi değiştiren testler (bağlı farenin profilini değiştirme, güç planı ayarı) varsayılan olarak
  atlanır. Bunları çalıştırmak için:

```powershell
$env:NITROLITE_ENABLE_LIVE_HARDWARE_TESTS = "1"; dotnet test NitroLite.Tests/NitroLite.Tests.csproj
```

### Ekran görüntüsü

`scripts/screenshot.ps1`, uygulamayı açıp ekranın ortasına koyup elle görüntü almanıza gerek bırakmaz.
NitroLite'ı kendi geçici veri klasörüyle başlatır, pencereyi köşeye park eder, sayfaları gezer, her
birini `PrintWindow` ile yakalar, sonra uygulamayı kapatıp klasörü siler. Gerçek ayarlarınız hiç
okunmaz, hiç yazılmaz.

```powershell
# beş sayfa, koyu tema, Almanca
powershell -ExecutionPolicy Bypass -File scripts\screenshot.ps1 -All -Theme dark -Language de

# tek sayfa, istediğiniz klasöre
powershell -ExecutionPolicy Bypass -File scripts\screenshot.ps1 -Page NavBattery -Out C:\temp\pil
```

Sayfalar görünen etiketle değil `AutomationId` ile seçilir (`NavModes`, `NavMonitoring`, `NavRgb`,
`NavBattery`, `NavDebloat`), yani aynı komut sekiz dilde de çalışır. `-Position bottom` pencereyi açık
olan her şeyin arkasına iter. Tamamen görünmez bir mod yok: WPF ekran dışında kalan pencereyi
çizmez, `PrintWindow` da o zaman bayat kare döndürür.

---

## Kaç kişi kullanıyor

NitroLite içinde **telemetri yoktur** — sayaç, analitik, dışarıya çağrı yok. Tek sayılar GitHub'ın zaten tuttuğu
sayılardır ve `scripts/stats.ps1` bunları yazdırır:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\stats.ps1
```

Sürüm başına indirme sayısını (herkese açık) ve 14 günlük depo trafiğini — ziyaret, tekil ziyaretçi, klon — gösterir;
trafik verisini GitHub yalnızca depo sahibine verir. [GitHub CLI](https://cli.github.com) ve bir kerelik
`gh auth login` gerekir.

---

## Lisans

Bu proje [MIT Lisansı](LICENSE) kapsamındadır.

Paket ayrıca GPL-3.0 lisanslı [WinMemoryCleaner](https://github.com/IgorMundstein/WinMemoryCleaner) 3.0.8 sürümünü
içerir. Araç değiştirilmemiş, ayrı bir çalıştırılabilir dosyadır; NitroLite onu yalnızca alt süreç olarak başlatır.
Bu yüzden ikisi GPL-3.0 madde 5 anlamında bir "toplama" (aggregate) oluşturur ve NitroLite'ın kendi kodu MIT
kalır. Paket, aracın lisansını, bir bildirimi ve kaynak arşivini taşır; ayrıntılar `THIRD-PARTY-NOTICES.md`
dosyasında.
