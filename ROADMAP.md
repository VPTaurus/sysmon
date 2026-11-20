# SysMon roadmap

## Backlog

- [ ] Keep awake "until a set time" option (currently 30 min / 1 h / 2 h / 4 h / until turned off)
- [ ] Clock in the taskbar readout (Windows already shows one there, so skipped for now)
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
- Global hotkeys (Ctrl+Alt+M / W / C / K)
- Git repository, README
- Expandable dashboard graphs (button or double-click, min/avg/max of the window, Esc to go back)
- Utilities: keep PC awake (SetThreadExecutionState, timed or indefinite, optional screen on), opt-in presence mouse nudge within a time window after N idle minutes
- Clock (time, optional seconds and date) in widget, Mini layout and dashboard
