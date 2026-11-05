$names136 = @(
    # Window & Toast
    "ToastBanner", "TxtToastIcon", "TxtToastMessage",
    # Titlebar Badges
    "TxtBatteryHeader", "TxtModeHeader",
    # Sidebar Navigation
    "NavModes", "NavMonitoring", "NavRgb", "NavBattery", "NavDebloat",
    "TxtSideCpuTemp", "TxtSideGpuTemp", "TxtSideCpuFan",
    # Tab Containers
    "TabModes", "TabMonitoring", "TabRgb", "TabBattery", "TabDebloat",
    # Operating Modes
    "TxtIndQuiet", "TxtIndDefault", "TxtIndExtreme", "TxtIndTurbo",
    # Fan Telemetry
    "TxtCpuFanRpm", "BarCpuFan", "TxtGpuFanRpm", "BarGpuFan",
    # CPU Boost
    "TxtBoostStatusBadge",
    # Game Detection
    "TxtGameStatus", "ChkAutoGameMode", "CmbConfiguredGames",
    "TxtSelectedGameNameBadge", "TxtSelectedGameExePath",
    "CmbGameTargetMode", "CmbGameTargetCpuBoost", "CmbGameTargetFan",
    # OSD Overlay
    "BtnToggleOsd",
    # Live Telemetry (Tab 2)
    "TxtCpuCoreCount", "TxtMonCpuTemp", "BarMonCpuTemp", "TxtMonCpuPower", "BarMonCpuPower",
    "TxtMonCpuUsage", "BarMonCpuUsage", "TxtMonCpuFreqGhz", "TxtMonGpuTemp", "BarMonGpuTemp",
    "TxtMonGpuPower", "BarMonGpuPower", "TxtMonGpuUsage", "BarMonGpuUsage", "TxtMonVram",
    "BarMonVram", "TxtMonRamDetail", "TxtMonRamPercent", "BarMonRam", "TxtMonSysTemp",
    "TxtRamCleanStatus", "ChkAutoRamClean",
    # Telemetry History Graph
    "CmbChartMetric", "CanvasHistoryChart", "ChartArea", "ChartLine",
    "TxtChartCurrentVal", "TxtChartMinVal", "TxtChartMaxVal", "TxtChartAvgVal",
    # RGB Lighting (Tab 3)
    "PreviewZone1", "PreviewZone2", "PreviewZone3", "PreviewZone4",
    "CmbRgbPresets", "TxtCustomPresetName", "CmbRgbEffect", "ChkBacklightTimeout",
    "TxtBrightnessVal", "SliderBrightness", "TxtSpeedVal", "SliderSpeed",
    # Battery Health (Tab 4)
    "TxtBatLevelBig", "TxtBatStateDesc", "BarBatBig", "TxtBatDischargeRate", "TxtBatRemainingTime",
    "ChkWatchdog", "TxtDischargeStatus", "BtnToggleDischarge", "TxtBatHealthPercent",
    "TxtBatWearPercent", "TxtBatFullChargeCap", "TxtBatDesignCap", "TxtBatVoltage",
    "BannerOvercharge", "TxtOverchargeMsg",
    # Thermal Health Report
    "TxtThermalBadge", "TxtPasteHealthPercent", "TxtThermalResistance", "TxtActiveFanHours",
    "TxtAvgFanRpm", "TxtPassiveFanHours", "TxtFanBearingHealth", "TxtStabilityDaysSubtitle",
    "RadioTrend14Days", "RadioTrend30Days", "TxtStabilityScore", "TxtStabilityStatus",
    "TxtStabilitySlope", "TxtStabilityBadge", "BarStabilityScore", "TxtThermalRecommendation",
    # Sub-Component Matrix
    "TxtSubMoboTemp", "TxtSubSsd1Temp", "TxtSubSsd2Temp", "TxtSubBatteryTemp",
    # Storage Health
    "TxtStorageBadge", "CardDrive0", "TxtDrive0Letter", "TxtDrive0Name", "TxtDrive0Bus",
    "TxtDrive0Temp", "TxtDrive0HealthBadge", "TxtDrive0Detail", "TxtDrive0Free", "BarDrive0",
    "CardDrive1", "TxtDrive1Letter", "TxtDrive1Name", "TxtDrive1Bus", "TxtDrive1Temp",
    "TxtDrive1HealthBadge", "TxtDrive1Detail", "TxtDrive1Free", "BarDrive1",
    # Debloat & Settings (Tab 5)
    "ListServiceStatuses", "TxtNitroSenseRunningBadge", "ChkAutoStart", "ChkStartMinimized",
    # Status Bar Footer
    "LedAas", "TxtAasStatus"
)

$xaml = Get-Content -LiteralPath "c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite\MainWindow.xaml" -Raw -Encoding UTF8

$missing = @()
foreach ($name in $names136) {
    if ($xaml -notmatch "x:Name=`"$name`"") {
        $missing += $name
    }
}

Write-Host "Target 136 list count: $($names136.Count)"
Write-Host "Missing count: $($missing.Count)"
if ($missing.Count -gt 0) {
    Write-Host "Missing names: $($missing -join ', ')"
} else {
    Write-Host "ALL 136 code-behind x:Name elements are 100% present in MainWindow.xaml!"
}
