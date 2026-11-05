$xaml = Get-Content 'NitroLite\MainWindow.xaml' -Raw

$requiredTags = @(
    'Quiet', 'Default', 'Extreme', 'Turbo',
    'Aggressive', 'Efficient', 'Disabled',
    'CPU_TEMP', 'GPU_TEMP', 'CPU_WATT', 'GPU_WATT', 'CPU_GHZ', 'CPU_USAGE', 'GPU_USAGE', 'RAM_USAGE', 'VRAM_GB',
    '#00E5FF', '#FF2A55', '#00FFA3', '#BD00FF', '#FFAA00', '#FFFFFF', '#000000',
    '14', '30',
    'Auto', 'Max'
)

$missingTags = @()
foreach ($tag in $requiredTags) {
    if (-not $xaml.Contains("Tag=""$tag""")) {
        $missingTags += $tag
    }
}

Write-Output "Required Tag count: $($requiredTags.Count)"
Write-Output "Missing Tags in MainWindow.xaml: $(if ($missingTags.Count -eq 0) { 'NONE (100% Intact)' } else { $missingTags -join ', ' })"
