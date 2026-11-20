using System;
using System.Collections.Generic;
using System.Linq;

namespace SysMonApp
{
    /*
      Anomaly detection, run on every aggregated log bucket (default 10 s):
      1. Learned baseline per metric: exponentially weighted mean/variance (half-life 2 h),
         rates in log space. A value is unusual when it is K standard deviations above normal
         (K = 5/4/3 for Low/Medium/High sensitivity), above an absolute floor, and stays so for 30 s.
      2. Rules: sustained overload (CPU, RAM, disk, GPU), connection loss / packet loss,
         memory leaks (steady per-process growth, linear fit over 30-60 min), new CPU-heavy processes,
         fast free-space drop on a drive, fast battery drain.
    */
    class Detector
    {
        class Base { public double Mean, Var; public int N, Streak; public double Peak; public bool Fired; }

        class Metric
        {
            public string Key, Label; public Func<Rec, double> Get; public bool Log; public double Scale = 1, Floor, MinDelta, MinStd;
            public Func<double, string> Fmt;
            public double T(double x) { return Log ? Math.Log(1 + Math.Max(0, x) / Scale) : x; }
            public double Inv(double y) { return Log ? (Math.Exp(y) - 1) * Scale : y; }
        }

        static Settings S { get { return Program.S; } }
        readonly List<Metric> metrics;
        readonly Dictionary<string, Base> bases = new Dictionary<string, Base>();
        readonly Dictionary<string, int> streaks = new Dictionary<string, int>();
        readonly HashSet<string> episodeFired = new HashSet<string>();
        readonly Dictionary<string, DateTime> lastFired = new Dictionary<string, DateTime>();
        readonly Dictionary<string, List<KeyValuePair<DateTime, double>>> procMem = new Dictionary<string, List<KeyValuePair<DateTime, double>>>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, DateTime> firstSeen = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, List<KeyValuePair<DateTime, double>>> driveFree = new Dictionary<string, List<KeyValuePair<DateTime, double>>>();
        readonly List<KeyValuePair<DateTime, double>> battery = new List<KeyValuePair<DateTime, double>>();
        DateTime lastProcSample = DateTime.MinValue, started = DateTime.MinValue, clock = DateTime.Now;
        readonly bool test;   // self-test instance: runs even when detection is switched off

        public readonly List<Anomaly> Recent = new List<Anomaly>();
        public event Action<Anomaly> Detected;

        public Detector(bool test = false)
        {
            this.test = test;
            Func<double, string> pct = Fmt.Pct, rate = v => Fmt.Rate(v, false), ms = v => v.ToString("0") + " ms";
            metrics = new List<Metric>
            {
                new Metric { Key = "cpu", Label = "CPU usage", Get = r => r.Cpu, Floor = 50, MinDelta = 25, MinStd = 4, Fmt = pct },
                new Metric { Key = "ram", Label = "Memory usage", Get = r => r.Ram, Floor = 75, MinDelta = 12, MinStd = 2, Fmt = pct },
                new Metric { Key = "gpu", Label = "GPU usage", Get = r => r.Gpu, Floor = 50, MinDelta = 30, MinStd = 4, Fmt = pct },
                new Metric { Key = "disk", Label = "Disk activity", Get = r => r.DiskR + r.DiskW, Log = true, Scale = 1 << 20, Floor = 30 << 20, MinDelta = 20 << 20, MinStd = 0.3, Fmt = rate },
                new Metric { Key = "rx", Label = "Download traffic", Get = r => r.Rx, Log = true, Scale = 256 << 10, Floor = 3 << 20, MinDelta = 2 << 20, MinStd = 0.3, Fmt = rate },
                new Metric { Key = "tx", Label = "Upload traffic", Get = r => r.Tx, Log = true, Scale = 128 << 10, Floor = 1 << 20, MinDelta = 800 << 10, MinStd = 0.3, Fmt = rate },
                new Metric { Key = "ping", Label = "Ping latency", Get = r => r.Ping, Log = true, Scale = 10, Floor = 80, MinDelta = 50, MinStd = 0.15, Fmt = ms },
            };
        }

        int Interval { get { return Math.Max(1, S.LogIntervalSec); } }
        double K { get { return S.AnomalySensitivity == "High" ? 3 : S.AnomalySensitivity == "Low" ? 5 : 4; } }

        // feed history without raising anything (fast warm-up after a restart)
        public void Seed(IEnumerable<Rec> history) { foreach (var r in history) Learn(r, false); }

        public void OnBucket(Rec r, List<ProcInfo> procs, Snapshot snap)
        {
            if (!S.Anomalies && !test) return;
            clock = r.T;                       // all timing follows the data, so replays work
            if (started == DateTime.MinValue) started = r.T;
            Learn(r, true);
            Rules(r);
            if (procs != null) Processes(r, procs);
            Battery(r);
        }

        // ---------------------------------------------------------------- 1. learned baselines
        void Learn(Rec r, bool detect)
        {
            int iv = Interval;
            double alpha = 1 - Math.Pow(0.5, 1.0 / (7200.0 / iv));
            int warm = 1800 / iv, need = Math.Max(2, (int)Math.Ceiling(30.0 / iv));
            foreach (var m in metrics)
            {
                double raw = m.Get(r);
                if (double.IsNaN(raw)) continue;
                double x = m.T(raw);
                Base b;
                if (!bases.TryGetValue(m.Key, out b)) { bases[m.Key] = b = new Base { Mean = x }; }
                double sd = Math.Max(Math.Sqrt(b.Var), m.MinStd);
                double normal = m.Inv(b.Mean);
                bool unusual = b.N >= warm && (x - b.Mean) / sd >= K && raw >= m.Floor && raw - normal >= m.MinDelta;

                if (detect && unusual)
                {
                    b.Streak++; b.Peak = Math.Max(b.Peak, raw);
                    if (b.Streak >= need && !b.Fired)
                    {
                        b.Fired = true;
                        string msg = m.Label + " unusually high: " + m.Fmt(b.Peak) + " (normal is about " + m.Fmt(normal) + ")" + Context(m.Key, r);
                        Fire("z:" + m.Key, (x - b.Mean) / sd >= K + 2 ? "warning" : "info", m.Key, b.Peak, normal, msg, 15);
                    }
                    alpha /= 10;   // don't let the anomaly itself become "normal" quickly
                }
                else { b.Streak = 0; b.Peak = 0; b.Fired = false; }

                double diff = x - b.Mean, inc = alpha * diff;
                b.Mean += inc;
                b.Var = (1 - alpha) * (b.Var + diff * inc);
                b.N++;
                alpha = 1 - Math.Pow(0.5, 1.0 / (7200.0 / iv));
            }
        }

        static string Context(string key, Rec r)
        {
            if ((key == "cpu" || key == "gpu" || key == "disk") && r.TopCpu != "")
                return ". Top CPU process: " + r.TopCpu + " (" + r.TopCpuPct.ToString("0") + "%)";
            if (key == "ram" && r.TopMem != "") return ". Largest process: " + r.TopMem + " (" + Fmt.Size(r.TopMemBytes) + ")";
            return "";
        }

        // ---------------------------------------------------------------- 2. rules
        bool Sustain(string key, bool cond, double seconds)
        {
            int c;
            streaks.TryGetValue(key, out c);
            if (!cond) { streaks[key] = 0; episodeFired.Remove(key); return false; }
            streaks[key] = ++c;
            if (c * Interval >= seconds && !episodeFired.Contains(key)) { episodeFired.Add(key); return true; }
            return false;
        }

        void Rules(Rec r)
        {
            if (Sustain("cpu-high", r.Cpu >= 90, 120))
                Fire("cpu-high", "warning", "cpu", r.Cpu, double.NaN, "CPU overloaded: above 90% for 2 minutes" + Context("cpu", r), 30, true);
            if (Sustain("ram-high", r.Ram >= 92, 60))
                Fire("ram-high", r.Ram >= 95 ? "critical" : "warning", "ram", r.Ram, double.NaN,
                     "Memory nearly full: " + Fmt.Pct(r.Ram) + " (" + r.RamGB.ToString("0.0") + " GB used)" + Context("ram", r), 30, true);
            if (Sustain("disk-busy", r.DiskBusy >= 95, 120))
                Fire("disk-busy", "warning", "disk", r.DiskBusy, double.NaN, "Disk saturated: over 95% busy for 2 minutes" + Context("disk", r), 30);
            if (Sustain("gpu-high", r.GpuMax >= 95, 300))
                Fire("gpu-high", "info", "gpu", r.Gpu, double.NaN, "GPU at full load for 5 minutes", 60);
            if (!double.IsNaN(r.PingLoss))
            {
                if (Sustain("net-down", r.PingLoss >= 100, 30))
                    Fire("net-down", "critical", "ping", 100, double.NaN, "Connection lost: no ping replies for 30 seconds", 10, true);
                if (Sustain("ping-loss", r.PingLoss >= 20 && r.PingLoss < 100, 60))
                    Fire("ping-loss", "warning", "ping", r.PingLoss, double.NaN, "Unstable connection: " + Fmt.Pct(r.PingLoss) + " packet loss", 15, true);
            }
        }

        void Processes(Rec r, List<ProcInfo> procs)
        {
            var now = r.T;
            foreach (var p in procs)
                if (!firstSeen.ContainsKey(p.Name)) firstSeen[p.Name] = now;

            // new CPU-heavy process (appeared after the first 10 minutes of monitoring)
            if ((now - started).TotalMinutes > 10)
                foreach (var p in procs)
                {
                    bool isNew = (now - firstSeen[p.Name]).TotalMinutes < 10;
                    if (Sustain("newproc:" + p.Name, isNew && p.Cpu >= 40, 60))
                        Fire("newproc:" + p.Name, "info", "cpu", p.Cpu, double.NaN,
                             "New process " + p.Name + " is using " + p.Cpu.ToString("0") + "% CPU", 60);
                }

            // memory leak: steady growth over 30-60 minutes
            if ((now - lastProcSample).TotalSeconds < 60) return;
            lastProcSample = now;
            foreach (var p in procs.OrderByDescending(x => x.Mem).Take(40))
            {
                List<KeyValuePair<DateTime, double>> h;
                if (!procMem.TryGetValue(p.Name, out h)) procMem[p.Name] = h = new List<KeyValuePair<DateTime, double>>();
                h.Add(new KeyValuePair<DateTime, double>(now, p.Mem));
                h.RemoveAll(kv => (now - kv.Key).TotalMinutes > 60);
                if (h.Count < 30) continue;
                double first = h[0].Value, lastV = h[h.Count - 1].Value, growth = lastV - first;
                if (growth < 200.0 * (1 << 20) || lastV < 300.0 * (1 << 20)) continue;
                double r2, slope = Fit(h, out r2);
                if (slope > 0 && r2 >= 0.8)
                {
                    double mins = (h[h.Count - 1].Key - h[0].Key).TotalMinutes;
                    Fire("leak:" + p.Name, "warning", "ram", lastV, first,
                         "Possible memory leak: " + p.Name + " grew by " + Fmt.Size(growth) + " in " + mins.ToString("0") + " min (now " + Fmt.Size(lastV) + ")", 60);
                }
            }
            foreach (var k in procMem.Where(kv => kv.Value.Count == 0 || (now - kv.Value[kv.Value.Count - 1].Key).TotalMinutes > 5).Select(kv => kv.Key).ToList())
                procMem.Remove(k);
        }

        static double Fit(List<KeyValuePair<DateTime, double>> h, out double r2)
        {
            double t0 = h[0].Key.Ticks, n = h.Count, sx = 0, sy = 0, sxx = 0, sxy = 0, syy = 0;
            foreach (var kv in h)
            {
                double x = (kv.Key.Ticks - t0) / (double)TimeSpan.TicksPerMinute, y = kv.Value;
                sx += x; sy += y; sxx += x * x; sxy += x * y; syy += y * y;
            }
            double cov = sxy - sx * sy / n, vx = sxx - sx * sx / n, vy = syy - sy * sy / n;
            r2 = vx > 0 && vy > 0 ? cov * cov / (vx * vy) : 0;
            return vx > 0 ? cov / vx : 0;
        }

        public void CheckDrives(DateTime now, List<KeyValuePair<string, double>> free)
        {
            clock = now;
            foreach (var d in free)
            {
                List<KeyValuePair<DateTime, double>> h;
                if (!driveFree.TryGetValue(d.Key, out h)) driveFree[d.Key] = h = new List<KeyValuePair<DateTime, double>>();
                h.Add(new KeyValuePair<DateTime, double>(now, d.Value));
                h.RemoveAll(kv => (now - kv.Key).TotalMinutes > 10.5);
                double drop = h[0].Value - d.Value;
                if ((now - h[0].Key).TotalMinutes >= 3 && drop >= 5.0 * (1L << 30))
                    Fire("drive:" + d.Key, "warning", "disk", d.Value, h[0].Value,
                         "Free space on " + d.Key.TrimEnd('\\') + " dropped by " + Fmt.Size(drop) + " within the last " + (now - h[0].Key).TotalMinutes.ToString("0") + " min (now " + Fmt.Size(d.Value) + " free)", 30);
            }
        }

        void Battery(Rec r)
        {
            if (r.OnAc != 0 || double.IsNaN(r.Bat)) { battery.Clear(); return; }
            battery.Add(new KeyValuePair<DateTime, double>(r.T, r.Bat));
            battery.RemoveAll(kv => (r.T - kv.Key).TotalMinutes > 15);
            double mins = (r.T - battery[0].Key).TotalMinutes;
            if (mins < 10) return;
            double perHour = (battery[0].Value - r.Bat) / mins * 60;
            if (perHour >= 25)
                Fire("battery", "warning", "battery", perHour, double.NaN,
                     "Battery draining fast: about " + perHour.ToString("0") + "% per hour" + Context("cpu", r), 60);
        }

        // ---------------------------------------------------------------- raise
        void Fire(string key, string severity, string metric, double value, double baseline, string msg, int cooldownMin, bool quiet = false)
        {
            if (!S.Anomalies && !test) return;
            DateTime t;
            if (lastFired.TryGetValue(key, out t) && (clock - t).TotalMinutes < cooldownMin) return;
            lastFired[key] = clock;
            var a = new Anomaly { T = clock, Severity = severity, Metric = metric, Value = value, Baseline = baseline, Message = msg, Quiet = quiet };
            Recent.Add(a);
            if (Recent.Count > 500) Recent.RemoveAt(0);
            if (Detected != null) Detected(a);
        }
    }
}
