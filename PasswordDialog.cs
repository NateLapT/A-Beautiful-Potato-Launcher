// ---------------------------------------------------------------------------
//  Asks for a server password.
//
//  The text is hidden by default and revealed only while SHOW is physically
//  held down - press and it shows, release and it hides again. That is
//  deliberately different from the usual toggle: a toggle left switched on
//  leaves the password sitting on screen for as long as the dialog is open,
//  which is exactly what hiding it was meant to prevent.
// ---------------------------------------------------------------------------

using System;
using System.Drawing;
using System.Windows.Forms;

namespace BeautifulPotatoExpLauncher
{
    internal sealed class PasswordDialog : Form
    {
        private static readonly Color Ink = Color.FromArgb(24, 24, 27);
        private static readonly Color Panel2 = Color.FromArgb(38, 38, 42);
        private static readonly Color Dim = Color.FromArgb(150, 150, 158);

        private readonly TextBox _box;

        public string Password { get { return _box.Text; } }

        private PasswordDialog(string serverName)
        {
            Text = "Password required";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(430, 150);
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

            Controls.Add(new Label
            {
                Text = "This server is password protected.",
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
            Controls.Add(_box);

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
            show.MouseDown += (s, e) => _box.UseSystemPasswordChar = false;
            show.MouseUp += (s, e) => _box.UseSystemPasswordChar = true;
            show.MouseLeave += (s, e) => _box.UseSystemPasswordChar = true;
            show.LostFocus += (s, e) => _box.UseSystemPasswordChar = true;
            Controls.Add(show);

            var ok = new Button
            {
                Text = "Connect",
                Bounds = new Rectangle(228, 100, 92, 30),
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
                Bounds = new Rectangle(328, 100, 86, 30),
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
        public static string Ask(IWin32Window owner, string serverName)
        {
            using (var dlg = new PasswordDialog(serverName))
            {
                if (dlg.ShowDialog(owner) != DialogResult.OK) return null;
                string v = dlg.Password;
                return string.IsNullOrEmpty(v) ? null : v;
            }
        }
    }
}
