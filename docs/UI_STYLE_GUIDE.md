# WTT UI Style Guide

Version: 1.2.0
Scope: `src/WTT.UI` — XAML resources, colour tokens, buttons, tables, navigation, theming
Audience: UI developers, reviewers, anyone changing the look of WTT
Related: `docs/USER_GUIDE.md` (end-user behaviour), `docs/ARCHITECTURE.md` (technical design)

---

## Contents

1. [Why this guide exists](#1-why-this-guide-exists)
2. [Where styles live](#2-where-styles-live)
3. [Colour system](#3-colour-system)
4. [Button catalogue](#4-button-catalogue)
5. [Choosing the right button](#5-choosing-the-right-button)
6. [Tabs](#6-tabs)
7. [Sidebar and title bar](#7-sidebar-and-title-bar)
8. [Tables and grids](#8-tables-and-grids)
9. [Dashboard tokens (ISA-101)](#9-dashboard-tokens-isa-101)
10. [Text and layout styles](#10-text-and-layout-styles)
11. [Status brushes](#11-status-brushes)
12. [KPI card gradients](#12-kpi-card-gradients)
13. [Theming (Dark / Light)](#13-theming-dark--light)
14. [Charts](#14-charts)
15. [Rules and review checklist](#15-rules-and-review-checklist)
16. [Known constraints](#16-known-constraints)
17. [Change log](#17-change-log)

---

## 1. Why this guide exists

WTT went through a full visual pass. Before it, buttons came in three inconsistent
flavours: WPF-UI `Appearance="Primary"`, `Appearance="Secondary"` and plain
`Appearance` defaults. Several buttons had **no visual affordance at all** — a
user could not tell they were clickable. Colour also drifted per page: violet
table headers one sprint, blue the next.

This guide is the contract. Any new screen must reuse these tokens instead of
inventing a colour. The two hard rules are:

1. **No default-looking buttons.** Every clickable control must visibly read as
   a control (fill, border or link treatment).
2. **Colour encodes intent, not decoration.** Blue means create/save, grey means
   safe/secondary, red means destructive, navy means verify/test.

---

## 2. Where styles live

| File | Contents |
|---|---|
| `src/WTT.UI/Resources/Styles.xaml` | **The single style library.** 1300+ lines, one `ResourceDictionary`. Every shared style in this guide lives here. |
| `src/WTT.UI/App.xaml` | Merges `ui:ThemesDictionary`, `ui:ControlsDictionary` and `Resources/Styles.xaml`; also registers all value converters as app resources. |
| `src/WTT.UI/Views/Windows/MainWindow.xaml` | Sidebar (`ui:NavigationView`) brush overrides, brand block, header buttons. |
| Page-local `Page.Resources` | Only for styles genuinely scoped to one page (see §16). |

`Styles.xaml` is merged at application level, so any page or window can reference a
style with `StaticResource` without re-declaring it.

### Merging order in `App.xaml`

```xml
<ui:ThemesDictionary Theme="Dark" />
<ui:ControlsDictionary />
<ResourceDictionary>
    <ResourceDictionary.MergedDictionaries>
        <ResourceDictionary Source="Resources/Styles.xaml" />
    </ResourceDictionary.MergedDictionaries>
    <!-- app-level converter resources -->
</ResourceDictionary>
```

WPF-UI (`WPF-UI` 4.3.0) supplies the base control templates. **Never restyle a
WPF-UI control by editing the package** — always override with a keyed style.

---

## 3. Colour system

### 3.1 Primary palette

| Token | Hex | Usage |
|---|---|---|
| Royal Blue | `#1D4ED8` | Primary actions: Add, Save, Search, Acknowledge, Export. Hover `#2563EB`, pressed `#1E40AF`, darkest `#172554`. |
| Dark Navy | `#1E3A5F` | Verification actions: Test connection, Test read, Browse controller, Configure server. Border `#152843`, hover `#2A4D7C`. |
| Indigo | `#4F46E5` | **Refresh** buttons (gradient, see §4). Border `#4338CA`, light stop `#818CF8`, hover light stop `#A5B4FC`. |
| Red | `#B42318` | Destructive: Delete, Write-to-PLC confirm, Delete Now. Border `#7A271A`, hover `#912018` / `#5F1D13`, pressed `#7A1A12` / `#4A130D`. |
| Trash Red | `#DC2626` | Compact 28×28 trash icon buttons. Border `#B91C1C`. |
| Amber | `#FBBF24 → #F59E0B` | Reset-password (key) action. Border `#D97706`. |
| Violet | `#8B5CF6 → #7C3AED` | Unlock-user action. Border `#6D28D9`. |
| Emerald | `#059669` | Dashboard Live Data action. Border `#047857`, hover `#10B981`. |
| Blue gradient | `#3B82F6 → #2563EB` | Edit (pencil) and Write row actions. Border `#1D4ED8`. |

### 3.2 Slate / neutral palette

| Token | Hex | Usage |
|---|---|---|
| Sidebar slate | `#1E293B` | Window background, therefore sidebar pane colour (see §7). |
| Nav text | `#CBD5E1` | Sidebar item default foreground. |
| Nav section label | `#94A3B8` | Sidebar group headings (MONITOR, MANAGE). |
| Nav hover wash | `#14FFFFFF` | Sidebar item hover (`NavigationViewItemBackgroundPointerOver`) |
| Nav selected wash | `#2E0EA5E9` (`WttNavSelectedBackgroundBrush`) | Still defined and still used by the `WttNavItemStyle` active trigger — the solid indigo below coexists as a separate WPF-UI template brush, it did not replace this wash |
| Accent sky | `#0EA5E9` (`WttAccentBrush`) / deep `#0284C7` (`WttAccentDeepBrush`) | Sidebar brand tile gradient `#0EA5E9 → #0369A1` (the `#0369A1` stop lives in `MainWindow.xaml`, not `Styles.xaml`) |
| Alarm badge (sidebar) | `#EF4444` (`WttAlarmBadgeBrush`) | Sidebar alarm counter |
| Alarm badge (header bell) | `#E53935` (hard-coded) | Header bell badge — deliberately separate from the sidebar brush |
| DataGrid header | `#1D4ED8` | All column headers, white bold text (implicit override). |
| Alternate row | `#FFF8E1` | Cream alternating row, forced text `#1A1A1A`. |
| Selection green | `#C6EFCE` / text `#006100` | Excel-style row selection (`SystemColors.HighlightBrushKey` override). Inactive `#E2EFDA`. |

### 3.3 Theme-adaptive brushes

Anything that must survive Dark and Light without a hard-coded hex uses WPF-UI's
dynamic brushes. Prefer these for ghost/neutral surfaces:

| Brush | Use |
|---|---|
| `ControlFillColorDefaultBrush` | Neutral button base fill |
| `ControlFillColorSecondaryBrush` | Neutral button hover |
| `ControlFillColorTertiaryBrush` | Neutral button pressed |
| `ControlFillColorDisabledBrush` | Disabled fill |
| `CardStrokeColorDefaultBrush` | Neutral button / card border |
| `TextFillColorPrimaryBrush` | Neutral button text |
| `TextFillColorSecondaryBrush` | Secondary/label text |
| `TextFillColorDisabledBrush` | Disabled text |
| `ApplicationBackgroundBrush` | Page/window background per theme |

> **Rule:** if a screen must look correct in *both* themes, it must use dynamic
> brushes. Hard-coded hex is only acceptable where the colour is a deliberate,
> theme-independent brand or semantic signal (the palette in §3.1).

---

## 4. Button catalogue

All full-size buttons share one geometry so toolbars line up:

```
FontSize 13 · FontWeight SemiBold · Padding 14,6 · MinHeight 34
CornerRadius 6 · BorderThickness 1 · SnapsToDevicePixels True
Cursor Hand · VerticalAlignment Center
```

### 4.1 `PrimaryBtn` — primary action

```xml
<Style x:Key="PrimaryBtn" TargetType="{x:Type Button}"
       BasedOn="{StaticResource ExportBtn}" />
```

An **alias** of `ExportBtn` so XAML reads by intent (Add/Save) while the pixels
stay identical. Use it for anything that creates or commits data.

Visual: solid `#1D4ED8`, border `#1E40AF`, white text, subtle `#301D4ED8`
drop shadow, hover `#2563EB`.

```xml
<Button Style="{StaticResource PrimaryBtn}" Command="{Binding AddCommand}">
    <StackPanel Orientation="Horizontal" VerticalAlignment="Center">
        <ui:SymbolIcon Symbol="Add24" Foreground="White" FontSize="14"
                       Margin="0,0,6,0" VerticalAlignment="Center" />
        <TextBlock Text="Add device" VerticalAlignment="Center" />
    </StackPanel>
</Button>
```

> WPF-UI's `ui:Button` `Icon` property cannot take a per-instance white
> foreground, so icon+text buttons are native `Button` + `StackPanel`. That is
> deliberate, not an accident.

### 4.2 `ExportBtn` — export / commit

Identical pixels to `PrimaryBtn`; kept as a separate key because it is the older,
more widely referenced name (CSV export on Live Data and Trends). New code may use
either; prefer `PrimaryBtn` for Add/Save.

### 4.3 `NeutralBtn` — secondary / ghost

```xml
<Style x:Key="NeutralBtn" TargetType="{x:Type Button}">
    <Setter Property="Background" Value="{DynamicResource ControlFillColorDefaultBrush}" />
    <Setter Property="BorderBrush" Value="{DynamicResource CardStrokeColorDefaultBrush}" />
    <Setter Property="Foreground" Value="{DynamicResource TextFillColorPrimaryBrush}" />
    ...
</Style>
```

Theme-adaptive, no brand colour. Research-backed secondary hierarchy: a soft
surface plus a border plus dark text, so it reads as clickable but never competes
with a blue primary beside it. Hover `ControlFillColorSecondaryBrush`, pressed
tertiary, disabled uses the disabled brush pair.

Used for: `Cancel`, `Back`, `Clear`, `Select all`, `Deselect all`,
`Enable / disable`, `Change password`, `Sign out`, `Trends`, `Open folder`,
`Previous page`, `Next page`, header bell and Account buttons.

### 4.4 `DangerBtn` — destructive

Solid `#B42318` with white text, same geometry as `PrimaryBtn`, no page-specific
margin. Hover `#912018` / border `#5F1D13`, pressed `#7A1A12` / `#4A130D`,
disabled `#E7E5E4` / `#D6D3D1` with `#A8A29E` text.

Used for the Write confirmation in `WriteTagWindow`. `AlarmsPage` keeps a local
`DangerProNative` (same red, plus a hard-coded `Margin="0,0,12,0"`) for
`Delete Now` — see §16.

### 4.5 `DangerBtn` vs `TrashBtn`

| | `DangerBtn` | `TrashBtn` |
|---|---|---|
| Size | Full (MinHeight 34) | Compact 28×28 icon square |
| Colour | `#B42318` | `#DC2626` |
| Use | A labelled confirm button | A trash icon in a grid row |

### 4.6 `CoralRefreshBtn` — Refresh (indigo)

**The name is historical.** The key was kept as `CoralRefreshBtn` because it is
referenced from nine pages; only the colour changed to indigo. Do not rename
without updating all call sites.

```xml
<Style x:Key="CoralRefreshBtn" TargetType="{x:Type Button}">
    <GradientStop Color="#818CF8" Offset="0" />
    <GradientStop Color="#4F46E5" Offset="1" />
    <!-- BorderBrush #4338CA -->
</Style>
```

Hover keeps the indigo pair with a lighter top stop (`#A5B4FC`); pressed darkens
to `#4F46E5 → #4338CA`. Includes a soft drop shadow and radius 6.

Standard Refresh markup (**always icon + text, never icon-only**):

```xml
<Button Style="{StaticResource CoralRefreshBtn}" Command="{Binding RefreshCommand}">
    <StackPanel Orientation="Horizontal" VerticalAlignment="Center">
        <ui:SymbolIcon Symbol="ArrowClockwise24" Foreground="White" FontSize="15"
                       Margin="0,0,8,0" VerticalAlignment="Center" />
        <TextBlock Text="Refresh" VerticalAlignment="Center" />
    </StackPanel>
</Button>
```

`CoralRefreshIconBtn` is a compact derived style (padding 9,7, min width 34) for
icon-only contexts. **Icon-only refresh buttons were removed app-wide** — an
unlabelled icon failed the "is this even a button?" test.

Anticlockwise/refresh glyph: `ArrowClockwise24` or `ArrowSync24` (both accepted).

### 4.7 `ConfigureBtn` — verification / configuration

Solid dark navy `#1E3A5F`, border `#152843`, white text, hover `#2A4D7C`,
pressed `#152843` / `#0E1C30`. Includes a `#20000000` drop shadow.

Used for `Test connection`, `Test read`, `Browse controller`, `Configure server`.

### 4.8 `DashLiveDataBtn` — emerald

Solid `#059669`, border `#047857`, hover `#10B981`, pressed `#047857` / `#065F46`.
Dashboard "Live Data" shortcut only.

### 4.9 `KeyBtn` (amber) and `UnlockBtn` (violet)

| Style | Gradient | Border | Row action |
|---|---|---|---|
| `KeyBtn` | `#FBBF24 → #F59E0B` | `#D97706` | Reset password |
| `UnlockBtn` | `#8B5CF6 → #7C3AED` | `#6D28D9` | Unlock user |

Both are used on `UsersPage` row actions and exist so that two different
administrative actions are never the same colour.

### 4.10 `SegmentBtn` — segmented pill

Transparent by default, radius 4, padding 10,5, margin 1, SemiBold 12. Hover
`ControlFillColorSecondaryBrush`, pressed tertiary, disabled opacity 0.45. Text
starts at `TextFillColorSecondaryBrush` and goes primary on hover.

Used for the Trends time-window selector (`1m 5m 15m 30m 1h`) inside a
`ControlFillColorDefaultBrush` track — a classic segmented control.

### 4.11 `LinkBtn` — compact text action

Transparent, no visible border, foreground `#1D4ED8`, SemiBold 11, padding 8,5.
Hover tint `#141D4ED8`, pressed `#291D4ED8`, disabled opacity 0.45.

Used for `Select All` / `Clear All` in the Trends tag list — actions that must be
obviously clickable but must not look like a button competing with the toolbar.

### 4.12 Row-level buttons

| Style | Where | Definition |
|---|---|---|
| `RowActionBtn` | `Styles.xaml` | `ui:Button`, theme fill + `CardStrokeColorDefaultBrush` border, radius 6, SemiBold, `Appearance="Secondary"`, cursor hand. |
| `RowBtn` | Page-local (`AlarmsPage`, `DeviceConfigPage`) | Same treatment; carries a `DataTrigger` forcing `#1A1A1A` text on cream alternate rows. |

Both exist for the same reason: WPF-UI's flat `Appearance="Secondary"` disappears
against a cream alternating row. The explicit fill + stroke restores the affordance
without breaking the row's colour scheme.

---

## 5. Choosing the right button

Decision table — use this before writing any new button XAML:

| The action… | Style | Example |
|---|---|---|
| Creates or commits data | `PrimaryBtn` | Add device, Save, Save mode, Acknowledge All, Search |
| Leaves the screen / cancels | `NeutralBtn` | Cancel, Back |
| Deselects, clears, resets | `NeutralBtn` | Clear, Deselect all, Clear filters |
| Reads or verifies without changing state | `ConfigureBtn` | Test connection, Test read, Browse controller |
| Deletes or writes to a controller | `DangerBtn` / `TrashBtn` | Write, Delete Now, Remove |
| Reloads the current view | `CoralRefreshBtn` | Refresh |
| Exports a file | `ExportBtn` | Export CSV |
| Resets a credential | `KeyBtn` | Reset password |
| Re-enables an account | `UnlockBtn` | Unlock |
| Switches a small range option | `SegmentBtn` | 1m / 5m / 15m |
| Inline text shortcut | `LinkBtn` | Select All / Clear All |
| Toggle inside a table row | `RowActionBtn` / `RowBtn` | Enable/disable, Configure, Acknowledge, Verify |

**Never** leave a `ui:Button` with no `Style` and no explicit `Appearance` — that
is exactly the "does not look like a button" defect this guide exists to prevent.

---

## 6. Tabs

`ProTabItem` (in `Styles.xaml`) is the one tab style for the whole application.

| State | Background | Foreground |
|---|---|---|
| Unselected | Transparent | `TextFillColorSecondaryBrush` |
| Hover | `#141D4ED8` | `TextFillColorPrimaryBrush` |
| **Selected** | **`#1D4ED8` solid** | **White, Bold** |

Corner radius `4,4,0,0` so selected tabs sit flush on the panel below.

Used by: `AlarmsPage` (Active Alarms / Alarm History), `LogViewerPage`
(Application log / Audit trail), `DeviceConfigPage` (all five tabs).

This matches the original `DeviceConfigPage` blue pill, which was lifted into
`Styles.xaml` in commit `7d7e5c0` so every tab strip looks identical. **Do not
re-introduce a page-local tab style.**

---

## 7. Sidebar and title bar

### 7.1 Sidebar

WPF-UI's `NavigationView` pane template is **transparent** — it has no
`Background` binding. The visible sidebar colour is therefore the **Window's**
background. `MainWindow.xaml` sets:

```xml
<Window Background="#1E293B" ...>
```

Additional pane overrides declared in `MainWindow` resources:

```xml
<SolidColorBrush x:Key="NavigationViewItemBackgroundSelected" Color="#4F46E5" />
<SolidColorBrush x:Key="NavigationViewItemBackgroundSelectedPointerOver" Color="#6366F1" />
<SolidColorBrush x:Key="NavigationViewItemBackgroundPointerOver" Color="#14FFFFFF" />
<SolidColorBrush x:Key="NavigationViewItemBackgroundPressed" Color="#294F46E5" />
```

`IsPaneToggleVisible="False"` — the hamburger was removed because its
theme-foreground icon is invisible against a dark sidebar.

### 7.2 Sidebar token styles

| Style | Purpose |
|---|---|
| `WttNavItemStyle` | 40 px rows, fixed foreground `#CBD5E1`, hover/active washes, active = SemiBold white |
| `WttNavSectionStyle` | 11 px Bold `#94A3B8` group headings (`MONITOR`, `MANAGE`); hit-test invisible |
| `WttNavBrandStyle` | 64 px header with the `#0EA5E9 → #0369A1` gradient tile and "WTT / INDUSTRIAL DATA ACQUISITION" |

Foreground is hard-coded (`#CBD5E1`) rather than theme-adaptive **on purpose**: a
Light theme must not paint the sidebar text white-on-slate.

### 7.3 Title bar

`Services/TitleBarTint.cs` owns the native caption colour.

```csharp
TitleBarTint.ApplySlate();   // #1E293B caption, white symbols
```

It is invoked from **two** places because WPF-UI undoes the work:

1. `ThemeManager.Apply` — `ApplicationThemeManager.Apply` calls
   `WindowBackgroundManager.UpdateBackground`, which resets the caption
   (`RemoveTitlebarBackground`) **and overwrites the Window background**.
2. `MainWindow.OnLoaded` — initial paint.

`ApplySlate()` does three things, all best-effort and exception-swallowed so the
app can never fail to start over styling:

- sets `MainWindow.Background` back to a frozen `#1E293B` brush (restores the
  sidebar if the theme manager overwrote it),
- restores an **opaque** `NavigationViewContentBackground` derived from the
  current theme's `ApplicationBackgroundBrush`,
- calls `DwmSetWindowAttribute` with `DWMWA_CAPTION_COLOR` (35) and
  `DWMWA_TEXT_COLOR` (36) on every open window.

Caption tinting is Windows 11+ only (`OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)`);
older Windows keeps the system default. All work is marshalled to the UI thread
via `Dispatcher.Invoke` when called from a background thread.

### 7.4 The opaque content fix — read this before touching backgrounds

`NavigationViewContentBackground` in WPF-UI's Light theme is a **50 % transparent
white** (per the comment in `TitleBarTint.cs` — no hex literal in code). While the window was also white it looked white. When the
window became slate, the same translucent layer blended to **grey** and the whole
main content area looked wrong.

`RestoreOpaqueContentBackground` therefore forces the content area to the current
theme's *window* colour at full opacity. The result:

| Surface | Colour |
|---|---|
| Sidebar | `#1E293B` slate (pane transparent → window background shows) |
| Content area | Light theme = opaque white, Dark theme = opaque dark |
| Title bar | Slate via DWM |

If you change the window background, you must re-check all three rows.

---

## 8. Tables and grids

One explicit keyed `DataGrid` style (`x:Key="Grid"`) in `Styles.xaml`, applied by pages that opt in.

| Property | Value | Why |
|---|---|---|
| Row/column virtualization | `EnableRowVirtualization` + `EnableColumnVirtualization` + `VirtualizingPanel.IsVirtualizing`, mode `Recycling` | **Load-bearing.** Without it, every refresh re-measures all rows and scrolling freezes on the Batches (50 rows/5 s) and Logs pages. |
| `AlternationCount` | 2 | Enables cream striping |
| Alternate row background | `#FFF8E1` | Readable in both themes |
| Alternate row foreground | `#1A1A1A` | Dark text on cream; a dark theme's white text would vanish |
| `RowHeight` | 38 | Room for in-row action buttons so they are not clipped |
| Grid lines | `CardStrokeColorDefaultBrush` | All four directions |
| `SelectionMode` | Single | |

### Column headers

An implicit `DataGridColumnHeader` override sets background `#1D4ED8` with white
text — this replaced an earlier violet treatment (commit `73a6d2b`). Pages add a
local `BasedOn` style for `FontWeight=Bold`, `FontSize=13`, `Padding=6,5`,
left-aligned, `MinHeight=34`.

### Selection

`SystemColors.HighlightBrushKey` is overridden to `#C6EFCE` with
`HighlightTextBrushKey` `#006100` (Excel-like green), inactive `#E2EFDA`.
Selection must never collide with the blue header or the cream stripe.

### Cell text

Pages define a local `TrimCell` `TextBlock` style (`TextTrimming=CharacterEllipsis`,
vertical centre) for long columns.

---

## 9. Dashboard tokens (ISA-101)

`Styles.xaml` carries a commented **"DASHBOARD TOKENS"** block modelled on
ISA-101 high-performance HMI practice: flat panels, no decoration that is not
data, hover state as outline rather than motion.

| Style | Purpose |
|---|---|
| `DashCanvas` | Chart canvas background |
| `DashCard`, `DashTileCard` | Flat panel surfaces |
| `DashSectionLabel`, `DashPanelTitle`, `DashPanelSubtitle` | Section hierarchy |
| `DashKpiCard` | KPI tile; hover adds surface + accent outline, **no animation** |
| `DashKpiLabel`, `DashKpiValue` (26 px), `DashKpiUnit` | KPI text hierarchy |
| `DashDivider`, `DashChartGridLine` | Rules and grid lines |
| `DashPill`, `DashPillText` | Status pills (Running / Paused) |
| `DashMiniLabel`, `DashMiniValue`, `DashRowLabel`, `DashRowValue` | Compact in-card rows |

Chart line colours: indigo for the primary series, amber for the secondary series.
Progress bar uses an emerald gradient. Running/Paused pills are tinted, not solid.

---

## 10. Text and layout styles

| Style | Spec | Use |
|---|---|---|
| `PageRoot` | page margin/container | Every page root |
| `PageTitle` | 24 px SemiBold | Page heading |
| `PageSubtitle` | muted, wraps | Page description under the title |
| `SectionTitle` | 15 px | Section heading inside a card |
| `FieldLabel` | small, SemiBold | Above every input/combo |
| `Hint` | small, muted | Helper text below inputs |
| `ErrorText` | red | Inline validation / `ErrorMessage` |
| `Card` | padded, bordered, radius | Card container |
| `StatValue` / `StatLabel` | big number / caption | Stat strips |
| `Dot` | small ellipse | Legend/status dot |

`ContentDialog` also has an implicit override: `MaxWidth=520`, `MinWidth=400`.
Without it, confirmation dialogs stretch to the full window width.

---

## 11. Status brushes

| Key | Colour | Meaning |
|---|---|---|
| `GoodBrush` | `#2ECC71` | OK / connected / Good quality |
| `WarnBrush` | `#F39C12` | Degraded / warning |
| `BadBrush` | `#E74C3C` | Fault / Bad quality / error |
| `NeutralBrush` | `#9CA3AF` | Unknown / neutral |

These are fixed hex because the meaning must not change with theme. Combine with
`QualityToBrushConverter`, `DeviceStatusToBrushConverter` and
`BoolToBrushConverter` rather than writing triggers inline.

---

## 12. KPI card gradients

Pastel gradients with **fixed dark text**, so they read identically in Light and
Dark mode (the card itself carries the light value):

| Key | Stops |
|---|---|
| `KpiBlueBg` | `#DBEAFE → #BFDBFE` |
| `KpiTealBg` | teal pair |
| `KpiPurpleBg` | purple pair |
| `KpiAmberBg` | amber pair |
| `KpiPinkBg` | pink pair |
| `KpiRedBg` | red pair |

Text: `KpiLabel` `#475569`, `KpiValue` `#0F172A` 26 px ExtraBold.
`ModernKpiCard` adds a hover lift of −3 px plus a drop shadow (the one place a
motion affordance is used).

`DashboardPage.xaml` holds a **local copy** of these styles. If you change a KPI
gradient here, change it there too.

---

## 13. Theming (Dark / Light)

- `App.xaml` starts with `Theme="Dark"`.
- `ThemeManager` (`Services/UiServices.cs`) persists the choice in `AppSettings`
  under key `ui.theme` (`AppSettingKeys.Theme`) and applies it with
  `ApplicationThemeManager.Apply(appTheme, WindowBackdropType.None, true)`.
- `WindowBackdropType.None` is mandatory: every window in WTT uses `None`, and a
  mismatched backdrop produces a blank window.
- **The apply call must happen on the UI thread.** The code has an explicit
  `Dispatcher.Invoke` with a comment warning that skipping it blanks the window.
- After every apply, `TitleBarTint.ApplySlate()` re-runs (§7.3).

Settings page → Appearance applies the theme instantly; no restart.

### Theme-change checklist

1. Sidebar still `#1E293B`? (Window background restored?)
2. Content area still opaque, not grey? (`RestoreOpaqueContentBackground`)
3. Title bar still slate? (DWM caption restored)
4. Neutral buttons still visible (they use dynamic brushes — should be fine)?
5. Cream table rows still readable? (they are fixed — should be fine)

---

## 14. Charts

WTT has **no charting library**. `Directory.Packages.props` contains no
LiveCharts/OxyPlot reference; all plots are hand-rendered.

### `ScadaTrendPlot` — `src/WTT.UI/Controls/ScadaTrendPlot.cs`

A custom `FrameworkElement` described in-code as an *"Industrial SCADA Multi-Pen
Vector Trend Plot"*. Used by `TrendsPage` as `<controls:ScadaTrendPlot x:Name="TrendPlot">`.

| Member | Notes |
|---|---|
| `Series` (`IEnumerable<PenSeriesModel>`) | Pen data, colour, visibility |
| `TimeWindow` (`TimeSpan`) | Visible window |
| `IsLive` (`bool`) | Live follow vs frozen |
| `RenderRevision` (`long`) | Bump to force a re-render |
| `YScaleMode` (`TrendYScaleMode.Shared \| Normalized`) | Shared axis or per-pen normalisation |

`PenSeriesModel` is an `ObservableObject` (identification via `DeviceId` /
`DeviceName` / `TagName` plus a computed `DisplayPath`; rendering via `Color`,
`StrokeBrush`, `LinePen`; data in `Points` with `CurrentValue` / `CurrentQuality`
/ `MinValue…MaxValue` projections; `MinAlarm` / `MaxAlarm` bands; `DataType` with
an `IsDigital` helper driving the digital lanes; `[ObservableProperty] IsVisible`
per-pen toggle).

Rendering happens in `OnRender` with 65 px / 55 px margins, supporting analog and
digital lanes, Y/X grids with date-aware labels, quality gaps, digital step
drawing and binary-search hover lookup.

### Dashboard sparklines

Plain WPF `<Polyline Points="{Binding ReadingTrend}">` and `BufferTrend` rendered
once near the top of `DashboardPage` (page-level aggregates from
`DashboardViewModel`, populated by `AddTrendPoint` / `Normalize`) — one readings
series and one buffer series for the whole plant view, not per device card.

**If you need a new chart type, extend `ScadaTrendPlot` — do not add a charting
NuGet package.**

---

## 15. Rules and review checklist

Before approving any XAML change, verify:

- [ ] No `ui:Button` without a keyed `Style` and without an explicit `Appearance`.
- [ ] No icon-only Refresh — icon + `TextBlock` label, always.
- [ ] New buttons use an existing key from §4; a new key needs a reason recorded
      in this document.
- [ ] Geometry matches: FontSize 13, Padding 14,6, MinHeight 34, radius 6.
- [ ] Destructive actions are red; nothing destructive is blue or grey.
- [ ] Both themes checked (or dynamic brushes used throughout).
- [ ] No `Binding`, `Command`, `CommandParameter`, `Visibility` or `IsEnabled`
      lost during a restyle — diff the XAML and compare command sets.
- [ ] Tab strips use `ProTabItem`.
- [ ] Grids use `Grid` with virtualization still enabled.
- [ ] `dotnet build WTT.slnx -c Debug` reports 0 errors.
- [ ] UI tests still pass: `dotnet test tests/WTT.Tests/WTT.Tests.csproj --filter "Category!=Integration"`.

### The verification method used for the styling pass

Because a restyle converts controls (`ui:Button` → `Button`), bindings are easy to
lose silently. The check that was run for every styling commit:

```powershell
git diff > $env:TEMP\diff.txt
# every Command={Binding X} removed must reappear on an added line
# every Content="..." label must survive (moved into a TextBlock if needed)
# every Symbol="..." icon must survive
# Click=, ItemsSource=, IsChecked=, SelectedItem= must show 0 removed / 0 added
```

Run this, then build, then run the unit tests. Colour-only commits must show
**0 changed `.cs` files** — if a `.cs` file appears in the diff, explain it.

---

## 16. Known constraints

1. **`CoralRefreshBtn` is not coral.** Historical key name, indigo colour. Renaming
   touches nine pages; deliberately not done.
2. **`DangerProNative` lives in `AlarmsPage.xaml`**, not `Styles.xaml`, because it
   carries a page-specific `Margin="0,0,12,0"`. `WriteTagWindow` uses the global
   `DangerBtn` instead. If you need a red button with no margin, use `DangerBtn`.
3. **`RowBtn` is defined twice** (Alarms and DeviceConfig) with different padding
   and font size. They are page-scoped and intentionally not unified yet.
4. **`DashboardPage` duplicates the KPI gradient styles** from `Styles.xaml`.
5. **Icon+text buttons need `StackPanel`** because `ui:Button`'s `Icon` property
   cannot be given a per-instance white foreground.
6. **Code and XAML comments are in English.** (An earlier Hinglish-comment convention was translated to English across `src`.)
7. **Page-local `TrimCell`** styles are duplicated per DataGrid rather than shared.

---

## 17. Change log

| Commit | Change |
|---|---|
| `dca2f6a` | Professional tables: bold headers, full visibility |
| `b76c508` | Violet headers, green selection, cream rows, grid lines |
| `2816dfb` | Styles and button styles across multiple pages |
| `f3aae9c` / `b5dd575` | Configure/LiveData colours, then reverted |
| `ee5fec3` | Blue gradient write buttons; configure/live-data colours |
| `73a6d2b` | Table header violet → blue |
| `693c868` | Device config tabs restyled as blue pill |
| `ef36d08` | Dashboard card readability: Last scan label, theme-aware error state, semibold toggles |
| `7d7e5c0` | `ProTabItem` moved to `Styles.xaml`, applied to Alarms and Logs |
| `132dd3c` | Amber `KeyBtn`, violet `UnlockBtn`, royal-blue `ExportBtn` |
| `4aa0528` | Uniform toolbar buttons; indigo `CoralRefreshBtn`; icon-only Refresh eliminated |
| `0481f7f` | Slate sidebar/title bar, opaque content fix, `TitleBarTint` |
| `2115478` | `PrimaryBtn`, `NeutralBtn`, `DangerBtn`, `SegmentBtn`, `LinkBtn`; 56 default buttons restyled across 9 dialogs and 10 pages |
