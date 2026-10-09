# WTT Administrator Guide

Version: 1.2.0
Audience: Administrators, commissioning engineers, plant IT
Prerequisite reading: `docs/USER_GUIDE.md` (how operators use the app), `docs/ARCHITECTURE.md` (how it works)

---

## Contents

1. [Administrator responsibilities](#1-administrator-responsibilities)
2. [First-time setup](#2-first-time-setup)
3. [Role and permission administration](#3-role-and-permission-administration)
4. [Password and account policy](#4-password-and-account-policy)
5. [Device and tag configuration rules](#5-device-and-tag-configuration-rules)
6. [PLC write safety rules](#6-plc-write-safety-rules)
7. [External SQL query rules](#7-external-sql-query-rules)
8. [Transfer and database rules](#8-transfer-and-database-rules)
9. [Alarm administration](#9-alarm-administration)
10. [Backup and recovery](#10-backup-and-recovery)
11. [Monitoring and health checks](#11-monitoring-and-health-checks)
12. [Security checklist](#12-security-checklist)
13. [Change management](#13-change-management)
14. [Escalation and support package](#14-escalation-and-support-package)
15. [Document history](#15-document-history)

---

## 1. Administrator responsibilities

As an administrator you own:

- **Accounts** — creating users, assigning roles, resetting passwords, unlocking
  accounts, never leaving zero active administrators.
- **Configuration** — devices, address blocks, tags, capture triggers, external
  queries, transfer destinations.
- **Safety** — deciding which tags may be written to a controller, and enforcing
  range, cooldown and read-back rules.
- **Data retention** — retention windows, database size, backup schedule,
  restore drills.
- **Change control** — installing updates, reviewing migrations, rolling back.

Three rules that are never negotiable:

> 1. **Never leave zero active administrators.** The product refuses to let you.
> 2. **Never delete untransferred readings.** Data in the local buffer has not
>    reached its destination yet.
> 3. **Never point a test system at production PLCs, MES or ERP.**

---

## 2. First-time setup

### 2.1 Prerequisites

- Windows 10/11 or supported Windows Server x64
- SQL Server Express running locally
- Administrator terminal for installation
- Follow `docs/INSTALL_AND_UPDATE.md` §5

### 2.2 First administrator enrollment

Fresh installations contain **no users and no shared default password**. WTT
detects this at startup and opens `Create the first administrator`.

1. Start WTT. The splash appears, then the enrollment window.
2. **Username** — e.g. `admin`.
3. **Full name** — e.g. `Plant Administrator`.
4. **Password** — minimum 8 characters with uppercase, lowercase and a digit,
   e.g. a pattern like `Word#2026` but with your own unique value.
5. Confirm the password and select **Create administrator**.
6. The login window appears. The `Users` table now contains exactly one active
   Admin row with a BCrypt hash.

If you close the enrollment window without enrolling, the application exits.
There is no way to skip it, and there is no backdoor.

**Never record passwords in screenshots, tickets or shared messages.**

### 2.3 Post-install verification

```powershell
# Service running?
Get-Service WTTData

# Database reachable?
sqlcmd -S "." -E -C -b -Q "SELECT COUNT(*) FROM WTT.dbo.Users;"

# Backup exists?
Get-ChildItem C:\ProgramData\WTT\Backups
```

Then in the application:

- [ ] Dashboard heartbeat shows `running` with a fresh timestamp
- [ ] Sidebar shows all items for your role
- [ ] Logs page opens and shows entries

**Sign out and sign back in** after first installation so Windows applies the
new `WTTUsers` group membership to your token. Skipping this is the most common
cause of "access denied" right after install.

---

## 3. Role and permission administration

### 3.1 The three roles

| Role | Intended for | Can |
|---|---|---|
| **Viewer** | Supervisors, auditors | View dashboard, live data, devices, transfer status |
| **Operator** | Shift engineers | Viewer ∪ start/stop capture, start/stop transfer, view logs, acknowledge alarms |
| **Admin** | You | Everything: configuration, users, settings, PLC writes |

### 3.2 Full permission matrix

| Permission | Admin | Operator | Viewer |
|---|:---:|:---:|:---:|
| View Dashboard | ✔ | ✔ | ✔ |
| View Live Data | ✔ | ✔ | ✔ |
| View Devices | ✔ | ✔ | ✔ |
| View Data Transfer | ✔ | ✔ | ✔ |
| View alarms | ✔ | ✔ | ✔ |
| Acknowledge alarm | ✔ | ✔ | — |
| Control Capture (start/stop, Transfer Now) | ✔ | ✔ | — |
| View Logs | ✔ | ✔ | ✔* |
| Acknowledge Alarm | ✔ | ✔ | — |
| Add / Edit / Delete devices | ✔ | — | — |
| Manage address blocks, tags, triggers, queries, transfer servers | ✔ | — | — |
| Write a value to PLC | ✔ | — | — |
| Manage users | ✔ | — | — |
| Change settings | ✔ | — | — |

\* The application layer grants `ViewLogs` to Operator only; the sidebar shows
Logs to Admin and Operator. Confirm behaviour against your build if it matters
for an audit.

### 3.3 Permission enforcement is dual-layer

Permissions are enforced in **both** processes:

- **UI** — hides nav items, disables commands, hides row buttons.
- **Service** — `ICurrentUserService.Demand(Permission)` inside every service
  method.

**Hiding a button is never the only protection.** If you find a screen where a
denied action is merely invisible rather than rejected, report it — that is a
defect.

### 3.4 Verifying a new account

Always test a new user before handing it over:

1. Sign in as that user.
2. Confirm allowed actions work.
3. Confirm denied actions are rejected **with a message**, not just hidden.
4. Sign back out.

Example test accounts:

```text
operator_test / Operator
viewer_test   / Viewer
```

### 3.5 Rules that protect you

| Rule | Behaviour if you try to break it |
|---|---|
| Last active administrator cannot be deleted | Rejected with an explanation |
| Last active administrator cannot be demoted | Rejected with an explanation |
| Disable vs delete | Disable preserves history; delete removes the login but preserves audit rows |
| Deleting a user never removes audit rows | `AuditLogs` are retained for 365 days by the retention job |

---

## 4. Password and account policy

### 4.1 Defaults

| Setting | Default | Range |
|---|---|---|
| Minimum length | 8 | ≥ 6 enforced at startup |
| Mixed case required | true | — |
| Digit required | true | — |
| Max failed logins | 5 | ≥ 1 |
| Lockout duration | 15 minutes | — |
| BCrypt work factor | 12 | 10–15 |
| Session timeout | 30 minutes | 1–720 |

### 4.2 Password rules

- Stored as BCrypt — **plaintext is never stored or logged**.
- Previous password cannot be reused.
- Accounts created by an admin get `MustChangePassword = true`; the user is
  forced to change the temporary password at first login.
- An unknown username returns the same generic
  `"Invalid username or password."` as a wrong password (timing-attack
  mitigation is applied too), so login errors never reveal which accounts exist.

### 4.3 Locked-out accounts

A lockout is 15 minutes by default. To release a user early:

1. Open **Users**.
2. Select **Unlock** (violet button on the row).

Failed-login counters reset on successful login.

### 4.4 Session timeout

Idle sessions sign out automatically after 30 minutes by default.

To verify the control works:

1. Set it to 1 minute in Settings → Security & retention, save.
2. Wait without touching the keyboard/mouse.
3. Confirm automatic logout.
4. **Restore it to 30 minutes and save.**

Always restore production timeout after testing.

---

## 5. Device and tag configuration rules

### 5.1 Device field constraints

These are enforced by `DeviceValidator` at save time, not merely by the UI:

| Field | Valid range |
|---|---|
| Name | `^[A-Za-z0-9_\-\. ]+$`, max 100, unique |
| Host | IP or hostname |
| Port | 1–65535 |
| Poll interval | **100 – 3,600,000 ms** |
| Timeout | **200 – 60,000 ms** |
| Slot | 0–31 |
| Rack | 0–7 |
| Connection size | 511, 4002, 4000 or 4064 (4000/4064 coerced to 4002) |
| Model | must belong to the chosen protocol + manufacturer |
| Trigger tag | required when capture mode is PLC Trigger |

### 5.2 Protocol and port reference

| Protocol | Default port | Notes |
|---|---|---|
| Modbus TCP | 502 | Unit ID required; word order matters; address blocks used |
| Siemens S7 | 102 | Rack + slot required; no symbol-table browsing |
| Allen-Bradley EtherNet/IP | **44818** | Port must be exactly 44818 or the driver refuses |

Default ports come from the model catalog. If you override the port, be certain
— the Allen-Bradley driver will reject anything other than 44818.

### 5.3 Capture modes — pick deliberately

| Mode | When it stores rows | Use for |
|---|---|---|
| **Manual** | Every poll while Capture is ON | Continuous historian recording |
| **PLC Trigger** | Only while the chosen boolean tag is true | Machine-running gates |
| **Logical** | Only while the expression evaluates true | Compound conditions |
| **Monitor ON / Capture OFF** | Never | Live values only, no historian growth |

**Misconfigured triggers fail safe to no-capture**, not to flooding the
database. If rows stop appearing, check the trigger first.

### 5.4 Tag rules

| Rule | Why |
|---|---|
| Tag name unique per device | Identity |
| No `'` or `,` in names | CSV export integrity |
| `ElementCount` 1–4096 | Memory guard |
| `DecimalPlaces` 0–10 | Display sanity |
| External tags need `QueryId` + `QueryColumnId` | Binding must be explicit |
| A tag cannot be deleted if it is the PLC trigger tag | Would silently break capture |
| A tag cannot be deleted if referenced by a logical condition | Same |
| Renaming a device rewrites `TagReadings.DeviceName` for **untransferred** rows only | Historical rows keep the name they had |

### 5.5 Address blocks (Modbus)

Sequential blocks generate many tags at once.

| Field | Example |
|---|---|
| Register type | Holding Registers |
| Start address | 1 |
| Data type | Float32 |
| Point count | 5 |
| Tag prefix | TEMP |
| Decimal places | 2 |

Start 1, Float32, 5 points, prefix `TEMP` produces `TEMP001`…`TEMP005` with
correct two-register spacing.

- Saving validates overlapping blocks and duplicate prefixes.
- Changing a block regenerates affected tags after confirmation.
- Deleting a block asks whether generated tags should also be removed.
- Address base is **per device** (`OneBasedAddressing`) — the same address string
  can mean different registers on different devices.

### 5.6 Recommended settings

- **Configure only required PLC tags.** Every extra tag costs a read each poll.
- Use `Selected for polling = OFF` for trigger-only tags so they do not flood
  the historian.
- Prefer **Monitor ON / Capture OFF** while proving wiring and scaling.

---

## 6. PLC write safety rules

**Writes are denied by default.** Enabling them is a deliberate administrative
act.

### 6.1 The five independent guards

All must pass or the write is rejected with a specific message:

| # | Guard | Where configured |
|---|---|---|
| 1 | **Tag allowlist** — `IsWriteEnabled` (default false) | Tag editor → Write safety |
| 2 | **Role** — only Admin holds `WriteToPlc` | Role model (fixed) |
| 3 | **Type** — input must parse to the tag's data type | Enforced at write |
| 4 | **Range** — must be within Minimum / Maximum | Tag editor |
| 5 | **Cooldown + read-back** — `MinimumWriteIntervalMs`, then read the controller back and compare | Tag editor |

Additional constraints: only `TagKind.Plc` tags, and only single-element tags
(`ElementCount == 1`).

Defaults: `MinimumWriteIntervalMs` = **1000 ms** (allowed 250–60000).

Read-back comparison uses exact matching for strings/booleans and a float
tolerance of `max(1e-6, |expected| * 1e-5)` for numerics. **A write that does
not read back is reported as a failure.**

Every write is audited with Old value, Requested value and Readback value.

### 6.2 Enabling a writable tag — checklist

- [ ] Business justification recorded
- [ ] Tag has Minimum and Maximum set to real engineering limits
- [ ] Cooldown appropriate for the equipment (raise it for slow actuators)
- [ ] Read-back verification left enabled — **never disable it**
- [ ] Test performed against the **simulator** first
- [ ] Real write covered by a permit and interlock review

### 6.3 Safe test sequence (simulator ON only)

| Test | Expected |
|---|---|
| Write to a tag with Write disabled | Blocked — the driver never executes |
| Write `150` to a 0–100 tag | Rejected with a range message |
| Write `50` | Success **plus read-back confirmation** |
| Immediately write again | Rejected by cooldown |

> **Never test writes against a real plant controller without permits, interlock
> review and operations approval.**

### 6.4 Turning writes off

If a write capability is no longer needed:

1. Open the tag → Write safety.
2. Clear **Write enabled**.
3. Save.

The service reconciles automatically within the 5-second reconcile interval — no
restart required.

---

## 7. External SQL query rules

External queries attach MES/ERP context (batch number, shift, recipe) to
historian rows. They are **read-only by construction**.

### 7.1 Hard rules

- **Exactly one statement, parsed as a SELECT.** Multiple statements, `DROP`,
  `INSERT`, `UPDATE`, `DELETE`, stacked queries and `SELECT INTO` are rejected at
  save **and** verify time — enforced by parsing with
  `Microsoft.SqlServer.TransactSql.ScriptDom`, not by string matching.
- **Use a dedicated SQL account with SELECT permission only.**
- **Encrypt the session.** `Trust server certificate` ON is acceptable only for
  same-machine tests; install a trusted certificate and set it to `False` in
  production.
- Query text limited to 8000 characters.

### 7.2 Negative test you should run once

```sql
SELECT 1; DROP TABLE dbo.ProductionContext;
```

Expected: **rejected**, table intact. If this ever succeeds, stop using the
feature and report a critical defect.

### 7.3 Workflow

1. Create the query (server, database, credentials, SQL text).
2. **Save**, then **Verify** — discovered columns appear
   (`BatchNo`, `Shift`, `Recipe`).
3. Create an **External tag** bound to query + column.
4. During triggered capture the value is refreshed once per cycle and stored
   alongside PLC values.

Runtime limits: connect timeout 10 s, command timeout 30 s,
`CommandBehavior.SingleRow` — only the first row is consumed.

---

## 8. Transfer and database rules

### 8.1 Transfer server fields

| Field | Production guidance |
|---|---|
| Server | SQL instance hosting the central DB |
| Database | Central database name |
| Table | Defaults to `TagDetail`; must match `^[A-Za-z_][A-Za-z0-9_]*$` |
| Integrated Security | **ON** — no stored password |
| Encrypt | **ON** |
| Trust server certificate | **OFF** in production (ON only for same-machine tests) |
| Enabled | Master switch for this destination |

**Save and run Test Connection before enabling.** Use a dedicated
least-privilege SQL login in production.

### 8.2 Idempotency — why retries are safe

Every row carries three keys:

| Column | Source |
|---|---|
| `SourceId` | Installation identity (`C:\ProgramData\WTT\installation-id`) |
| `SourceNode` | Machine name |
| `SourceReadingId` | Local `TagReadings.Id` |

The central table gets a **filtered unique index** on
`(SourceId, SourceReadingId)`. A retry that re-sends committed rows is a no-op,
not a duplicate.

**Local rows are marked transferred only after the remote commit.** A crash in
between causes a re-send, which the index absorbs. The reverse order would lose
data.

### 8.3 Resilience behaviour

- Transient SQL errors retry with exponential backoff and jitter (3 attempts).
- Repeated failures open a **per-server circuit breaker** for 60 s, isolating
  that server while other destinations continue.
- Every attempt records a `TransferBatches` row: record count, duration, status,
  error text.
- Failures never crash the service and never delete local rows.
- Recovery is automatic once the destination is fixed.

### 8.4 Database size management (SQL Server Express 10 GB ceiling)

| Threshold | Action |
|---|---|
| **8 GB** | Investigate: is transfer keeping up? |
| **9 GB** | Stop new capture if transfer/retention cannot drain data |
| **9.5 GB** | Provisioned ceiling (`MAXSIZE`) |

Default retention:

| Setting | Default | Meaning |
|---|---|---|
| `capture.transferredRetentionDays` | 7 | Purge transferred rows older than this |
| `capture.untransferredMaxAgeDays` | **0 (disabled)** | Never silently purge untransferred rows |
| `TransferBatches` | 30 days | Hard-coded |
| `AuditLogs` | 365 days | Hard-coded |
| Alarm retention | 90 days | Runtime `Alarm.RetentionDays` |

The retention job runs at most once per **60 minutes**.

> **Never silently delete untransferred readings.** If you must free space, fix
> transfer first. Setting `untransferredMaxAgeDays` above zero purges
> never-transferred rows and **logs a warning every time**.

### 8.5 Duplicate central rows

Normal operation is idempotent. If duplicates appear:

```sql
SELECT SourceId, SourceReadingId, COUNT(*) AS Copies
FROM dbo.TagDetail
WHERE SourceId IS NOT NULL
GROUP BY SourceId, SourceReadingId
HAVING COUNT(*) > 1;
```

Zero rows expected. Rows returned means someone inserted manually outside the
pipeline — the pipeline itself cannot produce duplicates.

---

## 9. Alarm administration

Alarms follow **ISA-18.2**. As administrator you configure thresholds; operators
acknowledge.

### 9.1 Per-tag alarm configuration

| Setting | Notes |
|---|---|
| Alarm enabled | Master switch for the tag |
| Min threshold | Low alarm |
| Max threshold | High alarm |
| Deadband | **Absolute engineering units**, default 2.0, range 0–50 |
| On-delay seconds | Must stay bad this long before triggering |
| Off-delay seconds | Must stay good this long before clearing |
| Priority | Critical / High / Medium / Low (default Medium) |
| Description | Shown to the operator |

Validation: minimum < maximum, at least one threshold, deadband 0–50.

### 9.2 Runtime protections

| Protection | Value |
|---|---|
| Flood suppression | **max 10 alarms per minute per tag** |
| Shelving | **max 24 hours** (hard limit, cannot be raised) |
| Critical alarms | written **directly to the database** (no queue) |
| Other alarms | bounded queue, 3 retries, then `LOST ALARM` critical log |
| Retention | startup + every 24 h; `Alarm.RetentionDays` (default 90, 0 = forever) |

### 9.3 Notification

| Channel | Behaviour |
|---|---|
| In-app beep | Critical = 3 bursts at 1200 Hz, High = 2 at 1000 Hz; gated by `Alarm.BeepEnabled` and priority ≥ High |
| Email | **Critical and High only**, throttled to 5 concurrent, 10 s timeout, `NotifyCleared` is deliberately a no-op |
| Sidebar badge | Live count of active alarms |
| Alarm History tab | Full audit with filters and paging |

### 9.4 Email configuration

`AlarmOptions` code defaults:

| Setting | Default |
|---|---|
| `EmailEnabled` | false |
| `SmtpHost` | null (falls back to `localhost`) |
| `SmtpPort` | 587 |
| `SmtpUseSsl` | true |
| `SmtpUsername` / `SmtpPassword` | null |
| `EmailFrom` / `EmailToCsv` | null |

Enable only after confirming SMTP reachability — a failing notifier is swallowed
by the composite notifier so it cannot block others, but you will get no mail.

---

## 10. Backup and recovery

### 10.1 What runs automatically

Scheduled task **"WTT Database Backup"**, daily at 02:00, as `SYSTEM`:

- `BACKUP DATABASE ... WITH COPY_ONLY, CHECKSUM, INIT`
- `RESTORE VERIFYONLY ... WITH CHECKSUM`
- Output `WTT-yyyyMMdd-HHmmss-<8 hex>.bak` in `C:\ProgramData\WTT\Backups`
- Prunes `.bak` older than 30 days

SQL Server Express has **no SQL Agent**, which is why a Windows scheduled task
is used instead.

### 10.2 Before the first backup

```powershell
New-Item -ItemType Directory -Force "C:\ProgramData\WTT\Backups"
icacls "C:\ProgramData\WTT" /grant "NT SERVICE\MSSQLSERVER:(OI)(CI)M"
```

For a named Express instance use its service SID, e.g.
`NT SERVICE\MSSQL$SQLEXPRESS`.

### 10.3 Policy you must maintain

- Daily full backup with checksum.
- 30-day local retention **plus an off-machine encrypted copy**.
- **`RESTORE VERIFYONLY` is not a disaster-recovery test.** Perform a real
  restore drill regularly and record the result.
- Alert at 8 GB; stop capture before 9 GB if transfer cannot drain.

### 10.4 Restore drill procedure

1. Restore to a scratch database, **not** over `WTT`:
   ```sql
   RESTORE DATABASE [WTT_DRILL] FROM DISK = N'C:\ProgramData\WTT\Backups\<file>.bak'
     WITH MOVE 'WTT' TO 'C:\...\WTT_DRILL.mdf',
          MOVE 'WTT_log' TO 'C:\...\WTT_DRILL_log.ldf';
   ```
2. Verify row counts against expectations.
3. Drop `WTT_DRILL`.
4. Record date, file used and duration.

### 10.5 What is preserved on uninstall

`Uninstall-WTT.ps1` removes the service, scheduled task and shortcut, and
**deliberately preserves**:

- the `WTT` database
- `C:\ProgramData\WTT\Backups`
- `C:\ProgramData\WTT\Logs`

To fully remove the product you must additionally delete `C:\Program Files\WTT`,
`C:\ProgramData\WTT` and — only if you are certain — the database and the
`WTTUsers` group.

**Never drop a database that still holds untransferred readings.**

---

## 11. Monitoring and health checks

### 11.1 Daily checks (Operator can do this)

1. Dashboard heartbeat fresh (`running`, recent timestamp).
2. Readings-today increased since yesterday.
3. Buffered count low and not climbing.
4. Transfer "Last success" recent.
5. Any device error cards acknowledged.

### 11.2 Signal reference

| Signal | Healthy | Investigate |
|---|---|---|
| `ServiceRuntimeState.HeartbeatAtUtc` | Updated within 60 s | Stale ⇒ service stopped |
| `DeviceRuntimeStates.IsConnected` | true | false ⇒ check network/PLC |
| `DeviceRuntimeStates.ErrorCount` | stable | climbing ⇒ read failures |
| `TagReadings WHERE IsTransferred = 0` | low and draining | growing ⇒ transfer blocked |
| `DeviceServers.LastSuccessAtUtc` | recent | stale ⇒ destination problem |
| `TransferBatches.Status` | Success / NothingToTransfer | Failed ⇒ read `ErrorMessage` |
| Database size | < 8 GB | ≥ 8 GB ⇒ act |

### 11.3 Verification queries

Run in SSMS with Windows Authentication against `.`:

```sql
-- Heartbeat and instance
USE WTT;
SELECT TOP (5) * FROM dbo.ServiceRuntimeState ORDER BY Id;
SELECT TOP (20) * FROM dbo.DeviceRuntimeStates;

-- Pending vs transferred
SELECT COUNT(*) AS Pending FROM dbo.TagReadings WHERE IsTransferred = 0;
SELECT COUNT(*) AS Today FROM dbo.TagReadings
WHERE TimestampUtc >= CAST(GETDATE() AS DATE);

-- Recent transfer results
SELECT TOP (20) DeviceName, TargetServer, RecordCount, Status, ErrorMessage, StartedAtUtc
FROM dbo.TransferBatches ORDER BY Id DESC;

-- Transfer destination health
SELECT ServerName, DatabaseName, IsEnabled, LastSuccessAtUtc, LastError
FROM dbo.DeviceServers;

-- Migrations present
SELECT MigrationId, ProductVersion FROM dbo.__EFMigrationsHistory
ORDER BY MigrationId;

-- Active alarms
SELECT Priority, COUNT(*) FROM dbo.AlarmEvents
WHERE State IN (1, 2) GROUP BY Priority;
```

---

## 12. Security checklist

### 12.1 Accounts

- [ ] No shared/generic admin account; named individuals only
- [ ] Every created user changed their temporary password
- [ ] Former employees disabled promptly
- [ ] At least two active administrators (so one holiday does not lock you out)
- [ ] Viewer accounts used for anything read-only (auditors, big screens)

### 12.2 Database

- [ ] Windows Integrated Security — **no SQL passwords stored in config**
- [ ] Runtime accounts hold only the `wtt_runtime` role
- [ ] Migrations run under a separate deployment administrator
- [ ] `Encrypt=True` set; `TrustServerCertificate=False` with trusted certs for
      any remote SQL
- [ ] Backup folder ACL restricted to the SQL service SID

### 12.3 Application

- [ ] `EnablePiiMasking = true` (default) — passwords and connection strings
      never logged
- [ ] Session timeout left at a sane value (30 min default)
- [ ] PLC simulator **OFF** in production (otherwise you record mock data)
- [ ] Only intended tags have `IsWriteEnabled`
- [ ] External queries bound to a SELECT-only SQL account

### 12.4 Network / host

- [ ] Service runs as `NT SERVICE\WTTData` (not LocalSystem)
- [ ] `C:\ProgramData\WTT` ACLs intact after any manual change
- [ ] UI never runs elevated (`asInvoker`)
- [ ] Test systems use `127.0.0.1` or clearly fake IPs — **never a real plant
      controller IP**

---

## 13. Change management

### 13.1 Configuration changes (routine)

1. Disable or pause capture for the affected device when practical.
2. Edit device, tags, triggers or servers and save.
3. Confirm the success notification and refresh to verify persistence.
4. Re-enable capture and watch one poll cycle in Live Data.
5. Check the audit log for the change record.

Changes reconcile automatically within the **5-second** reconcile interval — no
restart needed. Editing a device or tag restarts exactly that device's worker
(fingerprint change), not the whole pipeline.

### 13.2 Software updates

Before a schema-changing release:

1. Take a manual backup: `.\deploy\Backup-Wtt.ps1`
2. Review which EF migrations will be applied
3. Keep the previous package available
4. Schedule outside capture-critical hours
5. Close the UI (the updater refuses to run while `WTT.exe` is open)

Then follow `docs/INSTALL_AND_UPDATE.md` §7. `Update-WTT.ps1` takes a file
snapshot **and** a verified database backup, and rolls both back together on any
failure.

### 13.3 Schema change review

The service **verifies** schema and refuses to start if migrations are pending;
the UI **migrates**. So after a schema-changing update:

1. Confirm the migration bundle ran (check `__EFMigrationsHistory`).
2. Confirm `WTTData` reaches `Running`.
3. Confirm the heartbeat is fresh.

### 13.4 Rollback

See `docs/INSTALL_AND_UPDATE.md` §8. Remember: rolling the database backwards
**loses readings captured after that backup**. Take a fresh backup first and
record what was lost.

---

## 14. Escalation and support package

### 14.1 When to escalate

- Data stopped and restart did not fix it
- Duplicate central rows detected
- Database at 8 GB and transfer cannot drain
- Any write that reached a controller without authorization
- Login errors that reveal account existence

### 14.2 What to send

1. Exact date, time and timezone; device and tag names.
2. Screenshot of Dashboard heartbeat and the affected device card.
3. Screenshot of Data Transfer pending count and the last batch error.
4. UI logs from `%LocalAppData%\WTT\Logs` and service logs from
   `C:\ProgramData\WTT\Logs` covering the incident window.
5. Output of the pending-count and duplicate-check queries (§8.5, §11.3).
6. Steps already tried and their results.

### 14.3 What NOT to send

- Passwords (any)
- Private keys
- Full production connection strings
- Unrelated database contents

### 14.4 Useful log lines

| Line | Meaning |
|---|---|
| `Capture supervisor cycle failed; will retry.` | Transient capture fault, self-healing |
| `Transfer cycle failed unexpectedly; will retry next interval.` | Transient transfer fault, self-healing |
| `LOST ALARM: ...` | **Critical** — alarm queue exhausted after retries |
| `Another WTT pipeline service instance is already running.` | Duplicate service |
| `Database upgrade required: ...` | Migrations pending; service will not start |
| `At least one active administrator must remain` | Last-admin protection triggered |
| `A stored password could not be decrypted on this machine` | DPAPI secret moved between machines |

---

## 15. Document history

| Version | Date | Change |
|---|---|---|
| 1.2.0 | 2026-10-09 | Created. Replaces the 9-line summary that previously lived only inside the release artifact. Covers enrollment, roles, write safety, external query policy, transfer idempotency, alarms, backup, monitoring, security checklist, change management and escalation. |
