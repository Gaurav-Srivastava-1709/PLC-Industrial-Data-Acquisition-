param([Parameter(Mandatory = $true)][string]$PackageRoot)

$ErrorActionPreference = "Stop"
$serviceName = "WTTData"
$installRoot = "C:\Program Files\WTT"
$rollback = "$env:ProgramData\WTT\Rollback\$(Get-Date -Format yyyyMMdd-HHmmss)"
if (-not (Test-Path -LiteralPath $PackageRoot)) { throw "Package root not found." }
New-Item -ItemType Directory -Force -Path $rollback | Out-Null

Stop-Service $serviceName -Force
Copy-Item "$installRoot\*" $rollback -Recurse -Force
try
{
    Copy-Item "$PackageRoot\Service\*" "$installRoot\Service" -Recurse -Force
    Copy-Item "$PackageRoot\UI\*" "$installRoot\UI" -Recurse -Force
    Start-Service $serviceName
    if ((Get-Service $serviceName).Status -ne "Running") { throw "Updated service did not start." }
    "Update completed. Rollback copy: $rollback"
}
catch
{
    Copy-Item "$rollback\*" $installRoot -Recurse -Force
    Start-Service $serviceName
    throw
}
