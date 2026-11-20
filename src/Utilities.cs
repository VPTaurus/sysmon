using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace SysMonApp
{
    /*
      Keep awake: SetThreadExecutionState (the documented Windows way, no fake input) for a chosen time.
      Presence nudge (opt-in): inside a daily time window, after N minutes without real input, move the
      mouse 1 px and back so the system idle timer (used by Teams etc.) resets.
    */
    static class Utilities
    {
        [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
        [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
        [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO lii);
        [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public MOUSEINPUT mi; }
        [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);

        const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM = 0x1, ES_DISPLAY = 0x2;
        static Settings S { get { return Program.S; } }
        static DispatcherTimer timer;

        public static DateTime AwakeUntil = DateTime.MinValue;   // MaxValue = until turned off
        public static DateTime LastNudge = DateTime.MinValue;
        public static int NudgesToday;
        static DateTime nudgeDay = DateTime.Today;
        public static event Action Changed;

        public static bool AwakeActive { get { return AwakeUntil > DateTime.Now; } }
        public static bool AwakeForever { get { return AwakeUntil == DateTime.MaxValue; } }

        public static void Init()
        {
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            timer.Tick += (s, e) => Tick();
            timer.Start();
        }

        public static void KeepAwake(TimeSpan? duration)
        {
            AwakeUntil = duration == null ? DateTime.MaxValue : DateTime.Now + duration.Value;
            ApplyState();
        }

        public static void StopAwake() { AwakeUntil = DateTime.MinValue; ApplyState(); }

        public static void ToggleAwake()
        {
            if (AwakeActive) StopAwake(); else KeepAwake(null);
            if (Program.Tray != null)
                Program.Tray.ShowBalloonTip(2500, "SysMon", AwakeActive ? "Keep awake is on until you turn it off." : "Keep awake is off.", System.Windows.Forms.ToolTipIcon.Info);
        }

        // call after KeepAwakeScreen changes too
        public static void ApplyState()
        {
            uint f = ES_CONTINUOUS;
            if (AwakeActive) f |= ES_SYSTEM | (S.KeepAwakeScreen ? ES_DISPLAY : 0);
            SetThreadExecutionState(f);
            if (Changed != null) Changed();
        }

        static void Tick()
        {
            if (AwakeUntil != DateTime.MinValue && !AwakeForever && DateTime.Now >= AwakeUntil) StopAwake();
            if (DateTime.Today != nudgeDay) { nudgeDay = DateTime.Today; NudgesToday = 0; }
            try { Nudge(); } catch (Exception ex) { Store.Log("nudge: " + ex.Message); }
        }

        public static bool TryTime(string s, out TimeSpan t)
        {
            return TimeSpan.TryParseExact((s ?? "").Trim(), new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out t);
        }

        public static bool InNudgeWindow(DateTime now)
        {
            if (S.NudgeWeekdays && (now.DayOfWeek == DayOfWeek.Saturday || now.DayOfWeek == DayOfWeek.Sunday)) return false;
            TimeSpan from, to;
            if (!TryTime(S.NudgeFrom, out from) || !TryTime(S.NudgeTo, out to)) return false;
            var t = now.TimeOfDay;
            return from <= to ? t >= from && t < to : t >= from || t < to;   // supports windows over midnight
        }

        public static double IdleSeconds()
        {
            var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
            if (!GetLastInputInfo(ref lii)) return 0;
            return unchecked((uint)Environment.TickCount - lii.dwTime) / 1000.0;
        }

        static void Nudge()
        {
            if (!S.NudgeOn || !InNudgeWindow(DateTime.Now)) return;
            if (IdleSeconds() < Math.Max(1, S.NudgeIdleMin) * 60) return;
            var inputs = new[]
            {
                new INPUT { type = 0, mi = new MOUSEINPUT { dx = 1, dy = 0, dwFlags = 0x0001 } },    // MOUSEEVENTF_MOVE (relative)
                new INPUT { type = 0, mi = new MOUSEINPUT { dx = -1, dy = 0, dwFlags = 0x0001 } },
            };
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
            LastNudge = DateTime.Now; NudgesToday++;
            if (Changed != null) Changed();
        }

        // short status for the widget / dashboard, null when nothing is active
        public static string Badge()
        {
            if (AwakeActive)
            {
                if (AwakeForever) return "☕ awake";
                var left = AwakeUntil - DateTime.Now;
                return "☕ awake " + (left.TotalHours >= 1 ? ((int)left.TotalHours) + "h " + left.Minutes.ToString("00") + "m" : Math.Ceiling(left.TotalMinutes) + "m");
            }
            if (S.NudgeOn && InNudgeWindow(DateTime.Now)) return "● presence";
            return null;
        }

        public static string Describe()
        {
            string a = !AwakeActive ? "Keep awake is off" : AwakeForever ? "Keep awake is on until you turn it off" : "Keep awake is on until " + AwakeUntil.ToString("t");
            string n = !S.NudgeOn ? "presence nudge off"
                     : "presence nudge " + (InNudgeWindow(DateTime.Now) ? "active" : "waiting for " + S.NudgeFrom) +
                       (NudgesToday > 0 ? " (" + NudgesToday + " today, last " + LastNudge.ToString("t") + ")" : "");
            return a + "  ·  " + n;
        }
    }
}
