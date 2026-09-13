# Install And Update

## Prerequisites

- Windows 10/11 or supported Windows Server x64.
- SQL Server 2022 Express running locally.
- Administrator access for installation.

## Release

```powershell
.\deploy\Publish-Release.ps1 -Version 1.2.0
.\deploy\Install-WTT.ps1 -PackageRoot .\artifacts\WTT-1.2.0
```

The installer registers automatic delayed-start service `WTTData`, adds an SQL Server dependency, configures restart recovery, grants its virtual account least-privilege database access, creates a restricted local UI group, registers the daily verified backup task and creates a public desktop shortcut.

The default parameters target this machine's default SQL instance (`ServerInstance=.` and `SqlServiceName=MSSQLSERVER`). For a named Express instance use `-ServerInstance '.\SQLEXPRESS' -SqlServiceName 'MSSQL$SQLEXPRESS'`. Sign out and back in once after first installation so Windows applies new `WTTUsers` group membership to the UI token.

Use `Update-WTT.ps1` for an atomic service update with local rollback. Database backup and reviewed EF migrations must run before schema-changing releases. `Uninstall-WTT.ps1` intentionally preserves captured data, backups and logs.

Production releases should be Authenticode-signed. A publicly trusted Windows code-signing certificate is normally paid; WTT does not embed an unsafe self-signed substitute.
