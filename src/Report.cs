using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SysMonApp
{
    // Summary of one day, built from the logs (summary file when available, raw log otherwise)
    class DayReport
    {
        public DateTime Day;
        public bool HasData;
        public double HoursLogged;
        public double CpuAvg, CpuPeak, RamAvg, RamPeak, GpuAvg, GpuPeak, DiskRead, DiskWrite, Rx, Tx;
        public double PingAvg = double.NaN, PingLoss = double.NaN, BatMin = double.NaN;
        public readonly double[] HourCpu = Enumerable.Repeat(double.NaN, 24).ToArray();
        public int BusiestHour = -1;
        public List<Anomaly> Anomalies = new List<Anomaly>();
        public List<ProcInfo> TopCpu = new List<ProcInfo>(), TopMem = new List<ProcInfo>();

        static double Avg(IEnumerable<double> v) { var l = v.Where(x => !double.IsNaN(x)).ToList(); return l.Count > 0 ? l.Average() : double.NaN; }
        static double Max(IEnumerable<double> v) { var l = v.Where(x => !double.IsNaN(x)).ToList(); return l.Count > 0 ? l.Max() : double.NaN; }

        public static DayReport Build(DateTime day)
        {
            day = day.Date;
            var rep = new DayReport { Day = day };
            DateTime from = day, to = day.AddDays(1).AddTicks(-1);
            bool summary = day < DateTime.Today && LogStore.HasSummary(day);
            var recs = LogStore.Load(from, to, summary);
            if (recs.Count == 0) return rep;
            rep.HasData = true;

            double step = summary ? LogStore.SummaryMinutes * 60 : Math.Max(1, Program.S.LogIntervalSec);
            rep.HoursLogged = recs.Count * step / 3600.0;
            rep.CpuAvg = Avg(recs.Select(r => r.Cpu)); rep.CpuPeak = Max(recs.Select(r => r.CpuMax));
            rep.RamAvg = Avg(recs.Select(r => r.Ram)); rep.RamPeak = Max(recs.Select(r => r.Ram));
            rep.GpuAvg = Avg(recs.Select(r => r.Gpu)); rep.GpuPeak = Max(recs.Select(r => r.GpuMax));
            rep.DiskRead = recs.Sum(r => double.IsNaN(r.DiskR) ? 0 : r.DiskR * step);
            rep.DiskWrite = recs.Sum(r => double.IsNaN(r.DiskW) ? 0 : r.DiskW * step);
            rep.Rx = recs.Sum(r => r.RxBytes); rep.Tx = recs.Sum(r => r.TxBytes);
            rep.PingAvg = Avg(recs.Select(r => r.Ping)); rep.PingLoss = Avg(recs.Select(r => r.PingLoss));
            rep.BatMin = recs.Any(r => r.OnAc == 0) ? Max(recs.Where(r => r.OnAc == 0).Select(r => -r.Bat)) * -1 : double.NaN;
            foreach (var g in recs.GroupBy(r => r.T.Hour)) rep.HourCpu[g.Key] = Avg(g.Select(r => r.Cpu));
            double best = -1;
            for (int h = 0; h < 24; h++) if (!double.IsNaN(rep.HourCpu[h]) && rep.HourCpu[h] > best) { best = rep.HourCpu[h]; rep.BusiestHour = h; }

            rep.Anomalies = LogStore.LoadAnomalies(from, to).OrderBy(a => a.T).ToList();

            // processes: average CPU over the logged minutes (absent = 0), peak memory
            var minutes = new HashSet<string>();
            var cpu = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var mem = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var path = System.IO.Path.Combine(LogStore.Dir, "processes-" + day.ToString("yyyy-MM-dd") + ".csv");
            if (File.Exists(path))
            {
                try
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var rd = new StreamReader(fs))
                    {
                        string l;
                        while ((l = rd.ReadLine()) != null)
                        {
                            var p = l.Split(',');
                            if (p.Length < 4 || p[0] == "time") continue;
                            double c, m;
                            if (!double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out c)) continue;
                            double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out m);
                            minutes.Add(p[0]);
                            double v; cpu.TryGetValue(p[1], out v); cpu[p[1]] = v + c;
                            mem.TryGetValue(p[1], out v); mem[p[1]] = Math.Max(v, m);
                        }
                    }
                }
                catch { }
            }
            int n = Math.Max(1, minutes.Count);
            rep.TopCpu = cpu.OrderByDescending(kv => kv.Value).Take(5).Select(kv => new ProcInfo { Name = kv.Key, Cpu = kv.Value / n }).ToList();
            rep.TopMem = mem.OrderByDescending(kv => kv.Value).Take(5).Select(kv => new ProcInfo { Name = kv.Key, Mem = kv.Value }).ToList();
            return rep;
        }

        public string OneLine()
        {
            if (!HasData) return "No data was logged on " + Day.ToString("d") + ".";
            var parts = new List<string>
            {
                "CPU avg " + Fmt.Pct(CpuAvg) + ", peak " + Fmt.Pct(CpuPeak),
                "memory peak " + Fmt.Pct(RamPeak),
                Fmt.Dn + " " + Fmt.Size(Rx) + "  " + Fmt.UpA + " " + Fmt.Size(Tx),
                Anomalies.Count == 0 ? "no anomalies" : Anomalies.Count + " anomal" + (Anomalies.Count == 1 ? "y" : "ies")
            };
            if (TopCpu.Count > 0) parts.Add("busiest app " + TopCpu[0].Name);
            return string.Join(" · ", parts) + ". Click for the full report.";
        }
    }

    // 24 bars: average CPU per hour
    class HourBars : FrameworkElement
    {
        public double[] Values = new double[24];
        public Theme T;
        int hover = -1;

        public HourBars()
        {
            MouseMove += (s, e) => { hover = Math.Max(0, Math.Min(23, (int)(e.GetPosition(this).X / (ActualWidth / 24)))); InvalidateVisual(); };
            MouseLeave += (s, e) => { hover = -1; InvalidateVisual(); };
        }

        FormattedText Ft(string s, double size, Brush b)
        {
            return new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, b, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight - 16, bw = w / 24;
            if (w <= 0 || h <= 0 || T == null) return;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, ActualHeight));
            double max = Math.Max(10, Values.Where(v => !double.IsNaN(v)).DefaultIfEmpty(0).Max() * 1.15);
            for (int i = 0; i < 24; i++)
            {
                double x = i * bw + 2, v = Values[i];
                dc.DrawRoundedRectangle(T.Track, null, new Rect(x, 0, bw - 4, h), 2, 2);
                if (!double.IsNaN(v))
                {
                    double bh = Math.Max(2, h * v / max);
                    dc.DrawRoundedRectangle(i == hover ? T.Cpu : Theme.Fill(T.Cpu, 0xB0), null, new Rect(x, h - bh, bw - 4, bh), 2, 2);
                }
                if (i % 3 == 0) dc.DrawText(Ft(i.ToString("00"), 10, T.Faint), new Point(x, h + 2));
            }
            if (hover >= 0)
            {
                var ft = Ft(hover.ToString("00") + ":00 - " + (hover + 1).ToString("00") + ":00   " + (double.IsNaN(Values[hover]) ? "no data" : "CPU avg " + Fmt.Pct(Values[hover])), 11, T.Fg);
                double tx = Math.Max(0, Math.Min(w - ft.Width - 12, hover * bw - ft.Width / 2));
                dc.DrawRoundedRectangle(T.HoverBg, new Pen(T.Border, 1), new Rect(tx, 2, ft.Width + 12, ft.Height + 6), 4, 4);
                dc.DrawText(ft, new Point(tx + 6, 5));
            }
        }
    }

    class ReportWindow : Window
    {
        static ReportWindow open;
        Theme T;
        DateTime day;
        StackPanel body;
        TextBlock title;

        public static void ShowDay(DateTime day)
        {
            if (open == null) { open = new ReportWindow(); open.Closed += (s, e) => open = null; open.Show(); }
            open.Load(day);
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Activate();
        }

        ReportWindow()
        {
            T = Theme.Get(Program.S.Theme);
            Title = "SysMon - Daily report";
            Width = 860; Height = 760; MinWidth = 700; WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = T.Bg; Foreground = T.Fg; FontFamily = new FontFamily("Segoe UI"); FontSize = 12.5;
            try { Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(Program.Tray.Icon.Handle, Int32Rect.Empty, null); } catch { }
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Left) Load(day.AddDays(-1));
                else if (e.Key == Key.Right && day < DateTime.Today) Load(day.AddDays(1));
                else if (e.Key == Key.Escape) Close();
            };

            var root = new DockPanel { Margin = new Thickness(20, 16, 20, 16) };
            var hdr = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            var nav = new StackPanel { Orientation = Orientation.Horizontal };
            nav.Children.Add(NavButton("◀", () => Load(day.AddDays(-1))));
            nav.Children.Add(NavButton("Yesterday", () => Load(DateTime.Today.AddDays(-1))));
            nav.Children.Add(NavButton("Today so far", () => Load(DateTime.Today)));
            nav.Children.Add(NavButton("▶", () => { if (day < DateTime.Today) Load(day.AddDays(1)); }));
            DockPanel.SetDock(nav, Dock.Right);
            hdr.Children.Add(nav);
            title = new TextBlock { FontSize = 22, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            hdr.Children.Add(title);
            DockPanel.SetDock(hdr, Dock.Top);
            root.Children.Add(hdr);
            body = new StackPanel();
            root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            Content = root;
        }

        Border NavButton(string text, Action a)
        {
            var b = new Border { Child = new TextBlock { Text = text, Foreground = T.Fg }, Padding = new Thickness(12, 4, 12, 5), Margin = new Thickness(4, 0, 0, 0),
                                 CornerRadius = new CornerRadius(14), Background = T.Track, Cursor = Cursors.Hand };
            b.MouseEnter += (s, e) => b.Background = Theme.Fill(T.Fg, 0x20);
            b.MouseLeave += (s, e) => b.Background = T.Track;
            b.MouseLeftButtonUp += (s, e) => a();
            return b;
        }

        void Load(DateTime d)
        {
            day = d.Date;
            // the long date already contains the weekday in most cultures, so only prefix Today / Yesterday
            title.Text = (day == DateTime.Today ? "Today so far  ·  " : day == DateTime.Today.AddDays(-1) ? "Yesterday  ·  " : "") + day.ToString("D");
            body.Children.Clear();
            body.Children.Add(new TextBlock { Text = "Loading...", Foreground = T.Faint });
            var want = day;
            Task.Factory.StartNew(() => { DayReport r; try { r = DayReport.Build(want); } catch (Exception ex) { Store.Log("report: " + ex); r = new DayReport { Day = want }; } return r; })
                .ContinueWith(t => Dispatcher.BeginInvoke(new Action(() => { if (want == day) Render(t.Result); })));
        }

        TextBlock Label(string s) { return new TextBlock { Text = s, Foreground = T.Muted, FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 6) }; }

        Border Card(string name, string value, string sub, Brush accent)
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = name, Foreground = T.Muted, FontSize = 11, FontWeight = FontWeights.SemiBold });
            sp.Children.Add(new TextBlock { Text = value, Foreground = accent, FontSize = 20, FontWeight = FontWeights.SemiBold });
            sp.Children.Add(new TextBlock { Text = sub, Foreground = T.Faint, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
            return new Border { Child = sp, Background = T.Track, CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 8, 8) };
        }

        void Render(DayReport r)
        {
            body.Children.Clear();
            if (!r.HasData)
            {
                body.Children.Add(new TextBlock { Text = "No statistics were logged on this day (SysMon wasn't running, or logging was off).", Foreground = T.Faint, TextWrapping = TextWrapping.Wrap });
                return;
            }
            body.Children.Add(new TextBlock { Text = "Monitored for " + r.HoursLogged.ToString("0.0") + " h" + (r.BusiestHour >= 0 ? "  ·  busiest hour " + r.BusiestHour.ToString("00") + ":00" : ""), Foreground = T.Faint });

            var cards = new UniformGrid { Columns = 4, Margin = new Thickness(0, 12, -8, 0) };
            cards.Children.Add(Card("CPU", Fmt.Pct(r.CpuAvg), "average  ·  peak " + Fmt.Pct(r.CpuPeak), T.Cpu));
            cards.Children.Add(Card("MEMORY", Fmt.Pct(r.RamAvg), "average  ·  peak " + Fmt.Pct(r.RamPeak), T.Ram));
            cards.Children.Add(Card("GPU", Fmt.Pct(r.GpuAvg), "average  ·  peak " + Fmt.Pct(r.GpuPeak), T.Gpu));
            cards.Children.Add(Card("ANOMALIES", r.Anomalies.Count.ToString(),
                r.Anomalies.Count(a => a.Severity == "critical") + " critical  ·  " + r.Anomalies.Count(a => a.Severity == "warning") + " warnings", r.Anomalies.Count > 0 ? T.Warn : T.Good));
            cards.Children.Add(Card("DOWNLOADED", Fmt.Size(r.Rx), "network", T.Down));
            cards.Children.Add(Card("UPLOADED", Fmt.Size(r.Tx), "network", T.Up));
            cards.Children.Add(Card("DISK", Fmt.Size(r.DiskRead + r.DiskWrite), "read " + Fmt.Size(r.DiskRead) + "  ·  written " + Fmt.Size(r.DiskWrite), T.Read));
            cards.Children.Add(Card("PING", double.IsNaN(r.PingAvg) ? "-" : r.PingAvg.ToString("0") + " ms",
                double.IsNaN(r.PingLoss) ? "" : Fmt.Pct(r.PingLoss) + " packet loss" + (double.IsNaN(r.BatMin) ? "" : "  ·  battery low " + Fmt.Pct(r.BatMin)), T.Ping));
            body.Children.Add(cards);

            body.Children.Add(Label("CPU BY HOUR"));
            body.Children.Add(new HourBars { Values = r.HourCpu, T = T, Height = 110 });

            body.Children.Add(Label("TOP PROCESSES"));
            var pg = new Grid();
            pg.ColumnDefinitions.Add(new ColumnDefinition()); pg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) }); pg.ColumnDefinitions.Add(new ColumnDefinition());
            pg.Children.Add(ProcList("Average CPU", r.TopCpu.Select(p => Tuple.Create(p.Name, p.Cpu.ToString("0.0") + "%")), T.Cpu, 0));
            pg.Children.Add(ProcList("Peak memory", r.TopMem.Select(p => Tuple.Create(p.Name, Fmt.Size(p.Mem))), T.Ram, 2));
            body.Children.Add(pg);

            body.Children.Add(Label("ANOMALIES"));
            if (r.Anomalies.Count == 0) body.Children.Add(new TextBlock { Text = "Nothing unusual was detected.", Foreground = T.Faint });
            foreach (var a in r.Anomalies.Take(25))
            {
                var g = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                var br = a.Severity == "critical" ? T.Bad : a.Severity == "warning" ? T.Warn : T.Cpu;
                g.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = br, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 0, 0) });
                var tm = new TextBlock { Text = a.T.ToString("HH:mm"), Foreground = T.Faint }; Grid.SetColumn(tm, 1); g.Children.Add(tm);
                var msg = new TextBlock { Text = a.Message, Foreground = T.Fg, TextWrapping = TextWrapping.Wrap }; Grid.SetColumn(msg, 2); g.Children.Add(msg);
                body.Children.Add(g);
            }
            if (r.Anomalies.Count > 25) body.Children.Add(new TextBlock { Text = "... and " + (r.Anomalies.Count - 25) + " more (see the dashboard or anomalies.csv)", Foreground = T.Faint });
        }

        UIElement ProcList(string title, IEnumerable<Tuple<string, string>> rows, Brush accent, int col)
        {
            var sp = new StackPanel();
            Grid.SetColumn(sp, col);
            sp.Children.Add(new TextBlock { Text = title, Foreground = T.Faint, Margin = new Thickness(0, 0, 0, 4) });
            var list = rows.ToList();
            if (list.Count == 0) sp.Children.Add(new TextBlock { Text = "no process log for this day", Foreground = T.Faint });
            foreach (var r in list)
            {
                var g = new Grid { Margin = new Thickness(0, 1, 0, 1) };
                g.ColumnDefinitions.Add(new ColumnDefinition()); g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.Children.Add(new TextBlock { Text = r.Item1, Foreground = T.Fg, TextTrimming = TextTrimming.CharacterEllipsis });
                var v = new TextBlock { Text = r.Item2, Foreground = accent, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 0, 0, 0) };
                Grid.SetColumn(v, 1); g.Children.Add(v);
                sp.Children.Add(g);
            }
            return sp;
        }
    }
}
