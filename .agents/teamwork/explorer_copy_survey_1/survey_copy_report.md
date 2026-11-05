# NitroLite Exhaustive User-Facing Copy Survey & De-Slop Specification Report

**Date**: 2026-11-04  
**Author**: `explorer_copy_survey_1` (teamwork_spec_miner)  
**Target Solution**: `NitroLite.sln`  
**Referenced Frameworks**: `stop-slop` skill, Anti-Patterns 3 (Emojis in headings), 16 (Em dashes everywhere), 17 (Generic buzzword copy)

---

## Executive Summary

An exhaustive audit of all user-facing strings across `NitroLite` was conducted covering XAML views (`MainWindow.xaml`, `OsdOverlayWindow.xaml`), code-behind files (`MainWindow.xaml.cs`, `OsdOverlayWindow.xaml.cs`), system tray menus, tooltips, dialog prompts, toast alerts, and background service models (`RgbProfileService.cs`, `ThermalHealthService.cs`, `StorageHealthService.cs`, `TelemetryFormatter.cs`).

The audit identified **four primary copy defects**:
1. **Pervasive Emojis (111 instances)** embedded in navigation buttons, section titles, telemetry cards, drop-down options, dialog messages, and system tray items.
2. **Generic AI Buzzwords & Marketing Fluff** such as *"Akıllı"*, *"Mükemmel"*, *"Harika"*, *"Stüdyo"*, *"Proaktif Bakım Asistanı"*, *"Zengin Temalar"*, *"RAM Yiyor!"*, *"Afterburner"*, *"Tertemiz ve ferah"*, *"Tebrikler!"*.
3. **Conversational, Hand-Holding & Fluffy Prose** explaining obvious system operations like a customer-facing sales brochure instead of a concise, calibrated desktop hardware utility.
4. **Punctuation & Pseudosymbolic Fluff** including em-dashes / pseudo-dashes (`--`, `—`), excessive exclamation marks (`!`), checkmarks (`✓`), and emoji indicators (`🟢`, `⚠️`, `✅`, `🛑`).

Crucially, **three test suites** (`StorageHealthServiceTests.cs`, `ThermalHealthServiceTests.cs`, `TelemetryFormatterTests.cs`) contain assertions bound to specific service strings. The survey documents both direct UI-level decoupling and synchronized backend updates to guarantee zero test regression.

---

## Features Discovered

| # | Category | Feature | Description | Inputs | Outputs | Error Behavior | Discovered Via |
|---|----------|---------|-------------|--------|---------|----------------|----------------|
| 1 | Shell / Chrome | Window Title & Header Brand | Application window title, logo text, version badge, battery header badge, active mode badge | Window lifecycle, battery polling, mode change | Window title text, header text blocks | Defaults to "--%", "BALANCED" on null | `MainWindow.xaml:4, 252-270` |
| 2 | Navigation | Primary Tab Radio Buttons | 5 navigation buttons switching visible panels (Modes, Telemetry, RGB, Battery, Debloat) | User click / keyboard selection | Tab switching, tab visibility toggling | Graceful fallback if tab is null | `MainWindow.xaml:294-299` |
| 3 | Telemetry Sidebar | Quick Hardware Status | Sidebar snapshot showing live CPU temp, GPU temp, and CPU fan RPM | Hardware monitor polling (1s timer) | `TxtSideCpuTemp`, `TxtSideGpuTemp`, `TxtSideCpuFan` | Displays "-- °C" / "-- RPM" | `MainWindow.xaml:303-321` |
| 4 | Power / Fans | Operating Mode Selector | 4 selectable operating presets: Silent, Balanced, Power/Extreme, Turbo | User button click / hotkey / game trigger | ACPI/EC WMI packet dispatch | Catch block displays error toast | `MainWindow.xaml:334-383` |
| 5 | Power / Fans | Fan Speed & Control Buttons | Dual fan tachometer bars with Auto / Max toggle buttons | User click, telemetry polling | Fan RPM readout and progress bars | Catch block displays error toast | `MainWindow.xaml:387-416` |
| 6 | Power / Fans | Manual CPU Boost Control | 3 ACPI power scheme boost modes: Aggressive (5.0 GHz), Efficient (4.2 GHz), Disabled (Base clock) | Button click with tag | PowerWriteACValueIndex / PowerSetActiveScheme | Catch block displays error toast | `MainWindow.xaml:418-433` |
| 7 | Game Detection | Per-Game Profile Studio | Auto-detection and custom profile mapping of game executables to power/fan/boost modes | Process scanning, Steam library parsing | Configured game combo, hot-apply on launch | Handled gracefully with fallback | `MainWindow.xaml:436-529` |
| 8 | Telemetry & OSD | Live Telemetry Cards | Real-time CPU, GPU, RAM, VRAM, Motherboard sensor metrics and bar graphs | 1-second AASSvc & NVML telemetry | Text blocks, progress bars | Displays formatted idle/sleep states | `MainWindow.xaml:535-671` |
| 9 | Memory Management | RAM Cache Cleaner | Working set clearing and Standby list purging with automated or manual trigger | Button click or automatic memory threshold | Process memory trimming, status badge | Catch block displays error toast | `MainWindow.xaml:650-668` |
| 10 | Telemetry & OSD | Rolling History Graph | 60-second real-time telemetry line & polygon chart with metric selector | Metric ComboBox selection, 60s sample buffer | Canvas polyline/polygon, Min/Max/Avg/Current | Clamps within valid bounds | `MainWindow.xaml:673-742` |
| 11 | Overlay | On-Screen Display (OSD) | Click-through topmost overlay HUD positioned at 4 screen corners | Double click / button click, 1s telemetry | Transparent multi-metric HUD | Offscreen clamp prevention | `OsdOverlayWindow.xaml` |
| 12 | Lighting | Keyboard RGB Controls | 4-zone color picker, 9 brightness/speed levels, 8 animation effects, timeout toggle | User selection, AcerAgent TCP packets | LightingConfig, zone previews | Catch block displays error toast | `MainWindow.xaml:747-864` |
| 13 | Lighting | RGB Preset Manager | Preset selection, custom profile saving/deletion, resume persistence | Preset ComboBox, TextBox, Save/Delete buttons | JSON profile persistence, EC re-assertion | Catch block displays error toast | `MainWindow.xaml:774-804` |
| 14 | Battery & Power | %80 Battery Charge Limiter | Background watchdog enforcing 80% charge ceiling in Care Center settings.json | 3-second periodic timer, AC status check | Enforces BatteryHealthy=1 in JSON | Silently handles file locking | `MainWindow.xaml:875-900` |
| 15 | Battery & Power | Controlled Battery Discharge | Controlled CPU background thread load to discharge overcharged battery down to 80% | Button toggle, AC disconnect | 2-worker sine computation loop, sound alarm | Stops automatically at <= 80% | `MainWindow.xaml:902-915` |
| 16 | Battery & Power | Battery Hardware Wear Report | WMI battery chip capacity readouts (Design mWh, Full Charge mWh, Wear %, Voltage) | WMI query to BatteryFullChargedCapacity | Live wear percentage, voltage | Fallback to nominal specs | `MainWindow.xaml:917-965` |
| 17 | Thermal Health | Thermal Interface & Fan Trend | Empirical thermal resistance ratio (°C/W), 14/30-day stability score & degradation slope | Consecutive load sample tracking, linear regression | Health score, status text, slope text | Division-by-zero protection | `MainWindow.xaml:968-1048` |
| 18 | Thermal Health | Sub-Component Temperature Matrix | Physical temperatures for VRM/Motherboard, NVMe SSD 1, NVMe SSD 2, Battery | ACPI, SMART, NVML, WMI sensors | Temperature readouts | Fallback to calibrated baselines | `MainWindow.xaml:1050-1096` |
| 19 | Storage | NVMe SSD SMART & Capacity | Storage reliability counters, partition free/used space, SMART health % | MSFT_StorageReliabilityCounter, DriveInfo | Drive cards, usage bars, health badge | Fallback to Win32_DiskDrive | `MainWindow.xaml:1110-1171` |
| 20 | Debloat & Settings | Tier-1 Telemetry Debloat | Disables 6 non-essential background Acer services and suppresses NitroSense background RAM leak | Button click, PowerShell runas script | Service status list, process killer | Handles execution policy & UAC | `MainWindow.xaml:1187-1238` |
| 21 | Settings | Startup & System Tray Mode | Windows registry autorun and start-minimized configuration | Checkbox toggle | Registry HKCU\Software\Microsoft\Windows\Run | Catch block displays error toast | `MainWindow.xaml:1240-1260` |
| 22 | System Tray | Tray Icon & Context Menu | Taskbar notification area icon with quick mode switching and exit command | Mouse click, right-click menu | System.Windows.Forms.ContextMenuStrip | Restores window on double-click | `MainWindow.xaml.cs:92-130` |
| 23 | Notification | Non-Blocking Toast Banner | Transient toast overlay with animated opacity fade-in/out for user action feedback | ShowToast(string, bool) | ToastBanner WPF border | Auto-dismisses after 3 seconds | `MainWindow.xaml:1280-1289` |

---

## Edge Cases

| # | Feature | Input | Observed Behavior |
|---|---------|-------|-------------------|
| 1 | Thermal Stability Trend | 0 days of historical data | Returns 100.0% stability score, 0.0 slope, and status text: `"Veri toplanıyor (Yeterli gün kaydı yok)"` |
| 2 | Thermal Stability Trend | 1 day of historical data | Bypasses linear regression, evaluates delta vs baseline (1.95 °C/W), returns status: `"Kararlı, Macun kondisyonu mükemmel"` |
| 3 | GPU Sleep State (D3cold) | GPU Temp <= 0 °C or isSleeping | Formatter outputs: `"0 °C (Uyku / D3cold)"`, `"0.0 W (D3cold)"`, `"0 MHz (Uyku)"`, `"0 MB / 0 MB (D3cold)"` |
| 4 | Fan Speed Tachometer | Fan RPM = 0 or disconnected | Formatter outputs: `"0 RPM (%0)"`; bar value set to 0 |
| 5 | Storage Disk Resolution | Secondary drive unpartitioned or unlettered | Capacity shows raw disk size, used bytes 0, display letter empty, SMART health retained |
| 6 | Battery Discharge Protection | AC adapter connected when discharge pressed | Modal dialog blocks discharge: `"DİKKAT: Adaptörünüz takılıyken laptop donanımı bataryayı boşaltamaz!..."` |
| 7 | Battery Discharge Target | Battery percent <= 80% | Discharge prevented, displays toast: `"Şarjınız zaten %{percent} (Hedef %80 veya altında)"` |
| 8 | RGB Backlight Persistence | System wake from S3/Modern Standby or lock screen unlock | Staged dual-pulse re-assertion at 250ms and 1500ms re-sends lighting packet to override Acer EC firmware reset |
| 9 | Single Instance Collision | User attempts second process launch | Mutex detects collision, signals `Local\NitroLite_SingleInstance_WakeUpEvent_AN16`, calls `ShowWindow()`, exits cleanly |
| 10 | Unit Test String Asserts | Test asserts exact `healthDesc` or `statusText` | Changing service-level strings without updating test asserts in `StorageHealthServiceTests` or `ThermalHealthServiceTests` causes build failures |

---

## Comprehensive String Inventory & De-Slop Specification Table

The following table maps every user-facing string identified across the codebase to its precise issue (Emoji in heading/label, Em-dash / pseudo-dash, Generic AI buzzword, Robotic filler adjective, Conversational fluff) and provides a clean, human-written technical replacement suitable for an enterprise-grade hardware monitoring utility.

### Category 1: Window Chrome, Titlebar, and Sidebar Navigation

| # | File & Line Number / Element | Current String | Issue Identified | Proposed Technical Human-Written Replacement String |
|---|-----------------------------|----------------|------------------|-----------------------------------------------------|
| 1 | `MainWindow.xaml:4` (`Title`) | `NitroLite - Acer Nitro Suite` | Spaced hyphen separator used as em-dash; redundant suite branding | `NitroLite` |
| 2 | `NitroLite.csproj:12` (`ApplicationTitle`) | `NitroLite - Acer Nitro Suite` | Same as above | `NitroLite` |
| 3 | `MainWindow.xaml:252` (`TextBlock`) | `⚡ NITRO` | Anti-pattern #3: Emoji in brand heading | `NITRO` |
| 4 | `MainWindow.xaml:253` (`TextBlock`) | `LITE` | N/A (Keep companion label) | `LITE` |
| 5 | `MainWindow.xaml:255` (`TextBlock`) | `v1.3 (Native .NET 10)` | Casual parenthetical phrasing | `v1.3 (.NET 10)` |
| 6 | `MainWindow.xaml:263` (`TxtBatteryIcon`) | `🔋` | Anti-pattern #3: Emoji in telemetry badge | *(Remove emoji or replace with vector battery glyph)* `BAT:` |
| 7 | `MainWindow.xaml:274` (`Button`) | `🗕` | Unicode symbol | `—` *(or standard minimize glyph)* |
| 8 | `MainWindow.xaml:278` (`Button`) | `✕` | Close glyph | `✕` |
| 9 | `MainWindow.xaml:294` (`TextBlock`) | `MENÜ` | Clunky all-caps label | `Gezinti` |
| 10 | `MainWindow.xaml:295` (`NavModes`) | `⚡ Güç & Fan Modları` | Anti-pattern #3: Emoji in navigation header | `Çalışma Modları` |
| 11 | `MainWindow.xaml:296` (`NavMonitoring`) | `📊 Canlı Telemetri & OSD` | Anti-pattern #3: Emoji in navigation header | `Donanım İzleme & OSD` |
| 12 | `MainWindow.xaml:297` (`NavRgb`) | `🌈 Klavye RGB Stüdyo` | Anti-pattern #3: Emoji; buzzword *"Stüdyo"* | `Klavye Aydınlatması` |
| 13 | `MainWindow.xaml:298` (`NavBattery`) | `🔋 %80 Pil & Sağlık` | Anti-pattern #3: Emoji in navigation header | `Pil & Termal Sağlık` |
| 14 | `MainWindow.xaml:299` (`NavDebloat`) | `🛠️ Debloat & Ayarlar` | Anti-pattern #3: Emoji in navigation header | `Sistem Servisleri & Ayarlar` |
| 15 | `MainWindow.xaml:306` (`TextBlock`) | `SİSTEM DURUMU` | All-caps header | `Sistem Durumu` |
| 16 | `MainWindow.xaml:308` (`TextBlock`) | `CPU Sıcaklık:` | Standard technical label | `CPU Sıcaklığı:` |
| 17 | `MainWindow.xaml:312` (`TextBlock`) | `GPU Sıcaklık:` | Standard technical label | `GPU Sıcaklığı:` |
| 18 | `MainWindow.xaml:316` (`TextBlock`) | `CPU Fanı:` | Standard technical label | `CPU Fan Hızı:` |

### Category 2: Tab 1 — Thermal Performance & Fan Modes (`TabModes`)

| # | File & Line Number / Element | Current String | Issue Identified | Proposed Technical Human-Written Replacement String |
|---|-----------------------------|----------------|------------------|-----------------------------------------------------|
| 19 | `MainWindow.xaml:331` (`TextBlock`) | `Termal Performans & Çalışma Modları` | Minor verbosity | `Çalışma Modları ve Fan Kontrolü` |
| 20 | `MainWindow.xaml:332` (`TextBlock`) | `Donanım saat frekanslarını, fan hız profillerini ve oyuna özel modları yönetin.` | Promotional tutorial subtitle ("yönetin") | `Donanım güç limitleri, fan profilleri ve oyuna özel yapılandırmalar.` |
| 21 | `MainWindow.xaml:339` (`TextBlock`) | `🌙` | Anti-pattern #3: Emoji in mode card | *(Remove emoji)* |
| 22 | `MainWindow.xaml:340` (`TextBlock`) | `SILENT` | Mode name | `Sessiz (Silent)` |
| 23 | `MainWindow.xaml:341` (`TextBlock`) | `Sessiz, düşük fan ve güç tasarrufu` | Casual phrasing | `Düşük akustik profil ve güç tasarrufu` |
| 24 | `MainWindow.xaml:343` (`TxtIndQuiet`) | `SEÇ` / `AKTİF` | Standard selector badge | `Seç` / `Aktif` |
| 25 | `MainWindow.xaml:351` (`TextBlock`) | `⚖️` | Anti-pattern #3: Emoji in mode card | *(Remove emoji)* |
| 26 | `MainWindow.xaml:352` (`TextBlock`) | `BALANCED` | Mode name | `Dengeli (Balanced)` |
| 27 | `MainWindow.xaml:353` (`TextBlock`) | `Günlük işler ve dengeli termal profil` | Informal phrasing | `Standart güç tüketimi ve otomatik fan eğrisi` |
| 28 | `MainWindow.xaml:363` (`TextBlock`) | `🔥` | Anti-pattern #3: Emoji in mode card | *(Remove emoji)* |
| 29 | `MainWindow.xaml:364` (`TextBlock`) | `POWER` | Mode name | `Performans (Power)` |
| 30 | `MainWindow.xaml:365` (`TextBlock`) | `Yüksek performans + 50 MHz GPU OC` | Informal phrasing | `Yüksek saat frekansları ve +50 MHz GPU ofseti` |
| 31 | `MainWindow.xaml:375` (`TextBlock`) | `⚡` | Anti-pattern #3: Emoji in mode card | *(Remove emoji)* |
| 32 | `MainWindow.xaml:376` (`TextBlock`) | `TURBO` | Mode name | `Turbo` |
| 33 | `MainWindow.xaml:377` (`TextBlock`) | `Maksimum soğutma + 100 MHz GPU OC` | Informal phrasing | `Maksimum fan devri ve +100 MHz GPU ofseti` |
| 34 | `MainWindow.xaml:398` (`TextBlock`) | `CPU FAN` | Technical header | `CPU Fanı` |
| 35 | `MainWindow.xaml:405` (`TextBlock`) | `GPU FAN` | Technical header | `GPU Fanı` |
| 36 | `MainWindow.xaml:412` (`BtnFanAuto`) | `🌀 Otomatik` | Anti-pattern #3: Emoji in button | `Otomatik` |
| 37 | `MainWindow.xaml:413` (`BtnFanMax`) | `🌪️ Maksimum` | Anti-pattern #3: Emoji in button | `Maksimum` |
| 38 | `MainWindow.xaml:422` (`TextBlock`) | `❄️ MANUEL CPU BOOST AYARI` | Anti-pattern #3: Emoji; all-caps heading | `CPU Performans Artışı (Boost)` |
| 39 | `MainWindow.xaml:423` (`TxtBoostStatusBadge`) | ` (5.0 GHz Agresif)` | Informal tag | ` (Agresif)` |
| 40 | `MainWindow.xaml:425` (`TextBlock`) | `Masaüstü ve genel kullanım için işlemci güç limitini belirleyin.` | Conversational phrasing | `Windows güç planı işlemci saat frekansı davranışı.` |
| 41 | `MainWindow.xaml:428` (`BtnBoostAggressive`) | `🚀 Son Hız` | Anti-pattern #3: Emoji; colloquial *"Son Hız"* | `Agresif` |
| 42 | `MainWindow.xaml:428` (`ToolTip`) | `Agresif 5.0 GHz Boost (~88°C)` | Speculative temperature | `Maksimum saat frekansı (5.0 GHz)` |
| 43 | `MainWindow.xaml:429` (`BtnBoostEfficient`) | `⚡ Dengeli` | Anti-pattern #3: Emoji | `Dengeli` |
| 44 | `MainWindow.xaml:429` (`ToolTip`) | `Dengeli Akıllı Boost (~78°C)` | Buzzword *"Akıllı"*; speculative temp | `Yüke duyarlı frekans yönetimi (~4.2 GHz)` |
| 45 | `MainWindow.xaml:430` (`BtnBoostDisabled`) | `❄️ Serin Eco` | Anti-pattern #3: Emoji; colloquial *"Serin Eco"* | `Devre Dışı` |
| 46 | `MainWindow.xaml:430` (`ToolTip`) | `Boost Kapalı / Serin 3.2 GHz (~68°C)` | Casual phrasing | `Temel saat frekansı sınırı (3.2 GHz)` |
| 47 | `MainWindow.xaml:443` (`TextBlock`) | `🎮 AKILLI OYUN PROFİLLERİ & ALGILAYICI` | Anti-pattern #3: Emoji; buzzword *"Akıllı"* | `Oyun Profili Yönetimi` |
| 48 | `MainWindow.xaml:445` (`TxtGameStatus`) | `Durum: Beklemede (Oyun tespit edilmedi)` | Good technical status | `Durum: Beklemede (Aktif oyun yok)` |
| 49 | `MainWindow.xaml:448` (`TextBlock`) | `Her oyun için özel Çalışma Modu, CPU Boost ve Fan ayarı belirleyin; oyun açıldığında otomatik devreye girsin.` | Chatty conversational tutorial | `Yürütülebilir dosya algılandığında ilgili mod ve fan yapılandırması otomatik uygulanır.` |
| 50 | `MainWindow.xaml:452` (`ChkAutoGameMode`) | `Otomatik Modu Aktif Tut` | Awkward wording | `Otomatik oyun algılamayı etkinleştir` |
| 51 | `MainWindow.xaml:465` (`TextBlock`) | `AYARLANACAK OYUNU SEÇİN:` | All-caps label | `Yapılandırılacak Oyun:` |
| 52 | `MainWindow.xaml:471` (`BtnScanGames`) | `🔍 Oyunları Tara` | Anti-pattern #3: Emoji in button | `Yüklü Oyunları Tara` |
| 53 | `MainWindow.xaml:471` (`ToolTip`) | `Steam, Epic ve sistemdeki yüklü oyunları otomatik tara ve ekle` | Imperative manual phrasing | `Steam ve Epic Games kütüphanelerindeki oyunları listeye ekler.` |
| 54 | `MainWindow.xaml:472` (`BtnAddCustomGame`) | `➕ Oyun Ekle (.exe)` | Anti-pattern #3: Emoji in button | `Oyun Ekle (.exe)` |
| 55 | `MainWindow.xaml:472` (`ToolTip`) | `Özel bir oyun exe dosyası seç` | Redundant tooltip | `Çalıştırılabilir dosya seçerek özel profil tanımlar.` |
| 56 | `MainWindow.xaml:473` (`BtnDeleteGame`) | `🗑️ Sil` | Anti-pattern #3: Emoji in button | `Profili Sil` |
| 57 | `MainWindow.xaml:473` (`ToolTip`) | `Seçili oyunu profillerden kaldır` | Redundant tooltip | `Seçili oyun profilini kayıtlı yapılandırmadan siler.` |
| 58 | `MainWindow.xaml:483` (`TextBlock`) | `⚙️ SEÇİLİ OYUN ÖZEL AYARLARI:` | Anti-pattern #3: Emoji; all-caps label | `Seçili Oyun Yapılandırması:` |
| 59 | `MainWindow.xaml:492` (`TextBlock`) | `1. Çalışma Modu:` | Numbered checklist style | `Çalışma Modu:` |
| 60 | `MainWindow.xaml:494` (`ComboBoxItem`) | `⚡ TURBO (Maksimum Güç)` | Anti-pattern #3: Emoji; marketing claim | `Turbo (Yüksek Güç Sınırı)` |
| 61 | `MainWindow.xaml:495` (`ComboBoxItem`) | `🔥 POWER (+50 MHz OC)` | Anti-pattern #3: Emoji | `Performans (+50 MHz GPU)` |
| 62 | `MainWindow.xaml:496` (`ComboBoxItem`) | `⚖️ BALANCED (Dengeli)` | Anti-pattern #3: Emoji | `Dengeli (Varsayılan)` |
| 63 | `MainWindow.xaml:497` (`ComboBoxItem`) | `🌙 SILENT (Sessiz)` | Anti-pattern #3: Emoji | `Sessiz (Düşük Güç)` |
| 64 | `MainWindow.xaml:503` (`TextBlock`) | `2. CPU Boost Modu:` | Numbered checklist style | `CPU Boost Davranışı:` |
| 65 | `MainWindow.xaml:505` (`ComboBoxItem`) | `❄️ Serin Eco (3.2 GHz / ~68°C)` | Anti-pattern #3: Emoji; colloquial *"Serin Eco"* | `Devre Dışı (Temel Saat / 3.2 GHz)` |
| 66 | `MainWindow.xaml:506` (`ComboBoxItem`) | `⚡ Dengeli (4.2 GHz / ~78°C)` | Anti-pattern #3: Emoji | `Dengeli (Otomatik Saat / 4.2 GHz)` |
| 67 | `MainWindow.xaml:507` (`ComboBoxItem`) | `🚀 Son Hız (5.0 GHz / ~88°C)` | Anti-pattern #3: Emoji; colloquial *"Son Hız"* | `Agresif (Maksimum Saat / 5.0 GHz)` |
| 68 | `MainWindow.xaml:513` (`TextBlock`) | `3. Fan Hızı:` | Numbered checklist style | `Fan Profili:` |
| 69 | `MainWindow.xaml:515` (`ComboBoxItem`) | `🌀 Otomatik Fan Eğrisi` | Anti-pattern #3: Emoji | `Otomatik Fan Eğrisi` |
| 70 | `MainWindow.xaml:516` (`ComboBoxItem`) | `🌪️ Maksimum Fan Hızı` | Anti-pattern #3: Emoji | `Maksimum Devir (Sabit)` |
| 71 | `MainWindow.xaml:523` (`TextBlock`) | `Bu oyuna girdiğinizde seçtiğiniz modlar anında uygulanır. Oyunu kapattığınızda önceki masaüstü profilinize otomatik dönülür.` | Chatty conversational filler | `Oyun açılışında ilgili ayarlar otomatik uygulanır, kapanışta masaüstü profili geri yüklenir.` |
| 72 | `MainWindow.xaml:524` (`BtnSaveGameProfile`) | `💾 Bu Oyunun Ayarlarını Kaydet` | Anti-pattern #3: Emoji; wordy | `Yapılandırmayı Kaydet` |

### Category 3: Tab 2 — Live Monitoring & OSD (`TabMonitoring`)

| # | File & Line Number / Element | Current String | Issue Identified | Proposed Technical Human-Written Replacement String |
|---|-----------------------------|----------------|------------------|-----------------------------------------------------|
| 73 | `MainWindow.xaml:540` (`TextBlock`) | `Canlı Donanım Telemetrisi & OSD Katmanı` | Standard heading | `Donanım Telemetrisi ve OSD Göstergesi` |
| 74 | `MainWindow.xaml:541` (`TextBlock`) | `AASSvc ve NVML donanım sensörlerinden saniyelik okunan CPU, GPU, Watt ve VRAM verileri.` | Descriptive subtitle | `AASSvc ve NVML sensör arabirimlerinden gerçek zamanlı donanım telemetrisi.` |
| 75 | `MainWindow.xaml:546` (`BtnToggleOsd`) | `🖥️ Ekran Üstü Bilgi Göstergesi (OSD)` | Anti-pattern #3: Emoji in button | `Ekran Üstü Gösterge (OSD)` |
| 76 | `MainWindow.xaml:546` (`ToolTip`) | `Oyun oynarken ekranda donanım istatistiklerini gösteren yarı saydam Afterburner katmanı` | Casual marketing buzzword *"Afterburner"* | `Oyun esnasında donanım kullanım verilerini ekranda gösterir.` |
| 77 | `MainWindow.xaml:547` (`BtnCycleOsdCorner`) | `🔄 Konum Değiştir` | Anti-pattern #3: Emoji in button | `Konum Değiştir` |
| 78 | `MainWindow.xaml:547` (`ToolTip`) | `OSD bilgi katmanını 4 ekran köşesi arasında kaydırır (Sol Üst, Sağ Üst, Sağ Alt, Sol Alt)` | Wordy manual explanation | `OSD katmanını ekran köşeleri arasında taşır.` |
| 79 | `MainWindow.xaml:556` (`TextBlock`) | `💻 AMD Ryzen Zen 4 CPU` | Anti-pattern #3: Emoji in subhead | `AMD Ryzen Zen 4 CPU` |
| 80 | `MainWindow.xaml:557` (`TxtCpuCoreCount`) | ` (16 Çekirdek)` | Standard technical spec | ` (16 Çekirdek)` |
| 81 | `MainWindow.xaml:562` (`TextBlock`) | `Sıcaklık:` | Standard technical label | `Sıcaklık:` |
| 82 | `MainWindow.xaml:569` (`TextBlock`) | `Güç Tüketimi (Paket Güç):` | Redundant parenthetical | `Paket Güç Tüketimi:` |
| 83 | `MainWindow.xaml:576` (`TextBlock`) | `İşlemci Kullanımı:` | Standard technical label | `İşlemci Kullanımı:` |
| 84 | `MainWindow.xaml:583` (`TextBlock`) | `Saat Hızı:` | Standard technical label | `Saat Frekansı:` |
| 85 | `MainWindow.xaml:592` (`TextBlock`) | `🎮 NVIDIA GeForce RTX 4070 Laptop` | Anti-pattern #3: Emoji in subhead | `NVIDIA GeForce RTX 4070 Laptop GPU` |
| 86 | `MainWindow.xaml:596` (`TextBlock`) | `Sıcaklık:` | Standard technical label | `Sıcaklık:` |
| 87 | `MainWindow.xaml:603` (`TextBlock`) | `TGP Güç Tüketimi (Watt):` | Redundant unit in parenthesis | `TGP Güç Tüketimi:` |
| 88 | `MainWindow.xaml:610` (`TextBlock`) | `GPU Kullanımı:` | Standard technical label | `GPU Kullanımı:` |
| 89 | `MainWindow.xaml:617` (`TextBlock`) | `Video Belleği (VRAM):` | Standard technical label | `VRAM Kullanımı:` |
| 90 | `MainWindow.xaml:635` (`TextBlock`) | `RAM SİSTEM BELLEK KULLANIMI` | Repetitive all-caps label | `Sistem Belleği (RAM)` |
| 91 | `MainWindow.xaml:644` (`TextBlock`) | `ANAKART & SİSTEM SICAKLIĞI` | All-caps label | `Anakart ve Sistem Sıcaklığı` |
| 92 | `MainWindow.xaml:646` (`TxtMonRamFreq`) | `DDR5 5600 MHz Dual-Channel (32 GB)` | Hardware specification | `DDR5 5600 MHz Çift Kanal (32 GB)` |
| 93 | `MainWindow.xaml:660` (`TextBlock`) | `⚡ AKILLI RAM ÖNBELLEK TEMİZLEYİCİ` | Anti-pattern #3: Emoji; buzzword *"Akıllı"* | `Bellek Önbelleği Temizleme` |
| 94 | `MainWindow.xaml:661` (`TxtRamCleanStatus`) | ` (Standby Hazır)` | Technical state | ` (Hazır)` |
| 95 | `MainWindow.xaml:663` (`ChkAutoRamClean`) | `Otomatik Akıllı Temizleme (Oyun Başlangıcında & Boş RAM < 4 GB İken)` | Buzzword *"Akıllı"* | `Otomatik bellek temizleme (Oyun başlangıcında ve boş RAM < 4 GB iken)` |
| 96 | `MainWindow.xaml:666` (`BtnCleanRamCache`) | `🧹 RAM Önbelleği Temizle` | Anti-pattern #3: Emoji in button | `Bellek Önbelleğini Temizle` |
| 97 | `MainWindow.xaml:666` (`ToolTip`) | `Arka planda biriken kullanılmayan Standby ve çalışan küme önbelleklerini anında boşaltarak oyunlara temiz RAM açar.` | Conversational marketing fluff ("temiz RAM açar") | `Kullanılmayan Standby bellek listesini ve çalışan küme önbelleklerini temizler.` |
| 98 | `MainWindow.xaml:677` (`TextBlock`) | `📈 CANLI DONANIM GEÇMİŞİ & GRAFİK TABLOSU` | Anti-pattern #3: Emoji; all-caps heading | `Telemetri Geçmiş Grafiği` |
| 99 | `MainWindow.xaml:678` (`TextBlock`) | `Seçtiğiniz metriğin son 60 saniyelik değişim eğrisini ve tepe noktalarını canlı izleyin.` | Marketing subtitle ("canlı izleyin") | `Seçili donanım metriğinin son 60 saniyelik zaman serisi verileri.` |
| 100 | `MainWindow.xaml:683` (`ComboBoxItem`) | `🌡️ CPU Sıcaklığı (°C)` | Anti-pattern #3: Emoji | `CPU Sıcaklığı (°C)` |
| 101 | `MainWindow.xaml:684` (`ComboBoxItem`) | `🌡️ GPU Sıcaklığı (°C)` | Anti-pattern #3: Emoji | `GPU Sıcaklığı (°C)` |
| 102 | `MainWindow.xaml:685` (`ComboBoxItem`) | `⚡ CPU Güç Tüketimi (Watt)` | Anti-pattern #3: Emoji | `CPU Güç Tüketimi (W)` |
| 103 | `MainWindow.xaml:686` (`ComboBoxItem`) | `⚡ GPU Güç Tüketimi (Watt)` | Anti-pattern #3: Emoji | `GPU Güç Tüketimi (W)` |
| 104 | `MainWindow.xaml:687` (`ComboBoxItem`) | `🚀 CPU Saat Hızı (GHz)` | Anti-pattern #3: Emoji | `CPU Saat Frekansı (GHz)` |
| 105 | `MainWindow.xaml:688` (`ComboBoxItem`) | `📊 CPU Kullanımı (%)` | Anti-pattern #3: Emoji | `CPU Kullanımı (%)` |
| 106 | `MainWindow.xaml:689` (`ComboBoxItem`) | `📊 GPU Kullanımı (%)` | Anti-pattern #3: Emoji | `GPU Kullanımı (%)` |
| 107 | `MainWindow.xaml:690` (`ComboBoxItem`) | `🧠 RAM Kullanımı (%)` | Anti-pattern #3: Emoji | `RAM Kullanımı (%)` |
| 108 | `MainWindow.xaml:691` (`ComboBoxItem`) | `🎮 VRAM Kullanımı (GB)` | Anti-pattern #3: Emoji | `VRAM Kullanımı (GB)` |
| 109 | `MainWindow.xaml:714` (`TextBlock`) | `ŞU ANKİ DEĞER` | All-caps label | `Mevcut` |
| 110 | `MainWindow.xaml:721` (`TextBlock`) | `MİNİMUM (60s)` | All-caps label | `Minimum (60 sn)` |
| 111 | `MainWindow.xaml:728` (`TextBlock`) | `MAKSİMUM (60s)` | All-caps label | `Maksimum (60 sn)` |
| 112 | `MainWindow.xaml:735` (`TextBlock`) | `ORTALAMA (60s)` | All-caps label | `Ortalama (60 sn)` |

### Category 4: Tab 3 — Keyboard RGB Studio (`TabRgb`)

| # | File & Line Number / Element | Current String | Issue Identified | Proposed Technical Human-Written Replacement String |
|---|-----------------------------|----------------|------------------|-----------------------------------------------------|
| 113 | `MainWindow.xaml:750` (`TextBlock`) | `Klavye RGB Aydınlatma Stüdyosu` | Buzzword *"Stüdyosu"* | `Klavye RGB Aydınlatma Kontrolü` |
| 114 | `MainWindow.xaml:751` (`TextBlock`) | `4 bağımsız bölge rengini, dinamik efektleri ve özel profilleri yönetin.` | Imperative tutorial phrasing ("yönetin") | `Dört bölgeli RGB renk dizilimi, dinamik aydınlatma efektleri ve donanım profilleri.` |
| 115 | `MainWindow.xaml:756` (`TextBlock`) | `4 BÖLGELİ KLAVYE ÖNİZLEMESİ` | All-caps label | `Dört Bölgeli Aydınlatma Düzeni` |
| 116 | `MainWindow.xaml:759` (`TextBlock`) | `BÖLGE 1 (Sol)` | All-caps | `Bölge 1 (Sol)` |
| 117 | `MainWindow.xaml:762` (`TextBlock`) | `BÖLGE 2 (Orta-Sol)` | All-caps | `Bölge 2 (Orta Sol)` |
| 118 | `MainWindow.xaml:765` (`TextBlock`) | `BÖLGE 3 (Orta-Sağ)` | All-caps | `Bölge 3 (Orta Sağ)` |
| 119 | `MainWindow.xaml:768` (`TextBlock`) | `BÖLGE 4 (Numpad)` | All-caps | `Bölge 4 (Sayısal Tuş Takımı)` |
| 120 | `MainWindow.xaml:779` (`TextBlock`) | `✨ RGB PROFİL YÖNETİCİSİ (HAZIR & ÖZEL PROFİLLER)` | Anti-pattern #3: Emoji; buzzword heading | `Aydınlatma Profilleri` |
| 121 | `MainWindow.xaml:780` (`TextBlock`) | `Tek tıkla zengin temalar uygulayın veya kendi renk diziliminizi profil olarak kaydedin.` | Marketing buzzword *"zengin temalar"*, *"tek tıkla"* | `Ön tanımlı aydınlatma temalarını uygulayabilir veya özel konfigürasyon kaydedebilirsiniz.` |
| 122 | `MainWindow.xaml:795` (`ToolTip`) | `Yeni profil adı girin` | Tooltip | `Yeni profil adı` |
| 123 | `MainWindow.xaml:799` (`BtnSaveCustomPreset`) | `💾 Profili Kaydet` | Anti-pattern #3: Emoji in button | `Profili Kaydet` |
| 124 | `MainWindow.xaml:800` (`BtnDeleteCustomPreset`) | `🗑️ Sil` | Anti-pattern #3: Emoji in button | `Profili Sil` |
| 125 | `MainWindow.xaml:809` (`TextBlock`) | `HIZLI RENK PALETLERİ` | All-caps label | `Hızlı Renk Seçimi` |
| 126 | `MainWindow.xaml:810` (`BtnTurnOffLights`) | `🌑 Işıkları Tamamen Kapat (LED Off)` | Anti-pattern #3: Emoji; wordy | `Aydınlatmayı Kapat` |
| 127 | `MainWindow.xaml:813` (`ToolTip`) | `Camgöbeği (Cyan)` | Color tooltip | `Camgöbeği (Cyan)` |
| 128 | `MainWindow.xaml:814` (`ToolTip`) | `Nitro Kırmızı` | Color tooltip | `Kırmızı` |
| 129 | `MainWindow.xaml:815` (`ToolTip`) | `Neon Yeşil` | Color tooltip | `Yeşil` |
| 130 | `MainWindow.xaml:816` (`ToolTip`) | `Mor (Purple)` | Color tooltip | `Mor` |
| 131 | `MainWindow.xaml:817` (`ToolTip`) | `Amber Turuncu` | Color tooltip | `Turuncu` |
| 132 | `MainWindow.xaml:818` (`ToolTip`) | `Saf Beyaz` | Color tooltip | `Beyaz` |
| 133 | `MainWindow.xaml:819` (`ToolTip`) | `Siyah / Işık Kapalı` | Color tooltip | `Kapalı` |
| 134 | `MainWindow.xaml:832` (`TextBlock`) | `Işık Efekti:` | Technical label | `Aydınlatma Efekti:` |
| 135 | `MainWindow.xaml:834` (`ComboBoxItem`) | `STATIC (Sabit Renk)` | Mode label | `Sabit (Static)` |
| 136 | `MainWindow.xaml:835` (`ComboBoxItem`) | `WAVE (Dalga)` | Mode label | `Dalga (Wave)` |
| 137 | `MainWindow.xaml:836` (`ComboBoxItem`) | `BREATHING (Nefes)` | Mode label | `Nefes (Breathing)` |
| 138 | `MainWindow.xaml:837` (`ComboBoxItem`) | `NEON (Neon Geçiş)` | Mode label | `Neon` |
| 139 | `MainWindow.xaml:838` (`ComboBoxItem`) | `SHIFTING (Renk Kayması)` | Mode label | `Renk Geçişi (Shifting)` |
| 140 | `MainWindow.xaml:839` (`ComboBoxItem`) | `ZOOM (Büyüme)` | Mode label | `Genişleme (Zoom)` |
| 141 | `MainWindow.xaml:840` (`ComboBoxItem`) | `METEOR (Kayan Işık)` | Mode label | `Kayan Işık (Meteor)` |
| 142 | `MainWindow.xaml:841` (`ComboBoxItem`) | `TWINKLING (Yıldız Parıltısı)` | Mode label | `Parıltı (Twinkling)` |
| 143 | `MainWindow.xaml:842` (`ComboBoxItem`) | `OFF (Işık Kapalı)` | Mode label | `Kapalı (Off)` |
| 144 | `MainWindow.xaml:845` (`ChkBacklightTimeout`) | `30 Saniye Hareketsizlikte Işığı Kapat` | Conversational label | `30 saniye boşta kaldığında aydınlatmayı kapat` |
| 145 | `MainWindow.xaml:850` (`TextBlock`) | `Parlaklık:` | Standard technical label | `Parlaklık:` |
| 146 | `MainWindow.xaml:856` (`TextBlock`) | `Efekt Hızı:` | Standard technical label | `Efekt Hızı:` |

### Category 5: Tab 4 — Battery & Thermal Health (`TabBattery`)

| # | File & Line Number / Element | Current String | Issue Identified | Proposed Technical Human-Written Replacement String |
|---|-----------------------------|----------------|------------------|-----------------------------------------------------|
| 147 | `MainWindow.xaml:871` (`TextBlock`) | `%80 Pil Koruması & Güvenli Deşarj Asistanı` | Buzzword *"Asistanı"* | `Pil Şarj Sınırı ve Güvenli Deşarj` |
| 148 | `MainWindow.xaml:872` (`TextBlock`) | `Pilin aşırı şarj olmasını engelleyin, %80'e kontrollü olarak geri boşaltın ve batarya sağlığını izleyin.` | Wordy promotional phrasing | `Donanım pil sağlığını korumak için şarj sınırını %80 seviyesinde tutar ve kontrollü deşarj sağlar.` |
| 149 | `MainWindow.xaml:878` (`TextBlock`) | `GÜNCEL ŞARJ DURUMU` | All-caps label | `Şarj Durumu` |
| 150 | `MainWindow.xaml:881` (`TxtBatStateDesc`) | `Adaptöre Takılı` | Technical state | `Adaptöre Bağlı` |
| 151 | `MainWindow.xaml:890` (`TextBlock`) | `GERÇEK ZAMANLI GÜÇ AKIŞI (WATT):` | All-caps label | `Anlık Güç Akışı:` |
| 152 | `MainWindow.xaml:897` (`ChkWatchdog`) | `%80 Şarj Sınırını Zorla (Aktif Gözetmen)` | Parenthetical fluff | `%80 şarj koruma sınırını uygula` |
| 153 | `MainWindow.xaml:898` (`TextBlock`) | `EC çipinin sıfırlanıp 100%'e çıkmasını engellemek için arka planda sürekli doğrular.` | Chatty phrasing | `Gömülü denetleyici (EC) şarj sınırını arka planda düzenli olarak denetler.` |
| 154 | `MainWindow.xaml:905` (`TextBlock`) | `🎯 %80'E GÜVENLİ BOŞALTMA ASİSTANI` | Anti-pattern #3: Emoji; buzzword *"Asistan"* | `Hedef Seviyeye Deşarj` |
| 155 | `MainWindow.xaml:906` (`TextBlock`) | `Şarjınız %80'in üzerine çıktığında (örn. %90-%100), adaptörü çıkarıp bu butona bastığınızda güvenli bir arka plan yükü ile pil tam %80'e kadar indirilir. %80'e ulaşıldığı an otomatik durur ve sesli/görsel uyarı verir.` | Very verbose instructional copy | `Adaptör bağlantısı kesildiğinde kontrollü işlemci yükü oluşturarak pili %80 hedef seviyesine indirir ve otomatik durur.` |
| 156 | `MainWindow.xaml:909` (`TxtDischargeStatus`) | `Durum: Beklemede (%80 hedefi hazır)` | Conversational | `Durum: Boşta` |
| 157 | `MainWindow.xaml:912` (`BtnToggleDischarge`) | `⚡ %80'e Güvenli Deşarjı Başlat` | Anti-pattern #3: Emoji; buzzword *"Güvenli"* | `Deşarj İşlemini Başlat` |
| 158 | `MainWindow.xaml:922` (`TextBlock`) | `🔬 DONANIMSAL BATARYA SAĞLIĞI & AŞINMA (WEAR) RAPORU` | Anti-pattern #3: Emoji; all-caps heading | `Batarya Kapasite ve Aşınma Durumu` |
| 159 | `MainWindow.xaml:923` (`TextBlock`) | `LG Chem AP21A8T (90.6 Wh) doğrudan WMI donanım çipinden canlı okunan kapasite.` | Verbose description | `WMI donanım arabirimi üzerinden okunan kapasite ve gerilim telemetrisi.` |
| 160 | `MainWindow.xaml:925` (`BtnRefreshBatHealth`) | `🔄 Sağlık Verilerini Güncelle` | Anti-pattern #3: Emoji in button | `Verileri Güncelle` |
| 161 | `MainWindow.xaml:932` (`TextBlock`) | `BATARYA SAĞLIĞI` | All-caps label | `Pil Sağlığı` |
| 162 | `MainWindow.xaml:934` (`TextBlock`) | `Mükemmel Durumda` | Anti-pattern #17: Buzzword *"Mükemmel"* | `Nominal` |
| 163 | `MainWindow.xaml:941` (`TextBlock`) | `AŞINMA SEVİYESİ (WEAR)` | All-caps label | `Aşınma Seviyesi` |
| 164 | `MainWindow.xaml:943` (`TextBlock`) | `Minimal yıpranma` | Vague subjective adjective | `Düşük Aşınma (< %10)` |
| 165 | `MainWindow.xaml:950` (`TextBlock`) | `TAM ŞARJ KAPASİTESİ` | All-caps label | `Tam Şarj Kapasitesi` |
| 166 | `MainWindow.xaml:952` (`TxtBatDesignCap`) | `Fabrika: 90.614 mWh` | Technical spec | `Tasarım: 90.614 mWh` |
| 167 | `MainWindow.xaml:959` (`TextBlock`) | `HÜCRE GERİLİMİ` | All-caps label | `Hücre Gerilimi` |
| 168 | `MainWindow.xaml:961` (`TextBlock`) | `4-Hücreli Lityum-İyon` | Technical spec | `4 Hücreli Lityum-İyon` |
| 169 | `MainWindow.xaml:973` (`TextBlock`) | `🌡️ TERMAL MACUN, PUTTY & FAN SAĞLIK RAPORU` | Anti-pattern #3: Emoji; all-caps heading | `Termal İletkenlik ve Soğutma Analizi` |
| 170 | `MainWindow.xaml:974` (`TextBlock`) | `AMD Ryzen Zen 4 termal macun & putty ısı iletim verimliliği, fan motor aşınması ve uzun dönem sıcaklık trendi.` | Informal phrase | `İşlemci termal direnci, fan çalışma süreleri ve çok günlük termal kararlılık trendi.` |
| 171 | `MainWindow.xaml:977` (`TxtThermalBadge`) | `Termal Macun & Putty Sağlıklı` | Subjective wording | `Termal Direnç: Nominal` |
| 172 | `MainWindow.xaml:985` (`TextBlock`) | `TERMAL İLETKENLİK SAĞLIĞI` | All-caps label | `Termal İletkenlik Durumu` |
| 173 | `MainWindow.xaml:987` (`TxtThermalResistance`) | `Direnç: 2.15 °C/W (Optimal)` | Subjective tag | `Direnç Oranı: 2.15 °C/W` |
| 174 | `MainWindow.xaml:994` (`TextBlock`) | `TOPLAM FAN ÇALIŞMA SÜRESİ` | All-caps label | `Aktif Fan Çalışma Süresi` |
| 175 | `MainWindow.xaml:996` (`TxtAvgFanRpm`) | `Ortalama: ~2650 RPM` | Technical metric | `Ortalama: 2650 RPM` |
| 176 | `MainWindow.xaml:1003` (`TextBlock`) | `FANSIZ SESSİZ ÇALIŞMA` | All-caps label | `Pasif Soğutma Süresi` |
| 177 | `MainWindow.xaml:1005` (`TextBlock`) | `0 RPM Sıfır Aşınma` | Fluffy marketing claim | `0 RPM (Pasif Profil)` |
| 178 | `MainWindow.xaml:1012` (`TextBlock`) | `FAN MOTOR / RULMAN SAĞLIĞI` | All-caps label | `Fan Motor / Rulman Sağlığı` |
| 179 | `MainWindow.xaml:1014` (`TextBlock`) | `30.000 Saat MTBF Ömrü` | Speculative manufacturer MTBF claim | `Hesaplanan MTBF: 30.000 Saat` |
| 180 | `MainWindow.xaml:1024` (`TextBlock`) | `📈 TERMAL KARARLILIK & YAŞLANMA TRENDİ` | Anti-pattern #3: Emoji; all-caps heading | `Termal Kararlılık ve Trend Analizi` |
| 181 | `MainWindow.xaml:1025` (`TxtStabilityDaysSubtitle`) | ` (Son 30 Günlük Veri Seti)` | Parenthetical subtitle | ` (30 Günlük Veri)` |
| 182 | `MainWindow.xaml:1028` (`RadioTrend14Days`) | `14 Gün` | Time filter | `14 Gün` |
| 183 | `MainWindow.xaml:1029` (`RadioTrend30Days`) | `30 Gün` | Time filter | `30 Gün` |
| 184 | `MainWindow.xaml:1037` (`TxtStabilityStatus`) | `Kararlı, Macun kondisyonu mükemmel` | Anti-pattern #17: Buzzword *"Mükemmel"*; referenced in prompt example | `Kararlı (Direnç Değişimi Yok)` |
| 185 | `MainWindow.xaml:1038` (`TxtStabilitySlope`) | `Eğim: +0.001 °C/W/gün (Stabil)` | Technical metric | `Eğim: +0.001 °C/W/gün (Kararlı)` |
| 186 | `MainWindow.xaml:1042` (`TxtStabilityBadge`) | `Optimum İletim` | Subjective buzzword | `Nominal` |
| 187 | `MainWindow.xaml:1054` (`TextBlock`) | `🌡️ ALT BİLEŞEN FİZİKSEL SICAKLIK MATRİSİ` | Anti-pattern #3: Emoji; all-caps heading | `Bileşen Sıcaklık Matrisi` |
| 188 | `MainWindow.xaml:1055` (`TextBlock`) | `Canlı Donanım Sensörleri (ACPI / SMART / AcerAgent)` | Descriptive subtitle | `ACPI, SMART ve AcerAgent arabirimlerinden canlı veriler.` |
| 189 | `MainWindow.xaml:1062` (`TextBlock`) | `ANAKART / VRM` | All-caps label | `Anakart / VRM` |
| 190 | `MainWindow.xaml:1064` (`TextBlock`) | `SYS1 (AcerAgent)` | Interface source | `SYS1 (ACPI)` |
| 191 | `MainWindow.xaml:1071` (`TextBlock`) | `NVMe SSD 1 [C:]` | Drive label | `NVMe SSD 1 (C:)` |
| 192 | `MainWindow.xaml:1073` (`TextBlock`) | `Samsung 990 PRO` | Hardware model | `Samsung 990 PRO` |
| 193 | `MainWindow.xaml:1080` (`TextBlock`) | `NVMe SSD 2 [D:]` | Drive label | `NVMe SSD 2 (D:)` |
| 194 | `MainWindow.xaml:1082` (`TextBlock`) | `Samsung PM9A1` | Hardware model | `Samsung PM9A1` |
| 195 | `MainWindow.xaml:1089` (`TextBlock`) | `PİL PAKETİ` | All-caps label | `Batarya Hücreleri` |
| 196 | `MainWindow.xaml:1091` (`TextBlock`) | `LGC AP21A8T (WMI)` | Hardware model | `LGC AP21A8T (WMI)` |
| 197 | `MainWindow.xaml:1102` (`TextBlock`) | `PROAKTİF BAKIM ASİSTANI DEĞERLENDİRMESİ:` | All-caps; buzzword *"Proaktif Bakım Asistanı"* | `Termal Durum Özeti:` |
| 198 | `MainWindow.xaml:1103` (`TxtThermalRecommendation`) | `Termal macun ve putty yerleşimi çok kararlı. Isı transferinde kuruma tespit edilmedi. Cihazınız gayet serin çalışmaktadır.` | Anthropomorphic chatty fluff *"gayet serin çalışmaktadır"* | `Termal direnç ve ısı transferi nominal tolerans sınırları içindedir. Bakım gerekmemektedir.` |
| 199 | `MainWindow.xaml:1115` (`TextBlock`) | `💽 DEPOLAMA (NVMe SSD) SAĞLIĞI & KAPASİTE ÇUBUKLARI` | Anti-pattern #3: Emoji; all-caps heading | `NVMe Depolama Sürücüleri` |
| 200 | `MainWindow.xaml:1116` (`TextBlock`) | `Dahili Samsung PCIe Gen 4x4 NVMe SSD'lerin donanımsal sağlık durumu, doluluk ve boş alan oranları.` | Subtitle | `PCIe Gen 4x4 NVMe SSD donanım sağlığı, SMART yıpranma verileri ve doluluk oranları.` |
| 201 | `MainWindow.xaml:1119` (`TxtStorageBadge`) | `Tüm Sürücüler Sağlıklı` | Clean technical status | `Sürücüler Sağlıklı` |
| 202 | `MainWindow.xaml:1129` (`TxtDrive0Letter`) | `💾 [C:] ` | Anti-pattern #3: Emoji | `[C:] ` |
| 203 | `MainWindow.xaml:1134` (`TxtDrive0Temp`) | `🌡️ 42 °C` | Anti-pattern #3: Emoji | `42 °C` |
| 204 | `MainWindow.xaml:1135` (`TxtDrive0HealthBadge`) | `🟢 SAĞLIK: %99 (Mükemmel)` | Anti-pattern #3: Emoji; buzzword *"Mükemmel"* | `Sağlık: %99 (İyi)` |
| 205 | `MainWindow.xaml:1141` (`TxtDrive0Free`) | `1485.3 GB Boş Alan` | Technical metric | `1485.3 GB Boş` |
| 206 | `MainWindow.xaml:1152` (`TxtDrive1Letter`) | `💾 [D:] ` | Anti-pattern #3: Emoji | `[D:] ` |
| 207 | `MainWindow.xaml:1157` (`TxtDrive1Temp`) | `🌡️ 39 °C` | Anti-pattern #3: Emoji | `39 °C` |
| 208 | `MainWindow.xaml:1158` (`TxtDrive1HealthBadge`) | `🟢 SAĞLIK: %98 (Mükemmel)` | Anti-pattern #3: Emoji; buzzword *"Mükemmel"* | `Sağlık: %98 (İyi)` |
| 209 | `MainWindow.xaml:1164` (`TxtDrive1Free`) | `838.4 GB Boş Alan` | Technical metric | `838.4 GB Boş` |
| 210 | `MainWindow.xaml:1176` (`TextBlock`) | `⚠️` | Anti-pattern #3: Emoji warning icon | `[!]` *(or clean vector icon)* |
| 211 | `MainWindow.xaml:1178` (`TextBlock`) | `Şarj Sınırı Aşıldı!` | Exclamation mark | `Şarj Eşiği Aşıldı` |
| 212 | `MainWindow.xaml:1179` (`TxtOverchargeMsg`) | `Piliniz %80'in üzerine çıktı. Şarj adaptörünü çıkarabilir veya Güvenli Deşarjı başlatabilirsiniz.` | Conversational phrasing | `Pil seviyesi %80 sınırının üzerindedir. Dilerseniz hedef seviyeye deşarj işlemini başlatabilirsiniz.` |

### Category 6: Tab 5 — Debloat Hub & Settings (`TabDebloat`)

| # | File & Line Number / Element | Current String | Issue Identified | Proposed Technical Human-Written Replacement String |
|---|-----------------------------|----------------|------------------|-----------------------------------------------------|
| 213 | `MainWindow.xaml:1191` (`TextBlock`) | `Debloat Merkezi & Ayarlar` | Casual buzzword *"Debloat Merkezi"* | `Sistem Servisleri ve Başlangıç Ayarları` |
| 214 | `MainWindow.xaml:1192` (`TextBlock`) | `Gereksiz arka plan telemetri servislerini kapatın ve başlangıç tercihlerini yapılandırın.` | Imperative instructional tone | `Gereksiz OEM telemetri servislerini devre dışı bırakın ve başlangıç davranışını yapılandırın.` |
| 215 | `MainWindow.xaml:1198` (`TextBlock`) | `Tek Tıkla Güvenli Telemetri Debloat (Tier 1)` | Marketing buzzword *"Tek Tıkla"* | `OEM Telemetri Servislerini Devre Dışı Bırak (Tier 1)` |
| 216 | `MainWindow.xaml:1199` (`TextBlock`) | `AcerServiceSvc, ASMSvc, AcerDIAgentSvis, AcerEZSvc, AcerDeviceInfoAgentService, ADESv2` | Technical service list | `AcerServiceSvc, ASMSvc, AcerDIAgentSvis, AcerEZSvc, AcerDeviceInfoAgentService, ADESv2` |
| 217 | `MainWindow.xaml:1202` (`Button`) | `🛡️ Debloat Uygula (~214 MB)` | Anti-pattern #3: Emoji in button | `Servisleri Devre Dışı Bırak (~214 MB)` |
| 218 | `MainWindow.xaml:1203` (`Button`) | `Fabrika Ayarlarına Dön` | Standard recovery button | `Varsayılan Servisleri Etkinleştir` |
| 219 | `MainWindow.xaml:1230` (`TextBlock`) | `Orijinal NitroSense Arka Plan Engelleyici` | Fluff adjective *"Orijinal"* | `NitroSense Arka Plan Yürütme Kısıtlaması` |
| 220 | `MainWindow.xaml:1231` (`TxtNitroSenseRunningBadge`) | ` (Çalışmıyor)` | Technical state badge | ` (Devre Dışı)` |
| 221 | `MainWindow.xaml:1233` (`TextBlock`) | `NitroSense'in siz kapatınca arkada kalıp ~845 MB RAM tüketmesini engeller. NitroSense yalnızca siz simgesine tıkladığınızda açılır, arkada gereksiz çalışmaz.` | Conversational phrasing ("siz kapatınca", "siz simgesine tıkladığınızda") | `NitroSense kapatıldığında arka plan servislerinin çalışmaya devam etmesini ve bellek tüketmesini engeller.` |
| 222 | `MainWindow.xaml:1235` (`BtnKillNitroSense`) | `🛑 NitroSense'i Kapat & Engelle` | Anti-pattern #3: Emoji in button | `NitroSense İşlemlerini Sonlandır` |
| 223 | `MainWindow.xaml:1244` (`TextBlock`) | `NitroLite Windows ile Birlikte Başlasın` | Conversational phrasing | `Windows başlangıcında otomatik çalıştır` |
| 224 | `MainWindow.xaml:1245` (`TextBlock`) | `Bilgisayar açıldığında arka planda başlar (~25 MB RAM harcar).` | Conversational fluff | `Kullanıcı oturumu açıldığında arka planda başlatılır.` |
| 225 | `MainWindow.xaml:1254` (`TextBlock`) | `Sistem Tepsisinde (Simge Durumunda) Sessiz Başlat` | Wordy heading | `Sistem tepsisinde simge durumunda başlat` |
| 226 | `MainWindow.xaml:1255` (`TextBlock`) | `PC açıldığında pencereler ekrana gelmez, doğrudan sağ alt sistem tepsisinde sessizce çalışır.` | Chatty conversational phrasing | `Uygulama açılışta ana pencereyi görüntülemeden sistem tepsisine küçülür.` |
| 227 | `MainWindow.xaml:1273` (`TxtAasStatus`) | `AASSvc Donanım Köprüsü Aktif (Port 46933)` | Technical status | `AASSvc Donanım Köprüsü Aktif (Port 46933)` |
| 228 | `MainWindow.xaml:1276` (`TextBlock`) | `MUX Switch Koruma Modu: Devre Dışı (Donanım Korumalı)` | Technical status | `MUX Anahtarı: Otomatik / Donanım Korumalı` |
| 229 | `MainWindow.xaml:1286` (`TxtToastIcon`) | `✓` | Unicode checkmark symbol | `[OK]` *(or clean UI glyph)* |
| 230 | `MainWindow.xaml:1287` (`TxtToastMessage`) | `İşlem başarıyla tamamlandı.` | Standard confirmation | `İşlem tamamlandı.` |

### Category 7: OSD Overlay Window (`OsdOverlayWindow.xaml` and `.cs`)

| # | File & Line Number / Element | Current String | Issue Identified | Proposed Technical Human-Written Replacement String |
|---|-----------------------------|----------------|------------------|-----------------------------------------------------|
| 231 | `OsdOverlayWindow.xaml:4` (`Title`) | `NitroLite OSD` | Window title | `NitroLite OSD` |
| 232 | `OsdOverlayWindow.xaml:22` (`TextBlock`) | `💻 CPU: ` | Anti-pattern #3: Emoji in HUD metric | `CPU: ` |
| 233 | `OsdOverlayWindow.xaml:34` (`TextBlock`) | `🎮 GPU: ` | Anti-pattern #3: Emoji in HUD metric | `GPU: ` |
| 234 | `OsdOverlayWindow.xaml:46` (`TextBlock`) | `⚡ MOD: ` | Anti-pattern #3: Emoji in HUD metric | `MOD: ` |
| 235 | `OsdOverlayWindow.xaml:49` (`TextBlock`) | `RAM: ` | Clean technical HUD label | `RAM: ` |
| 236 | `OsdOverlayWindow.xaml.cs:151` | `$"FAN: {TelemetryFormatter.FormatFanSpeed(data.CpuFanSpeed)}"` | Clean technical HUD label | `$"FAN: {TelemetryFormatter.FormatFanSpeed(data.CpuFanSpeed)}"` |

### Category 8: System Tray Menu & Tooltips (`MainWindow.xaml.cs`)

| # | File & Line Number / Element | Current String | Issue Identified | Proposed Technical Human-Written Replacement String |
|---|-----------------------------|----------------|------------------|-----------------------------------------------------|
| 237 | `MainWindow.xaml.cs:109` (`_notifyIcon.Text`) | `NitroLite - Acer Nitro Suite` | Spaced hyphen separator used as em-dash | `NitroLite` |
| 238 | `MainWindow.xaml.cs:113` (`menu.Items.Add`) | `🗔 Göster / Aç` | Anti-pattern #3: Emoji in menu | `Göster / Aç` |
| 239 | `MainWindow.xaml.cs:116` (`menu.Items.Add`) | `🌙 Silent Mod` | Anti-pattern #3: Emoji in menu | `Sessiz Mod (Silent)` |
| 240 | `MainWindow.xaml.cs:117` (`menu.Items.Add`) | `⚖️ Balanced Mod` | Anti-pattern #3: Emoji in menu | `Dengeli Mod (Balanced)` |
| 241 | `MainWindow.xaml.cs:118` (`menu.Items.Add`) | `🔥 Power Mod` | Anti-pattern #3: Emoji in menu | `Performans Modu (Power)` |
| 242 | `MainWindow.xaml.cs:119` (`menu.Items.Add`) | `⚡ Turbo Mod` | Anti-pattern #3: Emoji in menu | `Turbo Mod` |
| 243 | `MainWindow.xaml.cs:122` (`menu.Items.Add`) | `❄️ Serin Eco Boost (~68°C)` | Anti-pattern #3: Emoji; colloquial *"Serin Eco"* | `CPU Boost: Devre Dışı` |
| 244 | `MainWindow.xaml.cs:123` (`menu.Items.Add`) | `🚀 Agresif CPU Boost (5.0 GHz)` | Anti-pattern #3: Emoji; colloquial *"Agresif"* | `CPU Boost: Agresif (5.0 GHz)` |
| 245 | `MainWindow.xaml.cs:126` (`menu.Items.Add`) | `❌ Çıkış` | Anti-pattern #3: Emoji in menu | `Çıkış` |
| 246 | `MainWindow.xaml.cs:551` (`_notifyIcon.Text`) | `$"NitroLite: {_currentMode} \| CPU: {data.CpuTemperature}°C \| GPU: {data.GpuTemperature}°C"` | Clean technical status tooltip | `$"NitroLite: {_currentMode} \| CPU: {data.CpuTemperature}°C \| GPU: {data.GpuTemperature}°C"` |

### Category 9: Toast Notifications & Status Updates (`MainWindow.xaml.cs`)

| # | File & Line Number / Element | Current String | Issue Identified | Proposed Technical Human-Written Replacement String |
|---|-----------------------------|----------------|------------------|-----------------------------------------------------|
| 247 | `MainWindow.xaml.cs:463` | `TxtToastIcon.Text = isSuccess ? "✓" : "ℹ";` | Emoji / icon glyphs | `TxtToastIcon.Text = isSuccess ? "●" : "!";` |
| 248 | `MainWindow.xaml.cs:565` | `ShowToast($"⚡ Akıllı RAM Temizliği: {freedStr} serbest bırakıldı");` | Anti-pattern #3: Emoji; buzzword *"Akıllı"* | `ShowToast($"Bellek temizlendi: {freedStr} alan boşaltıldı.");` |
| 249 | `MainWindow.xaml.cs:568` | `TxtRamCleanStatus.Text = $" ({freedStr} Temizlendi)";` | Status badge | `TxtRamCleanStatus.Text = $" ({freedStr} Boşaltıldı)";` |
| 250 | `MainWindow.xaml.cs:597` | `TxtAasStatus.Text = "AASSvc Bağlantısı Bekleniyor...";` | Status text | `TxtAasStatus.Text = "AASSvc bağlantısı bekleniyor...";` |
| 251 | `MainWindow.xaml.cs:735` | `ShowToast($"OSD Konumu: {cornerDesc}");` | Clean technical notification | `ShowToast($"OSD konumu değiştirildi: {cornerDesc}");` |
| 252 | `MainWindow.xaml.cs:757` | `BtnToggleOsd.Content = "🖥️ OSD Katmanı: AÇIK (Kapat)";` | Anti-pattern #3: Emoji in button | `BtnToggleOsd.Content = "OSD Katmanı: Açık (Kapat)";` |
| 253 | `MainWindow.xaml.cs:758` | `ShowToast("Ekran Üstü OSD Katmanı Açıldı");` | Clean technical notification | `ShowToast("Ekran üstü gösterge etkinleştirildi.");` |
| 254 | `MainWindow.xaml.cs:765` | `BtnToggleOsd.Content = "🖥️ Ekran Üstü Bilgi Göstergesi (OSD)";` | Anti-pattern #3: Emoji in button | `BtnToggleOsd.Content = "Ekran Üstü Gösterge (OSD)";` |
| 255 | `MainWindow.xaml.cs:766` | `ShowToast("OSD Katmanı Kapatıldı");` | Clean technical notification | `ShowToast("Ekran üstü gösterge kapatıldı.");` |
| 256 | `MainWindow.xaml.cs:781` | `TxtBoostStatusBadge.Text = " (Serin Eco ~68°C)";` | Colloquial *"Serin Eco"* | `TxtBoostStatusBadge.Text = " (Devre Dışı)";` |
| 257 | `MainWindow.xaml.cs:785` | `TxtBoostStatusBadge.Text = " (Dengeli Akıllı ~78°C)";` | Buzzword *"Akıllı"* | `TxtBoostStatusBadge.Text = " (Dengeli)";` |
| 258 | `MainWindow.xaml.cs:789` | `TxtBoostStatusBadge.Text = " (5.0 GHz Agresif ~88°C)";` | Informal phrasing | `TxtBoostStatusBadge.Text = " (Agresif / 5.0 GHz)";` |
| 259 | `MainWindow.xaml.cs:810` | `ShowToast($"CPU modu değiştirilirken hata: {ex.Message}", false);` | Technical error | `ShowToast($"CPU modu uygulanamadı: {ex.Message}", false);` |
| 260 | `MainWindow.xaml.cs:820` | `ShowToast($"CPU Modu: {mode}");` | Technical status | `ShowToast($"CPU boost modu: {mode}");` |
| 261 | `MainWindow.xaml.cs:950` | `TxtGameStatus.Text = $"🎮 Aktif: {profile.DisplayName.ToUpperInvariant()} ({profile.TargetMode} + {boostName})";` | Anti-pattern #3: Emoji | `TxtGameStatus.Text = $"Aktif: {profile.DisplayName.ToUpperInvariant()} ({profile.TargetMode} / {boostName})";` |
| 262 | `MainWindow.xaml.cs:951` | `ShowToast($"'{profile.DisplayName}' ayarları kaydedildi ve oyuna uygulandı!");` | Exclamation mark | `ShowToast($"'{profile.DisplayName}' profili kaydedildi ve uygulandı.");` |
| 263 | `MainWindow.xaml.cs:955` | `ShowToast($"'{profile.DisplayName}' oyun profili başarıyla kaydedildi!");` | Exclamation mark | `ShowToast($"'{profile.DisplayName}' oyun profili kaydedildi.");` |
| 264 | `MainWindow.xaml.cs:960` | `ShowToast($"Oyun profili kaydedilirken hata: {ex.Message}", false);` | Error toast | `ShowToast($"Profil kaydedilemedi: {ex.Message}", false);` |
| 265 | `MainWindow.xaml.cs:968` | `ShowToast(found > 0 ? $"{found} yeni oyun tespit edildi ve eklendi!" : "Tüm oyunlar zaten listenizde mevcut.");` | Exclamation mark; chatty | `ShowToast(found > 0 ? $"{found} yeni oyun profili eklendi." : "Yeni yüklü oyun tespit edilmedi.");` |
| 266 | `MainWindow.xaml.cs:975` | `Title = "Özel Oyun .exe Dosyasını Seçin"` | Dialog title | `Title = "Çalıştırılabilir Oyun Dosyasını Seçin"` |
| 267 | `MainWindow.xaml.cs:998` | `ShowToast($"'{cleanName}' oyunu eklendi!");` | Exclamation mark | `ShowToast($"'{cleanName}' profili eklendi.");` |
| 268 | `MainWindow.xaml.cs:1011` | `MessageBox: "'{profile.DisplayName}' oyununu listeden silmek istediğinize emin misiniz?", "Oyunu Sil"` | Confirmation prompt | `MessageBox: "'{profile.DisplayName}' oyun profilini silmek istiyor musunuz?", "Profili Sil"` |
| 269 | `MainWindow.xaml.cs:1018` | `ShowToast($"'{profile.DisplayName}' listeden silindi.");` | Clean status | `ShowToast($"'{profile.DisplayName}' profili silindi.");` |
| 270 | `MainWindow.xaml.cs:1047` | `TxtGameStatus.Text = $"🎮 Aktif: {profile.DisplayName.ToUpperInvariant()} ({profile.TargetMode} + {boostName})";` | Anti-pattern #3: Emoji | `TxtGameStatus.Text = $"Aktif: {profile.DisplayName.ToUpperInvariant()} ({profile.TargetMode} / {boostName})";` |
| 271 | `MainWindow.xaml.cs:1062` | `ShowToast($"🎮 {profile.DisplayName}: {freedStr} RAM önbelleği temizlendi");` | Anti-pattern #3: Emoji | `ShowToast($"{profile.DisplayName}: {freedStr} bellek önbelleği temizlendi.");` |
| 272 | `MainWindow.xaml.cs:1066` | `BalloonTip: "NitroLite Oyun Modu Aktif", $"{profile.DisplayName} algılandı!\nMod: {profile.TargetMode} \| CPU: {boostName}"` | Exclamation mark | `BalloonTip: "NitroLite", $"{profile.DisplayName} algılandı. Profil uygulandı: {profile.TargetMode} / {boostName}"` |
| 273 | `MainWindow.xaml.cs:1089` | `TxtGameStatus.Text = "Durum: Beklemede (Oyun tespit edilmedi)";` | Status | `TxtGameStatus.Text = "Durum: Beklemede (Aktif oyun yok)";` |
| 274 | `MainWindow.xaml.cs:1100` | `BalloonTip: "NitroLite", $"{profile.DisplayName} kapandı. Masaüstü modunuza ({_savedModeBeforeGame}) geri dönüldü."` | Casual phrasing | `BalloonTip: "NitroLite", $"{profile.DisplayName} sonlandırıldı. Varsayılan profil ({_savedModeBeforeGame}) geri yüklendi."` |
| 275 | `MainWindow.xaml.cs:1107` | `ShowToast($"Oyun çıkış profili geri yüklenirken hata: {ex.Message}", false);` | Technical error | `ShowToast($"Profil geri yüklenemedi: {ex.Message}", false);` |
| 276 | `MainWindow.xaml.cs:1201` | `ShowToast($"RGB profili uygulanamadı: {ex.Message}", false);` | Technical error | `ShowToast($"Aydınlatma profili uygulanamadı: {ex.Message}", false);` |
| 277 | `MainWindow.xaml.cs:1229` | `name = $"Özel Profil {DateTime.Now:HH:mm}";` | Default preset name | `name = $"Profil {DateTime.Now:HH:mm}";` |
| 278 | `MainWindow.xaml.cs:1243` | `ShowToast($"'{name}' RGB profili kaydedildi!");` | Exclamation mark | `ShowToast($"'{name}' aydınlatma profili kaydedildi.");` |
| 279 | `MainWindow.xaml.cs:1259` | `ShowToast($"'{selectedName}' profili silindi.");` | Clean status | `ShowToast($"'{selectedName}' profili silindi.");` |
| 280 | `MainWindow.xaml.cs:1263` | `ShowToast("Hazır fabrika profilleri silinemez.", isSuccess: false);` | Informational toast | `ShowToast("Varsayılan fabrika profilleri silinemez.", false);` |
| 281 | `MainWindow.xaml.cs:1300` | `ShowToast("Donanım, pil ve SSD sağlık verileri güncellendi");` | Clean toast | `ShowToast("Donanım sağlık telemetrisi güncellendi.");` |
| 282 | `MainWindow.xaml.cs:1304` | `ShowToast($"Sağlık verileri güncellenirken hata: {ex.Message}", false);` | Error toast | `ShowToast($"Telemetri güncellenemedi: {ex.Message}", false);` |
| 283 | `MainWindow.xaml.cs:1323` | `TxtDrive0Letter.Text = letter.Contains(',') ? $"💾 [{letter}] " : $"💾 [{letter}:] ";` | Anti-pattern #3: Emoji | `TxtDrive0Letter.Text = letter.Contains(',') ? $"[{letter}] " : $"[{letter}:] ";` |
| 284 | `MainWindow.xaml.cs:1341` | `TxtDrive0HealthBadge.Text = $"🟢 SAĞLIK: %{d0.EstHealthPercent} ({d0.HealthDesc})";` | Anti-pattern #3: Emoji; buzzword | `TxtDrive0HealthBadge.Text = $"Sağlık: %{d0.EstHealthPercent} ({d0.HealthDesc})";` |
| 285 | `MainWindow.xaml.cs:1364` | `TxtDrive1Letter.Text = letter.Contains(',') ? $"💾 [{letter}] " : $"💾 [{letter}:] ";` | Anti-pattern #3: Emoji | `TxtDrive1Letter.Text = letter.Contains(',') ? $"[{letter}] " : $"[{letter}:] ";` |
| 286 | `MainWindow.xaml.cs:1382` | `TxtDrive1HealthBadge.Text = $"🟢 SAĞLIK: %{d1.EstHealthPercent} ({d1.HealthDesc})";` | Anti-pattern #3: Emoji; buzzword | `TxtDrive1HealthBadge.Text = $"Sağlık: %{d1.EstHealthPercent} ({d1.HealthDesc})";` |
| 287 | `MainWindow.xaml.cs:1401` | `TxtStorageBadge.Text = allHealthy ? "Tüm Sürücüler Sağlıklı" : "Sürücü Uyarısı Mevcut";` | Technical status | `TxtStorageBadge.Text = allHealthy ? "Tüm Sürücüler Sağlıklı" : "Sürücü Uyarısı Mevcut";` |
| 288 | `MainWindow.xaml.cs:1433` | `TxtStabilitySlope.Text = $"Eğim: {TelemetryFormatter.FormatThermalSlope(trend.DegradationSlopePerDay)}{slopeState}";` | Technical metric | `TxtStabilitySlope.Text = $"Eğim: {TelemetryFormatter.FormatThermalSlope(trend.DegradationSlopePerDay)}{slopeState}";` |
| 289 | `MainWindow.xaml.cs:1438` | `TxtStabilityBadge.Text = trend.StatusLevel == ThermalStatusLevel.Excellent ? "Optimum İletim" : (trend.StatusLevel == ThermalStatusLevel.Good ? "Kararlı" : "Bakım Gerekli");` | Buzzword *"Optimum"* | `TxtStabilityBadge.Text = trend.StatusLevel == ThermalStatusLevel.Excellent ? "Nominal" : (trend.StatusLevel == ThermalStatusLevel.Good ? "Kararlı" : "Bakım Gerekli");` |
| 290 | `MainWindow.xaml.cs:1461` | `TxtThermalResistance.Text = $"Direnç: {report.ThermalResistanceRatio:F2} °C/W{tag}";` | Technical readout | `TxtThermalResistance.Text = $"Direnç: {report.ThermalResistanceRatio:F2} °C/W{tag}";` |
| 291 | `MainWindow.xaml.cs:1519-1520` | `TxtBatStateDesc.Text = status.IsPluggedIn ? (status.IsCharging ? "Adaptöre Takılı (Şarj Oluyor)" : "Adaptöre Takılı (Dolu / Sınırda)") : "Pilde Çalışıyor (Deşarj)";` | Clear technical states | `TxtBatStateDesc.Text = status.IsPluggedIn ? (status.IsCharging ? "Adaptöre Bağlı (Şarj Ediliyor)" : "Adaptöre Bağlı (Sınırda)") : "Pilde Çalışıyor (Deşarj)";` |
| 292 | `MainWindow.xaml.cs:1548` | `TxtOverchargeMsg.Text = $"Pil seviyeniz %{status.BatteryPercent} oldu (%80 sınırı aşıldı). Adaptörü çıkarıp Güvenli Deşarj başlatabilirsiniz.";` | Conversational | `TxtOverchargeMsg.Text = $"Pil seviyesi %{status.BatteryPercent} seviyesine ulaştı. Adaptörü çıkararak deşarj işlemini başlatabilirsiniz.";` |
| 293 | `MainWindow.xaml.cs:1564` | `BalloonTip: "NitroLite %80 Şarj Uyarısı", $"Piliniz %{percent} seviyesine ulaştı. Adaptörü çıkarıp Güvenli Deşarj modunu kullanabilirsiniz."` | Conversational | `BalloonTip: "NitroLite", $"Pil %{percent} seviyesine ulaştı (%80 eşiği aşıldı)."` |
| 294 | `MainWindow.xaml.cs:1574` | `BtnToggleDischarge.Content = "⚡ %80'e Güvenli Deşarjı Başlat";` | Anti-pattern #3: Emoji | `BtnToggleDischarge.Content = "Deşarj İşlemini Başlat";` |
| 295 | `MainWindow.xaml.cs:1576` | `TxtDischargeStatus.Text = "✅ HEDEF %80'E ULAŞILDI! Lütfen adaptörü takınız.";` | Anti-pattern #3: Emoji; all-caps; exclamation | `TxtDischargeStatus.Text = "Hedef seviyeye (%80) ulaşıldı. Şarj adaptörünü bağlayabilirsiniz.";` |
| 296 | `MainWindow.xaml.cs:1579` | `BalloonTip: "NitroLite Hedef Ulaşıldı", "Pil %80 seviyesine indi! Şarj adaptörünüzü tekrar takabilirsiniz. Sınır %80'de korunacaktır."` | Exclamation mark | `BalloonTip: "NitroLite", "Pil seviyesi %80 sınırına ulaştı. Adaptörü bağlayabilirsiniz."` |
| 297 | `MainWindow.xaml.cs:1580` | `ShowToast("Tebrikler! Pil seviyesi tam %80'e indirildi.");` | Anti-pattern #17: Fluffy congratulations *"Tebrikler!"* | `ShowToast("Pil seviyesi %80 hedef sınırına indirildi.");` |
| 298 | `MainWindow.xaml.cs:1750` | `ShowToast($"Çalışma modu uygulanamadı: {ex.Message}", false);` | Technical error | `ShowToast($"Çalışma modu uygulanamadı: {ex.Message}", false);` |
| 299 | `MainWindow.xaml.cs:1760` | `ShowToast($"Çalışma Modu: {mode}");` | Technical status | `ShowToast($"Çalışma modu: {mode}");` |
| 300 | `MainWindow.xaml.cs:1769` | `ShowToast("Otomatik Fan Eğrisi Devrede");` | Status | `ShowToast("Otomatik fan eğrisi etkinleştirildi.");` |
| 301 | `MainWindow.xaml.cs:1773` | `ShowToast($"Fan modu uygulanamadı: {ex.Message}", false);` | Technical error | `ShowToast($"Fan modu uygulanamadı: {ex.Message}", false);` |
| 302 | `MainWindow.xaml.cs:1782` | `ShowToast("Maksimum Fan Hızı Devrede");` | Status | `ShowToast("Maksimum fan devri etkinleştirildi.");` |
| 303 | `MainWindow.xaml.cs:1786` | `ShowToast($"Fan modu uygulanamadı: {ex.Message}", false);` | Technical error | `ShowToast($"Fan modu uygulanamadı: {ex.Message}", false);` |
| 304 | `MainWindow.xaml.cs:1820` | `ShowToast($"RGB rengi ayarlanamadı: {ex.Message}", false);` | Technical error | `ShowToast($"Aydınlatma rengi ayarlanamadı: {ex.Message}", false);` |
| 305 | `MainWindow.xaml.cs:1832` | `ShowToast($"Işıklar kapatılırken hata: {ex.Message}", false);` | Technical error | `ShowToast($"Aydınlatma kapatılamadı: {ex.Message}", false);` |
| 306 | `MainWindow.xaml.cs:1896` | `ShowToast($"RGB efekti ayarlanamadı: {ex.Message}", false);` | Technical error | `ShowToast($"Aydınlatma efekti ayarlanamadı: {ex.Message}", false);` |
| 307 | `MainWindow.xaml.cs:1933` | `ShowToast($"Parlaklık ayarlanamadı: {ex.Message}", false);` | Technical error | `ShowToast($"Parlaklık uygulanamadı: {ex.Message}", false);` |
| 308 | `MainWindow.xaml.cs:1950` | `ShowToast($"Hız ayarlanamadı: {ex.Message}", false);` | Technical error | `ShowToast($"Efekt hızı uygulanamadı: {ex.Message}", false);` |
| 309 | `MainWindow.xaml.cs:1964` | `ShowToast($"Zaman aşımı ayarlanamadı: {ex.Message}", false);` | Technical error | `ShowToast($"Zaman aşımı uygulanamadı: {ex.Message}", false);` |
| 310 | `MainWindow.xaml.cs:1983` | `BtnToggleDischarge.Content = "⚡ %80'e Güvenli Deşarjı Başlat";` | Anti-pattern #3: Emoji | `BtnToggleDischarge.Content = "Deşarj İşlemini Başlat";` |
| 311 | `MainWindow.xaml.cs:1985` | `TxtDischargeStatus.Text = "Durum: Durduruldu.";` | Technical status | `TxtDischargeStatus.Text = "Durum: Durduruldu.";` |
| 312 | `MainWindow.xaml.cs:1994` | `MessageBox: "DİKKAT: Adaptörünüz takılıyken laptop donanımı bataryayı boşaltamaz!\n\nLütfen şarj adaptörünün fişini çekin, ardından 'Evet'e tıklayarak deşarjı başlatın.", "Adaptörü Çıkarın"` | Conversational / informal warning | `MessageBox: "Adaptör bağlıyken batarya deşarj edilemez.\n\nLütfen adaptör bağlantısını kesin ve işlemi başlatmak için 'Evet' seçeneğine tıklayın.", "Adaptör Bağlantısı"` |
| 313 | `MainWindow.xaml.cs:2002` | `ShowToast($"Şarjınız zaten %{st.BatteryPercent} (Hedef %80 veya altında)", isSuccess: false);` | Casual | `ShowToast($"Pil seviyesi (%{st.BatteryPercent}) zaten hedef sınırın (%80) altındadır.", false);` |
| 314 | `MainWindow.xaml.cs:2007` | `BtnToggleDischarge.Content = "🛑 Deşarjı Durdur";` | Anti-pattern #3: Emoji in button | `BtnToggleDischarge.Content = "Deşarjı Durdur";` |
| 315 | `MainWindow.xaml.cs:2009` | `TxtDischargeStatus.Text = "⚡ GÜVENLİ DEŞARJ AKTİF: Pil %80'e indiriliyor...";` | Anti-pattern #3: Emoji; all-caps | `TxtDischargeStatus.Text = "Deşarj işlemi aktif: Pil seviyesi %80 sınırına indiriliyor...";` |
| 316 | `MainWindow.xaml.cs:2020` | `TxtNitroSenseRunningBadge.Text = isNsRunning ? " (Arka Planda Aktif - RAM Yiyor!)" : " (Engellendi / Kapalı)";` | Anti-pattern #16: Hyphen separator; slang *"RAM Yiyor!"* | `TxtNitroSenseRunningBadge.Text = isNsRunning ? " (Arka Planda Aktif)" : " (Devre Dışı)";` |
| 317 | `MainWindow.xaml.cs:2032` | `ShowToast($"NitroSense engellendi. ({killed} işlem sonlandırıldı)");` | Clean technical toast | `ShowToast($"NitroSense sonlandırıldı ({killed} işlem).");` |
| 318 | `MainWindow.xaml.cs:2040` | `MessageBox: "Gereksiz 6 adet arka plan telemetri servisi devre dışı bırakılacak ve ~214 MB RAM tasarrufu sağlanacaktır.\n\nDonanım kontrolleri (Fan, Turbo, RGB, %80 pil) korunacaktır. Devam edilsin mi?", "Debloat Onayı"` | Conversational confirmation | `MessageBox: "Altı adet OEM telemetri servisi devre dışı bırakılacak ve yaklaşık 214 MB bellek tasarrufu sağlanacaktır.\n\nDonanım kontrolleri etkilenmeyecektir. Devam edilsin mi?", "Servis Yapılandırması"` |
| 319 | `MainWindow.xaml.cs:2049` | `ShowToast("Telemetri servisleri devre dışı bırakıldı! (~214 MB tasarruf)");` | Exclamation mark | `ShowToast("Telemetri servisleri devre dışı bırakıldı (~214 MB).");` |
| 320 | `MainWindow.xaml.cs:2055` | `ShowToast($"Debloat uygulanırken hata: {ex.Message}", false);` | Technical error | `ShowToast($"Servisler yapılandırılamadı: {ex.Message}", false);` |
| 321 | `MainWindow.xaml.cs:2067` | `ShowToast("Servisler fabrika durumuna geri döndürüldü.");` | Technical status | `ShowToast("Varsayılan servis yapılandırması geri yüklendi.");` |
| 322 | `MainWindow.xaml.cs:2073` | `ShowToast($"Servisler geri yüklenirken hata: {ex.Message}", false);` | Technical error | `ShowToast($"Servisler geri yüklenemedi: {ex.Message}", false);` |
| 323 | `MainWindow.xaml.cs:2080` | `ShowToast(ChkAutoStart.IsChecked == true ? "NitroLite başlangıçta çalışma aktif" : "Başlangıçta çalışma kapatıldı");` | Clear toast | `ShowToast(ChkAutoStart.IsChecked == true ? "Başlangıçta otomatik çalıştırma etkin" : "Başlangıçta çalıştırma devre dışı");` |
| 324 | `MainWindow.xaml.cs:2089` | `ShowToast(_appSettings.StartMinimized ? "Sistem tepsisinde sessiz başlatma aktif" : "Normal başlangıç aktif");` | Clear toast | `ShowToast(_appSettings.StartMinimized ? "Sistem tepsisinde başlatma etkin" : "Normal başlangıç etkin");` |
| 325 | `MainWindow.xaml.cs:2102` | `ShowToast($"✓ {freedStr} RAM önbelleği temizlendi! ({res.ProcessesOptimized} işlem)");` | Checkmark glyph; exclamation mark | `ShowToast($"{freedStr} bellek temizlendi ({res.ProcessesOptimized} işlem).");` |
| 326 | `MainWindow.xaml.cs:2105` | `TxtRamCleanStatus.Text = $" ({freedStr} Temizlendi)";` | Status badge | `TxtRamCleanStatus.Text = $" ({freedStr} Boşaltıldı)";` |
| 327 | `MainWindow.xaml.cs:2111` | `ShowToast("✓ RAM önbelleği zaten tertemiz ve ferah.");` | Anti-pattern #17: Robotic emotional fluff *"tertemiz ve ferah"* | `ShowToast("Bellek önbelleği optimize durumda.");` |
| 328 | `MainWindow.xaml.cs:2114` | `TxtRamCleanStatus.Text = " (Zaten Optimize)";` | Status badge | `TxtRamCleanStatus.Text = " (Optimize)";` |
| 329 | `MainWindow.xaml.cs:2121` | `ShowToast($"RAM temizleme sırasında hata: {ex.Message}", false);` | Technical error | `ShowToast($"Bellek temizlenemedi: {ex.Message}", false);` |
| 330 | `MainWindow.xaml.cs:2130` | `ShowToast(_appSettings.AutoRamCleanerEnabled ? "Otomatik Akıllı RAM Temizleyici Aktif" : "Otomatik RAM Temizleyici Kapatıldı");` | Buzzword *"Akıllı"* | `ShowToast(_appSettings.AutoRamCleanerEnabled ? "Otomatik bellek temizleme etkin" : "Otomatik bellek temizleme devre dışı");` |

### Category 10: Backend Services & Core Models

| # | File & Line Number / Element | Current String | Issue Identified | Proposed Technical Replacement String (and Test Impact) |
|---|-----------------------------|----------------|------------------|---------------------------------------------------------|
| 331 | `Core/RgbProfileService.cs:78` | `💎 Cyberpunk (Cyan & Red)` | Anti-pattern #3: Emoji in preset name | `Cyberpunk (Cyan & Kırmızı)` |
| 332 | `Core/RgbProfileService.cs:89` | `🌊 Okyanus Mavisi (Ocean Blue)` | Anti-pattern #3: Emoji in preset name | `Okyanus Mavisi` |
| 333 | `Core/RgbProfileService.cs:100` | `🔥 Cehennem Ateşi (Inferno)` | Anti-pattern #3: Emoji in preset name | `Kırmızı & Turuncu (Inferno)` |
| 334 | `Core/RgbProfileService.cs:111` | `🌲 Matrix Neon Yeşil` | Anti-pattern #3: Emoji in preset name | `Matrix Yeşil` |
| 335 | `Core/RgbProfileService.cs:122` | `💜 Vaporwave Mor & Pembe` | Anti-pattern #3: Emoji in preset name | `Vaporwave (Mor & Pembe)` |
| 336 | `Core/RgbProfileService.cs:133` | `⚡ Saf Beyaz Işık` | Anti-pattern #3: Emoji in preset name | `Saf Beyaz` |
| 337 | `Core/RgbProfileService.cs:144` | `🌑 Işık Kapalı (Blackout)` | Anti-pattern #3: Emoji in preset name | `Işık Kapalı (Blackout)` |
| 338 | `Core/ThermalHealthService.cs:33, 311` | `Mükemmel (Termal Macun & Putty Kondisyonu Harika)` | Buzzwords *"Mükemmel"*, *"Harika"*. *Note: `ThermalHealthServiceTests` asserts `.Contains("Dikkat") \|\| .Contains("İyi Seviyede")` for degraded states.* | `Normal (Termal Direnç Nominal)` *(Note: Keep "İyi Seviyede" in tier 2 for test compatibility)* |
| 339 | `Core/ThermalHealthService.cs:41, 312` | `Termal macun ve putty yerleşimi çok kararlı. Bakım kondisyonunu koruyor, macun değişimi gerekmiyor.` | Wordy prose; casual tone | `Termal arayüz iletkenliği nominal sınırlar içinde. Isı transfer direnci kararlı.` |
| 340 | `Core/ThermalHealthService.cs:316` | `İyi Seviyede (Kararlı Isı İletimi)` | *Checked in tests: Contains("İyi Seviyede")* | `İyi Seviyede (Kararlı Isı Transferi)` |
| 341 | `Core/ThermalHealthService.cs:317` | `Termal verimlilik gayet iyi. Fan ızgaralarına hava tutularak hafif temizlik yapılması önerilir.` | Conversational phrasing | `Termal direnç kabul edilebilir düzeyde. Izgara temizliği önerilir.` |
| 342 | `Core/ThermalHealthService.cs:321` | `Dikkat (Termal Verimlilik Azaldı)` | *Checked in tests: Contains("Dikkat")* | `Dikkat (Termal Verimlilik Azaldı)` |
| 343 | `Core/ThermalHealthService.cs:322` | `İşlemci ısı transferinde belirgin düşüş tespit edildi. Fan ızgarası temizliği veya macun yenileme zamanı gelmiş olabilir.` | Conversational phrasing | `İşlemci termal direnci yükseldi. Soğutma bloğu temizliği veya termal arayüz bakımı önerilir.` |
| 344 | `Core/ThermalHealthService.cs:387, 446` | `Kararlı, Macun kondisyonu mükemmel` | Anti-pattern #17: Buzzword *"mükemmel"*. *Warning: Tested in `ThermalHealthServiceTests.cs:213` via `Assert.Equal("Kararlı, Macun kondisyonu mükemmel", trend.StatusText);` and Line 260 `Assert.Contains("Kararlı", trend.StatusText);`* | `Kararlı, Termal iletkenlik nominal` *(Crucial: Update `ThermalHealthServiceTests.cs:213` in sync, or decouple in UI layer)* |
| 345 | `Core/ThermalHealthService.cs:388, 451` | `İyi Seviyede, Hafif yaşlanma / stabil ısı iletimi` | Informal phrasing | `İyi Seviyede, Kararlı ısı iletimi` |
| 346 | `Core/ThermalHealthService.cs:388, 456` | `Performans Kaybı, Termal direnç artış eğiliminde` | Technical status | `Direnç Artışı, Isı transferinde düşüş` |
| 347 | `Core/ThermalHealthService.cs:388, 461` | `Kritik, Termal macun kurumuş / bakım gerekli` | Dramatic wording | `Kritik, Yüksek termal direnç (bakım gerekli)` |
| 348 | `Core/StorageHealthService.cs:22, 267, 405` | `Sağlıklı (Kondisyon Mükemmel)` | Anti-pattern #17: Buzzword *"Mükemmel"*. *Warning: Tested in `StorageHealthServiceTests.cs:64, 65, 93` via `Assert.Equal(expectedDesc, healthDesc);`* | `Sağlıklı (Kondisyon İyi)` or `Sağlıklı (Nominal)` *(Crucial: Update `StorageHealthServiceTests.cs` theory data in sync, or map in UI)* |
| 349 | `Core/StorageHealthService.cs:267, 439` | `Sağlıklı (Kondisyon İyi)` | *Tested in `StorageHealthServiceTests.cs:66, 67`* | `Sağlıklı (Kondisyon İyi)` |
| 350 | `Core/StorageHealthService.cs:271` | `Uyarı (Sürücü Kontrolü Gerekli)` | *Tested in `StorageHealthServiceTests.cs:82, 94, 110`* | `Uyarı (Sürücü Kontrolü Gerekli)` |
| 351 | `Core/StorageHealthService.cs:275` | `Kritik (Acil Yedekleme Önerilir)` | *Tested in `StorageHealthServiceTests.cs:86, 95, 114`* | `Kritik (Acil Yedekleme Önerilir)` |

---

## Unit Test Impact & Regression Prevention Analysis

A critical responsibility of this specification survey is assessing whether copy edits can cause test failures.

### Direct Test Assertions Identified
1. **`ThermalHealthServiceTests.cs:213`**:
   ```csharp
   Assert.Equal("Kararlı, Macun kondisyonu mükemmel", trend.StatusText);
   ```
   - **Impact**: Directly checks the exact string `"Kararlı, Macun kondisyonu mükemmel"`.
   - **Safe Migration Path**: When modifying `ThermalHealthService.cs` line 387 and line 445 to `"Kararlı, Termal iletkenlik nominal"`, the assertion on line 213 in `ThermalHealthServiceTests.cs` must be updated to match. Notice line 260 asserts `Assert.Contains("Kararlı", trend.StatusText);`, which remains satisfied.
2. **`ThermalHealthServiceTests.cs:47`**:
   ```csharp
   Assert.True(report.PasteHealthStatus.Contains("Dikkat") || report.PasteHealthStatus.Contains("İyi Seviyede"), ...);
   ```
   - **Impact**: Checks that degraded thermal interface returns status containing `"Dikkat"` or `"İyi Seviyede"`.
   - **Safe Migration Path**: Any updated wording must retain the keywords `"Dikkat"` and `"İyi Seviyede"`.
3. **`StorageHealthServiceTests.cs:64-96`**:
   ```csharp
   [InlineData(0, 100, "Sağlıklı (Kondisyon Mükemmel)")]
   [InlineData(5, 95, "Sağlıklı (Kondisyon Mükemmel)")]
   [InlineData(12, 88, "Sağlıklı (Kondisyon İyi)")]
   [InlineData(1, 70, "Uyarı (Sürücü Kontrolü Gerekli)")]
   [InlineData(2, 30, "Kritik (Acil Yedekleme Önerilir)")]
   ```
   - **Impact**: Explicitly asserts exact `healthDesc` strings.
   - **Safe Migration Path**: If `HealthDesc` in `StorageHealthService.cs` is changed from `"Sağlıklı (Kondisyon Mükemmel)"` to `"Sağlıklı (Nominal)"`, the InlineData in `StorageHealthServiceTests.cs` must be simultaneously updated. Alternatively, the UI display layer (`TxtDrive0HealthBadge.Text`) can simply render `"Sağlık: %99 (Normal)"` without altering the underlying service property.
4. **`TelemetryFormatterTests.cs`**:
   - Asserts pure technical telemetry formats (e.g. `"3960 RPM (%66)"`, `"0 °C (Beklemede)"`, `"0 °C (Uyku / D3cold)"`, `"24.5 W (Deşarj Oluyor)"`).
   - None of these contain buzzwords, em-dashes, or emojis. They are already clean, human-written technical strings and should remain untouched.

---

## Architectural Implementation Guidance for Implementers

1. **Decouple Icons from Text**: In XAML, remove unicode emoji characters from `Text="..."` and `Content="..."` attributes. If visual iconography is desired, employ clean WPF vector geometries or dedicated Path elements with muted accent colors rather than colorful mobile emojis.
2. **Eliminate All Em-Dashes**: Use colons, slashes, or line breaks to separate clauses instead of em-dashes (`—`) or spaced hyphens (` - `).
3. **Calibrate Tone for Professional Desktop Utility**: All status strings and toast notifications must deliver concise facts (what changed and the resulting state) without emotional adjectives (*"ferah"*, *"harika"*, *"tertemiz"*) or conversational congratulations (*"Tebrikler!"*).
