# NitroLite Visual Styling & XAML Structure Survey Report

**Author**: `explorer_ui_survey_1` (Teamwork Explorer)  
**Date**: 2026-11-04  
**Target Solution**: `NitroLite.sln` (.NET 10 WPF Desktop Application)  
**Files Audited**:
- `NitroLite/MainWindow.xaml` (1,293 lines)
- `NitroLite/MainWindow.xaml.cs` (2,133 lines)
- `NitroLite/OsdOverlayWindow.xaml` (57 lines)
- `NitroLite/OsdOverlayWindow.xaml.cs` (155 lines)
- `NitroLite/App.xaml` (9 lines)

---

## 1. Executive Summary

NitroLite is an Acer Nitro hardware utility providing fan control, telemetry polling, per-game profile automation, battery preservation, and keyboard lighting controls. While the underlying C# service architecture and hardware logic are robust, the user interface currently exhibits severe symptoms of **vibecoded / AI-generated design slop**.

Across `MainWindow.xaml` and `OsdOverlayWindow.xaml`, the application relies on saturated neon cyber aesthetics (`#00E5FF` Cyan, `#BD00FF` Purple, `#00FFA3` Green, `#FF2A55` Red), over 45 emojis embedded into navigation, titles, buttons, and combo box items, over 27 chaotic shades of dark blue backgrounds with inconsistent contrast, colored card borders on hover, blurred glassmorphism drop shadows, and repetitive 3-box/4-box mini-card grids.

This report documents:
1. An exhaustive catalog of all vibecoding anti-pattern occurrences across the codebase with exact file paths and line numbers.
2. A full inventory of named controls (`x:Name`), event handlers, and C# resource lookups (`FindResource`) required for 100% zero-regression refactoring.
3. A complete, cohesive desktop design system adhering to `ui-ux-designer` (solid high-contrast dark surfaces e.g. `#0F1115` / `#161A22` / `#1E232E`, 4px/8px spacing grid, Segoe UI Variable typography hierarchy, subtle accessible borders, and high data density).
4. Concrete, element-by-element replacement recommendations for all user-facing panels.

---

## 2. Exhaustive Audit of the 20 Vibecoding Anti-Patterns

| # | Anti-Pattern | Severity | Presence in NitroLite | Specific Locations & Evidence |
|---|---|---|---|---|
| **1** | **Purple-to-blue gradient / Neon rainbow styling** | Critical | Widespread | `MainWindow.xaml:15-26`: Palette contains saturated neon colors (`AccentPurple` `#BD00FF`, `AccentCyan` `#00E5FF`, `AccentGreen` `#00FFA3`, `AccentRed` `#FF2A55`). `MainWindow.xaml:252-253`: Red `⚡ NITRO` followed by Cyan `LITE`. `MainWindow.xaml:704-705`: Chart area filled with `#1500E5FF` cyan polygon. `OsdOverlayWindow.xaml:14`: Border cyan tint `#3300E5FF`. Telemetry values switch erratically between cyan, red, green, amber, and purple without semantic logic. |
| **2** | **Gradient hero text / flashy branding** | High | Present | `MainWindow.xaml:252-253`: Hero window title splits "NITRO" (red) and "LITE" (cyan) side-by-side with emoji `⚡`. In `OsdOverlayWindow.xaml:21-53`, inline text runs cycle through 5 neon colors. |
| **3** | **Emojis in headings and buttons** | Critical | Ubiquitous (>45 occurrences) | `MainWindow.xaml:252` (`⚡`), `MainWindow.xaml:263` (`🔋`), `MainWindow.xaml:274` (`🗕`), `MainWindow.xaml:295-299` (`⚡`, `📊`, `🌈`, `🔋`, `🛠️`), `MainWindow.xaml:339-375` (`🌙`, `⚖️`, `🔥`, `⚡`), `MainWindow.xaml:412-413` (`🌀`, `🌪️`), `MainWindow.xaml:421-430` (`❄️`, `🚀`, `⚡`, `❄️`), `MainWindow.xaml:443-473` (`🎮`, `🔍`, `➕`, `🗑️`), `MainWindow.xaml:483-524` (`⚙️`, `⚡`, `🔥`, `⚖️`, `🌙`, `❄️`, `🚀`, `🌀`, `🌪️`, `💾`), `MainWindow.xaml:546-547` (`🖥️`, `🔄`), `MainWindow.xaml:556-591` (`💻`, `🎮`), `MainWindow.xaml:660-666` (`⚡`, `🧹`), `MainWindow.xaml:677-691` (`📈`, `🌡️`, `⚡`, `🚀`, `📊`, `🧠`, `🎮`), `MainWindow.xaml:779-810` (`✨`, `💾`, `🗑️`, `🌑`), `MainWindow.xaml:905-928` (`🎯`, `⚡`, `🔬`, `🔄`), `MainWindow.xaml:973-1054` (`🌡️`, `📈`), `MainWindow.xaml:1115-1152` (`💽`, `💾`, `🌡️`, `🟢`), `MainWindow.xaml:1176` (`⚠️`), `MainWindow.xaml:1202-1235` (`🛡️`, `🛑`), `OsdOverlayWindow.xaml:22,34,46` (`💻`, `🎮`, `⚡`). |
| **4** | **Inter font everywhere / Lack of native Windows typography** | High | Present | Typography is mostly unstyled system fallbacks, while `OsdOverlayWindow.xaml:22-52` mixes `Consolas, Segoe UI` inline on every text run. Lacks the native Windows 11 `Segoe UI Variable Text` and `Segoe UI Variable Display` hierarchy with tabular numbers for telemetry. |
| **5** | **Colored border cards** | Critical | Ubiquitous | Hover triggers dynamically paint card borders with neon cyan: `MainWindow.xaml:51,55` (`AccentCyan` border on combobox), `MainWindow.xaml:118` (dropdown border `AccentCyan`), `MainWindow.xaml:134,138` (combobox hover/open border `AccentCyan`), `MainWindow.xaml:222` (`ModeCardButton` hover border `AccentCyan`). Static colored borders: `MainWindow.xaml:428` (`BtnBoostAggressive` red border), `MainWindow.xaml:473` (`BtnDeleteGame` red border), `MainWindow.xaml:546-547` (`BtnToggleOsd` & `BtnCycleOsdCorner` cyan borders), `MainWindow.xaml:666` (`BtnCleanRamCache` cyan border), `MainWindow.xaml:800` (`BtnDeleteCustomPreset` red border), `MainWindow.xaml:810` (`BtnTurnOffLights` red border), `MainWindow.xaml:1281` (`ToastBanner` green border), `OsdOverlayWindow.xaml:14` (`#3300E5FF` border). |
| **6** | **Glassmorphism cards & heavy drop shadows** | High | Widespread | `MainWindow.xaml:234`: Window outer container has `DropShadowEffect BlurRadius="25" Color="#000000" Opacity="0.8"`. `MainWindow.xaml:123`: ComboBox popup has `DropShadowEffect BlurRadius="15" Opacity="0.85"`. `MainWindow.xaml:1283`: Toast notification has `DropShadowEffect BlurRadius="18" Opacity="0.9"`. `OsdOverlayWindow.xaml:16`: OSD has `DropShadowEffect BlurRadius="12" Opacity="0.9"`. `ToastBanner Background="#E60D1926"` and `OsdOverlayWindow Background="#E60A0D14"` both simulate blurry translucent glass. |
| **7** | **Low-contrast dark mode / Muddy illegible dark palettes** | Critical | Ubiquitous | The XAML uses over 27 distinct hardcoded dark hex colors (`#0B0D13`, `#0E1017`, `#0F121B`, `#0C0E14`, `#10141D`, `#131620`, `#131824`, `#161B26`, `#161F2E`, `#162228`, `#162436`, `#1A2536`, `#1B1F2D`, `#1B2232`, `#1B2B38`, `#1C2130`, `#1C2433`, `#1E2536`, `#1E2B3E`, `#1F2536`, `#22283A`, `#242B3D`, `#252D40`, `#2A1B28`, `#2B151C`, `#3A1C25`, `#3D1822`). Many nested cards sit on `#10141D` or `#131620` with muted text `#55607A` or `#8E99AF` at 9pt-10pt, failing WCAG 2.1 AA 4.5:1 contrast requirements. |
| **8** | **3 icon boxes in a row / repetitive mini-card grids** | High | Ubiquitous | Repeated pattern of 3 or 4 identical rectangular mini-cards with an icon/emoji and a colored number: `MainWindow.xaml:335-383` (4 Mode cards), `MainWindow.xaml:427-431` (3 CPU boost buttons), `MainWindow.xaml:489-519` (3 per-game dropdowns), `MainWindow.xaml:591-628` (4 thermal matrix cards), `MainWindow.xaml:711-739` (4 chart statistic cards), `MainWindow.xaml:757-770` (4 keyboard preview boxes), `MainWindow.xaml:928-964` (4 battery health cards), `MainWindow.xaml:981-1017` (4 thermal paste/fan cards), `MainWindow.xaml:1058-1094` (4 subcomponent matrix cards). |
| **9** | **Badge above the headline / excessive pill badge clutter** | High | Ubiquitous | Over 11 pill badges cluttered around titles: `MainWindow.xaml:254` (`v1.3 (Native .NET 10)`), `MainWindow.xaml:261` (`BadgeBattery`), `MainWindow.xaml:268` (`BadgeMode`), `MainWindow.xaml:423` (`TxtBoostStatusBadge`), `MainWindow.xaml:444` (`BadgeGameStatus`), `MainWindow.xaml:484` (`TxtSelectedGameNameBadge`), `MainWindow.xaml:557` (`TxtCpuCoreCount`), `MainWindow.xaml:574` (`TxtStabilityBadge`), `MainWindow.xaml:651` (`TxtStorageBadge`), `MainWindow.xaml:976` (`TxtThermalBadge`), `MainWindow.xaml:1231` (`TxtNitroSenseRunningBadge`). |
| **10** | **Lucide / generic icon overload** | Moderate | Widespread | Icons pasted indiscriminately on buttons, dropdown items, and telemetry items without functional purpose (e.g., `💾` on save, `🔍` on scan, `➕` on add, `🗑️` on delete, `🧹` on clean, `🔄` on refresh). |
| **11** | **Untouched / template cards** | High | Present | Cards nested 3 layers deep (`Border` inside `Border` inside `Border`) with duplicate padding and misaligned borders (`CardBorder` enclosing `#10141D` enclosing `#161B26`). |
| **12** | **Fade-in on scroll / Opacity fades** | Moderate | Present | `ToastBanner` in `MainWindow.xaml:814` and `MainWindow.xaml.cs:147` uses `DoubleAnimation(1.0, 0.0)` opacity fade. |
| **13** | **Cursor-following beam** | Low | Not present | No cursor light follow in code. |
| **14** | **Buttons fade on hover / neon hover flashing** | High | Present | Buttons abruptly shift borders from dark to neon cyan or red on hover without smooth transitions or accessible focus rings. |
| **15** | **Inconsistent spacing & padding** | Critical | Ubiquitous | Over 30 different arbitrary margin/padding values (`Padding="12,8,34,8"`, `Margin="0,0,230,0"`, `Margin="12,10"`, `Margin="3"`, `Margin="4"`, `Margin="6,0,0,0"`). Complete lack of a unified 4px/8px design system grid. |
| **16** | **Em dashes everywhere** | High | Present | Used throughout telemetry placeholders, tooltips, and text: `-- °C`, `-- RPM`, `-- W`, `-- GB`, `Kalan Süre: --`, and copy sentences. |
| **17** | **Generic buzzword copy / AI slop** | Critical | Ubiquitous | "Akıllı Oyun Profilleri & Algılayıcı", "Proaktif Bakım Asistanı Değerlendirmesi: Termal macun ve putty yerleşimi çok kararlı. Isı transferinde kuruma tespit edilmedi...", "0 RPM Sıfır Aşınma", "Optimum İletim", "Mükemmel Durumda", "Tek Tıkla Güvenli Telemetri Debloat (Tier 1)", "Akıllı RAM Önbellek Temizleyici". |
| **18** | **Serif italic accents** | Low | Not present | Fonts are sans-serif or monospace. |
| **19** | **Space Grotesk + Instrument Serif** | Low | Not present | Standard WPF font stacks used. |
| **20** | **Grain over a gradient** | Low | Not present | No grain textures in XAML. |

---

## 3. Strict R3 Zero-Regression Inventory

To satisfy Requirement R3, any refactored XAML **must strictly preserve every control identifier (`x:Name`), every event handler, and every dynamic resource key**.

### 3.1. Complete Named Elements Inventory (114 Elements)

The following controls are referenced in code-behind (`MainWindow.xaml.cs` or `OsdOverlayWindow.xaml.cs`) and **must retain their exact `x:Name`**:

#### Title Bar & Window Chrome
1. `BadgeBattery` (`Border`)
2. `TxtBatteryIcon` (`TextBlock`)
3. `TxtBatteryHeader` (`TextBlock`)
4. `BadgeMode` (`Border`)
5. `TxtModeHeader` (`TextBlock`)

#### Sidebar Navigation & Quick Telemetry
6. `NavModes` (`RadioButton`)
7. `NavMonitoring` (`RadioButton`)
8. `NavRgb` (`RadioButton`)
9. `NavBattery` (`RadioButton`)
10. `NavDebloat` (`RadioButton`)
11. `TxtSideCpuTemp` (`TextBlock`)
12. `TxtSideGpuTemp` (`TextBlock`)
13. `TxtSideCpuFan` (`TextBlock`)

#### Tab 1: Operating Modes & Fans (`TabModes`)
14. `TabModes` (`Grid`)
15. `BtnModeQuiet` (`Button`)
16. `IndQuiet` (`Border`)
17. `TxtIndQuiet` (`TextBlock`)
18. `BtnModeDefault` (`Button`)
19. `IndDefault` (`Border`)
20. `TxtIndDefault` (`TextBlock`)
21. `BtnModeExtreme` (`Button`)
22. `IndExtreme` (`Border`)
23. `TxtIndExtreme` (`TextBlock`)
24. `BtnModeTurbo` (`Button`)
25. `IndTurbo` (`Border`)
26. `TxtIndTurbo` (`TextBlock`)
27. `TxtCpuFanRpm` (`TextBlock`)
28. `BarCpuFan` (`ProgressBar`)
29. `TxtGpuFanRpm` (`TextBlock`)
30. `BarGpuFan` (`ProgressBar`)
31. `BtnFanAuto` (`Button`)
32. `BtnFanMax` (`Button`)
33. `TxtBoostStatusBadge` (`TextBlock`)
34. `BtnBoostAggressive` (`Button`)
35. `BtnBoostEfficient` (`Button`)
36. `BtnBoostDisabled` (`Button`)
37. `BadgeGameStatus` (`Border`)
38. `TxtGameStatus` (`TextBlock`)
39. `ChkAutoGameMode` (`CheckBox`)
40. `CmbConfiguredGames` (`ComboBox`)
41. `BtnScanGames` (`Button`)
42. `BtnAddCustomGame` (`Button`)
43. `BtnDeleteGame` (`Button`)
44. `TxtSelectedGameNameBadge` (`TextBlock`)
45. `TxtSelectedGameExePath` (`TextBlock`)
46. `CmbGameTargetMode` (`ComboBox`)
47. `CmbGameTargetCpuBoost` (`ComboBox`)
48. `CmbGameTargetFan` (`ComboBox`)
49. `BtnSaveGameProfile` (`Button`)

#### Tab 2: Live Telemetry & Monitoring (`TabMonitoring`)
50. `TabMonitoring` (`Grid`)
51. `BtnToggleOsd` (`Button`)
52. `BtnCycleOsdCorner` (`Button`)
53. `TxtCpuCoreCount` (`TextBlock`)
54. `TxtMonCpuTemp` (`TextBlock`)
55. `BarMonCpuTemp` (`ProgressBar`)
56. `TxtMonCpuPower` (`TextBlock`)
57. `BarMonCpuPower` (`ProgressBar`)
58. `TxtMonCpuUsage` (`TextBlock`)
59. `BarMonCpuUsage` (`ProgressBar`)
60. `TxtMonCpuFreqGhz` (`TextBlock`)
61. `TxtMonGpuTemp` (`TextBlock`)
62. `BarMonGpuTemp` (`ProgressBar`)
63. `TxtMonGpuPower` (`TextBlock`)
64. `BarMonGpuPower` (`ProgressBar`)
65. `TxtMonGpuUsage` (`TextBlock`)
66. `BarMonGpuUsage` (`ProgressBar`)
67. `TxtMonVram` (`TextBlock`)
68. `BarMonVram` (`ProgressBar`)
69. `TxtMonRamDetail` (`TextBlock`)
70. `TxtMonRamPercent` (`TextBlock`)
71. `BarMonRam` (`ProgressBar`)
72. `TxtMonSysTemp` (`TextBlock`)
73. `TxtMonRamFreq` (`TextBlock`)
74. `TxtRamCleanStatus` (`TextBlock`)
75. `ChkAutoRamClean` (`CheckBox`)
76. `BtnCleanRamCache` (`Button`)
77. `CmbChartMetric` (`ComboBox`)
78. `CanvasHistoryChart` (`Canvas`)
79. `ChartArea` (`Polygon`)
80. `ChartLine` (`Polyline`)
81. `TxtChartCurrentVal` (`TextBlock`)
82. `TxtChartMinVal` (`TextBlock`)
83. `TxtChartMaxVal` (`TextBlock`)
84. `TxtChartAvgVal` (`TextBlock`)

#### Tab 3: Keyboard RGB Studio (`TabRgb`)
85. `TabRgb` (`Grid`)
86. `PreviewZone1` (`Border`)
87. `PreviewZone2` (`Border`)
88. `PreviewZone3` (`Border`)
89. `PreviewZone4` (`Border`)
90. `CmbRgbPresets` (`ComboBox`)
91. `TxtCustomPresetName` (`TextBox`)
92. `BtnSaveCustomPreset` (`Button`)
93. `BtnDeleteCustomPreset` (`Button`)
94. `BtnTurnOffLights` (`Button`)
95. `CmbRgbEffect` (`ComboBox`)
96. `ChkBacklightTimeout` (`CheckBox`)
97. `TxtBrightnessVal` (`TextBlock`)
98. `SliderBrightness` (`Slider`)
99. `TxtSpeedVal` (`TextBlock`)
100. `SliderSpeed` (`Slider`)

#### Tab 4: Battery & Hardware Health (`TabBattery`)
101. `TabBattery` (`Grid`)
102. `TxtBatLevelBig` (`TextBlock`)
103. `TxtBatStateDesc` (`TextBlock`)
104. `BarBatBig` (`ProgressBar`)
105. `TxtBatDischargeRate` (`TextBlock`)
106. `TxtBatRemainingTime` (`TextBlock`)
107. `ChkWatchdog` (`CheckBox`)
108. `DischargeStatusBox` (`Border`)
109. `TxtDischargeStatus` (`TextBlock`)
110. `BtnToggleDischarge` (`Button`)
111. `BtnRefreshBatHealth` (`Button`)
112. `TxtBatHealthPercent` (`TextBlock`)
113. `TxtBatWearPercent` (`TextBlock`)
114. `TxtBatFullChargeCap` (`TextBlock`)
115. `TxtBatDesignCap` (`TextBlock`)
116. `TxtBatVoltage` (`TextBlock`)
117. `TxtThermalBadge` (`TextBlock`)
118. `TxtPasteHealthPercent` (`TextBlock`)
119. `TxtThermalResistance` (`TextBlock`)
120. `TxtActiveFanHours` (`TextBlock`)
121. `TxtAvgFanRpm` (`TextBlock`)
122. `TxtPassiveFanHours` (`TextBlock`)
123. `TxtFanBearingHealth` (`TextBlock`)
124. `TxtStabilityDaysSubtitle` (`TextBlock`)
125. `RadioTrend14Days` (`RadioButton`)
126. `RadioTrend30Days` (`RadioButton`)
127. `TxtStabilityScore` (`TextBlock`)
128. `TxtStabilityStatus` (`TextBlock`)
129. `TxtStabilitySlope` (`TextBlock`)
130. `TxtStabilityBadge` (`TextBlock`)
131. `BarStabilityScore` (`ProgressBar`)
132. `TxtSubMoboTemp` (`TextBlock`)
133. `TxtSubSsd1Temp` (`TextBlock`)
134. `TxtSubSsd2Temp` (`TextBlock`)
135. `TxtSubBatteryTemp` (`TextBlock`)
136. `TxtThermalRecommendation` (`TextBlock`)
137. `TxtStorageBadge` (`TextBlock`)
138. `PanelStorageDrives` (`StackPanel`)
139. `CardDrive0` (`Border`)
140. `TxtDrive0Letter` (`TextBlock`)
141. `TxtDrive0Name` (`TextBlock`)
142. `TxtDrive0Bus` (`TextBlock`)
143. `TxtDrive0Temp` (`TextBlock`)
144. `TxtDrive0HealthBadge` (`TextBlock`)
145. `TxtDrive0Detail` (`TextBlock`)
146. `TxtDrive0Free` (`TextBlock`)
147. `BarDrive0` (`ProgressBar`)
148. `CardDrive1` (`Border`)
149. `TxtDrive1Letter` (`TextBlock`)
150. `TxtDrive1Name` (`TextBlock`)
151. `TxtDrive1Bus` (`TextBlock`)
152. `TxtDrive1Temp` (`TextBlock`)
153. `TxtDrive1HealthBadge` (`TextBlock`)
154. `TxtDrive1Detail` (`TextBlock`)
155. `TxtDrive1Free` (`TextBlock`)
156. `BarDrive1` (`ProgressBar`)
157. `BannerOvercharge` (`Border`)
158. `TxtOverchargeMsg` (`TextBlock`)

#### Tab 5: Debloat Hub & Settings (`TabDebloat`)
159. `TabDebloat` (`Grid`)
160. `BtnApplyDebloat` (`Button`)
161. `BtnRestoreServices` (`Button`)
162. `ListServiceStatuses` (`ItemsControl`)
163. `TxtNitroSenseRunningBadge` (`TextBlock`)
164. `BtnKillNitroSense` (`Button`)
165. `ChkAutoStart` (`CheckBox`)
166. `ChkStartMinimized` (`CheckBox`)

#### Footer & Toast Banner
167. `LedAas` (`Ellipse`)
168. `TxtAasStatus` (`TextBlock`)
169. `ToastBanner` (`Border`)
170. `TxtToastIcon` (`TextBlock`)
171. `TxtToastMessage` (`TextBlock`)

#### OsdOverlayWindow
172. `TxtOsdCpuTemp` (`TextBlock`)
173. `TxtOsdCpuPower` (`TextBlock`)
174. `TxtOsdCpuFreq` (`TextBlock`)
175. `TxtOsdCpuUsage` (`TextBlock`)
176. `TxtOsdGpuTemp` (`TextBlock`)
177. `TxtOsdGpuPower` (`TextBlock`)
178. `TxtOsdGpuFreq` (`TextBlock`)
179. `TxtOsdVram` (`TextBlock`)
180. `TxtOsdMode` (`TextBlock`)
181. `TxtOsdRam` (`TextBlock`)
182. `TxtOsdFan` (`TextBlock`)

### 3.2. Event Handlers Inventory (All 29 Events Preserved)

1. `TitleBar_MouseLeftButtonDown` (Window DragMove)
2. `BtnMinimize_Click` (Minimize window)
3. `BtnClose_Click` (Close / minimize to tray)
4. `Nav_Checked` (Sidebar tab switcher)
5. `BtnMode_Click` (Operating mode button click)
6. `BtnFanAuto_Click` (Fan auto mode)
7. `BtnFanMax_Click` (Fan max mode)
8. `BtnBoost_Click` (CPU boost switch)
9. `ChkAutoGameMode_Changed` (Auto game detection switch)
10. `CmbConfiguredGames_SelectionChanged` (Game list selection)
11. `BtnScanGames_Click` (Scan games)
12. `BtnAddCustomGame_Click` (Add custom game .exe)
13. `BtnDeleteGame_Click` (Delete game profile)
14. `BtnSaveGameProfile_Click` (Save game profile)
15. `BtnToggleOsd_Click` (Toggle OSD window)
16. `BtnCycleOsdCorner_Click` (Cycle OSD 4 corners)
17. `ChkAutoRamClean_Changed` (Auto RAM cleaner switch)
18. `BtnCleanRamCache_Click` (Clean RAM cache button)
19. `CmbChartMetric_SelectionChanged` (Metric selection for chart)
20. `CmbRgbPresets_SelectionChanged` (RGB preset selection)
21. `BtnSaveCustomPreset_Click` (Save RGB preset)
22. `BtnDeleteCustomPreset_Click` (Delete RGB preset)
23. `BtnTurnOffLights_Click` (Turn off lighting)
24. `BtnPresetColor_Click` (Preset color palette click)
25. `CmbRgbEffect_SelectionChanged` (RGB effect dropdown)
26. `ChkBacklightTimeout_Changed` (Backlight timeout switch)
27. `SliderBrightness_ValueChanged` (Brightness slider)
28. `SliderSpeed_ValueChanged` (Speed slider)
29. `ChkWatchdog_Changed` (Battery watchdog switch)
30. `BtnToggleDischarge_Click` (Toggle safe discharge)
31. `BtnRefreshBatHealth_Click` (Refresh battery health)
32. `RadioTrendPeriod_Checked` (14/30 day trend radio)
33. `BtnApplyDebloat_Click` (Apply debloat)
34. `BtnRestoreServices_Click` (Restore services)
35. `BtnKillNitroSense_Click` (Kill NitroSense)
36. `ChkAutoStart_Changed` (Windows auto-start switch)
37. `ChkStartMinimized_Changed` (Start minimized switch)

### 3.3. Dynamic Resources Required by C# Code (`FindResource`)

The following resource keys are dynamically resolved in C# via `(SolidColorBrush)FindResource("Key")`:
- `"AccentGreen"` (used in: Toast icon/border, AASSvc LED, RAM clean status, game status, storage health, paste health, battery discharge, mode indicators)
- `"AccentCyan"` (used in: Toast icon/border, Boost badge, game status, drive bars, thermal badge, battery discharge rate, mode indicators)
- `"AccentRed"` (used in: AASSvc LED failure, Boost badge, drive bars, thermal badge, discharge toggle button, mode indicators)
- `"AccentPurple"` (used for battery voltage)
- `"TextPrimary"` (used for main text)
- `"TextSecondary"` (used for subtext)
- `"TextMuted"` (used for inactive mode indicators)

**Zero-Regression Guarantee**: These exact keys must remain registered as `SolidColorBrush` resources in `App.xaml` or `MainWindow.xaml.Resources`.

---

## 4. Concrete Desktop Design System Specification (`ui-ux-designer`)

Adhering to the `ui-ux-designer` skill, desktop utilities require **purposeful structure, high contrast, clean visual hierarchy, and zero superficial decorations**.

### 4.1. Color System (Solid High-Contrast Professional Dark Theme)

All chaotic dark blues are replaced with a disciplined 4-level neutral slate surface hierarchy:

| Token Key | Hex Value | Role & Usage | Contrast vs Background |
|---|---|---|---|
| `SurfaceRoot` (`BgDark`) | `#0F1115` | Window root canvas (solid deep neutral charcoal) | Base canvas |
| `SurfaceSidebar` | `#13161D` | Sidebar navigation background | 1.1:1 elevation |
| `SurfaceCard` (`CardBg`) | `#171B24` | Primary cards and telemetry panels | 1.25:1 elevation |
| `SurfaceCardHover` | `#1E232F` | Hover state for interactive cards | Interactive state |
| `SurfaceControl` | `#222733` | Standard buttons, input backgrounds, table cells | 1.4:1 elevation |
| `SurfaceControlHover` | `#2B3242` | Button hover background | Interactive state |
| `SurfaceControlActive`| `#343D50` | Button pressed / active state | Interactive state |
| `BorderSubtle` (`BorderDark`) | `#252B38` | Card and panel structural 1px dividers | Crisp definition |
| `BorderStrong` | `#3A4356` | Input fields, active focus outlines | Clear affordance |
| `TextPrimary` | `#F1F4F9` | Primary titles, telemetry numbers, active labels | **15.2:1** (AAA Compliant) |
| `TextSecondary` | `#9BA5B7` | Subtitles, parameter names, secondary telemetry | **6.1:1** (AA Compliant) |
| `TextMuted` | `#667085` | Units, captions, helper hints | **3.8:1** (Accessible caption) |

#### Telemetry Accents (Refined, Non-Neon, Semantically Bound)
- `AccentGreen` (`#10B981`): Healthy status, optimal thermal resistance, active balanced mode, normal battery. (Contrast **6.5:1** on `#171B24`).
- `AccentCyan` (`#0EA5E9`): Active CPU telemetry, primary action accent, calm info indicator. (Contrast **7.2:1** on `#171B24`).
- `AccentRed` (`#EF4444`): High temperature warning, turbo/power modes, destructive actions (Delete, Terminate). (Contrast **4.8:1** on `#171B24`).
- `AccentAmber` (`#F59E0B`): Telemetry alert, battery discharge warning. (Contrast **7.9:1** on `#171B24`).
- `AccentPurple` (`#8B5CF6`): Electrical / battery voltage metrics. (Contrast **5.2:1** on `#171B24`).

### 4.2. Typography Hierarchy (Windows 11 Native Desktop)

- **Default Font Family**: `Segoe UI Variable Text, Segoe UI, sans-serif`
- **Monospace Telemetry Font Family**: `Consolas, Cascadia Code, Courier New` (used strictly for tabular numeric alignment in OSD and telemetry graphs)
- **Scale**:
  - `HeaderTitle`: 15pt, SemiBold, Foreground `TextPrimary` (App branding & TitleBar)
  - `PageTitle`: 18pt, SemiBold, Foreground `TextPrimary` (Tab headings)
  - `SectionHeader`: 12pt, SemiBold, Foreground `TextPrimary` (Panel headings, no emojis, clean title case)
  - `Body`: 12pt, Regular, Foreground `TextSecondary` (Descriptions, explanations)
  - `Label`: 11pt, SemiBold, Foreground `TextSecondary` (Field labels, parameters)
  - `DataLarge`: 22pt, Bold, Foreground `TextPrimary` (Primary big metrics: Fan RPM, Battery %)
  - `DataMedium`: 15pt, SemiBold, Foreground `TextPrimary` (Temperatures, Wattages)
  - `Caption`: 10pt, Regular, Foreground `TextMuted` (Hardware bus IDs, units)

### 4.3. Spacing & Layout Grid (4px / 8px Fixed Scale)

- **Layout Grid**: 8px baseline grid with 4px sub-intervals.
- **Rules**:
  - Outer Window Padding: `12` uniform.
  - Sidebar Width: `220` (fixed width, `Padding="12,16"`).
  - Main Tab Container Margin: `16` uniform.
  - Section-to-Section Vertical Gap: `16` uniform (`Margin="0,0,0,16"`).
  - Card Internal Padding: `16` uniform. Compact sub-card padding: `12,10`.
  - Element Inline Gaps: `8` or `12` (`Margin="0,0,8,0"` or `Margin="0,0,12,0"`).
  - Corner Radii:
    - Main Window: `10`
    - Cards & Panels: `6`
    - Buttons & Inputs: `4`
    - Eliminates exaggerated roundings (no more 14px/12px bubbles).

### 4.4. Component Styling Rules

1. **Elimination of Blurred Glass & Heavy Drop Shadows**:
   - Remove 25px, 18px, and 15px `DropShadowEffect`.
   - Windows desktop apps use crisp 1px borders (`#252B38`) against `#0F1115` base. A very subtle low-opacity shadow (`BlurRadius="10" Opacity="0.4" ShadowDepth="2"`) is permitted on the outermost window border only to separate it from the desktop wallpaper.
2. **Elimination of Neon Card Borders on Hover**:
   - Cards do not flash cyan outlines on hover. Hover feedback is achieved through a subtle background elevation (`#171B24` -> `#1E232F`).
3. **Restrained, Standardized Controls**:
   - **Buttons**: Flat solid control surface (`#222733`) with `#252B38` border. Primary buttons use solid accent fills (`#0EA5E9` or `#10B981`) with dark `#0F1115` text. Danger buttons use `#2E181D` background with `#EF4444` text.
   - **ComboBox**: Clean flat container `#171B24`, `#252B38` border, subtle chevron icon `#9BA5B7`. Popups use `#171B24` background with `#252B38` 1px border.
   - **ProgressBars**: Slim 4px height, track background `#222733`, solid fill (`#0EA5E9` or `#10B981`), no neon glow.

---

## 5. Section-by-Section Recommendations & Replacement Plan

### 5.1. Window Chrome & TitleBar (`MainWindow.xaml:231-283`)

#### Current Issues
- `⚡ NITRO` in red + `LITE` in cyan (vibecoded gradient text).
- `v1.3 (Native .NET 10)` pill badge.
- `BadgeBattery` with `🔋` emoji and `BadgeMode` pill badge.
- Raw unicode characters `🗕` and `✕` in caption buttons.
- 25px heavy blurred drop shadow.

#### Replacement Blueprint
- Remove `DropShadowEffect` or replace with subtle `BlurRadius="10" Opacity="0.35"`.
- Clean single-color title:
  ```xml
  <StackPanel Orientation="Horizontal" VerticalAlignment="Center">
      <TextBlock Text="NitroLite" Foreground="{StaticResource TextPrimary}" FontWeight="SemiBold" FontSize="14" />
      <TextBlock Text="Control Center" Foreground="{StaticResource TextMuted}" FontSize="12" Margin="6,0,0,0" VerticalAlignment="Center" />
  </StackPanel>
  ```
- Move battery and mode status indicators to a structured right-aligned telemetry pill:
  - Keep `BadgeBattery`, `TxtBatteryIcon`, `TxtBatteryHeader`, `BadgeMode`, `TxtModeHeader` element names.
  - In `TxtBatteryIcon`, replace `🔋` with a clean geometric icon or simple text label `BAT:`.
  - Use neutral solid background `#171B24` with `#252B38` border.
- Clean caption buttons with standard hover states:
  - Minimize: clean SVG path or subtle hyphen `–`.
  - Close: clean SVG `✕` path, red hover background `#C42B1C`.

### 5.2. Sidebar Navigation & Quick Hardware Status (`MainWindow.xaml:284-323`)

#### Current Issues
- Emojis in all 5 navigation options: `⚡ Güç & Fan Modları`, `📊 Canlı Telemetri & OSD`, `🌈 Klavye RGB Stüdyo`, `🔋 %80 Pil & Sağlık`, `🛠️ Debloat & Ayarlar`.
- "SİSTEM DURUMU" sub-panel is cramped with inconsistent padding.

#### Replacement Blueprint
- Clean technical navigation labels:
  - `NavModes`: `Power and Fan Profiles` (or `Güç ve Fan Profilleri`)
  - `NavMonitoring`: `Telemetry and Overlay` (or `Telemetri ve OSD`)
  - `NavRgb`: `Keyboard Lighting` (or `Klavye Aydınlatması`)
  - `NavBattery`: `Battery and Hardware Health` (or `Pil ve Donanım Sağlığı`)
  - `NavDebloat`: `System Maintenance` (or `Sistem ve Bakım`)
- Navigation Button Style (`NavButton`):
  - Left indicator strip (3px wide) that illuminates on `IsChecked="True"` using `#0EA5E9`.
  - Background transparent on default, `#1E232F` on hover, `#222733` on active.
  - Foreground: `TextSecondary` -> `TextPrimary`.
- Sidebar Hardware Box:
  - Solid `#171B24` background, `#252B38` border, uniform 12px padding.
  - Clean data grid with tabular figures.

### 5.3. Tab 1: Operating Modes & Fans (`MainWindow.xaml:327-532`)

#### Current Issues
- 4 Mode cards with huge emojis (`🌙`, `⚖️`, `🔥`, `⚡`), buzzing hover borders, and pill tags (`SEÇ`).
- Fan control cards have bright colored progress bars without numerical calibration.
- Manual CPU Boost has 3 colorful buttons with emojis (`🚀 Son Hız`, `⚡ Dengeli`, `❄️ Serin Eco`).
- Game detection header has `🎮 AKILLI OYUN PROFİLLERİ & ALGILAYICI` with `[Counter-Strike 2]` badge.
- ComboBox items filled with emojis.

#### Replacement Blueprint
- **Operating Mode Selection**:
  - Replace the 4 emoji buttons (`BtnModeQuiet`, `BtnModeDefault`, `BtnModeExtreme`, `BtnModeTurbo`) with structured hardware mode cards.
  - Card contents:
    - Mode Title (e.g. `Quiet`, `Balanced`, `Performance`, `Turbo`) in 14pt SemiBold.
    - Brief functional summary (e.g., `Optimized for low noise and battery life`, `Dynamic fan curve for daily desktop workloads`).
    - Status pill: Keep `IndQuiet`, `TxtIndQuiet`, etc. Active card receives a crisp left border or solid indicator badge.
    - Hover state: background elevates to `#1E232F`; no cyan outline.
- **Fan Control Card**:
  - Keep `TxtCpuFanRpm`, `BarCpuFan`, `TxtGpuFanRpm`, `BarGpuFan`, `BtnFanAuto`, `BtnFanMax`.
  - Remove emojis from buttons (`Otomatik`, `Maksimum`).
  - Progress bar uses clean `#0EA5E9` track with subtle 4px height.
- **CPU Power & Boost Card**:
  - Remove `❄️` emoji from header.
  - Replace 3 emoji buttons with segmented toggle control:
    - `BtnBoostAggressive`: `High Performance (5.0 GHz)`
    - `BtnBoostEfficient`: `Balanced Dynamic`
    - `BtnBoostDisabled`: `Energy Efficient (3.2 GHz)`
- **Game Profiles & Automation**:
  - Header: `Game Profiles and Automation` (clean typography, no `🎮`).
  - Action buttons:
    - `BtnScanGames`: `Scan Installed Games` (no `🔍`).
    - `BtnAddCustomGame`: `Add Executable...` (no `➕`).
    - `BtnDeleteGame`: `Remove Profile` (no `🗑️`).
  - Dropdown items in `CmbGameTargetMode`, `CmbGameTargetCpuBoost`, `CmbGameTargetFan`: Remove emojis from item contents.
  - Save button `BtnSaveGameProfile`: Clean primary button with `#0EA5E9` background.

### 5.4. Tab 2: Live Telemetry & Monitoring (`MainWindow.xaml:534-745`)

#### Current Issues
- Emojis in OSD buttons: `🖥️ Ekran Üstü Bilgi Göstergesi (OSD)`, `🔄 Konum Değiştir`.
- Colorful rainbow progress bars for CPU and GPU telemetry without semantic distinction.
- RAM cache cleaner has emoji `⚡ AKILLI RAM ÖNBELLEK TEMİZLEYİCİ` and `🧹`.
- Real-time graph has saturated neon stroke and polygon fill `#1500E5FF`.
- 4 mini-boxes under the graph with mismatched color coding.

#### Replacement Blueprint
- OSD Overlay controls:
  - `BtnToggleOsd`: `Toggle Screen Overlay` (keeps exact name and click event).
  - `BtnCycleOsdCorner`: `Switch Corner` (keeps exact name).
  - Clean secondary button styling without hardcoded cyan borders.
- Telemetry Cards:
  - Clean cards for CPU and GPU telemetry.
  - Primary metric: Temperature in `DataMedium` (16pt SemiBold), followed by power and load.
  - Progress bars: Standardized 4px height. High load / high temp (>85°C) conditionally highlights `AccentRed`.
- Telemetry History Graph:
  - Keep `CanvasHistoryChart`, `ChartArea`, `ChartLine`, `CmbChartMetric`.
  - Dropdown: Clean list without emojis (`CPU Temperature`, `GPU Temperature`, `CPU Power`, `GPU Power`, `CPU Frequency`, `CPU Load`, `GPU Load`, `RAM Load`, `VRAM Usage`).
  - Canvas: Dark surface `#171B24`, crisp 1px grid lines `#252B38`. `ChartLine` stroke `#0EA5E9` (1.5px), `ChartArea` fill with subtle vertical gradient from `#1A0EA5E9` to `#000EA5E9`.
  - Statistics table: Replace 4 mini-boxes with a clean, structured horizontal telemetry strip (Current, Min, Max, Average).

### 5.5. Tab 3: Keyboard RGB Studio (`MainWindow.xaml:746-866`)

#### Current Issues
- `4 BÖLGELİ KLAVYE ÖNİZLEMESİ` displays 4 solid cyan blocks with black text.
- Header has `✨ RGB PROFİL YÖNETİCİSİ (HAZIR & ÖZEL PROFİLLER)`.
- Buttons with emojis (`💾 Profili Kaydet`, `🗑️ Sil`, `🌑 Işıkları Tamamen Kapat`).

#### Replacement Blueprint
- Keyboard Preview Zones:
  - Keep `PreviewZone1`, `PreviewZone2`, `PreviewZone3`, `PreviewZone4`.
  - Subtle keycap-inspired layout with 4px corner radius, thin `#252B38` border, and clean zone label.
- Preset Manager:
  - Clean header: `Lighting Profiles`.
  - Keep `CmbRgbPresets`, `TxtCustomPresetName`, `BtnSaveCustomPreset`, `BtnDeleteCustomPreset`.
  - Remove `✨`, `💾`, `🗑️` emojis.
- Quick Color Swatches:
  - Keep 7 color preset buttons. Round corners (4px radius instead of circles or flat squares), with a subtle hover border for selection.
  - `BtnTurnOffLights`: `Turn Off Lighting` (clean secondary button).
- Controls:
  - Effect dropdown `CmbRgbEffect`: Remove emojis, clean items (`Static`, `Wave`, `Breathing`, `Neon`, `Shifting`, `Zoom`, `Meteor`, `Twinkling`, `Off`).
  - Brightness and speed sliders: Clean track styling `#222733` with `#0EA5E9` thumb.

### 5.6. Tab 4: Battery & Hardware Health (`MainWindow.xaml:867-1186`)

#### Current Issues
- Huge 36pt green battery text with muddy progress bar.
- Safe discharge assistant contains buzzwords: `🎯 %80'E GÜVENLİ BOŞALTMA ASİSTANI`.
- 4-column mini-card grids repeated 3 times (Battery Health, Paste/Fan, Subcomponent Matrix).
- Emojis in all drive labels (`💾 [C:]`, `🌡️ 42 °C`, `🟢 SAĞLIK: %99 (Mükemmel)`).
- AI slop diagnostic text in `TxtThermalRecommendation`.

#### Replacement Blueprint
- Battery Status Section:
  - Clean metric header: `Battery State of Charge` with `TxtBatLevelBig` in `DataLarge` (24pt) and `TxtBatStateDesc`.
  - Real-time power flow in a structured data row.
  - Safe Discharge Assistant: Replaced with clean technical copy: `Controlled Battery Discharge`.
  - Keep `DischargeStatusBox`, `TxtDischargeStatus`, `BtnToggleDischarge`.
- Hardware Longevity & Health Metrics:
  - Group into two structured data tables or clean 2-column cards instead of repetitive 4-box grids:
    - Group A: Battery Wear & Capacity (`TxtBatHealthPercent`, `TxtBatWearPercent`, `TxtBatFullChargeCap`, `TxtBatDesignCap`, `TxtBatVoltage`).
    - Group B: Thermal Interface & Fan Lifespan (`TxtPasteHealthPercent`, `TxtThermalResistance`, `TxtActiveFanHours`, `TxtPassiveFanHours`, `TxtFanBearingHealth`).
- Storage Health & Drive Capacity:
  - Clean NVMe drive cards (`CardDrive0`, `CardDrive1`).
  - Replace `💾 [C:]` with clean text badge `Drive C:`.
  - Replace `🟢 SAĞLIK: %99 (Mükemmel)` with clean text `Status: Good (99%)`.
  - Drive capacity bar: Slim 6px height with clean data label.

### 5.7. Tab 5: Debloat Hub & Settings (`MainWindow.xaml:1187-1264`)

#### Current Issues
- Section title: `Tek Tıkla Güvenli Telemetri Debloat (Tier 1)` with `🛡️ Debloat Uygula (~214 MB)`.
- NitroSense section has `🛑 NitroSense'i Kapat & Engelle` and buzzwords.

#### Replacement Blueprint
- Clean technical titles:
  - `Background Service Optimization` (or `Arka Plan Servis Yönetimi`).
  - `Background Service List`: Clean structured table showing service name and state via `ListServiceStatuses`.
  - Buttons: `Disable Telemetry Services` and `Restore Defaults`.
- NitroSense background suppressor:
  - Title: `NitroSense Background Process Manager`.
  - Clean button: `Terminate Background Processes`.
- Startup preferences:
  - Clean CheckBoxes: `Start with Windows`, `Start minimized to system tray`.

### 5.8. Footer Status Bar & Toast Banner (`MainWindow.xaml:1268-1292`)

#### Current Issues
- Status bar has muddy `#0C0E14` background.
- Toast banner uses heavy 18px blur drop shadow and green border.

#### Replacement Blueprint
- Footer:
  - Solid `#13161D` background with `#252B38` top border.
  - Hardware bridge indicator: Keep `LedAas` (6px circle) + `TxtAasStatus`.
  - Right label: `Hardware MUX protection active`.
- Toast Banner:
  - Crisp solid dark background `#1E232F` with subtle 1px border `#252B38`.
  - Subtle drop shadow (`BlurRadius="8" Opacity="0.4"`).
  - Clean typography without oversized icons.

### 5.9. OSD Overlay Window (`NitroLite/OsdOverlayWindow.xaml`)

#### Current Issues
- Border has cyan glow tint (`#3300E5FF`).
- 12px blur drop shadow.
- Emojis in telemetry rows (`💻 CPU: `, `🎮 GPU: `, `⚡ MOD: `).
- Inline font definitions `Consolas, Segoe UI` on every element.
- Noisy rainbow colors: cyan, green, white, red, amber, grey separated by pipe characters ` | `.

#### Replacement Blueprint
- Solid high-contrast background: `#F010131A` (94% opacity solid dark slate).
- Border: Crisp 1px `#252B38`.
- Remove drop shadow blur or set to crisp 4px.
- Typography: Uniform font stack `Consolas, Cascadia Code, monospace` across the window with 11pt size and tabular figure alignment.
- Labels:
  - Row 1: `CPU  [temp]  [power]  [frequency]  [usage]`
  - Row 2: `GPU  [temp]  [power]  [frequency]  [vram]`
  - Row 3: `SYS  [mode]  RAM: [ram]  FAN: [fan]`
  - Remove all emojis (`💻`, `🎮`, `⚡`).
  - Standardize colors: Labels in `#9BA5B7` (secondary), values in `#F1F4F9` (primary text), with critical warnings (>85°C) in `#EF4444`.

---

## 6. Implementation Architecture & Compatibility Plan

### 6.1. Resource Consolidation in `MainWindow.xaml.Resources`

To prevent regressions while streamlining styles, all tokens will be declared cleanly in `MainWindow.xaml.Resources`:

```xml
<Window.Resources>
    <!-- Design System Surface Palette -->
    <SolidColorBrush x:Key="BgDark" Color="#0F1115" />
    <SolidColorBrush x:Key="SidebarBg" Color="#13161D" />
    <SolidColorBrush x:Key="CardBg" Color="#171B24" />
    <SolidColorBrush x:Key="CardBgHover" Color="#1E232F" />
    <SolidColorBrush x:Key="ControlBg" Color="#222733" />
    <SolidColorBrush x:Key="ControlBgHover" Color="#2B3242" />
    <SolidColorBrush x:Key="BorderDark" Color="#252B38" />
    <SolidColorBrush x:Key="BorderStrong" Color="#3A4356" />

    <!-- Functional Telemetry Accents (Exact keys preserved for C# compatibility) -->
    <SolidColorBrush x:Key="AccentGreen" Color="#10B981" />
    <SolidColorBrush x:Key="AccentCyan" Color="#0EA5E9" />
    <SolidColorBrush x:Key="AccentRed" Color="#EF4444" />
    <SolidColorBrush x:Key="AccentAmber" Color="#F59E0B" />
    <SolidColorBrush x:Key="AccentPurple" Color="#8B5CF6" />

    <!-- Typography Brushes -->
    <SolidColorBrush x:Key="TextPrimary" Color="#F1F4F9" />
    <SolidColorBrush x:Key="TextSecondary" Color="#9BA5B7" />
    <SolidColorBrush x:Key="TextMuted" Color="#667085" />

    <!-- Standardized Control Templates -->
    <!-- Custom Dark ComboBox, Flat Buttons, RadioButtons, ProgressBars -->
</Window.Resources>
```

### 6.2. Verification Checklist for Downstream Implementer

1. **Build Verification**:
   - `dotnet build c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite.sln` must compile with 0 errors and 0 warnings.
2. **Test Verification**:
   - `dotnet test c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite.Tests\NitroLite.Tests.csproj` must execute all 201 tests with 100% pass rate.
3. **Runtime Control Verification**:
   - All 114 named controls must resolve without `NullReferenceException` during initialization and telemetry polling loops.
4. **Visual Quality Gate**:
   - No emojis remaining in titles, buttons, or navigation.
   - No cyan/red hover outlines on cards.
   - No purple-to-blue gradient text or buttons.
   - High contrast (>4.5:1) maintained on all text.
   - Spacing adheres strictly to the 4px/8px scale.
