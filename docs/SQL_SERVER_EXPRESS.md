# Local SQL Server Express

WTT uses Microsoft SQL Server Express as its only persistence database. No alternate file-based provider, runtime, test dependency or compatibility database remains in the project.

## Runtime Configuration

Default connection:

```text
Server=.;Database=WTT;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;Application Name=WTT
```

- Windows Integrated Security is mandatory. WTT does not store a local database password.
- `AttachDbFilename` and LocalDB are rejected.
- `TrustServerCertificate=True` is accepted only for a same-machine SQL Express connection. If TCP/remote SQL is enabled, install a trusted SQL certificate and change it to `False`.
- Override configuration with `WTT_Database__ConnectionString` when required.

## Provisioning

Run from an administrator terminal:

```powershell
sqlcmd -S "." -E -C -b -i "deploy\Provision-Database.sql"
dotnet ef database update --project "src\WTT.Infrastructure\WTT.Infrastructure.csproj" --startup-project "src\WTT.Infrastructure\WTT.Infrastructure.csproj"
```

The provisioning script configures:

- Explicit Unicode, case-insensitive collation for a new database.
- `SIMPLE` recovery, checksum page verification, no auto-close and no auto-shrink.
- Fixed 128 MB data-file growth and a 9.5 GB ceiling below the SQL Express 10 GB limit.
- A least-privilege `wtt_runtime` database role. Add the final Windows service login to this role during deployment.

Normal runtime identities should receive only the `wtt_runtime` role. Schema migrations must run under a separate deployment administrator.

The release installer accepts both default and named SQL instances. `WTTData` owns capture/transfer; WPF only reads persisted heartbeat, status and tag values, so closing the UI does not stop acquisition.

## Completed Data Cutover

The retired local file database was migrated into SQL Server in one transaction with identity and row-count verification. After a checksum backup and successful full restore drill, the importer, provider package and old database files were removed. `WTT` is the only application database.

## Backup

SQL Server Express has no SQL Agent. Schedule `deploy\Backup-Wtt.ps1` with Windows Task Scheduler under a restricted maintenance identity. Store a second encrypted copy off the machine and regularly perform a full restore drill; `RESTORE VERIFYONLY` alone is not a disaster-recovery test.

Before the first backup, create `C:\ProgramData\WTT` and grant only the SQL instance service SID the required write access:

```powershell
New-Item -ItemType Directory -Force "C:\ProgramData\WTT\Backups"
icacls "C:\ProgramData\WTT" /grant "NT SERVICE\MSSQLSERVER:(OI)(CI)M"
```

For a named Express instance, use its service SID such as `NT SERVICE\MSSQL$WTTEXPRESS` instead.

Recommended minimum:

- Daily full backup with checksum.
- 30-day local retention and an off-machine copy.
- Alert at 8 GB and stop new capture before 9 GB if transfer/retention cannot drain data.
- Never silently delete untransferred readings.
