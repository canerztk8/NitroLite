# Product

<!-- impeccable:product-schema 1 -->

## Platform

windows

## Users

Tek kullanıcı: bu Acer Nitro dizüstünün sahibi. Oyun oynarken ve günlük kullanımda fan, güç modu, klavye aydınlatması, pil ve MUX davranışını tek yerden yönetmek istiyor. NitroSense'i açmak zorunda kalmamak asıl motivasyon. Kişisel araç: başka kullanıcı, onboarding, çoklu hesap veya dağıtım senaryosu yok.

## Product Purpose

NitroLite, Acer Nitro dizüstülerde NitroSense'in yerini alan hafif bir Windows masaüstü uygulaması. AcerAgentService (127.0.0.1:46933) ve Acer Care Center (wss://localhost:4343) ile doğrudan konuşarak çalışma modu, fan hızı, klavye aydınlatması, pil şarj sınırı, MUX anahtarı ve donanım telemetrisini yönetir. Başarı: kullanıcı NitroSense'i hiç açmadan tüm donanım kontrolünü yapabilir ve ne olduğunu anlayabilir.

## Positioning

Aynı donanım kontrolünü, arada ek bir arka plan servisi ve telemetri katmanı olmadan tek süreçte sunmak. Protokol doğrudan Acer'ın kendi ajanına konuşur; uygulama kapalıyken hiçbir şey çalışmaz.

## Operating Context

- Windows 11, .NET 10 WPF, tek dosya self-contained yayın.
- Uygulama tepside yaşar; pencere kapansa da telemetri ve oyun algılama sürer.
- Oyun açılınca profil otomatik uygulanır, kapanınca masaüstü profili geri yüklenir.
- ATK kablosuz fare profili (Config 1-4) desteklenir; fare uykudayken komut yanıtsız kalabilir ve yeniden denenir.
- Bazı yollar yönetici hakkı ister (standby purge, disk sıcaklığı); yetki yoksa değer uydurulmaz.

## Capabilities and Constraints

- Çalışma modları: Sessiz, Dengeli, Performans, Turbo, Eco.
- Fan: Otomatik, Maksimum, Özel (CPU/GPU %30-100).
- Klavye aydınlatması: efekt, parlaklık, hız, yön, renk, bölge bazlı LED.
- Pil: şarj sınırı, sağlık, termal sağlık, döngü sayısı.
- Donanım izleme ve OSD overlay.
- MUX switch (yeniden başlatma gerektirir).
- Sistem servisleri, debloat ve onarım araçları.
- Kısıt: hafiflik. Boşta CPU ve RAM tüketimi düşük kalmalı; sürekli animasyon, ağır efekt veya büyük görsel katman eklenmemeli.
- Kısıt: uydurma veri yok. Okunamayan değer "--" olarak gösterilir, tahmin edilmez.
- Dil: yalnızca Türkçe.

## Brand Commitments

- Ad: NitroLite, sürüm 1.1.0, MIT lisansı.
- Emoji kullanılmaz.
- Görsel kimlik bağlayıcı değil: kullanıcı "daha modern" istedi, mevcut koyu tema ve vurgu renkleri korunmak zorunda değil.

## Evidence on Hand

- Çalışan uygulama ve yayınlanmış paket (`artifacts/`).
- 817 test: 811 geçen, 6 canlı donanım testi atlanmış.
- Gerçek donanım telemetrisi bu makinede doğrulandı (CPU/GPU sıcaklık, fan RPM, pil, ATK alıcısı).
- Uydurulacak müşteri, referans, benchmark veya basın içeriği yok.

## Product Principles

1. Hafiflik bir özellik: her yeni görsel katman, boşta tüketimi artırmıyorsa girer.
2. Dürüst telemetri: okunamayan değer uydurulmaz, "--" gösterilir.
3. Tek pencere, tek süreç: kullanıcı NitroSense'i açmak zorunda kalmaz.
4. Eylem anında geri bildirim: her donanım komutu sonucunu söyler (onaylandı, reddedildi, yanıt yok, deneniyor).
5. Türkçe arayüz, tanıdık Windows davranışı.

## Accessibility & Inclusion

- Klavye ile gezinme (mevcut RadioButton tabanlı gezinme korunur).
- Yüksek DPI'da okunabilirlik (2560x1380 ölçekli ekran).
- Durum yalnızca renkle değil metinle de belirtilir.
