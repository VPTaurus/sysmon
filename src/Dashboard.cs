using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SysMonApp
{
    class Dashboard : Window
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);

        static Settings S { get { return Program.S; } }
        static readonly string[] Ranges = { "15 min", "1 hour", "6 hours", "24 hours", "7 days", "30 days" };
        static TimeSpan Span(string r)
        {
            switch (r)
            {
                case "15 min": return TimeSpan.FromMinutes(15);
                case "6 hours": return TimeSpan.FromHours(6);
                case "24 hours": return TimeSpan.FromHours(24);
                case "7 days": return TimeSpan.FromDays(7);
                case "30 days": return TimeSpan.FromDays(30);
                default: return TimeSpan.FromHours(1);
            }
        }
        bool Live { get { return S.DashRange == "15 min"; } }

        Theme T;
        List<Rec> recs = new List<Rec>();
        List<Anomaly> anoms = new List<Anomaly>();
        int loadId;
        bool fullscreen;
        WindowState prevState = WindowState.Maximized;
        DateTime lastLiveDraw;

        readonly Dictionary<string, TextBlock[]> cards = new Dictionary<string, TextBlock[]>();
        TimeChart cCpu, cRam, cGpu, cDisk, cNet, cPing;
        StackPanel anomList, rangeBar;
        TextBlock anomTitle, subtitle, status, fsLabel, utilText, dashClock, dashDate;
        TextBlock[,] procCells;
        TextBlock procTitle, focusText;
        Border focusBar;
        Anomaly focus;                 // anomaly the charts are zoomed to (null = whole range)
        List<ProcInfo> focusProcs;     // processes logged around the focused anomaly
        DateTime focusProcsTime;
        string procSort = "cpu";
        CoreBars cores;

        public Dashboard()
        {
            Title = "SysMon Dashboard";
            Width = 1440; Height = 900; MinWidth = 1000; MinHeight = 640;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowState = WindowState.Maximized;
            FontFamily = new FontFamily("Segoe UI"); FontSize = 12;
            UseLayoutRounding = true;
            try { Icon = Imaging.CreateBitmapSourceFromHIcon(Program.Tray.Icon.Handle, Int32Rect.Empty, null); } catch { }
            SourceInitialized += (s, e) => DarkTitle();
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.F11) { ToggleFullscreen(); e.Handled = true; }
                else if (e.Key == Key.Escape && procView != null) { CloseProcess(); e.Handled = true; }
                else if (e.Key == Key.Escape && expanded >= 0) { ToggleExpand(expanded); e.Handled = true; }
                else if (e.Key == Key.Escape && focus != null) { Unfocus(); e.Handled = true; }
                else if (e.Key == Key.Escape && fullscreen) { ToggleFullscreen(); e.Handled = true; }
            };
            Build();
            Load();
        }

        void DarkTitle()
        {
            try
            {
                int on = T.IsLight ? 0 : 1;
                DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref on, 4);
            }
            catch { }
        }

        public void ApplyTheme() { Build(); Redraw(); DarkTitle(); }

        // ---------------------------------------------------------------- layout
        TextBlock Txt(string s, Brush b, double size = 12, bool bold = false)
        {
            return new TextBlock { Text = s, Foreground = b, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center };
        }

        Border Panel(UIElement child, Thickness margin)
        {
            return new Border { Background = T.Track, CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10, 12, 10), Margin = margin, Child = child };
        }

        Border Pill(string text, bool selected, Action click)
        {
            var b = new Border
            {
                CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 4, 12, 5), Margin = new Thickness(3, 0, 3, 0), Cursor = Cursors.Hand,
                Background = selected ? Theme.Fill(T.Cpu, 0x40) : T.Track,
                BorderBrush = selected ? T.Cpu : Brushes.Transparent, BorderThickness = new Thickness(1),
                Child = Txt(text, T.Fg, 12, selected)
            };
            b.MouseEnter += (s, e) => { if (!selected) b.Background = Theme.Fill(T.Fg, 0x20); };
            b.MouseLeave += (s, e) => { if (!selected) b.Background = T.Track; };
            b.MouseLeftButtonUp += (s, e) => click();
            return b;
        }

        void Build()
        {
            T = Theme.Get(S.Theme);
            Background = T.Bg; Foreground = T.Fg;
            cards.Clear();

            var root = new Grid { Margin = new Thickness(16, 12, 16, 8) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // header
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // cards
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // zoom bar
            root.RowDefinitions.Add(new RowDefinition());                              // charts + side
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // status

            // header
            var hdr = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var titleSp = new StackPanel();
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            titleRow.Children.Add(Txt("SysMon", T.Fg, 22, true));
            dashClock = Txt("", T.Fg, 22);
            dashClock.Margin = new Thickness(18, 0, 0, 0);
            dashDate = Txt("", T.Muted, 13);
            dashDate.Margin = new Thickness(10, 0, 0, 3); dashDate.VerticalAlignment = VerticalAlignment.Bottom;
            titleRow.Children.Add(dashClock); titleRow.Children.Add(dashDate);
            titleSp.Children.Add(titleRow);
            subtitle = Txt("", T.Faint, 12);
            titleSp.Children.Add(subtitle);
            DockPanel.SetDock(titleSp, Dock.Left);
            hdr.Children.Add(titleSp);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            actions.Children.Add(Pill("Export CSV", false, Export));
            actions.Children.Add(Pill("Logs folder", false, () => { Directory.CreateDirectory(LogStore.Dir); Process.Start("explorer.exe", "\"" + LogStore.Dir + "\""); }));
            actions.Children.Add(Pill("Settings", false, Program.ShowSettings));
            var fs = Pill(fullscreen ? "Exit fullscreen (Esc)" : "Fullscreen (F11)", false, ToggleFullscreen);
            fsLabel = (TextBlock)fs.Child;
            actions.Children.Add(fs);
            DockPanel.SetDock(actions, Dock.Right);
            hdr.Children.Add(actions);

            rangeBar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            BuildRangeBar();
            hdr.Children.Add(rangeBar);
            root.Children.Add(hdr);

            // summary cards
            var cardGrid = new UniformGrid { Columns = 8, Rows = 1, Margin = new Thickness(-4, 0, -4, 8) };
            AddCard(cardGrid, "cpu", "CPU", T.Cpu);
            AddCard(cardGrid, "ram", "MEMORY", T.Ram);
            AddCard(cardGrid, "gpu", "GPU", T.Gpu);
            AddCard(cardGrid, "disk", "DISK I/O", T.Read);
            AddCard(cardGrid, "rx", "DOWNLOAD", T.Down);
            AddCard(cardGrid, "tx", "UPLOAD", T.Up);
            AddCard(cardGrid, "ping", "PING", T.Ping);
            AddCard(cardGrid, "bat", "BATTERY", T.Good);
            Grid.SetRow(cardGrid, 1);
            root.Children.Add(cardGrid);

            var fb = new DockPanel();
            var back = Pill("Back to full range (Esc)", false, Unfocus);
            DockPanel.SetDock(back, Dock.Right);
            fb.Children.Add(back);
            focusText = Txt("", T.Fg, 12.5);
            focusText.TextTrimming = TextTrimming.CharacterEllipsis;
            fb.Children.Add(focusText);
            focusBar = new Border
            {
                Child = fb, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(12, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 8), Visibility = Visibility.Collapsed
            };
            Grid.SetRow(focusBar, 2);
            root.Children.Add(focusBar);

            // main area
            var main = new Grid();
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star), MinWidth = 330 });
            Grid.SetRow(main, 3);
            root.Children.Add(main);

            var charts = new UniformGrid { Columns = 2, Rows = 3, Margin = new Thickness(-4, -4, 4, -4) };
            chartGrid = charts;
            chartPanels.Clear(); chartStats.Clear(); expandLabels.Clear(); chartTitles.Clear();
            Func<double, string> pct = Fmt.Pct, rate = v => Fmt.Rate(v, false), ms = v => v.ToString("0") + " ms";
            cCpu = AddChart(charts, "CPU", 100, 1, pct, new[] { "average", "peak" }, new[] { T.Cpu, Theme.Fill(T.Cpu, 0x99) });
            cRam = AddChart(charts, "Memory", 100, 1, pct, new[] { "used" }, new[] { T.Ram });
            cGpu = AddChart(charts, "GPU", 100, 1, pct, new[] { "average", "peak" }, new[] { T.Gpu, Theme.Fill(T.Gpu, 0x99) });
            cDisk = AddChart(charts, "Disk I/O", double.NaN, 1 << 20, rate, new[] { "read", "write" }, new[] { T.Read, T.Write });
            cNet = AddChart(charts, "Network", double.NaN, 64 << 10, rate, new[] { "download", "upload" }, new[] { T.Down, T.Up });
            cPing = AddChart(charts, "Ping  " + S.PingTarget, double.NaN, 20, ms, new[] { "average", "peak", "packet loss" }, new[] { T.Ping, Theme.Fill(T.Ping, 0x99), T.Bad });
            pCpu = AddChart(charts, "Process CPU", double.NaN, 5, v => v.ToString("0.#") + "%", new[] { "share of total CPU" }, new[] { T.Cpu });
            pMem = AddChart(charts, "Process memory", double.NaN, 50 << 20, Fmt.Size, new[] { "private memory" }, new[] { T.Ram });
            main.Children.Add(charts);
            ApplyExpand();

            // side panel
            var side = new Grid();
            Grid.SetColumn(side, 1);
            side.RowDefinitions.Add(new RowDefinition { MinHeight = 120 });
            side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            main.Children.Add(side);

            var ap = new DockPanel();
            anomTitle = Txt("ANOMALIES", T.Muted, 11, true);
            anomTitle.Margin = new Thickness(0, 0, 0, 6);
            DockPanel.SetDock(anomTitle, Dock.Top);
            ap.Children.Add(anomTitle);
            anomList = new StackPanel();
            ap.Children.Add(new ScrollViewer { Content = anomList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            side.Children.Add(Panel(ap, new Thickness(4, 0, 0, 8)));

            var pp = BuildProcTable();
            var ppb = Panel(pp, new Thickness(4, 0, 0, 8));
            Grid.SetRow(ppb, 1);
            side.Children.Add(ppb);

            var cp = new StackPanel();
            cp.Children.Add(Txt("CPU CORES", T.Muted, 11, true));
            cores = new CoreBars { Height = 44, Margin = new Thickness(0, 6, 0, 0), Bar = Theme.Fill(T.Cpu, 0xCC), Track = T.Track, Text = T.Fg, HoverBg = T.HoverBg, Warn = T.Warn, Bad = T.Bad };
            cp.Children.Add(cores);
            var cpb = Panel(cp, new Thickness(4, 0, 0, 0));
            Grid.SetRow(cpb, 2);
            side.Children.Add(cpb);

            var statusRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            utilText = Txt("", T.Faint, 11);
            DockPanel.SetDock(utilText, Dock.Right);
            statusRow.Children.Add(utilText);
            status = Txt("", T.Faint, 11);
            status.TextTrimming = TextTrimming.CharacterEllipsis;
            statusRow.Children.Add(status);
            Grid.SetRow(statusRow, 4);
            root.Children.Add(statusRow);

            Content = root;
            UpdateSubtitle();
            RefreshHeader();
        }

        public void SetRange(string r)
        {
            S.DashRange = r; Program.SaveSettings();
            focus = null; focusProcs = null;
            BuildRangeBar(); Load();
            if (procView != null) ShowProcess(procView);   // reload its history for the new range
        }

        void BuildRangeBar()
        {
            rangeBar.Children.Clear();
            foreach (var r in Ranges)
            {
                string r2 = r;
                rangeBar.Children.Add(Pill(r == "15 min" ? "Live 15 min" : r, S.DashRange == r, () => SetRange(r2)));
            }
        }

        void AddCard(Panel p, string key, string title, Brush accent)
        {
            var sp = new StackPanel();
            sp.Children.Add(Txt(title, T.Muted, 11, true));
            var v = Txt("-", accent, 22, true);
            var sub = Txt("", T.Faint, 10.5);
            sub.TextTrimming = TextTrimming.CharacterEllipsis;
            sp.Children.Add(v); sp.Children.Add(sub);
            p.Children.Add(Panel(sp, new Thickness(4, 0, 4, 0)));
            cards[key] = new[] { v, sub };
        }

        // ---- expandable charts
        UniformGrid chartGrid;
        readonly List<Border> chartPanels = new List<Border>();
        readonly List<TextBlock> chartStats = new List<TextBlock>(), expandLabels = new List<TextBlock>();
        int expanded = -1;   // index of the chart filling the whole chart area, -1 = grid
        TimeChart[] Charts { get { return new[] { cCpu, cRam, cGpu, cDisk, cNet, cPing }; } }

        TimeChart AddChart(Panel p, string title, double fixedMax, double minMax, Func<double, string> fmt, string[] names, Brush[] colors)
        {
            int idx = chartPanels.Count;
            var dp = new DockPanel();
            var headRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };

            var btnText = Txt("⤢", T.Muted, 12, true);
            var btn = new Border { Child = btnText, Padding = new Thickness(8, 1, 8, 2), CornerRadius = new CornerRadius(10), Cursor = Cursors.Hand,
                                   Background = Brushes.Transparent, ToolTip = "Expand this graph (or double-click it)" };
            btn.MouseEnter += (s, e) => btn.Background = Theme.Fill(T.Fg, 0x20);
            btn.MouseLeave += (s, e) => btn.Background = Brushes.Transparent;
            btn.MouseLeftButtonUp += (s, e) => ToggleExpand(idx);
            DockPanel.SetDock(btn, Dock.Right);
            headRow.Children.Add(btn);

            var stats = Txt("", T.Fg, 11.5);
            stats.Margin = new Thickness(0, 0, 12, 0);
            stats.Visibility = Visibility.Collapsed;
            DockPanel.SetDock(stats, Dock.Right);
            headRow.Children.Add(stats);

            var head = new StackPanel { Orientation = Orientation.Horizontal };
            var titleText = Txt(title.ToUpper(), T.Muted, 11, true);
            head.Children.Add(titleText);
            chartTitles.Add(titleText);
            for (int i = 0; i < names.Length; i++)
            {
                head.Children.Add(new Rectangle { Width = 10, Height = 3, Fill = colors[i], Margin = new Thickness(14, 1, 5, 0), VerticalAlignment = VerticalAlignment.Center });
                head.Children.Add(Txt(names[i], T.Faint, 11));
            }
            headRow.Children.Add(head);
            DockPanel.SetDock(headRow, Dock.Top);
            dp.Children.Add(headRow);

            var c = new TimeChart { FixedMax = fixedMax, MinMax = minMax, Format = fmt, Th = T };
            c.MarkClicked = a => { if (SameAnomaly(a, focus)) Unfocus(); else FocusOn(a); };
            c.MouseLeftButtonDown += (s, e) => { if (e.ClickCount == 2) { ToggleExpand(idx); e.Handled = true; } };
            dp.Children.Add(c);
            var b = Panel(dp, new Thickness(4));
            b.Padding = new Thickness(8, 8, 10, 4);
            p.Children.Add(b);
            chartPanels.Add(b); chartStats.Add(stats); expandLabels.Add(btnText);
            return c;
        }

        void ToggleExpand(int i)
        {
            if (i >= 6) { CloseProcess(); return; }   // the two process charts close the process view
            expanded = expanded == i ? -1 : i;
            ApplyExpand();
            UpdateLayout();   // so the chart's new width is used for the data resolution
            Redraw();
        }

        // panels 0-5: system charts (grid or one expanded); panels 6-7: process CPU / memory (process view)
        void ApplyExpand()
        {
            if (chartGrid == null) return;
            bool proc = procView != null, any = expanded >= 0 && !proc;
            chartGrid.Rows = proc ? 2 : any ? 1 : 3; chartGrid.Columns = proc || any ? 1 : 2;
            var charts = Charts;
            for (int j = 0; j < chartPanels.Count; j++)
            {
                bool isProc = j >= 6, me = isProc ? proc : any && j == expanded;
                chartPanels[j].Visibility = (isProc ? proc : !proc && (!any || me)) ? Visibility.Visible : Visibility.Collapsed;
                if (!isProc) charts[j].YTicks = me ? 8 : 4;
                expandLabels[j].Text = me ? "✕  Back to all graphs (Esc)" : "⤢";
                chartStats[j].Visibility = me ? Visibility.Visible : Visibility.Collapsed;
            }
            if (proc)
            {
                chartTitles[6].Text = procView.ToUpper() + "  ·  CPU";
                chartTitles[7].Text = procView.ToUpper() + "  ·  MEMORY";
            }
        }

        // ---------------------------------------------------------------- process history view
        string procView;                                   // process shown in the two process charts, null = off
        List<ProcPoint> procLog = new List<ProcPoint>();   // points from the per-minute process log
        int procLoadId;
        TimeChart pCpu, pMem;
        readonly List<TextBlock> chartTitles = new List<TextBlock>();

        void ShowProcess(string name)
        {
            procView = name; expanded = -1; procLog = new List<ProcPoint>();
            int id = ++procLoadId;
            if (!Live && S.Logging)
            {
                var to = DateTime.Now; var from = to - Span(S.DashRange);
                Task.Factory.StartNew(() =>
                {
                    List<ProcPoint> l;
                    try { l = LogStore.LoadProcessHistory(name, from, to); } catch { l = new List<ProcPoint>(); }
                    Dispatcher.BeginInvoke(new Action(() => { if (id == procLoadId && procView == name) { procLog = l; Redraw(); } }));
                });
            }
            ApplyExpand();
            UpdateLayout();
            Redraw();
        }

        void CloseProcess()
        {
            procView = null; procLoadId++;
            ApplyExpand();
            UpdateLayout();
            Redraw();
        }

        static void Thin(List<ProcPoint> pts, int n, out double[] t, out double[] cpu, out double[] mem)
        {
            int step = Math.Max(1, (int)Math.Ceiling(pts.Count / (double)n));
            var lt = new List<double>(); var lc = new List<double>(); var lm = new List<double>();
            for (int i = 0; i < pts.Count; i += step)
            {
                int e = Math.Min(pts.Count, i + step);
                double c = 0, m = 0;
                for (int k = i; k < e; k++) { c += pts[k].Cpu; m = Math.Max(m, pts[k].Mem); }
                lt.Add(TimeChart.Sec(pts[i].T)); lc.Add(c / (e - i)); lm.Add(m);
            }
            t = lt.ToArray(); cpu = lc.ToArray(); mem = lm.ToArray();
        }

        void RenderProc(DateTime from, DateTime to)
        {
            if (procView == null || pCpu == null) return;
            // logged minutes, then the in-memory 2-second history for the most recent part
            var pts = procLog.Where(p => p.T >= from && p.T <= to).ToList();
            DateTime lastT = pts.Count > 0 ? pts[pts.Count - 1].T : DateTime.MinValue;
            List<ProcPoint> recent;
            if (Program.ProcHistory.TryGetValue(procView, out recent))
                pts.AddRange(recent.Where(p => p.T > lastT && p.T >= from && p.T <= to));

            double[] t, cpu, mem;
            Thin(pts, Math.Max(200, (int)Math.Min(1500, pCpu.ActualWidth > 0 ? pCpu.ActualWidth : 1000)), out t, out cpu, out mem);
            double gap = Live ? 8 : 150;
            var marks = anoms.Where(a => a.Message.IndexOf(procView, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            foreach (var c in new[] { pCpu, pMem })
            {
                c.From = from; c.To = to; c.GapSec = gap; c.YTicks = 6; c.Marks = marks; c.Selected = focus;
                c.Series.Clear();
            }
            pCpu.Series.Add(new ChartSeries { Name = "CPU", Stroke = T.Cpu, T = t, V = cpu });
            pMem.Series.Add(new ChartSeries { Name = "memory", Stroke = T.Ram, T = t, V = mem });
            pCpu.InvalidateVisual(); pMem.InvalidateVisual();

            string src = Live || procLog.Count == 0 ? "2-second samples" : "per-minute log (gaps = not in the top 10 then)";
            Func<double[], Func<double, string>, string> st = (v, f) =>
                v.Length == 0 ? "no data in this period" : "min " + f(v.Min()) + "   avg " + f(v.Average()) + "   max " + f(v.Max());
            chartStats[6].Text = st(cpu, x => x.ToString("0.0") + "%") + "      " + src;
            chartStats[7].Text = st(mem, Fmt.Size) + (mem.Length > 1 ? "      change " + (mem[mem.Length - 1] >= mem[0] ? "+" : "-") + Fmt.Size(Math.Abs(mem[mem.Length - 1] - mem[0])) : "");
        }

        // min / avg / max of each series in the visible window, shown in an expanded chart's header
        void UpdateExpandedStats()
        {
            if (expanded < 0) return;
            var c = Charts[expanded];
            var parts = new List<string>();
            foreach (var s in c.Series)
            {
                var v = s.V.Where(x => !double.IsNaN(x)).ToList();
                if (v.Count == 0) continue;
                parts.Add((s.Name ?? "") + ":  min " + c.Format(v.Min()) + "   avg " + c.Format(v.Average()) + "   max " + c.Format(v.Max()));
            }
            chartStats[expanded].Text = parts.Count > 0 ? string.Join("        ", parts) : "no data";
        }

        UIElement BuildProcTable()
        {
            var sp = new StackPanel();
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
            int rows = 13;
            procCells = new TextBlock[rows, 3];
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });
            procTitle = Txt("PROCESSES", T.Muted, 11, true);
            procTitle.TextTrimming = TextTrimming.CharacterEllipsis;
            g.Children.Add(procTitle);
            AddSortHeader(g, "CPU", "cpu", 1);
            AddSortHeader(g, "MEMORY", "mem", 2);
            for (int r = 0; r < rows; r++)
            {
                g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(19) });
                for (int c = 0; c < 3; c++)
                {
                    var t = Txt("", c == 0 ? T.Fg : c == 1 ? T.Cpu : T.Ram, 12, c > 0);
                    t.TextTrimming = TextTrimming.CharacterEllipsis;
                    if (c > 0) t.HorizontalAlignment = HorizontalAlignment.Right;
                    Grid.SetRow(t, r + 1); Grid.SetColumn(t, c);
                    g.Children.Add(t);
                    procCells[r, c] = t;
                }
            }
            // click a row: show that process's CPU / memory history in the chart area
            g.Background = Brushes.Transparent;
            g.MouseMove += (s, e) =>
            {
                int r = (int)((e.GetPosition(g).Y - 22) / 19);
                g.Cursor = r >= 0 && r < rows && procCells[r, 0].Text != "" ? Cursors.Hand : null;
            };
            g.MouseLeftButtonUp += (s, e) =>
            {
                int r = (int)((e.GetPosition(g).Y - 22) / 19);
                if (r < 0 || r >= rows || procCells[r, 0].Text == "") return;
                string name = procCells[r, 0].Text;
                if (string.Equals(name, procView, StringComparison.OrdinalIgnoreCase)) CloseProcess(); else ShowProcess(name);
            };
            sp.Children.Add(g);
            sp.Children.Add(new TextBlock { Text = "Click a process to see its history", Foreground = T.Faint, FontSize = 10.5, Margin = new Thickness(0, 4, 0, 0) });
            return sp;
        }

        void AddSortHeader(Grid g, string text, string key, int col)
        {
            var t = Txt(text + (procSort == key ? " ▾" : ""), procSort == key ? T.Fg : T.Muted, 11, true);
            t.HorizontalAlignment = HorizontalAlignment.Right; t.Cursor = Cursors.Hand;
            t.MouseLeftButtonUp += (s, e) => { procSort = key; Build(); Redraw(); };
            Grid.SetColumn(t, col);
            g.Children.Add(t);
        }

        public void RefreshHeader()
        {
            if (dashClock == null) return;
            var now = DateTime.Now;
            dashClock.Visibility = dashDate.Visibility = S.ShowClock ? Visibility.Visible : Visibility.Collapsed;
            dashClock.Text = now.ToString(S.ClockSeconds ? "T" : "t");
            dashDate.Text = S.ClockDate ? now.ToString("dddd, d MMMM") : "";
            string badge = Utilities.Badge();
            utilText.Text = Utilities.Describe();
            utilText.Foreground = badge != null ? T.Up : T.Faint;
        }

        void UpdateSubtitle()
        {
            var s = Program.LastSnap;
            string mem = s != null ? s.MemTotalGB.ToString("0") + " GB RAM" : "";
            subtitle.Text = Environment.MachineName + Fmt.Dot + Program.CpuName + Fmt.Dot + Environment.ProcessorCount + " threads" +
                            (mem != "" ? Fmt.Dot + mem : "") + Fmt.Dot + "up " + Fmt.Duration(Native.GetTickCount64() / 1000.0) +
                            Fmt.Dot + "SysMon " + Program.Version;
        }

        // ---------------------------------------------------------------- data
        void Load()
        {
            int id = ++loadId;
            focus = null; focusProcs = null;
            var to = DateTime.Now; var from = to - Span(S.DashRange);
            if (Live)
            {
                recs = Program.Live.Where(r => r.T >= from).ToList();
                anoms = Program.Detector.Recent.Where(a => a.T >= from).ToList();
                Redraw();
                return;
            }
            status.Text = "Loading " + S.DashRange + " of history...";
            bool logging = S.Logging;
            var session = Program.SessionRecs.ToList();
            var recent = Program.Detector.Recent.ToList();
            Task.Factory.StartNew(() =>
            {
                List<Rec> r; List<Anomaly> a;
                try
                {
                    r = logging ? LogStore.Load(from, to) : new List<Rec>();
                    DateTime lastT = r.Count > 0 ? r[r.Count - 1].T : DateTime.MinValue;
                    r.AddRange(session.Where(x => x.T > lastT && x.T >= from));
                    a = logging ? LogStore.LoadAnomalies(from, to) : new List<Anomaly>();
                    var known = new HashSet<string>(a.Select(x => x.T.ToString("s") + x.Message));
                    a.AddRange(recent.Where(x => x.T >= from && !known.Contains(x.T.ToString("s") + x.Message)));
                }
                catch (Exception ex) { Store.Log("dashboard load: " + ex); r = new List<Rec>(); a = new List<Anomaly>(); }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (id != loadId) return;
                    recs = r; anoms = a;
                    Redraw();
                }));
            });
        }

        public void OnSample(Snapshot s, Rec live)
        {
            UpdateCurrent(s);
            if (Live)
            {
                recs.Add(live);
                var from = DateTime.Now - Span(S.DashRange);
                int cut = recs.FindIndex(r => r.T >= from);
                if (cut > 0) recs.RemoveRange(0, cut);
                if ((DateTime.Now - lastLiveDraw).TotalMilliseconds >= 900) Redraw();
            }
        }

        public void OnBucket(Rec r)
        {
            if (Live) return;
            recs.Add(r);
            var from = DateTime.Now - Span(S.DashRange);
            int cut = recs.FindIndex(x => x.T >= from);
            if (cut > 0) recs.RemoveRange(0, cut);
            Redraw();
        }

        public void OnAnomaly(Anomaly a) { anoms.Add(a); Redraw(); }

        void UpdateCurrent(Snapshot s)
        {
            if (s == null) return;
            cards["cpu"][0].Text = Fmt.Pct(s.Cpu);
            cards["ram"][0].Text = Fmt.Pct(s.MemLoad);
            cards["gpu"][0].Text = s.HasGpu ? Fmt.Pct(s.Gpu) : "n/a";
            cards["disk"][0].Text = Fmt.Rate(s.DiskRead + s.DiskWrite, false);
            cards["rx"][0].Text = Fmt.Rate(s.Rx, S.Bits);
            cards["tx"][0].Text = Fmt.Rate(s.Tx, S.Bits);
            double p = Program.LastPing;
            cards["ping"][0].Text = double.IsNaN(p) ? "timeout" : p.ToString("0") + " ms";
            cards["bat"][0].Text = s.HasBattery ? s.BatPct + "%" + (s.Charging ? " +" : "") : "none";
            cores.Values = s.Cores; cores.InvalidateVisual();

            if (s.Procs != null) { lastProcs = s.Procs; if (focus == null) ShowProcs(); }
            if (DateTime.Now.Second % 30 == 0) UpdateSubtitle();
        }

        // ---------------------------------------------------------------- render
        static void Downsample(List<Rec> recs, DateTime from, DateTime to, int n, Func<Rec, double> sel, bool useMax, out double[] T, out double[] V)
        {
            var tl = new List<double>(); var vl = new List<double>();
            if (recs.Count <= n)
            {
                foreach (var r in recs) { double v = sel(r); if (double.IsNaN(v)) continue; tl.Add(TimeChart.Sec(r.T)); vl.Add(v); }
            }
            else
            {
                long w = Math.Max(1, (to - from).Ticks / n);
                long cur = long.MinValue; double acc = 0; int cnt = 0;
                Action flush = () =>
                {
                    if (cnt == 0) return;
                    tl.Add((from.Ticks + cur * w + w / 2) / 1e7);
                    vl.Add(useMax ? acc : acc / cnt);
                };
                foreach (var r in recs)
                {
                    double v = sel(r);
                    if (double.IsNaN(v)) continue;
                    long b = (r.T.Ticks - from.Ticks) / w;
                    if (b != cur) { flush(); cur = b; acc = useMax ? double.MinValue : 0; cnt = 0; }
                    acc = useMax ? Math.Max(acc, v) : acc + v;
                    cnt++;
                }
                flush();
            }
            T = tl.ToArray(); V = vl.ToArray();
        }

        static string Stats(IEnumerable<double> values, Func<double, string> f, bool low = false)
        {
            var v = values.Where(x => !double.IsNaN(x)).OrderBy(x => x).ToList();
            if (v.Count == 0) return "no data";
            double p95 = v[Math.Min(v.Count - 1, (int)(v.Count * 0.95))];
            return low ? "avg " + f(v.Average()) + Fmt.Dot + "min " + f(v[0])
                       : "avg " + f(v.Average()) + Fmt.Dot + "p95 " + f(p95) + Fmt.Dot + "max " + f(v[v.Count - 1]);
        }

        void SetChart(TimeChart c, DateTime from, DateTime to, double gap, string[] metrics, params Tuple<Func<Rec, double>, bool>[] series)
        {
            int n = Math.Max(100, (int)Math.Min(1200, c.ActualWidth > 0 ? c.ActualWidth : 800));
            c.From = from; c.To = to; c.GapSec = gap;
            var legend = new[] { "average", "peak" };
            var old = c.Series.ToList();
            c.Series.Clear();
            for (int i = 0; i < series.Length; i++)
            {
                double[] t, v;
                Downsample(view, from, to, n, series[i].Item1, series[i].Item2, out t, out v);
                var cs = new ChartSeries { T = t, V = v };
                c.Series.Add(cs);
            }
            c.Marks = anoms.Where(a => metrics.Contains(a.Metric)).ToList();
            c.InvalidateVisual();
        }

        void StyleChart(TimeChart c, params object[] spec)   // name, brush, fill, dashed ...
        {
            for (int i = 0; i < c.Series.Count && i * 4 + 3 < spec.Length; i++)
            {
                c.Series[i].Name = (string)spec[i * 4]; c.Series[i].Stroke = (Brush)spec[i * 4 + 1];
                c.Series[i].Fill = (bool)spec[i * 4 + 2]; c.Series[i].Dashed = (bool)spec[i * 4 + 3];
            }
        }

        static Tuple<Func<Rec, double>, bool> Sr(Func<Rec, double> f, bool max) { return Tuple.Create(f, max); }

        List<Rec> view = new List<Rec>();   // records inside the visible window

        void Redraw()
        {
            lastLiveDraw = DateTime.Now;
            var to = DateTime.Now; var from = to - Span(S.DashRange);
            double gap = Live ? 5 : Math.Max(S.LogIntervalSec * 3.5, (to - from).TotalSeconds / 1200 * 2.5);
            view = recs;
            if (focus != null)
            {
                // zoom: a window around the anomaly (wider for logged history than for the live view)
                from = focus.T - TimeSpan.FromMinutes(Live ? 4 : 20);
                to = focus.T + TimeSpan.FromMinutes(Live ? 2 : 10);
                if (to > DateTime.Now) to = DateTime.Now;
                if (to - from < TimeSpan.FromMinutes(3)) from = to - TimeSpan.FromMinutes(3);
                view = recs.Where(r => r.T >= from && r.T <= to).ToList();
                gap = Live ? 5 : S.LogIntervalSec * 3.5;
            }
            UpdateFocusBar(from, to);
            foreach (var c in new[] { cCpu, cRam, cGpu, cDisk, cNet, cPing }) c.Selected = focus;

            SetChart(cCpu, from, to, gap, new[] { "cpu", "battery" }, Sr(r => r.Cpu, false), Sr(r => r.CpuMax, true));
            StyleChart(cCpu, "avg", T.Cpu, true, false, "peak", Theme.Fill(T.Cpu, 0x99), false, true);
            SetChart(cRam, from, to, gap, new[] { "ram" }, Sr(r => r.Ram, false));
            StyleChart(cRam, "used", T.Ram, true, false);
            SetChart(cGpu, from, to, gap, new[] { "gpu" }, Sr(r => r.Gpu, false), Sr(r => r.GpuMax, true));
            StyleChart(cGpu, "avg", T.Gpu, true, false, "peak", Theme.Fill(T.Gpu, 0x99), false, true);
            SetChart(cDisk, from, to, gap, new[] { "disk" }, Sr(r => r.DiskR, false), Sr(r => r.DiskW, false));
            StyleChart(cDisk, "read", T.Read, true, false, "write", T.Write, true, false);
            SetChart(cNet, from, to, gap, new[] { "rx", "tx" }, Sr(r => r.Rx, false), Sr(r => r.Tx, false));
            StyleChart(cNet, "↓", T.Down, true, false, "↑", T.Up, true, false);
            SetChart(cPing, from, to, gap, new[] { "ping" }, Sr(r => r.Ping, false), Sr(r => r.PingMax, true));
            StyleChart(cPing, "avg", T.Ping, true, false, "peak", Theme.Fill(T.Ping, 0x99), false, true);
            cPing.LossT = view.Where(r => r.PingLoss > 0).Select(r => TimeChart.Sec(r.T)).ToArray();
            UpdateExpandedStats();
            RenderProc(from, to);

            Func<double, string> rate = v => Fmt.Rate(v, false), ms = v => v.ToString("0") + " ms";
            cards["cpu"][1].Text = Stats(view.Select(r => r.Cpu), Fmt.Pct);
            cards["ram"][1].Text = Stats(view.Select(r => r.Ram), Fmt.Pct);
            cards["gpu"][1].Text = Stats(view.Select(r => r.Gpu), Fmt.Pct);
            cards["disk"][1].Text = Stats(view.Select(r => r.DiskR + r.DiskW), rate);
            cards["rx"][1].Text = "total " + Fmt.Size(view.Sum(r => r.RxBytes)) + Fmt.Dot + Stats(view.Select(r => r.Rx), rate).Split(new[] { Fmt.Dot }, StringSplitOptions.None).Last();
            cards["tx"][1].Text = "total " + Fmt.Size(view.Sum(r => r.TxBytes)) + Fmt.Dot + Stats(view.Select(r => r.Tx), rate).Split(new[] { Fmt.Dot }, StringSplitOptions.None).Last();
            var pl = view.Where(r => !double.IsNaN(r.PingLoss)).ToList();
            cards["ping"][1].Text = Stats(view.Select(r => r.Ping), ms) +
                                    (pl.Count > 0 ? Fmt.Dot + "loss " + Fmt.Pct(pl.Average(r => r.PingLoss)) : "");
            cards["bat"][1].Text = Stats(view.Select(r => r.Bat), v => v.ToString("0") + "%", true);
            foreach (var kv in cards) kv.Value[1].ToolTip = kv.Value[1].Text;

            RenderAnomalies();
            UpdateCurrent(Program.LastSnap);
            ShowProcs();
            status.Text = (Live ? "Live view (1-second samples)" : recs.Count.ToString("N0") + " samples (" + S.LogIntervalSec + " s each)") +
                          Fmt.Dot + (S.Logging ? "Logging on, " + Fmt.Size(LogStore.SizeBytes()) + " in " + LogStore.Dir : "Logging is off - history only covers this session") +
                          Fmt.Dot + (S.Anomalies ? "Anomaly detection on (" + S.AnomalySensitivity.ToLower() + " sensitivity)" : "Anomaly detection off");
        }

        void RenderAnomalies()
        {
            anomList.Children.Clear();
            var list = anoms.OrderByDescending(a => a.T).Take(200).ToList();
            anomTitle.Text = "ANOMALIES  (" + anoms.Count + " in " + (Live ? "the last 15 min" : "the last " + S.DashRange) + ")";
            if (list.Count == 0)
            {
                anomList.Children.Add(new TextBlock
                {
                    Text = S.Anomalies ? "Nothing unusual detected in this period." : "Anomaly detection is turned off in Settings.",
                    Foreground = T.Faint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0)
                });
                return;
            }
            bool multiDay = Span(S.DashRange).TotalHours > 24;
            foreach (var a in list)
            {
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                var br = a.Severity == "critical" ? T.Bad : a.Severity == "warning" ? T.Warn : T.Cpu;
                g.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = br, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 0, 0) });
                var sp = new StackPanel();
                Grid.SetColumn(sp, 1);
                sp.Children.Add(new TextBlock { Text = a.T.ToString(multiDay ? "ddd dd.MM HH:mm" : "HH:mm:ss") + "   " + a.Severity.ToUpper(), Foreground = T.Faint, FontSize = 10.5 });
                sp.Children.Add(new TextBlock { Text = a.Message, Foreground = T.Fg, TextWrapping = TextWrapping.Wrap });
                g.Children.Add(sp);

                bool sel = SameAnomaly(a, focus);
                var item = new Border
                {
                    Child = g, Padding = new Thickness(6, 4, 6, 5), Margin = new Thickness(-6, 0, 0, 2), CornerRadius = new CornerRadius(6),
                    Cursor = Cursors.Hand, Background = sel ? Theme.Fill(br, 0x30) : Brushes.Transparent,
                    ToolTip = "Click to zoom the charts to this moment and see the processes running then"
                };
                var a2 = a;
                item.MouseEnter += (s, e) => { if (!sel) item.Background = T.Track; };
                item.MouseLeave += (s, e) => { if (!sel) item.Background = Brushes.Transparent; };
                item.MouseLeftButtonUp += (s, e) => { if (sel) Unfocus(); else FocusOn(a2); };
                anomList.Children.Add(item);
            }
        }

        static bool SameAnomaly(Anomaly a, Anomaly b)
        {
            return a != null && b != null && a.T == b.T && a.Message == b.Message;
        }

        // ---------------------------------------------------------------- zoom to anomaly
        void FocusOn(Anomaly a)
        {
            focus = a; focusProcs = null; focusProcsTime = DateTime.MinValue;
            var t = a.T;
            if (S.Logging)
                Task.Factory.StartNew(() =>
                {
                    DateTime when;
                    List<ProcInfo> p = null;
                    try { p = LogStore.LoadProcsAt(t, out when); } catch { when = DateTime.MinValue; }
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (!SameAnomaly(focus, a)) return;
                        focusProcs = p ?? new List<ProcInfo>(); focusProcsTime = when;
                        ShowProcs();
                    }));
                });
            Redraw();
        }

        void Unfocus()
        {
            focus = null; focusProcs = null;
            Redraw();
            ShowProcs();
        }

        void UpdateFocusBar(DateTime from, DateTime to)
        {
            if (focusBar == null) return;
            focusBar.Visibility = focus == null ? Visibility.Collapsed : Visibility.Visible;
            if (focus == null) return;
            var br = focus.Severity == "critical" ? T.Bad : focus.Severity == "warning" ? T.Warn : T.Cpu;
            focusBar.Background = Theme.Fill(br, 0x22); focusBar.BorderBrush = br;
            focusText.Text = "Zoomed to " + focus.T.ToString("ddd dd.MM HH:mm:ss") + Fmt.Dot + focus.Message +
                             Fmt.Dot + "showing " + from.ToString("HH:mm") + " - " + to.ToString("HH:mm");
            focusText.ToolTip = focus.Message;
        }

        List<ProcInfo> lastProcs;

        void ShowProcs()
        {
            if (procCells == null) return;
            List<ProcInfo> list;
            if (focus != null)
            {
                list = focusProcs ?? new List<ProcInfo>();
                procTitle.Text = focusProcs == null ? (S.Logging ? "PROCESSES  (loading...)" : "PROCESSES  (logging is off)")
                               : focusProcs.Count == 0 ? "PROCESSES  (none logged near this time)"
                               : "PROCESSES AT " + focusProcsTime.ToString("HH:mm");
            }
            else
            {
                list = lastProcs ?? new List<ProcInfo>();
                procTitle.Text = "PROCESSES";
            }
            list = (procSort == "mem" ? list.OrderByDescending(x => x.Mem) : list.OrderByDescending(x => x.Cpu).ThenByDescending(x => x.Mem)).ToList();
            for (int r = 0; r < procCells.GetLength(0); r++)
            {
                bool has = r < list.Count;
                procCells[r, 0].Text = has ? list[r].Name : "";
                procCells[r, 0].ToolTip = has ? list[r].Name : null;
                bool sel = has && string.Equals(list[r].Name, procView, StringComparison.OrdinalIgnoreCase);
                procCells[r, 0].Foreground = sel ? T.Cpu : T.Fg;
                procCells[r, 0].FontWeight = sel ? FontWeights.SemiBold : FontWeights.Normal;
                procCells[r, 1].Text = has ? list[r].Cpu.ToString("0.0") + "%" : "";
                procCells[r, 2].Text = has ? Fmt.Size(list[r].Mem) : "";
            }
        }

        // ---------------------------------------------------------------- actions
        void ToggleFullscreen()
        {
            fullscreen = !fullscreen;
            if (fullscreen)
            {
                prevState = WindowState;
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                WindowState = WindowState.Normal;   // re-maximize so it covers the taskbar
                WindowState = WindowState.Maximized;
            }
            else
            {
                WindowStyle = WindowStyle.SingleBorderWindow;
                ResizeMode = ResizeMode.CanResize;
                WindowState = prevState;
            }
            fsLabel.Text = fullscreen ? "Exit fullscreen (Esc)" : "Fullscreen (F11)";
        }

        void Export()
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = "sysmon-" + S.DashRange.Replace(" ", "") + "-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".csv",
                Filter = "CSV file (*.csv)|*.csv", DefaultExt = ".csv"
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                var lines = new List<string> { Rec.Header };
                lines.AddRange(recs.Select(r => r.ToCsv()));
                File.WriteAllLines(dlg.FileName, lines);
                status.Text = "Exported " + recs.Count.ToString("N0") + " rows to " + dlg.FileName;
            }
            catch (Exception ex) { MessageBox.Show(this, "Export failed: " + ex.Message, "SysMon"); }
        }
    }
}
