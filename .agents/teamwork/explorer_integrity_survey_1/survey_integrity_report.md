# NitroLite UI/Copy Refactoring Integrity Survey & Zero-Regression Boundary

**Auditor:** explorer_integrity_survey_1  
**Target Solution:** `NitroLite.sln` (.NET 10.0-windows WPF)  
**Baseline Test Status:** 201 Passed, 0 Failed, 0 Skipped (100% Green)  
**Date:** 2026-11-04  

---

## 1. Executive Summary & Purpose

This audit establishes a strict, non-negotiable **Zero-Regression Boundary** for the upcoming visual styling overhaul and text de-slopping across `MainWindow.xaml` and `OsdOverlayWindow.xaml`.

NitroLite is an Acer Nitro 16 hardware utility interacting directly with low-level kernel APIs, WMI classes, the Acer Hardware Service bridge (`AASSvc` / `AcerAgent` on port 46933), NVIDIA NVML drivers, Windows power scheme profiles, and an inter-process single-instance wake event. 

The WPF UI relies extensively on **direct code-behind manipulation** (`x:Name` member fields, event handler attributes, `FrameworkElement.Tag` property parsing, and dynamic resource resolution via `FindResource()`). Any accidental removal, renaming, or tag mutation during XAML restructuring will result in either compile-time failures, runtime `NullReferenceException` crashes, silent hardware command failures, or unit test regressions in `NitroLite.Tests`.

---

## 2. Inventory: `MainWindow.xaml` and `MainWindow.xaml.cs`

### 2.1 Code-Behind `x:Name` Inventory (136 Active Controls)

Every control below is accessed directly by identifier in `MainWindow.xaml.cs`. **These elements MUST retain their exact `x:Name` attribute in `MainWindow.xaml`.**

| Category | x:Name | Type in XAML | Code-Behind Purpose & Operations |
|---|---|---|---|
| **Window & Toast** | `ToastBanner` | `Border` | Fade-in/fade-out toast notifications (`Opacity` animated via `DoubleAnimation`). |
| | `TxtToastIcon` | `TextBlock` | Sets status icon ("✓" or "ℹ") and foreground brush. |
| | `TxtToastMessage` | `TextBlock` | Sets toast feedback text. |
| **Titlebar Badges** | `TxtBatteryHeader` | `TextBlock` | Displays battery percentage (`${status.BatteryPercent}%`) in titlebar. |
| | `TxtModeHeader` | `TextBlock` | Displays current mode (`SILENT`, `BALANCED`, `EXTREME`, `TURBO`). |
| **Sidebar Navigation** | `NavModes` | `RadioButton` | Tab 1 selection; checked in `Nav_Checked`. |
| | `NavMonitoring` | `RadioButton` | Tab 2 selection; checked in `Nav_Checked`. |
| | `NavRgb` | `RadioButton` | Tab 3 selection; checked in `Nav_Checked`. |
| | `NavBattery` | `RadioButton` | Tab 4 selection; checked in `Nav_Checked` (triggers thermal & storage refresh). |
| | `NavDebloat` | `RadioButton` | Tab 5 selection; checked in `Nav_Checked`. |
| | `TxtSideCpuTemp` | `TextBlock` | Real-time CPU temperature in sidebar telemetry badge. |
| | `TxtSideGpuTemp` | `TextBlock` | Real-time GPU temperature in sidebar telemetry badge. |
| | `TxtSideCpuFan` | `TextBlock` | Real-time CPU fan RPM in sidebar telemetry badge. |
| **Tab Containers** | `TabModes` | `Grid` | Visibility toggled in `Nav_Checked`. |
| | `TabMonitoring` | `Grid` | Visibility toggled in `Nav_Checked`. |
| | `TabRgb` | `Grid` | Visibility toggled in `Nav_Checked`. |
| | `TabBattery` | `Grid` | Visibility toggled in `Nav_Checked`. |
| | `TabDebloat` | `Grid` | Visibility toggled in `Nav_Checked`. |
| **Operating Modes** | `TxtIndQuiet` | `TextBlock` | Mode selection badge text ("SEÇ" vs "AKTİF") & brush. |
| | `TxtIndDefault` | `TextBlock` | Mode selection badge text ("SEÇ" vs "AKTİF") & brush. |
| | `TxtIndExtreme` | `TextBlock` | Mode selection badge text ("SEÇ" vs "AKTİF") & brush. |
| | `TxtIndTurbo` | `TextBlock` | Mode selection badge text ("SEÇ" vs "AKTİF") & brush. |
| **Fan Telemetry** | `TxtCpuFanRpm` | `TextBlock` | Real-time CPU fan RPM in Mode tab. |
| | `BarCpuFan` | `ProgressBar` | CPU fan RPM gauge (`Maximum="6000"`). |
| | `TxtGpuFanRpm` | `TextBlock` | Real-time GPU fan RPM in Mode tab. |
| | `BarGpuFan` | `ProgressBar` | GPU fan RPM gauge (`Maximum="6000"`). |
| **CPU Boost** | `TxtBoostStatusBadge` | `TextBlock` | Current CPU boost mode status label and color. |
| **Game Detection** | `TxtGameStatus` | `TextBlock` | Live game detection banner text ("Beklemede", "Aktif: CS2", etc.). |
| | `ChkAutoGameMode` | `CheckBox` | Global automatic game mode toggle. |
| | `CmbConfiguredGames` | `ComboBox` | Configured game profiles dropdown list. |
| | `TxtSelectedGameNameBadge`| `TextBlock`| Displays selected game display name. |
| | `TxtSelectedGameExePath` | `TextBlock` | Displays selected game executable file path. |
| | `CmbGameTargetMode` | `ComboBox` | Target operating mode for selected game (uses `Tag`). |
| | `CmbGameTargetCpuBoost` | `ComboBox` | Target CPU boost mode for selected game (uses `Tag`). |
| | `CmbGameTargetFan` | `ComboBox` | Target fan mode for selected game (uses `Tag`). |
| **OSD Overlay** | `BtnToggleOsd` | `Button` | Toggles OSD state; text and background updated in `ToggleOsd`. |
| **Live Telemetry (Tab 2)**| `TxtCpuCoreCount` | `TextBlock` | Core count label (`" ({data.CpuCores} Çekirdek)"`). |
| | `TxtMonCpuTemp` | `TextBlock` | CPU temperature string. |
| | `BarMonCpuTemp` | `ProgressBar` | CPU temperature bar (`Maximum="100"`). |
| | `TxtMonCpuPower` | `TextBlock` | CPU package power in Watts. |
| | `BarMonCpuPower` | `ProgressBar` | CPU power bar (`Maximum="65"`). |
| | `TxtMonCpuUsage` | `TextBlock` | CPU load percentage string. |
| | `BarMonCpuUsage` | `ProgressBar` | CPU usage bar (`Maximum="100"`). |
| | `TxtMonCpuFreqGhz` | `TextBlock` | CPU frequency in GHz. |
| | `TxtMonGpuTemp` | `TextBlock` | GPU temperature string. |
| | `BarMonGpuTemp` | `ProgressBar` | GPU temperature bar (`Maximum="100"`). |
| | `TxtMonGpuPower` | `TextBlock` | GPU TGP power in Watts. |
| | `BarMonGpuPower` | `ProgressBar` | GPU power bar (`Maximum="140"`). |
| | `TxtMonGpuUsage` | `TextBlock` | GPU load percentage string. |
| | `BarMonGpuUsage` | `ProgressBar` | GPU usage bar (`Maximum="100"`). |
| | `TxtMonVram` | `TextBlock` | VRAM usage string. |
| | `BarMonVram` | `ProgressBar` | VRAM usage bar (`Maximum` set dynamically to `VramTotalMB`). |
| | `TxtMonRamDetail` | `TextBlock` | RAM used / total string (`"12.4 GB / 31.9 GB"`). |
| | `TxtMonRamPercent` | `TextBlock` | RAM percentage string. |
| | `BarMonRam` | `ProgressBar` | RAM usage bar (`Maximum="100"`). |
| | `TxtMonSysTemp` | `TextBlock` | Motherboard system temperature (`SYS1_TEMPERATURE`). |
| | `TxtRamCleanStatus` | `TextBlock` | Status text for standby list memory cleaner. |
| | `ChkAutoRamClean` | `CheckBox` | Automatic background RAM cleaner toggle. |
| **Telemetry History Graph**| `CmbChartMetric` | `ComboBox` | Metric selection dropdown (items have `Tag`). |
| | `CanvasHistoryChart`| `Canvas` | Canvas hosting the rolling telemetry graph. |
| | `ChartArea` | `Polygon` | Semi-transparent filled polygon under graph curve. |
| | `ChartLine` | `Polyline` | Antialiased line displaying the metric curve. |
| | `TxtChartCurrentVal` | `TextBlock` | Current reading badge value. |
| | `TxtChartMinVal` | `TextBlock` | 60-second minimum badge value. |
| | `TxtChartMaxVal` | `TextBlock` | 60-second maximum badge value. |
| | `TxtChartAvgVal` | `TextBlock` | 60-second rolling average badge value. |
| **RGB Lighting (Tab 3)**| `PreviewZone1` | `Border` | Keyboard Zone 1 (Left) visual color indicator. |
| | `PreviewZone2` | `Border` | Keyboard Zone 2 (Center-Left) visual color indicator. |
| | `PreviewZone3` | `Border` | Keyboard Zone 3 (Center-Right) visual color indicator. |
| | `PreviewZone4` | `Border` | Keyboard Zone 4 (Numpad) visual color indicator. |
| | `CmbRgbPresets` | `ComboBox` | Presets dropdown (populated from service). |
| | `TxtCustomPresetName`| `TextBox` | Input box for saving custom RGB profiles. |
| | `CmbRgbEffect` | `ComboBox` | Lighting animation effect dropdown. |
| | `ChkBacklightTimeout`| `CheckBox` | 30-second keyboard inactivity timeout toggle. |
| | `TxtBrightnessVal` | `TextBlock` | Brightness reading (`"5 / 9"` or `"0 / 9 (Kapalı)"`). |
| | `SliderBrightness` | `Slider` | Brightness control slider (0-9). |
| | `TxtSpeedVal` | `TextBlock` | Effect speed reading (`"3 / 9"`). |
| | `SliderSpeed` | `Slider` | Effect speed control slider (1-9). |
| **Battery Health (Tab 4)**| `TxtBatLevelBig` | `TextBlock` | Large battery percentage display. |
| | `TxtBatStateDesc` | `TextBlock` | Battery power connection state string. |
| | `BarBatBig` | `ProgressBar` | Battery charge percentage bar. |
| | `TxtBatDischargeRate`| `TextBlock` | Live power flow rate in Watts. |
| | `TxtBatRemainingTime`| `TextBlock` | Estimated remaining battery runtime. |
| | `ChkWatchdog` | `CheckBox` | Active 80% charge threshold limiter toggle. |
| | `TxtDischargeStatus` | `TextBlock` | Status text for safe discharge assistant. |
| | `BtnToggleDischarge` | `Button` | Starts/stops safe background load discharge. |
| | `TxtBatHealthPercent`| `TextBlock` | Battery health percentage from WMI. |
| | `TxtBatWearPercent` | `TextBlock` | Battery wear level percentage from WMI. |
| | `TxtBatFullChargeCap`| `TextBlock` | Full charge capacity in mWh from WMI. |
| | `TxtBatDesignCap` | `TextBlock` | Factory design capacity in mWh from WMI. |
| | `TxtBatVoltage` | `TextBlock` | Pack voltage reading from WMI. |
| | `BannerOvercharge` | `Border` | Overcharge warning alert banner (`Visibility`). |
| | `TxtOverchargeMsg` | `TextBlock` | Overcharge warning message body. |
| **Thermal Health Report**| `TxtThermalBadge` | `TextBlock` | Thermal paste condition summary badge. |
| | `TxtPasteHealthPercent`| `TextBlock`| Thermal conductance health percentage. |
| | `TxtThermalResistance`| `TextBlock`| Thermal resistance ratio (°C/W). |
| | `TxtActiveFanHours` | `TextBlock` | Total active fan operating hours. |
| | `TxtAvgFanRpm` | `TextBlock` | Average lifetime fan RPM. |
| | `TxtPassiveFanHours` | `TextBlock` | Total silent/passive 0 RPM hours. |
| | `TxtFanBearingHealth`| `TextBlock` | Fan motor/bearing remaining life score. |
| | `TxtStabilityDaysSubtitle`| `TextBlock`| Dynamic subtitle for 14/30 day selection. |
| | `RadioTrend14Days` | `RadioButton` | 14-day trend window filter (`Tag="14"`). |
| | `RadioTrend30Days` | `RadioButton` | 30-day trend window filter (`Tag="30"`). |
| | `TxtStabilityScore` | `TextBlock` | OLS regression thermal stability score percentage. |
| | `TxtStabilityStatus`| `TextBlock` | OLS stability trend diagnosis string. |
| | `TxtStabilitySlope` | `TextBlock` | Degradation slope per day string (`+0.001 °C/W/gün`). |
| | `TxtStabilityBadge` | `TextBlock` | Stability evaluation pill badge. |
| | `BarStabilityScore` | `ProgressBar`| Stability score gauge bar. |
| | `TxtThermalRecommendation`| `TextBlock`| Proactive maintenance guidance text. |
| **Sub-Component Matrix** | `TxtSubMoboTemp` | `TextBlock` | Motherboard / VRM ACPI temperature. |
| | `TxtSubSsd1Temp` | `TextBlock` | NVMe SSD 1 (Disk 0) physical temperature. |
| | `TxtSubSsd2Temp` | `TextBlock` | NVMe SSD 2 (Disk 1) physical temperature. |
| | `TxtSubBatteryTemp` | `TextBlock` | Battery pack internal temperature. |
| **Storage Health** | `TxtStorageBadge` | `TextBlock` | All-drives health summary badge. |
| | `CardDrive0` | `Border` | NVMe Drive 0 container card (`Visibility`). |
| | `TxtDrive0Letter` | `TextBlock` | Drive 0 partition letter (`[C:]`). |
| | `TxtDrive0Name` | `TextBlock` | Drive 0 physical model string. |
| | `TxtDrive0Bus` | `TextBlock` | Drive 0 bus type and firmware. |
| | `TxtDrive0Temp` | `TextBlock` | Drive 0 SMART temperature reading. |
| | `TxtDrive0HealthBadge`| `TextBlock`| Drive 0 SMART health percentage badge. |
| | `TxtDrive0Detail` | `TextBlock` | Drive 0 capacity usage string. |
| | `TxtDrive0Free` | `TextBlock` | Drive 0 free space string. |
| | `BarDrive0` | `ProgressBar` | Drive 0 capacity utilization bar. |
| | `CardDrive1` | `Border` | NVMe Drive 1 container card (`Visibility`). |
| | `TxtDrive1Letter` | `TextBlock` | Drive 1 partition letter (`[D:]`). |
| | `TxtDrive1Name` | `TextBlock` | Drive 1 physical model string. |
| | `TxtDrive1Bus` | `TextBlock` | Drive 1 bus type and firmware. |
| | `TxtDrive1Temp` | `TextBlock` | Drive 1 SMART temperature reading. |
| | `TxtDrive1HealthBadge`| `TextBlock`| Drive 1 SMART health percentage badge. |
| | `TxtDrive1Detail` | `TextBlock` | Drive 1 capacity usage string. |
| | `TxtDrive1Free` | `TextBlock` | Drive 1 free space string. |
| | `BarDrive1` | `ProgressBar` | Drive 1 capacity utilization bar. |
| **Debloat & Settings (Tab 5)**| `ListServiceStatuses`| `ItemsControl`| ItemsControl for debloat telemetry service items. |
| | `TxtNitroSenseRunningBadge`| `TextBlock`| Live badge indicating whether NitroSense is running. |
| | `ChkAutoStart` | `CheckBox` | Windows startup registration toggle. |
| | `ChkStartMinimized` | `CheckBox` | Start minimized to system tray toggle. |
| **Status Bar Footer** | `LedAas` | `Ellipse` | AASSvc hardware connection LED (Green/Red fill). |
| | `TxtAasStatus` | `TextBlock` | AASSvc hardware bridge status text. |

### 2.2 Template-Internal and Visual `x:Name` Elements (41 Elements)

These elements appear in `MainWindow.xaml` but are not referenced directly in C# fields:
- `toggleButton`, `border`, `arrow`, `contentPresenter`, `dropDown`, `dropDownBorder`, `PART_Popup`, `ItemBorder` (Control template parts for ComboBox & RadioButtons)
- `BadgeBattery`, `TxtBatteryIcon`, `BadgeMode`, `BadgeGameStatus`, `DischargeStatusBox`, `PanelStorageDrives` (Styling containers)
- `BtnModeQuiet`, `BtnModeDefault`, `BtnModeExtreme`, `BtnModeTurbo` (Targeted via `Click="BtnMode_Click"` and `Tag`)
- `BtnBoostAggressive`, `BtnBoostEfficient`, `BtnBoostDisabled` (Targeted via `Click="BtnBoost_Click"` and `Tag`)
- `BtnFanAuto`, `BtnFanMax` (Targeted via specific Click handlers)
- `BtnScanGames`, `BtnAddCustomGame`, `BtnDeleteGame`, `BtnSaveGameProfile` (Targeted via Click handlers)
- `BtnCleanRamCache`, `BtnCycleOsdCorner`, `BtnSaveCustomPreset`, `BtnDeleteCustomPreset`, `BtnTurnOffLights`, `BtnRefreshBatHealth`, `BtnKillNitroSense` (Targeted via Click handlers)
- `IndQuiet`, `IndDefault`, `IndExtreme`, `IndTurbo` (Parent borders of `TxtInd*`)

---

### 2.3 Event Handlers Binding Table (43 Bindings)

| Event Type | Handler Method Name | Attached Control in XAML | Action in MainWindow.xaml.cs |
|---|---|---|---|
| `MouseLeftButtonDown` | `TitleBar_MouseLeftButtonDown` | Titlebar `Border` | Calls `DragMove()` when left mouse button is pressed. |
| `Click` | `BtnMinimize_Click` | Minimize Button | Saves window position and sets `WindowState.Minimized`. Trims working set. |
| `Click` | `BtnClose_Click` | Close Button | Saves window position and calls `Hide()`. Trims working set. |
| `Checked` | `Nav_Checked` | All 5 Nav RadioButtons | Switches visibility of tabs 1–5; refreshes thermal & storage on tab 4. |
| `Click` | `BtnMode_Click` | 4 Mode Cards | Reads `btn.Tag`, parses `OperatingMode`, calls `SwitchModeAsync`. |
| `Click` | `BtnFanAuto_Click` | Auto Fan Button | Calls `_agentClient.SetFanModeAsync(FanMode.Auto)`. |
| `Click` | `BtnFanMax_Click` | Max Fan Button | Calls `_agentClient.SetFanModeAsync(FanMode.Max)`. |
| `Click` | `BtnBoost_Click` | 3 CPU Boost Buttons | Reads `btn.Tag`, parses `CpuBoostMode`, calls `ApplyCpuBoostModeAsync`. |
| `Checked` / `Unchecked` | `ChkAutoGameMode_Changed` | Game Mode CheckBox | Updates `_gameService.AutoGameModeEnabled`. |
| `SelectionChanged` | `CmbConfiguredGames_SelectionChanged`| Games ComboBox | Updates selected game details and combo selections. |
| `Click` | `BtnScanGames_Click` | Scan Games Button | Executes `_gameService.ScanAndAddInstalledGames()`. |
| `Click` | `BtnAddCustomGame_Click` | Add Game (.exe) | Opens Win32 file picker, saves game profile. |
| `Click` | `BtnDeleteGame_Click` | Delete Game Button | Confirms with MessageBox, deletes profile. |
| `Click` | `BtnSaveGameProfile_Click`| Save Game Profile Button| Reads tags from `CmbGameTarget*`, persists profile, hot-applies if game is running. |
| `Click` | `BtnToggleOsd_Click` | Toggle OSD Button | Calls `ToggleOsd(!_isOsdOpen)`. |
| `Click` | `BtnCycleOsdCorner_Click`| Cycle Corner Button | Cycles OSD between 4 corners (0:TL, 1:TR, 2:BR, 3:BL). |
| `SelectionChanged` | `CmbChartMetric_SelectionChanged`| Chart ComboBox | Reads `item.Tag`, sets `_selectedChartMetric`, renders chart. |
| `Click` | `BtnCleanRamCache_Click`| RAM Cache Button | Calls `_memoryService.CleanMemoryAsync()` and displays freed MB/GB. |
| `Checked` / `Unchecked` | `ChkAutoRamClean_Changed`| Auto RAM CheckBox | Updates `_appSettings.AutoRamCleanerEnabled`. |
| `SelectionChanged` | `CmbRgbPresets_SelectionChanged` | RGB Presets ComboBox | Applies preset effect, colors, brightness, speed to agent client. |
| `Click` | `BtnSaveCustomPreset_Click` | Save Custom RGB Preset | Saves new profile to `_rgbProfileService`. |
| `Click` | `BtnDeleteCustomPreset_Click`| Delete Custom RGB Preset| Deletes non-built-in profile from `_rgbProfileService`. |
| `Click` | `BtnPresetColor_Click` | 7 Preset Color Swatches | Reads `btn.Tag` hex string; `#000000` turns lights off; else sets static color. |
| `Click` | `BtnTurnOffLights_Click`| Turn Off Lights Button | Calls `TurnOffLightsAsync()` (brightness 0, OFF effect, black). |
| `SelectionChanged` | `CmbRgbEffect_SelectionChanged` | RGB Effect ComboBox | Reads `text.Split(' ')[0]`, applies animation effect. |
| `ValueChanged` | `SliderBrightness_ValueChanged` | Brightness Slider | Applies 0-9 brightness level; 0 executes turn off. |
| `ValueChanged` | `SliderSpeed_ValueChanged` | Speed Slider | Applies 1-9 effect speed. |
| `Checked` / `Unchecked` | `ChkBacklightTimeout_Changed` | Inactivity Timeout CheckBox| Calls `_agentClient.SetBacklightTimeoutAsync`. |
| `Checked` / `Unchecked` | `ChkWatchdog_Changed` | Watchdog CheckBox | Enables/disables 80% battery charge limiter enforcement. |
| `Click` | `BtnToggleDischarge_Click` | Safe Discharge Button | Toggles background CPU load to discharge battery down to 80%. |
| `Click` | `BtnRefreshBatHealth_Click`| Refresh Health Button | Manually queries WMI battery, thermal, and storage health. |
| `Checked` | `RadioTrendPeriod_Checked` | 14/30 Days RadioButtons | Reads `rb.Tag` ("14" or "30"), recomputes OLS regression. |
| `Click` | `BtnApplyDebloat_Click` | Apply Debloat Button | Confirms with MessageBox, runs elevated PowerShell script. |
| `Click` | `BtnRestoreServices_Click`| Restore Services Button| Restores Acer telemetries to Automatic start. |
| `Click` | `BtnKillNitroSense_Click` | Kill NitroSense Button | Terminates background `NitroSense.exe` and disables registry trigger. |
| `Checked` / `Unchecked` | `ChkAutoStart_Changed` | AutoStart CheckBox | Writes run registry key for Windows startup. |
| `Checked` / `Unchecked` | `ChkStartMinimized_Changed`| Start Minimized CheckBox| Toggles silent system tray startup in settings. |

---

### 2.4 Resource Dictionary Keys & Dynamic Invocations

`MainWindow.xaml` defines a set of Resource Keys in `<Window.Resources>`. Several are resolved dynamically in C# via `FindResource(...)`.

#### Dynamically Invocated Resource Keys (CRITICAL)
If any of these keys are missing from resources, WPF will throw a fatal `ResourceReferenceKeyNotFoundException`:
1. `"AccentGreen"` — Used for success toasts, balanced mode, low temperatures, healthy telemetry, and battery charging.
2. `"AccentCyan"` — Used for info toasts, quiet mode, normal telemetry, OSD toggle active state, and chart lines.
3. `"AccentRed"` — Used for error toasts, extreme/turbo mode, high temperatures, overcharge alerts, and AAS disconnected.
4. `"TextSecondary"` — Used for muted labels, reset indicators, and default status text.
5. `"TextMuted"` — Used for inactive mode card badges ("SEÇ").

#### Statically Referenced Resource Keys
Required by `{StaticResource ...}` bindings in XAML:
- `BgDark`, `CardBg`, `CardBgHover`, `BorderDark`, `AccentPurple`, `TextPrimary`
- `DarkComboBoxToggleButtonTemplate` (ControlTemplate)
- `CardBorder` (Border Style)
- `NavButton` (RadioButton Style)
- `ModeCardButton` (Button Style)

---

### 2.5 Critical Tag Attributes Invariants

In WPF, when controls share a generic event handler, the code dispatches based on `Tag`. **These `Tag` attributes MUST be preserved verbatim:**

1. **Operating Mode Buttons (`BtnMode_Click`)**:
   - `Tag="Quiet"` -> parses to `OperatingMode.Quiet`
   - `Tag="Default"` -> parses to `OperatingMode.Default`
   - `Tag="Extreme"` -> parses to `OperatingMode.Extreme`
   - `Tag="Turbo"` -> parses to `OperatingMode.Turbo`
2. **CPU Boost Mode Buttons (`BtnBoost_Click`)**:
   - `Tag="Aggressive"` -> parses to `CpuBoostMode.Aggressive`
   - `Tag="Efficient"` -> parses to `CpuBoostMode.Efficient`
   - `Tag="Disabled"` -> parses to `CpuBoostMode.Disabled`
3. **Per-Game Target Mode ComboBoxItems (`CmbGameTargetMode`)**:
   - `Tag="Turbo"`, `Tag="Extreme"`, `Tag="Default"`, `Tag="Quiet"`
4. **Per-Game Target CPU Boost ComboBoxItems (`CmbGameTargetCpuBoost`)**:
   - `Tag="Disabled"`, `Tag="Efficient"`, `Tag="Aggressive"`
5. **Per-Game Target Fan ComboBoxItems (`CmbGameTargetFan`)**:
   - `Tag="Auto"`, `Tag="Max"`
6. **Telemetry History Chart ComboBoxItems (`CmbChartMetric`)**:
   - `Tag="CPU_TEMP"`, `Tag="GPU_TEMP"`, `Tag="CPU_WATT"`, `Tag="GPU_WATT"`, `Tag="CPU_GHZ"`, `Tag="CPU_USAGE"`, `Tag="GPU_USAGE"`, `Tag="RAM_USAGE"`, `Tag="VRAM_GB"`
7. **RGB Preset Color Buttons (`BtnPresetColor_Click`)**:
   - `Tag="#00E5FF"`, `Tag="#FF2A55"`, `Tag="#00FFA3"`, `Tag="#BD00FF"`, `Tag="#FFAA00"`, `Tag="#FFFFFF"`, `Tag="#000000"` (special handled for Turn Off)
8. **Thermal Trend Period RadioButtons (`RadioTrendPeriod_Checked`)**:
   - `Tag="14"`, `Tag="30"`

---

### 2.6 Critical String Prefix Invariants in XAML

In `MainWindow.xaml.cs`:
```csharp
var effectName = text.Split(' ')[0];
```
And:
```csharp
text.StartsWith(effect, StringComparison.OrdinalIgnoreCase)
```
For `CmbRgbEffect`, each `ComboBoxItem.Content` **MUST** begin with the exact uppercase firmware effect token followed by a space or end-of-string:
- `STATIC` (e.g. `STATIC (Sabit Renk)`)
- `WAVE` (e.g. `WAVE (Dalga)`)
- `BREATHING` (e.g. `BREATHING (Nefes)`)
- `NEON` (e.g. `NEON (Neon Geçiş)`)
- `SHIFTING` (e.g. `SHIFTING (Renk Kayması)`)
- `ZOOM` (e.g. `ZOOM (Büyüme)`)
- `METEOR` (e.g. `METEOR (Kayan Işık)`)
- `TWINKLING` (e.g. `TWINKLING (Yıldız Parıltısı)`)
- `OFF` (e.g. `OFF (Işık Kapalı)`)

*If a copy refactoring changes `STATIC (Sabit Renk)` to `Sabit Renk (Static)`, `text.Split(' ')[0]` yields `"Sabit"`, which the Acer hardware EC rejects.*

---

## 3. Inventory: `OsdOverlayWindow.xaml` and `OsdOverlayWindow.xaml.cs`

### 3.1 Code-Behind `x:Name` Inventory (11 Telemetry TextBlocks)

All 11 elements below are updated synchronously every second in `UpdateData(MonitorData data, OperatingMode mode)`:

| x:Name | Expected Content Format | Update Logic in C# |
|---|---|---|
| `TxtOsdCpuTemp` | `68 °C` or `0 °C (Beklemede)` | `TelemetryFormatter.FormatCpuTemperature(data.CpuTemperature)` |
| `TxtOsdCpuPower` | `45.2 W` | `TelemetryFormatter.FormatCpuPower(data.CpuPowerWatts)` |
| `TxtOsdCpuFreq` | `4.25 GHz` | `TelemetryFormatter.FormatCpuFrequency(data.CpuFrequencyMHz)` |
| `TxtOsdCpuUsage` | `28%` | `${data.CpuUsage:F0}%` |
| `TxtOsdGpuTemp` | `62 °C` or `0 °C (Uyku / D3cold)` | `TelemetryFormatter.FormatGpuTemperature(data.GpuTemperature, isGpuSleeping)` |
| `TxtOsdGpuPower` | `85.5 W` or `0.0 W (D3cold)` | `TelemetryFormatter.FormatGpuPower(data.GpuPowerWatts, isGpuSleeping)` |
| `TxtOsdGpuFreq` | `2100 MHz` or `0 MHz (Uyku)` | `TelemetryFormatter.FormatGpuFrequency(data.GpuFrequencyMHz, isGpuSleeping)` |
| `TxtOsdVram` | `2048 MB / 8192 MB (%25)` | `TelemetryFormatter.FormatVramUsage(data.VramUsedMB, data.VramTotalMB, isGpuSleeping)` |
| `TxtOsdMode` | `BALANCED`, `TURBO`, etc. | `mode.ToString().ToUpperInvariant()` |
| `TxtOsdRam` | `12.4GB` or `-- GB` | Calculated from `data.RamTotalMB` and `data.RamUsage` |
| `TxtOsdFan` | `FAN: 3960 RPM (%66)` | `$"FAN: {TelemetryFormatter.FormatFanSpeed(data.CpuFanSpeed)}"` |

### 3.2 OSD Window Invariants
- `Topmost="True"` — Must float over active fullscreen games.
- `ShowInTaskbar="False"` — Must not clutter the Windows taskbar.
- `AllowsTransparency="True"`, `Background="Transparent"`, `WindowStyle="None"`.
- `Focusable="False"`, `WindowStartupLocation="Manual"`.
- Win32 Extended Window Styles (`OnLoaded`):
  - `WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`
  - Ensures the OSD is completely click-through and never steals input focus from running games.
- Double-click handler on Window (`MouseLeftButtonDown`): cycles screen corners (0: Top-Left, 1: Top-Right, 2: Bottom-Right, 3: Bottom-Left).

---

## 4. Hardware Logic & Lifecycle Integrity

1. **WMI & Hardware Sensor Queries**:
   - `AcerAgentClient` connects over TCP port 46933 to `AASSvc`. Command framing: `[Type:4][Length:4][JSON Payload]`.
   - `HardwareMonitorService` dynamically initializes `nvml.dll` (`nvmlInit_v2`, `nvmlDeviceGetHandleByIndex_v2`, `nvmlDeviceGetPowerUsage`, `nvmlDeviceGetMemoryInfo`).
   - `BatteryWatchdogService` uses `kernel32!GetSystemPowerStatus` and WMI query `SELECT DischargeRate, ChargeRate, RemainingCapacity, Voltage, Discharging FROM BatteryStatus` in `root\WMI`.
2. **Thermal Resistance Physics & History**:
   - `ThermalHealthService` records samples only under stable conditions. Discards idle post-load heat-soak readings (> 54°C within cooldown debounce). Requires >= 3 consecutive load ticks before computing $R_{th} = \frac{T_{cpu} - T_{ambient}}{P_{cpu} \times \text{fanFactor}}$.
   - Ordinary Least Squares (OLS) regression calculates daily degradation slope and stability score over 14 or 30 days. Tested for zero-division, burn-in improvements, and noise resilience.
3. **GPU DDS & D3cold Deep Sleep**:
   - When the discrete GPU powers down to sleep (D3cold), temperature returns $\le 0$, power drops to 0, and clock drops to 0. `TelemetryFormatter` formats these states as `"0 °C (Uyku / D3cold)"`, `"0.0 W (D3cold)"`, and `"0 MHz (Uyku)"` without crashing.
4. **80% Battery Limit Enforcement**:
   - Enforced by updating `C:\ProgramData\Acer\CC\settings.json` with file-locking and firing WMI ACPI notifications.
   - Re-asserted on `SystemEvents.PowerModeChanged` (`PowerModes.Resume` and `PowerModes.StatusChange`).
5. **Keyboard Backlight Persistence**:
   - Persisted across `settings.json` and `active_rgb_state.json`.
   - Staged dual-pulse re-assertion at ~250ms and ~1500ms on `PowerModes.Resume` and `SessionSwitchReason.SessionUnlock` to override Acer EC firmware default resets.
6. **Single-Instance Mutex & Focus Activation**:
   - Uses named kernel mutex `Local\NitroLite_SingleInstance_Mutex_AN16` with `WaitOne(0)` handling `AbandonedMutexException`.
   - Secondary instance signals `Local\NitroLite_SingleInstance_WakeUpEvent_AN16` and invokes `AllowSetForegroundWindow(-1)` before exiting cleanly.
   - Primary instance listens on a background thread and calls `win.ShowWindow()` restoring the window from the tray.
7. **Background Polling Timers**:
   - Main telemetry poll: 1.0 second `DispatcherTimer`.
   - Toast auto-dismiss: 3.0 second `DispatcherTimer`.
   - Battery watchdog: 3.0 second `PeriodicTimer`.
   - Game scan: 2.0 second `DispatcherTimer`.

---

## 5. `NitroLite.Tests` String & Logic Assertions Matrix

All 201 unit tests currently pass. The test suite contains specific tests that assert on exact string outputs:

### 5.1 `TelemetryFormatterTests` (21 Tests asserting exact strings)
The worker MUST NOT alter the string formatting output of `TelemetryFormatter.cs` unless coordinating test updates:
- `"3960 RPM (%66)"`, `"0 RPM (%0)"`, `"6000 RPM (%100)"`
- `"3960 RPM"`, `"0 RPM"`
- `"%66"`, `"%0"`, `"%100"`
- `"68 °C"`, `"0 °C (Beklemede)"`
- `"62 °C"`, `"0 °C (Uyku / D3cold)"`
- `"45.2 W"`, `"0.0 W"`
- `"85.5 W"`, `"0.0 W (D3cold)"`
- `"4.25 GHz"`, `"0.00 GHz (Beklemede)"`
- `"2100 MHz"`, `"0 MHz (Uyku)"`
- `"12.4 GB / 31.9 GB (%38.9)"`, `"0.0 GB / 0.0 GB (%0.0)"`
- `"2048 MB / 8192 MB (%25)"`, `"0 MB / 0 MB (D3cold)"`
- `"24.5 W (Deşarj Oluyor)"`
- `"58.2 W (Şarj Oluyor)"`
- `"0.0 W (Adaptörde / Doygun)"`
- `"0.0 W (Pilde / Boşta)"`
- `"2 sa 30 dk"`, `"45 dk"`, `"Adaptöre Bağlı"`, `"Hesaplanıyor..."`
- `"483.6 GB / 1863.0 GB (%26.0 Dolu)"`, `"0.0 GB"`

### 5.2 `ThermalHealthServiceTests` (Asserting exact diagnosis strings)
- `"Kararlı, Macun kondisyonu mükemmel"`
- `"İyi Seviyede, Hafif yaşlanma / stabil ısı iletimi"`
- `"Performans Kaybı, Termal direnç artış eğiliminde"`
- `"Kritik, Termal macun kurumuş / bakım gerekli"`
- `"Veri toplanıyor (Yeterli gün kaydı yok)"`
- `"42 °C"`, `"-- °C"`, `"29.5 °C"`, `"+0.003 °C/W/gün"`, `"-0.002 °C/W/gün"`, `"%98.7"`

### 5.3 `AdversarialChallengerTests`
- Asserts that `PasteHealthStatus` contains `"İyi Seviyede"` or `"Dikkat"` depending on thermal resistance degradation.

### 5.4 Test Interactions with UI
- `NitroLite.Tests` does NOT instantiate WPF `Window` classes directly. It tests the models, services, persistence formats, and formatter utilities.
- Refactoring `MainWindow.xaml` and `OsdOverlayWindow.xaml` will NOT break existing tests **provided that**:
  1. `TelemetryFormatter` and `ThermalHealthService` method signatures and return strings remain unchanged.
  2. The project builds cleanly with `dotnet build NitroLite.sln`.

---

## 6. Strict "DO NOT BREAK" Refactoring Checklist

Workers refactoring UI/UX and text must adhere to the following checklist before committing changes:

### [ ] Checklist: XAML Element Names (`x:Name`)
- [ ] Retain all 136 `x:Name` references in `MainWindow.xaml` without typo or omission.
- [ ] Retain all 11 `x:Name` references in `OsdOverlayWindow.xaml`.
- [ ] Confirm no code-behind compiler error `CS0103` (The name 'xyz' does not exist in the current context).

### [ ] Checklist: Event Handlers
- [ ] Keep all 43 event bindings mapped to their exact C# method names (`Click`, `Checked`, `Unchecked`, `SelectionChanged`, `ValueChanged`, `MouseLeftButtonDown`).
- [ ] Do not remove `ClickMode` or handler attachments from custom template toggle buttons.

### [ ] Checklist: Tag Attributes
- [ ] All 4 Mode buttons retain `Tag="Quiet"`, `Tag="Default"`, `Tag="Extreme"`, `Tag="Turbo"`.
- [ ] All 3 CPU Boost buttons retain `Tag="Aggressive"`, `Tag="Efficient"`, `Tag="Disabled"`.
- [ ] All Per-Game Target ComboBoxItems retain their exact enum `Tag` values.
- [ ] All Telemetry Chart ComboBoxItems retain `Tag="CPU_TEMP"`, `Tag="GPU_TEMP"`, etc.
- [ ] All 7 Preset Color buttons retain their exact hex string `Tag` values, especially `Tag="#000000"`.
- [ ] Trend period RadioButtons retain `Tag="14"` and `Tag="30"`.

### [ ] Checklist: Resource Dictionary Keys
- [ ] The 5 dynamically resolved SolidColorBrush keys (`AccentGreen`, `AccentCyan`, `AccentRed`, `TextSecondary`, `TextMuted`) are defined in `Window.Resources`.
- [ ] Colors can be re-tuned for modern high-contrast aesthetic, but key names must never be renamed.

### [ ] Checklist: Text De-Slopping & Invariant Tokens
- [ ] Section titles, explanatory cards, and tooltips may be re-written to eliminate buzzwords and emojis.
- [ ] ComboBoxItems for `CmbRgbEffect` MUST start with the exact tokens: `STATIC`, `WAVE`, `BREATHING`, `NEON`, `SHIFTING`, `ZOOM`, `METEOR`, `TWINKLING`, `OFF`.
- [ ] Do not change output strings inside `TelemetryFormatter.cs` without updating corresponding unit tests in `TelemetryFormatterTests.cs`.

### [ ] Checklist: Window Styles & Click-Through
- [ ] `MainWindow.xaml` must retain `AllowsTransparency="True"`, `WindowStyle="None"`, `Background="Transparent"`, `ResizeMode="CanMinimize"`.
- [ ] `OsdOverlayWindow.xaml` must retain `Topmost="True"`, `ShowInTaskbar="False"`, `WindowStyle="None"`, `AllowsTransparency="True"`, `Background="Transparent"`, `Focusable="False"`.

### [ ] Checklist: Verification
- [ ] `dotnet build c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite.sln` compiles with 0 errors and 0 warnings.
- [ ] `dotnet test c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite.Tests\NitroLite.Tests.csproj` passes all 201 unit tests.
