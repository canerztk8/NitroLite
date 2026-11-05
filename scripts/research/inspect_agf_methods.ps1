# Inspect AcerGamingFunction SetGamingMiscSetting parameters
$ErrorActionPreference = "Continue"

$agf = Get-CimClass -Namespace "root\wmi" -ClassName "AcerGamingFunction"
$misc = $agf.CimClassMethods | Where-Object { $_.Name -like "*Misc*" }
$misc | Format-List Name, ReturnType, Parameters

$fan = $agf.CimClassMethods | Where-Object { $_.Name -like "*Fan*" }
$fan | Select-Object Name

$prof = $agf.CimClassMethods | Where-Object { $_.Name -like "*Profile*" }
$prof | Select-Object Name
