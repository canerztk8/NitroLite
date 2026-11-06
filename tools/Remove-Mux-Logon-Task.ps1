# Removes the "AcerNitroSenseMuxRestore" logon task created by older versions of
# Fix-NitroSense-Service.ps1 / Restore-Mux-Setting.ps1. That task rewrote
# HKLM\SOFTWARE\OEM\AcerAgentService\AdvanceSettings\discrete_gpu_support with highest privileges on every logon.
# Run from an elevated PowerShell.
$ErrorActionPreference = "Stop"

$taskName = "AcerNitroSenseMuxRestore"
$task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($null -eq $task) {
    Write-Host "Gorev bulunamadi: $taskName (kaldirilacak bir sey yok)." -ForegroundColor Green
    return
}

Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
    throw "Gorev kaldirilamadi: $taskName"
}
Write-Host "Gorev kaldirildi: $taskName" -ForegroundColor Green
