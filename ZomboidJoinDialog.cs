// ---------------------------------------------------------------------------
//  Joining a Project Zomboid server: which account, and how.
//
//  The game itself takes only the server address and the server password on
//  its command line. Everything else its connect window shows - the account
//  name, that account's saved password, Steam Relay - comes from the accounts
//  it has saved for the server, and it always opens with the first of them.
//  This asks the player which one they mean, so ZomboidAccounts can put that
//  one first before the game starts. See ZomboidAccounts for the why.
//
//  The account PASSWORD is never asked for here: the game keeps those itself,
//  as hashes, and asks for a new account's password the first time.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace ABeautifulPotatoLauncher
{
    internal sealed class ZomboidJoinDialog : Form
    {
        private static readonly Color Ink = Color.FromArgb(24, 24, 27);
        private static readonly Color Panel2 = Color.FromArgb(38, 38, 42);
        private static readonly Color Dim = Color.FromArgb(150, 150, 158);

        private const string NewAccountItem = "New account...";

        private readonly List<ZomboidAccount> _accounts;
        private readonly TextBox _serverPassword;
        private readonly ComboBox _account;
        private readonly TextBox _newName;
        private readonly CheckBox _relay;
        private readonly Button _join;

        /// <summary>What the player chose. Account null with NewUsername set means a new one.</summary>
        internal sealed class Choice
        {
            public string ServerPassword;
            public ZomboidAccount Account;
            public string NewUsername;
            public bool SteamRelay;

            /// <summary>False when the saved accounts could not be read; the game then asks.</summary>
            public bool AccountsUsable;

            public string Username { get { return Account != null ? Account.Username : NewUsername; } }
        }

        private ZomboidJoinDialog(string serverName, bool needsPassword, List<ZomboidAccount> accounts,
                                  string problem, string preferred)
        {
            _accounts = accounts ?? new List<ZomboidAccount>();
            bool usable = accounts != null;

            Text = "Join server";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            BackColor = Ink;
            ForeColor = Color.Gainsboro;
            Font = new Font("Segoe UI", 9f);

            int y = 12;
            Controls.Add(new Label
            {
                Text = serverName,
                Bounds = new Rectangle(14, y, 440, 20),
                ForeColor = Color.White,
                UseMnemonic = false,
                AutoEllipsis = true,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            });
            y += 30;

            // ---- server password, only when the server has one ----
            if (needsPassword)
            {
                Controls.Add(new Label { Text = "Server password", Bounds = new Rectangle(14, y + 3, 120, 18), ForeColor = Dim });
                _serverPassword = new TextBox
                {
                    Bounds = new Rectangle(140, y, 230, 24),
                    BackColor = Panel2,
                    ForeColor = Color.Gainsboro,
                    BorderStyle = BorderStyle.FixedSingle,
                    UseSystemPasswordChar = true
                };
                Controls.Add(_serverPassword);

                // Held, not toggled - see PasswordDialog for why.
                var show = new Button
                {
                    Text = "Show",
                    Bounds = new Rectangle(378, y - 1, 76, 26),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Panel2,
                    ForeColor = Color.White,
                    TabStop = false
                };
                show.FlatAppearance.BorderColor = Color.FromArgb(75, 75, 82);
                show.MouseDown += (s, e) => _serverPassword.UseSystemPasswordChar = false;
                show.MouseUp += (s, e) => _serverPassword.UseSystemPasswordChar = true;
                show.MouseLeave += (s, e) => _serverPassword.UseSystemPasswordChar = true;
                show.LostFocus += (s, e) => _serverPassword.UseSystemPasswordChar = true;
                Controls.Add(show);
                y += 36;
            }

            // ---- which account ----
            Controls.Add(new Label { Text = "Account", Bounds = new Rectangle(14, y + 3, 120, 18), ForeColor = Dim });
            _account = new ComboBox
            {
                Bounds = new Rectangle(140, y, 314, 24),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Panel2,
                ForeColor = Color.Gainsboro,
                FlatStyle = FlatStyle.Flat,
                Enabled = usable
            };
            foreach (var a in _accounts) _account.Items.Add(Describe(a));
            _account.Items.Add(NewAccountItem);
            Controls.Add(_account);
            y += 32;

            Controls.Add(new Label { Text = "New account name", Bounds = new Rectangle(14, y + 3, 120, 18), ForeColor = Dim });
            _newName = new TextBox
            {
                Bounds = new Rectangle(140, y, 314, 24),
                BackColor = Panel2,
                ForeColor = Color.Gainsboro,
                BorderStyle = BorderStyle.FixedSingle,
                Enabled = false
            };
            Controls.Add(_newName);
            y += 34;

            _relay = new CheckBox
            {
                Text = "Use Steam Relay",
                Location = new Point(140, y),
                AutoSize = true,
                ForeColor = Color.Gainsboro,
                Enabled = usable
            };
            Controls.Add(_relay);
            new ToolTip { AutoPopDelay = 15000 }.SetToolTip(_relay,
                "Connects through Steam's network instead of directly. Slower, but gets "
                + "through some home routers and hides your address from the server.");
            y += 30;

            var note = new Label
            {
                Text = usable
                    ? "The game opens its connect window with this account filled in - "
                      + "press Connect there. A new account's password is asked for once, by the game."
                    : "The game's saved accounts could not be read (" + problem + "), so the game "
                      + "will ask for your account in its connect window.",
                Bounds = new Rectangle(14, y, 440, 36),
                ForeColor = Dim
            };
            Controls.Add(note);
            y += 44;

            _join = new Button
            {
                Text = "Join",
                Bounds = new Rectangle(268, y, 92, 30),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(60, 95, 60),
                ForeColor = Color.White,
                DialogResult = DialogResult.OK
            };
            _join.FlatAppearance.BorderColor = Color.FromArgb(80, 120, 80);
            Controls.Add(_join);

            var cancel = new Button
            {
                Text = "Cancel",
                Bounds = new Rectangle(368, y, 86, 30),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(70, 55, 55),
                ForeColor = Color.White,
                DialogResult = DialogResult.Cancel
            };
            cancel.FlatAppearance.BorderColor = Color.FromArgb(100, 70, 70);
            Controls.Add(cancel);

            ClientSize = new Size(468, y + 44);
            AcceptButton = _join;
            CancelButton = cancel;

            _account.SelectedIndexChanged += (s, e) => SyncToAccount();
            _newName.TextChanged += (s, e) => UpdateJoin();
            if (_serverPassword != null) _serverPassword.TextChanged += (s, e) => UpdateJoin();

            // The account last chosen here for this server; failing that the one
            // played most recently; failing that a new one.
            int pick = _accounts.FindIndex(a => string.Equals(a.Username, preferred, StringComparison.Ordinal));
            if (pick < 0 && _accounts.Count > 0)
                pick = _accounts.IndexOf(_accounts.OrderByDescending(a => a.LastLogon).First());
            _account.SelectedIndex = pick >= 0 ? pick : _account.Items.Count - 1;
            SyncToAccount();

            FormClosing += (s, e) => { if (_serverPassword != null) _serverPassword.UseSystemPasswordChar = true; };
            Shown += (s, e) =>
            {
                if (_serverPassword != null) _serverPassword.Focus();
                else if (_newName.Enabled) _newName.Focus();
                else _join.Focus();
            };
        }

        private static string Describe(ZomboidAccount a)
        {
            string when = a.LastLogon == DateTime.MinValue ? "never played"
                        : "last played " + a.LastLogon.ToString("yyyy-MM-dd");
            return a.Username + "   (" + when + (a.PasswordSaved ? ", password saved" : ", no saved password") + ")";
        }

        private bool IsNew { get { return _account.SelectedIndex >= _accounts.Count; } }

        private void SyncToAccount()
        {
            _newName.Enabled = _account.Enabled && IsNew;
            if (!IsNew && _account.SelectedIndex >= 0) _relay.Checked = _accounts[_account.SelectedIndex].SteamRelay;
            UpdateJoin();
        }

        private void UpdateJoin()
        {
            bool ok = true;
            if (_serverPassword != null && _serverPassword.Text.Length == 0) ok = false;
            _join.Enabled = ok;
        }

        private Choice Result()
        {
            var c = new Choice
            {
                ServerPassword = _serverPassword == null ? null : _serverPassword.Text,
                SteamRelay = _relay.Checked,
                AccountsUsable = _account.Enabled
            };
            if (!c.AccountsUsable) return c;

            if (IsNew) c.NewUsername = _newName.Text.Trim();
            else c.Account = _accounts[_account.SelectedIndex];
            return c;
        }

        /// <summary>
        /// Asks how to join. Null when the player cancelled. A choice with no
        /// account and no new name means "let the game ask".
        /// </summary>
        public static Choice Ask(IWin32Window owner, string serverName, bool needsPassword,
                                 List<ZomboidAccount> accounts, string problem, string preferred)
        {
            using (var dlg = new ZomboidJoinDialog(serverName, needsPassword, accounts, problem, preferred))
            {
                if (dlg.ShowDialog(owner) != DialogResult.OK) return null;
                return dlg.Result();
            }
        }
    }
}
