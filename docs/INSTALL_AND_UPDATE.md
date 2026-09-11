# Install And Update

## Prerequisites

- Windows 10/11 or supported Windows Server x64.
- SQL Server 2022 Express running locally.
- Administrator access for installation.

## Release

```powershell
.\deploy\Publish-Release.ps1 -Version 1.1.0
.\deploy\Install-WTT.ps1 -PackageRoot .\artifacts\WTT-1.1.0
```

The installer registers automatic delayed-start service `WTTData`, configures restart recovery, grants its virtual account least-privilege database access and creates a public desktop shortcut.

Use `Update-WTT.ps1` for an atomic service update with local rollback. Database backup and reviewed EF migrations must run before schema-changing releases. `Uninstall-WTT.ps1` intentionally preserves captured data, backups and logs.

Production releases should be Authenticode-signed. A publicly trusted Windows code-signing certificate is normally paid; WTT does not embed an unsafe self-signed substitute.
