// ---------------------------------------------------------------------------
//  Asks for a server password.
//
//  The text is hidden by default and revealed only while SHOW is physically
//  held down - press and it shows, release and it hides again. That is
//  deliberately different from the usual toggle: a toggle left switched on
//  leaves the password sitting on screen for as long as the dialog is open,
//  which is exactly what hiding it was meant to prevent.
//
//  REMEMBERED PASSWORDS. The last password used for a server is filled in
//  next time (encrypted on disk - see ServerStore.LoadPassword). SHOW does
//  nothing for a filled-in one: it reveals only what the player types here,
//  so a saved password can never be read off the screen by whoever is
//  sitting at the PC. Unticking "Remember" forgets it. With one filled in,
//  "Auto join" (ticked by default) joins by itself after five seconds.
//
//  THE LENGTH IS NEVER SHOWN EITHER. A password box shows a dot per
//  character, and that alone tells an onlooker (or a stream) how long the
//  password is. Here the box always shows the same twelve asterisks once
//  anything is in it, whatever the length. The password itself is kept
//  out of the box: typing, Backspace and pasting edit it here, and the box
//  is only ever given the mask (or, while SHOW is held, the typed text).
// ---------------------------------------------------------------------------

using System;
using System.Drawing;
using System.Windows.Forms;

namespace ABeautifulPotatoLauncher
{
    internal sealed class PasswordDialog : Form
    {
        private static readonly Color Ink = Color.FromArgb(24, 24, 27);
        private static readonly Color Panel2 = Color.FromArgb(38, 38, 42);
        private static readonly Color Dim = Color.FromArgb(150, 150, 158);

        private readonly TextBox _box;
        private readonly CheckBox _remember;

        /// <summary>What every password looks like on screen - the same whatever its length.</summary>
        private const string Mask = "************";

        /// <summary>The password itself. The box only ever shows Mask, or this while SHOW is held.</summary>
        private string _value = "";

        /// <summary>True while SHOW is held down.</summary>
        private bool _showing;

        /// <summary>True while the box still holds the remembered password, untouched.</summary>
        private bool _filledIn;

        /// <summary>
        /// True from a click (or Tab) into the box until it is left. The box is
        /// emptied on entering: whatever is typed is a new password. Left
        /// without typing anything, the password from before comes back.
        /// </summary>
        private bool _entering;
        private string _before = "";
        private bool _beforeFilledIn;

        /// <summary>What Connect sends: what was typed, or the password from before when nothing was.</summary>
        public string Password { get { return _entering && _value.Length == 0 ? _before : _value; } }
        public bool Remember { get { return _remember.Checked; } }

        private PasswordDialog(string serverName, string saved)
        {
            Text = "Password required";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(430, 172);
            BackColor = Ink;
            ForeColor = Color.Gainsboro;
            Font = new Font("Segoe UI", 9f);

            Controls.Add(new Label
            {
                Text = serverName,
                Bounds = new Rectangle(14, 12, 400, 20),
                ForeColor = Color.White,
                UseMnemonic = false,           // a "&" in a server name is not a shortcut
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            });

            bool haveSaved = !string.IsNullOrEmpty(saved);
            Controls.Add(new Label
            {
                Text = haveSaved ? "Last password filled in. Click the box to type a new one."
                                 : "This server is password protected.",
                Bounds = new Rectangle(14, 34, 400, 18),
                ForeColor = Dim
            });

            _box = new TextBox
            {
                Bounds = new Rectangle(14, 60, 310, 24),
                BackColor = Panel2,
                ForeColor = Color.Gainsboro,
                BorderStyle = BorderStyle.FixedSingle,
                // No built-in cut/copy/paste or right-click menu: the box only
                // holds the mask, and pasting is handled below.
                ShortcutsEnabled = false,
                ContextMenu = new ContextMenu()
            };
            if (haveSaved)
            {
                _value = saved;
                _filledIn = true;
            }
            _box.KeyPress += OnBoxKeyPress;
            _box.KeyDown += OnBoxKeyDown;
            _box.Enter += (s, e) => StartNewPassword();
            _box.MouseDown += (s, e) => { if (!_entering) StartNewPassword(); };
            _box.Leave += (s, e) => EndNewPassword();
            Controls.Add(_box);
            ShowValue();

            _remember = new CheckBox
            {
                Text = "Remember password",
                Checked = true,
                Bounds = new Rectangle(14, 92, 300, 22),
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.Gainsboro
            };
            new ToolTip().SetToolTip(_remember,
                "Saved encrypted for your Windows account only, and filled in next time. Untick to forget it.");
            Controls.Add(_remember);

            var show = new Button
            {
                Text = "Show",
                Bounds = new Rectangle(332, 59, 82, 26),
                FlatStyle = FlatStyle.Flat,
                BackColor = Panel2,
                ForeColor = Color.White,
                TabStop = false                // holding it must not steal the caret
            };
            show.FlatAppearance.BorderColor = Color.FromArgb(75, 75, 82);

            // Held, not toggled - and every way of letting go is covered, including
            // the pointer sliding off the button while still down.
            show.MouseDown += (s, e) => { if (!_filledIn) { _showing = true; ShowValue(); } };
            show.MouseUp += (s, e) => HideTyped();
            show.MouseLeave += (s, e) => HideTyped();
            show.LostFocus += (s, e) => HideTyped();
            if (haveSaved) new ToolTip().SetToolTip(show, "A remembered password stays hidden. Type a new one to see it.");
            Controls.Add(show);

            var ok = new Button
            {
                Text = "Connect",
                Bounds = new Rectangle(228, 126, 92, 30),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(60, 95, 60),
                ForeColor = Color.White,
                DialogResult = DialogResult.OK
            };
            ok.FlatAppearance.BorderColor = Color.FromArgb(80, 120, 80);
            Controls.Add(ok);

            var cancel = new Button
            {
                Text = "Cancel",
                Bounds = new Rectangle(328, 126, 86, 30),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(70, 55, 55),
                ForeColor = Color.White,
                DialogResult = DialogResult.Cancel
            };
            cancel.FlatAppearance.BorderColor = Color.FromArgb(100, 70, 70);
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;

            // AUTO JOIN. With a password filled in, the dialog joins by itself
            // after AutoJoinSeconds, counting down on Connect. Unticking it, or
            // clicking into the box to type a new password, stops the count.
            if (haveSaved)
            {
                _connect = ok;
                _autoJoin = new CheckBox
                {
                    Text = "Auto join",
                    Checked = true,
                    Bounds = new Rectangle(14, 130, 150, 22),
                    FlatStyle = FlatStyle.Flat,
                    ForeColor = Color.Gainsboro
                };
                new ToolTip().SetToolTip(_autoJoin,
                    "Join with the last password in " + AutoJoinSeconds + " seconds. Untick to stay here.");
                _autoJoin.CheckedChanged += (s, e) => { if (!_autoJoin.Checked) StopAutoJoin(); };
                Controls.Add(_autoJoin);

                _left = AutoJoinSeconds;
                _countdown = new Timer { Interval = 1000 };
                _countdown.Tick += (s, e) =>
                {
                    if (--_left > 0) { ShowCountdown(); return; }
                    StopAutoJoin();
                    DialogResult = DialogResult.OK;
                    Close();
                };
                Shown += (s, e) => { ShowCountdown(); _countdown.Start(); };
                FormClosed += (s, e) => _countdown.Dispose();
            }

            // Entering the box empties it, so with a password filled in the
            // dialog starts on Connect: Enter joins with it, a click in the box
            // types a new one.
            if (haveSaved) ActiveControl = ok;

            // Never leave it revealed when the dialog closes.
            FormClosing += (s, e) => HideTyped();
        }

        /// <summary>Seconds before a filled-in password joins by itself.</summary>
        private const int AutoJoinSeconds = 5;
        private CheckBox _autoJoin;
        private Button _connect;
        private Timer _countdown;
        private int _left;

        private void ShowCountdown()
        {
            _connect.Text = "Connect (" + _left + ")";
        }

        /// <summary>No auto join this time: the count stops and Connect reads Connect again.</summary>
        private void StopAutoJoin()
        {
            if (_countdown == null || !_countdown.Enabled) return;
            _countdown.Stop();
            _connect.Text = "Connect";
            if (_autoJoin.Checked) _autoJoin.Checked = false;
        }

        /// <summary>Clicked or tabbed into: the box empties, ready for a new password.</summary>
        private void StartNewPassword()
        {
            StopAutoJoin();
            if (_entering) return;
            _entering = true;
            _before = _value;
            _beforeFilledIn = _filledIn;
            _value = "";
            ShowValue();
        }

        /// <summary>Left: nothing typed puts the password from before back.</summary>
        private void EndNewPassword()
        {
            if (!_entering) return;
            _entering = false;
            if (_value.Length == 0)
            {
                _value = _before;
                _filledIn = _beforeFilledIn;
            }
            _before = "";
            ShowValue();
        }

        /// <summary>Back to the mask (SHOW let go, or the dialog closing).</summary>
        private void HideTyped()
        {
            if (!_showing) return;
            _showing = false;
            ShowValue();
        }

        /// <summary>Puts the mask (or, while SHOW is held, the password) in the box, caret at the end.</summary>
        private void ShowValue()
        {
            _box.Text = _value.Length == 0 ? "" : _showing ? _value : Mask;
            _box.SelectionStart = _box.Text.Length;
            _box.SelectionLength = 0;
        }

        /// <summary>The whole box is selected - typing or pasting replaces it, Backspace clears it.</summary>
        private bool AllSelected
        {
            get { return _box.Text.Length > 0 && _box.SelectionLength == _box.Text.Length; }
        }

        /// <summary>Changes the password itself. Any edit makes it the player's own, which SHOW may reveal.</summary>
        private void SetValue(string v)
        {
            _value = v ?? "";
            _filledIn = false;
            ShowValue();
        }

        private void OnBoxKeyPress(object sender, KeyPressEventArgs e)
        {
            char c = e.KeyChar;
            e.Handled = true;                  // the box never edits its own text
            if (c == '\b')                     // Backspace: the last character, or all when selected
                SetValue(AllSelected ? "" : _value.Substring(0, Math.Max(0, _value.Length - 1)));
            else if (c == (char)127)           // Ctrl+Backspace: all of it
                SetValue("");
            else if (!char.IsControl(c))
                SetValue((AllSelected ? "" : _value) + c);
        }

        private void OnBoxKeyDown(object sender, KeyEventArgs e)
        {
            bool paste = (e.Control && e.KeyCode == Keys.V) || (e.Shift && e.KeyCode == Keys.Insert);
            if (paste)
            {
                e.Handled = e.SuppressKeyPress = true;
                string clip = "";
                try { if (Clipboard.ContainsText()) clip = Clipboard.GetText(); } catch { }
                // One line only - a password never holds a line break.
                int nl = clip.IndexOfAny(new[] { '\r', '\n' });
                if (nl >= 0) clip = clip.Substring(0, nl);
                if (clip.Length > 0) SetValue((AllSelected ? "" : _value) + clip);
            }
            else if (e.Control && e.KeyCode == Keys.A)
            {
                e.Handled = e.SuppressKeyPress = true;
                _box.SelectAll();
            }
            else if (e.KeyCode == Keys.Delete)
            {
                e.Handled = e.SuppressKeyPress = true;
                if (AllSelected) SetValue("");
            }
        }

        /// <summary>
        /// Returns the password, or null when the player cancelled. An empty
        /// box counts as cancelling: a blank password would just be refused by
        /// the server and look like a failure with no cause.
        /// </summary>
        public static string Ask(IWin32Window owner, string serverName, string endpoint)
        {
            using (var dlg = new PasswordDialog(serverName, ServerStore.LoadPassword(endpoint)))
            {
                if (dlg.ShowDialog(owner) != DialogResult.OK) return null;
                string v = dlg.Password;
                if (string.IsNullOrEmpty(v)) return null;

                // Remembered, or forgotten, only once the player has said Connect.
                ServerStore.SavePassword(endpoint, dlg.Remember ? v : null);
                return v;
            }
        }
    }
}
