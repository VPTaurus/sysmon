# SysMon Widget

Always-on-top system resource and network monitor for Windows 10/11, with a fullscreen dashboard,
statistics logging and anomaly detection. Single small `.exe`, no admin rights, no runtime to install
(built against the .NET Framework 4.8 that ships with Windows).

## Features

- **Floating widget** – CPU (+ per-core bars), memory, GPU, disk I/O, storage, network, ping, Wi-Fi/IP,
  data usage, top processes, battery. Full / Compact / Mini layouts, 4 themes, snap to edges, click-through.
- **Taskbar readout** – compact two-line meter on the taskbar; click to open the dashboard, drag to move.
- **Dashboard** – live 15 min to 30 days, charts with hover, avg/p95/max cards, process table, per-core bars,
  anomaly list; click an anomaly to zoom to it and see the processes logged at that time. F11 fullscreen, CSV export.
- **Logging** – CSV per day in `%APPDATA%\SysMonWidget\logs` (10 s averages + peaks), per-minute process log,
  anomaly log, configurable retention.
- **Anomaly detection** – learned per-metric baselines (EWMA, log-space for rates) plus rules: sustained overload,
  connection loss / packet loss, memory leaks, new CPU-heavy processes, disk-space and battery drain.
- **Alerts** – Windows notifications with thresholds and cooldown.
- **Global hotkeys** – Ctrl+Alt+M dashboard, Ctrl+Alt+W widget, Ctrl+Alt+C click-through (configurable).

## Build

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1            # -> dist\SysMon.exe
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Install   # also installs to %LOCALAPPDATA%\Programs\SysMon Widget + Start Menu
```

Uses `csc.exe` from `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319` (C# 5), so keep the code C# 5 compatible
(no `$"..."`, `?.`, `nameof`, expression-bodied members, auto-property initialisers).

## Command-line switches

| Switch | Effect |
|---|---|
| `--dashboard` | open the dashboard on start |
| `--selftest [file]` | run the anomaly self-test headless, write a report, exit code = failed scenarios |
| `--show-selftest` | run the self-test at start and show its detections on the live dashboard (demo) |

## Data locations

- Settings: `%APPDATA%\SysMonWidget\config.json`
- Data usage totals: `%APPDATA%\SysMonWidget\usage.json`
- Logs: `%APPDATA%\SysMonWidget\logs\`
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
| `legacy/SysMon.ps1` | first PowerShell prototype |

See [ROADMAP.md](ROADMAP.md) for planned work.
