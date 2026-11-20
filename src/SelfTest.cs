using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SysMonApp
{
    class TestResult
    {
        public string Name, Expect, Got = "";
        public bool Pass;
        public double DelaySec = double.NaN;
        public Anomaly Hit;
    }

    /*
      Replays synthetic data through a fresh Detector (never the live one, never the log files):
      3 hours of normal activity to learn a baseline, then one scenario. A scenario passes when the
      expected anomaly is raised; the "normal" scenario passes only when nothing is raised.
    */
    static class SelfTest
    {
        const double MB = 1 << 20, GB = 1L << 30;
        delegate void Tweak(Detector d, DateTime t, double min, Rec r, List<ProcInfo> procs);

        class Scenario
        {
            public string Name, Expect; public double Minutes; public Tweak Apply;
            public Scenario(string n, string e, double m, Tweak a) { Name = n; Expect = e; Minutes = m; Apply = a; }
        }

        static Rec Normal(DateTime t, Random rnd)
        {
            Func<double, double, double> j = (c, w) => c + (rnd.NextDouble() * 2 - 1) * w;
            double cpu = j(15, 5);
            return new Rec
            {
                T = t, Cpu = cpu, CpuMax = cpu + 10, Ram = j(55, 2), RamGB = 8.5, Gpu = j(5, 2), GpuMax = j(10, 3), GpuMem = 1,
                DiskR = j(2 * MB, MB), DiskW = j(1 * MB, 0.5 * MB), DiskBusy = j(5, 3), Rx = j(50 * 1024, 30 * 1024), Tx = j(20 * 1024, 10 * 1024),
                RxBytes = 500000, TxBytes = 200000, Ping = j(10, 2), PingMax = j(14, 3), PingLoss = 0, Bat = 80, OnAc = 1,
                TopCpu = "chrome", TopCpuPct = 4, TopMem = "chrome", TopMemBytes = 2.5 * GB
            };
        }

        static List<ProcInfo> NormalProcs(Random rnd)
        {
            var baseProcs = new[] { Tuple.Create("chrome", 4.0, 2.5 * GB), Tuple.Create("Code", 2.0, 900 * MB), Tuple.Create("explorer", 0.5, 150 * MB),
                                    Tuple.Create("svchost", 1.0, 300 * MB), Tuple.Create("Teams", 1.5, 600 * MB), Tuple.Create("dwm", 1.0, 90 * MB) };
            return baseProcs.Select(p => new ProcInfo { Name = p.Item1, Cpu = p.Item2 * (0.5 + rnd.NextDouble()), Mem = p.Item3 * (0.98 + rnd.NextDouble() * 0.04) }).ToList();
        }

        static List<Scenario> Scenarios()
        {
            return new List<Scenario>
            {
                new Scenario("Normal activity (no false alarms)", null, 30, (d, t, m, r, p) => { }),
                new Scenario("CPU spike to 75%", "CPU usage unusually high", 4, (d, t, m, r, p) => { r.Cpu = 75; r.CpuMax = 82; }),
                new Scenario("CPU overload at 97%", "CPU overloaded", 4, (d, t, m, r, p) => { r.Cpu = 97; r.CpuMax = 100; }),
                new Scenario("Memory fills up to 96%", "Memory nearly full", 3, (d, t, m, r, p) => { r.Ram = 96; r.RamGB = 15; }),
                new Scenario("GPU jumps to 80%", "GPU usage unusually high", 4, (d, t, m, r, p) => { r.Gpu = 80; r.GpuMax = 90; }),
                new Scenario("GPU at full load for 7 min", "GPU at full load", 7, (d, t, m, r, p) => { r.Gpu = 97; r.GpuMax = 99; }),
                new Scenario("Disk burst 150 MB/s", "Disk activity unusually high", 4, (d, t, m, r, p) => { r.DiskR = 150 * MB; }),
                new Scenario("Download burst 30 MB/s", "Download traffic unusually high", 4, (d, t, m, r, p) => { r.Rx = 30 * MB; }),
                new Scenario("Upload burst 5 MB/s", "Upload traffic unusually high", 4, (d, t, m, r, p) => { r.Tx = 5 * MB; }),
                new Scenario("Ping rises to 250 ms", "Ping latency unusually high", 4, (d, t, m, r, p) => { r.Ping = 250; r.PingMax = 300; }),
                new Scenario("40% packet loss", "Unstable connection", 4, (d, t, m, r, p) => { r.PingLoss = 40; }),
                new Scenario("Connection drops", "Connection lost", 3, (d, t, m, r, p) => { r.PingLoss = 100; r.Ping = double.NaN; r.PingMax = double.NaN; }),
                new Scenario("Process leaks 15 MB/min", "Possible memory leak: leaky", 50,
                             (d, t, m, r, p) => p.Add(new ProcInfo { Name = "leaky", Cpu = 1, Mem = 400 * MB + 15 * MB * m })),
                new Scenario("New process uses 60% CPU", "New process miner", 4,
                             (d, t, m, r, p) => { p.Add(new ProcInfo { Name = "miner", Cpu = 60, Mem = 50 * MB }); r.Cpu = 70; }),
                new Scenario("C: loses 1 GB per minute", "Free space on C:", 12, (d, t, m, r, p) =>
                {
                    if (t.Second == 0) d.CheckDrives(t, new List<KeyValuePair<string, double>> { new KeyValuePair<string, double>(@"C:\", 100 * GB - GB * Math.Floor(m)) });
                }),
                new Scenario("Battery drains 60%/hour", "Battery draining fast", 20, (d, t, m, r, p) => { r.OnAc = 0; r.Bat = 80 - m; }),
            };
        }

        public static List<TestResult> Run()
        {
            int iv = Math.Max(1, Program.S.LogIntervalSec);
            var results = new List<TestResult>();
            foreach (var sc in Scenarios())
            {
                var d = new Detector(true);
                var rnd = new Random(1234);
                var t = new DateTime(2026, 1, 5, 8, 0, 0);
                for (int i = 0; i < 3 * 3600 / iv; i++)
                {
                    d.OnBucket(Normal(t, rnd), NormalProcs(rnd), null);
                    if (t.Second == 0) d.CheckDrives(t, new List<KeyValuePair<string, double>> { new KeyValuePair<string, double>(@"C:\", 100 * GB) });
                    t = t.AddSeconds(iv);
                }
                int baselineAlarms = d.Recent.Count;
                var start = t;
                int steps = (int)Math.Ceiling(sc.Minutes * 60 / iv);
                for (int k = 0; k < steps; k++)
                {
                    var r = Normal(t, rnd); var p = NormalProcs(rnd);
                    sc.Apply(d, t, (t - start).TotalMinutes, r, p);
                    d.OnBucket(r, p, null);
                    t = t.AddSeconds(iv);
                }

                var res = new TestResult { Name = sc.Name, Expect = sc.Expect ?? "nothing" };
                if (sc.Expect == null)
                {
                    res.Pass = d.Recent.Count == 0;
                    res.Got = res.Pass ? "No anomalies in 3.5 h of normal data" : d.Recent.Count + " false alarm(s), e.g. " + d.Recent[0].Message;
                }
                else
                {
                    var hit = d.Recent.FirstOrDefault(a => a.T >= start && a.Message.IndexOf(sc.Expect, StringComparison.OrdinalIgnoreCase) >= 0);
                    res.Pass = hit != null && baselineAlarms == 0;
                    if (hit != null) { res.Hit = hit; res.Got = hit.Message; res.DelaySec = (hit.T - start).TotalSeconds + iv; }
                    else
                    {
                        var other = d.Recent.Where(a => a.T >= start).Select(a => a.Message).ToList();
                        res.Got = other.Count > 0 ? "Different result: " + string.Join(" | ", other) : "Not detected";
                    }
                    if (baselineAlarms > 0) res.Got += "  (plus " + baselineAlarms + " false alarm(s) during the baseline)";
                }
                results.Add(res);
            }
            return results;
        }

        public static string Report(List<TestResult> rs)
        {
            var sb = new StringBuilder();
            sb.AppendLine("SysMon anomaly self-test  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            sb.AppendLine(rs.Count(r => r.Pass) + "/" + rs.Count + " passed   sensitivity " + Program.S.AnomalySensitivity + ", interval " + Program.S.LogIntervalSec + " s");
            sb.AppendLine();
            foreach (var r in rs)
                sb.AppendLine((r.Pass ? "PASS  " : "FAIL  ") + r.Name.PadRight(36) + (double.IsNaN(r.DelaySec) ? "" : ("after " + r.DelaySec.ToString("0") + " s").PadRight(14)) + r.Got);
            return sb.ToString();
        }
    }

    class SelfTestWindow : Window
    {
        public SelfTestWindow(List<TestResult> rs)
        {
            var T = Theme.Get(Program.S.Theme);
            Title = "SysMon - Anomaly self-test";
            Width = 760; Height = 640; WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = T.Bg; Foreground = T.Fg; FontFamily = new FontFamily("Segoe UI"); FontSize = 12.5;
            try { Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(Program.Tray.Icon.Handle, Int32Rect.Empty, null); } catch { }

            var root = new DockPanel { Margin = new Thickness(16) };
            var head = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            int pass = rs.Count(r => r.Pass);
            head.Children.Add(new TextBlock { Text = pass + " of " + rs.Count + " scenarios passed", FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = pass == rs.Count ? T.Good : T.Warn });
            head.Children.Add(new TextBlock
            {
                Text = "Each scenario replays 3 hours of normal synthetic activity so the detector can learn a baseline, then injects one problem. " +
                       "Uses your current settings (" + Program.S.AnomalySensitivity.ToLower() + " sensitivity, " + Program.S.LogIntervalSec + " s interval). Nothing is written to your logs.",
                Foreground = T.Faint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0)
            });
            DockPanel.SetDock(head, Dock.Top);
            root.Children.Add(head);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            Func<string, Action, Button> btn = (text, a) =>
            {
                var b = new Button { Content = text, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(6, 0, 0, 0) };
                b.Click += (s, e) => a();
                buttons.Children.Add(b);
                return b;
            };
            btn("Show results in dashboard", () => Program.InjectTestAnomalies(rs.Where(r => r.Hit != null).Select(r => r.Hit).ToList()));
            btn("Copy report", () => { try { Clipboard.SetText(SelfTest.Report(rs)); } catch { } });
            btn("Close", Close);
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);

            var list = new StackPanel();
            foreach (var r in rs)
            {
                var g = new Grid { Margin = new Thickness(0, 0, 0, 8) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = r.Pass ? T.Good : T.Bad, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 0, 0) });
                var sp = new StackPanel();
                Grid.SetColumn(sp, 1);
                sp.Children.Add(new TextBlock { Text = r.Name, FontWeight = FontWeights.SemiBold, Foreground = T.Fg });
                sp.Children.Add(new TextBlock { Text = (r.Pass ? "" : "Expected: " + r.Expect + ".  ") + r.Got, Foreground = r.Pass ? T.Muted : T.Bad, TextWrapping = TextWrapping.Wrap });
                g.Children.Add(sp);
                var delay = new TextBlock { Text = double.IsNaN(r.DelaySec) ? "" : "detected after " + r.DelaySec.ToString("0") + " s", Foreground = T.Faint, Margin = new Thickness(12, 0, 0, 0) };
                Grid.SetColumn(delay, 2);
                g.Children.Add(delay);
                list.Children.Add(g);
            }
            root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            Content = root;
        }
    }
}
