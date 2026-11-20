using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SysMonApp
{
    static class Native
    {
        [DllImport("kernel32.dll")] public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
        [DllImport("kernel32.dll")] public static extern ulong GetTickCount64();

        [StructLayout(LayoutKind.Sequential)]
        public class MEMORYSTATUSEX
        {
            public uint dwLength = 64; public uint dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                         ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }
        [DllImport("kernel32.dll")] public static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX m);

        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
            public int BatteryLifeTime, BatteryFullLifeTime;
        }
        [DllImport("kernel32.dll")] public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);

        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);

        public static void SetExStyle(IntPtr h, bool clickThrough)
        {
            if (h == IntPtr.Zero) return;
            int s = GetWindowLong(h, -20);
            s |= 0x80;                               // WS_EX_TOOLWINDOW: keep out of Alt+Tab
            if (clickThrough) s |= 0x20; else s &= ~0x20;   // WS_EX_TRANSPARENT
            SetWindowLong(h, -20, s);
        }
    }

    // ---------------- PDH (performance counters by English name: locale independent) ----------------
    [StructLayout(LayoutKind.Sequential)] struct PdhFmtValue { public uint CStatus; public double Value; }
    [StructLayout(LayoutKind.Sequential)] struct PdhFmtItem { public IntPtr Name; public PdhFmtValue V; }

    class PdhQuery : IDisposable
    {
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhOpenQuery(string src, IntPtr user, out IntPtr q);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhAddEnglishCounter(IntPtr q, string path, IntPtr user, out IntPtr c);
        [DllImport("pdh.dll")] static extern uint PdhCollectQueryData(IntPtr q);
        [DllImport("pdh.dll")] static extern uint PdhCloseQuery(IntPtr q);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhGetFormattedCounterArray(IntPtr c, uint fmt, ref uint size, out uint count, IntPtr buf);

        const uint FMT = 0x200 | 0x8000;   // PDH_FMT_DOUBLE | PDH_FMT_NOCAP100
        const uint MORE_DATA = 0x800007D2;
        IntPtr q;

        public PdhQuery() { if (PdhOpenQuery(null, IntPtr.Zero, out q) != 0) q = IntPtr.Zero; }

        public IntPtr Add(string path)
        {
            if (q == IntPtr.Zero) return IntPtr.Zero;
            IntPtr c;
            return PdhAddEnglishCounter(q, path, IntPtr.Zero, out c) == 0 ? c : IntPtr.Zero;
        }

        public void Collect() { if (q != IntPtr.Zero) PdhCollectQueryData(q); }

        public List<KeyValuePair<string, double>> Read(IntPtr c)
        {
            var list = new List<KeyValuePair<string, double>>();
            if (c == IntPtr.Zero) return list;
            uint size = 0, count;
            if (PdhGetFormattedCounterArray(c, FMT, ref size, out count, IntPtr.Zero) != MORE_DATA || size == 0) return list;
            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArray(c, FMT, ref size, out count, buf) != 0) return list;
                int stride = Marshal.SizeOf(typeof(PdhFmtItem));
                for (int i = 0; i < count; i++)
                {
                    var it = (PdhFmtItem)Marshal.PtrToStructure(IntPtr.Add(buf, i * stride), typeof(PdhFmtItem));
                    if (it.V.CStatus > 1) continue;   // only VALID_DATA / NEW_DATA
                    list.Add(new KeyValuePair<string, double>(Marshal.PtrToStringUni(it.Name), it.V.Value));
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return list;
        }

        public void Dispose() { if (q != IntPtr.Zero) { PdhCloseQuery(q); q = IntPtr.Zero; } }
    }

    // ---------------- Wi-Fi (Native WLAN API, no admin) ----------------
    class WifiInfo { public string Ssid; public int Signal = -1; public int RxMbps; public bool Connected; }

    static class Wlan
    {
        [DllImport("wlanapi.dll")] static extern uint WlanOpenHandle(uint ver, IntPtr res, out uint neg, out IntPtr h);
        [DllImport("wlanapi.dll")] static extern uint WlanEnumInterfaces(IntPtr h, IntPtr res, out IntPtr list);
        [DllImport("wlanapi.dll")] static extern uint WlanQueryInterface(IntPtr h, ref Guid g, int op, IntPtr res, out uint size, out IntPtr data, IntPtr opType);
        [DllImport("wlanapi.dll")] static extern void WlanFreeMemory(IntPtr p);

        static IntPtr handle = IntPtr.Zero;
        static bool unavailable;

        public static WifiInfo Query()
        {
            if (unavailable) return null;
            try
            {
                if (handle == IntPtr.Zero)
                {
                    uint neg;
                    if (WlanOpenHandle(2, IntPtr.Zero, out neg, out handle) != 0) { unavailable = true; return null; }
                }
                IntPtr list;
                if (WlanEnumInterfaces(handle, IntPtr.Zero, out list) != 0) return null;
                try
                {
                    int n = Marshal.ReadInt32(list, 0);
                    if (n == 0) return null;
                    for (int i = 0; i < n; i++)
                    {
                        IntPtr item = IntPtr.Add(list, 8 + i * 532);
                        int state = Marshal.ReadInt32(item, 528);
                        if (state != 1) continue;     // wlan_interface_state_connected
                        var info = new WifiInfo { Connected = true };
                        Guid g = (Guid)Marshal.PtrToStructure(item, typeof(Guid));
                        uint size; IntPtr data;
                        if (WlanQueryInterface(handle, ref g, 7, IntPtr.Zero, out size, out data, IntPtr.Zero) == 0)
                        {
                            try
                            {
                                int len = Marshal.ReadInt32(data, 520);
                                if (len > 0 && len <= 32)
                                {
                                    var b = new byte[len];
                                    Marshal.Copy(IntPtr.Add(data, 524), b, 0, len);
                                    info.Ssid = System.Text.Encoding.UTF8.GetString(b);
                                }
                                if (string.IsNullOrEmpty(info.Ssid)) info.Ssid = Marshal.PtrToStringUni(IntPtr.Add(data, 8));
                                info.Signal = Marshal.ReadInt32(data, 576);
                                info.RxMbps = Marshal.ReadInt32(data, 580) / 1000;
                            }
                            finally { WlanFreeMemory(data); }
                        }
                        return info;
                    }
                    return new WifiInfo { Connected = false };
                }
                finally { WlanFreeMemory(list); }
            }
            catch { unavailable = true; return null; }
        }
    }
}
