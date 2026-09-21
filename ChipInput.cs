// ---------------------------------------------------------------------------
//  A box you type into, and the things you have added underneath it, each with
//  its own X.
//
//  Used wherever a filter takes SEVERAL values rather than one - the mods a
//  server must run, the mods to pick out of a server's list. Written once and
//  shared, so adding and removing behaves the same in both places.
//
//  The entries sit BELOW the box, flow left to right, wrap onto further lines,
//  and the area SCROLLS when there are more than fit. Three arrangements came
//  before this one, each fixing the last:
//
//    * a list box, whose scrollbar nobody noticed;
//    * chips beside the box, where the third mod ran off the edge;
//    * chips below the box with no scrolling, which simply stopped drawing once
//      the rows ran out - making a filter the player had set invisible.
//
//  Scrolling is drawn here rather than delegated to AutoScroll, which steals the
//  first click on whatever it decides to bring into view.
//
//  Chips are drawn, not built from controls: a label and a button per entry is
//  two windows to create, place and dispose every time the set changes, for
//  something that is a word and a cross.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace ABeautifulPotatoLauncher
{
    internal sealed class ChipInput : Panel
    {
        private static readonly Color Panel2 = Color.FromArgb(38, 38, 42);
        private static readonly Color ChipBack = Color.FromArgb(58, 78, 58);
        private static readonly Color ChipEdge = Color.FromArgb(92, 132, 92);
        private static readonly Color Dim = Color.FromArgb(150, 150, 158);

        private readonly ComboBox _box;
        private readonly Label _clear;
        private readonly Button _add;
        private readonly ChipStrip _strip;
        private readonly List<string> _entries = new List<string>();

        public event EventHandler EntriesChanged;

        public IEnumerable<string> Entries { get { return _entries.ToArray(); } }
        public int Count { get { return _entries.Count; } }

        /// <summary>The typing box, exposed so callers can stock its dropdown.</summary>
        public ComboBox Box { get { return _box; } }

        private const int AddWidth = 46;

        /// <summary>Height of one row of chips, including the gap below it.</summary>
        private const int ChipRowHeight = 22;

        public ChipInput(int width, int rows)
        {
            Width = width;
            Height = 26 + Math.Max(1, rows) * ChipRowHeight + 2;

            int boxWidth = Math.Max(90, width - AddWidth - 4);

            _box = new ComboBox
            {
                Bounds = new Rectangle(0, 0, boxWidth, 23),
                BackColor = Panel2,
                ForeColor = Color.Gainsboro,
                FlatStyle = FlatStyle.Flat,
                DropDownStyle = ComboBoxStyle.DropDown,
                AutoCompleteMode = AutoCompleteMode.SuggestAppend,
                AutoCompleteSource = AutoCompleteSource.ListItems
            };

            _box.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                e.Handled = true;
                e.SuppressKeyPress = true;          // no ding
                Add(_box.Text);
            };

            // Only a deliberate pick adds. SelectedIndexChanged also fires when
            // the list is refilled, which would add whatever was in the box.
            _box.SelectionChangeCommitted += (s, e) => Add(_box.Text);
            _box.TextChanged += (s, e) => _clear.Visible = _box.Text.Length > 0;
            Controls.Add(_box);

            _clear = new Label
            {
                Text = "✕",
                Bounds = new Rectangle(boxWidth - 38, 3, 17, 17),
                ForeColor = Dim,
                BackColor = Panel2,
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand,
                Visible = false
            };
            _clear.Click += (s, e) => { _box.Text = ""; _box.Focus(); };
            _clear.MouseEnter += (s, e) => _clear.ForeColor = Color.White;
            _clear.MouseLeave += (s, e) => _clear.ForeColor = Dim;
            Controls.Add(_clear);
            _clear.BringToFront();

            _add = new Button
            {
                Text = "ADD",
                Bounds = new Rectangle(boxWidth + 4, 0, AddWidth, 23),
                FlatStyle = FlatStyle.Flat,
                BackColor = Panel2,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 7.5f, FontStyle.Bold),
                Cursor = Cursors.Hand,
                TabStop = false
            };
            _add.FlatAppearance.BorderColor = Color.FromArgb(75, 75, 82);
            _add.Click += (s, e) => Add(_box.Text);
            Controls.Add(_add);

            _strip = new ChipStrip(this)
            {
                Bounds = new Rectangle(0, 26, width, Math.Max(ChipRowHeight, Height - 26)),
                BackColor = BackColor
            };
            Controls.Add(_strip);

            Resize += (s, e) =>
            {
                int bw = Math.Max(90, Width - AddWidth - 4);
                _box.Width = bw;
                _clear.Left = bw - 38;
                _add.Left = bw + 4;

                _strip.Bounds = new Rectangle(0, 26, Width, Math.Max(ChipRowHeight, Height - 26));
                _strip.Invalidate();
            };
        }

        public override Color BackColor
        {
            get { return base.BackColor; }
            set
            {
                base.BackColor = value;
                if (_strip != null) _strip.BackColor = value;
            }
        }

        // ------------------------------------------------------- entries --

        public void Add(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            string clean = text.Trim();

            // The dropdowns show "@Gunplay   (412 servers)"; keep the name.
            int gap = clean.IndexOf("   ", StringComparison.Ordinal);
            if (gap > 0) clean = clean.Substring(0, gap).Trim();

            if (clean.Length == 0) return;
            if (clean.StartsWith("(", StringComparison.Ordinal)) return;   // "(any map)" and friends
            if (_entries.Any(e => string.Equals(e, clean, StringComparison.OrdinalIgnoreCase))) return;

            _entries.Add(clean);
            _box.Text = "";
            _strip.ScrollToEnd();

            Raise();
        }

        public void RemoveAt(int index)
        {
            if (index < 0 || index >= _entries.Count) return;

            _entries.RemoveAt(index);
            _strip.Invalidate();

            Raise();
        }

        public void Clear()
        {
            if (_entries.Count == 0 && _box.Text.Length == 0) return;

            _entries.Clear();
            _box.Text = "";
            _strip.Invalidate();

            Raise();
        }

        private void Raise()
        {
            var h = EntriesChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        // --------------------------------------------------------- chips --

        /// <summary>
        /// Draws the entries as chips, scrolls them, and handles clicks on their
        /// crosses.
        ///
        /// Its own control so that painting and hit-testing share one set of
        /// rectangles - working them out twice is how the X ends up one chip
        /// away from where it looks.
        /// </summary>
        private sealed class ChipStrip : Control
        {
            private readonly ChipInput _owner;
            private readonly List<Rectangle> _closes = new List<Rectangle>();
            private int _hover = -1;

            /// <summary>How far the content is scrolled, in pixels.</summary>
            private int _scroll;

            /// <summary>Height the chips need. More than Height means scrolling.</summary>
            private int _contentHeight;

            public ChipStrip(ChipInput owner)
            {
                _owner = owner;
                SetStyle(ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.ResizeRedraw
                       | ControlStyles.UserPaint
                       | ControlStyles.Selectable, true);

                // The wheel goes to whatever has focus, so hovering has to take
                // it - otherwise scrolling here scrolls something else.
                MouseEnter += (s, e) => { if (CanFocus) Focus(); };
            }

            private const int ChipHeight = 19;
            private const int CloseWidth = 16;
            private const int Pad = 7;
            private const int BarWidth = 5;

            private bool Scrollable { get { return _contentHeight > Height; } }
            private int UsableWidth { get { return Width - (Scrollable ? BarWidth + 3 : 0); } }

            /// <summary>Shows the newest chip, which is the one just added.</summary>
            public void ScrollToEnd()
            {
                Measure();
                _scroll = Math.Max(0, _contentHeight - Height);
                Invalidate();
            }

            /// <summary>
            /// Where every chip goes, in content coordinates - before scrolling.
            ///
            /// Nothing is dropped: a chip that does not fit on screen still gets
            /// a position, which is what makes it reachable by scrolling instead
            /// of vanishing.
            /// </summary>
            private List<Rectangle> Layout()
            {
                var rects = new List<Rectangle>(_owner._entries.Count);
                int x = 0, y = 1;
                int room = Math.Max(40, UsableWidth);

                foreach (string text in _owner._entries)
                {
                    int chipWidth = Pad + TextRenderer.MeasureText(text, Font).Width + CloseWidth;
                    if (chipWidth > room) chipWidth = room;      // a very long name

                    if (x > 0 && x + chipWidth > room)
                    {
                        x = 0;
                        y += ChipHeight + 3;
                    }

                    rects.Add(new Rectangle(x, y, chipWidth, ChipHeight));
                    x += chipWidth + 5;
                }
                return rects;
            }

            private void Measure()
            {
                var rects = Layout();
                _contentHeight = rects.Count == 0 ? 0 : rects[rects.Count - 1].Bottom + 2;
            }

            private void ClampScroll()
            {
                int max = Math.Max(0, _contentHeight - Height);
                if (_scroll > max) _scroll = max;
                if (_scroll < 0) _scroll = 0;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(BackColor);
                _closes.Clear();

                if (_owner._entries.Count == 0)
                {
                    _contentHeight = 0;
                    _scroll = 0;

                    TextRenderer.DrawText(g, "nothing added yet", Font,
                        new Rectangle(2, 0, Width, Height), Dim,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                    return;
                }

                // Laid out first: the scrollbar has to know whether it is
                // needed, and the usable width depends on that answer.
                var rects = Layout();
                _contentHeight = rects[rects.Count - 1].Bottom + 2;
                ClampScroll();

                for (int i = 0; i < rects.Count; i++)
                {
                    var chip = rects[i];
                    chip.Y -= _scroll;

                    // Scrolled out of view. It still gets a close rectangle so
                    // the indexes line up - just one that cannot be hit.
                    if (chip.Bottom < 0 || chip.Top > Height)
                    {
                        _closes.Add(Rectangle.Empty);
                        continue;
                    }

                    using (var back = new SolidBrush(ChipBack))
                        g.FillRectangle(back, chip);
                    using (var edge = new Pen(ChipEdge))
                        g.DrawRectangle(edge, chip);

                    int textWidth = chip.Width - Pad - CloseWidth;
                    TextRenderer.DrawText(g, _owner._entries[i], Font,
                        new Rectangle(chip.X + Pad - 3, chip.Y, textWidth + 4, chip.Height),
                        Color.White,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                        | TextFormatFlags.EndEllipsis);

                    var close = new Rectangle(chip.Right - CloseWidth, chip.Y, CloseWidth, chip.Height);
                    _closes.Add(close);

                    TextRenderer.DrawText(g, "✕", Font, close,
                        _hover == i ? Color.White : Color.FromArgb(200, 225, 200),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }

                if (Scrollable) DrawScrollBar(g);
            }

            private void DrawScrollBar(Graphics g)
            {
                int trackX = Width - BarWidth;

                using (var track = new SolidBrush(Color.FromArgb(48, 48, 54)))
                    g.FillRectangle(track, trackX, 0, BarWidth, Height);

                int thumbHeight = Math.Max(14, (int)(Height * (Height / (double)_contentHeight)));
                int span = Math.Max(1, _contentHeight - Height);
                int thumbY = (int)((Height - thumbHeight) * (_scroll / (double)span));

                using (var thumb = new SolidBrush(Color.FromArgb(110, 110, 120)))
                    g.FillRectangle(thumb, trackX, thumbY, BarWidth, thumbHeight);
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                base.OnMouseWheel(e);
                if (!Scrollable) return;

                _scroll -= Math.Sign(e.Delta) * (ChipHeight + 3);
                ClampScroll();
                Invalidate();
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);

                int over = -1;
                for (int i = 0; i < _closes.Count; i++)
                {
                    if (_closes[i].IsEmpty || !_closes[i].Contains(e.Location)) continue;
                    over = i;
                    break;
                }

                Cursor = over >= 0 ? Cursors.Hand : Cursors.Default;

                if (over == _hover) return;
                _hover = over;
                Invalidate();
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                if (_hover < 0) return;
                _hover = -1;
                Invalidate();
            }

            protected override void OnMouseClick(MouseEventArgs e)
            {
                base.OnMouseClick(e);

                // Only the cross removes, so a stray click on the word never
                // silently drops a filter.
                for (int i = 0; i < _closes.Count; i++)
                {
                    if (_closes[i].IsEmpty || !_closes[i].Contains(e.Location)) continue;
                    _owner.RemoveAt(i);
                    return;
                }
            }
        }
    }
}
