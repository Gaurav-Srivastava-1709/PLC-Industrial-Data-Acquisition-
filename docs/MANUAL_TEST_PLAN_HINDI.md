# WTT Complete Manual Test Plan (Real PLC Ke Bina)

Version: 1.2.0  
Scope: Phase 1 se Phase 6, Visual Studio + Mock PLC + local SQL Server Express  
Rule: Is test me kisi real machine ya production PLC IP ka use nahi karna hai.

## 1. Aap Kya Verify Kar Paoge

Mock PLC ke saath ye features fully test ho sakte hain:

- Secure startup, login, logout, password policy aur session timeout.
- Admin, Operator aur Viewer permissions.
- Device, address block, tag, trigger, external query aur transfer configuration.
- Manual, PLC Trigger aur Logical capture modes.
- Monitoring, historian capture, live values aur dashboard chart.
- Safe PLC write: allowlist, type, minimum/maximum, cooldown aur readback.
- Local SQL Server persistence, central SQL transfer, retry display aur idempotency.
- CSV export, logs, theme, keyboard shortcuts aur responsive window.
- UI band hone ke baad Visual Studio service process me capture continue rehna.
- Restart ke baad configuration/data persistence.

Real PLC ke bina ye certify nahi hoga:

- Physical PLC cable/network.
- Real Siemens, Allen-Bradley ya Modbus firmware communication.
- Actual plant address map aur safety interlocks.
- Real controller par write/readback.
- 24x7 plant load aur hardware failure behavior.

Isliye ye complete software acceptance test hai, real-hardware commissioning test nahi.

## 2. Safety Rules

- Sirf `127.0.0.1` ya clearly fake IP use karein.
- Settings me `PLC simulator ON` confirm karein.
- Real plant PLC ka IP kabhi enter na karein.
- `WTT`, `ATIDAQ` ya doosre databases manually delete na karein.
- SQL test ke liye sirf `WTT_Central_Test` use karein.
- Test complete hone par Capture aur Monitor OFF karein.
- Kisi Windows/SQL service ko force-stop na karein.

## 3. Required Software Check

Checklist:

- [ ] Visual Studio installed hai.
- [ ] `.NET desktop development` workload installed hai.
- [ ] SQL Server service running hai.
- [ ] SSMS open ho raha hai.
- [ ] Project path available hai: `D:\Spring Boot\PLC Project\WTT_PLC\New PLc`.

SQL Server check:

1. Windows Start me `Services` search karein.
2. `SQL Server (MSSQLSERVER)` locate karein.
3. Status `Running` hona chahiye.
4. Ise test ke beech manually stop na karein.

## 4. Test Se Pehle Backup

PowerShell me run karein:

```powershell
cd "D:\Spring Boot\PLC Project\WTT_PLC\New PLc"
.\deploy\Backup-Wtt.ps1
```

Expected:

- `The backup set ... is valid.` message.
- Backup `C:\ProgramData\WTT\Backups` me create hoga.

Result:

- [ ] PASS
- [ ] FAIL
- Notes: ______________________________

## 5. Central Test Database Banana

1. SSMS open karein.
2. Server name me `.` enter karke Windows Authentication se connect karein.
3. Menu `File > Open > File` select karein.
4. File open karein: `New PLc\deploy\Create-Manual-Test-Databases.sql`.
5. Toolbar me `Execute` click karein.

Expected result:

- Result me database `WTT_Central_Test` dikhega.
- Row me `BATCH-TEST-001`, shift `A`, recipe `MOCK-RECIPE` dikhega.
- SSMS Object Explorer me Databases par right-click `Refresh` karne par `WTT_Central_Test` dikhega.

- [ ] PASS
- [ ] FAIL
- Notes: ______________________________

## 6. Visual Studio Me Project Open Karna

1. Visual Studio open karein.
2. `Open a project or solution` click karein.
3. File select karein: `D:\Spring Boot\PLC Project\WTT_PLC\New PLc\WTT.slnx`.
4. Solution Explorer me ye projects dikhne chahiye:
   - `WTT.Domain`
   - `WTT.Application`
   - `WTT.Infrastructure`
   - `WTT.Service`
   - `WTT.UI`
   - `WTT.Tests`
5. Visual Studio ko restore complete karne dein.
6. Menu `Build > Build Solution` click karein.

Expected:

- Build successful.
- `6 succeeded, 0 failed` aaye.
- Current baseline me `0 Warning(s), 0 Error(s)` aana chahiye.

- [ ] PASS
- [ ] FAIL
- Notes: ______________________________

## 7. UI Aur Service Ek Saath Start Karna

Sabse easy aur recommended production-style method:

1. Visual Studio me `Build > Rebuild Solution` complete karein.
2. Visual Studio debugger me koi WTT process running na ho; zarurat par `Shift+F5`.
3. Windows File Explorer me `New PLc\deploy` folder open karein.
4. `Run-Manual-Test.cmd` double-click karein. Terminal ek pal flash karke automatically close ho sakti hai.
5. Launcher service ko hidden start karega aur sirf WTT UI dikhayega.
6. WTT UI close karne par hidden test service bhi stop ho jayegi.

Is method me Visual Studio XAML debugger ka black-screen overlay nahi aata.

Alternative Visual Studio debugger method:

Preferred method:

1. Visual Studio top toolbar me startup-profile dropdown dekhein.
2. `WTT Full System - Mock PLC` select karein.
3. Green Start button ya `F5` press karein.

Agar profile dropdown me nahi dikhe:

1. Solution par right-click karein.
2. `Configure Startup Projects` select karein.
3. `Multiple startup projects` select karein.
4. `WTT.Service` ke saamne `Start` select karein.
5. `WTT.UI` ke saamne `Start` select karein.
6. Baaki projects ke saamne `None` rakhein.
7. `Apply`, phir `OK`.
8. `F5` press karein.

Expected:

- Ek console/service process start hoga. Use band na karein.
- `WTT.Service` ki black console window normal hai; service background me chal rahi hoti hai aur us window me text aana zaroori nahi.
- WTT startup screen dikhegi.
- Uske baad login window dikhegi.
- Dashboard login ke baad service heartbeat `running` dikhayega.

- [ ] PASS
- [ ] FAIL
- Notes: ______________________________

## 8. Login Test

Fresh reset ke baad koi user present nahi hai. Product me shared default password nahi hai.

### TC-AUTH-00 First Administrator Banana

1. Startup screen ke baad `Create the first administrator` window aani chahiye.
2. Username me `admin` enter karein.
3. Full name me apna naam ya `Test Administrator` enter karein.
4. Password me apna strong private password enter karein.
5. Confirm password me wahi password dobara enter karein.
6. `Create administrator` click karein.
7. Password kisi screenshot, report ya shared message me na likhein.

Strong password me minimum 8 characters, uppercase, lowercase aur digit hona chahiye. Example format samajhne ke liye: `Word#2026`, lekin apna alag password use karein.

Expected:

- Admin creation successful ho.
- Login screen aaye.
- SQL table `WTT.dbo.Users` me exactly 1 active Admin row bane.
- Password plaintext me database me na dikhe; sirf BCrypt hash store ho.

SSMS verification:

```sql
USE WTT;
SELECT Username, FullName, Role, IsActive, MustChangePassword, PasswordChangedAtUtc
FROM dbo.Users;
```

Expected: `admin`, role `Admin`, `IsActive=1`, `MustChangePassword=0`.

- [ ] PASS
- [ ] FAIL
- Notes: ______________________________

### TC-AUTH-01 Valid Login

1. Username `admin` enter karein.
2. Apna existing password enter karein.
3. `Sign in` click karein.

Expected: Main dashboard open ho.

- [ ] PASS
- [ ] FAIL

### TC-AUTH-02 Wrong Password

1. Logout karein.
2. Username `admin`, wrong password enter karein.

Expected: Login reject ho; application crash na ho.

- [ ] PASS
- [ ] FAIL

### TC-AUTH-03 Logout

1. Main screen par `Sign out` click karein.
2. Confirmation accept karein.

Expected: Login screen wapas aaye.

- [ ] PASS
- [ ] FAIL

### TC-AUTH-04 Keyboard Shortcuts

1. Login karke kisi doosre page par jayen.
2. `Ctrl+D` press karein.
3. `Ctrl+L` press karein.

Expected: `Ctrl+D` Dashboard khole; `Ctrl+L` confirmation ke baad logout kare.

- [ ] PASS
- [ ] FAIL

Password yaad nahi ho to repeated guesses na karein, account lock ho sakta hai. Testing rok kar password reset karwayein.

## 8A. Database Tables Ka Simple Meaning

Tables delete nahi karni hain. Fresh test me tables empty rehti hain aur features use karne par rows aati hain.

| Table | Simple meaning | Row kab banegi |
|---|---|---|
| `__EFMigrationsHistory` | Database version record | Hamesha 3 migration rows rahengi |
| `Users` | Login users and roles | First Admin aur naye users banane par |
| `Devices` | PLC/device configuration | Device Save karne par |
| `DeviceAddresses` | Modbus address blocks | Address block Save karne par |
| `DeviceTags` | PLC, Custom aur External tags | Tag Save/generate/import karne par |
| `DeviceTriggerConditions` | Logical trigger rules | Trigger condition add karne par |
| `DeviceQueries` | External read-only SQL query config | External query Save karne par |
| `DeviceQueryColumns` | Verified query ke returned columns | Query Verify karne par |
| `DeviceServers` | Central transfer destination | Central server config Save karne par |
| `TagReadings` | Captured historical values | Capture ON aur trigger true hone par |
| `TransferBatches` | Transfer success/failure history | Central transfer attempt ke baad |
| `AuditLogs` | Login/config/write security history | User actions ke baad |
| `AppSettings` | Theme, simulator, timeout, retention | Settings Save karne par |
| `ServiceRuntimeState` | Service heartbeat | WTT Service start hone par exactly 1 row |
| `DeviceRuntimeStates` | Device connection/scan status | Enabled device worker start hone par |

Fresh-start expected counts:

```sql
USE WTT;
SELECT (SELECT COUNT(*) FROM dbo.Users) AS Users,
       (SELECT COUNT(*) FROM dbo.Devices) AS Devices,
       (SELECT COUNT(*) FROM dbo.DeviceTags) AS Tags,
       (SELECT COUNT(*) FROM dbo.TagReadings) AS Readings,
       (SELECT COUNT(*) FROM dbo.__EFMigrationsHistory) AS Migrations;
```

First Admin se pehle expected: Users `0`, Devices `0`, Tags `0`, Readings `0`, Migrations `3`.
First Admin ke baad expected: Users `1`, baaki application data `0`, Migrations `3`.

## 9. Settings Aur Mock PLC

1. Admin se login karein.
2. Settings page open karein.
3. Confirm karein `PLC simulator ON` hai.
4. Agar OFF hai to ON karein aur warning accept karein.
5. `Save settings` click karein.
6. Theme Dark se Light aur Light se Dark karein.

Expected:

- Success notification aaye.
- Theme turant change ho.
- SQL section me `DESKTOP... / WTT` aur `Express Edition` dikhe.

- [ ] PASS
- [ ] FAIL

## 10. Device CRUD Test

### TC-DEV-01 Validation

1. Devices > Add Device.
2. Name blank rakh kar Save karein.

Expected: Validation message; blank device save na ho.

- [ ] PASS
- [ ] FAIL

### TC-DEV-02 Test Device Create

Device details:

```text
Name: ManualTestPLC
Protocol: Modbus TCP
Manufacturer: Generic
Model: Generic Modbus TCP
Host/IP: 127.0.0.1
Port: 502
Poll interval: 1000 ms
Timeout: 3000 ms
Unit ID: 1
```

Save karein.

Expected: Device list me `ManualTestPLC` aaye.

- [ ] PASS
- [ ] FAIL

### TC-DEV-03 Edit Persistence

Description ya poll interval change karke Save karein, page refresh karein.

Expected: Updated value retained ho.

- [ ] PASS
- [ ] FAIL

## 11. Address Block Aur Generated Tags

1. `ManualTestPLC` Configure open karein.
2. Address Blocks me Add karein.
3. Values:

```text
Address type: Sequential
Register type: Holding Registers
Start address: 1
Data type: Float32
Point count: 5
Tag prefix: TEMP
Decimal places: 2
```

4. Save karein.

Expected:

- `TEMP001` se `TEMP005` tak 5 tags generate hon.
- Addresses two-register spacing ke saath generate hon.
- Duplicate prefix/block par validation error aaye.

- [ ] PASS
- [ ] FAIL

## 12. Custom Tag Test

1. Tags me Add Custom Tag.
2. Name `LineName`.
3. Value `TEST-LINE-01`.
4. Included in capture checked rakhein.
5. Save.

Expected: Tag list me custom tag aaye.

- [ ] PASS
- [ ] FAIL

## 13. Manual Capture Test

1. Device capture mode `Manual` select karein.
2. Dashboard par `ManualTestPLC` ke liye Capture ON aur Monitor ON karein.
3. 15 seconds wait karein.
4. Live Data page open karein.

Expected:

- Service heartbeat running.
- Device connected dikhe, kyunki Mock PLC ON hai.
- TEMP tags ki changing values dikhein.
- `LineName` ka value `TEST-LINE-01` ho.
- Dashboard readings count increase ho.
- Chart line update ho.

- [ ] PASS
- [ ] FAIL

SSMS verification:

```sql
USE WTT;
SELECT TOP (20) DeviceName, TagName, Value, Quality, TimestampUtc
FROM dbo.TagReadings
WHERE DeviceName = N'ManualTestPLC'
ORDER BY Id DESC;
```

Expected: Recent rows, `Quality=Good`, same scan me multiple tags.

- [ ] PASS
- [ ] FAIL

## 14. Monitor-Only Test

1. Capture OFF karein.
2. Monitor ON rakhein.
3. SSMS me current count note karein:

```sql
SELECT COUNT(*) FROM WTT.dbo.TagReadings WHERE DeviceName=N'ManualTestPLC';
```

4. 10 seconds wait karein.
5. Live Data refresh karein aur SQL count dobara run karein.

Expected:

- Live values update hon.
- Historian row count significantly increase na ho.

- [ ] PASS
- [ ] FAIL

## 15. PLC Trigger Test With Mock PLC

1. Ek PLC tag add karein:

```text
Name: CaptureOn
Address: CO:10
Data type: Bool
Selected for polling: OFF is acceptable for trigger
Write enabled: ON
Minimum: 0
Maximum: 1
Cooldown: 500 ms
```

2. Device mode `PLC Trigger` select karein.
3. Trigger tag `CaptureOn` select karein.
4. Capture ON karein.
5. Live Data me `CaptureOn` par Write `0` karein.
6. 5 seconds wait karein aur reading count note karein.
7. Write `1` karein.
8. 10 seconds wait karein.

Expected:

- Trigger 0 par historian capture stop/idle.
- Trigger 1 par rows increase.
- Write ke baad readback verified success notification.

- [ ] PASS
- [ ] FAIL

## 16. Logical Trigger Test

1. Device me numeric PLC tag select karein, for example `TEMP001`.
2. Trigger condition add karein:

```text
Tag: TEMP001
Operator: Greater Than
Value: 0
```

3. Device mode `Logical` select karein.
4. Capture ON karein.
5. 10 seconds observe karein.

Expected: Mock numeric value 0 se greater hone par capture rows increase.

Negative test: value bahut high, jaise `999999`, set karein.

Expected: Condition false; capture rows stop increasing.

- [ ] PASS
- [ ] FAIL

## 17. PLC Write Safety Test

Sirf Mock PLC ON hone par karein.

### TC-WRITE-01 Default Deny

1. Kisi tag me `Write enabled` OFF rakhein.
2. Live Data me Write try karein.

Expected: Button disabled ya service rejection. Driver write execute na ho.

- [ ] PASS
- [ ] FAIL

### TC-WRITE-02 Range Rejection

1. Writable numeric tag minimum `0`, maximum `100` set karein.
2. Value `150` write karein.

Expected: Reject; clear range message.

- [ ] PASS
- [ ] FAIL

### TC-WRITE-03 Valid Readback

Value `50` write karein.

Expected: Write success and mock controller readback `50`.

- [ ] PASS
- [ ] FAIL

### TC-WRITE-04 Cooldown

Same tag ko immediately do baar write karein.

Expected: Second rapid write reject ho.

- [ ] PASS
- [ ] FAIL

## 18. External SQL Query Test

1. `ManualTestPLC` configuration me External Query add karein.
2. Details:

```text
Name: CurrentProduction
Server: .
Database: WTT_Central_Test
Integrated Security: ON
Encrypt: ON
Trust server certificate: ON (sirf same-laptop test ke liye)
Query:
SELECT TOP (1) BatchNo, Shift, Recipe
FROM dbo.ProductionContext
ORDER BY UpdatedAtUtc DESC;
```

3. Save, phir Verify/Test click karein.

Expected: Columns `BatchNo`, `Shift`, `Recipe` discover hon.

4. External tag banayein:

```text
Name: CurrentBatch
Query: CurrentProduction
Column: BatchNo
```

Expected: Triggered capture me value `BATCH-TEST-001` aaye.

- [ ] PASS
- [ ] FAIL

Security negative test query:

```sql
SELECT 1; DROP TABLE dbo.ProductionContext;
```

Expected: Save/verify reject ho. Table delete nahi honi chahiye.

- [ ] PASS
- [ ] FAIL

## 19. Central Data Transfer Test

1. Device configuration > Central Transfer Server.
2. Values:

```text
Server: .
Database: WTT_Central_Test
Table: TagDetail
Integrated Security: ON
Encrypt: ON
Trust server certificate: ON (same-laptop test only)
Enabled: ON
```

3. Save/Test Connection.
4. Device Manual capture mode me 20 seconds data collect karein.
5. Data Transfer page open karein.
6. Automatic transfer ke liye 10-20 seconds wait karein.

Expected:

- Pending count reduce ho.
- Successful batch history aaye.
- Last success update ho.

SSMS:

```sql
USE WTT_Central_Test;
SELECT TOP (20) TagName, PLCName, TagValue, TimeStampUTC, Quality, SourceId, SourceReadingId
FROM dbo.TagDetail
ORDER BY Pk_TagDetail DESC;
```

Expected:

- Rows present.
- `SourceId` empty na ho.
- Same source/read ID duplicate na ho.

Duplicate check:

```sql
SELECT SourceId, SourceReadingId, COUNT(*) AS Copies
FROM dbo.TagDetail
WHERE SourceId IS NOT NULL
GROUP BY SourceId, SourceReadingId
HAVING COUNT(*) > 1;
```

Expected: Zero rows.

- [ ] PASS
- [ ] FAIL

## 20. Transfer Failure And Recovery

Production SQL Server ko stop na karein.

1. Test device central server temporarily `invalid-test-server` set karein.
2. Capture ON rakhein.
3. Data Transfer page observe karein.

Expected:

- Application/service crash na ho.
- Error batch/message aaye.
- Local pending rows safe rahen.

4. Server name wapas `.` aur database `WTT_Central_Test` karein.
5. Wait karein.

Expected: Automatic recovery; pending data transfer ho.

- [ ] PASS
- [ ] FAIL

## 21. CSV Export

1. Live Data page me device select karein.
2. Filter optional.
3. `Export CSV` click karein.
4. File save karein.
5. Excel ya Notepad me open karein.

Expected:

- Headers correct.
- Visible rows exported.
- Unicode/unit values correct.
- Commas/quotes se columns break na hon.

- [ ] PASS
- [ ] FAIL

## 22. Dashboard And Responsive UI

1. Dashboard open karein.
2. Window maximize/unmaximize karein.
3. Width approximately 720 px tak reduce karein.
4. Dashboard, Live Data, Transfer aur Settings pages scroll karein.

Expected:

- Controls overlap/cut na hon.
- KPI cards 3-column layout me readable hon.
- Chart visible aur bounded ho.
- Horizontal grid scrolling available ho.

- [ ] PASS
- [ ] FAIL

## 23. User And Role Tests

Admin se users banayein:

```text
operator_test / Operator
viewer_test / Viewer
```

Strong temporary passwords use karein.

Operator expected:

- Dashboard/live monitoring accessible.
- Device configuration/user management denied.

Viewer expected:

- View pages accessible according to permission matrix.
- Capture control, configuration and PLC writes denied.

Admin expected:

- Full authorized access.
- Last active admin delete/demote reject ho.

- [ ] PASS
- [ ] FAIL

Test users ke passwords report me kabhi na likhein.

## 24. Session Timeout

Fast test:

1. Admin Settings me Session Timeout `1` minute set karein.
2. Save.
3. Koi mouse/keyboard activity na karein.

Expected: Approximately 1 minute ke baad login screen.

Test ke baad timeout `30` minutes restore karein.

- [ ] PASS
- [ ] FAIL

## 25. Log Viewer

1. Logs page open karein.
2. Level filters use karein.
3. Search me `transfer`, `login` ya `capture` type karein.
4. Refresh karein.

Expected:

- UI logs `%LocalAppData%\WTT\Logs` se dikhein.
- Service log file `%ProgramData%\WTT\Logs` me create ho.
- Password ya full connection string logs me na ho.

- [ ] PASS
- [ ] FAIL

## 26. UI Close, Service Continue Test

Visual Studio multiple-start mode me:

1. Device Monitor ON karein.
2. UI window close karein, lekin service console/process ko running rakhein.
3. 15 seconds wait karein.
4. Visual Studio se sirf WTT.UI dobara start karein.

Expected:

- Service heartbeat running raha.
- Live tag timestamps UI band hone ke dauran bhi update hue.
- Duplicate capture pipeline create nahi hui.

- [ ] PASS
- [ ] FAIL

## 27. Restart Persistence

1. Capture/Monitor OFF karein.
2. UI aur service dono normally stop karein (`Shift+F5`).
3. Visual Studio close/reopen karein.
4. Full System profile se start karein.

Expected:

- Users, devices, tags, settings aur history present.
- Service heartbeat new start time/instance dikhaye.
- Previous stale instance connected na dikhe.

- [ ] PASS
- [ ] FAIL

## 28. Automated Tests

Visual Studio:

1. Menu `Test > Test Explorer`.
2. `Run All Tests`.

Ya terminal:

```powershell
cd "D:\Spring Boot\PLC Project\WTT_PLC\New PLc"
dotnet test WTT.slnx -c Release
```

Expected baseline: At least `87 passed`, zero failed, zero skipped.

- [ ] PASS
- [ ] FAIL
- Actual result: Passed ____ / Failed ____ / Skipped ____

## 29. Final Database Checks

SSMS me run karein:

```sql
USE WTT;

SELECT MigrationId, ProductVersion
FROM dbo.__EFMigrationsHistory
ORDER BY MigrationId;

SELECT TOP (20) *
FROM dbo.ServiceRuntimeState;

SELECT TOP (20) *
FROM dbo.DeviceRuntimeStates;

SELECT COUNT(*) AS Pending
FROM dbo.TagReadings
WHERE IsTransferred = 0;
```

Expected:

- 3 migrations including `AddRuntimeStatus`.
- Recent heartbeat while service running.
- Device runtime rows.
- Pending rows transfer configuration ke according drain hon.

- [ ] PASS
- [ ] FAIL

## 30. Test Cleanup

1. `ManualTestPLC` Capture OFF.
2. `ManualTestPLC` Monitor OFF.
3. Session timeout wapas 30 minutes.
4. Central transfer test server disable karein agar further test nahi karna.
5. Test users optional disable/delete karein.
6. `Shift+F5` se Visual Studio debugging stop karein.
7. `WTT_Central_Test` ko abhi delete na karein; result review tak preserve rakhein.

- [ ] Cleanup complete

## 31. Final Acceptance Summary

| Area | PASS | FAIL | Notes |
|---|---|---|---|
| Build/startup | [ ] | [ ] | |
| Authentication | [ ] | [ ] | |
| Roles | [ ] | [ ] | |
| Device/tag CRUD | [ ] | [ ] | |
| Manual capture | [ ] | [ ] | |
| PLC trigger | [ ] | [ ] | |
| Logical trigger | [ ] | [ ] | |
| Live data/chart | [ ] | [ ] | |
| Safe writes | [ ] | [ ] | |
| External SQL | [ ] | [ ] | |
| Central transfer | [ ] | [ ] | |
| CSV export | [ ] | [ ] | |
| Logs/settings | [ ] | [ ] | |
| UI-close/service-continue | [ ] | [ ] | |
| Restart persistence | [ ] | [ ] | |
| Automated tests | [ ] | [ ] | |

Final decision:

- [ ] Manual acceptance passed.
- [ ] Defects found; production installation hold karein.
- [ ] Mock acceptance passed; real PLC commissioning still required.
