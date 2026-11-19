. "$PSScriptRoot\lib.ps1"
$out = Join-Path $PSScriptRoot 'tree'; New-Item -ItemType Directory -Force $out | Out-Null
function Inv($el) { try { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch {} }
Start-NL
try {
  Dump-Tree "$out\start.txt"
  foreach ($p in 'NavModes','NavLighting','NavMonitoring','NavBattery','NavTools','NavGames','NavSettings','NavDashboard') {
    $e = Find-ById $p; if (-not $e) { "missing $p"; continue }
    Inv $e; Start-Sleep -Milliseconds 1500; Dump-Tree "$out\$p.txt"; "dumped $p"
  }
} finally { Stop-NL }
