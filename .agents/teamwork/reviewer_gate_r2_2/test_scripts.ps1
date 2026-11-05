$reportLines = Get-Content "nitro_sense_analysis.md"

# Block 1: Debloat script (lines 642 to 685 in 1-based, 641 to 684 in 0-based)
$debloatScript = ($reportLines[641..684]) -join [Environment]::NewLine
$errors1 = $null
$tokens1 = $null
[System.Management.Automation.Language.Parser]::ParseInput($debloatScript, [ref]$tokens1, [ref]$errors1)
Write-Host "Debloat Script AST Errors:" ($errors1.Count)
if ($errors1) {
    $errors1 | ForEach-Object { Write-Host "  Error: $($_.Message) at line $($_.Extent.StartLineNumber)" }
}

# Block 2: On-demand task disable (lines 697 to 701 in 1-based, 696 to 700 in 0-based)
$taskScript = ($reportLines[696..700]) -join [Environment]::NewLine
$errors2 = $null
$tokens2 = $null
[System.Management.Automation.Language.Parser]::ParseInput($taskScript, [ref]$tokens2, [ref]$errors2)
Write-Host "Task Script AST Errors:" ($errors2.Count)

# Block 3: Rollback script (lines 739 to 780 in 1-based, 738 to 779 in 0-based)
$rollbackScript = ($reportLines[738..779]) -join [Environment]::NewLine
$errors3 = $null
$tokens3 = $null
[System.Management.Automation.Language.Parser]::ParseInput($rollbackScript, [ref]$tokens3, [ref]$errors3)
Write-Host "Rollback Script AST Errors:" ($errors3.Count)
if ($errors3) {
    $errors3 | ForEach-Object { Write-Host "  Error: $($_.Message) at line $($_.Extent.StartLineNumber)" }
}

# Block 4: Post-restoration check command (line 809 in 1-based, 808 in 0-based)
$verifyCmd = $reportLines[808]
$errors4 = $null
$tokens4 = $null
[System.Management.Automation.Language.Parser]::ParseInput($verifyCmd, [ref]$tokens4, [ref]$errors4)
Write-Host "Verification Command AST Errors:" ($errors4.Count)
