# SysMon roadmap

## Requested next (2026-11-20)

- [ ] **Expand a dashboard graph** – click a chart (header button or double-click) to enlarge it to fill the whole chart area, with more detail: finer resolution, more axis labels, min/avg/max for the visible window. Esc or click again to go back. Single clicks on anomaly markers keep zooming to the anomaly.
- [ ] **Utilities**
  - [ ] *Keep PC awake* – prevents sleep and screen-off using the Windows API (`SetThreadExecutionState`); no fake input. Timer options (30 min / 1 h / 2 h / until turned off / until a set time) and a tray/hotkey toggle.
  - [ ] *Keep presence active (mouse nudge)* – optional tiny periodic mouse movement during a chosen time period, only after X minutes of real inactivity. Opt-in, off by default (see note below).
- [ ] **Clock** – option to show time (and optionally date / seconds) in the floating widget header, mini strip, taskbar readout and dashboard header.

> Note on the mouse nudge: simulated input can conflict with workplace policies and may be visible to endpoint-monitoring tools. Keep-awake via the Windows API covers "PC stays awake" without that risk.

## Backlog

- [ ] Put the project in git (.gitignore for `dist/`, `app.ico`, `selftest.txt`; README; first commit)
- [ ] Lower memory use (~220–270 MB): read processes directly instead of PDH `Process(*)`, create GPU counters lazily
- [ ] Daily summary files so 7/30-day dashboard ranges load instantly
- [ ] Per-monitor DPI awareness (3 monitors with possibly different scaling)
- [ ] Daily summary report (notification + dashboard tab)
- [ ] Compare periods (today vs yesterday, week vs week)
- [ ] Per-process history chart from the process log
- [ ] Process actions in the dashboard (end process, open file location)
- [ ] Proper install: version in Settings, Apps & Features entry, uninstall

## Done

- Floating widget (CPU/cores, RAM, GPU, disk I/O, storage, network, ping, connection, data usage, top processes, battery), themes, layouts, alerts, settings
- Fullscreen dashboard with ranges, charts, stats, processes, anomalies, CSV export
- Statistics logging (CSV per day + process log + anomaly log, retention)
- Anomaly detection (learned baselines + rules) and self-test (16/16 scenarios)
- Click anomaly → zoom charts + processes at that time
- Taskbar readout
- Global hotkeys (Ctrl+Alt+M / W / C)
