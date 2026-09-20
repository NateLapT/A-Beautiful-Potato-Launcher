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

using System.Windows.Forms;

namespace BeautifulPotatoExpLauncher
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
