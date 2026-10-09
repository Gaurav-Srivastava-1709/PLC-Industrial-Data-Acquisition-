# Local SQL Server Express

**Version:** 1.2.0
**Scope:** Everything about the WTT database — connection, provisioning, schema, permissions, sizing, backup, restore and troubleshooting
**Audience:** DBAs, deployment engineers, support
**Related:** `docs/INSTALL_AND_UPDATE.md` (installer steps), `docs/ARCHITECTURE.md` §6 and §10 (schema and persistence internals), `docs/ADMIN_GUIDE.md` §8 and §10 (operational rules)

---

## Contents

1. [Why SQL Server Express](#1-why-sql-server-express)
2. [Runtime configuration](#2-runtime-configuration)
3. [Default vs named instances](#3-default-vs-named-instances)
4. [Provisioning a new database](#4-provisioning-a-new-database)
5. [Schema overview](#5-schema-overview)
6. [Indexes that matter](#6-indexes-that-matter)
7. [Permissions and roles](#7-permissions-and-roles)
8. [Migrations](#8-migrations)
9. [Sizing and the 10 GB ceiling](#9-sizing-and-the-10-gb-ceiling)
10. [Central destination database](#10-central-destination-database)
11. [Backup](#11-backup)
12. [Restore and restore drills](#12-restore-and-restore-drills)
13. [Completed data cutover](#13-completed-data-cutover)
14. [Verification queries](#14-verification-queries)
15. [Troubleshooting](#15-troubleshooting)
16. [Document history](#16-document-history)

---

## 1. Why SQL Server Express

WTT uses Microsoft SQL Server Express as its **only** persistence database. No alternate file-based provider, LocalDB, compatibility database or test-only engine remains in the project — the cutover is complete (§13).

Express is chosen because it is free, ships on Windows, and supports everything WTT needs: `datetime2(3)` precision, filtered indexes, covering indexes, `SqlBulkCopy`, `WITH (UPDLOCK, HOLDLOCK)` merge semantics and EF Core migrations.

**What Express does *not* give you:**

| Limit | Express | WTT's mitigation |
|---|---|---|
| 10 GB per database | hard ceiling | Provisioned `MAXSIZE=9500MB`; alert at 8 GB; retention purge |
| No SQL Server Agent | no native schedules | Windows Task Scheduler runs `Backup-Wtt.ps1` daily at 02:00 |
| No Service Broker / Availability Groups | — | Store-and-forward design keeps acquisition local |
| Limited CPU / memory use | 1 socket / 4 GB RAM | Acceptable for a single-plant buffer |

If you outgrow Express, move to SQL Server Standard — the connection string and EF migrations need no changes.

---

## 2. Runtime configuration

### 2.1 Default connection string

```text
Server=.;Database=WTT;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;Application Name=WTT;Connect Timeout=10;Pooling=True
```

Additional options set in code by `SqlServerConnectionStringFactory`:

```text
PersistSecurityInfo=False;MARS=False;Pooling=True;Application Name=WTT
```

### 2.2 Rules enforced at startup, not merely documented

`SqlServerConnectionStringFactory` builds the connection string and doubles as an options validator registered with `ValidateOnStart()`. A bad configuration therefore fails **at startup with a clear message**, not at the first query.

| Rule | Enforcement |
|---|---|
| **Windows Integrated Security is mandatory** | WTT does not store, read or accept a local database password |
| `AttachDbFilename` rejected | File-based databases are not supported |
| LocalDB rejected | Not a production target |
| `Encrypt=True` | Always on |
| `TrustServerCertificate=True` | Accepted **only** for a same-machine SQL Express connection |
| `Application Name=WTT` | Set so you can identify WTT sessions in `sys.dm_exec_sessions` |

### 2.3 Remote / TCP SQL

If you enable TCP endpoints or point WTT at a remote server:

1. Install a **trusted SQL certificate** on the server.
2. Change `TrustServerCertificate` to **`False`**.
3. Confirm `Encrypt=True` remains set.
4. Open TCP port 1433 (or your configured port) only between the WTT host and the SQL host.

`TrustServerCertificate=True` in production over a network disables certificate validation and is a man-in-the-middle exposure. It is for same-machine use only.

### 2.4 Overriding the connection string

Environment variable (works for both the UI and the service, prefix `WTT_`):

```powershell
$env:WTT_Database__ConnectionString = "Server=.\SQLEXPRESS;Database=WTT;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;Application Name=WTT"
```

Named instance example:

```text
Server=.\SQLEXPRESS;Database=WTT;Integrated Security=True;Encrypt=True;TrustServerCertificate=True
```

Remote example (with a trusted certificate):

```text
Server=10.0.0.25,1433;Database=WTT;Integrated Security=True;Encrypt=True;TrustServerCertificate=False;Application Name=WTT
```

Design-time (EF tools) reads `WTT_DESIGN_CONNECTION`, falling back to:

```text
Server=.;Database=WTT;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;Application Name=WTT EF Design
```

### 2.5 Connect timeout

The application uses `Connect Timeout=10` (10 seconds). SQL command timeout is 30 seconds. These are deliberately short so a dead database surfaces quickly rather than hanging a capture worker.

---

## 3. Default vs named instances

Everything defaults to the machine's **default instance** (`ServerInstance=.` and `SqlServiceName=MSSQLSERVER`).

SQL Server **Express** is very commonly installed as a **named instance**, typically `.\\SQLEXPRESS`.

| | Default instance | Named instance |
|---|---|---|
| `Server` in connection string | `.` or `localhost` | `.\SQLEXPRESS` |
| Windows service name | `MSSQLSERVER` | `MSSQL$SQLEXPRESS` |
| Install flag | *(none)* | `-ServerInstance '.\SQLEXPRESS' -SqlServiceName 'MSSQL$SQLEXPRESS'` |
| Backup ACL SID | `NT SERVICE\MSSQLSERVER` | `NT SERVICE\MSSQL$SQLEXPRESS` |
| `sqlcmd` | `sqlcmd -S "." -E -C -b` | `sqlcmd -S ".\SQLEXPRESS" -E -C -b` |

**All three places must agree** — connection string, installer `-SqlServiceName` (the service dependency) and the backup folder ACL. Getting the dependency name wrong is the most common cause of `WTTData` failing to start.

---

## 4. Provisioning a new database

Run from an **administrator** terminal:

```powershell
cd "D:\Spring Boot\PLC Project\WTT_PLC\New PLc"

# 1. Create and configure the database + least-privilege role
sqlcmd -S "." -E -C -b -i "deploy\Provision-Database.sql"

# 2. Apply the six EF Core migrations
dotnet ef database update `
    --project "src\WTT.Infrastructure\WTT.Infrastructure.csproj" `
    --startup-project "src\WTT.Infrastructure\WTT.Infrastructure.csproj"
```

Alternatively, launch the client — the UI runs `DatabaseInitializer.InitializeAsync()` → `Database.MigrateAsync()` on startup. **The service does not migrate**; it calls `VerifySchemaCurrentAsync()` and refuses to start if migrations are pending.

### 4.1 What `Provision-Database.sql` does

| Step | Detail |
|---|---|
| Create database | `CREATE DATABASE [$(DatabaseName)]` if missing (sqlcmd variable `DatabaseName`, default `WTT`) |
| Collation | `Latin1_General_100_CI_AS_SC` — explicit Unicode, case-insensitive |
| Recovery | `SIMPLE` (no log backup chain needed for a buffer database) |
| Page verify | `CHECKSUM` — detects torn pages |
| `AUTO_CLOSE` | **OFF** — closing on idle would drop connections and slow every poll cycle |
| `AUTO_SHRINK` | **OFF** — auto-shrink fragments indexes and causes file I/O churn |
| Data file | `FILEGROWTH=128MB`, `MAXSIZE=9500MB` — deliberately below the Express ceiling |
| Role | `CREATE ROLE [wtt_runtime]` |
| Grants | `GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::[dbo] TO [wtt_runtime]` |

### 4.2 Sanity checks after provisioning

```powershell
sqlcmd -S "." -E -C -b -Q "SELECT name, collation_name, recovery_model_desc FROM sys.databases WHERE name='WTT';"
sqlcmd -S "." -E -C -b -Q "SELECT r.name AS role_name FROM sys.database_principals r WHERE r.type='R';"
sqlcmd -S "." -E -C -b -Q "SELECT name FROM WTT.sys.tables ORDER BY name;"
sqlcmd -S "." -E -C -b -Q "SELECT MigrationId FROM WTT.dbo.__EFMigrationsHistory ORDER BY MigrationId;"
```

Expect 15 application tables plus `__EFMigrationsHistory` and the `wtt_runtime` role.

### 4.3 During installation

`Install-WTT.ps1` performs the same steps automatically (provision → backup → migrate → create logins) and grants `wtt_runtime` to:

- `NT SERVICE\WTTData` (the service virtual account)
- `<MACHINE>\WTTUsers` (the restricted local group)
- `NT AUTHORITY\SYSTEM`

plus `db_backupoperator` for the backup task.

---

## 5. Schema overview

Local database `WTT` — **15 application tables**:

| Table | Purpose | Key constraint |
|---|---|---|
| `Devices` | Device configuration | unique `Name` |
| `DeviceAddresses` | Modbus register blocks | — |
| `DeviceTags` | PLC / Custom / External tags | unique `(DeviceId, Name)` |
| `DeviceTriggerConditions` | Logical capture expression clauses | index `(DeviceId, Order)` |
| `DeviceQueries` | External read-only SQL sources | unique `(DeviceId, Name)` |
| `DeviceQueryColumns` | Columns discovered by Verify | — |
| `DeviceServers` | Central transfer destinations | unique `DeviceId` |
| `TagReadings` | **The historian buffer** | see §6 |
| `TransferBatches` | One row per transfer attempt | index `StartedAtUtc` |
| `Users` | Accounts (BCrypt hashes) | unique `NormalizedUsername` |
| `AuditLogs` | Security audit trail | index `TimestampUtc` |
| `AppSettings` | Runtime settings, key/value | `Key` is the primary key |
| `ServiceRuntimeState` | Heartbeat — exactly one row | `Id` never auto-generated |
| `DeviceRuntimeStates` | Per-device status counters | PK `DeviceId`, index `InstanceId` |
| `AlarmEvents` | ISA-18.2 alarm lifecycle | FK `DeviceTagId` cascade, 3 named indexes |

Plus `dbo.__EFMigrationsHistory`.

**There are no stored procedures, functions, views or triggers.** Verified: zero `CREATE PROCEDURE / FUNCTION / VIEW / TRIGGER` matches anywhere in `src`. All logic lives in the application.

### 5.1 Notable data types

| Column | Type | Note |
|---|---|---|
| `TagReadings.Timestamp`, `TimestampUtc` | `datetime2(3)` | millisecond precision |
| `TagReadings.Value` | `nvarchar(500)` | truncated by the capture worker |
| `TagReadings.ScanId` | `uniqueidentifier` | all rows of one poll share it |
| `TagReadings.Id` | `bigint` | identity — the `SourceReadingId` shipped centrally |
| `AppSettings.Value` | `nvarchar(4000)` max | |
| `TransferBatches.ErrorMessage` | `nvarchar(4000)` max | |
| `DeviceServers.ProtectedPassword` | `nvarchar(2000)` | DPAPI blob, prefix `dpapi:v1:` |
| `DeviceQueries.QueryText` | `nvarchar(8000)` max | |
| `Users.Role` | `nvarchar(20)` | stored as string |
| Enums on Devices/DeviceTags | `nvarchar` | `HasConversion<string>()` — readable in queries |

---

## 6. Indexes that matter

| Index | Definition | Why it exists |
|---|---|---|
| `IX_Devices_Name` | unique | Duplicate device names rejected |
| `IX_DeviceTags_DeviceId_Name` | unique | Duplicate tag names per device rejected |
| `IX_TagReadings_DeviceId_IsTransferred_Id` | `(DeviceId, IsTransferred, Id)` | Per-device pending scans |
| `IX_TagReadings_IsTransferred_TimestampUtc` | `(IsTransferred, TimestampUtc)` | Transfer queue + retention purge |
| **`IX_TagReadings_HistorianTrend`** | `(DeviceId, TagName, TimestampUtc) INCLUDE (Value, Quality)` | **Covering** — trend queries touch only these columns, so the plan never reads the row |
| `IX_Users_NormalizedUsername` | unique | Case-insensitive login lookup |
| `IX_TransferBatches_StartedAtUtc` | nonclustered | Recent-batches page |
| `IX_AuditLogs_TimestampUtc` | nonclustered | Audit paging |
| `IX_DeviceQueries_DeviceId_Name` | unique | |
| `IX_DeviceTriggerConditions_DeviceId_Order` | nonclustered | Ordered expression evaluation |
| `IX_AlarmEvents_ActiveQuery` | `(State ASC, Priority DESC, TriggeredAtUtc DESC)`, **filtered `[State] IN (1, 2)`** | Active + Acknowledged alarm list |
| `IX_AlarmEvents_RetentionCleanup` | `(State, TriggeredAtUtc)` | Retention purge |
| `IX_AlarmEvents_SyncQuery` | `(TriggeredAtUtc, ClearedAtUtc)` | Cross-process alarm sync |
| `IX_DeviceRuntimeStates_InstanceId` | nonclustered | Instance fencing |

> ⚠️ **`IX_AlarmEvents_ActiveQuery`'s filter must be `(1, 2)`.** The comment in `AlarmEventConfiguration.cs` warns that `(0, 1)` would *silently exclude Acknowledged* alarms — a correctness bug that looks like missing data rather than a broken index. If you ever rebuild this index, preserve the filter exactly.

### 6.1 Verifying index usage

```sql
-- Missing-index suggestions for the historian
SELECT OBJECT_NAME(mid.object_id) AS TableName, mid.statement, migs.avg_user_impact
FROM sys.dm_db_missing_index_details mid
JOIN sys.dm_db_missing_index_groups mig ON mid.index_handle = mig.index_handle
JOIN sys.dm_db_missing_index_group_stats migs ON mig.group_handle = migs.group_handle
WHERE mid.database_id = DB_ID('WTT')
ORDER BY migs.avg_user_impact DESC;
```

---

## 7. Permissions and roles

### 7.1 Least privilege

| Identity | Roles | Why |
|---|---|---|
| `NT SERVICE\WTTData` | `wtt_runtime` | The capture/transfer service needs DML only |
| `<MACHINE>\WTTUsers` | `wtt_runtime` | Local group for interactive access |
| `NT AUTHORITY\SYSTEM` | `wtt_runtime`, `db_backupoperator` | Backup task runs as SYSTEM |
| Deployment administrator | `db_owner` (or equivalent) | Runs migrations — **separate from runtime** |

`wtt_runtime` is `SELECT, INSERT, UPDATE, DELETE` on `dbo` **only**. It must never gain DDL rights: if the runtime identity could alter schema, a compromised service account could restructure the database.

### 7.2 Verifying grants

```sql
USE WTT;
SELECT dp.name AS principal, r.name AS role_name
FROM sys.database_role_members rm
JOIN sys.database_principals r ON rm.role_principal_id = r.principal_id
JOIN sys.database_principals dp ON rm.member_principal_id = dp.principal_id
ORDER BY r.name, dp.name;
```

Expect `wtt_runtime` membership for the three identities above.

### 7.3 Windows authentication only

There is no SQL login for WTT itself. `Integrated Security=True` means the connecting identity is the Windows token of whichever process is running:

- the **service** → `NT SERVICE\WTTData`
- the **UI** → the signed-in user (a member of `WTTUsers`)
- **backup task** → `SYSTEM`

This is why the installer creates the `WTTUsers` group and why you must **sign out and back in** after installation: Windows only refreshes group membership in your token at logon.

---

## 8. Migrations

Six EF Core migrations, all generated, **no raw SQL** in any of them:

| Migration | Date | Purpose |
|---|---|---|
| `20260911192001_InitialSqlServer` | 2026-09-11 | 12 tables + 16 indexes |
| `20260911201222_ProductionSecurityHardening` | 2026-09-11 | `IsWriteEnabled` (default `false`), `MinimumWriteIntervalMs` (1000), `WriteMaximum`, `WriteMinimum` |
| `20260913105214_AddRuntimeStatus` | 2026-09-13 | `ServiceRuntimeState`, `DeviceRuntimeStates` |
| `20260922183000_AddAlarmThresholdSystem` | 2026-09-22 | 8 alarm columns on `DeviceTags`, `AlarmEvents` + 3 indexes |
| `20260924092300_OptimizeAlarmIndexes` | 2026-09-24 | Drop `IX_AlarmEvents_Priority_State`, add the three named alarm indexes |
| `20260927090000_AddHistorianTrendIndex` | 2026-09-27 | `IX_TagReadings_HistorianTrend` |

### 8.1 Who migrates

| Process | Behaviour |
|---|---|
| **UI** (`DatabaseInitializer.InitializeAsync`) | Runs `Database.MigrateAsync()` — applies pending migrations |
| **Service** (`SchemaVerificationHostedService`) | Calls `VerifySchemaCurrentAsync()` — **verifies only**, throws `Database upgrade required: …` if pending |
| **`Update-WTT.ps1`** | Runs the self-contained migration bundle `Database\WTT.Database.exe` |
| **CI** | `dotnet ef database update` with `WTT_DESIGN_CONNECTION` before integration tests |

An unattended service never silently alters schema. After a schema-changing release, run the migration bundle (or start the UI) **before** expecting `WTTData` to run.

### 8.2 Checking migration state

```sql
USE WTT;
SELECT MigrationId, ProductVersion FROM dbo.__EFMigrationsHistory ORDER BY MigrationId;
```

Six rows expected. Fewer rows with the service refusing to start means migrations are pending.

### 8.3 Creating a new migration

```powershell
dotnet tool restore
dotnet ef migrations add MyChange `
    --project "src\WTT.Infrastructure\WTT.Infrastructure.csproj" `
    --startup-project "src\WTT.Infrastructure\WTT.Infrastructure.csproj"
```

Review the generated `.Designer.cs` before committing — especially any index filter (see §6) and any `DeleteBehavior` (SQL Server rejects some cascade paths, which is why `DeviceAddress → DeviceTags` is `NoAction`).

---

## 9. Sizing and the 10 GB ceiling

### 9.1 Provisioned limits

| Setting | Value |
|---|---|
| Data file growth | `128MB` fixed (predictable, avoids auto-growth storms) |
| Max size | `9500MB` (9.28 GB) — headroom below the 10 GB Express limit |
| Auto-shrink | OFF |
| Recovery model | SIMPLE (no log growth) |

### 9.2 Operating thresholds

| Database size | Action |
|---|---|
| **< 8 GB** | Normal operation |
| **≥ 8 GB** | Investigate: is transfer keeping up? Is retention running? |
| **≥ 9 GB** | **Stop new capture** if transfer/retention cannot drain data |
| **≥ 9.5 GB** | Provisioned ceiling — writes will fail |

```sql
SELECT
    DB_NAME()                                    AS DatabaseName,
    SUM(size) * 8 / 1024                          AS SizeMB,
    CAST(SUM(size) * 8.0 / 1024 / 1024 AS decimal(10,2)) AS SizeGB
FROM sys.database_files;
```

### 9.3 What grows

| Table | Growth driver | Control |
|---|---|---|
| `TagReadings` | Every captured scan | `capture.transferredRetentionDays` (default 7), hourly purge |
| `AlarmEvents` | Alarms firing | `Alarm.RetentionDays` (default 90), daily purge |
| `TransferBatches` | One row per batch | hard-coded **30 days** |
| `AuditLogs` | Every mutation/login | hard-coded **365 days** |

### 9.4 Retention rules you must not weaken

1. **Never silently delete untransferred readings.** `capture.untransferredMaxAgeDays` defaults to **0 = disabled**. Setting it above zero purges never-transferred rows **and logs a warning every time**.
2. The retention job runs at most **once per 60 minutes**.
3. If space is the problem, **fix transfer first** — do not delete the buffer.

---

## 10. Central destination database

The *central* database is a separate SQL Server (any edition) that receives transferred readings. Its schema is created and upgraded **at runtime** by `EnsureDestinationSchemaAsync` (at most once per hour per server/table), default table name `TagDetail`:

```sql
Pk_TagDetail      BIGINT IDENTITY PRIMARY KEY
TagName           NVARCHAR(200)
PLCName           NVARCHAR(100)
TagValue          NVARCHAR(500)
TimeStamp         DATETIME
TimeStampUTC      DATETIME
Quality           NVARCHAR(20)
ScanId            UNIQUEIDENTIFIER
SourceId          UNIQUEIDENTIFIER      -- installation identity
SourceNode        NVARCHAR(128)         -- machine name
SourceReadingId   BIGINT                -- TagReading.Id
```

Indexes created on the destination:

| Index | Definition | Purpose |
|---|---|---|
| `UX_<table>_SourceId` | filtered unique on `(SourceId, SourceReadingId)` | **Makes retries idempotent** |
| `IX_<table>_PLC_Time` | `(PLCName, TimeStamp) INCLUDE (TagName, TagValue)` | Reporting by machine and time |

Column set is a **superset** of the legacy `TagDetail` layout, so pre-existing central consumers keep working.

**Batch insert requirements** — the connection issues these first:

```sql
SET QUOTED_IDENTIFIER ON;
SET ARITHABORT ON;
```

They are required by the filtered unique index. Do not remove them.

**Duplicate check** (expected: zero rows):

```sql
SELECT SourceId, SourceReadingId, COUNT(*) AS Copies
FROM dbo.TagDetail
WHERE SourceId IS NOT NULL
GROUP BY SourceId, SourceReadingId
HAVING COUNT(*) > 1;
```

---

## 11. Backup

SQL Server Express has **no SQL Server Agent**, so there is no native maintenance plan. `Install-WTT.ps1` registers a Windows Scheduled Task instead.

| Property | Value |
|---|---|
| Task name | `WTT Database Backup` |
| Schedule | Daily 02:00 |
| Runs as | `SYSTEM` |
| Command | `C:\Program Files\WTT\Tools\Backup-Wtt.ps1` |

### 11.1 `Backup-Wtt.ps1` behaviour

1. `sqlcmd -E -C -b` runs `BACKUP DATABASE [WTT] ... WITH COPY_ONLY, CHECKSUM, INIT`
2. Then `RESTORE VERIFYONLY ... WITH CHECKSUM` against the produced file
3. File name: `WTT-yyyyMMdd-HHmmss-<8hex>.bak`
4. Deletes `.bak` files older than the retention window (default 30 days)
5. Prints `Verified backup created: ...`

| Parameter | Default | Notes |
|---|---|---|
| `-Server` | `.` | SQL instance |
| `-Database` | `WTT` | Must match `^[A-Za-z0-9_]+$` |
| `-BackupDirectory` | `C:\ProgramData\WTT\Backups` | |
| `-RetentionDays` | `30` | range 1–3650 |

### 11.2 Before the first backup — the ACL

Create the folder and grant write access **only** to the SQL instance service SID:

```powershell
New-Item -ItemType Directory -Force "C:\ProgramData\WTT\Backups"
icacls "C:\ProgramData\WTT" /grant "NT SERVICE\MSSQLSERVER:(OI)(CI)M"
```

For a named Express instance use its own SID, e.g. `NT SERVICE\MSSQL$SQLEXPRESS`.

Without this grant the scheduled task fails with access denied — the most common backup problem right after a manual install.

### 11.3 Backup policy

- Daily full backup with checksum.
- 30-day local retention **plus an off-machine encrypted copy**.
- `RESTORE VERIFYONLY` proves the checksums; it is **not** a disaster-recovery test — perform a real restore drill (§12) and record the result.

---

## 12. Restore and restore drills

### 12.1 Restore drill (safe, does not touch production)

```sql
USE master;
RESTORE DATABASE [WTT_DRILL]
FROM DISK = N'C:\ProgramData\WTT\Backups\<file>.bak'
WITH MOVE N'WTT'     TO N'C:\ProgramData\WTT\WTT_DRILL.mdf',
     MOVE N'WTT_log' TO N'C:\ProgramData\WTT\WTT_DRILL_log.ldf',
     RECOVERY;

-- verify
SELECT COUNT(*) FROM WTT_DRILL.dbo.TagReadings;
SELECT COUNT(*) FROM WTT_DRILL.dbo.Users;
GO
DROP DATABASE [WTT_DRILL];
```

Record date, file used, duration and row counts.

### 12.2 Emergency restore over `WTT`

> ⚠️ **Rolling the database backwards loses every reading captured after that backup.** Take a fresh backup first, and record what will be lost.

```sql
USE master;
ALTER DATABASE [WTT] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
RESTORE DATABASE [WTT]
FROM DISK = N'C:\ProgramData\WTT\Backups\<file>.bak'
WITH REPLACE, RECOVERY;
ALTER DATABASE [WTT] SET MULTI_USER;
```

Then start `WTTData` and confirm the heartbeat.

### 12.3 Automatic backup during updates

`Update-WTT.ps1` takes its own backup via `Backup-Wtt.ps1` and **verifies a new `.bak` exists** before running the migration bundle. On any failure it restores both files and database together (see `docs/INSTALL_AND_UPDATE.md` §8).

---

## 13. Completed data cutover

The retired local file database was migrated into SQL Server in **one transaction** with identity and row-count verification. After a checksum backup and a successful full restore drill, the importer, the provider package and the old database files were removed.

`WTT` is the only application database. There is no compatibility mode, no legacy provider and no fallback path.

---

## 14. Verification queries

Run in SSMS or `sqlcmd -S "." -E -C -b -Q "..."`.

```sql
USE WTT;

-- 1. Schema and migrations
SELECT MigrationId FROM dbo.__EFMigrationsHistory ORDER BY MigrationId;   -- 6 rows

-- 2. Heartbeat
SELECT TOP (5) Id, InstanceId, StartedAtUtc, HeartbeatAtUtc, LastError
FROM dbo.ServiceRuntimeState ORDER BY Id;

-- 3. Per-device health
SELECT DeviceId, WorkerRunning, IsConnected, IsCapturing, ScanCount,
       ReadingsWritten, ErrorCount, ReconnectAttempts, LastError, LastScanUtc
FROM dbo.DeviceRuntimeStates;

-- 4. Pending vs transferred
SELECT COUNT(*) AS Pending FROM dbo.TagReadings WHERE IsTransferred = 0;

SELECT COUNT(*) AS ReadingsToday FROM dbo.TagReadings
WHERE TimestampUtc >= CAST(GETDATE() AS DATE);

-- 5. Buffer age (how stale is the oldest untransferred row?)
SELECT MIN(TimestampUtc) AS OldestPending
FROM dbo.TagReadings WHERE IsTransferred = 0;

-- 6. Recent transfer results
SELECT TOP (20) DeviceName, TargetServer, RecordCount, Status, ErrorMessage, StartedAtUtc
FROM dbo.TransferBatches ORDER BY Id DESC;

-- 7. Destination health
SELECT ServerName, DatabaseName, TableName, IsEnabled, LastSuccessAtUtc, LastError
FROM dbo.DeviceServers;

-- 8. Active alarms by priority
SELECT Priority, COUNT(*) AS Count FROM dbo.AlarmEvents
WHERE State IN (1, 2) GROUP BY Priority;

-- 9. Database size
SELECT SUM(size) * 8 / 1024 AS SizeMB FROM sys.database_files;

-- 10. Table row counts
SELECT 'TagReadings' AS T, COUNT(*) AS Rows FROM dbo.TagReadings
UNION ALL SELECT 'AlarmEvents', COUNT(*) FROM dbo.AlarmEvents
UNION ALL SELECT 'TransferBatches', COUNT(*) FROM dbo.TransferBatches
UNION ALL SELECT 'AuditLogs', COUNT(*) FROM dbo.AuditLogs;

-- 11. Index fragmentation (rebuild above ~30%)
SELECT OBJECT_NAME(ips.object_id) AS TableName, i.name AS IndexName,
       ips.avg_fragmentation_in_percent
FROM sys.dm_db_index_physical_stats(DB_ID(), NULL, NULL, NULL, 'LIMITED') ips
JOIN sys.indexes i ON ips.object_id = i.object_id AND ips.index_id = i.index_id
WHERE ips.avg_fragmentation_in_percent > 30
ORDER BY ips.avg_fragmentation_in_percent DESC;
```

---

## 15. Troubleshooting

| Symptom | Likely cause | Action |
|---|---|---|
| `Login failed for user` / access denied | `WTTUsers` membership not applied | **Sign out and sign back in** |
| Same, for the service | Wrong `-SqlServiceName` dependency, or SQL not running | Check `services.msc`; confirm `sc qc WTTData` shows the right dependency |
| `Database upgrade required: ...` | Migrations pending; service refuses to start | Run the migration bundle or `dotnet ef database update` |
| Cannot connect after install | SQL service stopped, wrong instance | `sqlcmd -S "." -E -C -b -Q "SELECT 1"` (adjust `-S` for named instances) |
| Backup task fails, access denied | ACL not granted to SQL service SID | Re-run the `icacls` grant in §11.2 |
| Backup task fails, cannot open `Backup-Wtt.ps1` | Task points at a removed path | Re-register the task, or run `Install-WTT.ps1` again |
| Service starts then stops | Check `C:\ProgramData\WTT\Logs` for the real error | Look for `Database upgrade required` or SQL connectivity errors |
| Pending count grows forever | Destination unreachable or circuit breaker open | Check `DeviceServers.LastError` and `TransferBatches.ErrorMessage` |
| Database near 10 GB | Transfer not draining, or retention too long | Fix transfer; lower `capture.transferredRetentionDays`; see §9 |
| Slow trend queries | Index missing/dropped | Verify `IX_TagReadings_HistorianTrend` exists (§6) |
| Alarms missing from Active list | Wrong index filter | Verify `IX_AlarmEvents_ActiveQuery` filter is `State IN (1, 2)` (§6) |
| `A stored password could not be decrypted` | DPAPI secret moved between machines | Re-enter the transfer server password |
| Duplicate central rows | Manual inserts outside the pipeline | Run the duplicate check in §10 — the pipeline cannot produce duplicates |
| `Another WTT pipeline service instance is already running` | Duplicate service / stale process | `Get-Process WTT*`; stop the stale instance |

**Log locations:**

- Install: `C:\ProgramData\WTT\install.log`
- Service: `C:\ProgramData\WTT\Logs\`
- UI: `%LocalAppData%\WTT\Logs\`

---

## 16. Document history

| Version | Date | Change |
|---|---|---|
| 1.2.0 | 2026-09-16 | Initial SQL Server Express guide (configuration, provisioning, cutover, backup). |
| 1.2.0 | 2026-10-09 | Deep expansion: instance matrix, enforced connection rules, full schema and index reference, permissions/roles, migration ownership, sizing thresholds, central destination table, backup ACL, restore drills, verification queries and a troubleshooting matrix. |
