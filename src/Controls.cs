using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace SysMonApp
{
    class Series
    {
        public Ring Data; public Brush Stroke, Fill; public string Name;
        public Series(Ring d, Brush stroke, string name) { Data = d; Stroke = stroke; Fill = Theme.Fill(stroke, 0x38); Name = name; }
    }

    // Area chart with hover read-out
    class Spark : FrameworkElement
    {
        public readonly List<Series> Series = new List<Series>();
        public double FixedMax = double.NaN;     // NaN = auto scale
        public double MinMax = 1;
        public Func<double, string> Format = v => v.ToString("0");
        public Func<double> SecondsPerPoint = () => 1;
        public Brush Track, Text, HoverBg, NaNMark;
        public bool ShowScale;
        double hoverX = -1;

        public Spark()
        {
            ClipToBounds = true;
            MouseMove += (s, e) => { hoverX = e.GetPosition(this).X; InvalidateVisual(); };
            MouseLeave += (s, e) => { hoverX = -1; InvalidateVisual(); };
        }

        FormattedText Ft(string s, double size, Brush b)
        {
            return new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, b,
                                     VisualTreeHelper.GetDpi(this).PixelsPerDip);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0 || Series.Count == 0) return;
            dc.DrawRoundedRectangle(Track, null, new Rect(0, 0, w, h), 3, 3);

            double max = FixedMax;
            if (double.IsNaN(max)) { max = MinMax; foreach (var s in Series) max = Math.Max(max, s.Data.Max()); max *= 1.15; }

            int cap = Series[0].Data.Capacity;
            double step = w / (cap - 1);

            foreach (var s in Series)
            {
                var d = s.Data; int n = d.Count;
                if (n < 2) continue;
                double x0 = w - (n - 1) * step;
                var pen = new Pen(s.Stroke, 1.2); pen.Freeze();
                int i = 0;
                while (i < n)
                {
                    while (i < n && double.IsNaN(d[i])) i++;
                    int a = i;
                    while (i < n && !double.IsNaN(d[i])) i++;
                    int b = i - 1;
                    if (b < a) continue;
                    var fill = new StreamGeometry(); var line = new StreamGeometry();
                    using (var fc = fill.Open())
                    using (var lc = line.Open())
                    {
                        fc.BeginFigure(new Point(x0 + a * step, h + 1), true, true);
                        for (int k = a; k <= b; k++)
                        {
                            var p = new Point(x0 + k * step, h - Math.Min(1.0, d[k] / max) * (h - 2) - 0.5);
                            fc.LineTo(p, false, false);
                            if (k == a) lc.BeginFigure(p, false, false); else lc.LineTo(p, true, true);
                        }
                        if (a == b) lc.LineTo(new Point(x0 + a * step + 1, h - Math.Min(1.0, d[a] / max) * (h - 2)), true, true);
                        fc.LineTo(new Point(x0 + b * step, h + 1), false, false);
                    }
                    fill.Freeze(); line.Freeze();
                    dc.DrawGeometry(s.Fill, null, fill);
                    dc.DrawGeometry(null, pen, line);
                }
                if (NaNMark != null)
                    for (int k = 0; k < n; k++)
                        if (double.IsNaN(d[k])) dc.DrawRectangle(NaNMark, null, new Rect(x0 + k * step - 1, h - 4, Math.Max(2, step), 4));
            }

            if (ShowScale)
            {
                var ft = Ft(Format(max), 9, Text);
                dc.DrawText(ft, new Point(3, 1));
            }

            // hover read-out
            if (hoverX >= 0)
            {
                int k = (int)Math.Round((w - hoverX) / step);
                var parts = new List<string>();
                foreach (var s in Series)
                {
                    int idx = s.Data.Count - 1 - k;
                    if (idx < 0) continue;
                    double v = s.Data[idx];
                    parts.Add((s.Name != null ? s.Name + " " : "") + (double.IsNaN(v) ? "timeout" : Format(v)));
                }
                if (parts.Count == 0) return;
                double x = w - k * step;
                var lp = new Pen(Text, 1) { DashStyle = DashStyles.Dot }; lp.Freeze();
                dc.DrawLine(lp, new Point(x, 0), new Point(x, h));
                var ft = Ft(Fmt.Ago(k * SecondsPerPoint()) + "   " + string.Join("  ", parts), 10, Text);
                double tw = ft.Width + 8, tx = x + 6;
                if (tx + tw > w) tx = x - 6 - tw;
                if (tx < 0) tx = 0;
                dc.DrawRoundedRectangle(HoverBg, null, new Rect(tx, 1, tw, ft.Height + 2), 3, 3);
                dc.DrawText(ft, new Point(tx + 4, 2));
            }
        }
    }

    // per-core utilisation bars
    class CoreBars : FrameworkElement
    {
        public double[] Values = new double[0];
        public Brush Bar, Track, Text, HoverBg, Warn, Bad;
        int hover = -1;

        public CoreBars()
        {
            MouseMove += (s, e) =>
            {
                int n = Values.Length;
                hover = n == 0 ? -1 : Math.Max(0, Math.Min(n - 1, (int)(e.GetPosition(this).X / (ActualWidth / n))));
                InvalidateVisual();
            };
            MouseLeave += (s, e) => { hover = -1; InvalidateVisual(); };
        }

        protected override void OnRender(DrawingContext dc)
        {
            int n = Values.Length;
            double w = ActualWidth, h = ActualHeight;
            if (n == 0 || w <= 0) return;
            double gap = n > 24 ? 1 : 2;
            double bw = (w - gap * (n - 1)) / n;
            for (int i = 0; i < n; i++)
            {
                double x = i * (bw + gap), v = Values[i];
                dc.DrawRoundedRectangle(Track, null, new Rect(x, 0, bw, h), 1.5, 1.5);
                double bh = Math.Max(1, h * v / 100);
                var br = v >= 95 ? Bad : v >= 75 ? Warn : Bar;
                dc.DrawRoundedRectangle(br, null, new Rect(x, h - bh, bw, bh), 1.5, 1.5);
            }
            if (hover >= 0 && hover < n)
            {
                var ft = new FormattedText("Core " + hover + ": " + Fmt.Pct(Values[hover]), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                           new Typeface("Segoe UI"), 10, Text, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                double x = hover * (bw + gap) + bw / 2 - ft.Width / 2 - 4;
                x = Math.Max(0, Math.Min(w - ft.Width - 8, x));
                dc.DrawRoundedRectangle(HoverBg, null, new Rect(x, 0, ft.Width + 8, Math.Min(h, ft.Height + 1)), 3, 3);
                dc.DrawText(ft, new Point(x + 4, 0));
            }
        }
    }

    // thin progress bar
    class MiniBar : FrameworkElement
    {
        public double Value; public Brush Fill, Track;
        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            dc.DrawRoundedRectangle(Track, null, new Rect(0, 0, w, h), h / 2, h / 2);
            double fw = w * Math.Max(0, Math.Min(1, Value));
            if (fw > 0) dc.DrawRoundedRectangle(Fill, null, new Rect(0, 0, Math.Max(fw, h), h), h / 2, h / 2);
        }
    }
}
