# WTT Install, Update and Uninstall Guide

Version: 1.2.0 (artifacts shipped as 1.2.1)
Audience: Deployment engineers, system administrators, support
Related: `docs/SQL_SERVER_EXPRESS.md` (database), `docs/USER_GUIDE.md` (day-to-day use), `docs/ADMIN_GUIDE.md` (security rules)

---

## Contents

1. [What the installer actually does](#1-what-the-installer-actually-does)
2. [Prerequisites](#2-prerequisites)
3. [Directory and service layout](#3-directory-and-service-layout)
4. [Building a release package](#4-building-a-release-package)
5. [Fresh installation](#5-fresh-installation)
6. [Post-installation checklist](#6-post-installation-checklist)
7. [Updating an existing installation](#7-updating-an-existing-installation)
8. [Rollback](#8-rollback)
9. [Uninstalling](#9-uninstalling)
10. [Backup task](#10-backup-task)
11. [Script reference](#11-script-reference)
12. [Named SQL instances](#12-named-sql-instances)
13. [Code signing](#13-code-signing)
14. [Troubleshooting installation](#14-troubleshooting-installation)
15. [Document history](#15-document-history)

---

## 1. What the installer actually does

`deploy\Install-WTT.ps1` is the single entry point for a fresh installation. It is
idempotent in the sense that it removes any previous `WTTData` service before
recreating it, but it is **not** an upgrade path — use `Update-WTT.ps1` for that
(§7).

In order, `Install-WTT.ps1`:

1. Requires an elevated (Administrator) terminal; refuses otherwise.
2. Starts a transcript log at `C:\ProgramData\WTT\install.log`.
3. Validates the package layout — fails fast if any of these are missing:
   - `Service\WTT.Service.exe`
   - `UI\WTT.exe`
   - `Database\WTT.Database.exe`
   - `deploy\Provision-Database.sql`
4. Stops and deletes any existing `WTTData` service.
5. Creates `C:\ProgramData\WTT\Logs` and `C:\ProgramData\WTT\Backups` with ACLs.
6. Runs `Provision-Database.sql` through `sqlcmd -E -C -b`.
7. Runs `Backup-Wtt.ps1` with 30-day retention (so a known-good baseline exists
   before any schema change).
8. Runs the EF migration bundle `Database\WTT.Database.exe`.
9. Creates the local group **`WTTUsers`** and adds the installing user to it.
10. Copies binaries to `C:\Program Files\WTT\{Service,UI,Tools}`.
11. Writes `C:\ProgramData\WTT\installation-id` (a GUID — this is the stable
    installation identity used by `InstallationIdentityTests`).
12. Registers the Windows service `WTTData`:
    - `binPath` → `Service\WTT.Service.exe`
    - `start= delayed-auto` (delayed start so it does not race SQL Server)
    - `obj= NT SERVICE\WTTData` (a virtual service account, not LocalSystem)
    - dependency on the configured SQL service name
    - failure recovery: `restart/5000 restart/15000 restart/60000`
13. Provisions SQL logins and roles for `NT SERVICE\WTTData`,
    `<MACHINE>\WTTUsers` and `NT AUTHORITY\SYSTEM`, adding them to the
    `wtt_runtime` and `db_backupoperator` database roles.
14. Locks down `C:\ProgramData\WTT` (root, Logs, Backups) with `icacls`.
15. Starts the service and waits up to 60 seconds for `Running`.
16. Registers the scheduled task **"WTT Database Backup"** — daily at 02:00, runs
    as `SYSTEM`, executing `Tools\Backup-Wtt.ps1`.
17. Creates a public desktop shortcut `WTT.lnk` → `UI\WTT.exe`.

### Least-privilege notes

- The service runs as a **virtual service account**, never LocalSystem.
- Database access is granted through the `wtt_runtime` role (SELECT/INSERT/UPDATE/DELETE
  on `dbo` only), not `db_owner`.
- Backups need `db_backupoperator`, which is why that role is granted separately.
- The UI is never required to be elevated: `app.manifest` is `asInvoker`.

---

## 2. Prerequisites

| Requirement | Detail |
|---|---|
| OS | Windows 10/11 x64 or a supported Windows Server x64 |
| Runtime | Self-contained publish — no separate .NET install needed on the target |
| Database | SQL Server 2022 Express (or newer Express/Standard) running locally |
| SQL service | Must be `Running` in Windows Services **before** install |
| Access | Administrator terminal for install/update/uninstall |
| SQL access | Either Windows integrated admin access to the instance, or the appropriate flags |
| Disk | Room for the package, the install root and ~9.5 GB database ceiling |
| Sign-out | One sign-out/sign-in after first install so the `WTTUsers` group membership applies to your token |

Default assumptions are the machine's **default instance**:
`ServerInstance=.` and `SqlServiceName=MSSQLSERVER`. For a named Express instance
see §12.

---

## 3. Directory and service layout

| Path | Contents |
|---|---|
| `C:\Program Files\WTT\Service\` | `WTT.Service.exe` and dependencies |
| `C:\Program Files\WTT\UI\` | `WTT.exe` and dependencies |
| `C:\Program Files\WTT\Tools\` | `Backup-Wtt.ps1` and helper scripts |
| `C:\ProgramData\WTT\Logs\` | Service logs (ACL-restricted) |
| `C:\ProgramData\WTT\Backups\` | `.bak` files (ACL-restricted) |
| `C:\ProgramData\WTT\Rollback\` | Update snapshots (created by `Update-WTT.ps1`) |
| `C:\ProgramData\WTT\install.log` | Install transcript |
| `C:\ProgramData\WTT\installation-id` | Installation GUID |
| `%LocalAppData%\WTT\Logs\` | **UI** logs (per user, date-foldered) |

| Object | Name |
|---|---|
| Windows service | `WTTData` (display: "WTT Industrial Data Service") |
| Local group | `WTTUsers` |
| Scheduled task | `WTT Database Backup` (daily 02:00) |
| Database | `WTT` |
| Desktop shortcut | `WTT.lnk` |
| UI executable | `WTT.exe` |

---

## 4. Building a release package

```powershell
cd "D:\Spring Boot\PLC Project\WTT_PLC\New PLc"
.\deploy\Publish-Release.ps1 -Version 1.2.2
```

### Parameters

| Parameter | Required | Default | Meaning |
|---|---|---|---|
| `-Version` | Yes | — | Semantic version stamped into the assemblies and the output folder name |
| `-Output` | No | `..\artifacts\WTT-$Version` | Output directory |
| `-SkipTests` | No | off | Skip the Release test run (use only when you have run tests separately) |

### What it does

1. Deletes and recreates the output directory.
2. Unless `-SkipTests`, runs `dotnet test WTT.slnx -c Release`.
3. Publishes **both** `WTT.UI` and `WTT.Service` self-contained for `win-x64`
   with `-p:Version=$Version`. Deliberately **no** single-file and **no** trimming
   (trimming breaks WPF XAML and EF Core).
4. `dotnet tool restore`, then builds the EF migration bundle into
   `Database\WTT.Database.exe` (self-contained, `win-x64`).
5. Copies the entire `deploy\` and `docs\` folders into the package.
6. Writes `release.json`:
   ```json
   { "version": "1.2.1", "createdUtc": "2026-09-16T13:36:02Z", "runtime": "win-x64" }
   ```
7. Zips the package with `tar.exe -a -c -f <output>.zip`.

### Package layout

```
artifacts/WTT-1.2.2/
├── UI/            WTT.exe + deps
├── Service/       WTT.Service.exe + deps
├── Database/      WTT.Database.exe (EF migration bundle)
├── deploy/        all install/update/backup scripts
├── docs/          user guide, admin guide, install guide, test plan
└── release.json
artifacts/WTT-1.2.2.zip
```

> Note: `artifacts/` is in `.gitignore`. Packages are build outputs and are not
> committed.

### Version source of truth

`Directory.Build.props` carries `<Version>1.2.0</Version>` and applies it to every
project. `Publish-Release.ps1 -Version` overrides it at publish time. Currently the
shipped artifact (`artifacts/WTT-1.2.1/release.json`) is **ahead** of the
`Directory.Build.props` value — bump `Directory.Build.props` when you cut a release
so the two agree.

---

## 5. Fresh installation

From an **elevated** terminal:

```powershell
cd "D:\Spring Boot\PLC Project\WTT_PLC\New PLc"
.\deploy\Install-WTT.ps1 -PackageRoot .\artifacts\WTT-1.2.2
```

| Parameter | Required | Default | Meaning |
|---|---|---|---|
| `-PackageRoot` | Yes | — | Package folder produced by `Publish-Release.ps1` |
| `-ServerInstance` | No | `.` | SQL instance to provision against |
| `-SqlServiceName` | No | `MSSQLSERVER` | Windows service name of SQL, used for the service dependency |

For a named Express instance:

```powershell
.\deploy\Install-WTT.ps1 -PackageRoot .\artifacts\WTT-1.2.2 `
    -ServerInstance '.\SQLEXPRESS' -SqlServiceName 'MSSQL$SQLEXPRESS'
```

### First run after installation

1. **Sign out and sign back in.** Windows applies new `WTTUsers` group membership
   to your token only at logon. Skipping this is the most common cause of
   "access denied" immediately after install.
2. Launch `WTT` from the desktop shortcut.
3. The startup window appears, the schema check runs, then — because there are no
   users yet — **`Create the first administrator`** appears.
4. Create the first administrator (no shared default password exists anywhere in
   the product).
5. Sign in and confirm the service heartbeat shows `running`.

---

## 6. Post-installation checklist

- [ ] `WTTData` service status is `Running` (`services.msc`).
- [ ] Service recovery is configured (`restart/5000 restart/15000 restart/60000`).
- [ ] Service start type is **Delayed Automatic**.
- [ ] SQL dependency is present on the service.
- [ ] `C:\ProgramData\WTT\Backups` contains at least one `.bak`.
- [ ] Scheduled task **"WTT Database Backup"** exists and last result is `0`.
- [ ] `C:\ProgramData\WTT\installation-id` exists.
- [ ] You signed out and back in, so `WTTUsers` applies.
- [ ] First administrator enrolled; login succeeds.
- [ ] Dashboard heartbeat is fresh (`running` with a recent timestamp).
- [ ] Desktop shortcut `WTT.lnk` opens the UI.

---

## 7. Updating an existing installation

```powershell
.\deploy\Update-WTT.ps1 -PackageRoot .\artifacts\WTT-1.2.2
```

Same parameters as install (`-ServerInstance`, `-SqlServiceName`).

### What it does, in order

1. Requires Administrator.
2. **Refuses to run if a `WTT` UI process is open** — close the client first.
3. Validates the package layout.
4. Takes a **file snapshot** to
   `%ProgramData%\WTT\Rollback\yyyyMMdd-HHmmss`.
5. Stops `WTTData`.
6. Pre-flight `sqlcmd SELECT 1` to prove SQL is reachable.
7. Takes a **database backup** via `Backup-Wtt.ps1` (uses the already-installed
   copy if present) and **verifies a new `.bak` file actually exists**.
8. Runs the migration bundle `Database\WTT.Database.exe`.
9. Swaps the `Service\` and `UI\` folders.
10. Restarts the service and waits 20 seconds.

### Automatic rollback on failure

If **any** step from 6 onward fails, `Update-WTT.ps1`:

1. stops the service,
2. restores the file snapshot from `Rollback\`,
3. runs `RESTORE DATABASE [WTT] ... WITH REPLACE` from the pre-update backup
   (single-user rollback),
4. restarts the service,
5. rethrows the original error.

The update is therefore all-or-nothing: files and schema move forward together.

### Before a schema-changing release

1. Take a manual backup: `.\deploy\Backup-Wtt.ps1`.
2. Review the EF migrations being applied.
3. Keep the previous package available so you know which folder snapshot to expect.
4. Schedule the update outside capture-critical hours.

---

## 8. Rollback

**Automatic** — handled by `Update-WTT.ps1` on failure (§7).

**Manual** — if the update reported success but the result is wrong:

1. Stop `WTTData`.
2. Identify the snapshot under `C:\ProgramData\WTT\Rollback\`.
3. Copy `Service\` and `UI\` from that snapshot back into
   `C:\Program Files\WTT\`.
4. Restore the database from the `.bak` produced during that update:
   ```sql
   USE master;
   ALTER DATABASE [WTT] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
   RESTORE DATABASE [WTT] FROM DISK = N'C:\ProgramData\WTT\Backups\<file>.bak'
     WITH REPLACE, RECOVERY;
   ALTER DATABASE [WTT] SET MULTI_USER;
   ```
5. Start `WTTData` and confirm the heartbeat.

> Restoring a database backwards means **losing readings captured after the
> backup**. Take a fresh backup before you start, and record what was lost.

---

## 9. Uninstalling

```powershell
.\deploy\Uninstall-WTT.ps1
```

The script takes no parameters. It:

1. Unregisters the scheduled task **"WTT Database Backup"**.
2. Stops and `sc delete`s the `WTTData` service.
3. Removes the `WTT.lnk` desktop shortcut.

### What it deliberately preserves

| Preserved | Why |
|---|---|
| `WTT` database | Captured data is never destroyed by an uninstall |
| `C:\ProgramData\WTT\Backups` | Disaster recovery |
| `C:\ProgramData\WTT\Logs` | Audit and diagnosis |
| `C:\Program Files\WTT` binaries | Removed only if you delete them manually |
| `WTTUsers` group | May be shared; remove manually if truly unused |

To fully remove the product you must additionally delete `C:\Program Files\WTT`,
`C:\ProgramData\WTT` and (only if you are certain) the `WTT` database and the
`WTTUsers` group. **Never drop a database that still holds untransferred readings.**

---

## 10. Backup task

SQL Server Express has **no SQL Server Agent**, so there is no native maintenance
plan. `Install-WTT.ps1` registers a Windows Scheduled Task instead.

| Property | Value |
|---|---|
| Name | `WTT Database Backup` |
| Schedule | Daily 02:00 |
| Runs as | `SYSTEM` |
| Command | `Tools\Backup-Wtt.ps1` |

`Backup-Wtt.ps1` behaviour:

- `sqlcmd -E -C -b` runs `BACKUP DATABASE ... WITH COPY_ONLY, CHECKSUM, INIT`.
- Then `RESTORE VERIFYONLY ... WITH CHECKSUM` against the produced file.
- File name pattern: `WTT-yyyyMMdd-HHmmss-<8 hex>.bak`.
- Deletes `.bak` files older than the retention window (default 30 days).

| Parameter | Default | Range / notes |
|---|---|---|
| `-Server` | `.` | SQL instance |
| `-Database` | `WTT` | Must match `^[A-Za-z0-9_]+$` |
| `-BackupDirectory` | `C:\ProgramData\WTT\Backups` | |
| `-RetentionDays` | `30` | 1–3650 |

### Before the first backup

Create the folder and grant write access **only** to the SQL service SID:

```powershell
New-Item -ItemType Directory -Force "C:\ProgramData\WTT\Backups"
icacls "C:\ProgramData\WTT" /grant "NT SERVICE\MSSQLSERVER:(OI)(CI)M"
```

For a named instance use its own service SID, e.g.
`NT SERVICE\MSSQL$WTTEXPRESS`.

### Backup policy

- Daily full backup with checksum.
- 30-day local retention **plus** an off-machine encrypted copy.
- `RESTORE VERIFYONLY` alone is **not** a disaster-recovery test — perform a real
  restore drill regularly.
- Alert at 8 GB database size; stop new capture before 9 GB if transfer cannot
  drain data. Never silently delete untransferred readings.

---

## 11. Script reference

| Script | Purpose |
|---|---|
| `Publish-Release.ps1` | Build, test, publish, bundle and zip a release package |
| `Install-WTT.ps1` | Fresh install: provision DB, migrate, create service/group/task/shortcut |
| `Update-WTT.ps1` | Atomic update with file snapshot + DB backup + automatic rollback |
| `Uninstall-WTT.ps1` | Remove service, task and shortcut; preserve all data |
| `Backup-Wtt.ps1` | Verified backup with retention pruning |
| `Provision-Database.sql` | Create/configure the `WTT` database and `wtt_runtime` role |
| `Create-Manual-Test-Databases.sql` | Create `WTT_Central_Test` + seeded `dbo.ProductionContext` for manual tests |
| `Run-Manual-Test.ps1` | Local harness: start service + UI, run, check exit code |
| `Run-Manual-Test.cmd` / `.vbs` | Window-less wrappers around `Run-Manual-Test.ps1` |

### `Provision-Database.sql` specifics

- Creates database `WTT` if missing, collation `Latin1_General_100_CI_AS_SC`
  (explicit Unicode, case-insensitive).
- `RECOVERY SIMPLE`, `PAGE_VERIFY CHECKSUM`, `AUTO_CLOSE OFF`, `AUTO_SHRINK OFF`.
- Data file `FILEGROWTH=128MB`, `MAXSIZE=9500MB` — deliberately below the SQL
  Express 10 GB ceiling.
- Creates role `wtt_runtime` with `SELECT/INSERT/UPDATE/DELETE` on `dbo`.

Runtime identities get `wtt_runtime` only. Schema migrations must run under a
separate deployment administrator — never grant `wtt_runtime` DDL rights.

---

## 12. Named SQL instances

Everything defaults to the default instance. For SQL Server **Express** installed
as a named instance (the common case, `.\SQLEXPRESS`):

```powershell
.\deploy\Install-WTT.ps1 -PackageRoot .\artifacts\WTT-1.2.2 `
    -ServerInstance '.\SQLEXPRESS' `
    -SqlServiceName 'MSSQL$SQLEXPRESS'
```

Also update:

- The application connection string override
  `WTT_Database__ConnectionString`, e.g.
  `Server=.\SQLEXPRESS;Database=WTT;Integrated Security=True;Encrypt=True;TrustServerCertificate=True`
- The backup ACL grant to that instance's service SID
  (`NT SERVICE\MSSQL$SQLEXPRESS`).
- Any manual `sqlcmd -S` invocations in `docs/SQL_SERVER_EXPRESS.md`.

---

## 13. Code signing

Production releases should be **Authenticode-signed**.

A publicly trusted Windows code-signing certificate is normally a paid purchase.
WTT does **not** embed a self-signed substitute — a self-signed certificate does
not establish trust on customer machines and produces SmartScreen warnings, which
is worse than shipping unsigned internal builds clearly labelled as such.

For internal distribution, sign with your organisation's existing certificate if
one is available, or distribute over a trusted internal channel.

---

## 14. Troubleshooting installation

| Symptom | Likely cause | Action |
|---|---|---|
| "Package validation failed" | Incomplete package | Re-run `Publish-Release.ps1`; check `Service\`, `UI\`, `Database\`, `deploy\Provision-Database.sql` all exist |
| Install fails at SQL step | SQL service stopped, or wrong `-ServerInstance` | Start SQL, verify `sqlcmd -S "." -E -C -b -i deploy\Provision-Database.sql` manually |
| Service created but not `Running` | SQL not up yet, or dependency name wrong | Check `-SqlServiceName` matches the actual Windows service name; check `%ProgramData%\WTT\Logs` |
| Access denied right after install | `WTTUsers` membership not applied yet | **Sign out and sign back in** |
| "WTT UI is running" during update | Client left open | Close `WTT.exe` and retry |
| Update failed and rolled back | See the thrown error | Files + DB were restored together; check `install.log` and service logs |
| Desktop shortcut missing | Public desktop not writable | Create the shortcut manually → `C:\Program Files\WTT\UI\WTT.exe` |
| Backup task fails with access denied | ACL not granted to SQL service SID | Re-run the `icacls` grant in §10 |
| Service does not start after OS reboot | SQL slower than delayed start | Delayed start should cover it; if not, check the SQL service is also delayed/automatic |
| Migration bundle errors | SQL permissions or prior failed migration | Restore the pre-update backup (§8), then re-run under a deployment admin |

Log locations when diagnosing:

- Install: `C:\ProgramData\WTT\install.log`
- Service: `C:\ProgramData\WTT\Logs\`
- UI: `%LocalAppData%\WTT\Logs\`

---

## 15. Document history

| Version | Date | Change |
|---|---|---|
| 1.2.0 | 2026-09-16 | Restructured from a short command list into a full install/update/uninstall reference: installer step-by-step, package layout, rollback, backup task, named instances, script reference, troubleshooting matrix. |
