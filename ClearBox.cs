// ---------------------------------------------------------------------------
//  The little X that empties a text box.
//
//  Every search and filter field in the launcher has one, and they all behave
//  the same way: hidden while the box is empty, appearing the moment there is
//  something to clear, and putting the caret back in the box afterwards so the
//  next thing typed goes where it is expected.
//
//  A Label rather than a Button: a button brings a focus rectangle, a tab stop
//  and a border, all of which have to be turned off again, and none of which
//  belong on a glyph sitting inside another control.
// ---------------------------------------------------------------------------

using System;
using System.Drawing;
using System.Windows.Forms;

namespace ABeautifulPotatoLauncher
{
    internal static class ClearBox
    {
        private static readonly Color Dim = Color.FromArgb(150, 150, 158);

        /// <summary>
        /// Puts a clear button inside a text box, at its right-hand edge.
        ///
        /// The box keeps its own bounds; the glyph is positioned over it and
        /// follows it if it moves or is resized - which matters because the
        /// filter panel lays itself out more than once.
        /// </summary>
        public static Label AddTo(TextBox box)
        {
            // MUST be called after the box has been added to its parent - the
            // glyph is a sibling, so there is nowhere to put it otherwise. This
            // used to return quietly and the X simply never appeared.
            if (box == null) return null;
            if (box.Parent == null)
                throw new InvalidOperationException(
                    "ClearBox.AddTo: add the text box to its parent first.");

            var x = new Label
            {
                Text = "✕",
                Size = new Size(17, 17),
                ForeColor = Dim,
                BackColor = box.BackColor,
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand,
                Visible = box.Text.Length > 0
            };

            EventHandler place = (s, e) =>
                x.Location = new Point(box.Right - 20, box.Top + (box.Height - x.Height) / 2);

            place(null, EventArgs.Empty);

            box.Parent.Controls.Add(x);
            x.BringToFront();

            box.TextChanged += (s, e) => x.Visible = box.Text.Length > 0;
            box.LocationChanged += place;
            box.SizeChanged += place;

            x.Click += (s, e) =>
            {
                box.Clear();
                box.Focus();
            };
            x.MouseEnter += (s, e) => x.ForeColor = Color.White;
            x.MouseLeave += (s, e) => x.ForeColor = Dim;

            return x;
        }

        /// <summary>
        /// The same, for an editable ComboBox. The glyph sits left of the drop
        /// arrow rather than over it, or clearing and opening would be the same
        /// click.
        /// </summary>
        public static Label AddTo(ComboBox box)
        {
            if (box == null) return null;
            if (box.Parent == null)
                throw new InvalidOperationException(
                    "ClearBox.AddTo: add the combo box to its parent first.");

            var x = new Label
            {
                Text = "✕",
                Size = new Size(17, 17),
                ForeColor = Dim,
                BackColor = box.BackColor,
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand,
                Visible = box.Text.Length > 0
            };

            EventHandler place = (s, e) =>
                x.Location = new Point(box.Right - 38, box.Top + (box.Height - x.Height) / 2);

            place(null, EventArgs.Empty);

            box.Parent.Controls.Add(x);
            x.BringToFront();

            box.TextChanged += (s, e) => x.Visible = box.Text.Length > 0;
            box.LocationChanged += place;
            box.SizeChanged += place;

            x.Click += (s, e) =>
            {
                box.Text = "";
                box.Focus();
            };
            x.MouseEnter += (s, e) => x.ForeColor = Color.White;
            x.MouseLeave += (s, e) => x.ForeColor = Dim;

            return x;
        }
    }
}
