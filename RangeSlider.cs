// ---------------------------------------------------------------------------
//  A slider with two handles, for "between this many players and that many".
//
//  WinForms has TrackBar, which has one handle and therefore cannot express a
//  range. Asking for "around sixty players" needs both ends, so this draws its
//  own: a bar, two grips, and a readout.
//
//  Deliberately small and self-contained. It owns no timers, raises one event,
//  and paints in one pass - a filter control should never be the reason a
//  window feels slow.
// ---------------------------------------------------------------------------

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ABeautifulPotatoLauncher
{
    internal sealed class RangeSlider : Control
    {
        private int _min, _max = 127;
        private int _low, _high = 127;
        private Grip _dragging = Grip.None;

        private enum Grip { None, Low, High }

        public event EventHandler RangeChanged;

        /// <summary>
        /// Values snap to multiples of this. 1 means anywhere; 6 gives the
        /// quarter-day steps the game-time filter wants.
        /// </summary>
        public int Step = 1;

        /// <summary>
        /// Turns the two ends into the caption under the bar. Left null, the
        /// numbers are shown as plain player counts.
        /// </summary>
        public Func<int, int, string> FormatRange;

        /// <summary>Draw a mark at every Step. Only worth it when the steps are few.</summary>
        public bool ShowTicks;

        public RangeSlider()
        {
            // Without this the grips flicker while dragging, which on a dark
            // interface looks like the control is broken.
            SetStyle(ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.ResizeRedraw
                   | ControlStyles.UserPaint, true);

            Height = 38;
            Cursor = Cursors.Hand;
        }

        public int Minimum
        {
            get { return _min; }
            set { _min = value; Clamp(); Invalidate(); }
        }

        public int Maximum
        {
            get { return _max; }
            set { _max = Math.Max(value, _min + 1); Clamp(); Invalidate(); }
        }

        public int Low
        {
            get { return _low; }
            set { _low = value; Clamp(); Invalidate(); }
        }

        public int High
        {
            get { return _high; }
            set { _high = value; Clamp(); Invalidate(); }
        }

        /// <summary>True when the range covers everything, ie. filters nothing.</summary>
        public bool IsFullRange { get { return _low <= _min && _high >= _max; } }

        public void SetRange(int low, int high)
        {
            _low = low;
            _high = high;
            Clamp();
            Invalidate();
        }

        private void Clamp()
        {
            if (_low < _min) _low = _min;
            if (_high > _max) _high = _max;
            if (_low > _high) _low = _high;
        }

        // ------------------------------------------------------- geometry --

        private const int GripWidth = 9;
        private int TrackLeft { get { return GripWidth; } }
        private int TrackRight { get { return Math.Max(TrackLeft + 1, Width - GripWidth); } }
        private int TrackWidth { get { return TrackRight - TrackLeft; } }
        private int TrackY { get { return 12; } }

        private int ValueToX(int value)
        {
            double t = (value - _min) / (double)Math.Max(1, _max - _min);
            return TrackLeft + (int)Math.Round(t * TrackWidth);
        }

        /// <summary>Nearest multiple of Step, kept inside the range.</summary>
        private int Snap(int v)
        {
            if (Step <= 1) return v;

            int snapped = (int)Math.Round(v / (double)Step) * Step;
            return Math.Max(_min, Math.Min(_max, snapped));
        }

        private int XToValue(int x)
        {
            double t = (x - TrackLeft) / (double)Math.Max(1, TrackWidth);
            int v = _min + (int)Math.Round(t * (_max - _min));
            return Math.Max(_min, Math.Min(_max, v));
        }

        // ---------------------------------------------------------- input --

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);

            // Whichever grip is nearer to the click, so grabbing either end
            // works without having to hit nine pixels exactly.
            int dLow = Math.Abs(e.X - ValueToX(_low));
            int dHigh = Math.Abs(e.X - ValueToX(_high));

            // A tie goes to whichever grip can actually move that way - at the
            // extremes both sit on the same pixel, and picking the stuck one
            // makes the control feel dead.
            if (dLow == dHigh) _dragging = e.X > ValueToX(_low) ? Grip.High : Grip.Low;
            else _dragging = dLow < dHigh ? Grip.Low : Grip.High;

            MoveTo(e.X);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging != Grip.None) MoveTo(e.X);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragging = Grip.None;
        }

        private void MoveTo(int x)
        {
            int v = Snap(XToValue(x));

            if (_dragging == Grip.Low) _low = Math.Min(v, _high);
            else if (_dragging == Grip.High) _high = Math.Max(v, _low);
            else return;

            Invalidate();
            var h = RangeChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        // ---------------------------------------------------------- paint --

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);

            int y = TrackY;
            int lowX = ValueToX(_low), highX = ValueToX(_high);

            using (var track = new SolidBrush(Color.FromArgb(58, 58, 64)))
                g.FillRectangle(track, TrackLeft, y - 2, TrackWidth, 5);

            // Marks at each step, so it is obvious where the grips will land.
            if (ShowTicks && Step > 1)
            {
                using (var tick = new Pen(Color.FromArgb(88, 88, 96)))
                    for (int v = _min; v <= _max; v += Step)
                    {
                        int tx = ValueToX(v);
                        g.DrawLine(tick, tx, y + 4, tx, y + 7);
                    }
            }

            // The selected span, so the range reads at a glance.
            using (var span = new SolidBrush(IsFullRange
                       ? Color.FromArgb(70, 70, 78)
                       : Color.FromArgb(86, 140, 86)))
                g.FillRectangle(span, lowX, y - 2, Math.Max(1, highX - lowX), 5);

            DrawGrip(g, lowX, y);
            DrawGrip(g, highX, y);

            string text;
            if (FormatRange != null) text = FormatRange(_low, _high);
            else if (IsFullRange) text = "any number of players";
            else text = _low == _high ? _low + " players" : _low + " to " + _high + " players";

            using (var brush = new SolidBrush(IsFullRange
                       ? Color.FromArgb(150, 150, 158) : Color.Gainsboro))
            using (var font = new Font("Segoe UI", 7.5f))
                g.DrawString(text, font, brush, TrackLeft - 2, y + 9);
        }

        private static void DrawGrip(Graphics g, int x, int y)
        {
            var r = new Rectangle(x - GripWidth / 2, y - 7, GripWidth, 15);

            using (var fill = new SolidBrush(Color.FromArgb(215, 215, 220)))
                g.FillRectangle(fill, r);
            using (var edge = new Pen(Color.FromArgb(40, 40, 46)))
                g.DrawRectangle(edge, r);
        }
    }
}
