// ---------------------------------------------------------------------------
//  Making a ListView repaint without flashing.
//
//  WinForms draws a ListView straight to the screen: it clears each row, then
//  paints it. On a dark interface that clear is a bright flash, and a list that
//  re-renders while servers are arriving flashes several times a second.
//
//  The control supports double buffering perfectly well - the property is just
//  protected, because Microsoft never exposed it on ListView specifically. It
//  is the same DoubleBuffered every other control has, so setting it through
//  reflection is turning on a feature that is already there rather than working
//  around a missing one.
// ---------------------------------------------------------------------------

using System;
using System.Reflection;
using System.Windows.Forms;

namespace ABeautifulPotatoLauncher
{
    internal static class ListViewTweaks
    {
        private static readonly PropertyInfo DoubleBufferedProp =
            typeof(Control).GetProperty("DoubleBuffered",
                                        BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>
        /// Paints this list off-screen and blits it, so a redraw no longer
        /// flashes. Silently does nothing if the property ever disappears -
        /// a list that flickers is far better than one that will not open.
        /// </summary>
        public static void Smooth(Control list)
        {
            if (list == null || DoubleBufferedProp == null) return;
            try { DoubleBufferedProp.SetValue(list, true, null); }
            catch { }
        }
    }
}
