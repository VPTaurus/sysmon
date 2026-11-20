# SysMon roadmap

## Backlog

- [ ] Clock in the taskbar readout (Windows already shows one there, so skipped for now)
- [ ] Per-monitor DPI awareness – skipped for now: all 3 monitors run at 100 %, so it changes nothing visible.
      Worth doing when screens with different scaling are used together (e.g. laptop display at 150 % + 100 % monitors).
- [ ] Code signing (avoids the SmartScreen "unknown publisher" warning for downloads)

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
- Start view setting (widget / taskbar / dashboard / tray / last)
- GitHub Actions: build + self-test on every push, release with SysMon.exe on `v*` tags; version stamping
- Memory: WPF software rendering (~300 MB / 70 threads -> ~90 MB / 28 threads); PDH counters measured at < 7 MB
- Process history view: click a process in the dashboard to chart its CPU and memory (2 s in memory, per-minute log)
- 5-minute daily summary files: 30-day dashboard view loads in ~60-100 ms (tested with 30 days / 230k synthetic rows)
- Daily report: morning notification + report window (cards, CPU by hour, top processes, anomalies, day navigation)
- Update check against GitHub releases + one-click self-update (verified: 1.0.0-dev -> 1.0.0 download, swap, restart)
- Process actions: right-click a process for history, file location, web search, copy name, end process (protected list)
- Compare periods: overlay previous period / same time yesterday / last week as dashed lines, card deltas
- Per-user install / uninstall: Settings > Apps entry, Start Menu, install offer for downloaded copies, clean uninstall
- Keep awake until a set time (tray one-click "Until 17:30", time dialog, Settings; earlier time = tomorrow)
