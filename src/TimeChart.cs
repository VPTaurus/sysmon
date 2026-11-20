using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace SysMonApp
{
    class ChartSeries
    {
        public string Name; public Brush Stroke; public bool Fill = true, Dashed;
        public double[] T = new double[0], V = new double[0];   // T = seconds (DateTime ticks / 1e7)
    }

    // Time-axis line/area chart with axes, anomaly markers and hover read-out
    class TimeChart : FrameworkElement
    {
        public readonly List<ChartSeries> Series = new List<ChartSeries>();
        public double FixedMax = double.NaN, MinMax = 1, GapSec = 60;
        public Func<double, string> Format = v => v.ToString("0");
        public DateTime From, To;
        public List<Anomaly> Marks = new List<Anomaly>();
        public double[] LossT = new double[0];
        public Theme Th;
        public Anomaly Selected;                 // highlighted (zoomed-to) anomaly
        public int YTicks = 4;                   // horizontal grid lines (more when the chart is expanded)
        public Action<Anomaly> MarkClicked;
        Point hover = new Point(-1, -1);
        const double L = 58, R = 10, TP = 8, B = 20;

        public static double Sec(DateTime t) { return t.Ticks / 1e7; }

        public TimeChart()
        {
            ClipToBounds = true;
            MouseMove += (s, e) =>
            {
                hover = e.GetPosition(this);
                Cursor = MarkAt(hover.X) != null ? Cursors.Hand : null;
                InvalidateVisual();
            };
            MouseLeave += (s, e) => { hover = new Point(-1, -1); InvalidateVisual(); };
            MouseLeftButtonUp += (s, e) =>
            {
                var a = MarkAt(e.GetPosition(this).X);
                if (a != null && MarkClicked != null) MarkClicked(a);
            };
        }

        Anomaly MarkAt(double x)
        {
            double pw = ActualWidth - L - R, t0 = Sec(From), span = Math.Max(1, Sec(To) - t0);
            if (pw < 20) return null;
            Anomaly best = null; double bd = 7;
            foreach (var a in Marks)
            {
                double d = Math.Abs(L + (Sec(a.T) - t0) / span * pw - x);
                if (d < bd) { bd = d; best = a; }
            }
            return best;
        }

        FormattedText Ft(string s, double size, Brush b)
        {
            return new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, b,
                                     VisualTreeHelper.GetDpi(this).PixelsPerDip);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight, pw = w - L - R, ph = h - TP - B;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));   // hit-testable
            if (pw < 20 || ph < 20 || Th == null) return;
            double t0 = Sec(From), t1 = Sec(To), span = Math.Max(1, t1 - t0);

            double max = FixedMax;
            if (double.IsNaN(max))
            {
                max = MinMax;
                foreach (var s in Series) foreach (var v in s.V) if (!double.IsNaN(v) && v > max) max = v;
                max *= 1.1;
            }
            Func<double, double> X = t => L + (t - t0) / span * pw;
            Func<double, double> Y = v => TP + ph - Math.Min(1.0, Math.Max(0, v) / max) * ph;

            // grid + y labels
            var gp = new Pen(Th.Track, 1); gp.Freeze();
            for (int i = 0; i <= YTicks; i++)
            {
                double y = Math.Round(TP + ph - ph * i / (double)YTicks) + 0.5;
                dc.DrawLine(gp, new Point(L, y), new Point(L + pw, y));
                var ft = Ft(Format(max * i / (double)YTicks), 10, Th.Faint);
                dc.DrawText(ft, new Point(L - 6 - ft.Width, y - ft.Height / 2));
            }
            // x labels
            int nx = Math.Max(2, (int)(pw / 120));
            string xf = span <= 900 ? "HH:mm:ss" : span <= 2 * 86400 ? "HH:mm" : "dd.MM HH:mm";
            for (int i = 0; i <= nx; i++)
            {
                double t = t0 + span * i / nx;
                var ft = Ft(new DateTime((long)(t * 1e7)).ToString(xf), 10, Th.Faint);
                double x = X(t) - ft.Width / 2;
                x = Math.Max(L - 4, Math.Min(L + pw - ft.Width, x));
                dc.DrawText(ft, new Point(x, TP + ph + 3));
            }

            // anomaly markers (behind data)
            foreach (var a in Marks)
            {
                double ta = Sec(a.T);
                if (ta < t0 || ta > t1) continue;
                var br = Sev(a.Severity);
                double x = X(ta);
                bool sel = Selected != null && Selected.T == a.T && Selected.Message == a.Message;
                if (sel) dc.DrawRectangle(Theme.Fill(br, 0x22), null, new Rect(x - 6, TP, 12, ph));
                var mp = sel ? new Pen(br, 2) : new Pen(Theme.Fill(br, 0x70), 1) { DashStyle = DashStyles.Dash }; mp.Freeze();
                dc.DrawLine(mp, new Point(x, TP), new Point(x, TP + ph));
                var tri = new StreamGeometry();
                using (var c = tri.Open())
                {
                    c.BeginFigure(new Point(x - 5, TP - 1), true, true);
                    c.LineTo(new Point(x + 5, TP - 1), false, false);
                    c.LineTo(new Point(x, TP + 7), false, false);
                }
                tri.Freeze();
                dc.DrawGeometry(br, null, tri);
            }

            // series
            foreach (var s in Series)
            {
                int n = Math.Min(s.T.Length, s.V.Length);
                if (n == 0) continue;
                var pen = new Pen(s.Stroke, s.Dashed ? 1 : 1.4);
                if (s.Dashed) pen.DashStyle = new DashStyle(new double[] { 2, 2 }, 0);
                pen.Freeze();
                var fill = Theme.Fill(s.Stroke, 0x30);
                int i = 0;
                while (i < n)
                {
                    while (i < n && double.IsNaN(s.V[i])) i++;
                    int a = i;
                    while (i < n && !double.IsNaN(s.V[i]) && (i == a || s.T[i] - s.T[i - 1] <= GapSec)) i++;
                    int b = i - 1;
                    if (b < a) continue;
                    var line = new StreamGeometry();
                    using (var c = line.Open())
                    {
                        c.BeginFigure(new Point(X(s.T[a]), Y(s.V[a])), false, false);
                        for (int k = a + 1; k <= b; k++) c.LineTo(new Point(X(s.T[k]), Y(s.V[k])), true, true);
                        if (a == b) c.LineTo(new Point(X(s.T[a]) + 1.5, Y(s.V[a])), true, true);
                    }
                    line.Freeze();
                    if (s.Fill && b > a)
                    {
                        var area = new StreamGeometry();
                        using (var c = area.Open())
                        {
                            c.BeginFigure(new Point(X(s.T[a]), TP + ph), true, true);
                            for (int k = a; k <= b; k++) c.LineTo(new Point(X(s.T[k]), Y(s.V[k])), false, false);
                            c.LineTo(new Point(X(s.T[b]), TP + ph), false, false);
                        }
                        area.Freeze();
                        dc.DrawGeometry(fill, null, area);
                    }
                    dc.DrawGeometry(null, pen, line);
                }
            }

            // packet loss ticks
            foreach (var t in LossT)
                if (t >= t0 && t <= t1) dc.DrawRectangle(Th.Bad, null, new Rect(X(t) - 1, TP + ph - 5, 2.5, 5));

            if (Series.Count == 0 || Series.All(s => s.T.Length == 0))
            {
                var ft = Ft("No data for this period yet", 12, Th.Faint);
                dc.DrawText(ft, new Point(L + (pw - ft.Width) / 2, TP + (ph - ft.Height) / 2));
            }

            // hover
            if (hover.X >= L && hover.X <= L + pw && hover.Y >= 0)
            {
                double th = t0 + (hover.X - L) / pw * span;
                var lines = new List<string>();
                double snapT = double.NaN;
                foreach (var s in Series)
                {
                    int k = Nearest(s.T, th);
                    if (k < 0) continue;
                    if (double.IsNaN(snapT)) snapT = s.T[k];
                    if (Math.Abs(s.T[k] - th) > Math.Max(GapSec, span / pw * 8)) continue;
                    lines.Add((s.Name ?? "") + "  " + (double.IsNaN(s.V[k]) ? "-" : Format(s.V[k])));
                }
                if (double.IsNaN(snapT)) snapT = th;
                double hx = X(snapT);
                var cp = new Pen(Th.Faint, 1) { DashStyle = DashStyles.Dot }; cp.Freeze();
                dc.DrawLine(cp, new Point(hx, TP), new Point(hx, TP + ph));
                string head = new DateTime((long)(snapT * 1e7)).ToString(span > 86400 ? "ddd dd.MM HH:mm" : "HH:mm:ss");
                foreach (var a in Marks)
                    if (Math.Abs(X(Sec(a.T)) - hover.X) <= 6)
                        lines.Add("⚠ " + (a.Message.Length > 90 ? a.Message.Substring(0, 90) + "..." : a.Message));
                var ft = Ft(head + "\n" + string.Join("\n", lines), 11, Th.Fg);
                ft.MaxTextWidth = Math.Max(120, Math.Min(380, pw - 10));
                double bx = hx + 10, by = TP + 4;
                if (bx + ft.Width + 12 > w) bx = hx - ft.Width - 22;
                if (bx < 0) bx = 2;
                dc.DrawRoundedRectangle(Th.HoverBg, new Pen(Th.Border, 1), new Rect(bx, by, ft.Width + 12, ft.Height + 8), 4, 4);
                dc.DrawText(ft, new Point(bx + 6, by + 4));
            }
        }

        Brush Sev(string s) { return s == "critical" ? Th.Bad : s == "warning" ? Th.Warn : Th.Cpu; }

        static int Nearest(double[] t, double x)
        {
            if (t.Length == 0) return -1;
            int i = Array.BinarySearch(t, x);
            if (i >= 0) return i;
            i = ~i;
            if (i == 0) return 0;
            if (i >= t.Length) return t.Length - 1;
            return x - t[i - 1] < t[i] - x ? i - 1 : i;
        }
    }
}
