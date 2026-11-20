using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace SysMonApp
{
    class Widget : Window
    {
        static Settings S { get { return Program.S; } }
        Theme T;
        public IntPtr Hwnd = IntPtr.Zero;
        const double CW = 246, M = 8, SNAP = 20;

        public Ring hCpu, hRam, hGpu, hRead, hWrite, hRx, hTx, hPing;
        Snapshot last;
        long sessRx, sessTx;
        string pingTarget = "";
        int tickCount;
        public string PublicIp = "";

        // UI references (rebuilt on every settings change)
        TextBlock tUptime, tCpu, tRam, tGpu, tDisk, tDown, tUp, tIf, tPingLbl, tPing, tWifi, tIps, tToday, tMonth, tSession, tBat;
        TextBlock mCpu, mRam, mGpu, mDown, mUp, mPing, mBat;
        TextBlock tClock, tDate, mClock;
        readonly List<Spark> sparks = new List<Spark>();
        Spark sPing;
        CoreBars cores;
        MiniBar batBar;
        StackPanel diskRows, driveRows;
        TextBlock[] pcName, pcVal, pmName, pmVal;

        public Widget()
        {
            Title = "SysMon Widget";
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
            Topmost = true; ShowInTaskbar = false; ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight; UseLayoutRounding = true;
            FontFamily = new FontFamily("Segoe UI"); FontSize = 12;

            int pts = Points();
            hCpu = new Ring(pts); hRam = new Ring(pts); hGpu = new Ring(pts); hRead = new Ring(pts);
            hWrite = new Ring(pts); hRx = new Ring(pts); hTx = new Ring(pts); hPing = new Ring(PingPoints());

            SourceInitialized += (s, e) =>
            {
                Hwnd = new WindowInteropHelper(this).Handle;
                Native.SetExStyle(Hwnd, S.ClickThrough);
            };
            Loaded += (s, e) =>
            {
                if (S.HasPos && OnScreen(S.Left, S.Top)) { Left = S.Left; Top = S.Top; }
                else MoveToCorner();
            };
            MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2) { Program.ShowDashboard(); return; }
                if (S.Locked) return;
                try { DragMove(); } catch { }
                if (S.Snap) SnapToEdges();
                ClampToScreen();
                SavePos();
            };
            SizeChanged += (s, e) =>
            {
                if (Hwnd == IntPtr.Zero || e.PreviousSize.Width == 0) return;
                var wa = WorkAreaDip();
                // keep right / bottom anchored when the widget changes size
                if (Math.Abs(Left + e.PreviousSize.Width - (wa.Right - M)) < 3) Left = wa.Right - M - e.NewSize.Width;
                if (Math.Abs(Top + e.PreviousSize.Height - (wa.Bottom - M)) < 3) Top = wa.Bottom - M - e.NewSize.Height;
                if (Top + e.NewSize.Height > wa.Bottom) Top = Math.Max(wa.Top, wa.Bottom - e.NewSize.Height);
            };

            Left = -10000; Top = -10000;   // real position set on Loaded
            Rebuild();
        }

        int Points() { return Math.Max(10, S.GraphSeconds * 1000 / Math.Max(250, S.IntervalMs)); }
        int PingPoints() { return Math.Max(10, S.GraphSeconds / 2); }

        public void ApplySettings()
        {
            int pts = Points();
            foreach (var r in new[] { hCpu, hRam, hGpu, hRead, hWrite, hRx, hTx }) r.Resize(pts);
            hPing.Resize(PingPoints());
            Native.SetExStyle(Hwnd, S.ClickThrough);
            Rebuild();
        }

        public void ResetSession() { sessRx = 0; sessTx = 0; Render(last); }

        // ---------------------------------------------------------------- positioning
        Rect WorkAreaDip()
        {
            var scr = Hwnd != IntPtr.Zero ? System.Windows.Forms.Screen.FromHandle(Hwnd).WorkingArea
                                          : System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
            double sx = 1, sy = 1;
            var src = PresentationSource.FromVisual(this);
            if (src != null && src.CompositionTarget != null)
            {
                sx = src.CompositionTarget.TransformToDevice.M11;
                sy = src.CompositionTarget.TransformToDevice.M22;
            }
            return new Rect(scr.Left / sx, scr.Top / sy, scr.Width / sx, scr.Height / sy);
        }

        static bool OnScreen(double l, double t)
        {
            double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;
            return l >= vl - 50 && t >= vt - 10 && l <= vl + SystemParameters.VirtualScreenWidth - 60 && t <= vt + SystemParameters.VirtualScreenHeight - 30;
        }

        public void MoveToCorner()
        {
            var wa = WorkAreaDip();
            Left = wa.Right - M - ActualWidth; Top = wa.Top + M;
            SavePos();
        }

        void SnapToEdges()
        {
            var wa = WorkAreaDip();
            double w = ActualWidth, h = ActualHeight;
            if (Math.Abs(Left - (wa.Left + M)) < SNAP) Left = wa.Left + M;
            else if (Math.Abs(Left + w - (wa.Right - M)) < SNAP) Left = wa.Right - M - w;
            if (Math.Abs(Top - (wa.Top + M)) < SNAP) Top = wa.Top + M;
            else if (Math.Abs(Top + h - (wa.Bottom - M)) < SNAP) Top = wa.Bottom - M - h;
        }

        // keep the top edge and most of the widget on the screen it was dropped on
        void ClampToScreen()
        {
            var wa = WorkAreaDip();
            double w = ActualWidth, h = ActualHeight;
            Left = Math.Max(wa.Left - w + 80, Math.Min(wa.Right - 80, Left));
            Top = Math.Max(wa.Top, Math.Min(wa.Bottom - Math.Min(h, 60), Top));
        }

        void SavePos() { S.HasPos = true; S.Left = Left; S.Top = Top; Program.SaveSettings(); }

        // ---------------------------------------------------------------- UI building
        TextBlock Txt(string s, Brush b, double size = 12, bool bold = false)
        {
            return new TextBlock { Text = s, Foreground = b, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
        }

        StackPanel Section(Panel parent)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 9) };
            parent.Children.Add(sp);
            return sp;
        }

        TextBlock Row(Panel p, string label, Brush valBrush, out TextBlock lbl)
        {
            var g = new Grid();
            lbl = Txt(label, T.Muted, 11, true);
            lbl.VerticalAlignment = VerticalAlignment.Center;
            var v = Txt("", valBrush, 12, true);
            v.HorizontalAlignment = HorizontalAlignment.Right;
            g.Children.Add(lbl); g.Children.Add(v);
            p.Children.Add(g);
            return v;
        }
        TextBlock Row(Panel p, string label, Brush valBrush) { TextBlock l; return Row(p, label, valBrush, out l); }

        TextBlock Line(Panel p, Brush b, double size = 10.5)
        {
            var t = Txt("", b, size);
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            t.Margin = new Thickness(0, 1, 0, 0);
            p.Children.Add(t);
            return t;
        }

        Spark AddSpark(Panel p, double h, double fixedMax, double minMax, Func<double, string> fmt, bool scale, params Series[] ser)
        {
            var s = new Spark
            {
                Height = h, Margin = new Thickness(0, 4, 0, 0), FixedMax = fixedMax, MinMax = minMax, Format = fmt,
                Track = T.Track, Text = T.Fg, HoverBg = T.HoverBg, ShowScale = scale,
                SecondsPerPoint = () => S.IntervalMs / 1000.0
            };
            s.Series.AddRange(ser);
            p.Children.Add(s);
            sparks.Add(s);
            return s;
        }

        Func<double, string> RateFmt { get { return v => Fmt.Rate(v, S.Bits); } }

        public void Rebuild()
        {
            T = Theme.Get(S.Theme);
            sparks.Clear(); sPing = null; cores = null; batBar = null; diskRows = driveRows = null; pcName = null;
            tUptime = tCpu = tRam = tGpu = tDisk = tDown = tUp = tIf = tPingLbl = tPing = tWifi = tIps = tToday = tMonth = tSession = tBat = null;
            mCpu = mRam = mGpu = mDown = mUp = mPing = mBat = null;
            tClock = tDate = mClock = null;

            bool mini = S.Mode == "Mini";
            var root = new Border
            {
                CornerRadius = new CornerRadius(mini ? 8 : 10), Background = T.Bg, BorderBrush = T.Border, BorderThickness = new Thickness(1),
                Padding = mini ? new Thickness(10, 5, 10, 5) : new Thickness(12, 9, 12, 2),
                Child = mini ? BuildMini() : BuildFull(S.Mode == "Compact")
            };
            root.ContextMenu = BuildMenu();
            Content = root;
            Opacity = S.Opacity;
            RefreshDrives();
            Render(last);
            RenderPing();
        }

        UIElement BuildFull(bool compact)
        {
            var p = new StackPanel { Width = CW };

            var hdr = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            if (S.ShowClock)
            {
                var cl = new StackPanel { Orientation = Orientation.Horizontal, ToolTip = Environment.MachineName };
                tClock = Txt("", T.Fg, 15, true);
                cl.Children.Add(tClock);
                if (S.ClockDate)
                {
                    tDate = Txt("", T.Muted, 11);
                    tDate.Margin = new Thickness(7, 0, 0, 2); tDate.VerticalAlignment = VerticalAlignment.Bottom;
                    cl.Children.Add(tDate);
                }
                hdr.Children.Add(cl);
            }
            else hdr.Children.Add(Txt(Environment.MachineName, T.Muted, 11, true));
            tUptime = Txt("", T.Faint, 11);
            tUptime.HorizontalAlignment = HorizontalAlignment.Right;
            tUptime.VerticalAlignment = VerticalAlignment.Center;
            hdr.Children.Add(tUptime);
            p.Children.Add(hdr);

            Func<double, string> pct = Fmt.Pct;

            if (S.ShowCpu)
            {
                var sec = Section(p);
                tCpu = Row(sec, "CPU", T.Cpu);
                if (!compact) AddSpark(sec, 26, 100, 1, pct, false, new Series(hCpu, T.Cpu, null));
                if (S.ShowCores)
                {
                    cores = new CoreBars { Height = compact ? 10 : 16, Margin = new Thickness(0, 4, 0, 0), Bar = Theme.Fill(T.Cpu, 0xCC),
                                           Track = T.Track, Text = T.Fg, HoverBg = T.HoverBg, Warn = T.Warn, Bad = T.Bad };
                    sec.Children.Add(cores);
                }
            }
            if (S.ShowRam)
            {
                var sec = Section(p);
                tRam = Row(sec, "MEMORY", T.Ram);
                if (!compact) AddSpark(sec, 26, 100, 1, pct, false, new Series(hRam, T.Ram, null));
            }
            if (S.ShowGpu)
            {
                var sec = Section(p);
                tGpu = Row(sec, "GPU", T.Gpu);
                if (!compact) AddSpark(sec, 26, 100, 1, pct, false, new Series(hGpu, T.Gpu, null));
            }
            if (S.ShowDiskIo)
            {
                var sec = Section(p);
                tDisk = Row(sec, "DISK I/O", T.Fg);
                if (!compact) AddSpark(sec, 26, double.NaN, 512 * 1024, RateFmt, true,
                                       new Series(hRead, T.Read, "R"), new Series(hWrite, T.Write, "W"));
                diskRows = new StackPanel();
                sec.Children.Add(diskRows);
            }
            if (S.ShowStorage)
            {
                var sec = Section(p);
                sec.Children.Add(Txt("STORAGE", T.Muted, 11, true));
                driveRows = new StackPanel();
                sec.Children.Add(driveRows);
            }
            if (S.ShowNet)
            {
                var sec = Section(p);
                var g = new Grid();
                g.Children.Add(new TextBlock { Text = "NETWORK", Foreground = T.Muted, FontSize = 11, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
                var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
                tDown = Txt("", T.Down, 12, true); tDown.Margin = new Thickness(0, 0, 10, 0);
                tUp = Txt("", T.Up, 12, true);
                sp.Children.Add(tDown); sp.Children.Add(tUp);
                g.Children.Add(sp);
                sec.Children.Add(g);
                if (!compact) AddSpark(sec, 34, double.NaN, 8 * 1024, RateFmt, true,
                                       new Series(hRx, T.Down, Fmt.Dn), new Series(hTx, T.Up, Fmt.UpA));
                tIf = Line(sec, T.Faint);
            }
            if (S.ShowPing)
            {
                var sec = Section(p);
                tPing = Row(sec, "PING", T.Ping, out tPingLbl);
                if (!compact)
                {
                    sPing = AddSpark(sec, 20, double.NaN, 20, v => v.ToString("0") + " ms", false, new Series(hPing, T.Ping, null));
                    sPing.NaNMark = T.Bad;
                    sPing.SecondsPerPoint = () => 2;
                }
            }
            if (S.ShowConn)
            {
                var sec = Section(p);
                sec.Children.Add(Txt("CONNECTION", T.Muted, 11, true));
                tWifi = Line(sec, T.Fg, 11.5);
                tIps = Line(sec, T.Faint);
            }
            if (S.ShowUsage)
            {
                var sec = Section(p);
                sec.Children.Add(Txt("DATA USAGE", T.Muted, 11, true));
                tToday = UsageLine(sec, "Today");
                tMonth = UsageLine(sec, "This month");
                tSession = UsageLine(sec, "Since start");
            }
            if (S.ShowProcs)
            {
                var sec = Section(p);
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                pcName = new TextBlock[3]; pcVal = new TextBlock[3]; pmName = new TextBlock[3]; pmVal = new TextBlock[3];
                g.Children.Add(ProcColumn("TOP CPU", pcName, pcVal, T.Cpu, 0));
                g.Children.Add(ProcColumn("TOP MEMORY", pmName, pmVal, T.Ram, 2));
                sec.Children.Add(g);
            }
            if (S.ShowBattery)
            {
                var sec = Section(p);
                tBat = Row(sec, "BATTERY", T.Fg);
                batBar = new MiniBar { Height = 4, Margin = new Thickness(0, 4, 0, 0), Track = T.Track, Fill = T.Good };
                sec.Children.Add(batBar);
                sec.Visibility = Visibility.Collapsed;   // shown once a battery is detected
                sec.Tag = "battery";
            }
            return p;
        }

        TextBlock UsageLine(Panel p, string label)
        {
            var g = new Grid { Margin = new Thickness(0, 1, 0, 0) };
            g.Children.Add(Txt(label, T.Faint, 11));
            var v = Txt("", T.Fg, 11);
            v.HorizontalAlignment = HorizontalAlignment.Right;
            g.Children.Add(v);
            p.Children.Add(g);
            return v;
        }

        UIElement ProcColumn(string title, TextBlock[] names, TextBlock[] vals, Brush accent, int col)
        {
            var sp = new StackPanel();
            Grid.SetColumn(sp, col);
            sp.Children.Add(Txt(title, T.Muted, 11, true));
            for (int i = 0; i < 3; i++)
            {
                var g = new Grid { Margin = new Thickness(0, 1, 0, 0) };
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                names[i] = Txt("", T.Fg, 11); names[i].TextTrimming = TextTrimming.CharacterEllipsis;
                vals[i] = Txt("", accent, 11, true); vals[i].Margin = new Thickness(4, 0, 0, 0);
                Grid.SetColumn(vals[i], 1);
                g.Children.Add(names[i]); g.Children.Add(vals[i]);
                sp.Children.Add(g);
            }
            return sp;
        }

        UIElement BuildMini()
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            Func<string, Brush, TextBlock> item = (label, b) =>
            {
                var l = Txt(label, T.Muted, 11, true); l.Margin = new Thickness(sp.Children.Count == 0 ? 0 : 12, 0, 4, 0);
                l.VerticalAlignment = VerticalAlignment.Center;
                var v = Txt("", b, 12, true);
                sp.Children.Add(l); sp.Children.Add(v);
                return v;
            };
            if (S.ShowClock) { mClock = Txt("", T.Fg, 12, true); sp.Children.Add(mClock); }
            if (S.ShowCpu) mCpu = item("CPU", T.Cpu);
            if (S.ShowRam) mRam = item("RAM", T.Ram);
            if (S.ShowGpu) mGpu = item("GPU", T.Gpu);
            if (S.ShowNet)
            {
                mDown = item(Fmt.Dn, T.Down);
                mUp = item(Fmt.UpA, T.Up);
                ((TextBlock)sp.Children[sp.Children.Count - 2]).Margin = new Thickness(6, 0, 3, 0);
            }
            if (S.ShowPing) mPing = item("PING", T.Ping);
            if (S.ShowBattery) mBat = item("BAT", T.Fg);
            if (sp.Children.Count == 0) mCpu = item("CPU", T.Cpu);
            return sp;
        }

        ContextMenu BuildMenu()
        {
            var cm = new ContextMenu();
            Func<string, Action, MenuItem> mi = (h, a) => { var m = new MenuItem { Header = h }; m.Click += (s, e) => a(); return m; };
            Func<string, bool, Action<bool>, MenuItem> chk = (h, v, a) =>
            {
                var m = new MenuItem { Header = h, IsCheckable = true, IsChecked = v };
                m.Click += (s, e) => { a(m.IsChecked); Program.Apply(); };
                return m;
            };

            cm.Items.Add(mi("Open dashboard", Program.ShowDashboard));
            cm.Items.Add(mi("Settings...", Program.ShowSettings));
            cm.Items.Add(chk("Show on taskbar", S.TaskbarReadout, b => S.TaskbarReadout = b));
            var mode = new MenuItem { Header = "Layout" };
            foreach (var md in new[] { "Full", "Compact", "Mini" })
            {
                string m2 = md;
                mode.Items.Add(chk(md == "Mini" ? "Mini (one line)" : md == "Compact" ? "Compact (no graphs)" : "Full", S.Mode == md, b => S.Mode = m2));
            }
            cm.Items.Add(mode);
            var th = new MenuItem { Header = "Theme" };
            foreach (var n in Theme.Names) { string n2 = n; th.Items.Add(chk(n, S.Theme == n, b => S.Theme = n2)); }
            cm.Items.Add(th);
            cm.Items.Add(new Separator());
            cm.Items.Add(chk("Lock position", S.Locked, b => S.Locked = b));
            cm.Items.Add(chk("Click-through (undo from tray icon)", S.ClickThrough, b => S.ClickThrough = b));
            cm.Items.Add(chk("Start with Windows", Program.IsStartup(), Program.SetStartup));
            cm.Items.Add(chk("Keep PC awake (" + S.HotkeyAwake + ")", Utilities.AwakeActive, b => { if (b) Utilities.KeepAwake(null); else Utilities.StopAwake(); }));
            cm.Items.Add(new Separator());
            cm.Items.Add(mi("Reset session totals", ResetSession));
            cm.Items.Add(mi("Open Task Manager", () => { try { System.Diagnostics.Process.Start("taskmgr.exe"); } catch { } }));
            cm.Items.Add(new Separator());
            cm.Items.Add(mi("Hide widget", () => { S.WidgetVisible = false; Program.Apply(); }));
            cm.Items.Add(mi("Exit", Program.Exit));
            return cm;
        }

        // ---------------------------------------------------------------- data updates
        public void OnSample(Snapshot s)
        {
            if (s.TopCpu == null && last != null) { s.TopCpu = last.TopCpu; s.TopMem = last.TopMem; }
            hCpu.Add(s.Cpu); hRam.Add(s.MemLoad); hGpu.Add(s.Gpu);
            hRead.Add(s.DiskRead); hWrite.Add(s.DiskWrite); hRx.Add(s.Rx); hTx.Add(s.Tx);
            sessRx += s.RxBytes; sessTx += s.TxBytes;
            Program.Usage.Add(s.RxBytes, s.TxBytes);
            last = s;
            if (++tickCount % Math.Max(1, 15000 / S.IntervalMs) == 0) RefreshDrives();
            if (IsVisible) Render(s);
            Program.Alerts.Check(s);
            Program.UpdateTray(s, PingText(false));
        }

        public void OnPing(double ms, string target)
        {
            hPing.Add(ms);
            pingTarget = target;
            RenderPing();
            Program.Alerts.CheckPing(hPing, target);
        }

        double PingLoss()
        {
            int n = Math.Min(hPing.Count, 30), lost = 0;
            for (int i = hPing.Count - n; i < hPing.Count; i++) if (double.IsNaN(hPing[i])) lost++;
            return n == 0 ? 0 : lost * 100.0 / n;
        }

        string PingText(bool withLoss)
        {
            if (hPing.Count == 0) return S.ShowPing ? "..." : "";
            double v = hPing.Last;
            string t = double.IsNaN(v) ? "timeout" : v < 1 ? "<1 ms" : v.ToString("0") + " ms";
            if (withLoss) t += Fmt.Dot + Fmt.Pct(PingLoss()) + " loss";
            return t;
        }

        void RenderPing()
        {
            Set(tPing, PingText(true));
            if (tPing != null)
            {
                double v = hPing.Last;
                tPing.Foreground = double.IsNaN(v) && hPing.Count > 0 ? T.Bad : v > 150 ? T.Warn : T.Ping;
            }
            Set(tPingLbl, "PING  " + pingTarget);
            Set(mPing, PingText(false));
            if (sPing != null) sPing.InvalidateVisual();
        }

        static void Set(TextBlock t, string s) { if (t != null && t.Text != s) t.Text = s; }

        public void RefreshMenu()
        {
            var b = Content as Border;
            if (b != null) b.ContextMenu = BuildMenu();
        }

        // clock + uptime / keep-awake badge (called twice a second and on utility changes)
        public void RefreshHeader()
        {
            if (!IsVisible) return;
            var now = DateTime.Now;
            string time = now.ToString(S.ClockSeconds ? "T" : "t");
            Set(tClock, time); Set(mClock, time);
            Set(tDate, now.ToString("ddd d MMM"));
            if (tUptime != null)
            {
                string badge = Utilities.Badge();
                Set(tUptime, badge ?? "up " + Fmt.Duration(Native.GetTickCount64() / 1000.0));
                tUptime.Foreground = badge != null ? T.Up : T.Faint;
                tUptime.FontWeight = badge != null ? FontWeights.SemiBold : FontWeights.Normal;
                tUptime.ToolTip = Utilities.Describe();
            }
        }

        void Render(Snapshot s)
        {
            RefreshHeader();
            foreach (var sp in sparks) sp.InvalidateVisual();
            if (s == null) return;

            var cpuB = T.Level(s.Cpu, T.Cpu, 70, 90);
            Set(tCpu, Fmt.Pct(s.Cpu)); if (tCpu != null) tCpu.Foreground = cpuB;
            Set(mCpu, Fmt.Pct(s.Cpu)); if (mCpu != null) mCpu.Foreground = cpuB;
            if (cores != null) { cores.Values = s.Cores; cores.InvalidateVisual(); }

            var ramB = T.Level(s.MemLoad, T.Ram, 80, 90);
            Set(tRam, string.Format("{0:0.0} / {1:0.0} GB   {2}", s.MemUsedGB, s.MemTotalGB, Fmt.Pct(s.MemLoad)));
            if (tRam != null) tRam.Foreground = ramB;
            Set(mRam, Fmt.Pct(s.MemLoad)); if (mRam != null) mRam.Foreground = ramB;

            if (s.HasGpu)
            {
                string g = Fmt.Pct(s.Gpu) + Fmt.Dot + string.Format("{0:0.0} GB", s.GpuDedGB + s.GpuSharedGB);
                if (!double.IsNaN(s.GpuTemp)) g += Fmt.Dot + s.GpuTemp.ToString("0") + "°C";
                Set(tGpu, g);
                Set(mGpu, Fmt.Pct(s.Gpu));
            }
            else { Set(tGpu, "n/a"); Set(mGpu, "n/a"); }

            Set(tDisk, "R " + Fmt.Rate(s.DiskRead, false) + "   W " + Fmt.Rate(s.DiskWrite, false));
            if (diskRows != null)
            {
                while (diskRows.Children.Count < s.Disks.Count) diskRows.Children.Add(NewDiskLine());
                while (diskRows.Children.Count > s.Disks.Count) diskRows.Children.RemoveAt(diskRows.Children.Count - 1);
                for (int i = 0; i < s.Disks.Count; i++)
                {
                    var d = s.Disks[i];
                    var g = (Grid)diskRows.Children[i];
                    ((TextBlock)g.Children[0]).Text = "Disk " + d.Name;
                    var v = (TextBlock)g.Children[1];
                    v.Text = "R " + Fmt.Rate(d.Read, false) + "  W " + Fmt.Rate(d.Write, false) + Fmt.Dot + Fmt.Pct(d.Active) + " busy";
                    v.Foreground = T.Level(d.Active, T.Faint, 60, 90);
                }
            }

            Set(tDown, Fmt.Dn + " " + Fmt.Rate(s.Rx, S.Bits));
            Set(tUp, Fmt.UpA + " " + Fmt.Rate(s.Tx, S.Bits));
            Set(mDown, Fmt.Rate(s.Rx, S.Bits));
            Set(mUp, Fmt.Rate(s.Tx, S.Bits));
            Set(tIf, s.IfName + (s.LocalIp != "" ? Fmt.Dot + s.LocalIp : ""));
            if (tIf != null) tIf.ToolTip = tIf.Text;

            if (tWifi != null)
            {
                string w;
                if (s.IfName == "offline") w = "Offline";
                else if (s.IsWireless && s.Wifi != null && s.Wifi.Connected)
                {
                    w = "Wi-Fi  " + (string.IsNullOrEmpty(s.Wifi.Ssid) ? "(name hidden by Windows location privacy)" : s.Wifi.Ssid);
                    if (s.Wifi.Signal >= 0) w += Fmt.Dot + s.Wifi.Signal + "% signal";
                    if (s.Wifi.RxMbps > 0) w += Fmt.Dot + s.Wifi.RxMbps + " Mbps";
                }
                else w = (s.IsWireless ? "Wi-Fi  " : "Wired  ") + s.IfName;
                Set(tWifi, w);
                tWifi.ToolTip = w;
                Set(tIps, "Local " + (s.LocalIp == "" ? "-" : s.LocalIp) + "     Public " + (!S.PublicIp ? "off" : PublicIp == "" ? "..." : PublicIp));
            }

            var u = Program.Usage;
            Set(tToday, Fmt.Dn + " " + Fmt.Size(u.DayRx) + "   " + Fmt.UpA + " " + Fmt.Size(u.DayTx));
            Set(tMonth, Fmt.Dn + " " + Fmt.Size(u.MonthRx) + "   " + Fmt.UpA + " " + Fmt.Size(u.MonthTx));
            Set(tSession, Fmt.Dn + " " + Fmt.Size(sessRx) + "   " + Fmt.UpA + " " + Fmt.Size(sessTx));

            if (pcName != null && s.TopCpu != null)
            {
                for (int i = 0; i < 3; i++)
                {
                    bool has = i < s.TopCpu.Count;
                    pcName[i].Text = has ? s.TopCpu[i].Name : ""; pcVal[i].Text = has ? s.TopCpu[i].Value.ToString("0.0") + "%" : "";
                    pcName[i].ToolTip = pcName[i].Text;
                    has = i < s.TopMem.Count;
                    pmName[i].Text = has ? s.TopMem[i].Name : ""; pmVal[i].Text = has ? Fmt.Size(s.TopMem[i].Value) : "";
                    pmName[i].ToolTip = pmName[i].Text;
                }
            }

            if (s.HasBattery)
            {
                string b = s.BatPct + "%";
                if (s.Charging) b += Fmt.Dot + "charging";
                else if (s.OnAc) b += Fmt.Dot + "plugged in";
                else if (s.BatSecs > 0) b += Fmt.Dot + Fmt.Duration(s.BatSecs) + " left";
                Set(tBat, b);
                var bb = s.BatPct <= 15 && !s.OnAc ? T.Bad : s.BatPct <= 30 && !s.OnAc ? T.Warn : T.Good;
                if (batBar != null)
                {
                    batBar.Value = s.BatPct / 100.0; batBar.Fill = bb; batBar.InvalidateVisual();
                    ((FrameworkElement)batBar.Parent).Visibility = Visibility.Visible;
                }
                Set(mBat, s.BatPct + "%" + (s.Charging ? "+" : ""));
                if (mBat != null) mBat.Foreground = bb == T.Good ? T.Fg : bb;
            }
            else if (mBat != null) { mBat.Visibility = Visibility.Collapsed; }
        }

        Grid NewDiskLine()
        {
            var g = new Grid { Margin = new Thickness(0, 1, 0, 0) };
            g.Children.Add(Txt("", T.Faint, 10.5));
            var v = Txt("", T.Faint, 10.5);
            v.HorizontalAlignment = HorizontalAlignment.Right;
            g.Children.Add(v);
            return g;
        }

        public void RefreshDrives()
        {
            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).ToArray(); }
            catch { return; }
            if (driveRows != null) driveRows.Children.Clear();
            foreach (var d in drives)
            {
                double tot, free;
                try { tot = d.TotalSize; free = d.AvailableFreeSpace; } catch { continue; }
                if (tot <= 0) continue;
                double usedF = (tot - free) / tot;
                Program.Alerts.CheckDisk(d.Name, free / tot * 100);
                if (driveRows == null) continue;
                var g = new Grid { Margin = new Thickness(0, 3, 0, 0) };
                g.Children.Add(Txt(d.Name.TrimEnd('\\'), T.Fg, 11.5, true));
                var v = Txt(Fmt.Size(free) + " free of " + Fmt.Size(tot), T.Faint, 11);
                v.HorizontalAlignment = HorizontalAlignment.Right;
                g.Children.Add(v);
                driveRows.Children.Add(g);
                driveRows.Children.Add(new MiniBar
                {
                    Height = 4, Margin = new Thickness(0, 3, 0, 0), Value = usedF, Track = T.Track,
                    Fill = usedF > 0.9 ? T.Bad : usedF > 0.75 ? T.Warn : T.Good
                });
            }
        }
    }
}
