# WTT Architecture

Version: 1.2.0
Audience: Developers, reviewers, integrators, support engineers doing deep diagnosis
Scope: All six projects, the database, the runtime pipelines and cross-cutting concerns
Related: `docs/USER_GUIDE.md` (behaviour), `docs/UI_STYLE_GUIDE.md` (XAML conventions), `docs/SQL_SERVER_EXPRESS.md` (database operations)

---

## Contents

1. [System overview](#1-system-overview)
2. [Solution layout](#2-solution-layout)
3. [Dependency rules](#3-dependency-rules)
4. [Domain layer](#4-domain-layer)
5. [Application layer](#5-application-layer)
6. [Infrastructure layer](#6-infrastructure-layer)
7. [Service process](#7-service-process)
8. [UI process](#8-ui-process)
9. [Data flow end to end](#9-data-flow-end-to-end)
10. [Database schema](#10-database-schema)
11. [Security model](#11-security-model)
12. [Resilience and retry strategy](#12-resilience-and-retry-strategy)
13. [Alarms (ISA-18.2)](#13-alarms-isa-182)
14. [Configuration reference](#14-configuration-reference)
15. [Logging and observability](#15-logging-and-observability)
16. [Testing strategy](#16-testing-strategy)
17. [Build and CI](#17-build-and-ci)
18. [Design decisions and trade-offs](#18-design-decisions-and-trade-offs)

---

## 1. System overview

WTT is an unattended industrial data-acquisition product. It polls PLCs over the
plant network, buffers every reading in a local SQL Server Express historian, and
reliably ships batches to one or more central SQL Server databases.

Two processes, one database:

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

**The single most important property:** closing the UI never stops acquisition.
The service owns polling, storage and transfer; the UI only reads persisted
state. Stopping the service or SQL Server *does* stop capture — deliberately.

Health is not an HTTP endpoint. It is a database heartbeat: one
`ServiceRuntimeState` row plus one `DeviceRuntimeStates` row per device, read by
both processes.

---

## 2. Solution layout

`WTT.slnx` (XML solution format, .NET 10 style):

```
/src/
   WTT.Domain           net10.0        pure domain, zero packages
   WTT.Application      net10.0        contracts, DTOs, validators
   WTT.Infrastructure   net10.0-windows EF Core, PLC drivers, services
   WTT.Service          net10.0-windows Windows Service host (WTTData)
   WTT.UI               net10.0-windows WPF client (WTT.exe)
/tests/
   WTT.Tests            net10.0-windows xUnit
/Solution Items/
   Directory.Build.props  Directory.Packages.props  .gitignore
```

Toolchain pinned by `global.json`:

```json
{ "sdk": { "version": "10.0.400", "rollForward": "latestPatch", "allowPrerelease": false } }
```

### 2.1 Project facts

| Project | Output | Refs | Role |
|---|---|---|---|
| `WTT.Domain` | class lib | — | Entities, enums, driver abstraction, exceptions |
| `WTT.Application` | class lib | Domain | Interfaces, DTOs, FluentValidation, options, permission model |
| `WTT.Infrastructure` | class lib | Application | EF Core, PLC drivers, capture/transfer/alarms, auth |
| `WTT.Service` | `WinExe`, `net10.0-windows`, `win-x64` | Infrastructure | Unattended pipeline host |
| `WTT.UI` | `WinExe` (`AssemblyName=WTT`) | Application, Infrastructure | WPF client |
| `WTT.Tests` | test project | Infrastructure, UI | xUnit |

`WTT.UI` references `Application` **and** `Infrastructure` (it needs
`AddInfrastructure` for DI). `WTT.Tests` references `Infrastructure` and `UI` so
it can test view-models.

### 2.2 Build configuration

`Directory.Build.props` (applies to every project):

| Property | Value |
|---|---|
| `LangVersion` | `latest` |
| `Nullable` | `enable` |
| `ImplicitUsings` | `enable` |
| `WarningsAsErrors` | `nullable` (only nullable warnings are errors) |
| `TreatWarningsAsErrors` | `false` |
| `AnalysisLevel` | `latest-recommended` |
| `RestorePackagesWithLockFile` | `true` |
| `Version` | `1.2.0` |
| `Product` | `WTT Industrial Data Acquisition` |
| `NeutralLanguage` | `en-US` |

`Directory.Packages.props` enables **Central Package Management** (plus
`CentralPackageTransitivePinningEnabled`), so no project declares a version —
all versions live in one file.

### 2.3 Key package versions

| Package | Version | Purpose |
|---|---|---|
| `Microsoft.EntityFrameworkCore.SqlServer` (+ `.Design`) | 10.0.11 | Persistence |
| `Microsoft.Extensions.Hosting` (+ `.WindowsServices`) | 10.0.11 | Generic Host |
| `Microsoft.Data.SqlClient` | 7.0.2 | Bulk copy / direct SQL |
| `FluentValidation` | 12.1.1 | Input validation |
| `BCrypt.Net-Next` | 4.2.0 | Password hashing |
| `Polly.Core` | 8.7.0 | Transfer retry + circuit breaker |
| `Microsoft.SqlServer.TransactSql.ScriptDom` | 180.107.0 | External query SQL parsing |
| `libplctag` (+ `.NativeImport`) | 1.5.2 | EtherNet/IP (Allen-Bradley) |
| `S7netplus` | 0.20.0 | Siemens S7 |
| `NModbus` | 3.0.83 | Modbus/TCP |
| `Serilog` (+ sinks/enrichers) | 4.3.0 / 10.0.0 | Structured logging |
| `CommunityToolkit.Mvvm` | 8.4.2 | MVVM source generators |
| `WPF-UI` (+ `.DependencyInjection`) | 4.3.0 | Fluent controls, NavigationView |
| `xunit` / `FluentAssertions` / `NSubstitute` | 2.9.3 / 7.2.0 / 5.3.0 | Tests |

Lock files (`packages.lock.json`) are enabled solution-wide for reproducible
restore.

### 2.4 Absent by design

| Not present | Why |
|---|---|
| Charting library | `ScadaTrendPlot` is hand-rendered (see `UI_STYLE_GUIDE.md` §14) |
| MediatR / AutoMapper / CQRS | Plain interfaces, manual mapping — the surface is small enough |
| Repository layer | Services use `IDbContextFactory<AppDbContext>` directly |
| ASP.NET / HTTP endpoints | Desktop + service only; no web tier |
| Health check framework | Heartbeat rows instead (§15) |
| File-based DB / LocalDB | Rejected at the connection-string validator |
| `.editorconfig`, `nuget.config` | Not present in this repo |

---

## 3. Dependency rules

```text
Domain  ◄──  Application  ◄──  Infrastructure  ◄──  Service
                          ◄──  UI
```

- **Domain has zero package references.** No EF, no logging, no Microsoft
  extensions. It compiles standalone.
- **Application** depends only on Domain plus `FluentValidation` and
  `Microsoft.Extensions.Options`. It declares interfaces; it never implements them.
- **Infrastructure** implements everything in Application and owns all I/O.
- **Service and UI are hosts.** They compose dependencies; neither contains
  business rules.
- `DependencyInjection.AddInfrastructure(...)` in
  `src/WTT.Infrastructure/DependencyInjection.cs` is the **single shared
  composition root**.

### 3.1 Who runs the pipeline

This distinction matters and is easy to get wrong:

| Host | Calls | Result |
|---|---|---|
| `WTT.Service\Program.cs` | `AddInfrastructure` + **`AddWttPipelineWorkers()`** | Capture, transfer and alarm-retention workers run |
| `WTT.UI\App.xaml.cs` | `AddWttLogging` + `AddInfrastructure` **only** | No pipeline workers — UI is read-only w.r.t. the pipeline |
| `CapturePipelineEndToEndTests` | `AddInfrastructure` + `AddWttPipelineWorkers()` | Tests drive the real pipeline |

`AddWttPipelineWorkers()` registers `DataCaptureBackgroundService`,
`DataTransferBackgroundService` and `AlarmRetentionWorker`. If the UI ever
registered them, you would get two competing pollers writing the same tables.

---

## 4. Domain layer

`src/WTT.Domain` — pure, package-free.

### 4.1 Base types (`Common/Entity.cs`)

```csharp
public abstract class Entity { public int Id { get; set; } }

public abstract class AuditableEntity : Entity
{
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}
```

Audit columns are stamped centrally by `AppDbContext.SaveChanges[Async]`
(`StampAuditFields()`), not by each service.

### 4.2 Entities

| Entity | Table | Notes |
|---|---|---|
| `Device` | `Devices` | **Aggregate root.** Owns addresses, tags, queries, trigger conditions and a 1:1 `DeviceServer`. |
| `DeviceAddress` | `DeviceAddresses` | Modbus sequential/discrete register block |
| `DeviceTag` | `DeviceTags` | PLC / Custom / External tag, unique per `(DeviceId, Name)` |
| `DeviceTriggerCondition` | `DeviceTriggerConditions` | One clause of a logical capture expression |
| `DeviceQuery` | `DeviceQueries` | External read-only SQL source |
| `DeviceQueryColumn` | `DeviceQueryColumns` | Discovered columns after Verify |
| `DeviceServer` | `DeviceServers` | 1:1 central transfer destination per device |
| `TagReading` | `TagReadings` | **The store-and-forward buffer** |
| `TransferBatch` | `TransferBatches` | One row per transfer attempt |
| `User` | `Users` | Accounts, BCrypt hashes, lockout state |
| `AuditLog` | `AuditLogs` | Security audit trail |
| `AppSetting` | `AppSettings` | Runtime settings (key/value, `Key` is the PK) |
| `ServiceRuntimeState` | `ServiceRuntimeState` | Heartbeat, exactly one row (`Id = 1`) |
| `DeviceRuntimeState` | `DeviceRuntimeStates` | Per-device connection/scan counters |
| `AlarmEvent` | `AlarmEvents` | ISA-18.2 alarm lifecycle |

#### `Device` — notable members

```csharp
string Name;                 // unique
ProtocolType Protocol;       // ModbusTcp | EthernetIp | SiemensS7
Manufacturer Manufacturer;
DeviceModel Model;
string Host; int Port;
int PollIntervalMs = 1000;   int TimeoutMs = 3000;
int Slot; int Rack;
int ConnectionSize = 511;    // Allen-Bradley CIP; 4002 = large forward open
string? CipPath;
byte UnitId = 1;             // Modbus slave id
WordOrder WordOrder = LowHigh;
bool OneBasedAddressing = true;   // Modbus 40001-style addressing
CaptureMode CaptureMode = Manual;
string? TriggerTagName;
bool IsCaptureEnabled; bool IsMonitorEnabled;
bool ExternalContextLoaded;      // rising-edge latch for external SQL context
bool IsEnabled = true;           // master switch — disabled devices are skipped
```

#### `DeviceTag` — the richest entity

```csharp
// Identity and source
int DeviceId; int? AddressId; string Name; string? Alias;
TagKind Kind;                 // Plc | Custom | External
TagDataType DataType; string PlcAddress;
ModbusRegisterType? RegisterType; int ElementCount = 1; int ByteCount;
bool IsSelected = true;       // include in polling
string? SetValue;             // Custom constant, External resolved value

// Scaling
double? ScaleFactor; double? ScaleOffset; int? DecimalPlaces; string? EngineeringUnit;

// Write safety (all deny-by-default)
bool IsWriteEnabled = false;
double? WriteMinimum; double? WriteMaximum;
int MinimumWriteIntervalMs = 1000;

// Live state (flushed by the capture worker)
string? CurrentValue; TagQuality CurrentQuality = Uncertain; DateTime? CurrentValueAtUtc;

// External binding
int? QueryId; int? QueryColumnId;

// Alarms
bool IsAlarmEnabled;
double? AlarmMinThreshold; double? AlarmMaxThreshold;
double AlarmDeadband = 2.0;
int AlarmOnDelaySeconds; int AlarmOffDelaySeconds;
AlarmPriority AlarmPriority = Medium; string? AlarmDescription;

string DisplayName => string.IsNullOrWhiteSpace(Alias) ? Name : Alias;   // blank alias also falls back; not mapped
```

### 4.3 Enums

Stored as **strings** in SQL for the device/tag enums (`HasConversion<string>()`)
so readable values appear in queries. Numeric where the value itself is part of a
contract.

| Enum | Values |
|---|---|
| `ProtocolType` | `ModbusTcp=1`, `EthernetIp=2`, `SiemensS7=3` |
| `Manufacturer` | `AllenBradley=1`, `Siemens=2`, `Yokogawa=3`, `Advantech=4`, `Walchem=5`, `Generic=99` |
| `DeviceModel` | ControlLogix 1, CompactLogix 2, Plc5 3, Slc500 4, MicroLogix 5, Micro800 6; S7_300 10, S7_400 11, S7_1200 12, S7_1500 13; Mw100 20, Gm10 21; Adam6017 30, Adam6018 31; Walchem600 40; GenericModbusTcp 90 |
| `TagKind` | `Plc=1`, `Custom=2`, `External=3` |
| `TagDataType` | Bool 1 … String 11 (Bool, Byte, Int16, UInt16, Int32, UInt32, Int64, UInt64, Float32, Float64, String) |
| `CaptureMode` | `Manual=1`, `PlcTrigger=2`, `Logical=3` |
| `ModbusRegisterType` | Coils 1, DiscreteInputs 2, InputRegisters 3, HoldingRegisters 4 |
| `ModbusAddressType` | Discrete 1, Sequential 2 |
| `WordOrder` | `LowHigh=1`, `HighLow=2` |
| `TriggerOperator` | Equal 1, NotEqual 2, GreaterThan 3, LessThan 4, GreaterOrEqual 5, LessOrEqual 6, OnChange 7 |
| `LogicalOperator` | None 0, And 1, Or 2 |
| `ExternalQueryType` | Contextual 1, Expression 2 |
| `UserRole` | `Viewer=0`, `Operator=1`, `Admin=2` |
| `TransferStatus` | Success 1, Failed 2, NothingToTransfer 3 |
| `TagQuality` | Good 0, Uncertain 1, Bad 2, NotConnected 3, ConfigError 4 |
| `AppTheme` | Light 0, Dark 1 |
| `AlarmPriority` | `Critical=1`, `High=2`, `Medium=3`, `Low=4` |
| `AlarmType` | `High=1`, `Low=2` |
| `AlarmState` | `Active=1`, `Acknowledged=2`, `Cleared=3`, `Shelved=4` |

### 4.4 Value objects (`ValueObjects/PlcValueObjects.cs`)

Records, no behaviour beyond convenience:

| Record | Purpose |
|---|---|
| `TagReadResult(TagId, TagName, RawValue, Quality, Error)` | Per-tag read outcome (`+ IsGood`, `Good(...)`, `Bad(...)` factories) |
| `TagWriteRequest(DeviceTag, string Value)` | Write request |
| `PlcWriteResult(RequestedValue, PreviousValue, ReadbackValue, CompletedAtUtc)` | Verified write result |
| `DiscoveredTag(Name, DataType?, ElementCount, ByteCount, IsStructure, TypeName?)` | Browse result |
| `PlcDriverCapabilities(CanBrowseTags, CanWrite, SupportsBatchRead, RequiresAddressBlocks)` | Capability flags |
| `DeviceCaptureStatus(...)`, `CaptureRuntimeSnapshot(...)` | Status projection |
| `LiveTagValue(DeviceId, TagId, TagName, Value, Quality, TimestampUtc)` | In-process live cache entry |
| `AlarmTriggeredMessage(...)`, `AlarmClearedMessage(...)` | Alarm bus messages |

### 4.5 Exception hierarchy (`Exceptions/WttExceptions.cs`)

```text
WttException (abstract)
├── PlcConnectionException     // transport lost — carries DeviceName, message prefixed "[device]"
├── PlcReadException
├── PlcWriteException
├── PlcConfigurationException  // bad address/port/driver combo — idles the worker 30 s
├── AuthenticationException
├── AuthorizationException
├── NotFoundException(entity, key)
├── DataTransferException
├── ExternalQueryException
├── BusinessRuleException      // safe, user-facing message
└── HistorianException         // safe, trend-query message
```

`WttException` is the contract for "the UI may show this message directly".
Anything else becomes a generic error.

### 4.6 Driver abstraction (`Interfaces/IPlcDriver.cs`)

```csharp
public interface IPlcDriver : IAsyncDisposable
{
    Device Device { get; }
    PlcDriverCapabilities Capabilities { get; }
    bool IsConnected { get; }
    Task ConnectAsync(CancellationToken ct);
    Task DisconnectAsync();

    // "Never throws for individual tag failures… Throws PlcConnectionException
    //  only when the transport is lost."
    Task<IReadOnlyList<TagReadResult>> ReadAsync(IReadOnlyList<DeviceTag> tags, CancellationToken ct);
    Task WriteAsync(TagWriteRequest request, CancellationToken ct);
    Task<IReadOnlyList<DiscoveredTag>> BrowseTagsAsync(CancellationToken ct);
}

public interface IPlcDriverFactory { IPlcDriver Create(Device device); }
```

The per-tag-doesn't-throw contract is what lets one bad address produce
`TagQuality.ConfigError` on that row while the rest of the scan succeeds.
(`WriteAsync` returns no value; the verified `PlcWriteResult` with
previous/read-back values is produced one layer up, by
`IPlcOperationsService.WriteTagAsync`.)

### 4.7 Domain services

**`Common/TagValueFormatter.cs`** (static) — the single place scaling happens:

```csharp
engineering = raw * ScaleFactor + ScaleOffset
```

- rounding `Math.Clamp(decimals, 0, 15)` with `AwayFromZero`
- booleans render as `"1"` / `"0"`
- arrays joined with `,`
- **invariant culture** everywhere (no locale-dependent decimals in a historian)
- `ParseForWrite(TagDataType, string)` — bool accepts `1/true/True/TRUE/on/ON`
- `ModbusRegisterCount(type, count)` — 1 / 2 / 4 registers, string = `(count+1)/2`

Drivers return **raw unscaled** values; formatting is applied once, centrally.

### 4.8 Domain events — none

There is no domain-event dispatcher. Cross-process signalling goes through the
database (`ServiceRuntimeState`, `DeviceRuntimeStates`, `AlarmEvents`) and
in-process C# events (`TagValueChanged`, `BatchCompleted`, `AlarmTriggered`) stay
in-process. This avoids inventing a delivery mechanism with no second process to
deliver to.

---

## 5. Application layer

`src/WTT.Application` — contracts only, no implementations.

### 5.1 Service interfaces

**Configuration** (`Interfaces/IConfigurationServices.cs`):

| Interface | Notable members |
|---|---|
| `IDeviceService` | `CreateAsync(device, seedTags)`, `SetRunStateAsync`, `SetCaptureModeAsync` |
| `IDeviceAddressService` | `GenerateTagsAsync` |
| `ITagService` | `SetSelectedAsync`, `SetCustomValueAsync`, `ImportDiscoveredAsync` |
| `ITagProvisioningService` | `ProvisionAsync(device, seedTags)` |
| `ITriggerService` | Condition CRUD with auto-ordering |
| `IDeviceServerService` | `TestConnectionAsync` |
| `IExternalQueryService` | `VerifyAsync`, `RefreshExternalTagValuesAsync` |
| `IExternalSqlPolicy` | `DemandSelectOnly(sql)` |
| `IPlcOperationsService` | `TestConnectionAsync`, `BrowseTagsAsync`, `WriteTagAsync`, `ReadTagOnceAsync` |
| `IAppSettingsService` | Key/value runtime settings |

**Runtime** (`Interfaces/IRuntimeServices.cs`):

| Interface | Notable members |
|---|---|
| `IInstallationIdentity` | `Guid SourceId` — the idempotency key for transfer |
| `ICaptureRuntimeStatusReader/Writer` | Heartbeat and per-device status |
| `IDataCaptureController` | `Pause`, `Resume`, `IsPaused`, `RequestReconcile`, `GetStatuses`, `GetLiveValues`, `GetStatisticsAsync`, events `TagValueChanged` / `DeviceStatusChanged` |
| `IDataTransferController` | `Pause`, `Resume`, `TransferNowAsync`, `GetStatisticsAsync`, `GetRecentBatchesAsync`, event `BatchCompleted` |
| `ILogReaderService` | Log lines for the Logs page |
| `IRuntimeSettings` | Cached `UseMockPlc`, `SessionTimeoutMinutes`, `TransferredRetentionDays`, `TransferIntervalSeconds`; `ReloadAsync`; event `Changed` |

**Other:** `IHistorianService` (trend queries), `IAuthService`,
`IInitialAdminEnrollmentService`, `ICurrentUserService`, `IUserService`,
`IPasswordHasher`, `ISecureStorage`, `IAuditService`, `IAlarmService`,
`IAlarmNotifier`.

### 5.2 Setting keys

```csharp
public static class AppSettingKeys
{
    public const string Theme = "ui.theme";
    public const string UseMockPlc = "simulation.useMockPlc";
    public const string SessionTimeoutMinutes = "security.sessionTimeoutMinutes";
    public const string TransferredRetentionDays = "capture.transferredRetentionDays";
    public const string TransferIntervalSeconds = "transfer.intervalSeconds";
}
```

Plus two written by the alarm engine at runtime: `Alarm.RetentionDays`
(read back from `AppSettings` on every get) and `Alarm.BeepEnabled`
(`"1"`/`"0"`, written to `AppSettings` on change but **not** re-read —
`GetBeepEnabledAsync` returns the in-memory option, so a restarted process
falls back to the configured default until the checkbox is toggled again).

`RuntimeSettings.ReloadAsync` clamps: session **1–720**, retention **1–3650**,
transfer interval **1–3600**.

### 5.3 Permission model (`Security/Permissions.cs`)

```csharp
public enum Permission
{
    ViewDashboard, ViewLiveData, ViewDevices, ManageDevices, ManageTags,
    ControlCapture, WriteToPlc, ViewTransfer, ManageTransfer, ManageUsers,
    ManageSettings, ViewLogs, AcknowledgeAlarm
}
```

| Role | Permissions |
|---|---|
| **Viewer** | `ViewDashboard`, `ViewLiveData`, `ViewDevices`, `ViewTransfer` |
| **Operator** | Viewer ∪ `ControlCapture`, `ViewLogs`, `AcknowledgeAlarm` |
| **Admin** | all 13 |

`ICurrentUserService.Demand(Permission)` throws `AuthorizationException`.
Enforcement happens in **both** processes — hiding a button is never the only
protection.

### 5.4 Validators (FluentValidation)

| Validator | Key rules |
|---|---|
| `DeviceValidator` | name `^[A-Za-z0-9_\-\. ]+$` ≤100; host IP/hostname; `Port 1..65535`; **`PollIntervalMs 100..3_600_000`**; **`TimeoutMs 200..60_000`**; `Slot 0..31`; `Rack 0..7`; `ConnectionSize ∈ {511, 4002, 4000, 4064}`; model must match protocol+manufacturer; `TriggerTagName` required in `PlcTrigger` mode |
| `DeviceTagValidator` | name ≤200, no `'` or `,`; `ElementCount 1..4096`; `DecimalPlaces 0..10`; `MinimumWriteIntervalMs 250..60000`; `WriteMinimum ≤ WriteMaximum`; `PlcAddress` required for PLC; `QueryId`/`QueryColumnId` required for External; deadband `0..50`; min < max; ≥1 threshold |
| `DeviceAddressValidator` | `StartAddress 0..65535`; `ModuleOffset ±65535`; Sequential ⇒ `PointCount 1..2000` + `TagPrefix` |
| `DeviceServerValidator` | table name `^[A-Za-z_][A-Za-z0-9_]*$` |
| `DeviceQueryValidator` | `QueryText` ≤8000 |
| `PasswordPolicyValidator` | min length, mixed case, digit |
| `CreateUserRequestValidator` | username 3..50, `^[A-Za-z0-9_.\-]+$` |
| `LoginRequestValidator` | non-empty username/password |

Options are validated at startup with `ValidateOnStart()` (§6.7).

### 5.5 Trigger evaluation (`Triggers/TriggerEvaluator.cs`)

```csharp
Evaluate(conditions, values)
```

- **Strictly left-to-right** — `((A AND B) OR C)`, no operator precedence. This
  preserves legacy semantics and is asserted by tests.
- Numeric comparison when both sides parse (equality epsilon `1e-9`), otherwise
  ordinal string comparison. Never lexicographic on numbers.
- `OnChange` returns `false` when there is no previous value.
- Missing tag ⇒ `false`; empty condition list ⇒ `false` — **fail safe to
  no-capture**, never to flooding the historian.
- `Describe(...)` renders the expression for the UI.
- `IsTrue(value)` interprets PLC trigger booleans: bool parse, numeric ≠ 0,
  `"on"` (case-insensitive — `ON` and `On` count too).

### 5.6 Catalogs

**`PlcModelCatalog.cs`** — model → protocol, default port, capability flags:

| Family | Port | Models |
|---|---|---|
| Allen-Bradley EtherNet/IP | **44818** | ControlLogix, CompactLogix, Micro800, PLC-5, SLC 500, MicroLogix — only ControlLogix/CompactLogix `SupportsBrowse` |
| Siemens S7 | **102** | S7-300/400/1200/1500, `UsesSlot` + `UsesRack` |
| Modbus TCP | **502** | MW100, GM10, ADAM-6017/6018, Walchem W600, Generic Modbus — all `UsesAddressBlocks`, `UsesUnitId`, `UsesWordOrder` |

Also `SequentialDefaults(model)` (Mw100 → Float32/1-based/2 dp; Gm10 →
Float32/1-based/1 dp; ADAM → UInt16/0-based; Walchem → Float32/1-based) and
`Gm10ChannelRegisterOffset(channel)` = `bank*100 + inBank*2` (banks of 10
channels, 100 registers apart).

**`TagTemplateLibrary.cs`** — seeding templates when browsing is unavailable:
Logix starter (`Speed/Torque/Running/Fault`), PLC-5 (`N7_0`, `N7_1`, `F8_0`,
`B3_0`), Siemens S7 (`DB1.DBD0`, `DB1.DBD4`, `M0.0`, `M0.1`), MW100
(`CH001..CH020`, 2 registers apart), GM10 (`CH001..CH010` with bank offsets),
ADAM-6017 (`AI_CH000..007`, mA), ADAM-6018 (`TC_CH000..007`, °C), Walchem W600
(`pH`=reg 1, `Conductivity`=3, `Temperature`=5, `ORP`=7, `Flow`=9, `Level`=11),
Generic Modbus (`HR_001..HR_004`).

---

## 6. Infrastructure layer

`src/WTT.Infrastructure` — everything that touches I/O.

### 6.1 Persistence

**`AppDbContext`** — 15 `DbSet`s:

```csharp
Devices, DeviceAddresses, DeviceTags, TagReadings, DeviceServers,
DeviceQueries, DeviceQueryColumns, DeviceTriggerConditions, Users,
TransferBatches, AuditLogs, AppSettings, ServiceRuntimeStates,
DeviceRuntimeStates, AlarmEvents
```

- `OnModelCreating` → `ApplyConfigurationsFromAssembly`.
- `SaveChanges[Async]` overrides stamp `CreatedAtUtc`/`UpdatedAtUtc`.

**Why `IDbContextFactory` and no repositories:** each operation creates a
short-lived context with `AsNoTracking` for reads and `ExecuteUpdateAsync` /
`ExecuteDeleteAsync` for set-based writes. A repository layer would have to
expose all of that anyway. Everything is registered as a **singleton**; there is
no scoped lifetime because there is no request scope.

**Connection** (`SqlServerConnectionStringFactory`) **enforces**:

- Windows Integrated Security — **no stored database password, ever**
- `Encrypt=True`
- `AttachDbFilename` and LocalDB rejected
- `PersistSecurityInfo=false`, `MARS=false`, `Pooling=true`, `ApplicationName="WTT"`

Used as an options validator with `ValidateOnStart()`, so a bad connection string
fails at startup rather than at first query.

**EF retry policy:**

```csharp
sql.EnableRetryOnFailure(5, TimeSpan.FromSeconds(5), null);
sql.CommandTimeout(30);
sql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
```

`EnableSensitiveDataLogging(false)` — connection strings and values never reach
the log.

### 6.2 Migrations

Six migrations, all generated, no `migrationBuilder.Sql()` anywhere:

| Migration | Date | Purpose |
|---|---|---|
| `20260911192001_InitialSqlServer` | 2026-09-11 | 12 tables + 16 indexes |
| `20260911201222_ProductionSecurityHardening` | 2026-09-11 | `IsWriteEnabled` (default `false`), `MinimumWriteIntervalMs` (1000), `WriteMaximum`, `WriteMinimum` |
| `20260913105214_AddRuntimeStatus` | 2026-09-13 | `ServiceRuntimeState`, `DeviceRuntimeStates` |
| `20260922183000_AddAlarmThresholdSystem` | 2026-09-22 | 8 alarm columns on `DeviceTags`, `AlarmEvents` + 3 indexes |
| `20260924092300_OptimizeAlarmIndexes` | 2026-09-24 | Drop `IX_AlarmEvents_Priority_State`, add the three named alarm indexes |
| `20260927090000_AddHistorianTrendIndex` | 2026-09-27 | `IX_TagReadings_HistorianTrend` |

**No stored procedures, functions, views or triggers.** Verified: zero
`CREATE PROC|FUNCTION|VIEW|TRIGGER` matches in `src`.

`DatabaseInitializer`:
- `InitializeAsync()` → `Database.MigrateAsync()` (UI path)
- `VerifySchemaCurrentAsync()` → throws `"Database upgrade required: …"` if
  migrations are pending, returns `db.Users.AnyAsync()`

The **service does not migrate**. `SchemaVerificationHostedService` calls
`VerifySchemaCurrentAsync()` and refuses to start on a pending schema. The UI
migrates and uses the `hasUsers` result to decide whether to show first-admin
enrollment. This split means an unattended service never silently alters schema.

`DesignTimeDbContextFactory` reads `WTT_DESIGN_CONNECTION`, falling back to
`Server=.;Database=WTT;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;Application Name=WTT EF Design`.

### 6.3 PLC drivers

```csharp
// PlcDriverFactory.Create(Device) — singleton
if (_runtime.UseMockPlc) return new MockPlcDriver(device);

return device.Protocol switch
{
    ProtocolType.ModbusTcp  => new ModbusTcpDriver(...),
    ProtocolType.SiemensS7  => new SiemensS7Driver(...),
    ProtocolType.EthernetIp when device.Manufacturer == Manufacturer.AllenBradley
                               => new AllenBradleyDriver(...),
    _ => throw new PlcConfigurationException(
            $"No driver available for device '{device.Name}' ...")
};
```

**The header rule:** an unknown combination **throws**. It never falls back to
the mock. A misconfigured device silently producing simulated data would be a
data-integrity incident.

#### Allen-Bradley (`AllenBradley/`)

- libplctag; ControlLogix, CompactLogix, Micro800, PLC-5, SLC-500, MicroLogix.
- Port **must** be 44818, otherwise `PlcConfigurationException`.
- TCP probe before the first tag read.
- `AllowPacking = true` — libplctag packs CIP packets, replacing legacy manual
  `CIPConnectionSize - 53` batching.
- `UseConnectedMessaging = true` only for ControlLogix/CompactLogix with `ConnectionSize >= 4000` (large forward open, 4002 bytes).
- **"If EVERY attempted read times out the link is dead"** ⇒
  `PlcConnectionException` (drives backoff), while individual tag failures stay
  per-tag.
- `CanBrowseTags` only for ControlLogix/CompactLogix: walks `@tags` and
  `Program:*.@tags`, expands arrays ≤256 elements, maps type codes
  `0xC1..0xCB`, Logix STRING is an 88-byte structure.
- `AllenBradleyAddress.Parse` accepts `N7:0`, `F8:3`, `B3:0/4`, `ST10:2`,
  `A9:0-10` (inclusive range ⇒ `ElementCount = 11`), `T4:0.ACC`; Logix symbolic
  names pass through; malformed input throws `FormatException` (⇒ per-tag
  `ConfigError`).
- Bit writes use read-modify-write `SetBit`.

#### Siemens S7 (`Siemens/`)

- S7.Net; S7-300/400/1200/1500, port 102, rack/slot from the device.

```csharp
private const int MaxItemsPerRequest = 19;
private const int MaxPayloadPerRequest = 200; // bytes (PDU 240 minus headers)
```

- `BuildDataItem` forces `VarType` from `TagDataType` (Bit/Byte/Int/Word/DInt/
  DWord/Real/LReal/S7String).
- **Int64/UInt64 rejected**: *"S7 64-bit integer tags are not supported. Use
  Float64/LREAL or split the value into two 32-bit words."*
- Chunked `ReadMultipleVarsAsync`; on a non-link `PlcException` it **retries
  item-by-item** so one bad address does not poison the whole chunk.
- `BrowseTagsAsync` throws `NotSupportedException` — *"S7 controllers do not
  expose a symbol table over S7comm. Enter DB addresses manually."*

#### Modbus/TCP (`Modbus/`)

- NModbus; Yokogawa MW100/GM10, ADAM, Walchem, generic slaves.

```csharp
MaxRegistersPerRequest = 120;   // protocol max 125
MaxCoilsPerRequest    = 1900;   // protocol max 2000
MaxGapRegisters       = 8;      // blocks ≤8 unused registers apart are merged
```

- Address base is **per device** (`Device.OneBasedAddressing`) — the same
  address string means different registers on different devices.
- Tags grouped per register type → contiguous blocks → one read per block.
- `SlaveException` = data problem: that block gets bad values, the scan
  continues. Socket/IO/timeout ⇒ `PlcConnectionException`.
- Writes only to Coils and HoldingRegisters, ≤123 registers, input tables
  rejected with `"{type} are read-only."`.
- `Transport.Retries = 1`. `RequiresAddressBlocks = true`.
- `BrowseTagsAsync` throws.

**`ModbusCodec.cs`** — static, pure, no I/O:

- `RegisterCount(type, count)` — 1/2/4, string = `(n+1)/2`
- big-endian within a register; `WordOrder` determines register sequence for
  32/64-bit
- strings = ASCII, one register per 2 characters, NUL-truncated
- returns **raw unscaled** values
- `ModbusAddressParser.Parse(address, fallback, oneBased)` accepts prefixes
  `HR/H/4X/4`, `IR/I/3X/3`, `CO/C/0X/0`, `DI/D/1X/1`, applies
  `oneBased ? n-1 : n`, range-checks 0..65535, unknown prefix ⇒
  `FormatException` ⇒ per-tag `ConfigError`

#### Mock PLC (`MockPlcDriver.cs`)

Deterministic — **no `Random`, no `DateTime.Now`**:

- signal = f(stable hash of `PlcAddress`) → phase, period (20–59 s), amplitude,
  plus name keywords: `temp` 65, `press` 4.2, `level` 50, else 100; `trig`
  toggles every 30 s; `batch` → `BATCH-yyyymmdd-NNN`
- shared cross-process state in `C:\ProgramData\WTT\WTT_MockPlcState.json`
  (fallback `%LocalAppData%\WTT\...`, then temp), guarded by named mutex
  `Global\WTT_MockPlcStateMutex` (500 ms wait, 3 write retries,
  `AbandonedMutexException` tolerated)
- writes expire **15 s** after the write (`"{value}|{expiryTicks}"`)
- `Capabilities = (CanBrowseTags:true, CanWrite:true, SupportsBatchRead:true,
  RequiresAddressBlocks:false)`; `BrowseTagsAsync` returns a fixed 10-entry sample

The mock is what makes the whole acceptance test plan runnable without hardware.

### 6.4 Capture pipeline

#### `DataCaptureBackgroundService` — singleton supervisor

- Loop every `Capture:ReconcileIntervalSeconds` (**5 s**): reload
  `IRuntimeSettings`, reconcile workers, publish heartbeat.
- A bounded `_wakeup` channel (**capacity 1, `DropWrite`**) coalesces
  configuration changes — 20 rapid edits still cause one reconcile.
- Worker selection: `d.IsEnabled && (d.IsCaptureEnabled || d.IsMonitorEnabled)`.
- **Restart on fingerprint change.** `Fingerprint()` is a SHA-256 over:
  - device id, name, `UpdatedAtUtc`, host, port, poll, timeout, protocol, model,
    slot, rack, cipPath, connectionSize, unitId, wordOrder, oneBased, captureMode,
    triggerTagName, capture+monitor flags, mock flag
  - **every tag**: id, name, kind, dataType, address, registerType, elementCount,
    selected, scale, decimals, the block's register type and decimals
    (`Address?.RegisterType`, `Address?.DecimalPlaces`), `SetValue` (Custom only),
    queryColumnId, and the alarm fields except the description (`IsAlarmEnabled`,
    min/max threshold, deadband, on/off-delay, priority — `AlarmDescription` is
    display text and does not restart the worker)
  - all trigger conditions

  Editing a tag therefore restarts exactly that device's worker, and only that one.
- `Pause`/`Resume` demand `Permission.ControlCapture`; `GetStatuses`/`GetLiveValues`/
  `GetStatisticsAsync` demand `ViewDashboard`/`ViewLiveData`.

#### `DeviceCaptureWorker` — per-device poller

Created by the supervisor, **never from DI** (so it can be created/disposed freely).

| Aspect | Behaviour |
|---|---|
| Poll period | `Math.Max(CaptureOptions.MinPollIntervalMs, device.PollIntervalMs)` |
| Reconnect backoff | `min(60, 2 * 2^min(attempt-1,10))` + 0–20 % jitter → **2 s → 60 s** |
| Config error | idle **30 s**, mark all tags `ConfigError` |
| Other failure | mark `NotConnected` |
| Log throttling | attempts 1, 5, 20, then every 100 |

Each scan:

1. read set = selected PLC tags ∪ trigger tag ∪ PLC tags referenced by logical conditions
2. `driver.ReadAsync`
3. `TagValueFormatter.Format` (scaling, invariant culture)
4. publish to `LiveValueCache`
5. alarm evaluation — for non-bool/string numeric tags with `IsAlarmEnabled`
6. trigger evaluation
7. external context refresh **only on rising edge**, latched by
   `Devices.ExternalContextLoaded` via `ExecuteUpdateAsync`
8. historise if `IsCaptureEnabled && triggered` — rows share one `ScanId`,
   `Value` truncated to 500 chars, `AutoDetectChangesEnabled = false`
9. retain previous values for `OnChange`

Live values flush to `DeviceTags.CurrentValue/CurrentQuality/CurrentValueAtUtc`
at most every **`Capture:CurrentValueFlushSeconds` (5 s)**, inside the EF
retrying strategy with a transaction, re-queuing the snapshot on failure.

Status publishes every 20 scans (`_scanCount % 20 == 1`).

#### `LiveValueCache`

Singleton, lock-free nested `ConcurrentDictionary<deviceId, tagId, LiveTagValue>`.
Explicitly documented as **in-process only** — the UI process sees nothing in it.
Cross-process live values travel through `DeviceTags.CurrentValue` and
`DeviceRuntimeStates` in SQL.

#### Retention (inside the capture supervisor)

Runs at most once per **60 minutes**:

- delete `TagReadings` where `IsTransferred && TimestampUtc < now - TransferredRetentionDays`
- if `UntransferredMaxAgeDays > 0`, purge never-transferred rows older than that
  **with a warning** (0 = disabled, the default)
- hard-coded: `TransferBatches` older than **30 days**, `AuditLogs` older than
  **365 days**

**Never silently deletes untransferred readings.** That is the product's
central data-safety rule.

### 6.5 Data transfer — `DataTransferBackgroundService`

Interval = **`Transfer:IntervalSeconds`** (runtime override `transfer.intervalSeconds`,
clamped 1–3600).

**Cycle control:** `_cycleLock` — `WaitAsync(0)` for an unattended cycle (skip
quietly) or `10_000 ms` for a manual `TransferNow` (otherwise
`DataTransferException("A transfer is already running…")`). The queue holds
enabled `DeviceServer`s; a device re-enters only if it moved a full `BatchSize`
and `batches + 1 < MaxBatchesPerDevicePerCycle`.

**Per batch, inside a Polly v8 `ResiliencePipeline` keyed `"{ServerName}|{DatabaseName}"`:**

1. Open `SqlConnection` (connect timeout 15 s), then
   `SET QUOTED_IDENTIFIER ON; SET ARITHABORT ON;` — required by the filtered
   unique index.
2. `EnsureDestinationSchemaAsync` — **at most once per hour per server/table**:
   - create the legacy-compatible table if missing
   - add `Quality`, `ScanId`, `SourceId`, `SourceNode`, `SourceReadingId`, `TimeStampUTC`
   - widen `PLCName`/`TagName`/`TagValue`
   - create `UX_{table}_SourceId` (filtered unique on `(SourceId, SourceReadingId)`)
   - create `IX_{table}_PLC_Time` (`(PLCName, TimeStamp) INCLUDE (TagName, TagValue)`)
3. temp table `#WTTBatch` → `SqlBulkCopy` (`BulkCopyBatchSize`,
   `BulkCopyTimeoutSeconds`) →
   ```sql
   INSERT … SELECT … WHERE NOT EXISTS (… WITH (UPDLOCK, HOLDLOCK))
   OPTION (LOOP JOIN, MAXDOP 1)
   ```
   → commit.
4. **Only after remote commit:** `ExecuteUpdateAsync` marks the **contiguous id
   range** `IsTransferred = true`, updates
   `DeviceServers.LastSuccessAtUtc/LastError`, inserts a `TransferBatch`
   (Success), raises `BatchCompleted`.
5. On failure: `RecordFailureAsync` writes `LastError` (≤4000) and a `TransferBatch`
   (Failed).

**Idempotency keys:**

| Column | Source |
|---|---|
| `SourceId` | `IInstallationIdentity.SourceId` — file `C:\ProgramData\WTT\installation-id`, created atomically |
| `SourceNode` | `Environment.MachineName` |
| `SourceReadingId` | `TagReading.Id` |

The filtered unique index `UX_{table}_SourceId` makes retries safe: a duplicate
insert is a no-op, not a duplicate row.

**Local rows are marked only after the remote commit.** A crash between commit
and mark causes a re-send, which the index absorbs. The reverse order would lose
data.

### 6.6 Services of note

**`AuthService`** — login: validate → normalized lookup → disabled check →
lockout check → BCrypt verify.

- **Timing-attack mitigation:** for an unknown user it still runs
  `_hasher.Verify(request.Password, "$2a$12$C6UzMDM.H6dfI/f/IKcEeO5bZ0sQvYq0gJ2OaI6IhZ9Yz3X0f9G2u")`
  so response time does not reveal existence.
- Always returns the same `"Invalid username or password."`
- Lockout: `FailedLoginCount >= MaxFailedLogins` →
  `LockoutEndUtc = now + LockoutMinutes`, counter resets
- Every attempt is audited
- `ChangePasswordAsync` requires the current password, forbids reuse, clears
  `MustChangePassword`

**`UserService`** — CRUD under `Permission.ManageUsers`;
`EnsureNotLastActiveAdminAsync` ⇒ *"At least one active administrator must
remain"*; `Unlock`; audits every mutation.

**`InitialAdminEnrollmentService`** — `IsRequiredAsync` =
`!db.Users.AnyAsync()`; `EnrollAsync` runs inside the execution strategy at
`IsolationLevel.Serializable`, creating the first `Admin` with
`MustChangePassword = false`.

**`Security/SecurityServices.cs`**

| Service | Detail |
|---|---|
| `BcryptPasswordHasher` | `workFactor = Math.Clamp(BcryptWorkFactor, 10, 15)`, default 12 |
| `DpapiSecureStorage` | prefix `dpapi:v1:`, entropy `WTT.SecureStorage.v1`, `DataProtectionScope.LocalMachine`; decrypt failure ⇒ `BusinessRuleException("A stored password could not be decrypted on this machine…")` |
| `CurrentUserService` | process-wide holder; `Demand` throws `AuthorizationException` |

**`HistorianService`** — trend queries:
- keyset pagination `PageSize = 5000`
- `MaxParsedTotal = 100_000` with global stride
- **gap-aware normalized-LTTB downsampling**: good runs get proportional budgets,
  each bad-quality run contributes one marker so the chart **breaks the line**
  instead of interpolating across a communications outage
- `maxPoints >= 10`; failures wrap in `HistorianException`

**`ExternalQueryService`** — save requires `ManageTags`, runs
`_sqlPolicy.DemandSelectOnly`, DPAPI-protects passwords; `VerifyAsync` discovers
columns and syncs `DeviceQueryColumns` preserving ids; execution uses connect
timeout 10 s, `CommandTimeout = 30`, `CommandBehavior.SingleRow`, invariant
formatting.

**`ExternalSqlSelectPolicy`** — parses with
`TSql180Parser(initialQuotedIdentifiers: true)`; requires **exactly one batch,
one statement, `SelectStatement`**; rejects parse errors and `SELECT INTO`.
Stacked statements like `SELECT 1; DROP TABLE …` are rejected at parse time.

**`PlcOperationsService`** — ad-hoc test/browse/write over a short-lived
connection. Write path:

```
Demand(WriteToPlc)
  → only TagKind.Plc
  → IsWriteEnabled must be true
  → ElementCount == 1
  → parse + min/max clamp
  → per-tag SemaphoreSlim + MinimumWriteIntervalMs throttle
  → read-before → write → read-back verification
  → audit WriteToPlc with Old / Requested / Readback
```

Read-back comparison: ordinal for strings/bools, float tolerance
`max(1e-6, |expected| * 1e-5)` for numerics.

**`DeviceService`** — CRUD under `ManageDevices` + `DeviceValidator`;
`CreateAsync(device, seedTags)` provisions tags after save; rename rewrites
`TagReadings.DeviceName` for **untransferred** rows; delete is blocked while
untransferred readings exist; `Normalize` stamps protocol/manufacturer/default
port from the catalog and coerces `ConnectionSize 4000/4064 → 4002`; every
mutation calls `_capture.RequestReconcile()`.

**`TagService`** — CRUD under `ManageTags`; non-PLC tags get `IsWriteEnabled = false`
and cleared bounds; `ByteCount` auto-computed; delete blocked if the tag is the
PLC trigger tag or referenced by a logical condition; `SetCustomValueAsync`
requires `WriteToPlc`.

**`DeviceAddressService`** — Modbus blocks only; generated names are
`$"{TagPrefix}{index:000}"`, address =
`StartAddress + ModuleOffset + (Gm10 ? bankOffset : i * regsPerPoint)`, with
clash detection against other blocks.

**`TagProvisioningService`** — strategy: browse (ControlLogix/CompactLogix only,
**15 s `BrowseTimeout`**) → fallback to `TagTemplateLibrary`; checks
`HasPermission(ManageTags)` (warns and returns 0 rather than throwing); idempotent (skips existing names); failures log a warning and
**never delete the device**.

**`DeviceServerService`** — CRUD under `ManageTransfer`; password required on
create unless integrated security; `TestConnectionAsync` opens a connection
(10 s) and runs `SELECT @@VERSION`.

**`TriggerService`** — condition CRUD under `ManageDevices`, auto-ordering
(first clause `Conjunction = None`), renumber on delete, `ReorderAsync`.

**`AppSettingsService` / `RuntimeSettings`** — DB-backed settings; `SetAsync`
demands `ManageSettings`; `ReloadAsync` clamps and fires `Changed`.

### 6.7 DI registration (`DependencyInjection.cs`)

Options bound + `ValidateOnStart()`:

| Options | Validation |
|---|---|
| `DatabaseOptions` | valid SQL, Windows auth |
| `CaptureOptions` | `ReconcileIntervalSeconds >= 1`, `MinPollIntervalMs >= 50`, `TransferredRetentionDays >= 1` |
| `TransferOptions` | `IntervalSeconds >= 1`, `BatchSize 100..100_000`, `BulkCopyBatchSize >= 100`, `MaxBatchesPerDevicePerCycle 1..100` |
| `SecurityOptions` | `SessionTimeoutMinutes >= 1`, `MaxFailedLogins >= 1`, `MinPasswordLength >= 6`, `BcryptWorkFactor 10..15` |
| `AlarmOptions` | `RetentionDays 0..3650` |

Registered as **singletons**: `DatabaseInitializer`; security
(`IPasswordHasher`, `ISecureStorage`, `ICurrentUserService`, `IAuditService`,
`IAuthService`, `IInitialAdminEnrollmentService`, `IUserService`); all
configuration services; alarms (`InAppAlarmNotifier`, `EmailAlarmNotifier`,
`IAlarmService → AlarmEvaluationService`,
`IAlarmNotifier → CompositeAlarmNotifier(InApp, Email)`); PLC
(`IPlcDriverFactory`, `IPlcOperationsService`, `LiveValueCache`,
`SqlCaptureRuntimeStatusService` as reader *and* writer); pipelines
(`DataCaptureBackgroundService` → `IDataCaptureController`,
`DataTransferBackgroundService` → `IDataTransferController`).

**Circular-dependency workaround** — the alarm service needs the notifier, the
notifier is built from services; wiring happens through a hosted-service factory:

```csharp
services.AddSingleton<IHostedService>(sp =>
{
    var alarm = sp.GetRequiredService<IAlarmService>() as AlarmEvaluationService;
    var notifier = sp.GetRequiredService<IAlarmNotifier>();
    if (alarm != null) alarm.Notifier = notifier;
    return new NoOpHostedService();
});
```

`AddWttPipelineWorkers()` registers `DataCaptureBackgroundService`,
`DataTransferBackgroundService`, `AlarmRetentionWorker`.

---

## 7. Service process

`src/WTT.Service/Program.cs` — top-level statements, 63 lines.

1. **Bootstrap Serilog** → `C:\ProgramData\WTT\Logs\bootstrap-.log`, rolling
   daily, `retainedFileCountLimit 7`.
2. **Single-instance guard:**
   `new Mutex(true, "Global\\WTT.PipelineService", out var ownsPipeline)` →
   *"Another WTT pipeline service instance is already running."*
3. `Host.CreateDefaultBuilder(args)` →
   `.UseWindowsService(o => o.ServiceName = "WTTData")` →
   `.UseContentRoot(AppContext.BaseDirectory)`
4. **Config sources cleared, then re-added:** `appsettings.json` (required, no
   reload) → `appsettings.Service.json` (optional) → env vars prefixed `WTT_`.
   Because `cfg.Sources.Clear()` also removes the default command-line source,
   **there are no functional command-line switches.**
5. `.UseSerilog((ctx, services, cfg) => cfg.ReadFrom.Configuration(...).ReadFrom.Services(...).Enrich.FromLogContext())`
6. `AddInfrastructure(...)` + `AddHostedService<SchemaVerificationHostedService>()` +
   `AddWttPipelineWorkers()`
7. `await host.RunAsync()`; `Log.Fatal` on startup failure;
   `Log.CloseAndFlushAsync()` in `finally`

**`SchemaVerificationHostedService`** — `StartAsync` →
`DatabaseInitializer.VerifySchemaCurrentAsync()`. It **verifies, it does not
migrate**, and throws if migrations are pending.

### 7.1 Hosted services in the service process

| Hosted service | Schedule |
|---|---|
| `NoOpHostedService` (notifier wiring only) | no-op |
| `SchemaVerificationHostedService` | once, at startup |
| `DataCaptureBackgroundService` | reconcile every **5 s** + wakeup channel; retention ≤ every 60 min |
| `DataTransferBackgroundService` | every **5 s** (runtime-overridable 1–3600 s) |
| `AlarmRetentionWorker` | startup + every **24 h** |

### 7.2 Windows service registration

From `deploy\Install-WTT.ps1`:

```text
sc.exe create WTTData
  binPath=  "<root>\WTT.Service.exe"
  start=    delayed-auto
  obj=      "NT SERVICE\WTTData"
  DisplayName= "WTT Industrial Data Service"
  depend=   MSSQLSERVER

sc.exe failure WTTData reset= 86400 actions= restart/5000/restart/15000/restart/60000
```

Description: *"Unattended PLC capture and SQL Server transfer service."*
Delayed start prevents racing SQL Server on boot; the virtual account is not
LocalSystem.

---

## 8. UI process

`src/WTT.UI` — detailed in `docs/UI_STYLE_GUIDE.md` for visuals. Structure:

### 8.1 Composition

`App` constructor builds a single `IHost` with `Host.CreateDefaultBuilder()`.
Config order: `appsettings.json` (required) →
`appsettings.{WTT_ENVIRONMENT|Production}.json` (optional) → `WTT_`-prefixed
environment variables (sources cleared first).

`ConfigureServices` order:

1. `AddWttLogging(configuration)` — Serilog stack + `LoggingOptions`,
   `LoggingLevelSwitch`, `LoggingController`
2. `AddInfrastructure(configuration)` — everything in §6.7 **without** pipeline workers
3. WPF-UI plumbing — `AddNavigationViewPageProvider()`,
   `INavigationService → NavigationService`, `IContentDialogService`,
   `ISnackbarService`
4. UI singletons — `IDialogService`, `NavigationContext`, `ThemeManager`,
   `SessionManager`, `ShellNavigator`, `CsvExportService`
5. Windows + VMs (transient) — `LoginWindow`, `AdminEnrollmentWindow`, `MainWindow`
6. Pages + VMs — **transient**, except **`TrendsPage`/`TrendsViewModel` and
   `LogViewerPage`/`LogViewerViewModel` which are singletons** so pens and
   filters survive navigation
7. Dialog VMs via `AddWithFactory<T>` (registers `T` transient **and** a
   `Func<T>` factory singleton)

### 8.2 Startup sequence (`OnStartup`)

```text
register global exception handlers
  → show StartupWindow (420×230 borderless splash)
  → DatabaseInitializer.VerifySchemaCurrentAsync()  ∥  ThemeManager.LoadAsync()
  → if no users: show AdminEnrollmentWindow (cancel ⇒ shutdown)
  → _host.StartAsync()          // background services (UI-side only)
  → ShellNavigator.ShowLogin()
  → close splash
```

Failure ⇒ message box naming the log folder `%LocalAppData%\WTT\Logs`, exit
code `-1`.

`OnExit`: `_host.StopAsync` (15 s budget) → dispose → `Log.CloseAndFlushAsync()`.

### 8.3 Global exception handlers

| Handler | Behaviour |
|---|---|
| `DispatcherUnhandledException` | logged, `Handled = true`, error dialog via `IDialogService` (MessageBox fallback) |
| `TaskScheduler.UnobservedTaskException` | observed |
| `AppDomain.UnhandledException` | critical log + flush |

The UI never crashes out from a background fault.

### 8.4 Login flow

```text
App.OnStartup → schema check
  → no users? AdminEnrollmentWindow (modal)  [close ⇒ shutdown]
  → _host.StartAsync()
  → ShellNavigator.ShowLogin()  (fresh transient LoginWindow, CanSignIn=true)
  → LoginViewModel.LoginAsync → IAuthService.LoginAsync
       ├─ failure          → inline error
       ├─ MustChangePassword → ChangePasswordWindow modal
       │     ├─ changed    → ShellNavigator.ShowMain()
       │     └─ cancelled  → LogoutAsync + "must change the temporary password"
       └─ success          → ShellNavigator.ShowMain()
  → MainWindow starts SessionManager
       idle ≥ SessionTimeoutMinutes (default 30) → auto logout → new LoginWindow
```

Keyboard: **Ctrl+D** → Dashboard, **Ctrl+L** → sign out.

### 8.5 Pages

| Page | Purpose | VM |
|---|---|---|
| `DashboardPage` | KPI cards, connected-devices panel, per-device cards with `Polyline` sparklines, capture/monitor toggles, Configure | `DashboardViewModel` (+ `DeviceCardViewModel`) |
| `DevicesPage` | Device DataGrid, Refresh/Add device, row Configure/Edit/Delete | `DevicesViewModel` (+ `DeviceRowViewModel`, `DeviceEditViewModel`) |
| `DeviceConfigPage` | 5 `ProTabItem` tabs: PLC tags, Address blocks (Modbus only), Custom & external tags, Capture trigger, Central server | `DeviceConfigViewModel` (~410 lines, 23 commands) |
| `LiveDataPage` | Tag value grid, Export CSV, Refresh, row Write gated by `CanWrite` | `LiveDataViewModel` (+ `LiveTagRow`, `WriteTagViewModel`) |
| `TrendsPage` | SCADA multi-pen plot, time windows, pen legend, live toggle | `TrendsViewModel` (~700 lines) |
| `AlarmsPage` | Active Alarms + Alarm History, acknowledge, retention | `AlarmsViewModel` |
| `DataTransferPage` | KPIs, servers grid, recent batches | `DataTransferViewModel` |
| `UsersPage` | User grid, Add/Edit/Reset password/Unlock/Delete | `UsersViewModel` |
| `LogViewerPage` | Application log + Audit trail tabs | `LogViewerViewModel` |
| `SettingsPage` | My Account, Appearance, Simulation, Security & retention, Log File Generation, Local database, About | `SettingsViewModel` |
| `ProfilePage` | Account details, status & security, permission list | `ProfileViewModel` |

### 8.6 `ViewModelBase`

```csharp
ViewModelBase : ObservableObject, IPageViewModel
```

Provides `IsBusy`, `ErrorMessage`, and `RunAsync(...)` which:
- sets/clears the busy flag,
- converts `WttException` → friendly dialog or inline message,
- converts anything else → logged generic error,
- exposes `OnUi`/`OnUiAsync` dispatcher helpers.

All commands are CommunityToolkit.Mvvm source-generated (`[RelayCommand]` on a
private method ⇒ `<Method>Command` / `<Method>AsyncCommand`).

### 8.7 Sidebar

`ui:NavigationView`, `PaneDisplayMode=Left`, `OpenPaneLength=248`, no pane
toggle, font 13.5.

```text
[brand tile]   WTT / INDUSTRIAL DATA ACQUISITION

MONITOR
  Dashboard        Grid24
  Live Data        Pulse24        (CanViewLiveData)
  Trends           ChartMultiple24 (CanViewLiveData)
  Alarms           AlertUrgent24  + red badge → ActiveAlarmCount

MANAGE
  Devices          Server24       (CanViewDevices)
  Data Transfer    ArrowSync24    (CanViewTransfer)
  Users            PeopleTeam24   (CanManageUsers)
  Logs             DocumentText24 (CanViewLogs)

──────────────
Settings         Settings24      (all roles)

header: [bell + badge → Alarms]  [Account → Profile]
```

Also: `ui:ContentDialogHost` and `ui:SnackbarPresenter` (bottom-centre, max
width 600).

---

## 9. Data flow end to end

### 9.1 Capture

```text
DeviceCaptureWorker tick (period = max(`MinPollIntervalMs` (default 100 ms, configurable), device.PollIntervalMs))
  │
  ├─ build read set: selected PLC tags ∪ trigger tag ∪ condition tags
  ├─ driver.ReadAsync(...)                 ← per-tag failures become TagReadResult
  │     ModbusTcp  → NModbus, block-packed, MaxRegistersPerRequest 120
  │     SiemensS7  → S7.Net, MaxItemsPerRequest 19 / 200 bytes
  │     EthernetIp → libplctag, AllowPacking
  │     (UseMockPlc → MockPlcDriver, deterministic)
  │
  ├─ TagValueFormatter.Format  → engineering = raw * ScaleFactor + ScaleOffset
  ├─ LiveValueCache.Put       → in-process only
  ├─ AlarmEvaluationService   → delays, deadband, flood suppression, persist
  ├─ TriggerEvaluator         → manual / plc-trigger / logical
  ├─ external context         → rising edge only (ExternalContextLoaded latch)
  │
  └─ if IsCaptureEnabled && triggered:
        INSERT TagReadings … all rows share one ScanId, IsTransferred = 0
        (Value truncated to 500 chars, AutoDetectChangesEnabled = false)
```

### 9.2 Transfer

```text
DataTransferBackgroundService tick (default 5 s)
  │  _cycleLock — skip if a cycle is already running
  │
  ├─ queue = enabled DeviceServers
  ├─ per device, up to MaxBatchesPerDevicePerCycle (4)
  │
  └─ per batch, inside Polly pipeline keyed "{ServerName}|{DatabaseName}":
        SET QUOTED_IDENTIFIER ON; SET ARITHABORT ON;
        EnsureDestinationSchemaAsync   (≤ once/hour/server/table)
        SELECT … FROM TagReadings WHERE IsTransferred = 0
        → temp #WTTBatch → SqlBulkCopy
        → INSERT…SELECT WHERE NOT EXISTS (UPDLOCK, HOLDLOCK)
             OPTION (LOOP JOIN, MAXDOP 1)
        → COMMIT
        → only then: UPDATE TagReadings SET IsTransferred = 1 (contiguous id range)
        → UPDATE DeviceServers.LastSuccessAtUtc
        → INSERT TransferBatch (Success)
        → raise BatchCompleted
```

Failure ⇒ Polly retry (3, exponential, 2 s base, jitter) ⇒ circuit breaker
(5 failures / 5 min sampling / 60 s break) ⇒ `TransferBatch(Failed)` +
`DeviceServers.LastError`. Local rows untouched.

### 9.3 Read path for the UI

```text
UI page → service (IDbContextFactory, short-lived context, AsNoTracking)
  Dashboard  → CaptureStatistics, DeviceRuntimeStates, ServiceRuntimeState
  Live Data  → DeviceTags.CurrentValue/CurrentQuality/CurrentValueAtUtc
  Trends     → HistorianService.GetTrendsAsync → keyset + LTTB downsample
  Transfer   → TransferStatistics, TransferBatches
  Alarms     → AlarmEvents, alarm counts
```

---

## 10. Database schema

### 10.1 Local database `WTT` — 15 tables

| Table | Purpose |
|---|---|
| `Devices` | Device configuration (unique `Name`) |
| `DeviceAddresses` | Modbus register blocks |
| `DeviceTags` | PLC/Custom/External tags (unique `(DeviceId, Name)`) |
| `DeviceTriggerConditions` | Logical capture expression clauses |
| `DeviceQueries` | External read-only SQL sources |
| `DeviceQueryColumns` | Verified columns |
| `DeviceServers` | Central transfer destinations (unique `DeviceId`) |
| `TagReadings` | **The historian buffer** |
| `TransferBatches` | Transfer attempt history |
| `Users` | Accounts (unique `NormalizedUsername`) |
| `AuditLogs` | Security audit |
| `AppSettings` | Runtime settings (`Key` is the PK) |
| `ServiceRuntimeState` | Heartbeat, exactly one row (`Id` never generated) |
| `DeviceRuntimeStates` | Per-device status (PK `DeviceId`) |
| `AlarmEvents` | ISA-18.2 alarm lifecycle |

### 10.2 Important indexes

| Index | Definition | Why |
|---|---|---|
| `IX_Devices_Name` | unique | Duplicate device names rejected |
| `IX_DeviceTags_DeviceId_Name` | unique | Duplicate tag names per device rejected |
| `IX_TagReadings_DeviceId_IsTransferred_Id` | `(DeviceId, IsTransferred, Id)` | Per-device pending scans |
| `IX_TagReadings_IsTransferred_TimestampUtc` | `(IsTransferred, TimestampUtc)` | Transfer queue + retention purge |
| **`IX_TagReadings_HistorianTrend`** | `(DeviceId, TagName, TimestampUtc) INCLUDE (Value, Quality)` | Trend queries read only these columns — the covering index avoids touching the row |
| `IX_Users_NormalizedUsername` | unique | Case-insensitive lookup |
| `IX_TransferBatches_StartedAtUtc` | | Recent batches page |
| `IX_AuditLogs_TimestampUtc` | | Audit paging |
| `IX_DeviceQueries_DeviceId_Name` | unique | |
| `IX_DeviceTriggerConditions_DeviceId_Order` | | Ordered evaluation |
| **`IX_AlarmEvents_ActiveQuery`** | `(State ASC, Priority DESC, TriggeredAtUtc DESC)`, **filtered `[State] IN (1,2)`**, large INCLUDE list | Active + Acknowledged alarms |
| `IX_AlarmEvents_RetentionCleanup` | `(State, TriggeredAtUtc)` | Purge |
| `IX_AlarmEvents_SyncQuery` | `(TriggeredAtUtc, ClearedAtUtc)` | Cross-process sync |
| `IX_DeviceRuntimeStates_InstanceId` | | Instance fencing |

> **`IX_AlarmEvents_ActiveQuery` filter must be `(1, 2)`.** The comment in
> `AlarmEventConfiguration.cs` warns that `(0, 1)` would *silently exclude
> Acknowledged* alarms — a correctness bug that looks like missing data.

### 10.3 Data types worth knowing

- `TagReadings.Timestamp` / `TimestampUtc` are `datetime2(3)`.
- `TagReadings.Value` is `nvarchar(500)` (truncated by the worker).
- `AppSettings.Value` ≤ 4000.
- `TransferBatches.ErrorMessage` ≤ 4000.
- `DeviceServers.ProtectedPassword` `nvarchar(2000)` (DPAPI blob).
- `DeviceQueries.QueryText` ≤ 8000.
- `Users.Role` `nvarchar(20)`.
- `ServiceRuntimeState.Id` / `DeviceRuntimeStates.DeviceId` are never
  auto-generated (explicit PK values).

### 10.4 Central destination table

Created/upgraded at runtime by `EnsureDestinationSchemaAsync`, default name
`TagDetail` (`DeviceServer.TableName`):

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

Indexes: `UX_{table}_SourceId` (filtered unique on
`(SourceId, SourceReadingId)`) and `IX_{table}_PLC_Time`
(`(PLCName, TimeStamp) INCLUDE (TagName, TagValue)`).

Legacy-compatible: the column set is a superset of the original `TagDetail`
layout, so pre-existing central consumers keep working.

### 10.5 Provisioning

`deploy\Provision-Database.sql`:

```sql
CREATE DATABASE [$(DatabaseName)] COLLATE Latin1_General_100_CI_AS_SC;
-- RECOVERY SIMPLE, PAGE_VERIFY CHECKSUM, AUTO_CLOSE OFF, AUTO_SHRINK OFF
-- FILEGROWTH=128MB, MAXSIZE=9500MB  (below the Express 10 GB ceiling)
CREATE ROLE [wtt_runtime];
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::[dbo] TO [wtt_runtime];
```

Runtime identities get `wtt_runtime` **only**. Migrations run under a separate
deployment administrator — `wtt_runtime` must never gain DDL rights.

---

## 11. Security model

### 11.1 Authentication

| Control | Value |
|---|---|
| Password storage | BCrypt, work factor 12 (clamped 10–15) |
| Shared default password | **Does not exist.** First admin is enrolled interactively |
| Min length | 8 |
| Mixed case | required |
| Digit | required |
| Failed logins | 5 |
| Lockout | 15 minutes |
| Username lookup | `NormalizedUsername` — case-insensitive |
| Timing attack | dummy BCrypt verify for unknown users |
| Error message | always `"Invalid username or password."` |
| Forced change | `MustChangePassword = true` for created users |
| Reuse | previous password rejected |

### 11.2 Authorization

Three roles, 13 permissions (§5.3). Enforced in **both** processes:

- **UI:** nav-item visibility (`CanViewLiveData`, `CanViewDevices`,
  `CanViewTransfer`, `CanManageUsers`, `CanViewLogs`), command `CanExecute`,
  row-button visibility.
- **Service:** `ICurrentUserService.Demand(Permission)` inside every service
  method.

Hiding a button is never the only protection.

### 11.3 Secret handling

| Secret | Mechanism |
|---|---|
| Local database password | **None** — Windows Integrated Security only |
| Transfer server password | DPAPI `LocalMachine`, prefix `dpapi:v1:`, entropy `WTT.SecureStorage.v1` |
| External query password | same DPAPI scheme |
| Logs | `EnablePiiMasking = true`; passwords and full connection strings never logged |
| Audit | `AuditService` never throws — auditing must not break the primary operation |

### 11.4 PLC write safety — five independent guards

All must pass, or the write is rejected with a specific message:

| # | Guard | Failure message |
|---|---|---|
| 1 | **Tag allowlist** — `IsWriteEnabled` (default `false`) | Write denied, driver never executes |
| 2 | **Role** — only Admin (`WriteToPlc`) | Authorization failure |
| 3 | **Type** — text must parse to `TagDataType` | Parse error |
| 4 | **Range** — within `WriteMinimum`/`WriteMaximum` | Range rejection |
| 5 | **Cooldown + read-back** — `MinimumWriteIntervalMs` (default 1000 ms, min 250), then read the controller back and compare | Cooldown rejection, or read-back mismatch |

Additional constraints: `TagKind.Plc` only, `ElementCount == 1` only.

Read-back comparison: ordinal for strings/bools; float tolerance
`max(1e-6, |expected| * 1e-5)` for numerics. Every write is audited with
Old / Requested / Readback.

### 11.5 External SQL safety

`ExternalSqlSelectPolicy` parses with `TSql180Parser` and demands:

- exactly **one batch**
- exactly **one statement**
- the statement must be a `SelectStatement`
- no parse errors
- no `SELECT INTO`

Stacked statements (`SELECT 1; DROP TABLE …`) are rejected at parse time, at
**save** and at **verify**. Runtime uses `CommandBehavior.SingleRow` with a 30 s
timeout. Recommended: a dedicated SQL login with `SELECT` permission only.

### 11.6 File system

| Path | ACL |
|---|---|
| `C:\ProgramData\WTT` root | Locked down by `icacls` |
| `C:\ProgramData\WTT\Logs` | Restricted |
| `C:\ProgramData\WTT\Backups` | SQL service SID gets write; restricted otherwise |
| Service identity | `NT SERVICE\WTTData` virtual account |
| Local group | `WTTUsers` |
| UI manifest | `asInvoker` — never requires elevation |

---

## 12. Resilience and retry strategy

Six independent layers:

| # | Layer | Policy |
|---|---|---|
| 1 | **EF Core SQL** | `EnableRetryOnFailure(5, 5 s)` + `CommandTimeout(30)` |
| 2 | **Transfer per central server** | Polly v8: retry 3×, exponential backoff, 2 s base, jitter; circuit breaker `FailureRatio 1.0`, `MinimumThroughput = max(2, CircuitBreakerFailures)`, `SamplingDuration 5 min`, `BreakDuration = max(5 s, CircuitBreakerDurationSeconds)` |
| 3 | **PLC reconnect** | Exponential + jitter, 2 s → 60 s; config errors idle 30 s |
| 4 | **Alarm DB queue** | 3 retries, `100 ms * retryCount` backoff, then `LogCritical("LOST ALARM: …")` |
| 5 | **Mock state file** | 3 attempts, 500 ms mutex wait, `50*(attempt+1)` ms backoff |
| 6 | **Windows Service** | `restart/5000 restart/15000 restart/60000`, reset 86400 |

### Transient `SqlException` numbers

```text
-2, 53, 121, 233, 1205, 4060, 10053, 10054, 10060,
40197, 40501, 40613, 49918, 49919, 49920, 11001
```

plus `Class >= 20`, `TimeoutException`, and timeout `InvalidOperationException`.

### Never-terminate rules

These boundaries log and continue rather than crash the host:

- `"Capture supervisor cycle failed; will retry."`
- `"Transfer cycle failed unexpectedly; will retry next interval."`
  — comment: *"Must never terminate the host"*
- Alarm queue exhausted → `LogCritical("LOST ALARM: …")`
- `AuditService` swallows its own failures
- `SqlCaptureRuntimeStatusService.GetSnapshotAsync` returns a failed snapshot
  instead of throwing — *"a health display must never itself throw"*

### Data-safety rules

1. Local rows are marked transferred **only after** the remote commit.
2. Untransferred readings are never silently deleted.
3. `UntransferredMaxAgeDays` defaults to **0 (disabled)** and warns when set.
4. Delete-device is blocked while untransferred readings exist.
5. Update takes a DB backup and verifies it exists before migrating.

---

## 13. Alarms (ISA-18.2)

`AlarmEvaluationService` (763 lines) implements `IAlarmService`.

### 13.1 Evaluation features

| Feature | Behaviour |
|---|---|
| On-delay | `AlarmOnDelaySeconds` — value must stay bad that long before triggering |
| Off-delay | `AlarmOffDelaySeconds` — must stay good that long before clearing |
| Deadband | **absolute engineering units** — `isHighCleared = value < Max - deadband` (default 2.0) |
| Flood suppression | **max 10 alarms/minute/tag** |
| Shelving | **max 24 h** — *"Alarm shelving duration cannot exceed 24 hours for safety reasons."* |
| Priorities | `Critical=1`, `High=2`, `Medium=3`, `Low=4` |
| Types | `High=1`, `Low=2` |
| States | `Active=1`, `Acknowledged=2`, `Cleared=3`, `Shelved=4` |

### 13.2 Persistence path

- **Critical** alarms write **directly to the database** — no channel, no loss
  window.
- Others go through a bounded channel
  (`BoundedChannelFullMode.DropOldest`, capacity **1000**) with **3 retries** and
  `100 ms * retry` backoff. Exhaustion logs
  `LogCritical("LOST ALARM: …")`.
- Cleanup every **10 minutes**.
- Retention: startup + every **24 h** (`AlarmRetentionWorker`);
  `RetentionDays <= 0` = keep forever; runtime key `Alarm.RetentionDays`
  overrides the bound option; values outside 0–3650 are rejected with
`ArgumentOutOfRangeException` (`"Retention must be 0-3650 days (0=keep forever)."`).

### 13.3 Cross-process sync

`SyncCrossProcessAlarmsAsync` reconciles rows created by the *other* process —
both the UI and the service can raise alarms, and `AlarmEvents` is the shared
record. `IX_AlarmEvents_SyncQuery` supports this.

### 13.4 Notifiers

| Notifier | Behaviour |
|---|---|
| `InAppAlarmNotifier` | P/Invoke `user32!MessageBeep` + `Console.Beep`. Critical = 3 bursts @1200 Hz, High = 2 @1000 Hz, 400 ms tone / 200 ms gap. Gated by `BeepEnabled` and priority ≥ High. |
| `EmailAlarmNotifier` | `System.Net.Mail.SmtpClient`, **only** Critical/High, throttle `SemaphoreSlim(5,5)`, `SmtpHost ?? "localhost"`, `SmtpPort ?? 25`, `EnableSsl = SmtpUseSsl ?? false`, **timeout 10000 ms**, optional `NetworkCredential`, HTML body with priority colour, `NotifyClearedAsync` deliberately a no-op. |
| `CompositeAlarmNotifier` | Fan-out, swallows per-notifier exceptions — one broken notifier cannot stop the others. |

---

## 14. Configuration reference

### 14.1 `appsettings.json` (`src/WTT.UI/appsettings.json`)

Also copied into the Service output as `appsettings.json`.

```text
Database
  ConnectionString = Server=.;Database=WTT;Integrated Security=True;Encrypt=True;
                     TrustServerCertificate=True;Application Name=WTT;
                     Connect Timeout=10;Pooling=True

Capture
  ReconcileIntervalSeconds   5
  MinPollIntervalMs          100
  ReconnectInitialDelaySeconds 2
  ReconnectMaxDelaySeconds   60
  CurrentValueFlushSeconds   5
  TransferredRetentionDays   7
  UntransferredMaxAgeDays    0        (0 = never purge untransferred)

Transfer
  IntervalSeconds            5
  BatchSize                  1000
  BulkCopyBatchSize          1000
  BulkCopyTimeoutSeconds     60
  CommandTimeoutSeconds      30
  RetryCount                 3
  CircuitBreakerFailures     5
  CircuitBreakerDurationSeconds 60
  MaxBatchesPerDevicePerCycle 4

Security
  SessionTimeoutMinutes      30
  MaxFailedLogins            5
  LockoutMinutes             15
  MinPasswordLength          8
  RequireMixedCasePassword   true
  RequireDigitPassword       true
  BcryptWorkFactor           12

Simulation
  UseMockPlc                 false

Logging
  EnableFileLogging          true
  LogDirectory               %LocalAppData%/WTT/Logs
  FileName                   wtt
  RollingInterval            Day
  RetainedFileCountLimit     30
  FileSizeLimitBytes         20971520      (20 MB)
  RollOnFileSizeLimit        true
  FlushToDiskInterval        00:00:02
  MinimumLevel               Information
  LevelOverrides             Microsoft=Warning, System=Warning,
                             Microsoft.EntityFrameworkCore=Warning,
                             Microsoft.EntityFrameworkCore.Database.Command=Warning,
                             Microsoft.Hosting.Lifetime=Information
  UseJsonFormatter           false
  IncludeScopingProperties   true
  EnablePiiMasking           true
  AdditionalSinks            []
  EnableDebugSink            true
```

No `Alarm` section here ⇒ `AlarmOptions` uses code defaults (§14.3).

### 14.2 `appsettings.Service.json` (service-only overlay)

```text
Serilog:WriteTo:File
  path                 C:/ProgramData/WTT/Logs/service-.log
  rollingInterval      Day
  retainedFileCountLimit 30
  fileSizeLimitBytes   20971520
  rollOnFileSizeLimit  true
  flushToDiskInterval  00:00:02
  outputTemplate       {Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}]
                        [{ThreadId}] [{SourceContext}] {Message:lj}{NewLine}{Exception}
```

### 14.3 Code defaults (`AppOptions.cs`)

Used when a key is absent — these differ from appsettings in two places.

| Options | Defaults |
|---|---|
| `CaptureOptions` | same as appsettings |
| `TransferOptions` | **`BatchSize 10000`** (appsettings uses **1000**), rest same, `BatchHistoryRetentionDays 30` |
| `SecurityOptions` | same as appsettings |
| `DatabaseOptions` | same as appsettings |
| `SimulationOptions` | `UseMockPlc false` |
| `AlarmOptions` | `RetentionDays 90`, `BeepEnabled true`, `EmailEnabled false`, `SmtpHost null`, `SmtpPort 587`, `SmtpUseSsl true`, `SmtpUsername/SmtpPassword/EmailFrom/EmailToCsv null` |

> **Known discrepancy:** `TransferOptions.BatchSize` default is `10000` in code
> but `1000` in `appsettings.json`. The file wins at runtime. Align them if you
> touch either.

### 14.4 Environment overrides

Both hosts clear config sources and re-add explicitly, so the override prefix is
**`WTT_`**:

```powershell
$env:WTT_Database__ConnectionString = "Server=.\SQLEXPRESS;Database=WTT;..."
$env:WTT_Simulation__UseMockPlc = "true"
$env:WTT_ENVIRONMENT = "Development"     # picks appsettings.Development.json
```

### 14.5 Development overlays

| File | Contents |
|---|---|
| `src/WTT.UI/appsettings.Development.json` | `Logging.EnableFileLogging=false`, `Logging.EnableDebugSink=true` |
| `src/WTT.UI/Properties/launchSettings.json` | profile `"WTT UI - Mock PLC"`: `WTT_ENVIRONMENT=Development`, `WTT_Database__ConnectionString` |
| `src/WTT.Service/Properties/launchSettings.json` | profile `"WTT Service - Mock PLC"`: `DOTNET_ENVIRONMENT=Development`, custom connection string, `WTT_Simulation__UseMockPlc=true` |

### 14.6 Runtime (database) settings

Stored in `AppSettings`, applied without restart:

| Key | Meaning | Clamp |
|---|---|---|
| `ui.theme` | Light / Dark | — |
| `simulation.useMockPlc` | Mock PLC on/off | — |
| `security.sessionTimeoutMinutes` | Idle auto-logout | 1–720 |
| `capture.transferredRetentionDays` | Purge horizon for transferred rows | 1–3650 |
| `transfer.intervalSeconds` | Transfer cycle interval | 1–3600 |
| `Alarm.RetentionDays` | Alarm purge horizon | 0–3650 (0 = forever) |
| `Alarm.BeepEnabled` | Audible alarm on/off | `"1"` / `"0"` — written on change, not re-read after restart (see §5.2) |

---

## 15. Logging and observability

### 15.1 Framework

**Serilog** in both hosts.

**Service** — `UseSerilog(ReadFrom.Configuration)`:
- bootstrap: `C:\ProgramData\WTT\Logs\bootstrap-.log`, rolling daily, retained 7
- main: `C:/ProgramData/WTT/Logs/service-.log`, rolling daily, retained 30,
  20 MB roll, template includes `ThreadId` and `SourceContext`

**UI** — custom `AddWttLogging` (`src/WTT.UI/Logging/LoggingExtensions.cs`):
- `LoggingOptions` bound from the `Logging` section
- `LoggingLevelSwitch` + `LoggingController` (both singletons)
- enrichers: `FromLogContext`, `MachineName`, `ThreadId`,
  `CorrelationIdEnricher`, `PiiMaskingEnricher`, per-namespace level overrides
- custom **`DateFolderFileSink`** — writes into `yyyy-MM-dd` folders
- optional Debug sink
- `CorrelationIdExtensions.PushCorrelationId`

`LoggingController` toggles log level and file logging **at runtime without a
restart**; `EnableFileLogging` is persisted back into `appsettings.json`. The
Settings page's *Log File Generation* switch drives it.

### 15.2 Log locations

| Process | Path |
|---|---|
| Service | `C:\ProgramData\WTT\Logs\` |
| Service bootstrap | `C:\ProgramData\WTT\Logs\bootstrap-.log` |
| UI | `%LocalAppData%\WTT\Logs\` (date-foldered, `wtt-*.log`) |
| Install transcript | `C:\ProgramData\WTT\install.log` |

### 15.3 In-app log viewer

`LogReaderService` reads `wtt-*.log` from `%LocalAppData%\WTT\Logs` (flat or
`yyyy-MM-dd` folders), last 10 files, shared read
(`FileShare.ReadWrite | Delete`), level order `VRB DBG INF WRN ERR FTL`, regex
for `2026-10-01 00:01:56.454 +05:30 [INF] [T2] [Source] message`.

### 15.4 Health — no HTTP endpoint

| Signal | Source |
|---|---|
| Service alive | `ServiceRuntimeState` (`Id = 1`) — `InstanceId`, `HeartbeatAtUtc`, `StartedAtUtc`, `LastError` |
| Alive vs crashed | `SqlCaptureRuntimeStatusService.StaleAfter = 60 s` |
| Fencing | `InstanceId` — writes from a process that lost its session are rejected |
| Per device | `DeviceRuntimeStates` — `WorkerRunning`, `IsConnected`, `IsCapturing`, `IsMonitoring`, `LastScanUtc`, `ScanCount`, `ReadingsWritten`, `ErrorCount`, `ReconnectAttempts`, `LastError` |
| Transfer health | `DeviceServers.LastSuccessAtUtc` / `LastError`, plus `TransferBatches` |

There is **no** ASP.NET health-check framework, no OpenTelemetry, no
AppMetrics, no EventCounters. The database is the health channel — which is
correct for two processes that already share it.

---

## 16. Testing strategy

### 16.1 Stack

| Item | Value |
|---|---|
| Framework | xUnit 2.9.3 + `xunit.runner.visualstudio` 3.0.2 |
| Assertions | FluentAssertions 7.2.0 |
| Mocking | NSubstitute 5.3.0 |
| Test SDK | Microsoft.NET.Test.Sdk 17.13.0 |
| TFM | `net10.0-windows`, deliberately **not** `UseWPF` |
| Parallelism | **Disabled** (`[assembly: CollectionBehavior(DisableTestParallelization = true)]`) |

`WTT.Tests` references `WTT.Infrastructure` and `WTT.UI`. The UI reference
exists for view-model tests only — **no window is ever created**. The comment in
the csproj explains that setting `UseWPF` breaks implicit usings / `System.IO`.

### 16.2 Test classes

| Class | Category | Focus |
|---|---|---|
| `AlarmEvaluationServiceTests` | unit | on/off delay, deadband, flood suppression (10/min), shelving, critical direct-DB write, acknowledge-all, init lock (13 facts) |
| `AllenBradleyAddressAndCatalogTests` | unit | PCCC parsing, ASCII ranges, type inference, Logix symbolic pass-through, catalog completeness, GM10 bank layout, scaling, `ParseForWrite` |
| `AuthAndPersistenceTests` | **Integration** | real SQL + real BCrypt (only audit faked): first-admin enrollment, password change survives restart, weak password rejected, lockout, case-insensitive username, viewer restrictions, last-admin protection |
| `CapturePipelineEndToEndTests` | **Integration** | manual mode shared `ScanId`, PLC-trigger gating, logical expression enforcement, sequential Modbus block generation, unknown protocol rejected (not mocked) |
| `DataTransferIntegrationTests` | **Integration** | bulk transfer creates Unicode schema, acknowledges rows |
| `RuntimeStatusIntegrationTests` | **Integration** | heartbeat visible cross-process, clean stop is immediate |
| `ExternalSqlSelectPolicyTests` | unit | accepts one read-only SELECT; rejects non-read-only and multiple statements |
| `InstallationIdentityTests` | unit | identity stable and unique per path |
| `LiveDataLoadingTests` | unit (STA) | first load completes within navigation, status resolved in one pass, sorting/filtering, no-devices path, failure resets busy, navigation stops the timer |
| `ModbusCodecTests` | unit | Float32 LowHigh (Yokogawa) vs HighLow, encode/decode round trip, string round trip, address parser + rejections |
| `RuntimeSettingsTests` | unit | `Changed` raised only when values actually change |
| `TriggerEvaluatorTests` | unit | numeric/string comparison, non-lexicographic, `OnChange` needs a previous value, left-to-right semantics, missing tag false, `IsTrue`, `Describe` |

**Shape:** 12 test files, 14 test classes, 63 `[Fact]`/`[Theory]` attributes
(theories expand to more cases). The current suite reports **97 tests passing**
with `Category!=Integration`.

> `docs/USER_GUIDE.md` historically claimed "110 automated tests passed". Verify
> the number from a fresh run before quoting it.

### 16.3 Fixtures and helpers

**`SqlServerTestDatabase`**
- per-test database `WTT_Test_{guid:N}`
- server from env **`WTT_TEST_SQLSERVER`** (default `.`), Windows integrated security
- `CreateOptions()` → `DbContextOptions<AppDbContext>`
- `DeleteAsync()` clears pools then `DROP DATABASE` (single-user rollback,
  immediate)

**`Sta`**
```csharp
Sta.Run(Action body, int settleMs = 350)
```
Runs a body on a dedicated **STA** thread then pumps the dispatcher
(`Sta.Pump(ms)`). xUnit runs MTA, but `LiveDataViewModel` uses a
`DispatcherTimer` and a fire-and-forget first load — without this the tests
would deadlock or race.

**`AssemblyInfo`** disables parallelization (integration tests each own a
database).

**Integration classes** implement `IAsyncLifetime`:
`InitializeAsync()` builds `SqlServerTestDatabase`, an
`IDbContextFactory<AppDbContext>` and runs **`db.Database.MigrateAsync()`**;
`DisposeAsync()` → `_database.DeleteAsync()`.

### 16.4 Running tests

```powershell
# Unit only (no SQL Server needed)
dotnet test tests/WTT.Tests/WTT.Tests.csproj -c Debug --filter "Category!=Integration"

# Full suite (needs SQL Server)
dotnet test WTT.slnx -c Debug
```

`Category=Integration` marks the four classes requiring a real SQL Server.

---

## 17. Build and CI

### 17.1 Local build

```powershell
dotnet tool restore                       # dotnet-ef 10.0.11
dotnet restore WTT.slnx
dotnet build WTT.slnx -c Debug
```

`.config\dotnet-tools.json` pins `dotnet-ef` **10.0.11** with no `rollForward`.

### 17.2 CI — `.github/workflows/ci.yml`

`name: CI`, triggers on `push`/`pull_request` to `main`.
Env: `DOTNET_VERSION=10.0.x`.

**Job `build-and-test`** (`windows-latest`, 30 min):

1. Native SQL Server 2022 via `potatoqualitee/mssqlsuite@v1`
   (`install: sqlengine`, SA password `Password123!`, reachable at `localhost`).
   A Docker `services:` container is deliberately **not** used — GitHub runs
   service containers on Linux runners only, so it fails on `windows-latest`
   at "Initialize containers" before any step executes.
2. checkout → setup-dotnet → `dotnet tool restore`
3. poll `sqlcmd` until ready (30 × 5 s)
4. `dotnet restore WTT.slnx`
5. `dotnet build -c Release --no-restore`
6. **Unit tests** `--filter "Category!=Integration"` → `unit-tests.trx`
7. **Integration tests:** `dotnet ef database update` (context `AppDbContext`)
   with `WTT_DESIGN_CONNECTION`, then
   `--filter "Category=Integration"` → `integration-tests.trx`, using
   `WTT_DESIGN_CONNECTION=Server=localhost;Database=WTT;User ID=sa;Password=Password123!;...`
8. upload all `**/*.trx` as `test-results` (always)

**Job `deploy-artifacts`** (needs `build-and-test`, push to `main` only, 15 min):

1. restore + build Release
2. `dotnet publish` UI and Service self-contained `win-x64` → `artifacts/UI`, `artifacts/Service`
3. `dotnet tool run dotnet-ef migrations bundle --self-contained -r win-x64 -o artifacts/Database/WTT.Database.exe`
4. copy `deploy/*.ps1`
5. upload `artifacts/` as `wtt-release-<sha>`, retention 30 days

### 17.3 Release

```powershell
.\deploy\Publish-Release.ps1 -Version 1.2.2
.\deploy\Install-WTT.ps1 -PackageRoot .\artifacts\WTT-1.2.2
```

Full detail in `docs/INSTALL_AND_UPDATE.md`.

---

## 18. Design decisions and trade-offs

| Decision | Rationale | Cost |
|---|---|---|
| **Two processes, one DB** | Acquisition must survive UI closure; SQL is already needed, so it becomes the IPC channel | UI shows slightly stale data; heartbeat must be interpreted |
| **`IDbContextFactory` + no repositories** | Short-lived contexts, `AsNoTracking`, set-based `ExecuteUpdate/Delete`; a repository would expose the same surface | Services talk to EF directly |
| **Everything singleton** | No request scope exists outside a host; factory gives isolation | Callers must not assume scoped lifetime |
| **Service verifies schema, UI migrates** | An unattended service must never silently alter schema | Schema changes require the UI (or the migration bundle) to run first |
| **Windows Integrated Security only** | No stored database password to leak | Deployment needs SQL logins for service accounts |
| **DPAPI for secrets** | Machine-bound, no key management | Binaries are not portable across machines |
| **Enums stored as strings** | Readable in SQL, stable if numeric values shift | Slightly larger storage, conversion cost |
| **Unknown driver combo throws, never falls back to mock** | Silent simulated data would be a data-integrity incident | A typo blocks the device until fixed |
| **Fail-safe to no-capture** | A broken trigger must not flood the historian | Missed rows during misconfiguration (visible via `ConfigError`) |
| **Local rows marked only after remote commit** | Crash ⇒ re-send, absorbed by the filtered unique index | A crash costs a duplicate attempt, never data |
| **Filtered unique index on `(SourceId, SourceReadingId)`** | Makes transfer retries genuinely idempotent | Requires `QUOTED_IDENTIFIER ON` and `ARITHABORT ON` |
| **Covering index `IX_TagReadings_HistorianTrend`** | Trend queries read only 5 columns; avoids touching rows | Extra index to maintain on every insert |
| **Critical alarms bypass the queue** | The most severe alarms must not be droppable | Two persistence paths |
| **LTTB downsampling with quality markers** | Charts stay fast over 100k points **and** break the line at gaps instead of lying across an outage | Downsampled points are approximate |
| **Hand-rendered `ScadaTrendPlot`** | Full control over pens, gaps, digital steps, pan/zoom; no dependency | We maintain the control |
| **No domain events** | No second process to deliver to; DB already carries cross-process state | In-process C# events only |
| **Mock PLC is deterministic** | Reproducible acceptance runs, no `Random` flakiness | Simulated signals are stylised, not statistical |
| **`CoralRefreshBtn` name kept after colour change** | Referenced from nine pages; renaming is churn without user value | Confusing name — documented in the style guide |

### Known inconsistencies

1. **`TransferOptions.BatchSize`** — `10000` in code, `1000` in `appsettings.json`.
   The file wins.
2. **Version drift** — `Directory.Build.props` says `1.2.0`, the shipped artifact
   is `1.2.1`.
3. **Test count** — `USER_GUIDE.md` says 110; a fresh run reports 97.
4. **`ADMIN_GUIDE.md`** was referenced by `README.md` but absent from `docs/`.
5. **`DangerProNative`** lives in `AlarmsPage.xaml` rather than `Styles.xaml`.
6. **`RowBtn`** is defined twice (Alarms, DeviceConfig) with different padding.

---

## Document history

| Version | Date | Change |
|---|---|---|
| 1.2.0 | 2026-10-09 | Initial architecture reference, assembled from a full source review of all six projects, migrations, scripts and CI. |
