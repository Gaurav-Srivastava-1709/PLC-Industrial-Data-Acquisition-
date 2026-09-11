param([Parameter(Mandatory = $true)][string]$PackageRoot)

$ErrorActionPreference = "Stop"
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw "Run this installer as Administrator." }

$serviceName = "WTTData"
$installRoot = "C:\Program Files\WTT"
$serviceRoot = "$installRoot\Service"
$uiRoot = "$installRoot\UI"
if (-not (Test-Path -LiteralPath $PackageRoot)) { throw "Package root not found: $PackageRoot" }
if (-not (Test-Path -LiteralPath "$PackageRoot\Service\WTT.Service.exe")) { throw "Service executable is missing." }
if (-not (Test-Path -LiteralPath "$PackageRoot\UI\WTT.exe")) { throw "UI executable is missing." }
if (-not (Test-Path -LiteralPath "$PackageRoot\Database\WTT.Database.exe")) { throw "Database migration bundle is missing." }

sqlcmd -S "." -E -C -b -i "$PackageRoot\deploy\Provision-Database.sql"
if ($LASTEXITCODE -ne 0) { throw "Database provisioning failed." }
& "$PackageRoot\Database\WTT.Database.exe" --connection "Server=.;Database=WTT;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;Application Name=WTT Installer"
if ($LASTEXITCODE -ne 0) { throw "Database migration failed." }

if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $serviceName -Force
    sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 2
}
New-Item -ItemType Directory -Force -Path $serviceRoot, $uiRoot, "C:\ProgramData\WTT\Logs", "C:\ProgramData\WTT\Backups" | Out-Null
Copy-Item "$PackageRoot\Service\*" $serviceRoot -Recurse -Force
Copy-Item "$PackageRoot\UI\*" $uiRoot -Recurse -Force

sc.exe create $serviceName binPath= "`"$serviceRoot\WTT.Service.exe`"" start= delayed-auto obj= "NT SERVICE\$serviceName" DisplayName= "WTT Industrial Data Service" | Out-Null
sc.exe description $serviceName "Unattended PLC capture and SQL Server transfer service." | Out-Null
sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null

$sql = "IF NOT EXISTS(SELECT 1 FROM sys.server_principals WHERE name=N'NT SERVICE\$serviceName') CREATE LOGIN [NT SERVICE\$serviceName] FROM WINDOWS; USE [WTT]; IF NOT EXISTS(SELECT 1 FROM sys.database_principals WHERE name=N'NT SERVICE\$serviceName') CREATE USER [NT SERVICE\$serviceName] FOR LOGIN [NT SERVICE\$serviceName]; ALTER ROLE [wtt_runtime] ADD MEMBER [NT SERVICE\$serviceName];"
sqlcmd -S "." -E -C -b -Q $sql
if ($LASTEXITCODE -ne 0) { throw "SQL service-account provisioning failed." }

icacls "C:\ProgramData\WTT" /grant "NT SERVICE\$serviceName`:(OI)(CI)M" | Out-Null
Start-Service -Name $serviceName

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut("$env:PUBLIC\Desktop\WTT.lnk")
$shortcut.TargetPath = "$uiRoot\WTT.exe"
$shortcut.WorkingDirectory = $uiRoot
$shortcut.Save()
"WTT installed. Service status: $((Get-Service $serviceName).Status)"
