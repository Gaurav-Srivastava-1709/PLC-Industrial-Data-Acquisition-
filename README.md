# WTT Industrial Data Acquisition

**Version:** 1.2.0 · **Platform:** .NET 10 LTS (Windows x64) · **Database:** Microsoft SQL Server Express

WTT is a Windows application for **unattended PLC data acquisition**: it polls industrial controllers over the plant network, buffers every reading in a local SQL Server Express historian, and reliably transfers batches to one or more central SQL Server databases for reporting, MES/ERP integration and long-term storage.

It ships as two cooperating processes over one database:

| Process | Executable | Runs as | Owns |
|---|---|---|---|
| Operator / admin client | `WTT.exe` | Interactive user (never elevated) | Dashboards, configuration, live data, trends, alarms, users, settings, logs |
| Acquisition service | `WTT.Service.exe` | Windows Service `WTTData` | PLC polling, alarm evaluation, store-and-forward transfer, retention jobs |

**Closing the client never stops acquisition.** The service is the only process that talks to PLCs; the client only reads persisted state. Health is not an HTTP endpoint — it is a database heartbeat shared by both processes.

---

## Contents

1. [Highlights](#1-highlights)
2. [Supported PLC hardware](#2-supported-plc-hardware)
3. [Architecture at a glance](#3-architecture-at-a-glance)
4. [Repository layout](#4-repository-layout)
5. [Projects](#5-projects)
6. [Quick start (development)](#6-quick-start-development)
7. [Running with the mock PLC simulator](#7-running-with-the-mock-plc-simulator)
8. [Tests](#8-tests)
9. [Building a release](#9-building-a-release)
10. [Installing and updating](#10-installing-and-updating)
11. [Configuration](#11-configuration)
12. [Database](#12-database)
13. [Documentation](#13-documentation)
14. [Versioning and CI](#14-versioning-and-ci)
15. [Repository hygiene](#15-repository-hygiene)

---

## 1. Highlights

- **Three protocols, one model** — Modbus TCP, Siemens S7 and Allen-Bradley EtherNet/IP behind a single `IPlcDriver` abstraction.
- **Store-and-forward historian** — readings buffer locally with `IsTransferred = 0`, ship in batches, and are marked only *after* the remote commit. A crash costs a duplicate attempt, never data.
- **Idempotent transfer** — every row carries `SourceId` (installation GUID), `SourceNode` (machine) and `SourceReadingId`; a filtered unique index on the central table makes retries a no-op.
- **Capture modes** — Manual (continuous), PLC Trigger (boolean gate) and Logical (left-to-right boolean expression over tags). A broken trigger fails safe to *no capture*, never to flooding the database.
- **External SQL context** — attach MES/ERP values (batch, shift, recipe) to historian rows. Exactly one parsed `SELECT` statement is accepted; everything else is rejected at save and verify time.
- **PLC write safety** — five independent guards: tag allowlist, role, type, range, cooldown + read-back verification. Writes are denied by default.
- **ISA-18.2 alarms** — on/off delay, absolute deadband, flood suppression (10/min/tag), shelving (≤24 h), priorities, acknowledgment and history.
- **Role-based access** — Admin / Operator / Viewer over 13 permissions, enforced in *both* the UI and the service.
- **No shared default password** — the first administrator is created interactively at first run.
- **Resilient** — Polly retry + per-server circuit breaker on transfer, exponential reconnect backoff with jitter on PLCs, EF retry policy, Windows Service failure recovery.
- **Deterministic mock PLC** — the entire acceptance test plan runs without hardware.
- **Structured logging with PII masking** — Serilog, date-foldered files, correlation IDs, in-app log and audit viewers.
- **Hand-rendered SCADA trend plot** — multi-pen, pan/zoom, quality gaps, shared/normalized Y — no charting dependency.

---

## 2. Supported PLC hardware

| Protocol | Port | Manufacturers / models | Address blocks | Tag browsing |
|---|---|---|:---:|:---:|
| **Modbus TCP** | 502 | Yokogawa MW100, Yokogawa GM10, Advantech ADAM-6017/6018, Walchem W600, Generic Modbus TCP | ✔ | — |
| **Siemens S7** | 102 | S7-300, S7-400, S7-1200, S7-1500 (rack + slot) | — | — |
| **Allen-Bradley EtherNet/IP** | **44818** | ControlLogix, CompactLogix, Micro800, PLC-5, SLC 500, MicroLogix | — | ControlLogix / CompactLogix only |

Notes:

- The Allen-Bradley driver **requires port 44818** and refuses anything else.
- S7 does not expose a symbol table over S7comm, so browsing is unsupported and 64-bit integer tags are rejected (use `Float64`/`LREAL`, or split into two 32-bit words).
- Modbus addressing is **per device** — the same address string can mean different registers on a one-based vs zero-based device.
- Unknown protocol/manufacturer combinations **throw** `PlcConfigurationException`. The driver factory never falls back to the simulator: silent simulated data would be a data-integrity incident.

---

## 3. Architecture at a glance

```text
┌─────────────────────────────┐        ┌──────────────────────────────────┐
│  WTT.exe  (WPF UI)          │        │  WTT.Service.exe (WTTData)       │
│  - dashboards, config       │  SQL   │  - per-device capture workers    │
│  - live values, trends      │◄──────►│  - alarm evaluation              │
│  - users, settings, logs    │  WTT   │  - transfer worker (store/forward)│
│  NEVER talks to PLCs        │        │  - retention jobs                │
│  NEVER runs the pipeline    │        │  OWNS all PLC communication      │
└─────────────────────────────┘        └──────────────────────────────────┘
                     ▲                              │
                     │                              │ TCP to PLCs
                     │                       ┌──────▼───────┐
                     │                       │  PLC / DAQ   │
                     │                       └──────────────┘
                     │                              │
              central SQL Server ◄── bulk copy ─────┘
              (TagDetail table)
```

**Layering:**

```text
Domain  ◄──  Application  ◄──  Infrastructure  ◄──  Service
                          ◄──  UI
```

- `WTT.Domain` has **zero package references** — entities, enums, driver abstraction, exceptions.
- `WTT.Application` declares interfaces, DTOs, FluentValidation validators and the permission model. No implementations.
- `WTT.Infrastructure` implements everything and owns all I/O (EF Core, PLC drivers, pipelines, auth, alarms).
- `WTT.Service` and `WTT.UI` are hosts. Only the service calls `AddWttPipelineWorkers()` — the UI deliberately does not, so there is never a second competing poller.

Full detail — entities, enums, indexes, resilience layers, configuration keys, CI — in **[`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md)**.

---

## 4. Repository layout

```text
WTT.slnx                  Solution (XML format)
WTT.slnxLaunch            Launch profiles (service + UI, mock PLC)
Directory.Build.props     LangVersion, Nullable, Version, analysis settings
Directory.Packages.props   Central Package Management (all versions pinned here)
global.json                .NET SDK 10.0.400, rollForward latestPatch
.config/dotnet-tools.json  Local tool: dotnet-ef 10.0.11
.github/workflows/ci.yml  CI: unit + integration tests, release artifacts
deploy/                    Install, update, uninstall, backup, publish scripts
docs/                      All documentation (see §13)
src/                       The five projects
tests/                     WTT.Tests (xUnit)
artifacts/                 Build output — git-ignored
```

---

## 5. Projects

| Project | TFM | Output | References | Role |
|---|---|---|---|---|
| `src/WTT.Domain` | `net10.0` | class lib | — | Entities, enums, `IPlcDriver`, exceptions |
| `src/WTT.Application` | `net10.0` | class lib | Domain | Service interfaces, DTOs, validators, options, permissions |
| `src/WTT.Infrastructure` | `net10.0-windows` | class lib | Application | EF Core, PLC drivers, capture/transfer/alarms, auth |
| `src/WTT.Service` | `net10.0-windows` | `WinExe`, `win-x64` | Infrastructure | Unattended pipeline host (Windows Service `WTTData`) |
| `src/WTT.UI` | `net10.0-windows` | `WTT.exe` (WPF) | Application, Infrastructure | Operator and administrator client |
| `tests/WTT.Tests` | `net10.0-windows` | test project | Infrastructure, UI | xUnit test suite |

### Key dependencies (pinned centrally in `Directory.Packages.props`)

| Area | Packages |
|---|---|
| Persistence | `Microsoft.EntityFrameworkCore.SqlServer` 10.0.11, `Microsoft.Data.SqlClient` 7.0.2 |
| PLC | `libplctag` 1.5.2 (EtherNet/IP), `S7netplus` 0.20.0 (S7), `NModbus` 3.0.83 (Modbus) |
| Resilience | `Polly.Core` 8.7.0 |
| Security | `BCrypt.Net-Next` 4.2.0, `System.Security.Cryptography.ProtectedData` (DPAPI) |
| SQL parsing | `Microsoft.SqlServer.TransactSql.ScriptDom` 180.107.0 |
| Validation | `FluentValidation` 12.1.1 |
| Logging | `Serilog` 4.3.0 + file/debug sinks, thread/environment enrichers |
| UI | `WPF-UI` 4.3.0, `CommunityToolkit.Mvvm` 8.4.2, `Microsoft.Extensions.Hosting` 10.0.11 |
| Tests | `xunit` 2.9.3, `FluentAssertions` 7.2.0, `NSubstitute` 5.3.0 |

Deliberately **absent**: charting library, MediatR/AutoMapper/CQRS, repository layer, ASP.NET/HTTP tier, health-check framework, file-based database/LocalDB.

---

## 6. Quick start (development)

### Prerequisites

- Windows 10/11 or supported Windows Server x64
- [.NET SDK 10.0.400](https://dotnet.microsoft.com/download) (pinned by `global.json`)
- SQL Server Express running locally
- Visual Studio 2026 / VS Code / Rider (optional)

### Build

```powershell
git clone <repo-url>
cd <repo-folder>

dotnet tool restore          # installs dotnet-ef 10.0.11
dotnet restore WTT.slnx
dotnet build WTT.slnx -c Debug
```

### Database (first run)

From an administrator terminal:

```powershell
sqlcmd -S "." -E -C -b -i "deploy\Provision-Database.sql"
dotnet ef database update --project "src\WTT.Infrastructure\WTT.Infrastructure.csproj" `
                          --startup-project "src\WTT.Infrastructure\WTT.Infrastructure.csproj"
```

Alternatively, just launch the client — `DatabaseInitializer` runs `MigrateAsync()` on startup. The **service** only *verifies* schema and refuses to start if migrations are pending.

### Run

Use the launch profile **`WTT Full System - Mock PLC`** (`WTT.slnxLaunch`), which starts both the service and the UI, or run them separately:

```powershell
dotnet run --project src\WTT.Service
dotnet run --project src\WTT.UI
```

On first run you will see **Create the first administrator** — there is no shared default password. Create the admin, sign in, and you land on the Dashboard.

---

## 7. Running with the mock PLC simulator

No hardware required:

```powershell
# UI launch profile "WTT UI - Mock PLC" sets WTT_ENVIRONMENT=Development
# Service launch profile "WTT Service - Mock PLC" sets:
$env:WTT_Simulation__UseMockPlc = "true"
```

Or flip **Settings → Simulation** at runtime (persisted as `simulation.useMockPlc`, applied without restart).

The simulator is **deterministic** — no `Random`, no `DateTime.Now`. Signal values derive from a stable hash of each tag's address plus name keywords (`temp` → 65, `press` → 4.2, `level` → 50), `trig` toggles every 30 s, `batch` emits `BATCH-yyyymmdd-NNN`. Shared state lives in `C:\ProgramData\WTT\WTT_MockPlcState.json` guarded by a named mutex, and writes expire after 15 s — so the UI and the service see the same simulated plant.

---

## 8. Tests

```powershell
# Unit tests only — no SQL Server needed (typical pre-commit check)
dotnet test tests\WTT.Tests\WTT.Tests.csproj -c Debug --filter "Category!=Integration"

# Full suite — requires a reachable SQL Server
dotnet test WTT.slnx -c Debug
```

**`Category=Integration`** marks the four classes that need a real SQL Server (`AuthAndPersistenceTests`, `CapturePipelineEndToEndTests`, `DataTransferIntegrationTests`, `RuntimeStatusIntegrationTests`). Each gets its own throwaway database `WTT_Test_{guid}` and drops it on disposal.

Coverage areas: alarm evaluation (delay, deadband, flood suppression, shelving), Allen-Bradley address parsing and the model catalog, authentication/persistence, the capture pipeline end-to-end, data transfer, external SQL select policy, installation identity, live-data loading, Modbus codec, runtime settings, and trigger evaluation.

Test facts worth knowing:

- Parallelization is **disabled** assembly-wide — integration tests each own a database.
- `LiveDataViewModel` tests run through a helper that pumps the dispatcher on a dedicated STA thread, because xUnit runs MTA.
- The suite reports **97 passing tests** with `Category!=Integration` (run it fresh to confirm the current number before quoting it).

---

## 9. Building a release

```powershell
.\deploy\Publish-Release.ps1 -Version 1.2.2
```

This runs the Release test suite (skip with `-SkipTests`), publishes UI and Service **self-contained `win-x64`** (no single-file, no trimming — both break WPF/EF), builds the EF migration bundle into `Database\WTT.Database.exe`, copies `deploy\` and `docs\`, writes `release.json`, and zips the package:

```text
artifacts/WTT-1.2.2/
├── UI/            WTT.exe + dependencies
├── Service/       WTT.Service.exe + dependencies
├── Database/      WTT.Database.exe   (EF migration bundle)
├── deploy/        install/update/backup scripts
├── docs/          all documentation
└── release.json   { version, createdUtc, runtime }
```

`artifacts/` is git-ignored — packages are build outputs.

---

## 10. Installing and updating

```powershell
# Fresh install (elevated terminal)
.\deploy\Install-WTT.ps1 -PackageRoot .\artifacts\WTT-1.2.2

# Atomic update with file snapshot + DB backup + automatic rollback
.\deploy\Update-WTT.ps1 -PackageRoot .\artifacts\WTT-1.2.2

# Remove service/task/shortcut — database, backups and logs are preserved
.\deploy\Uninstall-WTT.ps1
```

Named SQL Express instance:

```powershell
.\deploy\Install-WTT.ps1 -PackageRoot .\artifacts\WTT-1.2.2 `
    -ServerInstance '.\SQLEXPRESS' -SqlServiceName 'MSSQL$SQLEXPRESS'
```

The installer registers the delayed-start service `WTTData`, adds an SQL Server dependency, configures restart recovery (`restart/5000 restart/15000 restart/60000`), grants its virtual service account least-privilege database access, creates the restricted local `WTTUsers` group, registers the daily verified backup task, and creates a public desktop shortcut.

> **Sign out and back in once after first installation** so Windows applies the new `WTTUsers` group membership to your token. Skipping this is the most common cause of "access denied" right after install.

Step-by-step detail, rollback and troubleshooting: **[`docs/INSTALL_AND_UPDATE.md`](docs/INSTALL_AND_UPDATE.md)**.

---

## 11. Configuration

Both hosts clear config sources and re-add them explicitly, so **environment overrides use the `WTT_` prefix**:

```powershell
$env:WTT_Database__ConnectionString = "Server=.\SQLEXPRESS;Database=WTT;Integrated Security=True;Encrypt=True;TrustServerCertificate=True"
$env:WTT_Simulation__UseMockPlc = "true"
$env:WTT_ENVIRONMENT = "Development"    # selects appsettings.Development.json
```

| File | Purpose |
|---|---|
| `src/WTT.UI/appsettings.json` | Shared defaults (also copied into the Service output) |
| `src/WTT.UI/appsettings.Development.json` | Debug sink on, file logging off |
| `src/WTT.Service/appsettings.Service.json` | Service-only Serilog file sink → `C:\ProgramData\WTT\Logs\service-.log` |

Key settings (defaults):

| Section | Setting | Default |
|---|---|---|
| `Database` | `ConnectionString` | `Server=.;Database=WTT;Integrated Security=True;Encrypt=True;TrustServerCertificate=True` |
| `Capture` | `ReconcileIntervalSeconds` | `5` |
| `Capture` | `MinPollIntervalMs` | `100` |
| `Capture` | `ReconnectInitialDelaySeconds` / `ReconnectMaxDelaySeconds` | `2` / `60` |
| `Capture` | `CurrentValueFlushSeconds` | `5` |
| `Capture` | `TransferredRetentionDays` | `7` |
| `Capture` | `UntransferredMaxAgeDays` | `0` (never purge untransferred) |
| `Transfer` | `IntervalSeconds` | `5` |
| `Transfer` | `BatchSize` / `BulkCopyBatchSize` | `10000` / `1000` in code — but `appsettings.json` overrides `BatchSize` to `1000`, so **1000** is the effective default |
| `Transfer` | `RetryCount` / `CircuitBreakerFailures` / `CircuitBreakerDurationSeconds` | `3` / `5` / `60` |
| `Transfer` | `MaxBatchesPerDevicePerCycle` | `4` |
| `Security` | `SessionTimeoutMinutes` | `30` |
| `Security` | `MaxFailedLogins` / `LockoutMinutes` | `5` / `15` |
| `Security` | `MinPasswordLength` / `BcryptWorkFactor` | `8` / `12` |
| `Simulation` | `UseMockPlc` | `false` |
| `Logging` | `LogDirectory` / `RetainedFileCountLimit` / `FileSizeLimitBytes` | `%LocalAppData%/WTT/Logs` / `30` / `20971520` |
| `Logging` | `MinimumLevel` / `EnablePiiMasking` | `Information` / `true` |

Runtime settings live in the database (`AppSettings` table) and apply **without restart**: `ui.theme`, `simulation.useMockPlc`, `security.sessionTimeoutMinutes`, `capture.transferredRetentionDays`, `transfer.intervalSeconds`, `Alarm.RetentionDays`, `Alarm.BeepEnabled`.

Complete reference — including code defaults and two known discrepancies — in [`docs/ARCHITECTURE.md` §14](docs/ARCHITECTURE.md#14-configuration-reference).

---

## 12. Database

**Windows Integrated Security is mandatory.** WTT stores no local database password; `AttachDbFilename` and LocalDB are rejected at the connection-string validator.

Local database `WTT` — 15 tables:

```text
Devices            DeviceAddresses       DeviceTags          DeviceTriggerConditions
DeviceQueries      DeviceQueryColumns    DeviceServers       TagReadings
TransferBatches    Users                 AuditLogs           AppSettings
ServiceRuntimeState                      DeviceRuntimeStates  AlarmEvents
```

Central destination table (default `TagDetail`, created/upgraded at runtime):

```text
Pk_TagDetail BIGINT IDENTITY PK · TagName · PLCName · TagValue · TimeStamp · TimeStampUTC
Quality · ScanId · SourceId · SourceNode · SourceReadingId
+ filtered unique index UX_<table>_SourceId (SourceId, SourceReadingId)
+ index IX_<table>_PLC_Time (PLCName, TimeStamp) INCLUDE (TagName, TagValue)
```

Six EF Core migrations, **no stored procedures, functions, views or triggers**. Notable indexes: `IX_TagReadings_HistorianTrend` (covering, for trends) and `IX_AlarmEvents_ActiveQuery` (filtered on `State IN (1, 2)` — must include Acknowledged alarms).

SQL Server Express has a **10 GB** data ceiling (provisioned `MAXSIZE` is 9500 MB). Investigate at 8 GB; stop new capture before 9 GB if transfer cannot drain. **Never silently delete untransferred readings.**

Database provisioning, backup and cutover: **[`docs/SQL_SERVER_EXPRESS.md`](docs/SQL_SERVER_EXPRESS.md)**.

---

## 13. Documentation

| Document | Audience | Contents |
|---|---|---|
| [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) | Developers, support | Full technical reference: layers, entities, enums, drivers, pipelines, indexes, resilience, configuration, logging, testing, CI, design decisions |
| [`docs/USER_GUIDE.md`](docs/USER_GUIDE.md) | Operators, admins, viewers | End-to-end user manual: login, dashboard, devices, tags, capture modes, live data, PLC writes, transfer, logs, users, settings, troubleshooting, glossary |
| [`docs/ADMIN_GUIDE.md`](docs/ADMIN_GUIDE.md) | Administrators | First-run enrollment, roles, password policy, device/tag rules, write safety, external SQL policy, transfer idempotency, alarms, backup, monitoring, security checklist, change management, escalation |
| [`docs/UI_STYLE_GUIDE.md`](docs/UI_STYLE_GUIDE.md) | UI developers | Colour system, button catalogue, tabs, sidebar/title bar, tables, dashboard tokens, theming, charts, review checklist |
| [`docs/INSTALL_AND_UPDATE.md`](docs/INSTALL_AND_UPDATE.md) | Deployment engineers | Installer step-by-step, package layout, update + rollback, backup task, named instances, script reference, troubleshooting |
| [`docs/SQL_SERVER_EXPRESS.md`](docs/SQL_SERVER_EXPRESS.md) | DBAs, support | Runtime configuration, provisioning, data cutover, backup policy |
| [`docs/MANUAL_TEST_PLAN_HINDI.md`](docs/MANUAL_TEST_PLAN_HINDI.md) | Field testers | 31-section manual acceptance plan with test case IDs (Hinglish, by design) |

---

## 14. Versioning and CI

**Version source of truth:** `Directory.Build.props` → `<Version>1.2.0</Version>`, applied to every project. `Publish-Release.ps1 -Version` overrides it at publish time.

> ⚠️ The shipped artifact `artifacts/WTT-1.2.1` is currently **ahead** of `Directory.Build.props`. Bump `Directory.Build.props` when cutting a release so the two agree.

**CI** (`.github/workflows/ci.yml`, on `push`/`pull_request` to `main`):

1. **`build-and-test`** — `windows-latest`, SQL Server 2022 container; restore, Release build, unit tests (`Category!=Integration`), then EF migrations + integration tests; uploads `*.trx`.
2. **`deploy-artifacts`** (main only) — publishes self-contained UI + Service, builds the migration bundle, uploads `wtt-release-<sha>` (30-day retention).

**Local tooling:** `dotnet tool restore` installs `dotnet-ef` 10.0.11 (pinned, no rollForward).

---

## 15. Repository hygiene

- **Git-ignored:** `bin/`, `obj/`, `*.user`, `.vs/`, `*.log`, `Logs/`, `artifacts/`, `node_modules/`.
- **No `.editorconfig` or `nuget.config`** in this repository — style is enforced by `Directory.Build.props` (`AnalysisLevel=latest-recommended`, nullable warnings are errors).
- **Comments** in parts of the codebase (`MainWindow.xaml`, `Styles.xaml`, `LoggingController`, `App.xaml.cs`) are written in Hinglish; this is accepted as-is.
- UI exe is `WTT.exe` (`AssemblyName=WTT`); service is `WTTData`; local group is `WTTUsers`; scheduled task is `WTT Database Backup`.
