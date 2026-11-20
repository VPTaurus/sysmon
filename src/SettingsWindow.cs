using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SysMonApp
{
    class SettingsWindow : Window
    {
        static Settings S { get { return Program.S; } }
        bool applying;
        TabControl tabs;

        public SettingsWindow()
        {
            Title = "SysMon Widget " + Program.Version + " - Settings";
            Width = 420; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen; Topmost = true;
            FontFamily = new FontFamily("Segoe UI"); FontSize = 12.5;
            try { Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(Program.Tray.Icon.Handle, Int32Rect.Empty, null); } catch { }
            Build(0);
        }

        public void Sync() { if (!applying) Build(tabs != null ? tabs.SelectedIndex : 0); }

        void Changed() { applying = true; try { Program.Apply(); } finally { applying = false; } }

        void Build(int tab)
        {
            tabs = new TabControl { Margin = new Thickness(10, 10, 10, 0) };
            tabs.Items.Add(new TabItem { Header = "General", Content = General() });
            tabs.Items.Add(new TabItem { Header = "Sections", Content = Sections() });
            tabs.Items.Add(new TabItem { Header = "Network", Content = Network() });
            tabs.Items.Add(new TabItem { Header = "Alerts", Content = AlertsPage() });
            tabs.Items.Add(new TabItem { Header = "History & anomalies", Content = HistoryPage() });
            tabs.Items.Add(new TabItem { Header = "Utilities", Content = UtilitiesPage() });
            tabs.Items.Add(new TabItem { Header = "Hotkeys", Content = HotkeysPage() });
            tabs.SelectedIndex = tab;

            var close = new Button { Content = "Close", Width = 90, Height = 28, Margin = new Thickness(10), HorizontalAlignment = HorizontalAlignment.Right, IsCancel = true };
            close.Click += (s, e) => Close();
            var dp = new DockPanel();
            DockPanel.SetDock(close, Dock.Bottom);
            dp.Children.Add(close);
            dp.Children.Add(tabs);
            Content = dp;
        }

        // ---------------------------------------------------------------- pages
        UIElement General()
        {
            var p = Page();
            var starts = new[] { "Remember", "Widget", "Taskbar", "Dashboard", "Tray" };
            Combo(p, "When SysMon starts, show", new[] { "Same as last time", "Floating widget", "Taskbar readout", "Dashboard", "Nothing (tray icon only)" },
                  Array.IndexOf(starts, S.StartupView), i => S.StartupView = starts[i]);
            Combo(p, "Theme", Theme.Names, Array.IndexOf(Theme.Names, S.Theme), i => S.Theme = Theme.Names[i]);
            var modes = new[] { "Full", "Compact", "Mini" };
            Combo(p, "Layout", new[] { "Full (graphs)", "Compact (no graphs)", "Mini (one line)" }, Array.IndexOf(modes, S.Mode), i => S.Mode = modes[i]);
            var ints = new[] { 500, 1000, 2000 };
            Combo(p, "Update every", new[] { "0.5 second", "1 second", "2 seconds" }, Array.IndexOf(ints, S.IntervalMs), i => S.IntervalMs = ints[i]);
            var lens = new[] { 60, 300, 900 };
            Combo(p, "Graph length", new[] { "1 minute", "5 minutes", "15 minutes" }, Array.IndexOf(lens, S.GraphSeconds), i => S.GraphSeconds = lens[i]);

            var g = LabeledRow(p, "Opacity");
            var sl = new Slider { Minimum = 40, Maximum = 100, Value = S.Opacity * 100, Width = 170, IsSnapToTickEnabled = true, TickFrequency = 5, VerticalAlignment = VerticalAlignment.Center };
            var val = new TextBlock { Text = Fmt.Pct(sl.Value), Width = 40, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            sl.ValueChanged += (s, e) => { val.Text = Fmt.Pct(sl.Value); Program.W.Opacity = sl.Value / 100; };
            sl.PreviewMouseUp += (s, e) => { S.Opacity = sl.Value / 100; Changed(); };
            sl.LostKeyboardFocus += (s, e) => { S.Opacity = sl.Value / 100; Changed(); };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(sl); sp.Children.Add(val);
            Grid.SetColumn(sp, 1); g.Children.Add(sp);

            Gap(p);
            Check(p, "Show floating widget", S.WidgetVisible, b => S.WidgetVisible = b);
            Check(p, "Show compact readout on the taskbar", S.TaskbarReadout, b => S.TaskbarReadout = b);
            Check(p, "Show clock in the widget and dashboard", S.ShowClock, b => S.ShowClock = b);
            Check(p, "    with seconds", S.ClockSeconds, b => S.ClockSeconds = b);
            Check(p, "    with date", S.ClockDate, b => S.ClockDate = b);
            Hint(p, "The taskbar readout sits left of the tray icons. Drag it sideways to move it; click it to open the dashboard.");
            Check(p, "Network speed in bits (Mbps) instead of bytes (MB/s)", S.Bits, b => S.Bits = b);
            Check(p, "Snap to screen edges when dragging", S.Snap, b => S.Snap = b);
            Check(p, "Lock position", S.Locked, b => S.Locked = b);
            Check(p, "Click-through (mouse passes through the widget)", S.ClickThrough, b => S.ClickThrough = b);
            Hint(p, "When click-through is on, use the tray icon to turn it off again.");
            Check(p, "Start with Windows", Program.IsStartup(), Program.SetStartup);
            Hint(p, "\"When SysMon starts\" applies each time the app starts, including with Windows. You can still show or hide everything afterwards from the tray icon.");
            return p;
        }

        UIElement Sections()
        {
            var p = Page();
            Hint(p, "Choose what the widget shows. The battery section only appears on devices with a battery.");
            Gap(p);
            Check(p, "CPU usage", S.ShowCpu, b => S.ShowCpu = b);
            Check(p, "    Per-core bars", S.ShowCores, b => S.ShowCores = b);
            Check(p, "Memory", S.ShowRam, b => S.ShowRam = b);
            Check(p, "GPU usage and memory", S.ShowGpu, b => S.ShowGpu = b);
            Check(p, "Disk activity (read / write speed)", S.ShowDiskIo, b => S.ShowDiskIo = b);
            Check(p, "Storage (free space per drive)", S.ShowStorage, b => S.ShowStorage = b);
            Check(p, "Network speed", S.ShowNet, b => S.ShowNet = b);
            Check(p, "Ping / latency", S.ShowPing, b => S.ShowPing = b);
            Check(p, "Connection info (Wi-Fi, IP addresses)", S.ShowConn, b => S.ShowConn = b);
            Check(p, "Data usage (today / this month)", S.ShowUsage, b => S.ShowUsage = b);
            Check(p, "Top processes", S.ShowProcs, b => S.ShowProcs = b);
            Check(p, "Battery", S.ShowBattery, b => S.ShowBattery = b);
            return p;
        }

        UIElement Network()
        {
            var p = Page();
            Text(p, "Ping target", S.PingTarget, v => S.PingTarget = v.Trim() == "" ? "1.1.1.1" : v.Trim());
            Hint(p, "An IP address or host name, e.g. 1.1.1.1 or google.com. Type \"gateway\" to ping your router.");
            Gap(p);
            Check(p, "Show public IP address", S.PublicIp, b => { S.PublicIp = b; Program.RefreshPublicIp(); });
            Hint(p, "Looked up from api.ipify.org every 10 minutes and when your network changes.");
            Gap(p);
            Hint(p, "Data usage is counted only while SysMon is running. It is stored in %APPDATA%\\SysMonWidget\\usage.json.");
            var reset = new Button { Content = "Reset today's and this month's totals", Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(10, 3, 10, 3), HorizontalAlignment = HorizontalAlignment.Left };
            reset.Click += (s, e) =>
            {
                if (MessageBox.Show(this, "Reset the saved data usage totals?", "SysMon Widget", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                Program.Usage.DayRx = Program.Usage.DayTx = Program.Usage.MonthRx = Program.Usage.MonthTx = 0;
                Store.Save("usage.json", Program.Usage);
            };
            p.Children.Add(reset);
            return p;
        }

        UIElement AlertsPage()
        {
            var p = Page();
            Check(p, "Show Windows notifications for alerts", S.AlertsOn, b => S.AlertsOn = b);
            Hint(p, "Set a value to 0 to turn that alert off.");
            Gap(p);
            Number(p, "CPU above (%)", S.AlertCpu, 0, 100, v => S.AlertCpu = v);
            Number(p, "    ...for at least (seconds)", S.AlertCpuSecs, 1, 3600, v => S.AlertCpuSecs = v);
            Number(p, "Memory above (%)", S.AlertRam, 0, 100, v => S.AlertRam = v);
            Number(p, "Disk free space below (%)", S.AlertDiskFree, 0, 100, v => S.AlertDiskFree = v);
            Number(p, "Battery below (%)", S.AlertBattery, 0, 100, v => S.AlertBattery = v);
            Number(p, "Ping packet loss above (%)", S.AlertPingLoss, 0, 100, v => S.AlertPingLoss = v);
            Number(p, "Repeat the same alert after (minutes)", S.AlertCooldownMin, 1, 1440, v => S.AlertCooldownMin = v);
            Gap(p);
            var test = new Button { Content = "Send a test notification", Padding = new Thickness(10, 3, 10, 3), HorizontalAlignment = HorizontalAlignment.Left };
            test.Click += (s, e) => Program.Tray.ShowBalloonTip(4000, "SysMon Widget", "Alerts will look like this.", System.Windows.Forms.ToolTipIcon.Info);
            p.Children.Add(test);
            return p;
        }

        UIElement HistoryPage()
        {
            var p = Page();
            Check(p, "Log statistics to disk", S.Logging, b => S.Logging = b);
            var ivs = new[] { 5, 10, 30, 60 };
            Combo(p, "Log interval", new[] { "5 seconds", "10 seconds", "30 seconds", "1 minute" }, Array.IndexOf(ivs, S.LogIntervalSec), i => S.LogIntervalSec = ivs[i]);
            var keeps = new[] { 7, 30, 90, 365 };
            Combo(p, "Keep logs for", new[] { "7 days", "30 days", "90 days", "1 year" }, Array.IndexOf(keeps, S.LogKeepDays), i => S.LogKeepDays = keeps[i]);
            Check(p, "Also log top processes every minute", S.LogProcesses, b => S.LogProcesses = b);
            Hint(p, "Each interval stores the average and peak of CPU, memory, GPU, disk, network, ping and battery as CSV, one file per day (about 1.5 MB/day at 10 s). Currently using " + Fmt.Size(LogStore.SizeBytes()) + ".");
            var open = new Button { Content = "Open logs folder", Padding = new Thickness(10, 3, 10, 3), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 0) };
            open.Click += (s, e) => { System.IO.Directory.CreateDirectory(LogStore.Dir); System.Diagnostics.Process.Start("explorer.exe", "\"" + LogStore.Dir + "\""); };
            p.Children.Add(open);

            Gap(p); Gap(p);
            Check(p, "Detect anomalies", S.Anomalies, b => S.Anomalies = b);
            var sens = new[] { "Low", "Medium", "High" };
            Combo(p, "Sensitivity", new[] { "Low (fewer, only big deviations)", "Medium", "High (more, smaller deviations)" }, Array.IndexOf(sens, S.AnomalySensitivity), i => S.AnomalySensitivity = sens[i]);
            Check(p, "Show a notification for warnings and critical anomalies", S.AnomalyNotify, b => S.AnomalyNotify = b);
            Hint(p, "SysMon learns what is normal for this PC (from the last 24 hours of logs) and flags values far above it, plus memory leaks, new CPU-heavy processes, sustained overloads, fast disk-space or battery drain and connection loss.");
            var test = new Button { Content = "Run anomaly self-test", Padding = new Thickness(10, 3, 10, 3), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
            test.Click += (s, e) => Program.ShowSelfTest();
            p.Children.Add(test);
            Hint(p, "Replays 16 simulated problems (spikes, leaks, packet loss, disk and battery drain...) through the detector and shows which ones it catches.");
            return p;
        }

        UIElement UtilitiesPage()
        {
            var p = Page();
            p.Children.Add(new TextBlock { Text = "Keep PC awake", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2) });
            var choices = new[] { "Off", "30 minutes", "1 hour", "2 hours", "4 hours", "Until I turn it off" };
            var spans = new TimeSpan?[] { null, TimeSpan.FromMinutes(30), TimeSpan.FromHours(1), TimeSpan.FromHours(2), TimeSpan.FromHours(4), null };
            var statusText = new TextBlock { Foreground = Brushes.Gray, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) };
            Action showStatus = () => statusText.Text = Utilities.Describe();
            var g = LabeledRow(p, "Keep awake for");
            var c = new ComboBox { Width = 180, ItemsSource = choices, SelectedIndex = !Utilities.AwakeActive ? 0 : Utilities.AwakeForever ? 5 : -1 };
            c.SelectionChanged += (s, e) =>
            {
                int i = c.SelectedIndex;
                if (i == 0) Utilities.StopAwake();
                else if (i == 5) Utilities.KeepAwake(null);
                else if (i > 0) Utilities.KeepAwake(spans[i]);
                showStatus();
            };
            Grid.SetColumn(c, 1); g.Children.Add(c);
            p.Children.Add(statusText);
            showStatus();
            Check(p, "Also keep the screen on (not only the PC)", S.KeepAwakeScreen, b => S.KeepAwakeScreen = b);
            Hint(p, "Uses the standard Windows power request (like a video player does), no simulated input. Also on the tray menu and " + S.HotkeyAwake + ".");

            Gap(p); Gap(p);
            p.Children.Add(new TextBlock { Text = "Presence nudge (keeps Teams and similar apps \"Available\")", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2) });
            Check(p, "Nudge the mouse by 1 pixel when I'm idle", S.NudgeOn, b => S.NudgeOn = b);
            TimeText(p, "Only between (from)", S.NudgeFrom, v => S.NudgeFrom = v);
            TimeText(p, "...and (to)", S.NudgeTo, v => S.NudgeTo = v);
            Check(p, "Weekdays only", S.NudgeWeekdays, b => S.NudgeWeekdays = b);
            Number(p, "After this many idle minutes", S.NudgeIdleMin, 1, 30, v => S.NudgeIdleMin = v);
            Hint(p, "Teams switches to Away after about 5 minutes without input, so 4 minutes works well. The nudge only happens when you haven't touched the mouse or keyboard, and moves the pointer 1 px there and back.");
            Hint(p, "Note: simulated input may be against your organisation's policies and can be visible to device-monitoring software. Keep awake (above) does not use simulated input.");
            return p;
        }

        void TimeText(Panel p, string label, string v, Action<string> set)
        {
            var g = LabeledRow(p, label);
            var t = new TextBox { Width = 70, Text = v, Padding = new Thickness(2), HorizontalContentAlignment = HorizontalAlignment.Center };
            Action commit = () =>
            {
                TimeSpan ts;
                if (!Utilities.TryTime(t.Text, out ts)) { t.Text = v; return; }
                t.Text = ts.ToString(@"hh\:mm");
                if (t.Text != v) { v = t.Text; set(v); Changed(); }
            };
            t.LostFocus += (s, e) => commit();
            t.KeyDown += (s, e) => { if (e.Key == Key.Enter) commit(); };
            Grid.SetColumn(t, 1); g.Children.Add(t);
        }

        UIElement HotkeysPage()
        {
            var p = Page();
            Check(p, "Enable global hotkeys (work from any app)", S.HotkeysOn, b => S.HotkeysOn = b);
            Hint(p, "Click a box, then press the new shortcut. It must include Ctrl, Alt, Shift or Win. Backspace turns a shortcut off.");
            Gap(p);
            HotkeyBox(p, "Open / close dashboard", "dashboard", S.HotkeyDashboard, v => S.HotkeyDashboard = v);
            HotkeyBox(p, "Show / hide floating widget", "widget", S.HotkeyWidget, v => S.HotkeyWidget = v);
            HotkeyBox(p, "Turn click-through on / off", "clickthrough", S.HotkeyClickThrough, v => S.HotkeyClickThrough = v);
            HotkeyBox(p, "Keep PC awake on / off", "awake", S.HotkeyAwake, v => S.HotkeyAwake = v);
            Gap(p);
            var reset = new Button { Content = "Restore defaults", Padding = new Thickness(10, 3, 10, 3), HorizontalAlignment = HorizontalAlignment.Left };
            reset.Click += (s, e) =>
            {
                S.HotkeyDashboard = "Ctrl+Alt+M"; S.HotkeyWidget = "Ctrl+Alt+W"; S.HotkeyClickThrough = "Ctrl+Alt+C"; S.HotkeyAwake = "Ctrl+Alt+K";
                Program.Apply(); Build(tabs.SelectedIndex);
            };
            p.Children.Add(reset);
            return p;
        }

        void HotkeyBox(Panel p, string label, string name, string v, Action<string> set)
        {
            var g = LabeledRow(p, label);
            var t = new TextBox { Width = 180, Text = string.IsNullOrEmpty(v) ? "(off)" : v, IsReadOnly = true, Padding = new Thickness(2), HorizontalContentAlignment = HorizontalAlignment.Center, Cursor = Cursors.Hand };
            Grid.SetColumn(t, 1); g.Children.Add(t);
            var status = new TextBlock { FontSize = 11, Foreground = Brushes.IndianRed, Margin = new Thickness(0, -2, 0, 4), HorizontalAlignment = HorizontalAlignment.Right };
            p.Children.Add(status);
            Action showStatus = () =>
            {
                string st;
                status.Text = S.HotkeysOn && Hotkeys.Status.TryGetValue(name, out st) && st != "" && st != "off" ? "Not active: " + st : "";
            };
            showStatus();
            t.GotKeyboardFocus += (s, e) => { Hotkeys.Suspend(); t.Background = Brushes.LightYellow; };
            t.LostKeyboardFocus += (s, e) => { t.Background = Brushes.White; Changed(); showStatus(); };
            t.PreviewKeyDown += (s, e) =>
            {
                var key = e.Key == Key.System ? e.SystemKey : e.Key;
                if (key == Key.Tab) return;
                e.Handled = true;
                if (key == Key.Back || key == Key.Delete) { t.Text = "(off)"; set(""); return; }
                var hk = Hotkeys.FromKeyEvent(e);
                if (hk == null) return;
                t.Text = hk; set(hk);
                Changed(); Hotkeys.Suspend(); showStatus();
            };
        }

        // ---------------------------------------------------------------- helpers
        static StackPanel Page() { return new StackPanel { Margin = new Thickness(14, 12, 14, 14) }; }
        static void Gap(Panel p) { p.Children.Add(new Border { Height = 8 }); }

        static void Hint(Panel p, string text)
        {
            p.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 11.5, Margin = new Thickness(0, 2, 0, 4) });
        }

        void Check(Panel p, string text, bool v, Action<bool> set)
        {
            var c = new CheckBox { Content = text, IsChecked = v, Margin = new Thickness(0, 4, 0, 4) };
            c.Click += (s, e) => { set(c.IsChecked == true); Changed(); };
            p.Children.Add(c);
        }

        static Grid LabeledRow(Panel p, string label)
        {
            var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            p.Children.Add(g);
            return g;
        }

        void Combo(Panel p, string label, string[] items, int sel, Action<int> set)
        {
            var g = LabeledRow(p, label);
            var c = new ComboBox { Width = 180, ItemsSource = items, SelectedIndex = Math.Max(0, sel) };
            c.SelectionChanged += (s, e) => { if (c.SelectedIndex >= 0) { set(c.SelectedIndex); Changed(); } };
            Grid.SetColumn(c, 1); g.Children.Add(c);
        }

        void Text(Panel p, string label, string v, Action<string> set)
        {
            var g = LabeledRow(p, label);
            var t = new TextBox { Width = 180, Text = v, Padding = new Thickness(2) };
            Action commit = () => { if (t.Text != v) { v = t.Text; set(t.Text); Changed(); } };
            t.LostFocus += (s, e) => commit();
            t.KeyDown += (s, e) => { if (e.Key == Key.Enter) commit(); };
            Grid.SetColumn(t, 1); g.Children.Add(t);
        }

        void Number(Panel p, string label, int v, int min, int max, Action<int> set)
        {
            var g = LabeledRow(p, label);
            var t = new TextBox { Width = 70, Text = v.ToString(), Padding = new Thickness(2), HorizontalContentAlignment = HorizontalAlignment.Right };
            Action commit = () =>
            {
                int n;
                if (!int.TryParse(t.Text, out n)) { t.Text = v.ToString(); return; }
                n = Math.Max(min, Math.Min(max, n));
                t.Text = n.ToString();
                if (n != v) { v = n; set(n); Changed(); }
            };
            t.LostFocus += (s, e) => commit();
            t.KeyDown += (s, e) => { if (e.Key == Key.Enter) commit(); };
            Grid.SetColumn(t, 1); g.Children.Add(t);
        }
    }
}
