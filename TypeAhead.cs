// ---------------------------------------------------------------------------
//  Typing in a dropdown narrows its list - done here, not by Windows.
//
//  WHY NOT AutoCompleteSource.ListItems
//    Windows' autocomplete is a shell COM object, and WinForms rebuilds the
//    whole ComboBox window whenever it is attached or the list changes under
//    it. Measured: about 500 ms per rebuild whatever the size - 519 ms for a
//    10-item list - rising to 1.4 s at 800. The filter panel refills three
//    such boxes when it opens, so the first open took 4.3 s; the Find mod box
//    is refilled on every server selected. The same refill without
//    autocomplete takes 11 to 50 ms.
//
//    It was also the crash: rebuilding the list while the COM object was
//    attached could fault inside it and close the launcher with no trace.
//    With nothing attached, that cannot happen.
//
//  WHAT THIS DOES INSTEAD
//    Each box keeps its full list here. As the player types, the box shows
//    only the entries containing what they typed - anywhere, not just at the
//    start, so "fuel" finds "SyphonFuel" - and drops the list open.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace ABeautifulPotatoLauncher
{
    internal static class TypeAhead
    {
        /// <summary>Most entries shown while narrowing; the list is meant to be read.</summary>
        private const int MaxShown = 100;

        private sealed class State
        {
            public string[] All = new string[0];
            public bool Busy;
        }

        private static readonly Dictionary<ComboBox, State> States = new Dictionary<ComboBox, State>();

        public static void Attach(ComboBox box)
        {
            if (box == null || States.ContainsKey(box)) return;
            States[box] = new State();

            // TextUpdate fires for the player's typing only - never for code
            // setting Text - so the refills below cannot loop back into it.
            box.TextUpdate += (s, e) => Narrow(box, true);

            // Emptied - by a pick being added, or the X - so the whole list
            // comes back for the next look. Deferred: this can fire from
            // inside the box's own selection handling, which is no place to
            // swap its items.
            box.TextChanged += (s, e) =>
            {
                State st;
                if (box.Text.Length > 0 || !box.IsHandleCreated) return;
                if (!States.TryGetValue(box, out st) || st.Busy || box.Items.Count == st.All.Length) return;
                box.BeginInvoke((Action)(() => { if (!box.DroppedDown) Narrow(box, false); }));
            };

            box.Disposed += (s, e) => States.Remove(box);
        }

        /// <summary>
        /// Replaces the box's choices, keeping whatever is typed. A box the
        /// player is typing in stays narrowed to what they typed.
        /// </summary>
        public static void SetChoices(ComboBox box, string[] items)
        {
            State st;
            if (box == null || !States.TryGetValue(box, out st) || st.Busy || box.DroppedDown) return;

            st.All = items ?? new string[0];
            Narrow(box, false);
        }

        /// <summary>
        /// How many choices the box holds - all of them, not just the ones
        /// showing while the player's typing narrows the list.
        /// </summary>
        public static int ChoiceCount(ComboBox box)
        {
            State st;
            return box != null && States.TryGetValue(box, out st) ? st.All.Length : 0;
        }

        private static void Narrow(ComboBox box, bool open)
        {
            State st;
            if (!States.TryGetValue(box, out st) || st.Busy) return;

            string text = box.Text ?? "";
            int caret = box.SelectionStart;
            bool narrowing = text.Trim().Length > 0 && (open || box.Focused);

            string[] shown = narrowing
                ? st.All.Where(i => i.IndexOf(text.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
                        .Take(MaxShown).ToArray()
                : st.All;

            st.Busy = true;
            try
            {
                box.BeginUpdate();
                box.Items.Clear();
                if (shown.Length > 0) box.Items.AddRange(shown);
                box.EndUpdate();

                // Opened BEFORE the text goes back: opening the list selects the
                // first match and puts it in the box, which would overwrite
                // what the player is in the middle of typing.
                if (open && narrowing && shown.Length > 0 && !box.DroppedDown)
                {
                    box.DroppedDown = true;
                    Cursor.Current = Cursors.Default;   // opening hides the pointer
                }

                if (box.Text != text) box.Text = text;
                box.SelectionStart = Math.Min(caret, text.Length);
                box.SelectionLength = 0;
            }
            finally
            {
                st.Busy = false;
            }
        }
    }
}
