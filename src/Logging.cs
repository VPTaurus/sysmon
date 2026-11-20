using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace SysMonApp
{
    // One aggregated statistics row (also used for the in-memory live history)
    class Rec
    {
        public DateTime T;
        public double Cpu, CpuMax, Ram, RamGB, Gpu, GpuMax, GpuMem, DiskR, DiskW, DiskBusy, Rx, Tx, RxBytes, TxBytes;
        public double Ping = double.NaN, PingMax = double.NaN, PingLoss = double.NaN, Bat = double.NaN;
        public int OnAc = -1;
        public string TopCpu = "", TopMem = "";
        public double TopCpuPct, TopMemBytes;

        public const string Header = "time,cpu_pct,cpu_max_pct,ram_pct,ram_used_gb,gpu_pct,gpu_max_pct,gpu_mem_gb,disk_read_Bps,disk_write_Bps,disk_busy_max_pct," +
                                     "net_down_Bps,net_up_Bps,net_down_bytes,net_up_bytes,ping_ms,ping_max_ms,ping_loss_pct,battery_pct,on_ac," +
                                     "top_cpu_process,top_cpu_pct,top_mem_process,top_mem_bytes";
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        static string N(double v, string f) { return double.IsNaN(v) ? "" : v.ToString(f, IC); }
        static double P(string s) { double v; return double.TryParse(s, NumberStyles.Float, IC, out v) ? v : double.NaN; }
        public static string Clean(string s) { return (s ?? "").Replace(",", " ").Replace("\"", " ").Replace("\n", " "); }

        public string ToCsv()
        {
            return string.Join(",", new[] {
                T.ToString("yyyy-MM-dd HH:mm:ss", IC), N(Cpu, "0.0"), N(CpuMax, "0.0"), N(Ram, "0.0"), N(RamGB, "0.00"), N(Gpu, "0.0"), N(GpuMax, "0.0"),
                N(GpuMem, "0.00"), N(DiskR, "0"), N(DiskW, "0"), N(DiskBusy, "0.0"), N(Rx, "0"), N(Tx, "0"), N(RxBytes, "0"), N(TxBytes, "0"),
                N(Ping, "0.0"), N(PingMax, "0"), N(PingLoss, "0"), N(Bat, "0"), OnAc < 0 ? "" : OnAc.ToString(),
                Clean(TopCpu), N(TopCpuPct, "0.0"), Clean(TopMem), N(TopMemBytes, "0") });
        }

        public static Rec Parse(string line)
        {
            var p = line.Split(',');
            if (p.Length < 24) return null;
            DateTime t;
            if (!DateTime.TryParseExact(p[0], "yyyy-MM-dd HH:mm:ss", IC, DateTimeStyles.None, out t)) return null;
            var r = new Rec { T = t };
            r.Cpu = P(p[1]); r.CpuMax = P(p[2]); r.Ram = P(p[3]); r.RamGB = P(p[4]); r.Gpu = P(p[5]); r.GpuMax = P(p[6]); r.GpuMem = P(p[7]);
            r.DiskR = P(p[8]); r.DiskW = P(p[9]); r.DiskBusy = P(p[10]); r.Rx = P(p[11]); r.Tx = P(p[12]); r.RxBytes = P(p[13]); r.TxBytes = P(p[14]);
            r.Ping = P(p[15]); r.PingMax = P(p[16]); r.PingLoss = P(p[17]); r.Bat = P(p[18]);
            int ac; r.OnAc = int.TryParse(p[19], out ac) ? ac : -1;
            r.TopCpu = p[20]; r.TopCpuPct = P(p[21]); r.TopMem = p[22]; r.TopMemBytes = P(p[23]);
            return r;
        }

        // one-sample record for the live (1 s) history
        public static Rec FromSnapshot(Snapshot s, double ping)
        {
            var r = new Rec
            {
                T = s.Time, Cpu = s.Cpu, CpuMax = s.Cpu, Ram = s.MemLoad, RamGB = s.MemUsedGB, Gpu = s.Gpu, GpuMax = s.Gpu,
                GpuMem = s.GpuDedGB + s.GpuSharedGB, DiskR = s.DiskRead, DiskW = s.DiskWrite, DiskBusy = s.DiskActive,
                Rx = s.Rx, Tx = s.Tx, RxBytes = s.RxBytes, TxBytes = s.TxBytes, Ping = ping, PingMax = ping,
                Bat = s.HasBattery ? s.BatPct : double.NaN, OnAc = s.HasBattery ? (s.OnAc ? 1 : 0) : -1
            };
            if (s.TopCpu != null && s.TopCpu.Count > 0) { r.TopCpu = s.TopCpu[0].Name; r.TopCpuPct = s.TopCpu[0].Value; }
            if (s.TopMem != null && s.TopMem.Count > 0) { r.TopMem = s.TopMem[0].Name; r.TopMemBytes = s.TopMem[0].Value; }
            return r;
        }
    }

    class Anomaly
    {
        public DateTime T;
        public string Severity = "warning";   // info | warning | critical
        public string Metric = "", Message = "";
        public double Value = double.NaN, Baseline = double.NaN;
        public bool Quiet;   // already covered by a simple alert
    }

    static class LogStore
    {
        public static readonly string Dir = Path.Combine(Store.Dir, "logs");
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        static string AnomalyFile { get { return Path.Combine(Dir, "anomalies.csv"); } }

        static void AppendLine(string path, string header, string line)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                bool isNew = !File.Exists(path);
                using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                using (var w = new StreamWriter(fs, new UTF8Encoding(false)))
                {
                    if (isNew) w.WriteLine(header);
                    w.WriteLine(line);
                }
            }
            catch (Exception ex) { Store.Log("log write: " + ex.Message); }
        }

        static IEnumerable<string> ReadLines(string path)
        {
            if (!File.Exists(path)) yield break;
            FileStream fs = null;
            try { fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
            catch { yield break; }
            using (var r = new StreamReader(fs))
            {
                string l;
                while ((l = r.ReadLine()) != null) yield return l;
            }
        }

        public static void Append(Rec r) { AppendLine(Path.Combine(Dir, r.T.ToString("yyyy-MM-dd") + ".csv"), Rec.Header, r.ToCsv()); }

        public static void AppendProcs(DateTime t, List<ProcInfo> procs)
        {
            var pick = procs.OrderByDescending(p => p.Cpu).Take(10)
                            .Union(procs.OrderByDescending(p => p.Mem).Take(10)).Distinct().ToList();
            var sb = new StringBuilder();
            string ts = t.ToString("yyyy-MM-dd HH:mm:ss", IC);
            foreach (var p in pick)
            {
                if (sb.Length > 0) sb.Append(Environment.NewLine);
                sb.Append(ts).Append(',').Append(Rec.Clean(p.Name)).Append(',').Append(p.Cpu.ToString("0.0", IC)).Append(',').Append(p.Mem.ToString("0", IC));
            }
            if (sb.Length > 0) AppendLine(Path.Combine(Dir, "processes-" + t.ToString("yyyy-MM-dd") + ".csv"), "time,process,cpu_pct,private_mem_bytes", sb.ToString());
        }

        public static void AppendAnomaly(Anomaly a)
        {
            AppendLine(AnomalyFile, "time,severity,metric,value,baseline,message",
                string.Join(",", a.T.ToString("yyyy-MM-dd HH:mm:ss", IC), a.Severity, a.Metric,
                            double.IsNaN(a.Value) ? "" : a.Value.ToString("0.##", IC),
                            double.IsNaN(a.Baseline) ? "" : a.Baseline.ToString("0.##", IC),
                            "\"" + (a.Message ?? "").Replace("\"", "'") + "\""));
        }

        // processes logged closest to time t (the last entry at or before t + 30 s, else the first one after)
        public static List<ProcInfo> LoadProcsAt(DateTime t, out DateTime when)
        {
            when = DateTime.MinValue;
            var groups = new SortedDictionary<DateTime, List<ProcInfo>>();
            foreach (var l in ReadLines(Path.Combine(Dir, "processes-" + t.ToString("yyyy-MM-dd") + ".csv")))
            {
                if (l.StartsWith("time")) continue;
                var p = l.Split(',');
                if (p.Length < 4) continue;
                DateTime ts;
                if (!DateTime.TryParseExact(p[0], "yyyy-MM-dd HH:mm:ss", IC, DateTimeStyles.None, out ts)) continue;
                if (Math.Abs((ts - t).TotalMinutes) > 5) continue;
                double c, m;
                double.TryParse(p[2], NumberStyles.Float, IC, out c); double.TryParse(p[3], NumberStyles.Float, IC, out m);
                List<ProcInfo> g;
                if (!groups.TryGetValue(ts, out g)) groups[ts] = g = new List<ProcInfo>();
                g.Add(new ProcInfo { Name = p[1], Cpu = c, Mem = m });
            }
            if (groups.Count == 0) return null;
            var before = groups.Keys.Where(k => k <= t.AddSeconds(30)).ToList();
            when = before.Count > 0 ? before.Last() : groups.Keys.First();
            return groups[when];
        }

        // one process's CPU / memory from the per-minute process log (only minutes where it was in the top lists)
        public static List<ProcPoint> LoadProcessHistory(string name, DateTime from, DateTime to)
        {
            var list = new List<ProcPoint>();
            string prefix = "," + Rec.Clean(name) + ",";
            for (var d = from.Date; d <= to.Date; d = d.AddDays(1))
                foreach (var l in ReadLines(Path.Combine(Dir, "processes-" + d.ToString("yyyy-MM-dd") + ".csv")))
                {
                    if (l.Length < 21 || l.IndexOf(prefix, 19, StringComparison.OrdinalIgnoreCase) != 19) continue;
                    var p = l.Split(',');
                    if (p.Length < 4) continue;
                    DateTime ts;
                    if (!DateTime.TryParseExact(p[0], "yyyy-MM-dd HH:mm:ss", IC, DateTimeStyles.None, out ts) || ts < from || ts > to) continue;
                    double c, m;
                    double.TryParse(p[2], NumberStyles.Float, IC, out c); double.TryParse(p[3], NumberStyles.Float, IC, out m);
                    list.Add(new ProcPoint { T = ts, Cpu = c, Mem = m });
                }
            list.Sort((a, b) => a.T.CompareTo(b.T));
            return list;
        }

        public static List<Rec> Load(DateTime from, DateTime to)
        {
            var list = new List<Rec>();
            for (var d = from.Date; d <= to.Date; d = d.AddDays(1))
                foreach (var l in ReadLines(Path.Combine(Dir, d.ToString("yyyy-MM-dd") + ".csv")))
                {
                    if (l.StartsWith("time")) continue;
                    var r = Rec.Parse(l);
                    if (r != null && r.T >= from && r.T <= to) list.Add(r);
                }
            list.Sort((a, b) => a.T.CompareTo(b.T));
            return list;
        }

        public static List<Anomaly> LoadAnomalies(DateTime from, DateTime to)
        {
            var list = new List<Anomaly>();
            foreach (var l in ReadLines(AnomalyFile))
            {
                if (l.StartsWith("time")) continue;
                var p = l.Split(new[] { ',' }, 6);
                if (p.Length < 6) continue;
                DateTime t;
                if (!DateTime.TryParseExact(p[0], "yyyy-MM-dd HH:mm:ss", IC, DateTimeStyles.None, out t) || t < from || t > to) continue;
                double v, b;
                list.Add(new Anomaly
                {
                    T = t, Severity = p[1], Metric = p[2],
                    Value = double.TryParse(p[3], NumberStyles.Float, IC, out v) ? v : double.NaN,
                    Baseline = double.TryParse(p[4], NumberStyles.Float, IC, out b) ? b : double.NaN,
                    Message = p[5].Trim().Trim('"')
                });
            }
            return list;
        }

        public static void Cleanup(int keepDays)
        {
            try
            {
                if (!Directory.Exists(Dir)) return;
                var cutoff = DateTime.Today.AddDays(-Math.Max(1, keepDays));
                foreach (var f in Directory.GetFiles(Dir, "*.csv"))
                {
                    string n = Path.GetFileNameWithoutExtension(f);
                    if (n.StartsWith("processes-")) n = n.Substring(10);
                    DateTime d;
                    if (DateTime.TryParseExact(n, "yyyy-MM-dd", IC, DateTimeStyles.None, out d) && d < cutoff) File.Delete(f);
                }
                // trim old anomalies
                if (File.Exists(AnomalyFile))
                {
                    var keep = ReadLines(AnomalyFile).Where(l =>
                    {
                        DateTime t;
                        return l.StartsWith("time") || !DateTime.TryParseExact(l.Length >= 19 ? l.Substring(0, 19) : l, "yyyy-MM-dd HH:mm:ss", IC, DateTimeStyles.None, out t) || t >= cutoff;
                    }).ToList();
                    File.WriteAllLines(AnomalyFile, keep);
                }
            }
            catch (Exception ex) { Store.Log("cleanup: " + ex.Message); }
        }

        public static long SizeBytes()
        {
            try { return Directory.Exists(Dir) ? Directory.GetFiles(Dir).Sum(f => new FileInfo(f).Length) : 0; }
            catch { return 0; }
        }
    }

    // Aggregates 1 s samples into LogIntervalSec buckets, writes them, and hands them to the detector
    class Logger
    {
        DateTime bucket; int n;
        double sCpu, mCpu, sRam, sRamGB, sGpu, mGpu, sGpuMem, sR, sW, mBusy, sRx, sTx, bRx, bTx;
        readonly List<double> pings = new List<double>();
        Snapshot lastSnap;
        List<ProcInfo> lastProcs;
        DateTime lastProcLog = DateTime.MinValue;

        public event Action<Rec, List<ProcInfo>, Snapshot> Bucket;

        static DateTime Floor(DateTime t)
        {
            long iv = Math.Max(1, Program.S.LogIntervalSec) * TimeSpan.TicksPerSecond;
            return new DateTime(t.Ticks - t.Ticks % iv);
        }

        public void Add(Snapshot s)
        {
            var b = Floor(s.Time);
            if (n > 0 && b != bucket) Flush();
            if (n == 0) bucket = b;
            n++;
            sCpu += s.Cpu; mCpu = Math.Max(mCpu, s.Cpu);
            sRam += s.MemLoad; sRamGB += s.MemUsedGB;
            sGpu += s.Gpu; mGpu = Math.Max(mGpu, s.Gpu); sGpuMem += s.GpuDedGB + s.GpuSharedGB;
            sR += s.DiskRead; sW += s.DiskWrite; mBusy = Math.Max(mBusy, s.DiskActive);
            sRx += s.Rx; sTx += s.Tx; bRx += s.RxBytes; bTx += s.TxBytes;
            lastSnap = s;
            if (s.Procs != null) lastProcs = s.Procs;

            if (Program.S.Logging && Program.S.LogProcesses && lastProcs != null && (s.Time - lastProcLog).TotalSeconds >= 60)
            {
                lastProcLog = s.Time;
                LogStore.AppendProcs(s.Time, lastProcs);
            }
        }

        public void AddPing(double ms) { pings.Add(ms); }

        void Flush()
        {
            var r = new Rec
            {
                T = bucket, Cpu = sCpu / n, CpuMax = mCpu, Ram = sRam / n, RamGB = sRamGB / n, Gpu = sGpu / n, GpuMax = mGpu, GpuMem = sGpuMem / n,
                DiskR = sR / n, DiskW = sW / n, DiskBusy = mBusy, Rx = sRx / n, Tx = sTx / n, RxBytes = bRx, TxBytes = bTx
            };
            var ok = pings.Where(p => !double.IsNaN(p)).ToList();
            if (pings.Count > 0)
            {
                r.PingLoss = (pings.Count - ok.Count) * 100.0 / pings.Count;
                if (ok.Count > 0) { r.Ping = ok.Average(); r.PingMax = ok.Max(); }
            }
            var s = lastSnap;
            if (s != null)
            {
                if (s.HasBattery) { r.Bat = s.BatPct; r.OnAc = s.OnAc ? 1 : 0; }
                if (s.TopCpu != null && s.TopCpu.Count > 0) { r.TopCpu = s.TopCpu[0].Name; r.TopCpuPct = s.TopCpu[0].Value; }
                if (s.TopMem != null && s.TopMem.Count > 0) { r.TopMem = s.TopMem[0].Name; r.TopMemBytes = s.TopMem[0].Value; }
            }
            n = 0; sCpu = mCpu = sRam = sRamGB = sGpu = mGpu = sGpuMem = sR = sW = mBusy = sRx = sTx = bRx = bTx = 0;
            pings.Clear();

            if (Program.S.Logging) LogStore.Append(r);
            if (Bucket != null) Bucket(r, lastProcs, s);
        }
    }
}
