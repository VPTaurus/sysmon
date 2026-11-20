using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SysMonApp
{
    class DiskIo { public string Name; public double Read, Write, Active; }
    class ProcInfo { public string Name; public double Value, Cpu, Mem; }
    struct ProcPoint { public DateTime T; public double Cpu, Mem; }

    class Snapshot
    {
        public double Cpu;
        public double[] Cores = new double[0];
        public double MemUsedGB, MemTotalGB, MemLoad;
        public bool HasGpu; public double Gpu, GpuDedGB, GpuSharedGB, GpuTemp = double.NaN;
        public List<DiskIo> Disks = new List<DiskIo>();
        public double DiskRead, DiskWrite, DiskActive;
        public double Rx, Tx; public long RxBytes, TxBytes;
        public string IfName = "", LocalIp = "", Gateway = "";
        public bool IsWireless;
        public List<ProcInfo> TopCpu, TopMem;      // null when not refreshed this tick
        public List<ProcInfo> Procs;               // all processes (grouped by name), null when not refreshed
        public List<KeyValuePair<string, double>> DriveFree;   // set by the app every minute
        public DateTime Time = DateTime.Now;
        public bool HasBattery; public int BatPct; public bool Charging, OnAc; public int BatSecs = -1;
        public WifiInfo Wifi; public bool WifiUpdated;
    }

    class Sampler
    {
        readonly PdhQuery qFast = new PdhQuery(), qProc = new PdhQuery();
        readonly IntPtr cCpu, cGpu, cGpuDed, cGpuShared, cDiskR, cDiskW, cDiskIdle, cProcCpu, cProcMem;
        readonly int nCpu = Environment.ProcessorCount;
        int tick;
        DateTime lastProc = DateTime.MinValue;
        string nvidiaSmi;
        double gpuTemp = double.NaN;
        WifiInfo wifi;

        public static volatile string CurrentGateway = "";

        public Sampler()
        {
            cCpu = qFast.Add(@"\Processor Information(*)\% Processor Utility");
            if (cCpu == IntPtr.Zero) cCpu = qFast.Add(@"\Processor(*)\% Processor Time");
            cGpu = qFast.Add(@"\GPU Engine(*)\Utilization Percentage");
            cGpuDed = qFast.Add(@"\GPU Adapter Memory(*)\Dedicated Usage");
            cGpuShared = qFast.Add(@"\GPU Adapter Memory(*)\Shared Usage");
            cDiskR = qFast.Add(@"\PhysicalDisk(*)\Disk Read Bytes/sec");
            cDiskW = qFast.Add(@"\PhysicalDisk(*)\Disk Write Bytes/sec");
            cDiskIdle = qFast.Add(@"\PhysicalDisk(*)\% Idle Time");
            cProcCpu = qProc.Add(@"\Process(*)\% Processor Time");
            cProcMem = qProc.Add(@"\Process(*)\Working Set - Private");
            qFast.Collect(); qProc.Collect();
            NetSample(); // prime
            nvidiaSmi = FindOnPath("nvidia-smi.exe");
        }

        static string FindOnPath(string exe)
        {
            try
            {
                var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').ToList();
                dirs.Add(Environment.SystemDirectory);
                foreach (var d in dirs)
                {
                    if (string.IsNullOrWhiteSpace(d)) continue;
                    var p = Path.Combine(d.Trim(), exe);
                    if (File.Exists(p)) return p;
                }
            }
            catch { }
            return null;
        }

        public Snapshot Sample()
        {
            tick++;
            var s = new Snapshot();
            qFast.Collect();

            // ---- CPU ----
            var cpu = qFast.Read(cCpu);
            var cores = new List<KeyValuePair<string, double>>();
            bool gotTotal = false;
            foreach (var kv in cpu)
            {
                if (kv.Key == "_Total") { s.Cpu = Clamp(kv.Value); gotTotal = true; }
                else if (!kv.Key.Contains("_Total")) cores.Add(kv);
            }
            cores.Sort((a, b) => CoreKey(a.Key).CompareTo(CoreKey(b.Key)));
            s.Cores = cores.Select(c => Clamp(c.Value)).ToArray();
            if (!gotTotal) s.Cpu = SysTimesCpu();

            // ---- RAM ----
            var m = new Native.MEMORYSTATUSEX();
            if (Native.GlobalMemoryStatusEx(m))
            {
                s.MemTotalGB = m.ullTotalPhys / 1073741824.0;
                s.MemUsedGB = (m.ullTotalPhys - m.ullAvailPhys) / 1073741824.0;
                s.MemLoad = s.MemTotalGB > 0 ? s.MemUsedGB / s.MemTotalGB * 100 : 0;
            }

            // ---- GPU: Task Manager style = busiest engine type ----
            var eng = qFast.Read(cGpu);
            if (eng.Count > 0 || cGpu != IntPtr.Zero)
            {
                var byType = new Dictionary<string, double>();
                foreach (var kv in eng)
                {
                    int i = kv.Key.IndexOf("engtype_", StringComparison.Ordinal);
                    string t = i >= 0 ? kv.Key.Substring(i + 8) : "other";
                    double v; byType.TryGetValue(t, out v);
                    byType[t] = v + kv.Value;
                }
                s.HasGpu = cGpu != IntPtr.Zero;
                s.Gpu = byType.Count > 0 ? Clamp(byType.Values.Max()) : 0;
                s.GpuDedGB = qFast.Read(cGpuDed).Sum(kv => kv.Value) / 1073741824.0;
                s.GpuSharedGB = qFast.Read(cGpuShared).Sum(kv => kv.Value) / 1073741824.0;
            }
            if (nvidiaSmi != null && tick % 5 == 1) gpuTemp = NvidiaTemp();
            s.GpuTemp = gpuTemp;

            // ---- Disk I/O ----
            var dr = qFast.Read(cDiskR).ToDictionary(k => k.Key, k => k.Value);
            var dw = qFast.Read(cDiskW).ToDictionary(k => k.Key, k => k.Value);
            var di = qFast.Read(cDiskIdle).ToDictionary(k => k.Key, k => k.Value);
            foreach (var name in dr.Keys.OrderBy(k => k))
            {
                double r = dr[name], w = 0, idle = 100;
                dw.TryGetValue(name, out w);
                if (!di.TryGetValue(name, out idle)) idle = 100;
                double act = Clamp(100 - idle);
                if (name == "_Total") { s.DiskRead = r; s.DiskWrite = w; continue; }
                s.Disks.Add(new DiskIo { Name = name, Read = r, Write = w, Active = act });
                s.DiskActive = Math.Max(s.DiskActive, act);
            }

            // ---- Network ----
            NetSample();
            s.Rx = rxRate; s.Tx = txRate; s.RxBytes = rxDelta; s.TxBytes = txDelta;
            s.IfName = ifName; s.LocalIp = localIp; s.Gateway = gateway; s.IsWireless = isWireless;
            CurrentGateway = gateway;

            // ---- Top processes (every 2 s) ----
            if ((DateTime.UtcNow - lastProc).TotalSeconds >= 1.9)
            {
                lastProc = DateTime.UtcNow;
                qProc.Collect();
                var pc = Group(qProc.Read(cProcCpu));
                var pm = Group(qProc.Read(cProcMem));
                s.Procs = new List<ProcInfo>();
                foreach (var name in pc.Keys.Union(pm.Keys, StringComparer.OrdinalIgnoreCase))
                {
                    double c, mm;
                    pc.TryGetValue(name, out c); pm.TryGetValue(name, out mm);
                    s.Procs.Add(new ProcInfo { Name = name, Cpu = Math.Min(100, c / nCpu), Mem = mm });
                }
                s.TopCpu = pc.OrderByDescending(k => k.Value).Take(3)
                             .Select(k => new ProcInfo { Name = k.Key, Value = Math.Min(100, k.Value / nCpu) }).ToList();
                s.TopMem = pm.OrderByDescending(k => k.Value).Take(3)
                             .Select(k => new ProcInfo { Name = k.Key, Value = k.Value }).ToList();
            }

            // ---- Battery ----
            Native.SYSTEM_POWER_STATUS ps;
            if (Native.GetSystemPowerStatus(out ps) && (ps.BatteryFlag & 128) == 0 && ps.BatteryFlag != 255 && ps.BatteryLifePercent <= 100)
            {
                s.HasBattery = true;
                s.BatPct = ps.BatteryLifePercent;
                s.OnAc = ps.ACLineStatus == 1;
                s.Charging = (ps.BatteryFlag & 8) != 0;
                s.BatSecs = ps.BatteryLifeTime;
            }

            // ---- Wi-Fi (every 5 s) ----
            if (tick % 5 == 1) { wifi = Wlan.Query(); s.WifiUpdated = true; }
            s.Wifi = wifi;
            return s;
        }

        static Dictionary<string, double> Group(List<KeyValuePair<string, double>> raw)
        {
            var d = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in raw)
            {
                string n = kv.Key;
                if (n == "_Total" || n == "Idle") continue;
                int h = n.IndexOf('#');
                if (h > 0) n = n.Substring(0, h);
                double v; d.TryGetValue(n, out v);
                d[n] = v + kv.Value;
            }
            return d;
        }

        static long CoreKey(string name)
        {
            // "0,12" -> group 0, core 12
            var p = name.Split(',');
            long a = 0, b = 0;
            if (p.Length == 2) { long.TryParse(p[0], out a); long.TryParse(p[1], out b); }
            else long.TryParse(name, out b);
            return a * 100000 + b;
        }

        static double Clamp(double v) { return double.IsNaN(v) ? 0 : Math.Max(0, Math.Min(100, v)); }

        long pIdle, pKernel, pUser;
        double SysTimesCpu()
        {
            long i, k, u;
            if (!Native.GetSystemTimes(out i, out k, out u)) return 0;
            long di = i - pIdle, total = (k - pKernel) + (u - pUser);
            bool first = pKernel == 0;
            pIdle = i; pKernel = k; pUser = u;
            return first || total <= 0 ? 0 : Clamp((total - di) * 100.0 / total);
        }

        double NvidiaTemp()
        {
            try
            {
                var psi = new ProcessStartInfo(nvidiaSmi, "--query-gpu=temperature.gpu --format=csv,noheader,nounits")
                { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
                using (var p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(2000);
                    double t;
                    if (double.TryParse(o.Split('\n')[0].Trim(), System.Globalization.NumberStyles.Float,
                                        System.Globalization.CultureInfo.InvariantCulture, out t)) return t;
                }
            }
            catch { nvidiaSmi = null; }
            return double.NaN;
        }

        // ---- network (adapters with a default gateway; avoids double counting virtual switches) ----
        Dictionary<string, long[]> prev = new Dictionary<string, long[]>();
        DateTime prevT = DateTime.MinValue;
        double rxRate, txRate; long rxDelta, txDelta;
        string ifName = "", localIp = "", gateway = ""; bool isWireless;

        static long Delta(long now, long before)
        {
            long d = now - before;
            if (d < 0 && before <= 0xFFFFFFFFL) d += 0x100000000L;
            return d < 0 ? 0 : d;
        }

        void NetSample()
        {
            DateTime now = DateTime.UtcNow;
            double dt = prevT == DateTime.MinValue ? 0 : (now - prevT).TotalSeconds;
            prevT = now;
            var up = new List<NetworkInterface>();
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up) continue;
                var t = n.NetworkInterfaceType;
                if (t == NetworkInterfaceType.Loopback || t == NetworkInterfaceType.Tunnel) continue;
                up.Add(n);
            }
            var sel = new List<NetworkInterface>();
            string gw = "", ip = "";
            bool wl = false;
            foreach (var n in up)
            {
                string g = null;
                IPInterfaceProperties props = null;
                try
                {
                    props = n.GetIPProperties();
                    foreach (var ga in props.GatewayAddresses)
                    {
                        var a = ga.Address;
                        if (a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any)) continue;
                        if (g == null || a.AddressFamily == AddressFamily.InterNetwork) g = a.ToString();
                    }
                }
                catch { }
                if (g == null) continue;
                sel.Add(n);
                if (gw == "")
                {
                    gw = g;
                    wl = n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
                    foreach (var ua in props.UnicastAddresses)
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork) { ip = ua.Address.ToString(); break; }
                }
            }
            if (sel.Count == 0) sel = up;

            long rx = 0, tx = 0;
            var names = new List<string>();
            var cur = new Dictionary<string, long[]>();
            foreach (var n in sel)
            {
                long r, w;
                try { var st = n.GetIPStatistics(); r = st.BytesReceived; w = st.BytesSent; }
                catch { continue; }
                cur[n.Id] = new long[] { r, w };
                long[] p;
                if (prev.TryGetValue(n.Id, out p)) { rx += Delta(r, p[0]); tx += Delta(w, p[1]); }
                names.Add(n.Name);
            }
            prev = cur;
            rxDelta = rx; txDelta = tx;
            if (dt > 0) { rxRate = rx / dt; txRate = tx / dt; }
            ifName = names.Count > 0 ? string.Join(", ", names) : "offline";
            gateway = gw; localIp = ip; isWireless = wl;
        }
    }
}
