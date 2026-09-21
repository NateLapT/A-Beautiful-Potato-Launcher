// ---------------------------------------------------------------------------
//  A box you type into, and the things you have added laid out beside it, each
//  with its own X.
//
//  Used wherever a filter takes SEVERAL values rather than one - the mods a
//  server must run, the mods to pick out of a server's list. Written once and
//  shared, so adding and removing behaves the same in both places.
//
//  The entries sit BELOW the box and flow left to right across the full width,
//  wrapping onto further lines. Two earlier arrangements did not work: a
//  scrolling list hid everything past the second row behind a scrollbar nobody
//  noticed, and putting the chips beside the box left them so little room that
//  the third mod ran off the edge. Mod names are long; they need the whole
//  width and as many lines as they take.
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

        /// <summary>Width of the typing box; the chips take everything after it.</summary>
        private const int BoxWidth = 230;
        private const int AddWidth = 46;

        /// <summary>Height of one row of chips, including the gap below it.</summary>
        private const int ChipRowHeight = 22;

        public ChipInput(int width, int rows)
        {
            Width = width;

            // The box's row, then however many rows of chips were asked for.
            Height = 26 + Math.Max(1, rows) * ChipRowHeight + 2;

            _box = new ComboBox
            {
                Bounds = new Rectangle(0, 0, BoxWidth, 23),
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
                Bounds = new Rectangle(BoxWidth - 38, 3, 17, 17),
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
                Bounds = new Rectangle(BoxWidth + 4, 0, AddWidth, 23),
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

            // The chips go UNDERNEATH, across the whole width. Beside the box
            // there was only room for two or three before they disappeared off
            // the edge.
            _strip = new ChipStrip(this)
            {
                Bounds = new Rectangle(0, 26, width, Math.Max(ChipRowHeight, Height - 26)),
                BackColor = BackColor
            };
            Controls.Add(_strip);

            Resize += (s, e) =>
            {
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
            _strip.Invalidate();

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
        /// Draws the entries as chips and handles clicks on their crosses.
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

            public ChipStrip(ChipInput owner)
            {
                _owner = owner;
                SetStyle(ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.ResizeRedraw
                       | ControlStyles.UserPaint, true);
            }

            private const int ChipHeight = 19;
            private const int CloseWidth = 16;
            private const int Pad = 7;

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(BackColor);
                _closes.Clear();

                int x = 0, y = 1;

                for (int i = 0; i < _owner._entries.Count; i++)
                {
                    string text = _owner._entries[i];
                    int textWidth = TextRenderer.MeasureText(text, Font).Width;
                    int chipWidth = Pad + textWidth + CloseWidth;

                    // Wrap rather than run off the edge.
                    if (x > 0 && x + chipWidth > Width)
                    {
                        x = 0;
                        y += ChipHeight + 3;
                        if (y + ChipHeight > Height) break;      // no room left
                    }

                    var chip = new Rectangle(x, y, chipWidth, ChipHeight);

                    using (var back = new SolidBrush(ChipBack))
                        g.FillRectangle(back, chip);
                    using (var edge = new Pen(ChipEdge))
                        g.DrawRectangle(edge, chip);

                    TextRenderer.DrawText(g, text, Font,
                        new Rectangle(chip.X + Pad - 3, chip.Y, textWidth + 4, chip.Height),
                        Color.White,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

                    // The cross sits immediately after the name, inside the chip.
                    var close = new Rectangle(chip.Right - CloseWidth, chip.Y, CloseWidth, chip.Height);
                    _closes.Add(close);

                    TextRenderer.DrawText(g, "✕", Font, close,
                        _hover == i ? Color.White : Color.FromArgb(200, 225, 200),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

                    x += chipWidth + 5;
                }

                // Say what the empty space is for, rather than leaving a blank.
                if (_owner._entries.Count == 0)
                    TextRenderer.DrawText(g, "nothing added yet", Font,
                        new Rectangle(2, 0, Width, Height), Dim,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);

                int over = -1;
                for (int i = 0; i < _closes.Count; i++)
                    if (_closes[i].Contains(e.Location)) { over = i; break; }

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
                    if (!_closes[i].Contains(e.Location)) continue;
                    _owner.RemoveAt(i);
                    return;
                }
            }
        }
    }
}
