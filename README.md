# NitroLite 🚀

[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Windows 11](https://img.shields.io/badge/Platform-Windows_11-0078D4?logo=windows11&logoColor=white)](https://www.microsoft.com/windows)
[![Tests](https://img.shields.io/badge/Tests-386%20Passed-10B981)](#testler)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Architecture](https://img.shields.io/badge/Architecture-Clean%20%2F%20Non--Blocking-0EA5E9)](#mimari)

**NitroLite**, Acer Nitro ve Predator serisi oyuncu dizüstü bilgisayarları (özellikle **Acer Nitro 16 AN16-41** ve Zen 4 / RTX 40 serisi) için geliştirilmiş, ağır ve kaynak tüketen OEM yazılımlarının (NitroSense vb.) yerini alan **ultra hafif, açık kaynaklı ve modern** bir sistem kontrol merkezidir.

Standart OEM yazılımları arka planda 300 MB'tan fazla RAM ve sürekli CPU tüketirken, NitroLite **~25 MB RAM** ve **%0 boşta CPU** kullanımıyla çalışır.

---

## 🌟 Öne Çıkan Özellikler

### 1. Çalışma Modları ve Donanım Güç Limitleri
- **4 Farklı Güç Modu:** Sessiz (*Quiet*), Dengeli (*Default/Balanced*), Performans (*Performance*), Turbo (*Extreme*).
- **Yerel Donanım Köprüsü:** `AASSvc` (Port 46933) üzerinden gömülü denetleyiciye (EC) doğrudan komut iletimi.
- **Fan Kontrolü:** Otomatik akıllı fan eğrisi veya Maksimum Devir (Max RPM) profilleri.

### 2. İşlemci Performans Artışı (CPU Boost) Yönetimi
- **Agresif (5.0+ GHz):** Maksimum tek çekirdek ve rekabetçi FPS performansı.
- **Dengeli (~4.2 GHz):** Isıyı kontrol altında tutan yüke duyarlı frekans yönetimi.
- **Devre Dışı (3.2 GHz):** Temel saat frekansı sınırı; fan gürültüsünü ve sıcaklığı minimuma indirir.

### 3. GPU Çalışma Modu (MUX Switch) & Güvenlik
- **NVIDIA Advanced Optimus (DDS) Entegrasyonu:** Yeniden başlatma gerektirmeden oyunlarda harici RTX GPU'ya, masaüstünde dahili Radeon iGPU'ya akıllı geçiş.
- **Donanım Koruması:** Zorunlu MUX kilitlenmelerine ve siyah ekran risklerine karşı güvenli donanım modu.

### 4. Akıllı Oyun Algılama ve Otomatik Profiller
- **Sıfır Gecikmeli Algılama:** Sistemde çalışan oyunları (Steam, Epic Games vb.) otomatik tespit eder.
- **Oyuna Özel Yapılandırma:** Her oyun için ayrı çalışma modu, fan profili, CPU boost seviyesi ve fare profili tanımlanabilir.
- **Otomatik Geri Yükleme:** Oyundan çıkıldığında sistem ve fare otomatik olarak masaüstü ayarlarına döner.

### 5. ATK / VXE Oyuncu Faresi Donanım Entegrasyonu
- **Doğrudan HID İletişimi:** ATK A9 ve Compx fare protokolü üzerinden donanım sağlama toplamı (checksum) ile profil değiştirme.
- **Config 1..4 Desteği:** Her oyuna özel fare konfigürasyonu (DPI, polling rate, lod vb.) atayabilme.
- **Varsayılan Masaüstü Profili:** Oyun bittiğinde farenin otomatik olarak belirlenen varsayılan profile (ör. Config 1) dönmesi.

### 6. Pil Sağlığı, Şarj Eşiği ve Otomatik Eco Modu
- **%80 Şarj Sınırı:** Batarya ömrünü uzatan donanımsal şarj eşiği kontrolü.
- **Gerçek Zamanlı Telemetri:** Anlık deşarj gücü (W), tahmini kalan pil süresi, yıpranma oranı (wear level) ve döngü sayısı.
- **Fişten Çekilince Otomatik Eco:** Cihaz pildeyken ekranı otomatik 60 Hz'e çekip enerji tasarrufu sağlar; fişe takıldığında 165 Hz'e geri yükler.

### 7. Modern Fluent OSD (Ekran Üstü Gösterge)
- **Ultra Sade 3 Satırlı Gösterge:**
  - `CPU: [Sıcaklık] | [Güç W] | [Frekans GHz] | [Kullanım %]`
  - `GPU: [Sıcaklık] | [Güç W] | [Frekans MHz] | [VRAM]`
  - `RAM: [Kullanım GB] | FAN: [RPM]`
- **Bağımsız ve Güvenli:** Üçüncü taraf DLL enjeksiyonuna ihtiyaç duymaz; CS2 gibi korumalı oyunlarda hile korumasına takılmaz.
- **Köşe Değiştirme:** Ekranın 4 köşesi arasında tek tıkla konumlandırma.

### 8. 4 Bölgeli Klavye RGB Aydınlatması
- Statik renkler, dalga, nefes alma ve zoom efektleri.
- Windows oturum açılışında otomatik geri yükleme.

---

## 🏗️ Mimari ve Tasarım İlkeleri

NitroLite, Windows 11 Fluent tasarım standartlarına uygun olarak modern WPF ve C# 13 ile inşa edilmiştir:

```
NitroLite
├── Core/
│   ├── AcerAgentClient.cs      # AASSvc TCP donanım köprüsü (Port 46933)
│   ├── AtkMouseService.cs       # ATK / VXE raw HID donanım protokolü
│   ├── BatteryService.cs        # WMI / Win32 batarya ve deşarj telemetrisi
│   ├── CpuPowerService.cs       # Powercfg GUID işlemci boost yöneticisi
│   ├── GameDetectionService.cs  # Arka plan oyun algılama motoru
│   ├── DebloatService.cs        # OEM arka plan servis optimizasyonu
│   └── TelemetryFormatter.cs    # Güvenli biçimlendirici ve matematiksel limitler
├── OsdOverlayWindow.xaml        # Şeffaf, donanım hızlandırmalı OSD penceresi
├── MainWindow.xaml              # Fluent Dark UI ana kontrol merkezi
└── App.xaml                     # Tekil örnekleme (Single-Instance Mutex)
```

---

## 🛠️ Gereksinimler ve Kurulum

### Sistem Gereksinimleri
- **İşletim Sistemi:** Windows 11 (veya Windows 10 x64)
- **Çalışma Zamanı:** [.NET 10.0 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
- **Desteklenen Donanım:** Acer Nitro 16 (AN16-41) serisi (Zen 4 + RTX 40xx) ve uyumlu Acer Nitro / Predator modelleri.

### Kaynaktan Derleme

```powershell
# Projeyi klonlayın
git clone https://github.com/canerztrk8/NitroLite.git
cd NitroLite

# Testleri çalıştırın
dotnet test NitroLite.sln

# Release sürümünü derleyin
dotnet build -c Release NitroLite/NitroLite.csproj
```

Derlenen çalıştırılabilir dosya `NitroLite/bin/Release/net10.0-windows/NitroLite.exe` konumunda hazır olacaktır.

---

## 🧪 Testler

NitroLite kapsamlı bir test süitine sahiptir:
- **386 Birim ve Dayanıklılık Testi:** Donanım paket baytları, sağlama toplamları, sınır değerler, negatif sıcaklıklar, batarya formülleri ve eşzamanlılık testleri.
- Testleri çalıştırmak için:
  ```powershell
  dotnet test NitroLite.Tests/NitroLite.Tests.csproj
  ```

---

## 📄 Lisans

Bu proje [MIT Lisansı](LICENSE) kapsamında lisanslanmıştır. Dilediğiniz gibi kullanabilir, geliştirebilir ve paylaşabilirsiniz.
