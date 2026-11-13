---
name: NitroLite
description: Acer Nitro dizüstüler için hafif NitroSense alternatifi — Departure Board görsel dünyası
colors:
  bg-dark: "#0E1216"
  rail: "#0A0D10"
  panel: "#11161A"
  card: "#151A1F"
  card-hover: "#1C2228"
  border: "#252D35"
  border-strong: "#3A444E"
  signal: "#F0B429"
  on-signal: "#0E1216"
  danger: "#E85C4A"
  warn-bg: "#2A1A16"
  ok: "#3FA96B"
  info: "#5B9BD5"
  text-primary: "#F4F1E8"
  text-secondary: "#A3ADB7"
  text-muted: "#7C8791"
typography:
  display:
    fontFamily: "Segoe UI Variable Text, Segoe UI, sans-serif"
    fontSize: "20px"
    fontWeight: 600
    lineHeight: 1.2
    letterSpacing: "0em"
  title:
    fontFamily: "Segoe UI Variable Text, Segoe UI, sans-serif"
    fontSize: "15px"
    fontWeight: 600
    lineHeight: 1.3
    letterSpacing: "0em"
  body:
    fontFamily: "Segoe UI Variable Text, Segoe UI, sans-serif"
    fontSize: "12px"
    fontWeight: 400
    lineHeight: 1.45
    letterSpacing: "0em"
  label:
    fontFamily: "Segoe UI Variable Text, Segoe UI, sans-serif"
    fontSize: "11px"
    fontWeight: 400
    lineHeight: 1.4
    letterSpacing: "0em"
  micro:
    fontFamily: "Segoe UI Variable Text, Segoe UI, sans-serif"
    fontSize: "10px"
    fontWeight: 700
    lineHeight: 1.3
    letterSpacing: "0.02em"
rounded:
  sm: "2px"
  md: "3px"
  lg: "4px"
spacing:
  xs: "4px"
  sm: "8px"
  md: "12px"
  lg: "16px"
  xl: "20px"
components:
  mode-row:
    backgroundColor: "{colors.card}"
    textColor: "{colors.text-primary}"
    rounded: "{rounded.md}"
    padding: "14px 11px"
  mode-row-active:
    backgroundColor: "{colors.signal}"
    textColor: "{colors.on-signal}"
    rounded: "{rounded.md}"
    padding: "14px 11px"
  nav-item:
    backgroundColor: "{colors.rail}"
    textColor: "{colors.text-secondary}"
    rounded: "{rounded.md}"
    padding: "12px 9px"
  nav-item-active:
    backgroundColor: "{colors.signal}"
    textColor: "{colors.on-signal}"
    rounded: "{rounded.md}"
    padding: "12px 9px"
  seg-button:
    backgroundColor: "{colors.panel}"
    textColor: "{colors.text-primary}"
    rounded: "{rounded.md}"
    padding: "12px 6px"
  seg-button-active:
    backgroundColor: "{colors.signal}"
    textColor: "{colors.on-signal}"
    rounded: "{rounded.md}"
    padding: "12px 6px"
  card:
    backgroundColor: "{colors.card}"
    textColor: "{colors.text-primary}"
    rounded: "{rounded.lg}"
    padding: "14px"
  stat-cell:
    backgroundColor: "{colors.card}"
    textColor: "{colors.text-primary}"
    rounded: "{rounded.sm}"
    padding: "10px 0px"
---

## Overview

NitroLite'ın görsel dünyası **Departure Board**: bir tren istasyonundaki kalkış tahtası, loş bir odada okunurken. İki mürekkep — koyu zemin ve kırık beyaz metin — artı tek bir amber sinyal rengi. Dünya yalnızca tipografi, palet, yoğunluk ve tek imza hareketi verir; yerleşim, gezinme ve kontrol davranışı tanıdık Windows kalır.

İmza hareket: **aktif satır tam zemin bandı olarak döner.** Kenar çizgisi, sol şerit, parlama veya gradyan yok — satırın tamamı amber dolguya geçer ve tipografisi koyuya döner. Bir ekranda aynı anda tek bir amber band vardır.

Yüzey modu **Operate**: kullanıcı bir görevi bitirir. Süs değil, tarama hızı ve tutarlılık önemlidir. Marka, hassas detaylarda yaşar: hizalama, tek sinyal rengi, dürüst boş durumlar.

## Colors

- **bg-dark `#0E1216`** — pencere zemini. Her şeyin altındaki tek katman.
- **rail `#0A0D10`** — başlık çubuğu ve kenar çubuğu. Zeminden bir tık koyu, böylece gezinme "çerçeve" gibi okunur.
- **panel `#11161A`** — iç içe paneller, segment düğmelerinin boş durumu.
- **card `#151A1F`** — içerik kartları. Zeminden ayrımı gölge değil, 1px `border` çizgisi sağlar.
- **card-hover `#1C2228`** — yalnızca fare üzerindeyken.
- **border `#252D35`** — varsayılan 1px çizgi. **border-strong `#3A444E`** — odak ve hover.
- **signal `#F0B429`** — amber. Uygulamada **tek** vurgu rengi: aktif mod satırı, aktif gezinme öğesi, seçili segment, birincil düğme, grafik çizgisi, ölçüm çubukları.
- **on-signal `#0E1216`** — amber zemin üzerindeki metin. Saf siyah değil; zeminle aynı mürekkep.
- **danger `#E85C4A`** — yalnızca gerçekten yanlış olan durumlar: 85 °C üstü sıcaklık, silme eylemleri, engellenmiş mod.
- **warn-bg `#2A1A16`** — yıkıcı düğmelerin sakin zemini; kırmızı metinle birlikte.
- **ok `#3FA96B`** — onaylanmış durum çipleri ("Config 1 (Aktif)").
- **info `#5B9BD5`** — yalnızca pil gerilimi gibi nötr bir üçüncü ölçüm gerektiğinde.
- **text-primary `#F4F1E8`** — kırık beyaz, saf beyaz değil. **text-secondary `#A3ADB7`** — açıklamalar. **text-muted `#7C8791`** — mikro etiketler ve "SEÇ" ipuçları.

Renk asla tek başına anlam taşımaz: her durum çipi metin de yazar ("AKTİF", "SEÇ", "Devre dışı").

## Typography

Tek aile: **Segoe UI Variable Text**, yedek Segoe UI. Özel font yüklenmez — hafiflik kısıtı.

Ölçek dar ve bilinçli olarak küçük: bu bir kontrol paneli, okunacak makale değil.

| Rol | Boyut | Ağırlık | Kullanım |
|---|---|---|---|
| display | 20px | SemiBold | Sekme başlığı, tek bir tane |
| title | 15px | SemiBold | Mod satırı adı, kart başlığı |
| body | 12px | Regular | Açıklama paragrafı |
| label | 11px | Regular / SemiBold | Alan etiketi, düğme metni, durum çipi |
| micro | 10px | Bold | Ölçüm etiketi, "SEÇ" |

Rakamlar hizalı okunur: ölçüm değerleri Bold ve sağa yaslı, etiketler solda. Bir satırda etiket + değer çifti her zaman aynı baseline'da.

## Layout

- Pencere: 1060×730 varsayılan, 2560×1380'e kadar genişler. Kenar çubuğu sabit **210px**, içerik `*`.
- İçerik boşluğu **20px**; kart arası **10–16px**.
- Kart içi dolgu **14px**. Satır dolgusu **14px 11px**.
- Dikey ritim 4'ün katları: 4 / 8 / 12 / 16 / 20.
- **Kart ızgarası yok.** Aynı boy dört kutu yerine tek bir okuma şeridi: üstte 1px çizgi, altında etiket/değer çiftleri. Ölçümler satır satır dizilir, kutu kutu değil.
- Geniş ekranda satırlar uzar; bu doğru — tahta geniştir. İçerik ortalanmaz, sola yaslanır.
- Form alanları (ComboBox) içeriğe göre sınırlanır (`MaxWidth`), satırı doldurmaz.

## Elevation & Depth

**Gölge yok.** Hiyerarşi üç şeyle kurulur: zemin tonu (rail → bg → panel → card), 1px çizgi ve boşluk.

- `DropShadowEffect` kullanılmaz; hiçbir yerde. Pencere kenarını Windows çizer.
- Bulanıklık, cam efekti, `AllowsTransparency` yok — WPF GPU üzerinde çizsin.
- Katman sayısı düşük tutulur: her ek kutu boşta tüketim demektir.

## Shapes

- Köşe yarıçapı **2 / 3 / 4px**. Daha büyüğü "kart" hissi verir, bu dünyada istenmez.
- 1px çizgi her yerde aynı kalınlıkta. 2px yalnızca grafik çizgisinde.
- Ölçüm çubukları **3–5px** yüksekliğinde, yuvarlatılmış uçlu.
- Onay kutuları ve radyo düğmeleri **14×14px kare**, 2px yarıçap. Yuvarlak değil — tahta dilinde kare.
- Slider: 3px ray, **10×14px kare** tutamak.

## Components

**Mode row** — tam genişlik satır, altında 1px ayırıcı. Sol: ad (title) + açıklama (label, %72 opaklık). Sağ: durum çipi. Aktifken satırın tamamı `signal` dolguya geçer, metin `on-signal` olur, çip "AKTİF" yazar. Hover, dolgunun **altında** duran ayrı bir katmandır; aktif bandı asla yıkamaz.

**Nav item** — aynı dil, daha küçük: 12px 9px dolgu, 3px yarıçap. Aktif olan amber band + koyu metin.

**Seg button** — fan modu ve CPU boost gibi 2–4 seçenekli gruplar. Boş durum `panel` zemin + `text-primary`; seçili olan amber band + koyu metin. Boş durumdaki renk ipucu (kırmızı "Maksimum", amber "Özel") korunur; seçilince yerini sinyal rengine bırakır.

**Card** — `card` zemin, 1px `border`, 4px yarıçap, 14px dolgu. Başlık title, altında label açıklama.

**Stat cell** — kart değil: üstte 1px çizgi, altında micro etiket + büyük değer + not. Dört tanesi yan yana bir okuma şeridi oluşturur.

**Checkbox / Radio** — 14px kare, `border-strong` çerçeve; işaretliyken amber dolgu + koyu tik. Metin 7px sağda.

**Slider** — 3px ray (`panel` zemin, `border` çerçeve), dolu kısım amber, kare tutamak `text-primary`.

**Toast** — `card` zemin, `border-strong` çerçeve, 4px yarıçap. Gölge yok.

## Do's and Don'ts

**Yap**

- Bir ekranda tek amber band. Aynı anda iki "aktif" satır gösterme.
- Durumu metinle de yaz: "AKTİF", "SEÇ", "Devre dışı", "Uyanıyor…".
- Okunamayan değeri `--` göster; tahmin etme.
- Hiyerarşiyi zemin tonu + 1px çizgi + boşlukla kur.
- Yeni bir ölçüm eklerken mevcut okuma şeridine satır olarak ekle, yeni kart açma.
- Türkçe yaz; kod tanımlayıcıları İngilizce kalsın.

**Yapma**

- Gölge, gradyan, bulanıklık, cam efekti, parlama.
- Başlığın üstüne küçük "kicker" etiketi.
- Kartın üstüne veya yanına renkli 1px şerit.
- Gradyan metin.
- Aynı boy kartlardan oluşan ızgara (dört kutu, altı kutu…).
- Oyuncu RGB'si: neon, çok renkli vurgular, animasyonlu geçişler.
- Sürekli animasyon veya saniyede birden fazla kare tüketen efekt. Tek hareket: 200ms toast fade.
- Emoji.
