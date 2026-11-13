---
name: NitroLite
description: Acer Nitro dizüstüler için hafif NitroSense alternatifi — Kandilli sismograf kağıdı görsel dünyası
colors:
  paper: "#F2EDE3"
  paper-deep: "#E8E0D2"
  card: "#F8F4EB"
  card-hover: "#EDE6D8"
  rail: "#EAE3D5"
  rule: "#D5CBB8"
  rule-strong: "#B8AB93"
  ink: "#1B1917"
  ink-soft: "#4A4238"
  ink-faint: "#6E6558"
  vermilion: "#C0392B"
  vermilion-deep: "#9B2C1F"
  olive: "#5F6B3E"
  slate: "#4A6274"
  on-accent: "#F7F2E8"
  warn-bg: "#F0E2D2"
typography:
  display:
    fontFamily: "Segoe UI Variable Text, Segoe UI, sans-serif"
    fontSize: "18px"
    fontWeight: 600
    lineHeight: 1.2
    letterSpacing: "0em"
  title:
    fontFamily: "Segoe UI Variable Text, Segoe UI, sans-serif"
    fontSize: "14px"
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
    fontWeight: 600
    lineHeight: 1.3
    letterSpacing: "0.02em"
  figure:
    fontFamily: "Cascadia Mono, Consolas, Courier New, monospace"
    fontSize: "12px"
    fontWeight: 400
    lineHeight: 1.3
    letterSpacing: "0em"
rounded:
  none: "0px"
  sm: "1px"
  md: "2px"
spacing:
  xs: "4px"
  sm: "8px"
  md: "12px"
  lg: "16px"
  xl: "20px"
components:
  mode-row:
    backgroundColor: "transparent"
    textColor: "{colors.ink-soft}"
    rounded: "{rounded.none}"
    padding: "12px 14px 12px 0px"
  mode-row-active:
    backgroundColor: "transparent"
    textColor: "{colors.ink}"
    rounded: "{rounded.none}"
    padding: "12px 14px 12px 0px"
  nav-item:
    backgroundColor: "transparent"
    textColor: "{colors.ink-soft}"
    rounded: "{rounded.md}"
    padding: "8px 10px"
  nav-item-active:
    backgroundColor: "{colors.paper}"
    textColor: "{colors.ink}"
    rounded: "{rounded.md}"
    padding: "8px 10px"
  seg-button:
    backgroundColor: "transparent"
    textColor: "{colors.ink-soft}"
    rounded: "{rounded.md}"
    padding: "5px 14px"
  seg-button-active:
    backgroundColor: "{colors.paper-deep}"
    textColor: "{colors.ink}"
    rounded: "{rounded.md}"
    padding: "5px 14px"
  section:
    backgroundColor: "transparent"
    textColor: "{colors.ink}"
    rounded: "{rounded.none}"
    padding: "14px 0px 0px 0px"
  stat-cell:
    backgroundColor: "transparent"
    textColor: "{colors.ink}"
    rounded: "{rounded.none}"
    padding: "8px 16px 0px 0px"
  stamp:
    backgroundColor: "transparent"
    textColor: "{colors.vermilion}"
    rounded: "{rounded.sm}"
    padding: "2px 7px"
---

## Overview

NitroLite'ın görsel dünyası **Kandilli sismograf kağıdı**: bir tambur kayıt cihazının üzerine sardığı kağıt. Sıcak krem stok bütün yüzeyi kaplar, gövdeyi tek bir mürekkep taşır, tek bir vermilyon "şimdi"yi ve gerçekten dikkat isteyen şeyi işaretler. Dünya yalnızca zemin, çizgi, tipografi ve tek imza hareketi verir; yerleşim, gezinme ve kontrol davranışı tanıdık Windows kalır.

İmza hareket: **kayıt şeridi.** Her ekranın üstünde son 60 saniyenin sürekli mürekkep izi var; sağ kenarda kesikli vermilyon bir "şimdi" çizgisi, izin ucunda vermilyon bir nokta. Kalem mürekkeptir, damga vermilyondur — bu ayrım dünyanın tamamını yönetir.

Yüzey modu **Operate**: kullanıcı bir görevi bitirir. Süs değil, tarama hızı ve tutarlılık önemlidir. Marka, hassas detaylarda yaşar: hizalama, tek vurgu rengi, dürüst boş durumlar, ölçümün mürekkeple çizilmesi.

## Colors

- **paper `#F2EDE3`** — pencere zemini. Her şeyin altındaki tek katman; gün boyu açık kalan bir ekran için göz yormayan sıcak krem.
- **paper-deep `#E8E0D2`** — kayıt şeridinin ve grafiğin zemini, seçili segment düğmesinin dolgusu. Kağıdın bir ton koyusu.
- **card `#F8F4EB`** — form alanları ve açılır liste zemini. Kağıttan bir tık açık.
- **card-hover `#EDE6D8`** — yalnızca fare üzerindeyken.
- **rail `#EAE3D5`** — kenar çubuğu. Zeminden bir tık koyu, böylece gezinme "çerçeve" gibi okunur.
- **rule `#D5CBB8`** — varsayılan 1px çizgi. **rule-strong `#B8AB93`** — odak, hover, ölçüm çubuğu rayı.
- **ink `#1B1917`** — gövde metni, ölçüm çubuklarının dolgusu, grafik izi, işaretli kutu. Saf siyah değil; kağıda basılmış mürekkep.
- **ink-soft `#4A4238`** — açıklamalar, ikincil değerler. **ink-faint `#6E6558`** — mikro etiketler ve "SEÇ" ipuçları.
- **vermilion `#C0392B`** — dünyanın tek vurgusu ve **yalnızca üç iş için**: (1) şu an canlı olan şey — başlık çubuğundaki mod, grafiğin uç noktası, "şimdi" çizgisi; (2) dikkat isteyen durum — eşik aşımı, bakım önerisi, engellenmiş mod; (3) yıkıcı eylem metni. Başka hiçbir yerde kullanılmaz.
- **vermilion-deep `#9B2C1F`** — yıkıcı düğme metni ve uyarı başlığı. Parlak vermilyondan bir tık koyu, düğme metni olarak daha az bağırır.
- **olive `#5F6B3E`** — doğrulanmış iyi durum: pil sağlığı, SSD sıcaklığı, nominal kararlılık.
- **slate `#4A6274`** — nötr üçüncü ölçüm (pil gerilimi, GPU modu rozeti).
- **on-accent `#F7F2E8`** — mürekkep dolgu üzerindeki metin ve tik.
- **warn-bg `#F0E2D2`** — uyarı bandının zemini; vermilyon metinle birlikte.

Renk asla tek başına anlam taşımaz: her durum çipi metin de yazar ("AKTİF", "SEÇ", "Devre dışı", "Bakım Önerilebilir").

**Ölçüm çubukları renk taşımaz.** Ray `rule`, dolgu `ink`. Bir çubuk ölçümü gösterir, yargıyı değil; yargı yanındaki metinde ve rozette durur. Tek istisna %85 üzeri disk doluluğu ve çalışan deşarj işlemi — bunlar gerçek eşik ve gerçek canlı durum.

## Typography

İki aile, kesin iş bölümü: **kaydedilen rakamlar** Cascadia Mono (yedek Consolas, Courier New), **yazılan metin** Segoe UI Variable Text. Özel font yüklenmez — hafiflik kısıtı.

| Rol | Boyut | Ağırlık | Aile | Kullanım |
|---|---|---|---|---|
| display | 18px | SemiBold | Segoe UI | Sekme başlığı, her ekranda bir tane |
| title | 14px | SemiBold | Segoe UI | Mod satırı adı, bölüm başlığı |
| body | 12px | Regular | Segoe UI | Açıklama paragrafı |
| label | 11px | Regular / SemiBold | Segoe UI | Alan etiketi, düğme metni, durum çipi |
| micro | 10px | SemiBold | Segoe UI | Ölçüm etiketi, "SEÇ", eksen yazısı |
| figure | 12–20px | Regular / SemiBold | Cascadia Mono | Her sayısal okuma, eksen değeri, RPM, °C |

Mono rakamlar sütun halinde hizalanır: bir değer listesi aşağı doğru tarandığında basamaklar üst üste gelir. Etiket solda, değer sağda, ikisi aynı baseline'da.

WPF'te harf aralığı ayarı yok; mikro etiketlerin ferahlığı büyük harf ve `ink-faint` ile kurulur, `letter-spacing` ile değil.

## Layout

- Pencere: 1060×730 varsayılan, 2560×1380'e kadar genişler. Kenar çubuğu sabit **200px**, içerik `*`.
- İçerik boşluğu **26px** yatay, **20px** dikey. Bölüm arası **14–18px**.
- Dikey ritim 4'ün katları: 4 / 8 / 12 / 16 / 20.
- **Kart ızgarası yok.** Bölüm, kağıt üzerinde çizgiyle ayrılmış bir banttır: üstte 1px `rule`, altında 14px boşluk, sonra içerik. Aynı boy dört kutu yerine tek bir okuma şeridi.
- Ölçümler satır satır dizilir, kutu kutu değil. Dört sütunlu okuma şeridinde her hücrenin üstünde kendi 1px çizgisi vardır; hücreler arasında kutu, gölge veya zemin farkı yoktur.
- Geniş ekranda satırlar uzar; bu doğru — kağıt geniştir. İçerik ortalanmaz, sola yaslanır.
- Form alanları (ComboBox, TextBox) içeriğe göre sınırlanır, satırı doldurmaz.
- Sağa yaslı eylem sütunu her bölümde aynı x'te durur; düğmeler satırın ortasında yüzmez.

## Elevation & Depth

**Gölge yok.** Hiyerarşi üç şeyle kurulur: kağıt tonu (rail → paper → card → paper-deep), 1px çizgi ve boşluk.

- `DropShadowEffect` kullanılmaz; hiçbir yerde. Pencere kenarını Windows çizer.
- Gradyan, bulanıklık, cam efekti, `AllowsTransparency` yok — WPF GPU üzerinde çizsin, boşta tüketim düşük kalsın.
- Katman sayısı düşük tutulur: her ek kutu boşta tüketim demektir.
- Derinlik hissi tek yerden gelir: kayıt şeridi kağıttan bir ton koyu bir banttır ve 1px çizgiyle çevrilidir. Başka hiçbir yüzey "yukarıda" durmaz.

## Shapes

- Köşe yarıçapı **0 / 1 / 2px**. Daha büyüğü "kart" hissi verir, bu dünyada istenmez. Bölümler ve okuma şeritleri tamamen kare.
- 1px çizgi her yerde aynı kalınlıkta. 1.5px yalnızca grafik izinde.
- Ölçüm çubukları **4–6px** yüksekliğinde, yuvarlatılmamış uçlu.
- Onay kutuları ve radyo düğmeleri **14×14px kare**, 1px yarıçap. Yuvarlak değil — kayıt kağıdı dilinde kare.
- Slider: 3px ray, **8×16px** kare tutamak, rayın iki ucunda 4px pay (tutamak kenarda kırpılmaz).
- Mod satırının sol işareti **3×18px** dikdörtgen; gezinme işareti **5×5px** kare.

## Components

**Kayıt şeridi (imza)** — ekranın üstünde 72px yüksekliğinde `paper-deep` bant, 1px `rule` çerçeve. İçinde son 60 saniyenin sürekli `ink` izi (1.4px), sağ kenarda kesikli vermilyon "şimdi" çizgisi, mod değişimlerinde vermilyon dikey tik. Üstünde mikro etiketler: solda "KAYIT · 42 SANİYE", sağda "ARALIK 58–71 °C" ve "ŞİMDİ 64 °C". Veri yokken ortada "Kayıt bekleniyor" yazar, boş bir çizgi çizilmez.

**Telemetri grafiği** — aynı dil, daha büyük (140px). İz `ink`, uçtaki nokta vermilyon. Ölçek **sağda ayrı bir olukta** durur, 1px dikey çizgiyle ayrılmış; etiket asla izin üstüne binmez. Üç yatay kılavuz çizgisi `rule`.

**Mode row** — tam genişlik satır, üstünde 1px `rule`. Sol kenarda 14px'lik işaret kolonu, içinde aktif satırda 3×18px vermilyon dikdörtgen. Sağda durum damgası: pasif satırda `rule` çerçeveli "SEÇ" (`ink-faint`), aktif satırda vermilyon çerçeveli "AKTİF" (vermilyon metin). Aktif satırın metni `ink`, diğerleri `ink-soft`. **Durum dolguyla değil, geri çekilmeyle kurulur** — hiçbir satır renkli banda dönmez.

**Nav item** — aynı dil, daha küçük: 10px 8px dolgu, 2px yarıçap. Aktif olan `paper` zemin alır ve sol margininde 5×5px vermilyon kare belirir. Şerit yok, dolgu yok.

**Seg button** — fan modu ve CPU boost gibi 2–4 seçenekli gruplar. Boş durum saydam zemin + `rule` çerçeve + `ink-soft` metin. Seçili olan `paper-deep` zemin + `ink` çerçeve + `ink` metin + SemiBold + altında 2px `ink` çizgi: kağıda bastırılmış gibi okunur, kırmızıya boyanmaz.

**Section** — `CardBorder` stili: saydam zemin, üstte 1px `rule`, 14px üst dolgu, yarıçap yok. Başlık title, altında label açıklama. 21 kullanım aynı dili miras alır.

**Stat cell** — kart değil: üstte 1px çizgi, altında mikro etiket + mono değer + not. Dört tanesi yan yana bir okuma şeridi oluşturur.

**Checkbox / Radio** — 14px kare, `rule-strong` çerçeve; işaretliyken `ink` dolgu + `on-accent` tik. Metin 7px sağda. Radyo da kare — aynı aileden okunur.

**Slider** — 3px ray (`rule`), dolu kısım `ink`, kare tutamak `ink`.

**Stamp** — vermilyon 1px çerçeve, 1px yarıçap, 2px 7px dolgu, mikro büyük harf. Yalnızca "AKTİF" için. Ekranda tek tane olur.

**Toast** — `card` zemin, `rule-strong` çerçeve, 2px yarıçap, 200ms fade. Gölge yok.

## Do's and Don'ts

**Yap**

- Kalemi mürekkeple, damgayı vermilyonla çiz. Bir ekranda vermilyon en fazla üç yerde görünsün ve her biri "şu an" ya da "dikkat" desin.
- Durumu metinle de yaz: "AKTİF", "SEÇ", "Devre dışı", "Uyanıyor…".
- Okunamayan değeri `--` göster; tahmin etme. Tahmini değeri "~" ve "(tahmini)" ile işaretle.
- Hiyerarşiyi kağıt tonu + 1px çizgi + boşlukla kur.
- Her sayıyı mono ile diz; etiketle değeri aynı baseline'da hizala.
- Yeni bir ölçüm eklerken mevcut okuma şeridine satır olarak ekle, yeni kart açma.
- Türkçe yaz; kod tanımlayıcıları İngilizce kalsın.

**Yapma**

- Gölge, gradyan, bulanıklık, cam efekti, parlama.
- Ölçüm çubuğunu duruma göre renklendirme — çubuk ölçer, rozet yargılar.
- Onay kutusunu, radyoyu, slider dolgusunu veya seçili segmenti vermilyona boyama.
- Başlığın üstüne küçük "kicker" etiketi.
- Kartın üstüne veya yanına renkli 1px şerit.
- İç içe kutu: bölüm içinde bölüm, kart içinde kart.
- Aynı boy kartlardan oluşan ızgara (dört kutu, altı kutu…).
- Grafik eksen etiketini izin üstüne bindirme; ölçek kendi oluğunda durur.
- Oyuncu RGB'si: neon, çok renkli vurgular, animasyonlu geçişler.
- Sürekli animasyon veya saniyede birden fazla kare tüketen efekt. Tek hareket: 200ms toast fade.
- Emoji.
