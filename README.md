# SysMon Widget

[![build](https://github.com/VPTaurus/sysmon/actions/workflows/build.yml/badge.svg)](https://github.com/VPTaurus/sysmon/actions/workflows/build.yml)

**Download:** the latest `SysMon.exe` is on the [Releases](https://github.com/VPTaurus/sysmon/releases) page
(every push also produces a build artifact on the Actions page). Run it and choose **Install**: it is copied to
`%LOCALAPPDATA%\Programs\SysMon Widget`, gets a Start Menu entry and appears in **Settings > Apps**, where it can be
uninstalled. No administrator rights are needed. Choosing "No" just runs the downloaded copy.

Always-on-top system resource and network monitor for Windows 10/11, with a fullscreen dashboard,
statistics logging and anomaly detection. Single small `.exe`, no admin rights, no runtime to install
(built against the .NET Framework 4.8 that ships with Windows).

## Features

- **Floating widget** – CPU (+ per-core bars), memory, GPU, disk I/O, storage, network, ping, Wi-Fi/IP,
  data usage, top processes, battery. Full / Compact / Mini layouts, 4 themes, snap to edges, click-through.
- **Taskbar readout** – compact two-line meter on the taskbar; click to open the dashboard, drag to move.
- **Dashboard** – live 15 min to 30 days, charts with hover, avg/p95/max cards, process table, per-core bars,
  anomaly list; click an anomaly to zoom to it and see the processes logged at that time; click a process to chart
  its CPU and memory history; right-click a process to open its file location, search for it or end it;
  compare with the previous period, yesterday or last week (dashed overlay + changes on the cards). F11 fullscreen, CSV export.
- **Logging** – CSV per day in `%APPDATA%\SysMonWidget\logs` (10 s averages + peaks), per-minute process log,
  anomaly log, configurable retention.
- **Anomaly detection** – learned per-metric baselines (EWMA, log-space for rates) plus rules: sustained overload,
  connection loss / packet loss, memory leaks, new CPU-heavy processes, disk-space and battery drain.
- **Alerts** – Windows notifications with thresholds and cooldown.
- **Expandable graphs** – button or double-click makes a dashboard chart fill the chart area with min/avg/max.
- **Utilities** – keep PC awake (Windows power request, timed or until off, optional screen on) and an opt-in
  presence nudge (1 px mouse move after N idle minutes inside a daily time window).
- **Clock** – optional time / seconds / date in the widget, Mini layout and dashboard.
- **Start view** – start with the widget, taskbar readout, dashboard, tray icon only, or the same as last time.
- **Daily report** – morning notification about yesterday (CPU / memory / GPU, traffic, disk, ping, anomalies,
  top processes) and a report window with CPU-by-hour and day navigation (tray, dashboard, Settings).
- **Updates** – checks this repository's GitHub releases once a day (release builds) and updates in one click:
  downloads `SysMon.exe`, verifies it, swaps the file and restarts. Settings, logs and history are kept.
- **Fast history** – finished days get a 5-minute summary file, so 7 / 30-day views load in well under a second.
- **Global hotkeys** – Ctrl+Alt+M dashboard, Ctrl+Alt+W widget, Ctrl+Alt+C click-through, Ctrl+Alt+K keep awake (configurable).

## Build

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1            # -> dist\SysMon.exe
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Install   # also installs to %LOCALAPPDATA%\Programs\SysMon Widget + Start Menu
```

Uses `csc.exe` from `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319` (C# 5), so keep the code C# 5 compatible
(no `$"..."`, `?.`, `nameof`, expression-bodied members, auto-property initialisers).

The version is stamped by `build.ps1` (`-Version 1.2.0`, otherwise `<last tag>-dev+<commit>`) and shown in the
Settings title and dashboard subtitle.

## Releases (GitHub Actions)

`.github/workflows/build.yml` builds on Windows and runs the anomaly self-test on every push and pull request.
To publish a release with `SysMon.exe` attached:

```powershell
git tag v1.0.0
git push origin v1.0.0
```

Creating the release on the GitHub website (with a new `v*` tag) works too: the workflow builds and attaches
`SysMon.exe` to it.

## Diagnostics

Set `SYSMON_DIAG=1` before starting to log managed heap, GC counts, private memory, threads and handles to
`error.log` every 10 seconds. WPF runs in software rendering mode by default (keeps the GPU driver's memory and
threads out of the process: ~90 MB instead of ~300 MB); set `"GpuRendering": true` in `config.json` to change that.

## Command-line switches

| Switch | Effect |
|---|---|
| `--dashboard` | open the dashboard on start |
| `--selftest [file]` | run the anomaly self-test headless, write a report, exit code = failed scenarios |
| `--show-selftest` | run the self-test at start and show its detections on the live dashboard (demo) |
| `--report [yyyy-MM-dd]` | open the daily report (default: yesterday) |
| `--update-now` | install the latest GitHub release over this exe if it is newer, then restart |
| `--install [--quiet]` | install this exe for the current user (Start Menu, Settings > Apps); `--quiet` = don't start it |
| `--uninstall [--quiet]` | remove SysMon; asks whether to delete settings and logs (`--quiet` keeps them, no dialogs) |

Environment: `SYSMON_DATA_DIR=<folder>` runs a copy against its own settings and logs (separate single-instance lock),
handy for testing; `SYSMON_DIAG=1` logs memory diagnostics.

## Data locations

- Settings: `%APPDATA%\SysMonWidget\config.json`
- Data usage totals: `%APPDATA%\SysMonWidget\usage.json`
- Logs: `%APPDATA%\SysMonWidget\logs\` (`yyyy-MM-dd.csv` raw, `summary-*.csv` 5-minute, `processes-*.csv`, `anomalies.csv`)
- Error log: `%APPDATA%\SysMonWidget\error.log`

## Source layout

| File | Purpose |
|---|---|
| `src/Program.cs` | entry point, tray, background threads, data routing, alerts |
| `src/Sampler.cs` | collects metrics (PDH counters by English name, network, battery, Wi-Fi) |
| `src/Native.cs` | P/Invoke: PDH, WLAN, memory, power, window styles |
| `src/Widget.cs` | floating widget |
| `src/TaskbarReadout.cs` | taskbar meter |
| `src/Dashboard.cs`, `src/TimeChart.cs` | dashboard window and time-axis chart |
| `src/Logging.cs` | records, CSV store, 10 s aggregator |
| `src/Anomaly.cs`, `src/SelfTest.cs` | anomaly detector and its scenario self-test |
| `src/Controls.cs` | sparkline, core bars, mini bar |
| `src/Core.cs` | settings, themes, formatting, ring buffer |
| `src/SettingsWindow.cs`, `src/Hotkeys.cs` | settings UI, global hotkeys |
| `src/Utilities.cs` | keep awake, presence nudge |
| `src/Report.cs` | daily report and report window |
| `src/Updater.cs` | GitHub release check and self-update |
| `src/Installer.cs` | per-user install / uninstall, Settings > Apps entry |
| `src/ProcActions.cs` | process actions (file location, end, search) |
| `legacy/SysMon.ps1` | first PowerShell prototype |

See [ROADMAP.md](ROADMAP.md) for planned work.
