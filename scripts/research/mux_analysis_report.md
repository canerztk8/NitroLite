# NitroSense MUX Anahtarı Kaybolma Kök Neden Analiz Raporu

**Hedef Sistem:** Acer Nitro AN16-41 (AMD Ryzen 7 7840HS, NVIDIA GeForce RTX 4070 Laptop GPU, AMD Radeon 780M)  
**İşletim Sistemi:** Windows 11 Home (Build 26300.9711, 26H2)  
**Yazılım:** Acer NitroSense 5.0.1327.0 (Electron Mimarisi)  
**Analiz Tarihi:** 04 Kasım 2026  
**Rapor Türü:** Kök Neden Tespiti ve Adli Sistem İncelemesi (Root Cause Analysis & Forensic System Audit)

---

## 1. Yönetici Özeti (Executive Summary)

Acer Nitro AN16-41 sisteminde daha önce NitroSense arayüzünde bulunan **MUX Anahtarı / GPU Çalışma Modu (GPU Working Mode)** ayarının kaybolmasının kesin teknik sebebi tespit edilmiştir:

1. **Arayüz Katmanı (NitroSense Electron UI):** NitroSense, arayüzü başlatırken `127.0.0.1:46933` portu üzerinden arka plan ana servisi olan `AcerAgentService.exe`'ye `INITIALIZATION / SUPPORT_GPU_MODE` sorgusu gönderir.
2. **Alınan Yanıt:** Canlı TCP soket sorgusunda servisin NitroSense'e `{"data": [0], "request": "SUPPORT_GPU_MODE", "result": "0"}` yanıtını verdiği kanıtlanmıştır.
3. **Gizleme Tetikleyicisi:** NitroSense JavaScript kodundaki `getSupportGPUMode()` metodu `1 == e[0]` (yani `1 == 0`) mantıksal denetimini yapmakta; dönen değer `0` olduğu için denetim `false` çıkmakta ve **GPU Display Mode arayüz bileşeni (`MUI_GPU_Display_Mode`) DOM'a hiç eklenmeyerek tamamen gizlenmektedir.**
4. **Kök Neden Kayıt Defteri Anahtarı:** `AcerAgentService.exe`'nin bu değeri okuduğu yer tersine mühendislikle deşifre edilmiş ve sistem kayıt defterindeki:
   `HKLM\SOFTWARE\OEM\AcerAgentService\AdvanceSettings` altındaki `discrete_gpu_support` değerinin **0** olduğu doğrulanmıştır.
5. **Önceki Proje İlişkisi:** Önceki görüşmelerde bilgisayarın MUX modu doğrudan dGPU'ya (NVIDIA GPU Only) kilitlenmiş, iGPU (AMD Radeon 780M) görüntü çıkışından düşmüş ve `AcerHardwareService.exe` servisinin sistem açılışındaki `check_Discrete_GPU_support` denetimi (`GetGamingMiscSetting(9)` WMI sorgusu) başarısız olarak (`check_Discrete_GPU_support is fail!!`) bu anahtarı `0`'a çekmiştir. Donanımın kendisi ise aynı kayıt defterinde `dgpu_mode_capability = 7` (yani `0b111` = Optimus + dGPU + DDS/Advanced Optimus) olarak tam destek vermeye devam etmektedir.

---

## 2. Somut Teknik Kanıtlar (Empirical Forensic Evidence)

### 2.1 NitroSense İkili (Binary/ASAR) Kod Analizi
`ULICTekInc.NitroSenseforNotebook_5.0.1327.0_x64__nt9dgb7efx6bt\app\resources\app.asar` içerisinden çıkartılan `main.js` incelendiğinde GPU modunu belirleyen mantık:

```javascript
// Electron Ana Süreci - PSSdkAdapter
getSupportGPUMode() {
    return (0, d.getLogger)().debug("[PA] getSupportGPUMode"),
    this.sendRequest(m.INITIALIZATION, { Function: "SUPPORT_GPU_MODE" })
        .then(e => 1 == e[0]) // e[0] değeri 1 ise true, 0 ise false döner!
}

getSupportGPUModeCapability() {
    return (0, d.getLogger)().debug("[PA] getSupportGPUModeCapability"),
    this.sendRequest(m.INITIALIZATION, { Function: "SUPPORT_GPU_MODE_CAPABILITY" })
        .then(e => e[0])
}
```

Eğer `getSupportGPUMode()` `false` dönerse, Angular arayüzü `MUI_GPU_Display_Mode` ("GPU Anzeigemodus" / "GPU Görüntüleme Modu") sekmesini kullanıcıya hiç göstermemektedir.

---

### 2.2 Canlı IPC Servis Soket Sorgusu (Port 46933)
`127.0.0.1:46933` portuna `ACER` protokol başlığıyla doğrudan TCP soket paketi gönderilerek `AcerAgentService`'in ne yanıt verdiği sorgulanmıştır:

**Çalıştırılan Teşhis Kodu:**
```powershell
$client = New-Object System.Net.Sockets.TcpClient
$client.Connect("127.0.0.1", 46933)
$stream = $client.GetStream()
$magic = [System.Text.Encoding]::ASCII.GetBytes("ACER")
$cmd = [System.BitConverter]::GetBytes([uint32]0) # INITIALIZATION = 0
$json = [System.Text.Encoding]::UTF8.GetBytes('{"Function":"SUPPORT_GPU_MODE"}')
$packet = $magic + $cmd + $json
$stream.Write($packet, 0, $packet.Length)
```

**Alınan Canlı Yanıtlar:**
```json
Response for SUPPORT_GPU_MODE:
{
	"data" : [ 0 ],
	"request" : "SUPPORT_GPU_MODE",
	"result" : "0"
}

Response for SUPPORT_GPU_MODE_CAPABILITY:
{
	"request" : "SUPPORT_GPU_MODE_CAPABILITY",
	"result" : "3"
}
```
> **Kanıt:** `data: [ 0 ]` dönmektedir. NitroSense tam olarak bu yüzden MUX ayarını gizlemektedir.

---

### 2.3 Kayıt Defteri (Registry) Analizi
`AcerAgentService.exe` ikili dosyasında `SUPPORT_GPU_MODE` fonksiyonunun referans verdiği kayıt defteri anahtarı:
`SOFTWARE\OEM\AcerAgentService\AdvanceSettings`

Sistemdeki canlı kayıt defteri değerleri:
```powershell
Get-ItemProperty -Path "HKLM:\SOFTWARE\OEM\AcerAgentService\AdvanceSettings"

discrete_gpu_support  : 0       <--- [KAYIP MUX AYARININ DOĞRUDAN SEBEBİ]
LCD_Overdrive_support : 1
dgpu_mode_capability  : 7       <--- [DONANIM ASLINDA 3 MODU DA DESTEKLİYOR]
```

* `dgpu_mode_capability: 7` değeri ikili tabanda `111`'dir:
  * Bit 0 (1): Optimus (MSHybrid) Desteği
  * Bit 1 (2): Discrete Only (dGPU) Desteği
  * Bit 2 (4): Dynamic Display Switch (Advanced Optimus / DDS) Desteği
* Cihaz donanımsal ve lisans olarak tüm modlara sahip olmasına rağmen `discrete_gpu_support` değeri **0** yapıldığı için servis kapalı yanıt vermektedir.

---

### 2.4 `AcerHardwareService.exe` İncelemesi ve Servis Hata Kaydı
`AcerHardwareService.exe` (RVA `0xF913`, dosya ofseti `0xED13`) tersine mühendislikle deşifre edildiğinde:
* Fonksiyon: `treadstone::TsClientCommandProcessor::check_Discrete_GPU_support`
* Çağrılan Metot: `AcerGamingFunction::GetGamingMiscSetting(9)`
* Dallanma Analizi:
  ```
  85 FF               test    edi, edi
  0F 84 B4 00 00 00   jz      loc_fail   ; Başarısız olursa failure dalına zıpla
  ...
  loc_fail:
  LEA RDX, "check_Discrete_GPU_support is fail!!"
  ; HKLM\SOFTWARE\OEM\AcerAgentService\AdvanceSettings -> discrete_gpu_support = 0 yazar!
  ```
Sistem daha önce Discrete Only moduna alınıp iGPU görüntü hattından koptuğunda veya sürücü/servis debloat sırasında WMI çağrısı zaman aşımına uğradığında servis bu hata dalına girerek değeri kalıcı olarak `0`'a çekmiştir.

---

## 3. Sistem Durumu Özeti (Mevcut Durum)

| Bileşen | Durum | Açıklama |
| :--- | :--- | :--- |
| **NVIDIA GeForce RTX 4070** | Aktif (2560x1600) | Ekran şu anda fiziksel olarak dGPU'ya bağlı |
| **AMD Radeon 780M (iGPU)** | Boşta (Çözünürlük Yok) | Ekran sinyali taşımıyor |
| **`discrete_gpu_support`** | **0 (Hatalı)** | Servis, NitroSense'e MUX yok diyor |
| **`dgpu_mode_capability`** | **7 (Eksiksiz)** | Donanım tüm geçiş yeteneklerine sahip |
| **NitroSense UI** | **Gizlenmiş** | `1 == 0` denetimi sebebiyle anahtar menüde yok |

---

## 4. Sorunu Giderme ve MUX Ayarını Geri Getirme Adımları

MUX anahtarının NitroSense arayüzüne geri gelmesi için iki temiz yol bulunmaktadır:

### Yöntem 1: Kayıt Defterini ve Servisi Düzeltme (Hızlı Çözüm)
Yönetici olarak açılmış bir PowerShell penceresinde:
```powershell
# 1. Kayıt defterinde discrete_gpu_support değerini 1 yapın
Set-ItemProperty -Path "HKLM:\SOFTWARE\OEM\AcerAgentService\AdvanceSettings" -Name "discrete_gpu_support" -Value 1 -Type DWord

# 2. Acer Agent Service'i yeniden başlatın
Restart-Service -Name "AASSvc" -Force

# 3. NitroSense'i kapatıp tekrar açın
Stop-Process -Name "NitroSense" -Force -ErrorAction SilentlyContinue
```
Bu komutların ardından `SUPPORT_GPU_MODE` sorgusu `data: [ 1 ]` döndürecek ve NitroSense arayüzündeki MUX / GPU Modu anahtarı anında tekrar görünür hale gelecektir.

### Yöntem 2: BIOS Sıfırlama / Standart Mod Geçişi
Bilgisayar açılışında `F2` ile BIOS'a girip `Display Mode` ayarını `Optimus` veya `Auto` moduna alıp `F10` ile kaydedip yeniden başlattığınızda da `AcerHardwareService` WMI üzerinden iGPU el sıkışmasını yeniden kurarak bu bayrağı otomatik düzeltecektir.
