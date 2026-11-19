# ==============================================================================
# MISTRAL VIBE CLI - İKİ AŞAMALI GÜVENLİ OTONOM KOD TEMİZLİK DÖNGÜSÜ
# ==============================================================================

$ErrorActionPreference = "Stop"

# --- KULLANICI YAPILANDIRMASI (PROJENE GÖRE DÜZENLE) ---
$BuildAndTestCmd = "dotnet test --nologo" # Örn: "npm run build; npm test" veya "dotnet test"
$MaxLoopIterations = 120                  # Sonsuz döngü emniyet sigortası (Max tur)
$CooldownSeconds = 10                     # API rate-limit koruması için turlar arası bekleme
$TrackerFile = "CLEANUP_TRACKER.md"
$LogFile = "CLEANUP_AUDIT_LOG.md"

# Ajanın ASLA otomatik silme/değiştirme yapmayacağı hassas yollar (Sadece raporlar)
$DoNotTouchPaths = @(
    "src/Hardware/Drivers",
    "src/Core/Safety",
    "profiles.json"
) -join ", "

# --- 0. GÜVENLİK AĞI: GIT KONTROLÜ VE İZOLE DAL OLUŞTURMA ---
Write-Host "[0/3] Git güvenlik kontrolü yapılıyor..." -ForegroundColor Cyan

if (-not (Test-Path ".git")) {
    Write-Error "Bu klasörde Git deposu bulunamadı! Güvenlik için önce 'git init' yapın."
    exit 1
}

$gitStatus = git status --porcelain
if ($gitStatus) {
    Write-Error "Çalışma dizininde kaydedilmemiş (uncommitted) değişiklikler var! Önce mevcut işlerinizi commit edin veya stash'leyin."
    exit 1
}

$BranchName = "cleanup/vibe-autonomous-$(Get-Date -Format 'yyyyMMdd-HHmm')"
git checkout -b $BranchName | Out-Null
Write-Host "Güvenli çalışma dalı oluşturuldu: $BranchName" -ForegroundColor Green

# İlk doğrulama: Başlangıçta projenin sağlam olduğundan emin ol
Write-Host "Başlangıç build ve testleri doğrulanıyor..." -ForegroundColor Cyan
Invoke-Expression $BuildAndTestCmd
if ($LASTEXITCODE -ne 0) {
    Write-Error "Başlangıç testleri zaten başarısız! Temizliğe başlamadan önce mevcut hataları düzeltin."
    exit 1
}

# ==============================================================================
# AŞAMA 1: SALT OKUNUR HARİTALAMA (HİÇBİR KODU DEĞİŞTİRMEDEN ADAY LİSTESİ ÇIKARMA)
# ==============================================================================
if (-not (Test-Path $TrackerFile)) {
    Write-Host "`n[AŞAMA 1] Proje taranıyor ve $TrackerFile oluşturuluyor (Salt Okunur)..." -ForegroundColor Yellow

    $Phase1Prompt = @"
SEN BİR KIDEMLİ YAZILIM DENETÇİSİSİN.
GÖREV: Bu projedeki tüm kaynak kod klasörlerini ve dosyalarını tara. HİÇBİR KAYNAK KOD DOSYASINI DEĞİŞTİRME VEYA SİLME.
Sadece proje kök dizininde '$TrackerFile' adlı bir Markdown dosyası oluştur.

KURALLAR:
1. Projedeki tüm modülleri/klasörleri mantıksal gruplara (her grupta 2-4 dosya olacak şekilde) böl.
2. '$TrackerFile' içine her grup için şu formatta bir kontrol listesi yaz:
   - [ ] Modül/Grup Adı | Dosyalar: path/to/file1, path/to/file2
3. Şu dokunulmaz yolları listeye '[DO-NOT-TOUCH - SADECE RAPORLA]' etiketiyle ekle: $DoNotTouchPaths
4. Başka hiçbir dosya oluşturma veya düzenleme. Listeyi yaz ve bitir.
"@

    vibe -p $Phase1Prompt --auto-approve
    
    # Güvenlik kontrolü: Aşama 1'de yanlışlıkla kod değiştirildiyse geri al
    git checkout -- . 2>$null
    
    if (-not (Test-Path $TrackerFile)) {
        Write-Error "Aşama 1 başarısız: $TrackerFile oluşturulamadı."
        exit 1
    }

    git add $TrackerFile
    git commit -m "chore(audit): initialize autonomous cleanup tracker" | Out-Null
    Write-Host "Aşama 1 tamamlandı! $TrackerFile oluşturuldu ve commit edildi." -ForegroundColor Green
}

# ==============================================================================
# AŞAMA 2: ATOMİK TEMİZLİK, TEST DOĞRULAMA VE OTOMATİK ROLLBACK DÖNGÜSÜ
# ==============================================================================
Write-Host "`n[AŞAMA 2] Sabaha kadar sürecek güvenli otonom temizlik döngüsü başlıyor..." -ForegroundColor Yellow

if (-not (Test-Path $LogFile)) {
    "# Otonom Kod Temizlik ve Hata Denetim Raporu`n`nBaşlangıç: $(Get-Date)`n" | Out-File -FilePath $LogFile -Encoding utf8
    git add $LogFile
    git commit -m "chore(audit): initialize audit log" | Out-Null
}

for ($i = 1; $i -le $MaxLoopIterations; $i++) {
    $trackerContent = Get-Content $TrackerFile -Raw

    # Tamamlanmamış madde (- [ ]) kalıp kalmadığını kontrol et
    if ($trackerContent -notmatch "- \[ \]") {
        Write-Host "`nTÜM MADDELER TAMAMLANDI! Temizlik döngüsü başarıyla sonlandı." -ForegroundColor Green
        break
    }

    Write-Host "`n------------------------------------------------------------------" -ForegroundColor DarkGray
    Write-Host "[Tur $i / $MaxLoopIterations] Yeni taze oturum başlatılıyor ($(Get-Date -Format 'HH:mm:ss'))..." -ForegroundColor Cyan

    $Phase2Prompt = @"
SEN HASSAS SİSTEMLERDE UZMAN BİR KOD TEMİZLİK VE HATA AYIKLAMA AJANISIN.
BAĞLAM: '$TrackerFile' dosyasını oku ve henüz '- [ ]' olarak işaretlenmiş İLK TEK MADDEYİ seç. Sadece o maddedeki dosyalara odaklan.

KESİN GÜVENLİK KURALLARI:
1. DOKUNULMAZ ALANLAR: Eğer madde '[DO-NOT-TOUCH]' içeriyorsa veya şu yollardaysa ($DoNotTouchPaths), HİÇBİR KODU DEĞİŞTİRME; sadece bulduğun hataları '$LogFile' dosyasına yaz ve maddeyi '- [x]' yap.
2. YALNIZCA KESİN ÖLÜ VE HATALI KODU TEMİZLE:
   - Hiçbir yerden çağrılmayan private metotları, gereksiz import/using satırlarını, erişilemeyen (unreachable) kod bloklarını, boş catch bloklarını ve bariz null/kaynak sızıntısı hatalarını düzelt.
   - DİKKAT: Reflection, XAML/UI Data Binding, JSON Serialization, Event Handler veya harici API tarafından dinamik çağrılma ihtimali olan public/internal metotlara ASLA dokunma! Şüpheye düşersen SİLME, sadece '$LogFile' dosyasına 'Şüpheli - İncelenmeli' olarak not düş.
3. DOĞRULAMA ZORUNLULUĞU:
   - Değişiklik yaptıktan sonra terminalde şu komutu çalıştır: $BuildAndTestCmd
   - Eğer build uyarı/hata verirse veya tek bir test bile başarısız olursa, yaptığın kod değişikliğini derhal geri al ('git checkout -- <dosya>'), durumu '$LogFile' dosyasına yaz ve maddeyi '- [x] (ATLANDI - Test hatası)' olarak işaretle.
4. BİTİŞ ADIMI:
   - Tüm testler başarılıysa '$TrackerFile' içinde sadece bu maddeyi '- [x]' yap.
   - Ne sildiğini/düzelttiğini 2-3 satırla '$LogFile' dosyasının sonuna ekle.
   - 'git push' veya 'git reset' KULLANMA. Tek bir maddeyi bitir ve hemen oturumu sonlandır.
"@

    # Ajanı taze bağlamla çalıştır
    vibe -p $Phase2Prompt --auto-approve

    # --- DIŞ GÜVENLİK KİLİDİ (POWERSHELL SEVİYESİNDE GARANTİ) ---
    # Ajan testleri çalıştırmayı unutsa veya yanlış raporlasa bile PowerShell bizzat kontrol eder!
    Write-Host "[Güvenlik Kilidi] Build ve testler doğrulanıyor..." -ForegroundColor DarkCyan
    Invoke-Expression $BuildAndTestCmd | Out-Null

    if ($LASTEXITCODE -ne 0) {
        Write-Host "[UYARI] Ajanın yaptığı değişiklik build/test kırdı! Otomatik geri alınıyor (Rollback)..." -ForegroundColor Red
        
        # Sadece kaynak kod değişikliklerini geri al, tracker ve log dosyalarını koru
        git checkout -- . ':!CLEANUP_TRACKER.md' ':!CLEANUP_AUDIT_LOG.md'
        
        # Takılı kalmamak için ilk açık kutucuğu ATLANDI olarak işaretle
        $raw = Get-Content $TrackerFile -Raw
        $regex = [regex]"- \[ \]"
        $updated = $regex.Replace($raw, "- [x] [OTOMATİK ROLLBACK - TEST KIRILDI]", 1)
        Set-Content -Path $TrackerFile -Value $updated -Encoding utf8
        
        "`n- **[Tur $i - $(Get-Date -Format 'HH:mm')]** Otomatik Rollback uygulandı (Build/Test başarısız oldu)." | Out-File -FilePath $LogFile -Append -Encoding utf8
    }

    # Değişiklik varsa atomik commit at
    $changes = git status --porcelain
    if ($changes) {
        git add -A
        git commit -m "chore(autonomous-cleanup): step $i verified and tested" | Out-Null
        Write-Host "[Tur $i] Değişiklikler doğrulandı ve atomik commit atıldı." -ForegroundColor Green
    } else {
        Write-Host "[Tur $i] Değişiklik yapılmadı, sonraki tura geçiliyor." -ForegroundColor Gray
    }

    Start-Sleep -Seconds $CooldownSeconds
}

Write-Host "`nİşlem tamamlandı. Detaylı raporu '$LogFile' içinde ve değişiklikleri 'git log -p' ile inceleyebilirsin." -ForegroundColor Cyan