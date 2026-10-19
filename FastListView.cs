// ---------------------------------------------------------------------------
//  A ListView that draws its coloured rows quickly.
//
//  WHY
//    A row whose cells have their own colours (UseItemStyleForSubItems off) is
//    drawn by WinForms one cell at a time. For every cell, on every repaint,
//    Windows sends a message for its colours and another for its text, each a
//    trip through WinForms and back - and WinForms answers the first by making
//    a brand new Windows font from the cell's Font and throwing the last one
//    away. Twenty rows of fourteen columns came to 100-200 ms per repaint of
//    the server list, against 1-6 ms for the plain mod list beside it, and
//    everything that repaints the list paid it: scrolling, servers arriving,
//    typing a filter, opening the filter panel.
//
//  WHAT THIS DOES
//    Draws each row whole, in answer to the one message Windows sends per
//    row, and tells Windows to skip it - with the same colours, insets,
//    selection and focus outline as Windows' own drawing, checked pixel for
//    pixel against it. Fonts and brushes are made once and kept.
//
//    Should a row ever not be drawable that way, it falls back to Windows'
//    cell-by-cell drawing, with the font taken from the same cache. Owner
//    drawn and hot-tracking lists are left to WinForms entirely.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ABeautifulPotatoLauncher
{
    internal class FastListView : ListView
    {
        private const int WM_REFLECT_NOTIFY = 0x2000 + 0x004E;
        private const int NM_CUSTOMDRAW = -12;
        private const int CDDS_PREPAINT = 0x00000001;
        private const int CDDS_ITEMPREPAINT = 0x00010001;
        private const int CDDS_SUBITEMPREPAINT = 0x00010001 | 0x00020000;
        private const int CDIS_SELECTED = 0x0001, CDIS_FOCUS = 0x0010;
        private const int CDIS_ANY_HIGHLIGHT = 0x0047;     // selected, grayed, disabled, hot
        private const int CDRF_NEWFONT = 0x0002, CDRF_SKIPDEFAULT = 0x0004;
        private const int LVM_GETITEMRECT = 0x1000 + 14;
        private const int LVIR_BOUNDS = 0;
        private const int TRANSPARENT = 1;
        private const int DT_LEFT = 0x0, DT_CENTER = 0x1, DT_RIGHT = 0x2, DT_VCENTER = 0x4,
                          DT_SINGLELINE = 0x20, DT_NOPREFIX = 0x800, DT_END_ELLIPSIS = 0x8000;
        private const int LVM_GETITEMSTATE = 0x1000 + 44;
        private const int LVIS_SELECTED = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        private struct NMHDR { public IntPtr hwndFrom; public IntPtr idFrom; public int code; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct NMCUSTOMDRAW
        {
            public NMHDR hdr;
            public int dwDrawStage;
            public IntPtr hdc;
            public RECT rc;
            public IntPtr dwItemSpec;
            public int uItemState;
            public IntPtr lItemlParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NMLVCUSTOMDRAW
        {
            public NMCUSTOMDRAW nmcd;
            public int clrText;
            public int clrTextBk;
            public int iSubItem;
        }

        private static readonly int OffClrText = Marshal.OffsetOf(typeof(NMLVCUSTOMDRAW), "clrText").ToInt32();
        private static readonly int OffClrTextBk = Marshal.OffsetOf(typeof(NMLVCUSTOMDRAW), "clrTextBk").ToInt32();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr obj);

        // Keyed by value - the heading rows make a new (identical) Font object
        // each time they are built, and that must not mean a new handle each.
        private readonly Dictionary<Font, IntPtr> _fonts = new Dictionary<Font, IntPtr>();

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_REFLECT_NOTIFY && !OwnerDraw && !HotTracking)
            {
                var hdr = (NMHDR)Marshal.PtrToStructure(m.LParam, typeof(NMHDR));
                if (hdr.code == NM_CUSTOMDRAW)
                {
                    var cd = (NMLVCUSTOMDRAW)Marshal.PtrToStructure(m.LParam, typeof(NMLVCUSTOMDRAW));

                    // Each column's width is a message to Windows to read, so
                    // they are read once per repaint rather than once per row.
                    if (cd.nmcd.dwDrawStage == CDDS_PREPAINT) ReadColumnWidths();

                    if (View == View.Details && cd.nmcd.dwDrawStage == CDDS_ITEMPREPAINT && DrawRow(ref m, cd)) return;
                    if (cd.nmcd.dwDrawStage == CDDS_SUBITEMPREPAINT && DrawCell(ref m, cd)) return;
                }
            }
            base.WndProc(ref m);
        }

        /// <summary>
        /// Handles the per-cell step of a custom-drawn row. False for anything
        /// else, which then goes to WinForms as usual.
        /// </summary>
        private bool DrawCell(ref Message m, NMLVCUSTOMDRAW cd)
        {
            try
            {
                int index = (int)cd.nmcd.dwItemSpec;
                int state = cd.nmcd.uItemState;

                // WinForms' rule: unless the selection is hidden, only a row
                // that really is selected is drawn as selected.
                if (!HideSelection && (state & CDIS_SELECTED) != 0)
                {
                    int real = (int)SendMessage(Handle, LVM_GETITEMSTATE, new IntPtr(index), new IntPtr(LVIS_SELECTED));
                    if ((real & LVIS_SELECTED) == 0) state &= ~CDIS_SELECTED;
                }

                ListViewItem item = index >= 0 && index < Items.Count ? Items[index] : null;
                Font font = null;
                Color fore = Color.Empty, back = Color.Empty;

                if (item != null && cd.iSubItem < item.SubItems.Count)
                {
                    var cell = item.SubItems[cd.iSubItem];
                    font = cell.Font;

                    // The first cell of a highlighted row keeps the highlight's
                    // colours; every other cell has its own.
                    if (cd.iSubItem > 0 || (state & CDIS_ANY_HIGHLIGHT) == 0)
                    {
                        fore = cell.ForeColor;
                        back = cell.BackColor;
                    }
                }

                // As WinForms: no colours of its own on a disabled list, or on a
                // highlighted row of a click-to-activate one.
                bool coloured = Enabled && (Activation == ItemActivation.Standard
                                            || (state & CDIS_ANY_HIGHLIGHT) == 0);
                if (coloured)
                {
                    int text = ColorTranslator.ToWin32(fore.IsEmpty ? ForeColor : fore);

                    // Exactly the hot-track colour makes Windows draw the text
                    // as a link; one step off it does not show.
                    if (text == ColorTranslator.ToWin32(SystemColors.HotTrack))
                        text = (text & 0xFF) > 0 ? text - 1 : text + 1;

                    Marshal.WriteInt32(m.LParam, OffClrText, text);
                    Marshal.WriteInt32(m.LParam, OffClrTextBk, ColorTranslator.ToWin32(back.IsEmpty ? BackColor : back));
                }

                SelectObject(cd.nmcd.hdc, FontHandleFor(font ?? Font));
                m.Result = new IntPtr(CDRF_NEWFONT);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Draws a whole row in one go and tells Windows to skip it.
        ///
        /// Left to Windows, a row of fourteen columns is a conversation of
        /// about thirty messages - the row, then each cell's colours, then each
        /// cell's text - and every message is a trip through WinForms and back.
        /// Here it is one message, and the drawing is plain GDI.
        /// </summary>
        private bool DrawRow(ref Message m, NMLVCUSTOMDRAW cd)
        {
            int index = (int)cd.nmcd.dwItemSpec;
            if (index < 0 || index >= Items.Count) return false;

            ListViewItem item;
            try { item = Items[index]; }
            catch { return false; }
            if (item == null) return false;

            var row = new RECT { left = LVIR_BOUNDS };
            if (SendMessage(Handle, LVM_GETITEMRECT, new IntPtr(index), ref row) == IntPtr.Zero) return false;

            int state = cd.nmcd.uItemState;
            bool selected = (state & CDIS_SELECTED) != 0
                && ((int)SendMessage(Handle, LVM_GETITEMSTATE, new IntPtr(index), new IntPtr(LVIS_SELECTED)) & LVIS_SELECTED) != 0;
            bool active = Focused;

            IntPtr hdc = cd.nmcd.hdc;
            int saved = SaveDC(hdc);
            try
            {
                SetBkMode(hdc, TRANSPARENT);

                int width = ClientSize.Width;
                int[] widths = _widths != null && _widths.Length == Columns.Count ? _widths : ReadColumnWidths();
                int x = row.left;
                Color rowBack = BackColor;
                for (int c = 0; c < widths.Length; c++)
                {
                    int w = widths[c];
                    var cell = new RECT { left = x, top = row.top, right = x + w, bottom = row.bottom };
                    x += w;

                    // Windows leaves the first few pixels of a row to the list's
                    // own background, selected or not.
                    if (c == 0) cell.left += FirstGap;
                    if (w <= 0 || cell.right <= 0 || cell.left >= width || cell.right <= cell.left) continue;

                    var sub = c < item.SubItems.Count ? item.SubItems[c] : null;
                    var style = item.UseItemStyleForSubItems || sub == null ? item.SubItems[0] : sub;

                    Color fore, back;
                    if (selected)
                    {
                        fore = active ? SystemColors.HighlightText : SystemColors.ControlText;
                        back = active ? SystemColors.Highlight : SystemColors.Control;
                    }
                    else
                    {
                        fore = style.ForeColor.IsEmpty ? ForeColor : style.ForeColor;
                        back = style.BackColor.IsEmpty ? BackColor : style.BackColor;
                    }

                    FillRect(hdc, ref cell, BrushFor(back));
                    if (c == 0) rowBack = back;

                    string text = sub == null ? "" : sub.Text;
                    if (string.IsNullOrEmpty(text)) continue;

                    // The first column is always left-aligned in a details
                    // list, whatever it is set to - Windows' rule, kept.
                    int align = c == 0 ? DT_LEFT
                              : Columns[c].TextAlign == HorizontalAlignment.Center ? DT_CENTER
                              : Columns[c].TextAlign == HorizontalAlignment.Right ? DT_RIGHT : DT_LEFT;

                    var area = new RECT
                    {
                        left = cell.left + (c == 0 ? FirstPad - FirstGap : CellPad),
                        top = cell.top,
                        right = cell.right - CellPad,
                        bottom = cell.bottom
                    };
                    if (area.right <= area.left) continue;

                    SelectObject(hdc, FontHandleFor(style.Font ?? Font));
                    SetTextColor(hdc, ColorTranslator.ToWin32(fore));
                    DrawText(hdc, text, text.Length, ref area,
                             align | DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX | DT_END_ELLIPSIS);
                }

                if (active && (state & CDIS_FOCUS) != 0 && ShowFocusCues)
                    DrawFocusDots(hdc, row.left + FirstGap, row.top, Math.Min(x, width), row.bottom, rowBack);
            }
            finally
            {
                RestoreDC(hdc, saved);
            }

            m.Result = new IntPtr(CDRF_SKIPDEFAULT);
            return true;
        }

        /// <summary>
        /// Insets, measured against Windows' own drawing of the same list: the
        /// first column's background starts FirstGap in, its text FirstPad in,
        /// every other column's text CellPad in.
        /// </summary>
        private const int FirstGap = 4, FirstPad = 6, CellPad = 6;

        private int[] _widths;

        private int[] ReadColumnWidths()
        {
            var widths = new int[Columns.Count];
            for (int c = 0; c < widths.Length; c++) widths[c] = Columns[c].Width;
            _widths = widths;
            return widths;
        }

        /// <summary>
        /// The keyboard-focus outline, dotted as Windows draws it: every other
        /// pixel, in the inverse of the colour underneath. Drawn dot by dot
        /// rather than with DrawFocusRect, which inverts what is there - and
        /// a row drawn twice in one repaint would cancel itself out.
        /// </summary>
        private void DrawFocusDots(IntPtr hdc, int left, int top, int right, int bottom, Color under)
        {
            if (right - left < 2 || bottom - top < 2) return;
            IntPtr brush = BrushFor(Color.FromArgb(255 - under.R, 255 - under.G, 255 - under.B));

            Action<int, int> dot = (px, py) =>
            {
                if (((px + py) & 1) == 0) return;
                var r = new RECT { left = px, top = py, right = px + 1, bottom = py + 1 };
                FillRect(hdc, ref r, brush);
            };

            for (int px = left; px < right; px++) { dot(px, top); dot(px, bottom - 1); }
            for (int py = top + 1; py < bottom - 1; py++) { dot(left, py); dot(right - 1, py); }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref RECT lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "DrawTextW")]
        private static extern int DrawText(IntPtr hdc, string text, int length, ref RECT rect, int format);

        [DllImport("user32.dll")]
        private static extern int FillRect(IntPtr hdc, ref RECT rect, IntPtr brush);

        [DllImport("gdi32.dll")]
        private static extern int SetTextColor(IntPtr hdc, int color);

        [DllImport("gdi32.dll")]
        private static extern int SetBkMode(IntPtr hdc, int mode);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateSolidBrush(int color);

        [DllImport("gdi32.dll")]
        private static extern int SaveDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool RestoreDC(IntPtr hdc, int saved);

        private readonly Dictionary<int, IntPtr> _brushes = new Dictionary<int, IntPtr>();

        private IntPtr BrushFor(Color color)
        {
            int key = ColorTranslator.ToWin32(color);
            IntPtr b;
            if (_brushes.TryGetValue(key, out b)) return b;

            b = CreateSolidBrush(key);
            _brushes[key] = b;
            return b;
        }

        private IntPtr FontHandleFor(Font font)
        {
            IntPtr h;
            if (_fonts.TryGetValue(font, out h)) return h;

            h = font.ToHfont();
            _fonts[font] = h;
            return h;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            foreach (IntPtr h in _fonts.Values) DeleteObject(h);
            _fonts.Clear();
            foreach (IntPtr h in _brushes.Values) DeleteObject(h);
            _brushes.Clear();
        }
    }
}
