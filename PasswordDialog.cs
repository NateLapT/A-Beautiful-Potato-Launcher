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
//  sitting at the PC. Unticking "Remember" forgets it.
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

        /// <summary>True while the box still holds the remembered password, untouched.</summary>
        private bool _filledIn;

        public string Password { get { return _box.Text; } }
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
                Text = haveSaved ? "This server is password protected. Your last password is filled in."
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
                UseSystemPasswordChar = true
            };
            if (haveSaved)
            {
                _box.Text = saved;
                _filledIn = true;
            }
            // Any edit makes it the player's own typing again, which SHOW may reveal.
            _box.TextChanged += (s, e) => _filledIn = false;
            Controls.Add(_box);

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
            show.MouseDown += (s, e) => { if (!_filledIn) _box.UseSystemPasswordChar = false; };
            show.MouseUp += (s, e) => _box.UseSystemPasswordChar = true;
            show.MouseLeave += (s, e) => _box.UseSystemPasswordChar = true;
            show.LostFocus += (s, e) => _box.UseSystemPasswordChar = true;
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

            // Never leave it revealed when the dialog closes.
            FormClosing += (s, e) => _box.UseSystemPasswordChar = true;
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
