# NitroLite logosu — kullanım kılavuzu

**Fikir:** Sıcak bir laptopun üstünde mışıl mışıl uyuyan bir kedi. Laptop serin ve sessiz, kimse rahatsız olmuyor.
Oyun yazılımlarının alev, şimşek ve kalkan klişelerinin tam tersi: sakin, hafif, samimi.

## Parçalar

| Parça | Dosya | Ne zaman |
|---|---|---|
| Yatay logo | `lockups/nitrolite-horizontal.svg` | Varsayılan: README başlığı, web sitesi, sürüm notları |
| Dikey logo | `lockups/nitrolite-stacked.svg` | Kare ya da dar alanlar: sosyal medya görseli, sticker, kapak |
| Sembol | `symbol/nitrolite-symbol.svg` | Logo zaten bağlamdaysa: avatar, uygulama içi başlık |
| Küçük sembol | `symbol/nitrolite-symbol-small.svg` | **48 px altı**: gözler yok, kulaklar biraz büyük, çubuk kalın |
| Yazı | `lockups/nitrolite-wordmark.svg` | Kedinin zaten göründüğü yerler (ör. uygulama penceresi başlığı) |
| Ters versiyonlar | `*-reversed.svg` | Koyu zeminler (koyu tema, karanlık README) |
| Tek renk | `*-black.svg`, `*-white.svg`, `*-mono-1b1917.svg` | Tek renk baskı, gravür, kazıma, faks gibi durumlar |
| Uygulama ikonu | `windows/nitrolite-app.ico` | Pencere, görev çubuğu, exe (16–256 px) |
| Tepsi ikonu | `windows/nitrolite-tray.ico` | Sistem tepsisi (16–32 px, şeffaf, küçük çizim) |
| Web ikonları | `web/` | favicon, apple-touch, PWA; `head-snippet.html` içinde hazır `<head>` satırları var |

## Renkler

Uygulamanın kendi paletiyle aynı; logo için yeni bir renk eklenmedi.

| Ad | HEX | RGB | CMYK (yaklaşık) | Görev |
|---|---|---|---|---|
| Mürekkep | `#1B1917` | 27 25 23 | 0 7 15 89 | Kedi ve yazı |
| Vermilyon | `#C0392B` | 192 57 43 | 0 70 78 25 | **Sadece** laptop çubuğu |
| Kâğıt | `#F2EDE3` | 242 237 227 | 0 2 6 5 | Zemin, ters versiyonda kedi ve yazı |

CMYK değerleri ekrandan hesaplandı. Baskıdan önce matbaanın prova çıktısıyla kontrol edin. Pantone karşılığı için
fiziksel bir renk kartelasından seçim yapılmalı; burada bir Pantone kodu uydurulmadı.

**Vermilyon tek vurgu rengidir.** Kediye, yazıya ya da gözlere vermilyon verilmez; çubuk dışında hiçbir parça renk taşımaz.

## Boşluk ve en küçük boyut

- **Boşluk payı:** Logonun her yanında en az **2 × çubuk yüksekliği** kadar boş alan bırakın (256'lık sembolde 40 birim).
  Bu alana yazı, kenar ya da başka bir logo girmez.
- **En küçük boyut:**
  - Yatay logo: ekranda 120 px genişlik, baskıda 30 mm.
  - Sembol: 16 px (48 px'in altında *küçük sembol* kullanılır), baskıda 8 mm.
  - Yazı tek başına: 80 px genişlik.

## Zeminler

- **Açık zemin** (beyaz, kâğıt, açık gri): normal versiyon.
- **Koyu zemin** (siyah, mürekkep, koyu tema): `-reversed` versiyonu. Normal versiyon koyu zeminde kaybolur.
- **Vermilyon zemin:** `-white` versiyonu. Normal versiyonda çubuk zeminle birleşir.
- **Fotoğraf üstü:** Sadece sakin, düşük kontrastlı bir alanda; gerekirse altına kâğıt rengi bir kutu koyun.

## Yapılmaması gerekenler

- Kediyi döndürmek, aynalamak, esnetmek ya da eğmek.
- Çubuğu kaldırmak (kedi sembolde her zaman laptopun üstündedir) ya da çubuğun rengini değiştirmek.
- Gözleri açmak, kediye ağız, bıyık veya başka bir detay eklemek.
- Gölge, parlama, degrade ya da kontur eklemek.
- Yazıyı başka bir fontla yeniden yazmak; harfler elle çizildi ve çizim dosyası kullanılmalı.
- Kedi ile yazı arasındaki mesafeyi ya da oranı değiştirmek; hazır yatay ve dikey dosyaları kullanın.

## Teknik notlar

- Kedi tek bir dış hat; gözler gerçek boşluk (`fill-rule="evenodd"`). Bu yüzden her zeminde doğru görünür.
- **Yazı (wordmark) hâlâ çizgi (stroke) tabanlı.** SVG içinde orantılı ölçeklenir ve tarayıcıda sorunsuzdur;
  ama kesim plotteri, nakış veya bazı baskı yazılımları için Inkscape/Illustrator'da *Stroke to Path* ile dolguya
  çevrilmesi gerekir.
- Kaynak geometri `docs/brand/logo-2026/kit/masters/` klasöründe.
- Marka tescil taraması yapılmadı. Ticari kullanımdan önce TÜRKPATENT ve EUIPO veritabanlarında arama yapılması önerilir.
