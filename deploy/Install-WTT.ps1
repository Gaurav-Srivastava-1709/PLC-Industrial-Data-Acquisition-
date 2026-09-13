param(
    [Parameter(Mandatory = $true)][string]$PackageRoot,
    [string]$ServerInstance = ".",
    [string]$SqlServiceName = "MSSQLSERVER"
)

$ErrorActionPreference = "Stop"
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw "Run this installer as Administrator." }
New-Item -ItemType Directory -Force -Path "C:\ProgramData\WTT" | Out-Null
Start-Transcript -Path "C:\ProgramData\WTT\install.log" -Append | Out-Null
trap { $_ | Out-String | Add-Content -LiteralPath "C:\ProgramData\WTT\install.log"; Stop-Transcript | Out-Null; exit 1 }

$serviceName = "WTTData"
$operatorsGroup = "WTTUsers"
$installRoot = "C:\Program Files\WTT"
$serviceRoot = "$installRoot\Service"
$uiRoot = "$installRoot\UI"
$toolsRoot = "$installRoot\Tools"
if (-not (Test-Path -LiteralPath $PackageRoot)) { throw "Package root not found: $PackageRoot" }
if (-not (Test-Path -LiteralPath "$PackageRoot\Service\WTT.Service.exe")) { throw "Service executable is missing." }
if (-not (Test-Path -LiteralPath "$PackageRoot\UI\WTT.exe")) { throw "UI executable is missing." }
if (-not (Test-Path -LiteralPath "$PackageRoot\Database\WTT.Database.exe")) { throw "Database migration bundle is missing." }
if (-not (Test-Path -LiteralPath "$PackageRoot\deploy\Provision-Database.sql")) { throw "Database provisioning script is missing." }

if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $serviceName -Force
}
New-Item -ItemType Directory -Force -Path "C:\ProgramData\WTT\Logs", "C:\ProgramData\WTT\Backups" | Out-Null
icacls "C:\ProgramData\WTT\Backups" /grant "NT SERVICE\$SqlServiceName`:(OI)(CI)M" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Could not grant SQL Server backup access." }
sqlcmd -S $ServerInstance -E -C -b -i "$PackageRoot\deploy\Provision-Database.sql"
if ($LASTEXITCODE -ne 0) { throw "Database provisioning failed." }
& "$PackageRoot\deploy\Backup-Wtt.ps1" -Server $ServerInstance -Database "WTT" -BackupDirectory "C:\ProgramData\WTT\Backups" -RetentionDays 30
& "$PackageRoot\Database\WTT.Database.exe" --connection "Server=$ServerInstance;Database=WTT;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;Application Name=WTT Installer"
if ($LASTEXITCODE -ne 0) { throw "Database migration failed." }

if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    sc.exe delete $serviceName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Existing service removal failed: $LASTEXITCODE" }
    Start-Sleep -Seconds 2
}
net.exe localgroup $operatorsGroup | Out-Null
if ($LASTEXITCODE -ne 0) {
    net.exe localgroup $operatorsGroup /add | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not create local WTT users group." }
}
net.exe localgroup $operatorsGroup "$env:USERDOMAIN\$env:USERNAME" /add | Out-Null
if ($LASTEXITCODE -notin 0, 2) { throw "Could not add installer user to WTT users group." }
New-Item -ItemType Directory -Force -Path $serviceRoot, $uiRoot, $toolsRoot, "C:\ProgramData\WTT\Logs", "C:\ProgramData\WTT\Backups" | Out-Null
Copy-Item "$PackageRoot\Service\*" $serviceRoot -Recurse -Force
Copy-Item "$PackageRoot\UI\*" $uiRoot -Recurse -Force
Copy-Item "$PackageRoot\deploy\Backup-Wtt.ps1" $toolsRoot -Force
if (-not (Test-Path -LiteralPath "C:\ProgramData\WTT\installation-id")) { [guid]::NewGuid().ToString("D") | Set-Content -LiteralPath "C:\ProgramData\WTT\installation-id" -Encoding ASCII }

sc.exe create $serviceName binPath= "`"$serviceRoot\WTT.Service.exe`"" start= delayed-auto obj= "NT SERVICE\$serviceName" DisplayName= "WTT Industrial Data Service" depend= $SqlServiceName | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Service creation failed: $LASTEXITCODE" }
sc.exe description $serviceName "Unattended PLC capture and SQL Server transfer service." | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Service description failed: $LASTEXITCODE" }
sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Service recovery configuration failed: $LASTEXITCODE" }

$groupLogin = "$env:COMPUTERNAME\$operatorsGroup"
$sql = "IF NOT EXISTS(SELECT 1 FROM sys.server_principals WHERE name=N'NT SERVICE\$serviceName') CREATE LOGIN [NT SERVICE\$serviceName] FROM WINDOWS; IF NOT EXISTS(SELECT 1 FROM sys.server_principals WHERE name=N'$groupLogin') CREATE LOGIN [$groupLogin] FROM WINDOWS; IF NOT EXISTS(SELECT 1 FROM sys.server_principals WHERE name=N'NT AUTHORITY\SYSTEM') CREATE LOGIN [NT AUTHORITY\SYSTEM] FROM WINDOWS; USE [WTT]; IF NOT EXISTS(SELECT 1 FROM sys.database_principals WHERE name=N'NT SERVICE\$serviceName') CREATE USER [NT SERVICE\$serviceName] FOR LOGIN [NT SERVICE\$serviceName]; IF NOT EXISTS(SELECT 1 FROM sys.database_principals WHERE name=N'$groupLogin') CREATE USER [$groupLogin] FOR LOGIN [$groupLogin]; IF NOT EXISTS(SELECT 1 FROM sys.database_principals WHERE name=N'NT AUTHORITY\SYSTEM') CREATE USER [NT AUTHORITY\SYSTEM] FOR LOGIN [NT AUTHORITY\SYSTEM]; ALTER ROLE [wtt_runtime] ADD MEMBER [NT SERVICE\$serviceName]; ALTER ROLE [wtt_runtime] ADD MEMBER [$groupLogin]; ALTER ROLE [db_backupoperator] ADD MEMBER [NT AUTHORITY\SYSTEM];"
sqlcmd -S $ServerInstance -E -C -b -Q $sql
if ($LASTEXITCODE -ne 0) { throw "SQL service-account provisioning failed." }

icacls "C:\ProgramData\WTT" /inheritance:r /grant:r "SYSTEM:(OI)(CI)F" "BUILTIN\Administrators:(OI)(CI)F" "BUILTIN\Users:(OI)(CI)RX" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "ProgramData root ACL failed." }
icacls "C:\ProgramData\WTT\Logs" /inheritance:r /grant:r "SYSTEM:(OI)(CI)F" "BUILTIN\Administrators:(OI)(CI)F" "NT SERVICE\$serviceName`:(OI)(CI)M" "$groupLogin`:(OI)(CI)R" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Log ACL failed." }
icacls "C:\ProgramData\WTT\Backups" /inheritance:r /grant:r "SYSTEM:(OI)(CI)F" "BUILTIN\Administrators:(OI)(CI)F" "NT SERVICE\$SqlServiceName`:(OI)(CI)M" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Backup ACL failed." }
Start-Service -Name $serviceName
$deadline = (Get-Date).AddSeconds(60)
do { Start-Sleep -Seconds 2; $serviceStatus = (Get-Service $serviceName).Status } while ($serviceStatus -ne "Running" -and (Get-Date) -lt $deadline)
if ($serviceStatus -ne "Running") { throw "WTT service did not remain running." }

$taskAction = New-ScheduledTaskAction -Execute "powershell.exe" -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$toolsRoot\Backup-Wtt.ps1`""
$taskTrigger = New-ScheduledTaskTrigger -Daily -At "02:00"
$taskPrincipal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest
Register-ScheduledTask -TaskName "WTT Database Backup" -Action $taskAction -Trigger $taskTrigger -Principal $taskPrincipal -Description "Daily verified WTT SQL Server backup" -Force | Out-Null

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut("$env:PUBLIC\Desktop\WTT.lnk")
$shortcut.TargetPath = "$uiRoot\WTT.exe"
$shortcut.WorkingDirectory = $uiRoot
$shortcut.Save()
"WTT installed. Service status: $((Get-Service $serviceName).Status)"
Stop-Transcript | Out-Null
