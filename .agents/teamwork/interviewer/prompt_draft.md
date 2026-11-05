NitroLite’ın kalan geri yükleme, süreç yönetimi ve eşzamanlılık sorunlarını uçtan uca, mevcut çalışan mantığı koruyarak ve üretim kalitesinde çöz.

Working directory: C:\Users\caner\Documents\antigravity\eager-shannon
Integrity mode: development

## Requirements

### R1. Oyun Profili Uygulama ve Masaüstüne Dönüşün Tutarlı Hale Getirilmesi
Mevcut `_gameProfileTransitionLock`, oturum nesli ve `operation.Task.Unwrap()` yapısını koruyarak bütün profil geçişinin tamamlanmasını bekle. Aktif oyun bilgisi, özgün masaüstü ayarları ve bekleyen geri yükleme adımları birbirinden ayrı anlam taşımalıdır.
- Masaüstü referansını ilk oyun değişikliğinden önce kaydet; önceki geri yükleme bekliyorsa veya yarıda kesildiyse yeni oyun bu referansı ezmemelidir.
- Fan dahil gerçekten değiştirilen ayarların önceki durumunu koru; kullanıcı oyun öncesinde maksimum fan seçmişse geri dönüşte koşulsuz `Auto` uygulama.
- Donanım değişikliğini yalnızca başarıdan sonra arayüze yansıt; başarısız veya sonucu belirsiz yazmaları başarılı kabul etme.
- Oyun kapandığında aktif oyun ve pil izleme bayraklarını kapat; başarısız geri yükleme adımlarını ve özgün hedeflerini koru.
- İşlem bir sonraki adıma ulaşmadan kesildiğinde henüz denenmemiş adımların kaybolmasını önle.
- Yeni oyun başladığında eski akışın o anda başlamış donanım işlemini güvenli şekilde tamamlayıp sonraki komutu göndermeden kilidi devretmesini sağla.
- Fareye ait önceki bekleyen dönüş kaydını, yeni oyunda fare profili bulunmaması veya yeni fare komutunun başarısız olması durumunda silme.
- Başarısız geri yüklemeyi genel başarı bildirimiyle sonuçlandırma; bağımsız adımların ayrı ayrı denenebilmesini ve başarısız kalanların açıkça bildirilmesini sağla.
- `RetryPendingDesktopRestoreAsync()` metodunu tepsi menüsündeki "Masaüstü ayarlarını yeniden dene" eylemine bağla. Eylemi yalnızca bekleyen iş varsa kullanılabilir yap. Yeniden deneme sırasında aynı geçiş kilidini kullan, aktif oyun ve oturum neslini uygun noktalarda yeniden kontrol et; yinelenen donanım yazmalarını engelle.
- Manuel profil uygulaması, canlı profil kaydetme uygulaması ve güç kaynağı otomasyonunun geri yükleme akışıyla sıralamasını belirle ve uygula; son kullanıcı talebinin eski bir işlem tarafından ezilmesini önle.

### R2. `powercfg` Süreç Sahipliği ve Zaman Aşımının Yönetimi
`CpuPowerService` içindeki yazma ve sorgu yollarında her süreç nesnesinin tek bir sahibi olmasını sağla; takibe devredilen süreç yerel `using` veya `finally` blokları tarafından kapatılmamalıdır.
- Süre aşımında sonlandırmayı dene; çıkış doğrulanmadıysa süreci izlemeyi sürdür ve yeni güç yazmalarını engelle.
- Süreç gerçekten çıktığında kaynağını bırak, takip kaydını temizle ve sonraki işlemlerin yeniden çalışabilmesini sağla.
- Sonlandırma, durum sorgulama ve çıktı okuma hatalarını açık sonuçlara dönüştür; belirsiz süreç durumunda yeni yazmaya geçme.
- Her komut yalnızca süreç tamamlandığında ve çıkış kodu sıfır olduğunda başarılı sayılsın.
- Zincirde bir yazma başarısızsa kalan yazmaları durdur; kısmi değişiklik ihtimalini sorgulayarak değerlendir.
- AC ve DC hedeflerini koru; tek bir `CurrentMode` gösteriliyorsa aktif güç kaynağının doğrulanmış değerini kullan, sorgu başarısızlığını doğrulanmış varsayılan değer gibi sunma.
- Çıktı okuma ve süreç bekleme yollarının tamamına kesin süre sınırları uygula.

### R3. Debloat İşleminin Doğrulanmış Geri Yükleme Olarak Tamamlanması
Yedekteki her servisin başlangıç türünü ve kaydedilmiş çalışma durumunu (`Running` / `Stopped`) geri yükle ve doğrula.
- Başlangıç türü ile çalışma durumunun farklı olduğunu dikkate alarak doğru işlem sırasını belirle; geri getirilemeyen durum için başarı bildirme.
- Servis bulunamaması, sorgulanamaması, durdurulamaması, başlatılamaması veya son durumun uyuşmaması durumlarını hata olarak topla; diğer bağımsız kayıtları denemeye devam et.
- Zamanlanmış görevin kaydedilmiş etkin veya devre dışı durumunu doğrula; özgün durum bilinmiyorsa görevi değiştirme.
- Yedeği yalnızca gerekli bütün geri yüklemeler doğrulandığında sil; kısmi başarıda özgün yedeği koru ve ikinci denemenin kısmen değiştirilmiş sistemden yeni başlangıç yedeği üretmesini engelle.
- Eski sürümlerin yedeklerindeki eksik veya bozuk kayıtları sessiz varsayımlarla geri yükleyip yedeği silme.
- Yönetici PowerShell sürecinin yaşam döngüsünü koru: 20 saniyelik zaman aşımı sonrasında dahi süreç çıkana kadar yeni Uygula/Geri Yükle işlemini engelle; kullanıcıya durumun sürdüğünü veya belirsiz olduğunu bildir; süreç sonlandığında yeniden denemeye izin ver.

### R4. Gerçek Uygulama Akışına Bağlı ve Güvenli Test Doğrulaması
Testlerin ayrık kurgular yerine doğrudan üretim kodunu çağırmasını sağla. Gerekli en küçük test bağlantılarını (test seams) kullanarak uçtan uca akışları doğrula.
- A oyunu donanım komutu beklerken A kapanıp B açıldığında yazmaların doğru sırayla başlamasını ve ilk masaüstü referansının korunduğunu doğrula.
- Geri yüklemede bir adım başarısız olduğunda diğerlerinin denendiğini ve tepsi eyleminden yeniden denemenin yalnızca gereken işleri tamamladığını doğrula.
- Yeniden deneme sırasında yeni oyun başladığında eski geri yüklemenin yeni profilin üzerine yazmadığını doğrula.
- Fare dönüşü başarısız olduğunda, sonraki oyunda fare profili olmasa bile önceki dönüş kaydının korunduğunu doğrula.
- `powercfg` hata kodu verdiğinde, zaman aşımına uğradığında veya sonlandırma sonrası çalışmaya devam ettiğinde yeni yazmaların engellendiğini ve süreç çıktıktan sonra sistemin toparlandığını doğrula; sorgu yolunda takibe devredilen sürecin erken `Dispose` edilmediğini doğrula.
- Debloat yedeğinde `Stopped`, sahte sistemde `Running` olduğunda durdurma başarısızsa hata verilip yedeğin korunduğunu, başarılıysa son durumun doğrulandığını doğrula; eksik servis/görev durumunda yedeğin korunduğunu ve hataların raporlandığını doğrula.
- Debloat süreci zaman aşımına uğradığında ilk süreç çıkmadan ikinci işlemin başlamadığını ve kısmi geri yükleme sonrası ikinci denemenin aynı özgün yedeği kullandığını doğrula.
- Güvenlik kısıtı: Gerçek fare profili, güç planı, Windows servisi veya zamanlanmış görev ayarlarını değiştiren testleri bu doğrulamada çalıştırma; gerçek `CpuPowerService` oluşturup `SetBoostModeAsync` çağıran eşzamanlılık testi dahil olmak üzere tüm testleri kontrollü sahte süreç/donanım katmanıyla güvenli hale getir.

## Verification Resources

- `NitroLite.Tests` test projesi ve `dotnet test` (gerçek donanıma/servislere dokunmayan kontrollü sahte katmanlarla).

## Acceptance Criteria

### Derleme ve Güvenli Test Doğrulaması
- [ ] Uygulama ve test projeleri Release yapılandırmasında 0 hata ve 0 uyarı ile derlenir (`dotnet build -c Release`).
- [ ] Testler gerçek fare profili, Windows güç planı, Windows servisi veya zamanlanmış görev ayarlarını değiştirmeden kontrollü sahte/taklit (mock/fake) katmanlarıyla çalıştırılır.
- [ ] `NitroLite.Tests` içerisindeki tüm ilgili birim ve entegrasyon testleri başarıyla geçer (`dotnet test`).

### Oyun Profili ve Masaüstü Geri Yükleme
- [ ] İlk masaüstü referansı ilk oyun öncesi kaydedilir ve yarım kalan/bekleyen adımlar yeni oyun geçişinde ezilmez.
- [ ] Fan ayarı dahil yalnızca değiştirilen donanım durumları geri yüklenir; kullanıcı oyun öncesi özel/maksimum fan seçtiyse geri dönüşte `Auto` zorlanmaz.
- [ ] Donanım değişiklikleri yalnızca başarılı yazma sonrasında arayüze yansıtılır.
- [ ] Oyun kapandığında aktif oyun ve pil izleme bayrakları kapatılır, başarısız adımlar hedefleriyle korunur ve bağımsız adımlar ayrı denenir.
- [ ] Yeni oyun başladığında önceki donanım işlemi güvenle tamamlanıp kilidi devreder.
- [ ] Fare dönüş kaydı yeni oyunda fare profili olmasa veya yazma başarısız olsa dahi silinmez.
- [ ] Tepsi menüsünde "Masaüstü ayarlarını yeniden dene" eylemi yalnızca bekleyen geri yükleme varsa etkinleştirilir ve yinelenen tıklamalarda çakışan yazma başlatmaz.
- [ ] Manuel profil, canlı uygulama ve güç otomasyonu yazmaları geri yükleme akışıyla doğru sıralanır; son kullanıcı talebi eski işlemler tarafından ezilmez.

### `powercfg` Süreç Yönetimi
- [ ] Süreç nesneleri tek bir sahiplik modeline sahiptir; takibe devredilen süreç yerel `using`/`finally` blokları tarafından erken `Dispose` edilmez.
- [ ] Zaman aşımına uğrayan süreçler için sonlandırma doğrulanana kadar yeni güç yazmaları engellenir.
- [ ] Süreç çıkışı doğrulanıp çıkış kodu 0 olduğunda işlem başarılı sayılır; hata durumunda kaynaklar temizlenir ve zincirdeki kalan yazmalar durdurulur.
- [ ] Tüm süreç bekleme ve çıktı okuma yolları katı zaman aşımına tabidir.

### Debloat Doğrulaması ve Süreç Koruması
- [ ] Servislerin hem başlangıç türü hem de çalışma durumu (`Running` / `Stopped`) doğrulanarak geri yüklenir; durum uyuşmazlıkları hata olarak toplanır.
- [ ] Zamanlanmış görevlerin özgün durumu doğrulanır, bilinmeyen görevler değiştirilmez.
- [ ] Yedek dosyası yalnızca tüm adımlar başarıyla doğrulandığında silinir; kısmi başarısızlıkta özgün yedek korunur.
- [ ] PowerShell işlemi zaman aşımına uğradığında süreç çıkana kadar yeni Uygula/Geri Yükle işlemi engellenir.

### Raporlama ve İnceleme
- [ ] Normal giriş noktalarına bağlı olmayan metot, kontrol edilmeyen sonuç, erken bırakılan kilit, erken kapatılan kaynak ve yanlış başarı bildirimi kalmadığı kod incelemesiyle teyit edilir.
- [ ] Son raporda değiştirilen dosyalar, çalıştırılan testler, sonuçlar ve varsa doğrulanamayan davranışlar açıkça belirtilir.
