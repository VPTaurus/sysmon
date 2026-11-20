using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using WF = System.Windows.Forms;

[assembly: AssemblyTitle("SysMon Widget")]
[assembly: AssemblyProduct("SysMon Widget")]
[assembly: AssemblyDescription("Always-on-top system resource and network monitor")]
[assembly: AssemblyVersion("2.0.0.0")]
[assembly: AssemblyFileVersion("2.0.0.0")]

namespace SysMonApp
{
    static class Program
    {
        public static Settings S;
        public static Usage Usage;
        public static Widget W;
        public static WF.NotifyIcon Tray;
        public static Alerts Alerts = new Alerts();
        public static readonly Logger Logger = new Logger();
        public static readonly Detector Detector = new Detector();
        public static readonly List<Rec> Live = new List<Rec>(), SessionRecs = new List<Rec>();
        public static Snapshot LastSnap;
        public static double LastPing = double.NaN;
        public static string CpuName = "CPU";
        public static Dashboard Dash;
        public static TaskbarReadout Readout;
        static WF.ToolStripMenuItem trayClick, trayTaskbar;
        static string lastTheme;
        static DateTime lastDriveCheck = DateTime.MinValue, lastCleanupDay = DateTime.Today;
        static SettingsWindow settingsWin;
        static volatile bool running = true;
        static readonly AutoResetEvent wakeSampler = new AutoResetEvent(false), wakePing = new AutoResetEvent(false), wakePublic = new AutoResetEvent(false);
        static Mutex mutex;
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunName = "SysMonWidget";

        [STAThread]
        static void Main(string[] args)
        {
            int st = Array.FindIndex(args, a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase));
            if (st >= 0)
            {
                // headless detector test: SysMon.exe --selftest [report.txt]; exit code = failed scenarios
                S = Store.Load<Settings>("config.json");
                var rs = SelfTest.Run();
                string outp = st + 1 < args.Length ? args[st + 1] : System.IO.Path.Combine(Store.Dir, "selftest.txt");
                System.IO.File.WriteAllText(outp, SelfTest.Report(rs));
                Environment.Exit(rs.Count(r => !r.Pass));
            }

            bool created;
            mutex = new Mutex(true, @"Local\SysMonWidget_v2", out created);
            if (!created) return;

            S = Store.Load<Settings>("config.json");
            Usage = Store.Load<Usage>("usage.json");
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += (s, e) => { Store.Log("UI: " + e.Exception); e.Handled = true; };
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Store.Log("fatal: " + e.ExceptionObject);

            // keep the startup entry pointing at this exe if it was moved
            if (IsStartup()) SetStartup(true);

            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                    if (k != null) CpuName = System.Text.RegularExpressions.Regex.Replace(((string)k.GetValue("ProcessorNameString") ?? "CPU").Trim(), @"\s+", " ");
            }
            catch { }
            lastTheme = S.Theme;
            Logger.Bucket += OnBucket;
            Detector.Detected += OnAnomaly;

            W = new Widget();
            CreateTray();
            try { Hotkeys.Apply(S); } catch (Exception ex) { Store.Log("hotkeys: " + ex.Message); }
            if (S.WidgetVisible) W.Show();
            if (S.TaskbarReadout) { Readout = new TaskbarReadout(); Readout.Show(); }
            if (args.Any(a => a.Equals("--dashboard", StringComparison.OrdinalIgnoreCase))) ShowDashboard();
            if (args.Any(a => a.Equals("--show-selftest", StringComparison.OrdinalIgnoreCase)))
            {
                // run the self-test at startup and put its detections on the live dashboard (demo / testing)
                var rs = SelfTest.Run();
                InjectTestAnomalies(rs.Where(r => r.Hit != null).Select(r => r.Hit).ToList());
            }

            // clean old logs and warm up the anomaly baselines from the last 24 h
            System.Threading.Tasks.Task.Factory.StartNew(() =>
            {
                LogStore.Cleanup(S.LogKeepDays);
                var hist = LogStore.Load(DateTime.Now.AddHours(-24), DateTime.Now);
                W.Dispatcher.BeginInvoke(new Action(() => Detector.Seed(hist)));
            });

            Start(SamplerLoop); Start(PingLoop); Start(PublicIpLoop);

            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            t.Tick += (s, e) =>
            {
                if (W.IsVisible) { W.Topmost = false; W.Topmost = true; }   // re-assert after fullscreen apps
                if (DateTime.Now.Second < 10) Store.Save("usage.json", Usage);
                if (DateTime.Today != lastCleanupDay)
                {
                    lastCleanupDay = DateTime.Today;
                    int keep = S.LogKeepDays;
                    System.Threading.Tasks.Task.Factory.StartNew(() => LogStore.Cleanup(keep));
                }
            };
            t.Start();

            app.Run();
        }

        static void Start(ThreadStart f) { new Thread(f) { IsBackground = true, Priority = ThreadPriority.BelowNormal }.Start(); }

        // ---------------------------------------------------------------- background loops
        static void SamplerLoop()
        {
            Sampler sampler;
            try { sampler = new Sampler(); }
            catch (Exception ex) { Store.Log("sampler init: " + ex); return; }
            var sw = new Stopwatch();
            string lastGw = null;
            while (running)
            {
                sw.Restart();
                try
                {
                    var snap = sampler.Sample();
                    if (lastGw != null && snap.Gateway != lastGw) wakePublic.Set();   // network changed
                    lastGw = snap.Gateway;
                    W.Dispatcher.BeginInvoke(new Action(() => OnSample(snap)));
                }
                catch (Exception ex) { Store.Log("sample: " + ex.Message); }
                wakeSampler.WaitOne(Math.Max(100, S.IntervalMs - (int)sw.ElapsedMilliseconds));
            }
        }

        static void PingLoop()
        {
            var sw = new Stopwatch();
            using (var p = new Ping())
                while (running)
                {
                    sw.Restart();
                    if (S.ShowPing || S.AlertsOn)
                    {
                        string target = (S.PingTarget ?? "").Trim();
                        if (target == "" ) target = "1.1.1.1";
                        if (target.Equals("gateway", StringComparison.OrdinalIgnoreCase)) target = Sampler.CurrentGateway;
                        double ms = double.NaN;
                        if (!string.IsNullOrEmpty(target))
                        {
                            try { var r = p.Send(target, 1000); if (r.Status == IPStatus.Success) ms = r.RoundtripTime; }
                            catch { }
                        }
                        string shown = target;
                        W.Dispatcher.BeginInvoke(new Action(() => OnPing(ms, shown)));
                    }
                    wakePing.WaitOne(Math.Max(100, 2000 - (int)sw.ElapsedMilliseconds));
                }
        }

        static void PublicIpLoop()
        {
            while (running)
            {
                if (S.PublicIp)
                {
                    string ip = "";
                    try
                    {
                        var req = (HttpWebRequest)WebRequest.Create("https://api.ipify.org");
                        req.Timeout = 6000; req.UserAgent = "SysMonWidget";
                        using (var resp = req.GetResponse())
                        using (var rd = new System.IO.StreamReader(resp.GetResponseStream()))
                            ip = rd.ReadToEnd().Trim();
                        if (ip.Length > 45) ip = "";
                    }
                    catch { ip = "unavailable"; }
                    W.Dispatcher.BeginInvoke(new Action(() => W.PublicIp = ip));
                }
                else W.Dispatcher.BeginInvoke(new Action(() => W.PublicIp = ""));
                wakePublic.WaitOne(TimeSpan.FromMinutes(10));
                Thread.Sleep(3000);   // let a new connection settle
            }
        }

        // ---------------------------------------------------------------- data routing (UI thread)
        static void OnSample(Snapshot s)
        {
            W.OnSample(s);          // also fills in the last known top-process lists
            LastSnap = s;

            var rec = Rec.FromSnapshot(s, LastPing);
            Live.Add(rec);
            if (Live.Count > 0 && (s.Time - Live[0].T).TotalMinutes > 15.5)
            {
                int cut = Live.FindIndex(r => (s.Time - r.T).TotalMinutes <= 15);
                if (cut > 0) Live.RemoveRange(0, cut);
            }
            Logger.Add(s);

            if ((s.Time - lastDriveCheck).TotalSeconds >= 60)
            {
                lastDriveCheck = s.Time;
                if (S.Anomalies)
                {
                    var free = new List<KeyValuePair<string, double>>();
                    try
                    {
                        foreach (var d in System.IO.DriveInfo.GetDrives())
                            if (d.DriveType == System.IO.DriveType.Fixed && d.IsReady) free.Add(new KeyValuePair<string, double>(d.Name, d.AvailableFreeSpace));
                    }
                    catch { }
                    Detector.CheckDrives(s.Time, free);
                }
            }
            if (Dash != null) Dash.OnSample(s, rec);
            if (Readout != null) Readout.Update(s);
        }

        static void OnPing(double ms, string target)
        {
            LastPing = ms;
            W.OnPing(ms, target);
            Logger.AddPing(ms);
            if (Readout != null) Readout.UpdatePing(ms);
        }

        static void OnBucket(Rec r, List<ProcInfo> procs, Snapshot snap)
        {
            SessionRecs.Add(r);
            if ((r.T - SessionRecs[0].T).TotalHours > 24.5)
            {
                int cut = SessionRecs.FindIndex(x => (r.T - x.T).TotalHours <= 24);
                if (cut > 0) SessionRecs.RemoveRange(0, cut);
            }
            try { Detector.OnBucket(r, procs, snap); }
            catch (Exception ex) { Store.Log("detector: " + ex); }
            if (Dash != null) Dash.OnBucket(r);
        }

        static void OnAnomaly(Anomaly a)
        {
            if (S.Logging) LogStore.AppendAnomaly(a);
            // rules that duplicate the simple alerts stay quiet while alerts are on
            if (S.AnomalyNotify && a.Severity != "info" && !(a.Quiet && S.AlertsOn))
                Tray.ShowBalloonTip(8000, a.Severity == "critical" ? "Critical anomaly" : "Anomaly detected", a.Message, WF.ToolTipIcon.Warning);
            if (Dash != null) Dash.OnAnomaly(a);
        }

        public static void ShowSelfTest()
        {
            List<TestResult> rs;
            System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            try { rs = SelfTest.Run(); }
            finally { System.Windows.Input.Mouse.OverrideCursor = null; }
            new SelfTestWindow(rs).Show();
        }

        // put self-test detections on the live dashboard (memory only, never logged)
        public static void InjectTestAnomalies(List<Anomaly> hits)
        {
            var now = DateTime.Now;
            int i = hits.Count;
            foreach (var h in hits)
                Detector.Recent.Add(new Anomaly
                {
                    T = now.AddSeconds(-25 * i--), Severity = h.Severity, Metric = h.Metric, Value = h.Value, Baseline = h.Baseline,
                    Message = "[Test] " + h.Message
                });
            if (Dash == null) { S.DashRange = "15 min"; ShowDashboard(); }
            else { Dash.SetRange("15 min"); ShowDashboard(); }
            var first = hits.FirstOrDefault(h => h.Severity != "info");
            if (first != null) Tray.ShowBalloonTip(6000, "Anomaly detected (test)", first.Message, WF.ToolTipIcon.Warning);
        }

        public static void ShowDashboard()
        {
            if (Dash == null)
            {
                Dash = new Dashboard();
                Dash.Closed += (s, e) => Dash = null;
                Dash.Show();
            }
            if (Dash.WindowState == WindowState.Minimized) Dash.WindowState = WindowState.Maximized;
            Dash.Activate();
        }

        // ---------------------------------------------------------------- settings
        public static void SaveSettings() { Store.Save("config.json", S); }

        public static void Apply()
        {
            SaveSettings();
            W.ApplySettings();
            if (S.WidgetVisible && !W.IsVisible) { W.Show(); W.Topmost = true; }
            else if (!S.WidgetVisible && W.IsVisible) W.Hide();
            if (S.TaskbarReadout && Readout == null) { Readout = new TaskbarReadout(); Readout.Show(); }
            else if (!S.TaskbarReadout && Readout != null) { Readout.Close(); Readout = null; }
            else if (Readout != null) Readout.Rebuild();
            if (Dash != null && lastTheme != S.Theme) Dash.ApplyTheme();
            lastTheme = S.Theme;
            if (trayClick != null) trayClick.Checked = S.ClickThrough;
            if (trayTaskbar != null) trayTaskbar.Checked = S.TaskbarReadout;
            try { Hotkeys.Apply(S); } catch (Exception ex) { Store.Log("hotkeys: " + ex.Message); }
            wakeSampler.Set(); wakePing.Set();
            if (settingsWin != null) settingsWin.Sync();
        }

        public static void RefreshPublicIp() { wakePublic.Set(); }

        public static void ShowSettings()
        {
            if (settingsWin == null)
            {
                settingsWin = new SettingsWindow();
                settingsWin.Closed += (s, e) => settingsWin = null;
                settingsWin.Show();
            }
            settingsWin.Activate();
        }

        public static bool IsStartup()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(RunName) != null; }
            catch { return false; }
        }

        public static void SetStartup(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue(RunName, "\"" + Assembly.GetEntryAssembly().Location + "\"");
                    else if (k.GetValue(RunName) != null) k.DeleteValue(RunName);
                }
            }
            catch (Exception ex) { Store.Log("startup: " + ex.Message); }
        }

        public static void Exit()
        {
            running = false;
            wakeSampler.Set(); wakePing.Set(); wakePublic.Set();
            SaveSettings();
            Store.Save("usage.json", Usage);
            try { Tray.Visible = false; Tray.Dispose(); } catch { }
            try { if (Dash != null) Dash.Close(); if (Readout != null) Readout.Close(); } catch { }
            Application.Current.Shutdown();
        }

        // ---------------------------------------------------------------- tray
        static Icon MakeIcon()
        {
            try
            {
                var ico = Icon.ExtractAssociatedIcon(Assembly.GetEntryAssembly().Location);
                if (ico != null) return new Icon(ico, WF.SystemInformation.SmallIconSize);
            }
            catch { }
            return SystemIcons.Application;
        }

        static void CreateTray()
        {
            Tray = new WF.NotifyIcon { Icon = MakeIcon(), Text = "SysMon Widget", Visible = true };
            var menu = new WF.ContextMenuStrip();
            menu.Items.Add("Open dashboard", null, (s, e) => ShowDashboard());
            menu.Items.Add("Show / hide widget", null, (s, e) => ToggleVisible());
            trayTaskbar = new WF.ToolStripMenuItem("Show on taskbar") { Checked = S.TaskbarReadout, CheckOnClick = true };
            trayTaskbar.Click += (s, e) => { S.TaskbarReadout = trayTaskbar.Checked; Apply(); };
            menu.Items.Add(trayTaskbar);
            menu.Items.Add("Settings...", null, (s, e) => ShowSettings());
            trayClick = new WF.ToolStripMenuItem("Click-through") { Checked = S.ClickThrough, CheckOnClick = true };
            trayClick.Click += (s, e) => { S.ClickThrough = trayClick.Checked; Apply(); };
            menu.Items.Add(trayClick);
            menu.Items.Add("Move to top-right corner", null, (s, e) => { W.Show(); W.MoveToCorner(); });
            menu.Items.Add("-");
            menu.Items.Add("Exit", null, (s, e) => Exit());
            Tray.ContextMenuStrip = menu;
            Tray.MouseClick += (s, e) => { if (e.Button == WF.MouseButtons.Left) ToggleVisible(); };
        }

        static void ToggleVisible()
        {
            S.WidgetVisible = !W.IsVisible;
            Apply();
        }

        public static void UpdateTray(Snapshot s, string ping)
        {
            string t = string.Format("CPU {0:0}%  RAM {1:0}%{2}\n{3} {4}  {5} {6}", s.Cpu, s.MemLoad,
                                     s.HasGpu ? string.Format("  GPU {0:0}%", s.Gpu) : "",
                                     Fmt.Dn, Fmt.Rate(s.Rx, S.Bits), Fmt.UpA, Fmt.Rate(s.Tx, S.Bits));
            if (!string.IsNullOrEmpty(ping) && t.Length + ping.Length + 3 <= 63) t += "  " + ping;
            if (t.Length > 63) t = t.Substring(0, 63);
            try { Tray.Text = t; } catch { }
        }
    }

    // ---------------------------------------------------------------- alerts
    class Alerts
    {
        readonly Dictionary<string, DateTime> fired = new Dictionary<string, DateTime>();
        DateTime? cpuSince;
        static Settings S { get { return Program.S; } }

        void Fire(string key, string title, string text)
        {
            if (!S.AlertsOn || Program.Tray == null) return;
            DateTime t;
            if (fired.TryGetValue(key, out t) && (DateTime.Now - t).TotalMinutes < Math.Max(1, S.AlertCooldownMin)) return;
            fired[key] = DateTime.Now;
            Program.Tray.ShowBalloonTip(6000, title, text, WF.ToolTipIcon.Warning);
        }

        public void Check(Snapshot s)
        {
            if (S.AlertCpu > 0 && s.Cpu >= S.AlertCpu)
            {
                if (cpuSince == null) cpuSince = DateTime.Now;
                if ((DateTime.Now - cpuSince.Value).TotalSeconds >= S.AlertCpuSecs)
                {
                    string top = s.TopCpu != null && s.TopCpu.Count > 0 ? " Top: " + s.TopCpu[0].Name + " (" + s.TopCpu[0].Value.ToString("0") + "%)" : "";
                    Fire("cpu", "High CPU usage", "CPU above " + S.AlertCpu + "% for " + S.AlertCpuSecs + " s." + top);
                }
            }
            else cpuSince = null;

            if (S.AlertRam > 0 && s.MemLoad >= S.AlertRam)
            {
                string top = s.TopMem != null && s.TopMem.Count > 0 ? " Top: " + s.TopMem[0].Name + " (" + Fmt.Size(s.TopMem[0].Value) + ")" : "";
                Fire("ram", "Memory almost full", "RAM at " + Fmt.Pct(s.MemLoad) + "." + top);
            }

            if (S.AlertBattery > 0 && s.HasBattery && !s.OnAc && s.BatPct <= S.AlertBattery)
                Fire("bat", "Battery low", s.BatPct + "% remaining" + (s.BatSecs > 0 ? " (" + Fmt.Duration(s.BatSecs) + ")" : "") + ".");
        }

        public void CheckDisk(string name, double freePct)
        {
            if (S.AlertDiskFree > 0 && freePct < S.AlertDiskFree)
                Fire("disk" + name, "Disk almost full", name.TrimEnd('\\') + " has only " + Fmt.Pct(freePct) + " free space left.");
        }

        public void CheckPing(Ring h, string target)
        {
            if (S.AlertPingLoss <= 0 || h.Count < 15) return;
            int n = Math.Min(30, h.Count), lost = 0;
            for (int i = h.Count - n; i < h.Count; i++) if (double.IsNaN(h[i])) lost++;
            double loss = lost * 100.0 / n;
            if (loss >= S.AlertPingLoss && loss < 100)
                Fire("ping", "Unstable connection", Fmt.Pct(loss) + " packet loss to " + target + " in the last minute.");
            else if (loss >= 100)
                Fire("ping", "Connection lost", "No ping replies from " + target + " in the last minute.");
        }
    }
}
