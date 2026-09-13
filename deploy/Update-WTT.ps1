param(
    [Parameter(Mandatory = $true)][string]$PackageRoot,
    [string]$ServerInstance = ".",
    [string]$SqlServiceName = "MSSQLSERVER"
)

$ErrorActionPreference = "Stop"
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw "Run this updater as Administrator." }
$serviceName = "WTTData"
$installRoot = "C:\Program Files\WTT"
$rollback = "$env:ProgramData\WTT\Rollback\$(Get-Date -Format yyyyMMdd-HHmmss)"
if (-not (Test-Path -LiteralPath $PackageRoot)) { throw "Package root not found." }
if (-not (Test-Path -LiteralPath "$PackageRoot\Service\WTT.Service.exe") -or -not (Test-Path -LiteralPath "$PackageRoot\UI\WTT.exe") -or -not (Test-Path -LiteralPath "$PackageRoot\Database\WTT.Database.exe")) { throw "Update package is incomplete." }
if (Get-Process -Name "WTT" -ErrorAction SilentlyContinue) { throw "Close the WTT UI before updating." }
New-Item -ItemType Directory -Force -Path $rollback | Out-Null

Stop-Service $serviceName -Force
Copy-Item "$installRoot\*" $rollback -Recurse -Force
$backupScript = if (Test-Path -LiteralPath "$installRoot\Tools\Backup-Wtt.ps1") { "$installRoot\Tools\Backup-Wtt.ps1" } else { "$PackageRoot\deploy\Backup-Wtt.ps1" }
& $backupScript -Server "." -Database "WTT" -BackupDirectory "C:\ProgramData\WTT\Backups" -RetentionDays 30
$databaseBackup = Get-ChildItem -LiteralPath "C:\ProgramData\WTT\Backups" -Filter "WTT-*.bak" | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
if (-not $databaseBackup) { throw "Pre-update database backup was not created." }
try
{
    & "$PackageRoot\Database\WTT.Database.exe" --connection "Server=$ServerInstance;Database=WTT;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;Application Name=WTT Updater"
    if ($LASTEXITCODE -ne 0) { throw "Database migration failed." }
    Remove-Item "$installRoot\Service", "$installRoot\UI" -Recurse -Force
    New-Item -ItemType Directory -Path "$installRoot\Service", "$installRoot\UI" | Out-Null
    Copy-Item "$PackageRoot\Service\*" "$installRoot\Service" -Recurse -Force
    Copy-Item "$PackageRoot\UI\*" "$installRoot\UI" -Recurse -Force
    Start-Service $serviceName
    Start-Sleep -Seconds 20
    if ((Get-Service $serviceName).Status -ne "Running") { throw "Updated service did not start." }
    "Update completed. Rollback copy: $rollback"
}
catch
{
    Stop-Service $serviceName -Force -ErrorAction SilentlyContinue
    Remove-Item "$installRoot\Service", "$installRoot\UI" -Recurse -Force -ErrorAction SilentlyContinue
    Copy-Item "$rollback\*" $installRoot -Recurse -Force
    $path = $databaseBackup.FullName.Replace("'", "''")
    sqlcmd -S $ServerInstance -E -C -b -d master -Q "ALTER DATABASE [WTT] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; RESTORE DATABASE [WTT] FROM DISK=N'$path' WITH REPLACE, RECOVERY; ALTER DATABASE [WTT] SET MULTI_USER;"
    if ($LASTEXITCODE -ne 0) { throw "Database rollback failed; service remains stopped. Manual recovery required from $($databaseBackup.FullName)." }
    Start-Service $serviceName
    throw
}
