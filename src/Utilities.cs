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

        public static bool AwakeUntilClock;   // true when started with "until HH:mm" (vs. a duration)

        public static void KeepAwake(TimeSpan? duration)
        {
            AwakeUntilClock = false;
            AwakeUntil = duration == null ? DateTime.MaxValue : DateTime.Now + duration.Value;
            ApplyState();
        }

        // until the next occurrence of a clock time: today if still ahead, otherwise tomorrow
        public static DateTime NextTime(TimeSpan t)
        {
            var d = DateTime.Today + t;
            return d > DateTime.Now ? d : d.AddDays(1);
        }

        public static bool KeepAwakeUntil(string hhmm)
        {
            TimeSpan t;
            if (!TryTime(hhmm, out t)) return false;
            S.AwakeUntilTime = t.ToString(@"hh\:mm");
            Program.SaveSettings();
            AwakeUntilClock = true;
            AwakeUntil = NextTime(t);
            ApplyState();
            return true;
        }

        public static void StopAwake() { AwakeUntil = DateTime.MinValue; AwakeUntilClock = false; ApplyState(); }

        public static void ToggleAwake()
        {
            if (AwakeActive) StopAwake(); else KeepAwake(null);
            if (Program.Tray != null)
                Program.Notify(2500, "SysMon", AwakeActive ? "Keep awake is on until you turn it off." : "Keep awake is off.", System.Windows.Forms.ToolTipIcon.Info, null);
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

        // small "keep awake until ..." dialog (tray menu / settings)
        public static void AskUntilTime()
        {
            var T = Theme.Get(S.Theme);
            var w = new System.Windows.Window
            {
                Title = "SysMon - Keep awake until", SizeToContent = System.Windows.SizeToContent.WidthAndHeight, ResizeMode = System.Windows.ResizeMode.NoResize,
                WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen, Topmost = true,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"), FontSize = 13
            };
            var sp = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(18, 14, 18, 14), Width = 300 };
            sp.Children.Add(new System.Windows.Controls.TextBlock { Text = "Keep the PC awake until:", FontWeight = System.Windows.FontWeights.SemiBold });
            var box = new System.Windows.Controls.TextBox { Text = S.AwakeUntilTime, FontSize = 18, Width = 90, Margin = new System.Windows.Thickness(0, 8, 0, 4),
                                                            HorizontalAlignment = System.Windows.HorizontalAlignment.Left, HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center };
            sp.Children.Add(box);
            var hint = new System.Windows.Controls.TextBlock { Foreground = System.Windows.Media.Brushes.Gray, FontSize = 11.5, TextWrapping = System.Windows.TextWrapping.Wrap };
            System.Action update = () =>
            {
                TimeSpan t;
                if (!TryTime(box.Text, out t)) { hint.Text = "24-hour time, for example 17:30"; return; }
                var at = NextTime(t);
                hint.Text = (at.Date > DateTime.Today ? "Tomorrow at " : "Today at ") + at.ToString("t") + " (in " + Fmt.Duration((at - DateTime.Now).TotalSeconds + 59) + ")";
            };
            box.TextChanged += (s, e) => update();
            update();
            sp.Children.Add(hint);
            var buttons = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new System.Windows.Thickness(0, 12, 0, 0) };
            var ok = new System.Windows.Controls.Button { Content = "Keep awake", IsDefault = true, Padding = new System.Windows.Thickness(12, 3, 12, 3) };
            var cancel = new System.Windows.Controls.Button { Content = "Cancel", IsCancel = true, Padding = new System.Windows.Thickness(12, 3, 12, 3), Margin = new System.Windows.Thickness(6, 0, 0, 0) };
            ok.Click += (s, e) => { if (KeepAwakeUntil(box.Text)) w.Close(); else box.Focus(); };
            buttons.Children.Add(ok); buttons.Children.Add(cancel);
            sp.Children.Add(buttons);
            w.Content = sp;
            w.Loaded += (s, e) => { box.Focus(); box.SelectAll(); };
            w.Show();
            w.Activate();
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
