# WTT User Guide (Complete)

**Version:** 1.2.0
**Application:** WTT Industrial Data Acquisition
**Components:** WPF client (`WTT.UI` → `WTT.exe`) + background Windows Service (`WTTData`) + local SQL Server Express database (`WTT`)
**Audience:** Operators, Administrators, Viewers, Support Engineers

This guide explains how to *use* WTT: what every screen does, how to configure capture safely, how to read transfer health, and what to do when something looks wrong.

**Related documents:**

| Document | Read it for |
|---|---|
| `docs/ADMIN_GUIDE.md` | Security rules, write-safety policy, backup, monitoring checklist |
| `docs/ARCHITECTURE.md` | How the system works internally (for developers and deep support) |
| `docs/INSTALL_AND_UPDATE.md` | Installation, update, rollback |
| `docs/SQL_SERVER_EXPRESS.md` | Database provisioning, backup, 10 GB ceiling |
| `docs/UI_STYLE_GUIDE.md` | UI colours, buttons, tabs (for UI developers) |
| `docs/MANUAL_TEST_PLAN_HINDI.md` | Step-by-step manual acceptance test plan |

## Contents

1. Overview and Architecture
2. Prerequisites
3. Roles and Permissions
4. First Startup, Login, Logout and Passwords
5. Main Window and Navigation
6. Dashboard in Detail
7. Devices in Detail
8. Address Blocks in Detail
9. Tags in Detail (PLC, Custom, External)
10. Capture Modes and Triggers in Detail
11. Live Data, PLC Write Safety and CSV Export in Detail
12. Data Transfer in Detail
13. Historian Trends in Detail
14. Alarms and Events in Detail (ISA-18.2)
15. Log Viewer in Detail
16. User Management in Detail (Admin Only)
17. Settings in Detail
18. External SQL Queries in Detail
19. Service Behavior, Restart and Data Safety
20. Database Tables - Simple Meaning
21. Daily Operating Procedures
22. Verification Queries for Support
23. Troubleshooting Matrix
24. Safety Rules
25. Support Package - What to Send
26. Glossary
27. Document History

---

## 1. Overview and Architecture

WTT collects data from PLC devices over the plant network, stores every scan in a local historian buffer, and reliably forwards it to central SQL Server databases.

Three parts work together:

- `WTT.UI`: the WPF desktop application you click and operate. It shows dashboards, configuration screens, live values, historian trends, alarms, transfer status, logs, users and settings. It does not talk to PLCs directly.
- `WTTData` service: the background Windows Service that owns polling, PLC communication, local storage, alarm evaluation and central transfer. It keeps running even when the UI is closed.
- SQL Server Express database `WTT`: the single source of truth for configuration, users, readings, batches, alarms, audit logs, settings and service heartbeat.

Typical data flow:

```text
PLC device -> Driver (Modbus TCP / Siemens S7 / Allen-Bradley EtherNet/IP / Simulator)
  -> Capture worker -> alarm evaluation -> Local WTT.TagReadings table
  -> Transfer worker -> Central TagDetail table
  -> Dashboard / Live Data / Trends / Alarms / Transfer pages read from SQL
```

Closing the UI never stops capture. Stopping SQL Server or the `WTTData` service does stop capture and transfer. This is by design.

Release 1.2.0 runs on .NET 10 LTS on 64-bit Windows 10, Windows 11 or supported Windows Server, with local SQL Server 2022 Express.

## 2. Prerequisites

- Windows 10 / 11 or supported Windows Server x64.
- Local SQL Server Express running. The SQL Server service status must be `Running` in Windows Services.
- Administrator access for first installation and first administrator enrollment.
- Desktop shortcut `WTT` created by the installer.
- For testing without hardware: PLC simulator stays ON. Never enter a real plant controller IP during acceptance testing.

Related documents:

- `docs/INSTALL_AND_UPDATE.md`: installer, update, rollback, backup task.
- `docs/SQL_SERVER_EXPRESS.md`: connection string, provisioning, backup, 10 GB limit.
- `docs/ADMIN_GUIDE.md`: administrator security rules.

## 3. Roles and Permissions

WTT has three fixed roles. Permissions are enforced in both the UI and the background service, so hiding a button is never the only protection.

| Capability | Admin | Operator | Viewer |
|---|---|---|---|
| View Dashboard | Yes | Yes | Yes |
| View Live Data | Yes | Yes | Yes |
| View Devices and device list | Yes | Yes | Yes |
| View Data Transfer status and batches | Yes | Yes | Yes |
| Start / Stop capture, Start / Stop transfer, Transfer Now | Yes | Yes | No |
| View logs (application log and audit trail) | Yes | Yes | No |
| View alarms (acknowledge: Admin/Operator only) | Yes | Yes | Yes |
| Add / Edit / Delete devices | Yes | No | No |
| Add / Edit address blocks, tags, triggers, queries, transfer servers | Yes | No | No |
| Write a value to PLC | Yes | No | No |
| Manage users | Yes | No | No |
| Change settings | Yes | No | No |

Practical meaning:

- Viewer: read-only monitoring for supervisors and auditors.
- Operator: shift monitoring plus capture and transfer control, without configuration risk.
- Admin: full configuration, user management and safety-critical PLC writes.

Always verify a new user by signing in as that user and confirming allowed and denied actions.

## 4. First Startup, Login, Logout and Passwords

### 4.1 First administrator enrollment

Fresh installations have no users and no shared default password.

1. Start WTT. A startup window appears, then `Create the first administrator`.
2. Enter Username, for example `admin`.
3. Enter Full name, for example `Plant Administrator`.
4. Enter a strong private password: minimum 8 characters with uppercase, lowercase and digit, for example a pattern like `Word#2026` but with your own unique value.
5. Confirm the password and select `Create administrator`.
6. The login window appears. The `Users` table now contains exactly one active Admin row with a BCrypt hash, never plaintext.

Never record passwords in screenshots, tickets or shared messages.

### 4.2 Normal login

1. Enter username and existing password.
2. Select `Sign in`.
3. The main dashboard opens. The service heartbeat should show `running`.

Login failures show a clear error and never crash the application. Do not guess repeatedly; accounts can lock. If you forget a password, ask an administrator for a reset instead of repeated attempts.

### 4.3 Logout

- Select Profile / Sign out and confirm, or press `Ctrl+L` and confirm.
- You return to the login window. Background capture continues.

### 4.4 Change password

Open Change Password, enter the current password once and the new password twice, then save. A success notification appears and an audit entry is recorded.

### 4.5 Session timeout

Idle sessions sign out automatically. The default is 30 minutes.

- For a quick timeout test, an administrator can set it to 1 minute, wait without input, confirm automatic logout, then restore it to 30 minutes and save.
- Always restore production timeout to 30 minutes after testing.

### 4.6 Keyboard shortcuts

- `Ctrl+D`: open Dashboard from any page.
- `Ctrl+L`: sign out with confirmation.

## 5. Main Window and Navigation

After login the main window has a dark slate sidebar on the left (`#1E293B`), a content area on the right, and a top status bar with the alarm bell and an **Account** button.

### 5.1 Sidebar structure

The sidebar is grouped into two sections plus a footer:

```text
[brand tile]   WTT / INDUSTRIAL DATA ACQUISITION

MONITOR
  Dashboard        plant health, KPIs, per-device cards, sparklines
  Live Data        current tag values, filters, PLC write, CSV export
  Trends           multi-pen historian trends
  Alarms           active alarms + history, with a live red badge

MANAGE
  Devices          PLC connection and capture configuration
  Data Transfer    pending counts, destinations, recent batches
  Users            Admin-only user administration
  Logs             application log and audit trail

──────────────────────
Settings         theme, simulator, security, retention, log generation
```

Items you do not have permission for are hidden entirely, not just disabled —
except **Dashboard**, **Alarms** and **Settings**, which have no visibility gate
and are shown to every role. (Viewing alarms is allowed for all roles;
acknowledging demands the `AcknowledgeAlarm` permission, which Viewers lack.
**Trends** shares the Live Data permission — there is no separate Trends
permission; hiding Live Data hides Trends too.)

### 5.2 Page guide

| Page | What it answers | Primary users |
|---|---|---|
| **Dashboard** | Is data flowing? Is anything buffered? Is the service alive? | All |
| **Live Data** | What are the tags reading *right now*? | All |
| **Trends** | How did a value behave over the last minutes/hours? | All |
| **Alarms** | What needs attention, and what has already been acknowledged? | Operator, Admin |
| **Devices** | How are the PLCs configured? | Admin (edits), all (view) |
| **Data Transfer** | Is data reaching the central database? | Operator, Admin |
| **Users** | Who can sign in, and with what role? | Admin |
| **Logs** | What did the application and the audit trail record? | Admin, Operator |
| **Settings** | Theme, simulator, security, retention, log generation | All (sections vary) |
| **My Profile** (header → Account) | My details, status, security and what I am allowed to do | All |

### 5.3 Status bar

- **Alarm bell with badge** — shows the count of active alarms; clicking it opens the Alarms page. (Sidebar badge `#EF4444`, header bell badge hard-coded `#E53935`.)
- **Account** button — opens My Profile, from which you can change your password or sign out.
- **Snackbars** appear at the bottom for success, warning and error messages.

### 5.4 Layout behaviour

Windows can be resized down to about 720 px width without overlap; grids scroll horizontally when needed. Tab strips (Device Config, Alarms, Logs) use the same solid blue selected tab with white bold text everywhere. Sidebar, title bar and header buttons follow a single colour system documented in `docs/UI_STYLE_GUIDE.md`.

## 6. Dashboard in Detail

The Dashboard answers: is data flowing, is anything buffered, is the service alive?

Displayed indicators:

- Total enabled devices.
- Running capture workers.
- Connected devices.
- Readings captured today.
- Cumulative error count.
- Buffered readings: rows stored locally but not yet transferred. Rising buffered counts mean transfer is behind or failing.
- Service heartbeat: `running` with fresh timestamp means healthy. A stale or missing heartbeat means the service stopped.
- Readings chart: main line is captured readings over time; second series is buffered data. A flat capture line with rising buffer usually means capture works but transfer is blocked.
- Per-device cards: device name, connection state (Connected / Disconnected / Stopped), last scan time, error message when present.

Operator routine:

1. Confirm heartbeat is fresh.
2. Confirm readings-today is increasing.
3. Confirm buffered count is stable or draining.
4. Open any device card for detail when an error appears.

If readings stop, record the exact time, then check Data Transfer and Log Viewer before contacting support.

## 7. Devices in Detail

Devices stores one row per PLC or data source. Only administrators can create, edit or delete devices. Operators and Viewers can only view the list.

### 7.1 Device fields

| Field | Meaning | Example / Default |
|---|---|---|
| Name | Unique display name, used in Live Data, Dashboard and central `PLCName` | `ManualTestPLC` |
| Description | Free text for line, area, cabinet | `Line 1 - packing` |
| Protocol | Driver family | Modbus TCP, Siemens S7, Ethernet/IP |
| Manufacturer | Vendor selector driving model list | Generic, Siemens, Allen-Bradley |
| Model | Specific model profile | Generic Modbus TCP |
| Host / IP | Controller address, test only loopback | `127.0.0.1` |
| Port | TCP port | 502 for Modbus, 102 for S7, 44818 for EtherNet/IP |
| Poll interval ms | Time between scans | 1000 |
| Timeout ms | Connect and read timeout | 3000 |
| Slot | Allen-Bradley / Siemens CPU slot | 0 |
| Rack | Siemens rack | 0 |
| Connection size | Allen-Bradley CIP size, controls batch packing | 511 or 4002 |
| CIP path | Optional explicit route, for example `1,0`. Empty means derived from slot | empty |
| Unit ID | Modbus slave ID | 1 |
| Word order | Modbus 32/64-bit word order | LowHigh |
| One-based addressing | When ON, user addresses are 1-based (40001 style) | ON |
| Capture mode | Manual, PLC Trigger or Logical | Manual |
| Trigger tag | Boolean tag name used in PLC Trigger mode | `CaptureOn` |
| Capture enabled | When ON, scans are stored in historian | ON / OFF |
| Monitor enabled | When ON, live values refresh even without historian storage | ON / OFF |
| Is enabled | Master switch. Disabled devices are ignored by every background worker | ON |

### 7.2 Add device procedure

1. Open Devices and select Add Device.
2. Fill name, protocol, manufacturer, model, host, port, poll interval, timeout and protocol settings.
3. Select capture mode and enable Capture and Monitor as required.
4. Save. The device appears in the list.
5. Open Configure to add address blocks, tags, triggers and transfer servers.

Validation rejects blank names, unknown protocol combinations and duplicate device names.

### 7.3 Edit and delete

- Edit any field and save, then refresh the list to confirm persistence.
- Delete asks for confirmation. Historical readings already stored are retained per retention policy; future scans stop immediately.
- Disabling the master switch is safer than deleting when testing.

### 7.4 Recommended simulation device

For acceptance without hardware:

```text
Name: ManualTestPLC
Protocol: Modbus TCP
Manufacturer: Generic
Model: Generic Modbus TCP
Host: 127.0.0.1
Port: 502
Poll interval: 1000 ms
Timeout: 3000 ms
Unit ID: 1
```

## 8. Address Blocks in Detail

Address blocks generate many sequential tags at once instead of typing each address manually. They apply mainly to Modbus-style register maps.

Fields:

| Field | Meaning | Example |
|---|---|---|
| Address type | Sequential block layout | Sequential |
| Register type | Holding Registers, Input Registers, Coils, Discrete Inputs | Holding Registers |
| Start address | First register number | 1 |
| Data type | How bytes are interpreted | Float32 |
| Point count | How many tags to generate | 5 |
| Tag prefix | Prefix for generated names | TEMP |
| Decimal places | Display rounding | 2 |

Example: start 1, Float32, 5 points, prefix TEMP creates `TEMP001` through `TEMP005` with correct two-register spacing.

Rules:

- Saving validates overlapping blocks and duplicate prefixes.
- Changing a block regenerates affected tags after confirmation.
- Deleting a block asks whether generated tags should also be removed.

## 9. Tags in Detail (PLC, Custom, External)

A device can have three tag kinds. All tags share name, unit, selection for polling, scaling and historian inclusion, but differ in value source.

### 9.1 PLC tags

Direct controller addresses.

Key fields:

- Name, unique per device, for example `TEMP001`.
- PLC address, for example `HR1`, `DB1.DBD0`, or tag name depending on driver. For Modbus tests this is a register address.
- Data type: Bool, Int16, Int32, Float32, Float64, String and others per driver.
- Register type and element count for array reads.
- Scale factor and offset: stored value equals raw times factor plus offset.
- Decimal places for display.
- Selected for polling: OFF is allowed, useful for trigger-only tags that should not flood the historian.
- Write enabled: OFF by default. See Section 11 for safety.
- Minimum, maximum, cooldown ms and readback requirement when writable.

### 9.2 Custom tags

Constant metadata stored with every scan, for example:

```text
Name: LineName
Value: TEST-LINE-01
Included in capture: checked
```

Use custom tags for line, shift, recipe or operator context that the PLC does not provide.

### 9.3 External tags

Values pulled from an external SQL query configured under the device, for example batch number from MES. See Section 16.

An external tag links a query name plus a column name, for example query `CurrentProduction`, column `BatchNo`, tag `CurrentBatch`. During triggered capture the service refreshes external context once per cycle and stores it alongside PLC values.

## 10. Capture Modes and Triggers in Detail

### 10.1 Manual mode

Captures on every poll while Capture is ON. Use for continuous historian recording.

Procedure:

1. Set device capture mode to Manual.
2. Turn Capture ON and Monitor ON from Dashboard or Devices.
3. Wait 15 seconds and open Live Data. Values change and the dashboard count increases.
4. Confirm in SQL that recent rows exist with `Quality = Good`.

### 10.2 PLC Trigger mode

Captures only while a chosen boolean tag is true. Useful for machine-running gates.

Example tag:

```text
Name: CaptureOn
Address: CO:10
Data type: Bool
Selected for polling: OFF allowed
Write enabled: ON, Minimum 0, Maximum 1, Cooldown 500 ms
```

Procedure:

1. Add the boolean tag above.
2. Set device mode to PLC Trigger and select `CaptureOn`.
3. Turn Capture ON.
4. Write 0 to `CaptureOn`, wait 5 seconds and note the reading count.
5. Write 1, wait 10 seconds. Historian rows increase only while the trigger is 1.
6. A successful write shows a readback-verified notification.

### 10.3 Logical mode

Captures only while trigger conditions evaluate to true. Conditions combine tag, operator and value with AND / OR order.

Example:

```text
Tag: TEMP001
Operator: Greater Than
Value: 0
```

Procedure:

1. Select a numeric PLC tag such as `TEMP001`.
2. Add the condition above.
3. Set device mode to Logical and turn Capture ON.
4. Observe rows increasing while the mock value is above 0.
5. Negative test: set value to `999999` so the condition is false. Row growth stops.

Trigger evaluation uses the same expression builder for every scan; misconfigured conditions fail safe to no-capture rather than flooding the database.

### 10.4 Monitor-only mode

Monitor ON plus Capture OFF refreshes live values without historian growth.

Use to verify wiring and scaling before enabling long-term storage:

1. Turn Capture OFF, leave Monitor ON.
2. Note the SQL row count, wait 10 seconds, refresh Live Data and recount.
3. Live values update while the historian count stays nearly flat.

## 11. Live Data, PLC Write Safety and CSV Export in Detail

### 11.1 Live Data page

1. Select a device at the top.
2. Use the text filter to find tags by name, address or value.
3. Columns show tag, address, data type, value, unit, quality and UTC timestamp.
4. Quality `Good` means the last read succeeded. Other states indicate communication or conversion problems.
5. Refresh or leave the page open for automatic updates while Monitor is ON.

### 11.2 PLC write safety

Writes go through five independent guards. All must pass or the write is rejected with a clear message:

1. Global write allowlist: the tag must have Write enabled by an administrator. Default is denied; the Write button is disabled or the service rejects the request.
2. Role check: only Admin can write. Operator and Viewer writes are denied even if enabled on the tag.
3. Type check: the entered text must parse to the tag data type.
4. Range check: numeric values must be within Minimum and Maximum, for example 0 to 100 rejects 150.
5. Cooldown and readback: rapid double writes within cooldown are rejected; after a valid write the driver reads the controller back and confirms the value, for example writing 50 reads back 50.

Test sequence with simulator ON only:

- Disabled tag: write attempt is blocked, driver never executes.
- Out-of-range 150 on a 0-100 tag: rejected with range message.
- Valid 50: success plus readback confirmation.
- Immediate second write: rejected by cooldown.

Never test writes against a real plant controller without permits, interlock review and operations approval.

### 11.3 CSV export

1. Select a device and apply any filter.
2. Select Export CSV and choose a file path.
3. Open the file in Excel or Notepad.

Guarantees:

- Headers are always Device, Tag, AddressOrKind, DataType, Value, Unit, Quality, TimestampUtc.
- Only visible (filtered) rows are exported.
- Unicode and unit symbols are preserved with UTF-8.
- Commas and quotes inside values are escaped so columns never break.
- Spreadsheet formula injection is neutralized by prefixing values starting with `= + - @`.

If export reports file-in-use, close the CSV in Excel or antivirus scan hold and export again. The exporter writes to a temporary file, then moves it with retries for reliability.

## 12. Data Transfer in Detail

Transfer copies local `TagReadings` rows to each enabled central destination, by default table `TagDetail`, using bulk copy inside a transaction with merge-deduplication.

### 12.1 Transfer server fields

| Field | Meaning | Test Value |
|---|---|---|
| Server | SQL instance hosting central DB | `.` |
| Database | Central database | `WTT_Central_Test` |
| Table | Destination table | `TagDetail` |
| Integrated Security | Use Windows auth, no stored password | ON |
| Encrypt | Encrypt the SQL session | ON |
| Trust server certificate | ON only for same-laptop tests; OFF with trusted certs in production | ON for test |
| Enabled | Master switch for this destination | ON |

Use Save plus Test Connection before enabling. A dedicated least-privilege SQL login is recommended in production.

### 12.2 Normal operation

1. Collect 20 seconds of data in Manual capture mode.
2. Open Data Transfer and wait 10 to 20 seconds.
3. Pending count decreases, a successful batch appears, and Last Success updates.
4. Verify centrally that rows exist with non-empty `SourceId` and no duplicate `(SourceId, SourceReadingId)` pairs.

Automatic cycles run on the configured interval. Transfer Now forces one cycle for a single device or all devices.

### 12.3 Resilience

- Transient SQL errors retry with exponential backoff and jitter.
- Repeated failures open a per-server circuit breaker for a cooldown period, isolating that server while others continue.
- Every attempt records a `TransferBatches` row with record count, duration, status and error text.
- Failures never crash the service and never delete local rows. Recovery is automatic once the server is fixed.

### 12.4 Failure drill (safe)

Do not stop production SQL Server. For a safe drill change the test device destination to `invalid-test-server`, keep capture ON, confirm an error batch appears with local rows preserved, then restore server to `.` and database to `WTT_Central_Test` and confirm automatic drain.

## 13. Historian Trends in Detail

The **Trends** page is the SCADA-style multi-pen trend viewer: *"Multi-pen real-time and historical process trends across all PLC machines."* It draws directly from the local historian (`TagReadings`), so it works whether or not the plant network is up.

### 13.1 What is on the page

| Element | Purpose |
|---|---|
| Device selector | Chooses whose tags are listed |
| Tag list with text filter | Find tags by name; each row has a visibility checkbox |
| **Select All / Clear All** | Blue text actions (`LinkBtn`) for bulk pen selection |
| Time-window segments | `1m · 5m · 15m · 30m · 1h` — segmented pills (`SegmentBtn`) in a track |
| **Reset View** | Restores pan/zoom and Y mode |
| **Export CSV** | Royal-blue `ExportBtn` — exports the visible window |
| **Refresh** | Indigo icon + text button, reloads the historical window |
| Live toggle | Follow new data in real time, or freeze the current window |
| Y-mode toggle | **Shared** axis (all pens on one scale) vs **Normalized** (each pen to its own range) |
| Pen legend | Shows pen name, colour, current value and engineering unit; each pen can be hidden independently |

### 13.2 How to use it

1. Select a device.
2. Click **Select All**, or tick individual tags.
3. Choose a time window — `5m` for a recent disturbance, `1h` for a shift overview.
4. Read the pens. Analog pens draw as lines; digital/boolean pens draw as steps in their own lane.
5. Hover the plot to read exact values at a point in time (lookup is binary-searched over the samples).
6. Toggle **Live** on to keep the window sliding forward as new readings arrive.
7. **Export CSV** when you need the numbers.

> PNG export exists in the view-model (`ExportPngCommand`) but its toolbar
> button is currently commented out in `TrendsPage.xaml` — do not document it
> as available until the button is restored.

### 13.3 Things worth knowing

- **Quality gaps break the line.** When readings are missing or Bad quality, the plot stops the pen rather than drawing a straight line across the outage. A gap is real information — do not "fix" it by widening the window.
- **Shared vs Normalized:** Shared shows true engineering magnitude, so a 0–100 % pen and a 0–200 °C pen sit on one axis. Normalized stretches each pen to fill the plot, which is better for comparing shapes rather than values.
- **Historical queries are downsampled.** Long windows fetch up to a bounded point count and are downsampled (LTTB, gap-aware) so the plot stays responsive. Peaks in a very long window are therefore representative, not exhaustive — zoom in for exact values.
- **The trend page and the Logs page survive navigation.** Both are kept alive as singletons, so your pens, filters and time window are still there when you come back from another page.
- **No charting library is used.** The plot is a hand-rendered control (`ScadaTrendPlot`), which is why it supports pan/zoom, digital lanes and quality gaps natively.

### 13.4 Troubleshooting Trends

| Symptom | Likely cause | Action |
|---|---|---|
| Empty plot | No readings in the window, or no tags selected | Widen the time window; press **Select All**; confirm capture is ON |
| Pens flat at zero | Tags have no scaling, or the device is not being polled | Check Live Data for the same tags |
| Gaps everywhere | Communications problems or Bad quality | Check the device card on the Dashboard and the Logs page |
| Only some pens visible | Pens individually hidden in the legend | Re-tick them in the legend |
| Slow to load | Window too wide for the data volume | Use a shorter window, or export CSV for the full range |

## 14. Alarms and Events in Detail (ISA-18.2)

The **Alarms** page is the operator-facing alarm console: active alarms on one tab, searchable history on the other. Alarm *configuration* lives on the tag editor (Admin); alarm *acknowledgement* is an Operator and Admin action.

### 14.1 The two tabs

**Active Alarms**

- Lists alarms currently in `Active` or `Acknowledged` state.
- Columns include priority, type (High/Low), device, tag, trigger value, threshold, unit, duration and who acknowledged it.
- **Acknowledge** on a single row, or **Acknowledge All** in the toolbar.
- **Refresh** reloads the list (indigo icon + text button).
- Rows are colour-coded by priority so Critical and High stand out immediately.

**Alarm History**

- Every alarm that has ever fired, with filters for device, tag, priority and
  state. (Date-range and type filter properties exist in the view-model but no
  date pickers or type selector are bound in the UI yet.)
- **Clear** resets the filter set; **Next Page** / **Prev Page** page through results.
- The header also offers **Save Retention** (apply the retention window) and **Delete Now** (purge immediately, red button). **Delete Now has no role gate in the UI** — anyone who can open the Alarms page can purge history, so treat page access as the control.

### 14.2 Alarm states

| State | Meaning |
|---|---|
| `Active` | Condition is still bad and nobody has acknowledged it |
| `Acknowledged` | An operator has seen it; the condition may or may not still be bad |
| `Cleared` | Condition returned to normal; the row moves to history |
| `Shelved` | Temporarily suppressed — **maximum 24 hours**, and this limit cannot be raised |

Acknowledging does **not** clear an alarm. It records that a human has seen it. The alarm clears only when the process value returns inside the deadband for the off-delay period.

### 14.3 How an alarm fires (what the operator sees)

1. A tag has **Alarm enabled** with a Minimum and/or Maximum threshold, a deadband, on/off delays and a priority (set by an Admin on the tag).
2. The value must stay outside the limit for the **on-delay** seconds before the alarm triggers — momentary spikes do not alarm.
3. On trigger: a row is written, the sidebar **Alarms badge** increments, and the audible beep sounds (Critical = 3 bursts at 1200 Hz, High = 2 bursts at 1000 Hz).
4. The value must return inside the limit **beyond the deadband** for the **off-delay** seconds before it clears.
5. The operator acknowledges; the acknowledgement, user and time are recorded.

**Deadband is in absolute engineering units** (default 2.0). If a High alarm is at 100 with deadband 2, the value must fall below 98 before it clears — this is what stops an alarm flapping on and off at the threshold.

### 14.4 Built-in protections

| Protection | Behaviour |
|---|---|
| Flood suppression | At most **10 alarms per minute per tag** — a broken sensor cannot bury the console |
| Shelving limit | **24 hours maximum**, for safety |
| On/off delay | Configurable per tag to ignore transient spikes |
| Critical alarms | Written directly to the database, bypassing the queue, so they cannot be dropped |
| Acknowledge All | Clears the acknowledgement backlog in one action without deleting history |

### 14.5 Alarm administration (Admin)

On the tag editor → alarm section:

| Setting | Notes |
|---|---|
| Alarm enabled | Master switch for this tag |
| Min threshold | Low alarm |
| Max threshold | High alarm |
| Deadband | Absolute engineering units, 0–50, default 2.0 |
| On-delay / Off-delay seconds | Debounce in and out |
| Priority | Critical / High / Medium / Low (default Medium) |
| Description | Shown to the operator |

Validation: minimum < maximum, at least one threshold set, deadband within 0–50.

Retention is configured in **Settings → Security & retention** (`Alarm retention days`, default 90; `0` keeps history forever; values outside 0–3650 are rejected). Purging runs at startup and then every 24 hours. Audible alarms are toggled by the **Beep** checkbox in the Alarms page header (applies to Critical/High only).

> Note: the beep choice is written to `Alarm.BeepEnabled` but is not re-read after a restart — the app falls back to the configured default until the checkbox is toggled again.

### 14.6 Troubleshooting Alarms

| Symptom | Likely cause | Action |
|---|---|---|
| No alarms firing | Alarm not enabled on the tag, or no threshold set | Open the tag editor and check the alarm section |
| Alarms firing constantly | Threshold outside normal range, or deadband too small | Widen the deadband; review thresholds against real values |
| Alarm never clears | Off-delay too long, or value genuinely still out of range | Watch Live Data; verify the off-delay |
| No beep | Beep disabled, or priority below High | Settings → enable audible alarm; only Critical/High beep |
| Sidebar badge missing | No active alarms, or page not refreshed | Press Refresh on the Alarms page |
| History missing old rows | Retention purge | Raise `Alarm retention days` |

## 15. Log Viewer in Detail

The Logs page has **two tabs**:

| Tab | Shows | Columns |
|---|---|---|
| **Application log** | UI logs parsed from `%LocalAppData%\WTT\Logs` | Time, Level, Source, Message |
| **Audit trail** | Security-relevant actions recorded in the database | Time, User, Action |

Service logs live in `%ProgramData%\WTT\Logs` and are referenced from the same screen.

- Filter by level: Information for normal flow, Warning for retries and recoverable faults, Error for failures needing action.
- Search for `transfer`, `login`, `capture`, device name or tag name.
- Select **Refresh** after changing filters (indigo icon + text button).
- **Open folder** launches the log directory directly when you need to attach files.
- **Copy:** there is no Copy toolbar button — select lines and press **Ctrl+C** (hint shown under the grid).
- **Auto-refresh toggle** (`Live (30s)` / `Auto-refresh`) keeps the log reloading every 30 seconds without pressing Refresh.

The log page survives navigation — your filters are still applied when you come back to it.

Privacy: passwords and full connection strings are never logged. Usernames, device names and error messages are logged for diagnosis.

## 16. User Management in Detail (Admin Only)

1. Open Users as Admin.
2. Select **Add user** and enter username (unique), full name, role and strong temporary password.
3. Save. The user appears as active and is forced to change the temporary password at first login.
4. Row actions:
   - **Edit** (blue) — change full name, role or active state.
   - **Reset password** (amber key icon) — issue a new temporary password.
   - **Unlock** (violet) — release an account that locked out after 5 failed logins.
   - **Delete** (red trash) — remove the login; audit rows are preserved.
5. Disable preserves history; delete removes the login but preserves audit rows.
6. The last active administrator cannot be deleted or demoted. The UI rejects the action with an explanation.

Example test accounts:

```text
operator_test / Operator
viewer_test / Viewer
```

Sign in as each and confirm:

- Operator: dashboard and monitoring work; Add Device, user management, settings and PLC writes are denied.
- Viewer: views work per permission matrix; capture control, configuration and writes are denied.
- Admin: full access succeeds.

Never write test passwords in reports.

## 17. Settings in Detail

Settings are grouped into sections on one page. Runtime settings are stored in the local database and apply **immediately — no restart needed**.

**Not admin-only:** *My Account* and *Appearance* are available to every role. Only *Simulation*, *Security & retention* and the database section are gated by `CanManage` (Admin).

| Section | Setting | Meaning | Default / Test |
|---|---|---|---|
| **My Account** | Change password / Logout | Your own credentials | — |
| **Appearance** | Theme | Dark or Light, applied instantly | Dark |
| **Simulation** | PLC simulator | ON uses the deterministic Mock PLC; OFF uses real drivers | ON for tests |
| **Security & retention** | Session timeout minutes | Idle auto-logout | 30, 1 for timeout test then restore |
| | Transfer interval seconds | Delay between automatic transfer cycles | 5 |
| | Transferred retention days | Age after which transferred readings are cleaned | 7 |
| | Untransferred max age days | Safety purge horizon for never-transferred rows, with warning | 0 means disabled |
| | Alarm retention days | Age after which alarm history is purged | 90, 0 means keep forever |
| **Log File Generation** | Enable file logging | Toggle log file writing at runtime without restarting | ON |
| **Local database** | Edition and size | Read-only display of server, edition and data size | `DESKTOP... / WTT`, Express Edition |
| **About** | Version and components | Product version and build information | 1.2.0 |

Save shows a success notification. Settings persist in `AppSettings` and survive restarts. Changing simulator mode or capture settings triggers an immediate worker reconcile — within about 5 seconds the workers restart with the new configuration.

## 18. External SQL Queries in Detail

External queries attach MES or ERP context to historian rows without allowing writes through WTT.

Strict rules:

- Exactly one statement, must parse as SELECT. Multiple statements, DROP, INSERT, UPDATE, DELETE and stacked queries are rejected at save and verify time.
- Use a dedicated SQL account with SELECT permission only.
- Encrypt the session. Trust-server-certificate ON is allowed only for same-laptop tests.

Example (test only):

```text
Name: CurrentProduction
Server: .
Database: WTT_Central_Test
Integrated Security: ON
Encrypt: ON
Trust server certificate: ON
Query:
SELECT TOP (1) BatchNo, Shift, Recipe
FROM dbo.ProductionContext
ORDER BY UpdatedAtUtc DESC;
```

After Save, select Verify. Discovered columns such as `BatchNo`, `Shift`, `Recipe` appear. Then create an external tag:

```text
Name: CurrentBatch
Query: CurrentProduction
Column: BatchNo
```

During triggered capture the value should read `BATCH-TEST-001`.

Negative test:

```sql
SELECT 1; DROP TABLE dbo.ProductionContext;
```

Expected: rejected, table intact.

## 19. Service Behavior, Restart and Data Safety

- UI closed, service running: capture and transfer continue. Reopen the UI to see fresh timestamps with no gap.
- Service restarted: heartbeat gets a new instance ID and start time; stale instances never show as connected.
- Machine restarted: users, devices, tags, settings and history persist; workers reconcile automatically.
- Retention hourly job removes transferred readings older than the configured days, 30-day-old batch records and year-old audit rows. It purges never-transferred rows only when an explicit max age is configured, with a warning.
- SQL Express 10 GB ceiling: the database is provisioned near 9.5 GB with fixed growth. Alert at 8 GB. Stop new capture before 9 GB if transfer cannot drain. Never silently delete untransferred data.

Backup (summary, see SQL guide for exact ACLs):

```powershell
cd "D:\Spring Boot\PLC Project\WTT_PLC\New PLc"
.\deploy\Backup-Wtt.ps1
```

Expected: `The backup set ... is valid`, file under `C:\ProgramData\WTT\Backups`. Keep a second encrypted off-machine copy and run restore drills; `VERIFYONLY` alone is not a disaster-recovery test.

## 20. Database Tables - Simple Meaning

Do not delete tables manually. Fresh databases start empty and gain rows as features are used.

| Table | Simple Meaning | Row Created When |
|---|---|---|
| `__EFMigrationsHistory` | Database version record | Always 6 migration rows |
| `Users` | Login users and roles | First Admin and new users |
| `Devices` | PLC and device configuration | Device Save |
| `DeviceAddresses` | Modbus address blocks | Address block Save |
| `DeviceTags` | PLC, Custom and External tags | Tag Save, generate or import |
| `DeviceTriggerConditions` | Logical trigger rules | Trigger condition Add |
| `DeviceQueries` | External read-only SQL configs | External query Save |
| `DeviceQueryColumns` | Verified query columns | Query Verify |
| `DeviceServers` | Central transfer destinations | Transfer server Save |
| `TagReadings` | Captured historical values | Capture ON and trigger true |
| `TransferBatches` | Transfer success and failure history | Every central attempt |
| `AuditLogs` | Login, config and write security history | User actions |
| `AppSettings` | Theme, simulator, timeouts, retention | Settings Save |
| `ServiceRuntimeState` | Service heartbeat | Service start, exactly 1 row |
| `DeviceRuntimeStates` | Per-device connection and scan status | Enabled worker start |

Fresh-start counts: Users 0, Devices 0, Tags 0, Readings 0, Migrations 6. After first Admin: Users 1, others still 0 except migrations.

## 21. Daily Operating Procedures

Morning checks (Operator, 2 minutes):

1. Open Dashboard, confirm heartbeat fresh.
2. Confirm readings-today increased since yesterday.
3. Confirm buffered count is low and not climbing.
4. Acknowledge any device error card.

Configuration change (Admin):

1. Disable or pause capture for the affected device when practical.
2. Edit device, tags, triggers or servers and save.
3. Confirm success notification and refresh persistence.
4. Re-enable capture and watch one poll cycle in Live Data.
5. Check audit log for the change record.

Central handover:

1. Confirm Last Success is recent.
2. Export CSV for the shift if required.
3. Attach transfer batch ID and time range to the handover note.

End of test or shift:

1. Turn Capture and Monitor OFF for test devices.
2. Restore session timeout to 30 minutes.
3. Disable test transfer servers if no further testing is planned.
4. Stop debugging with Shift+F5; preserve `WTT_Central_Test` until results are reviewed.

## 22. Verification Queries for Support

Run in SSMS with Windows Authentication against `.`.

Fresh-start counts:

```sql
USE WTT;
SELECT (SELECT COUNT(*) FROM dbo.Users) AS Users,
       (SELECT COUNT(*) FROM dbo.Devices) AS Devices,
       (SELECT COUNT(*) FROM dbo.DeviceTags) AS Tags,
       (SELECT COUNT(*) FROM dbo.TagReadings) AS Readings,
       (SELECT COUNT(*) FROM dbo.__EFMigrationsHistory) AS Migrations;
```

Recent readings:

```sql
USE WTT;
SELECT TOP (20) DeviceName, TagName, Value, Quality, TimestampUtc
FROM dbo.TagReadings
WHERE DeviceName = N'ManualTestPLC'
ORDER BY Id DESC;
```

Monitor-only check:

```sql
SELECT COUNT(*) FROM WTT.dbo.TagReadings WHERE DeviceName = N'ManualTestPLC';
```

Central rows:

```sql
USE WTT_Central_Test;
SELECT TOP (20) TagName, PLCName, TagValue, TimeStampUTC, Quality, SourceId, SourceReadingId
FROM dbo.TagDetail
ORDER BY Pk_TagDetail DESC;
```

Duplicate check, must return zero rows:

```sql
SELECT SourceId, SourceReadingId, COUNT(*) AS Copies
FROM dbo.TagDetail
WHERE SourceId IS NOT NULL
GROUP BY SourceId, SourceReadingId
HAVING COUNT(*) > 1;
```

Migrations and heartbeat:

```sql
USE WTT;
SELECT MigrationId, ProductVersion FROM dbo.__EFMigrationsHistory ORDER BY MigrationId;
SELECT TOP (20) * FROM dbo.ServiceRuntimeState;
SELECT TOP (20) * FROM dbo.DeviceRuntimeStates;
SELECT COUNT(*) AS Pending FROM dbo.TagReadings WHERE IsTransferred = 0;
```

## 23. Troubleshooting Matrix

| Symptom | Likely Cause | Action |
|---|---|---|
| Login window blank or app exits at startup | SQL stopped, bad theme init | Start SQL Server, restart service then UI |
| Cannot sign in, valid user | Wrong password, locked, disabled | Retry once, then admin reset; check Users IsActive |
| Dashboard heartbeat stale | Service stopped or crashed | Start `WTTData`, check service logs |
| No live values | Monitor OFF, device disabled, simulator OFF | Enable Monitor and device, set simulator ON for tests |
| Live updates but no history | Capture OFF or trigger false | Enable Capture; set Manual or drive trigger true |
| Trigger never fires | Wrong tag, operator or threshold | Check condition, test with low threshold like > 0 |
| Write blocked | Write not enabled, wrong role, range, cooldown | Admin enable, check min/max, wait cooldown, retry |
| Pending keeps growing | Bad server name, auth, network, full disk | Check batch error, fix destination, wait auto-recovery |
| Duplicate central rows suspected | Manual re-insert bypassing merge | Run duplicate query; normal pipeline is idempotent |
| CSV export fails | File open in Excel, locked by scanner | Close file, export again |
| Theme does not change | Settings not saved | Save settings, watch success toast |
| Window overlap at small size | Narrow width | Maximize or scroll grid horizontally |
| Slow startup over 2 seconds | SQL load, disk, many workers | Check SQL, disk space, poll intervals |
| 8 GB database warning | Transfer behind | Fix transfer, extend retention review, never delete pending silently |

## 24. Safety Rules

- Use only `127.0.0.1` or clearly fake IPs for tests. Never enter a real plant PLC IP in a test system.
- Keep PLC simulator ON for acceptance. Turn OFF only for supervised commissioning.
- Test writes on simulator only. Real writes need permits and interlock review.
- Use only `WTT_Central_Test` for transfer tests. Never point tests at production MES.
- Never manually delete `WTT`, `ATIDAQ` or other databases.
- Never force-stop Windows or SQL services mid-cycle. Stop capture normally first.
- Turn Capture and Monitor OFF after tests.
- Preserve test users and central test data until results are reviewed.

## 25. Support Package - What to Send

When data stops or an error persists, send:

1. Exact date, time and timezone plus device and tag names.
2. Screenshot of Dashboard heartbeat and device card.
3. Screenshot of Data Transfer pending and last batch error.
4. UI logs from `%LocalAppData%\WTT\Logs` and service logs from `%ProgramData%\WTT\Logs` covering the incident window.
5. Pending count query output and central duplicate-check output when transfer is involved.
6. Steps already tried and their results.

Do not include passwords, private keys or full production connection strings.

## 26. Glossary

- Capture: storing polled scans in the local historian.
- Monitor: refreshing live values without historian storage.
- Historian buffer: local `TagReadings` table holding data until transfer.
- Transfer: bulk copy from local buffer to central `TagDetail`.
- Batch: one transfer attempt with count, duration and status.
- Heartbeat: periodic service alive row with instance ID.
- Idempotent: safe to retry without duplicates.
- Simulator (Mock PLC): built-in random data source for testing without hardware.
- Trigger: boolean or logical gate controlling when capture stores rows.
- Audit log: security record of logins, config changes and writes.

## 27. Document History

| Version | Date | Change |
|---|---|---|
| 1.0 | 2026-09-12 | Initial production hardening user guide. |
| 1.1 | 2026-09-13 | Added runtime status, deployment artifacts and manual test references. |
| 1.2 | 2026-09-16 | Complete rewrite: full field reference, procedures, verification SQL, safety and support package. |
| 1.2.0 | Current | Matches release 1.2.0, mock acceptance complete, real-hardware commissioning still required. |
| 1.2.0 | 2026-10-09 | Deep update: full sidebar/page reference (§5), new §13 Historian Trends and §14 Alarms (ISA-18.2), Settings sections (§17), Logs tabs and user row actions (§15–16), roles table extended with alarm permissions, cross-links to ARCHITECTURE / ADMIN_GUIDE / UI_STYLE_GUIDE. |
