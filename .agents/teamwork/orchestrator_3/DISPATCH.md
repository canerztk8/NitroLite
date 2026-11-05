# Dispatch Log

## 2026-11-04T15:57:17Z

NitroLite uygulamasının OSD ve RTSS entegrasyonunda %0.1 LOW FPS hesaplama ve filtreleme mantığını düzeltmek, Counter-Strike 2 (CS2) ve anti-cheat korumalı oyunlarda FPS/frametime telemetrisinin güvenilir şekilde okunup görüntülenmesini sağlamak.

Working directory: c:\Users\caner\Documents\antigravity\eager-shannon
Integrity mode: development

## Requirements

### R1. %0.1 LOW FPS Hesaplama ve Filtreleme Mantığının Düzeltilmesi
Spotifast ve diğer uygulamalarda %0.1 LOW FPS değerinin hatalı/saçma sonuçlar (ör. 398798 FPS) üretmesi kalıcı olarak engellenmelidir. Frametime ring buffer ve RTSS istatistiklerinden hesaplama yapılırken standart persentil hesabı uygulanmalı, mikrosaniye mertebesindeki geçersiz kare süreleri (<1ms / 1-2 us) filtrelenmeli, %0.1 LOW FPS ortalama FPS değerini mantıksız şekilde aşmayacak şekilde üst sınır (clamping) ve mantık kontrolleriyle doğrulanmalıdır.

### R2. CS2 ve Anti-Cheat Korumalı Oyunlarda FPS Telemetrisi
Counter-Strike 2 (`cs2.exe`) gibi Valve Trusted Mode veya benzeri anti-cheat mekanizmaları nedeniyle standart RTSS DLL enjeksiyonunun engellendiği oyunlarda OSD'nin `FPS: -- (RTSS Bekleniyor)` durumunda kalması çözülmelidir. Ekip en uygun yaklaşımı (RTSS profil yapılandırması, PresentMon / DXGI ETW yedek motoru veya hibrit bir çözüm) belirleyerek CS2'nin kare hızı ve kare süresi verilerinin yakalanmasını sağlamalıdır. ETW/PresentMon yedek mekanizması için sistemde Yönetici (Administrator) yetkisi gerekmesi kabul edilebilirdir.

### R3. Test Kapsamı ve Release Derlemesi
`NitroLite.Tests` projesindeki mevcut tüm birim testler (320+ test) yeşil kalmalı, %0.1 LOW FPS hesaplama/filtreleme algoritmaları ve CS2 telemetri mekanizması için yeni birim testler eklenmeli ve tüm çözüm Release modunda hatasız derlenmelidir.

## Acceptance Criteria

### %0.1 LOW FPS Doğruluğu
- [ ] %0.1 LOW FPS değeri hiçbir koşulda ortalama FPS değerini aşmamalı ve gerçek dışı aşırı yüksek değerler (ör. 398798 FPS) üretmemelidir.
- [ ] Ring buffer içerisindeki mikrosaniye mertebesindeki (<1ms) geçersiz veya aykırı kare süreleri filtreleme mantığı tarafından elenmelidir.
- [ ] Yetersiz kare verisi veya geçersiz değer durumunda mantıklı bir varsayılan/fallback gösterimi sağlanmalıdır.

### CS2 FPS Telemetrisi
- [ ] CS2 (`cs2.exe`) çalışırken OSD üzerinde geçerli FPS ve frametime değerleri başarıyla yakalanıp görüntülenmelidir.
- [ ] Anti-cheat kısıtlamaları nedeniyle RTSS doğrudan kancalanamasa dahi belirlenen telemetri mekanizması (profil ayarı veya PresentMon/ETW) üzerinden kare verisi temin edilmelidir.

### Test ve Derleme
- [ ] `NitroLite.Tests` projesindeki mevcut 320+ birim test ve yeni eklenen tüm testler `dotnet test` çalıştırıldığında başarıyla (yeşil) geçmelidir.
- [ ] `dotnet build -c Release` hatasız tamamlanmalıdır.
