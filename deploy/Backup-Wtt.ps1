param(
    [string]$Server = ".",
    [string]$Database = "WTT",
    [string]$BackupDirectory = "C:\ProgramData\WTT\Backups",
    [int]$RetentionDays = 30
)

$ErrorActionPreference = "Stop"
if (-not (Test-Path -LiteralPath (Split-Path -Parent $BackupDirectory))) {
    throw "Backup parent directory does not exist: $(Split-Path -Parent $BackupDirectory)"
}
if (-not (Test-Path -LiteralPath $BackupDirectory)) {
    New-Item -ItemType Directory -Path $BackupDirectory | Out-Null
}

$sqlcmd = Get-Command sqlcmd -ErrorAction Stop
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$backupPath = Join-Path $BackupDirectory "$Database-$stamp.bak"
$escapedPath = $backupPath.Replace("'", "''")
$query = "BACKUP DATABASE [$Database] TO DISK=N'$escapedPath' WITH COPY_ONLY, CHECKSUM, INIT; RESTORE VERIFYONLY FROM DISK=N'$escapedPath' WITH CHECKSUM;"

& $sqlcmd.Source -S $Server -E -C -b -Q $query
if ($LASTEXITCODE -ne 0) { throw "SQL Server backup or verification failed." }

Get-ChildItem -LiteralPath $BackupDirectory -Filter "$Database-*.bak" -File |
    Where-Object LastWriteTimeUtc -lt (Get-Date).ToUniversalTime().AddDays(-$RetentionDays) |
    Remove-Item -Force

"Verified backup created: $backupPath"
