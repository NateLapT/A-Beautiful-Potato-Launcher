// ---------------------------------------------------------------------------
//  One rule for the pointer: if it can be clicked, it says so.
//
//  WinForms gives a Button the ordinary arrow, which on a dark custom-drawn
//  interface leaves people guessing what is a control and what is a label.
//  This walks a window once, after it is built, and gives the hand cursor to
//  everything that actually does something when clicked.
//
//  It is applied by walking the tree rather than set on each control as it is
//  created, so a control added later cannot quietly miss out.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace ABeautifulPotatoLauncher
{
    internal static class UiCursors
    {
        /// <summary>Gives the hand cursor to every clickable control in a window.</summary>
        public static void ApplyTo(Control root)
        {
            if (root == null) return;

            foreach (Control c in root.Controls)
            {
                if (IsClickable(c)) c.Cursor = Cursors.Hand;
                ApplyTo(c);
            }
        }

        /// <summary>
        /// Gives a ListView a hand cursor over the columns that act as buttons.
        ///
        /// A ListView has no notion of a clickable cell - the launcher builds
        /// its own by painting "Repair" or "Remove" into a column and watching
        /// for a click on it. Nothing about the pointer says so, which left the
        /// player guessing at what was a button and what was text. This watches
        /// the pointer and switches the cursor over exactly those columns.
        ///
        /// <paramref name="isLive"/> is asked before showing the hand, so a
        /// greyed-out cell - one whose action does not apply to that row - keeps
        /// the ordinary arrow rather than promising something it will not do.
        /// </summary>
        public static void HandOverColumns(ListView list, Func<ListViewItem, int, bool> isLive,
                                           params int[] columns)
        {
            if (list == null || columns == null || columns.Length == 0) return;

            var wanted = new HashSet<int>(columns);

            list.MouseMove += (s, e) =>
            {
                Cursor want = Cursors.Default;
                try
                {
                    var hit = list.HitTest(e.Location);
                    if (hit.Item != null && hit.SubItem != null)
                    {
                        int col = hit.Item.SubItems.IndexOf(hit.SubItem);

                        // An empty cell is not a button even in a button column:
                        // rows that offer no action leave theirs blank.
                        if (wanted.Contains(col)
                            && !string.IsNullOrWhiteSpace(hit.SubItem.Text)
                            && (isLive == null || isLive(hit.Item, col)))
                            want = Cursors.Hand;
                    }
                }
                catch { }

                if (list.Cursor != want) list.Cursor = want;
            };

            // Leaving by any route puts it back - without this the hand can be
            // left behind when the pointer exits over a button cell.
            list.MouseLeave += (s, e) => list.Cursor = Cursors.Default;
        }

        /// <summary>
        /// Controls where a click DOES something.
        ///
        /// Text boxes are deliberately excluded - they take a caret, and the
        /// I-beam is what tells the player they can type. A ListView is excluded
        /// too: most of its surface is rows to read, and the launcher's lists
        /// set the hand themselves on the cells that act as buttons.
        /// </summary>
        private static bool IsClickable(Control c)
        {
            if (!c.Enabled) return false;

            if (c is ButtonBase) return true;          // Button, CheckBox, RadioButton
            if (c is LinkLabel) return true;
            if (c is ComboBox) return true;
            if (c is TabControl) return true;
            if (c is PictureBox) return c.Tag as string == "clickable";

            // A label only counts when something was actually wired to it -
            // the launcher uses a few as links and as the search clear cross.
            if (c is Label) return c.Tag as string == "clickable";

            return false;
        }
    }
}
