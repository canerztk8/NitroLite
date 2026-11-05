# Inspect AcerGenericMethod in root\wmi
$ErrorActionPreference = "Continue"

$agmClass = Get-CimClass -Namespace "root\wmi" -ClassName "AcerGenericMethod"
Write-Host "Methods on AcerGenericMethod:"
$agmClass.CimClassMethods | Select-Object Name, ReturnType, Parameters

$instances = Get-CimInstance -Namespace "root\wmi" -ClassName "AcerGenericMethod"
Write-Host "Instances count: $($instances.Count)"
foreach ($inst in $instances) {
    Write-Host "Instance: $($inst.InstanceName)"
}
