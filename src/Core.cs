using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media;

namespace SysMonApp
{
    public class Settings
    {
        public bool HasPos { get; set; }
        public double Left { get; set; }
        public double Top { get; set; }
        public string Theme { get; set; }
        public string Mode { get; set; }          // Full | Compact | Mini
        public int IntervalMs { get; set; }
        public int GraphSeconds { get; set; }
        public double Opacity { get; set; }
        public bool Locked { get; set; }
        public bool ClickThrough { get; set; }
        public bool Bits { get; set; }
        public bool Snap { get; set; }

        public bool ShowCpu { get; set; }
        public bool ShowCores { get; set; }
        public bool ShowRam { get; set; }
        public bool ShowGpu { get; set; }
        public bool ShowDiskIo { get; set; }
        public bool ShowStorage { get; set; }
        public bool ShowNet { get; set; }
        public bool ShowPing { get; set; }
        public bool ShowConn { get; set; }
        public bool ShowUsage { get; set; }
        public bool ShowProcs { get; set; }
        public bool ShowBattery { get; set; }

        public string PingTarget { get; set; }
        public bool PublicIp { get; set; }

        public bool AlertsOn { get; set; }
        public int AlertCpu { get; set; }
        public int AlertCpuSecs { get; set; }
        public int AlertRam { get; set; }
        public int AlertDiskFree { get; set; }
        public int AlertBattery { get; set; }
        public int AlertPingLoss { get; set; }
        public int AlertCooldownMin { get; set; }

        public bool WidgetVisible { get; set; }
        public bool TaskbarReadout { get; set; }
        public double TaskbarOffset { get; set; }     // DIPs from taskbar right edge; < 0 = automatic
        public bool Logging { get; set; }
        public int LogIntervalSec { get; set; }
        public int LogKeepDays { get; set; }
        public bool LogProcesses { get; set; }
        public bool Anomalies { get; set; }
        public string AnomalySensitivity { get; set; }  // Low | Medium | High
        public bool AnomalyNotify { get; set; }
        public string DashRange { get; set; }
        public bool HotkeysOn { get; set; }
        public string HotkeyDashboard { get; set; }
        public string HotkeyWidget { get; set; }
        public string HotkeyClickThrough { get; set; }
        public string HotkeyAwake { get; set; }

        public bool KeepAwakeScreen { get; set; }     // keep the display on too, not just the system
        public bool NudgeOn { get; set; }             // tiny mouse nudge to keep presence "available"
        public string NudgeFrom { get; set; }         // "HH:mm"
        public string NudgeTo { get; set; }
        public bool NudgeWeekdays { get; set; }
        public int NudgeIdleMin { get; set; }

        public string StartupView { get; set; }       // Remember | Widget | Taskbar | Dashboard | Tray
        public bool GpuRendering { get; set; }        // false = WPF software rendering (much lower memory)
        public bool ShowClock { get; set; }
        public bool ClockSeconds { get; set; }
        public bool ClockDate { get; set; }

        public Settings()
        {
            HotkeyAwake = "Ctrl+Alt+K"; KeepAwakeScreen = true;
            NudgeOn = false; NudgeFrom = "08:00"; NudgeTo = "18:00"; NudgeWeekdays = true; NudgeIdleMin = 4;
            ShowClock = true; ClockSeconds = false; ClockDate = true; StartupView = "Remember";
            HotkeysOn = true; HotkeyDashboard = "Ctrl+Alt+M"; HotkeyWidget = "Ctrl+Alt+W"; HotkeyClickThrough = "Ctrl+Alt+C";
            WidgetVisible = true; TaskbarReadout = false; TaskbarOffset = -1;
            Logging = true; LogIntervalSec = 10; LogKeepDays = 30; LogProcesses = true;
            Anomalies = true; AnomalySensitivity = "Medium"; AnomalyNotify = true; DashRange = "1 hour";
            Theme = "Dark"; Mode = "Full"; IntervalMs = 1000; GraphSeconds = 60; Opacity = 0.95; Snap = true;
            ShowCpu = ShowCores = ShowRam = ShowGpu = ShowDiskIo = ShowStorage = ShowNet = ShowPing = ShowProcs = ShowBattery = true;
            ShowConn = false; ShowUsage = false;
            PingTarget = "1.1.1.1"; PublicIp = true;
            AlertsOn = true; AlertCpu = 90; AlertCpuSecs = 30; AlertRam = 90; AlertDiskFree = 10;
            AlertBattery = 15; AlertPingLoss = 25; AlertCooldownMin = 10;
        }
    }

    public class Usage
    {
        public string Day { get; set; }
        public long DayRx { get; set; }
        public long DayTx { get; set; }
        public string Month { get; set; }
        public long MonthRx { get; set; }
        public long MonthTx { get; set; }

        public void Add(long rx, long tx)
        {
            var now = DateTime.Now;
            string d = now.ToString("yyyy-MM-dd"), m = now.ToString("yyyy-MM");
            if (Day != d) { Day = d; DayRx = 0; DayTx = 0; }
            if (Month != m) { Month = m; MonthRx = 0; MonthTx = 0; }
            DayRx += rx; DayTx += tx; MonthRx += rx; MonthTx += tx;
        }
    }

    static class Store
    {
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SysMonWidget");
        static readonly JavaScriptSerializer js = new JavaScriptSerializer();

        public static T Load<T>(string file) where T : new()
        {
            try
            {
                string p = Path.Combine(Dir, file);
                if (File.Exists(p)) return js.Deserialize<T>(File.ReadAllText(p));
            }
            catch (Exception ex) { Log("load " + file + ": " + ex.Message); }
            return new T();
        }

        public static void Save(string file, object o)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                string p = Path.Combine(Dir, file), tmp = p + ".tmp";
                File.WriteAllText(tmp, js.Serialize(o));
                if (File.Exists(p)) File.Replace(tmp, p, null); else File.Move(tmp, p);
            }
            catch (Exception ex) { Log("save " + file + ": " + ex.Message); }
        }

        public static void Log(string msg)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(Path.Combine(Dir, "error.log"), DateTime.Now.ToString("s") + "  " + msg + Environment.NewLine);
            }
            catch { }
        }
    }

    class Theme
    {
        public Brush Bg, Border, Fg, Muted, Faint, Track, HoverBg;
        public Brush Cpu, Ram, Gpu, Read, Write, Down, Up, Ping, Good, Warn, Bad;
        public bool IsLight;

        public static readonly string[] Names = { "Dark", "Light", "Arctic", "Dusk" };

        static Brush B(string c) { var b = (Brush)new BrushConverter().ConvertFromString(c); b.Freeze(); return b; }

        public static Brush Fill(Brush b, byte alpha)
        {
            var sc = b as SolidColorBrush;
            if (sc == null) return b;
            var c = sc.Color; c.A = alpha;
            var r = new SolidColorBrush(c); r.Freeze(); return r;
        }

        public static Theme Get(string name)
        {
            var t = new Theme();
            switch (name)
            {
                case "Light":
                    t.IsLight = true;
                    t.Bg = B("#FFFBFBFC"); t.Border = B("#26000000"); t.Fg = B("#202124"); t.Muted = B("#5F6368"); t.Faint = B("#80868B");
                    t.Track = B("#0F000000"); t.HoverBg = B("#F0FFFFFF");
                    t.Cpu = B("#0277BD"); t.Ram = B("#7E57C2"); t.Gpu = B("#D84315"); t.Read = B("#00838F"); t.Write = B("#C2185B");
                    t.Down = B("#2E7D32"); t.Up = B("#EF6C00"); t.Ping = B("#9E7C00"); t.Good = B("#388E3C"); t.Warn = B("#F9A825"); t.Bad = B("#D32F2F");
                    break;
                case "Arctic":
                    t.Bg = B("#FF2E3440"); t.Border = B("#334C566A"); t.Fg = B("#ECEFF4"); t.Muted = B("#AEB7C6"); t.Faint = B("#7B869A");
                    t.Track = B("#14ECEFF4"); t.HoverBg = B("#F03B4252");
                    t.Cpu = B("#88C0D0"); t.Ram = B("#B48EAD"); t.Gpu = B("#D08770"); t.Read = B("#8FBCBB"); t.Write = B("#BF616A");
                    t.Down = B("#A3BE8C"); t.Up = B("#EBCB8B"); t.Ping = B("#81A1C1"); t.Good = B("#A3BE8C"); t.Warn = B("#EBCB8B"); t.Bad = B("#BF616A");
                    break;
                case "Dusk":
                    t.Bg = B("#FF282A36"); t.Border = B("#3344475A"); t.Fg = B("#F8F8F2"); t.Muted = B("#A9ADC8"); t.Faint = B("#7A7F9E");
                    t.Track = B("#14F8F8F2"); t.HoverBg = B("#F044475A");
                    t.Cpu = B("#8BE9FD"); t.Ram = B("#BD93F9"); t.Gpu = B("#FF79C6"); t.Read = B("#8BE9FD"); t.Write = B("#FF79C6");
                    t.Down = B("#50FA7B"); t.Up = B("#FFB86C"); t.Ping = B("#F1FA8C"); t.Good = B("#50FA7B"); t.Warn = B("#F1FA8C"); t.Bad = B("#FF5555");
                    break;
                default:
                    t.Bg = B("#FF161A22"); t.Border = B("#30FFFFFF"); t.Fg = B("#E8EAED"); t.Muted = B("#9AA0A6"); t.Faint = B("#80868B");
                    t.Track = B("#12FFFFFF"); t.HoverBg = B("#F0262B35");
                    t.Cpu = B("#4FC3F7"); t.Ram = B("#B388FF"); t.Gpu = B("#FF8A65"); t.Read = B("#80DEEA"); t.Write = B("#F48FB1");
                    t.Down = B("#69F0AE"); t.Up = B("#FFB74D"); t.Ping = B("#FFF176"); t.Good = B("#81C784"); t.Warn = B("#FFD54F"); t.Bad = B("#FF6E6E");
                    break;
            }
            return t;
        }

        public Brush Level(double pct, Brush normal, double warn, double bad)
        {
            return pct >= bad ? Bad : pct >= warn ? Warn : normal;
        }
    }

    static class Fmt
    {
        public const string Dn = "↓", UpA = "↑", Dot = "  ·  ";
        static readonly CultureInfo C = CultureInfo.CurrentCulture;

        public static string Rate(double bps, bool bits)
        {
            string[] u; double v, k;
            if (bits) { v = bps * 8; u = new[] { "bps", "Kbps", "Mbps", "Gbps" }; k = 1000; }
            else { v = bps; u = new[] { "B/s", "KB/s", "MB/s", "GB/s" }; k = 1024; }
            int i = 0;
            while (v >= k && i < 3) { v /= k; i++; }
            return Num(v, i) + " " + u[i];
        }

        public static string Size(double b)
        {
            string[] u = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            while (b >= 1024 && i < 4) { b /= 1024; i++; }
            return (i < 2 ? b.ToString("0", C) : b.ToString(b < 10 ? "0.0" : "0", C)) + " " + u[i];
        }

        static string Num(double v, int unitIndex)
        {
            if (unitIndex == 0) return v.ToString("0", C);
            return v.ToString(v < 10 ? "0.0" : "0", C);
        }

        public static string Pct(double v) { return v.ToString("0", C) + "%"; }

        public static string Duration(double seconds)
        {
            var t = TimeSpan.FromSeconds(seconds);
            if (t.TotalDays >= 1) return string.Format("{0}d {1}h {2}m", (int)t.TotalDays, t.Hours, t.Minutes);
            if (t.TotalHours >= 1) return string.Format("{0}h {1}m", t.Hours, t.Minutes);
            return string.Format("{0}m", t.Minutes);
        }

        public static string Ago(double seconds)
        {
            if (seconds < 1) return "now";
            if (seconds < 60) return "-" + seconds.ToString("0", C) + "s";
            return "-" + ((int)(seconds / 60)) + "m" + ((int)seconds % 60).ToString("00") + "s";
        }
    }

    // fixed-capacity ring buffer of samples
    class Ring
    {
        double[] b; int start, n;
        public Ring(int cap) { b = new double[Math.Max(2, cap)]; }
        public int Count { get { return n; } }
        public int Capacity { get { return b.Length; } }
        public double this[int i] { get { return b[(start + i) % b.Length]; } }
        public double Last { get { return n == 0 ? double.NaN : this[n - 1]; } }

        public void Add(double v)
        {
            if (n < b.Length) { b[(start + n) % b.Length] = v; n++; }
            else { b[start] = v; start = (start + 1) % b.Length; }
        }

        public void Resize(int cap)
        {
            cap = Math.Max(2, cap);
            if (cap == b.Length) return;
            var nb = new double[cap];
            int keep = Math.Min(n, cap);
            for (int i = 0; i < keep; i++) nb[i] = this[n - keep + i];
            b = nb; start = 0; n = keep;
        }

        public void Clear() { n = 0; start = 0; }

        public double Max()
        {
            double m = 0;
            for (int i = 0; i < n; i++) { double v = this[i]; if (!double.IsNaN(v) && v > m) m = v; }
            return m;
        }
    }
}
