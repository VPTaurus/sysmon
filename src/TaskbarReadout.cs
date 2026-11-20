using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace SysMonApp
{
    // Compact two-line meter that sits on the Windows taskbar, left of the tray icons
    class TaskbarReadout : Window
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
        [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);

        static Settings S { get { return Program.S; } }
        IntPtr hwnd;
        readonly DispatcherTimer timer;
        Theme T;
        TextBlock vCpu, vRam, vGpu, vDown, vUp, vPing, vBat;
        bool dragging, pressed, hiddenForFullscreen;
        double pressX, pressLeft;

        public TaskbarReadout()
        {
            Title = "SysMon Taskbar";
            WindowStyle = WindowStyle.None; AllowsTransparency = true; ResizeMode = ResizeMode.NoResize;
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));   // nearly invisible but clickable
            Topmost = true; ShowInTaskbar = false; ShowActivated = false;
            SizeToContent = SizeToContent.WidthAndHeight; UseLayoutRounding = true;
            FontFamily = new FontFamily("Segoe UI");
            Left = -10000; Top = -10000;

            SourceInitialized += (s, e) =>
            {
                hwnd = new WindowInteropHelper(this).Handle;
                SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) | 0x80 | 0x08000000);   // TOOLWINDOW | NOACTIVATE
            };
            Loaded += (s, e) => Reposition();
            SizeChanged += (s, e) => Reposition();

            MouseLeftButtonDown += (s, e) =>
            {
                pressed = true; dragging = false;
                pressX = ScreenX(e); pressLeft = Left;
                CaptureMouse();
            };
            LostMouseCapture += (s, e) => { if (pressed && !dragging) pressed = false; };
            MouseMove += (s, e) =>
            {
                if (!pressed) return;
                if (e.LeftButton != MouseButtonState.Pressed) { pressed = false; dragging = false; ReleaseMouseCapture(); return; }
                double dx = ScreenX(e) - pressX;
                if (Math.Abs(dx) > 4) dragging = true;
                if (dragging) Left = pressLeft + dx;
            };
            MouseLeftButtonUp += (s, e) =>
            {
                if (!pressed) return;
                pressed = false; ReleaseMouseCapture();
                if (dragging)
                {
                    RECT tb;
                    if (Taskbar(out tb)) { S.TaskbarOffset = Math.Max(0, tb.R / Scale - (Left + ActualWidth)); Program.SaveSettings(); }
                    Reposition();
                }
                else Program.ShowDashboard();
            };

            Rebuild();
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            timer.Tick += (s, e) => Reposition();
            timer.Start();
            Closed += (s, e) => timer.Stop();
        }

        double ScreenX(MouseEventArgs e) { return PointToScreen(e.GetPosition(this)).X / Scale; }

        double Scale
        {
            get
            {
                var src = PresentationSource.FromVisual(this);
                return src != null && src.CompositionTarget != null ? src.CompositionTarget.TransformToDevice.M11 : 1;
            }
        }

        static bool LightTaskbar()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return k != null && Convert.ToInt32(k.GetValue("SystemUsesLightTheme", 0)) == 1;
            }
            catch { return false; }
        }

        public void Rebuild()
        {
            T = Theme.Get(LightTaskbar() ? "Light" : "Dark");
            vCpu = vRam = vGpu = vDown = vUp = vPing = vBat = null;
            var items = new List<Tuple<string, Brush, double, Action<TextBlock>>>();
            if (S.ShowCpu) items.Add(Tuple.Create<string, Brush, double, Action<TextBlock>>("CPU", T.Cpu, 32, t => vCpu = t));
            if (S.ShowRam) items.Add(Tuple.Create<string, Brush, double, Action<TextBlock>>("RAM", T.Ram, 32, t => vRam = t));
            if (S.ShowNet)
            {
                items.Add(Tuple.Create<string, Brush, double, Action<TextBlock>>(Fmt.Dn, T.Down, 62, t => vDown = t));
                items.Add(Tuple.Create<string, Brush, double, Action<TextBlock>>(Fmt.UpA, T.Up, 62, t => vUp = t));
            }
            if (S.ShowGpu) items.Add(Tuple.Create<string, Brush, double, Action<TextBlock>>("GPU", T.Gpu, 32, t => vGpu = t));
            if (S.ShowPing) items.Add(Tuple.Create<string, Brush, double, Action<TextBlock>>("PING", T.Ping, 44, t => vPing = t));
            if (S.ShowBattery) items.Add(Tuple.Create<string, Brush, double, Action<TextBlock>>("BAT", T.Good, 36, t => vBat = t));
            if (items.Count == 0) items.Add(Tuple.Create<string, Brush, double, Action<TextBlock>>("CPU", T.Cpu, 32, t => vCpu = t));

            var g = new Grid { Margin = new Thickness(6, 2, 6, 2) };
            g.RowDefinitions.Add(new RowDefinition()); g.RowDefinitions.Add(new RowDefinition());
            for (int i = 0; i < items.Count; i++)
            {
                int col = i / 2, row = i % 2;
                while (g.ColumnDefinitions.Count <= col * 2 + 1)
                {
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                }
                var it = items[i];
                var l = new TextBlock { Text = it.Item1, Foreground = T.Muted, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(col == 0 ? 0 : 10, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
                var v = new TextBlock { Text = "-", Foreground = T.Fg, FontSize = 11.5, FontWeight = FontWeights.SemiBold, MinWidth = it.Item3, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetRow(l, row); Grid.SetColumn(l, col * 2);
                Grid.SetRow(v, row); Grid.SetColumn(v, col * 2 + 1);
                g.Children.Add(l); g.Children.Add(v);
                it.Item4(v);
            }
            Content = g;
            ToolTip = "Click: open dashboard   Drag: move along the taskbar   Right-click: options";
            ContextMenu = BuildMenu();
            if (Program.LastSnap != null) Update(Program.LastSnap);
        }

        ContextMenu BuildMenu()
        {
            var cm = new ContextMenu();
            Action<string, Action> add = (h, a) => { var m = new MenuItem { Header = h }; m.Click += (s, e) => a(); cm.Items.Add(m); };
            add("Open dashboard", Program.ShowDashboard);
            var w = new MenuItem { Header = "Show floating widget", IsCheckable = true, IsChecked = S.WidgetVisible };
            w.Click += (s, e) => { S.WidgetVisible = w.IsChecked; Program.Apply(); };
            cm.Items.Add(w);
            add("Settings...", Program.ShowSettings);
            add("Reset position", () => { S.TaskbarOffset = -1; Program.SaveSettings(); Reposition(); });
            cm.Items.Add(new Separator());
            add("Remove from taskbar", () => { S.TaskbarReadout = false; Program.Apply(); });
            add("Exit SysMon", Program.Exit);
            return cm;
        }

        static void Set(TextBlock t, string s) { if (t != null && t.Text != s) t.Text = s; }

        public void Update(Snapshot s)
        {
            Set(vCpu, Fmt.Pct(s.Cpu)); if (vCpu != null) vCpu.Foreground = T.Level(s.Cpu, T.Fg, 70, 90);
            Set(vRam, Fmt.Pct(s.MemLoad)); if (vRam != null) vRam.Foreground = T.Level(s.MemLoad, T.Fg, 80, 90);
            Set(vGpu, s.HasGpu ? Fmt.Pct(s.Gpu) : "-");
            Set(vDown, Fmt.Rate(s.Rx, S.Bits));
            Set(vUp, Fmt.Rate(s.Tx, S.Bits));
            Set(vBat, s.HasBattery ? s.BatPct + "%" + (s.Charging ? "+" : "") : "-");
            if (vBat != null) vBat.Foreground = s.HasBattery && !s.OnAc && s.BatPct <= 20 ? T.Bad : T.Fg;
        }

        public void UpdatePing(double ms)
        {
            Set(vPing, double.IsNaN(ms) ? "lost" : ms < 1 ? "<1 ms" : ms.ToString("0") + " ms");
            if (vPing != null) vPing.Foreground = double.IsNaN(ms) ? T.Bad : ms > 150 ? T.Warn : T.Fg;
        }

        // ---------------------------------------------------------------- placement
        static bool Taskbar(out RECT r)
        {
            r = new RECT();
            IntPtr tb = FindWindow("Shell_TrayWnd", null);
            return tb != IntPtr.Zero && GetWindowRect(tb, out r);
        }

        bool FullscreenAppActive(IntPtr tb)
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == hwnd) return false;
            var sb = new StringBuilder(64);
            GetClassName(fg, sb, 64);
            string c = sb.ToString();
            if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd") return false;
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (!GetMonitorInfo(MonitorFromWindow(tb, 2), ref mi)) return false;
            RECT w;
            if (!GetWindowRect(fg, out w)) return false;
            return w.L <= mi.rcMonitor.L && w.T <= mi.rcMonitor.T && w.R >= mi.rcMonitor.R && w.B >= mi.rcMonitor.B;
        }

        public void Reposition()
        {
            if (hwnd == IntPtr.Zero || pressed) return;
            IntPtr tb = FindWindow("Shell_TrayWnd", null);
            RECT r;
            if (tb == IntPtr.Zero || !GetWindowRect(tb, out r)) return;

            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            GetMonitorInfo(MonitorFromWindow(tb, 2), ref mi);
            bool autoHidden = r.T >= mi.rcMonitor.B - 4 || r.B <= mi.rcMonitor.T + 4;
            bool hide = autoHidden || FullscreenAppActive(tb);
            if (hide != hiddenForFullscreen)
            {
                hiddenForFullscreen = hide;
                Visibility = hide ? Visibility.Hidden : Visibility.Visible;
            }
            if (hide) return;

            double sc = Scale, w = ActualWidth, h = ActualHeight;
            double tbL = r.L / sc, tbR = r.R / sc, tbT = r.T / sc, tbH = (r.B - r.T) / sc;
            double x;
            if (S.TaskbarOffset >= 0) x = tbR - S.TaskbarOffset - w;
            else
            {
                IntPtr tray = FindWindowEx(tb, IntPtr.Zero, "TrayNotifyWnd", null);
                RECT tr;
                if (tray != IntPtr.Zero && GetWindowRect(tray, out tr) && tr.L > r.L + 100) x = tr.L / sc - w - 8;
                else x = tbR - 340 - w;
            }
            x = Math.Max(tbL, Math.Min(tbR - w, x));
            double y = tbT + (tbH - h) / 2;
            if (Math.Abs(Left - x) > 0.5) Left = x;
            if (Math.Abs(Top - y) > 0.5) Top = y;
            SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);   // TOPMOST, NOSIZE|NOMOVE|NOACTIVATE
        }
    }
}
