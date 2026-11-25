// ---------------------------------------------------------------------------
//  The launcher's window - the one thing that stays put when the game changes.
//
//  WHY THE WINDOW AND ITS CONTENTS ARE SEPARATE
//    Everything the launcher shows belongs to one game: the list, filters,
//    caches, the mod panel and the Steam session behind them. Switching game
//    used to close the window and open a new one, and even done in the same
//    process that looks exactly like the launcher restarting - the window
//    vanishes, reappears, and the taskbar button blinks.
//
//    So the window is this small form, and the launcher itself (MainForm) is
//    hosted inside it. Switching game builds the other game's MainForm in the
//    same window and swaps it in; the window, its place on screen and its
//    taskbar button never change. In between, the Steam session is moved to
//    the other game's app - see MainForm.BeginGame.
//
//  WHAT LIVES HERE RATHER THAN IN MainForm
//    Anything about the window as a window: its size and position (and
//    remembering them), the minimum size, the icon, the title, and closing -
//    an embedded form is never told the window is closing, so the clean-up it
//    needs is called from here.
// ---------------------------------------------------------------------------

using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ABeautifulPotatoLauncher
{
    internal sealed class LauncherWindow : Form
    {
        private MainForm _content;
        private bool _switching;

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        private const int WM_SETREDRAW = 0x000B;

        [DllImport("user32.dll")]
        private static extern bool RedrawWindow(IntPtr hWnd, IntPtr rect, IntPtr region, uint flags);
        private const uint RDW_INVALIDATE = 0x0001, RDW_ERASE = 0x0004, RDW_ALLCHILDREN = 0x0080, RDW_UPDATENOW = 0x0100;

        private readonly System.Diagnostics.Stopwatch _startClock = System.Diagnostics.Stopwatch.StartNew();
        private bool _revealed;

        public LauncherWindow()
        {
            // NO WHITE FLASH ON START. Windows shows a new window at once and
            // fills every part not yet drawn with white; the launcher's panels
            // each draw a moment later, so it opened as a "flashbang" of white
            // panels on a dark app. The window starts fully transparent, every
            // panel draws once, and only then is it shown - see Reveal.
            Opacity = 0;

            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1180, 800);
            MinimumSize = new Size(1020, 680);
            BackColor = Color.FromArgb(18, 18, 20);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            RestoreWindow();
            ResizeEnd += (s, e) => RememberWindow();

            Program.Trace("LauncherWindow: ctor");
            var content = new MainForm();
            Program.Trace("LauncherWindow: MainForm built");
            Attach(content);
            Program.Trace("LauncherWindow: attached");
            HandleCreated += (s, e) => Program.Trace("LauncherWindow: handle created");
            Load += (s, e) => Program.Trace("LauncherWindow: Load");
            Program.LogLine("Start-up: window built in " + _startClock.ElapsedMilliseconds + " ms.");
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Program.LogLine("Start-up: Shown at " + _startClock.ElapsedMilliseconds + " ms.");
            // After whatever is already queued (the first paints), not inside
            // Shown itself. The timer is the safety net: the window is shown
            // within a second and a half whatever happens.
            BeginInvoke((Action)Reveal);
            var safety = new Timer { Interval = 1500 };
            safety.Tick += (s, a) => { safety.Stop(); safety.Dispose(); Reveal(); };
            safety.Start();
        }

        /// <summary>Paints every panel now, then makes the window visible. Once.</summary>
        private void Reveal()
        {
            if (_revealed || IsDisposed) return;
            _revealed = true;
            try { RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_UPDATENOW); }
            catch { }
            Opacity = 1;
            Program.LogLine("Start-up: window shown after " + _startClock.ElapsedMilliseconds + " ms.");
        }

        /// <summary>Puts a launcher in the window and shows it.</summary>
        private void Attach(MainForm content)
        {
            content.TopLevel = false;
            content.FormBorderStyle = FormBorderStyle.None;
            content.Dock = DockStyle.Fill;
            content.SwitchRequested += g => BeginInvoke((Action)(() => SwitchTo(g)));

            Controls.Add(content);
            content.Show();
            content.BringToFront();

            _content = content;
            Text = content.Text;
        }

        /// <summary>
        /// Swaps in the other game's launcher without the window going anywhere.
        ///
        /// Drawing is held off while the new one is put in place, so the window
        /// goes straight from one game to the other with no half-built frame in
        /// between. Building the new launcher takes a moment - it reads that
        /// game's saved list - so the wait cursor shows meanwhile.
        /// </summary>
        private void SwitchTo(Game game)
        {
            if (_switching || game == Games.Current) return;
            _switching = true;

            var old = _content;
            Cursor = Cursors.WaitCursor;
            SendMessage(Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
            try
            {
                old.Shutdown();
                MainForm.BeginGame(game);

                var fresh = new MainForm();
                Attach(fresh);

                Controls.Remove(old);
                old.Dispose();
            }
            finally
            {
                SendMessage(Handle, WM_SETREDRAW, new IntPtr(1), IntPtr.Zero);
                Refresh();
                Cursor = Cursors.Default;
                _switching = false;
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_content != null) _content.Shutdown();
            RememberWindow();
            base.OnFormClosing(e);
        }

        private void RememberWindow()
        {
            if (WindowState == FormWindowState.Normal)
                ServerStore.SaveWindow(Location, Size);
        }

        private void RestoreWindow()
        {
            Point loc; Size sz;
            if (ServerStore.LoadWindow(out loc, out sz))
            {
                StartPosition = FormStartPosition.Manual;
                Location = loc;
                Size = sz;
            }
        }
    }
}
