// ---------------------------------------------------------------------------
//  "Install this workshop item."
//
//  Reached from the Mod Manager. The player has a workshop id - from a server
//  admin, a forum post, or the address bar of a workshop page - and wants the
//  mod on disk without hunting for it in Steam.
//
//  Accepts a bare id or a pasted workshop URL, because the id is usually
//  arrived at by copying the link, and making somebody extract the number by
//  hand from a URL they already have is busywork.
// ---------------------------------------------------------------------------

using System;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace ABeautifulPotatoLauncher
{
    internal static class InstallByIdDialog
    {
        private static readonly Color Ink = Color.FromArgb(24, 24, 27);
        private static readonly Color Panel2 = Color.FromArgb(38, 38, 42);
        private static readonly Color Dim = Color.FromArgb(150, 150, 158);

        /// <summary>
        /// Asks for a workshop id. Returns 0 when cancelled or nothing usable
        /// was entered.
        /// </summary>
        public static ulong Ask(IWin32Window owner)
        {
            using (var f = new Form())
            {
                f.Text = "Install from Workshop ID";
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.MinimizeBox = false;
                f.MaximizeBox = false;
                f.ShowInTaskbar = false;
                f.ClientSize = new Size(470, 176);
                f.BackColor = Ink;
                f.ForeColor = Color.Gainsboro;
                f.Font = new Font("Segoe UI", 9f);

                f.Controls.Add(new Label
                {
                    Text = "Workshop ID",
                    Bounds = new Rectangle(14, 14, 200, 18),
                    ForeColor = Color.Gainsboro,
                    Font = new Font("Segoe UI", 9f, FontStyle.Bold)
                });

                var box = new TextBox
                {
                    Bounds = new Rectangle(14, 36, 442, 23),
                    BackColor = Panel2,
                    ForeColor = Color.Gainsboro,
                    BorderStyle = BorderStyle.FixedSingle
                };
                f.Controls.Add(box);

                f.Controls.Add(new Label
                {
                    Text = "The number from a workshop address, for example 3805561602.\r\n"
                         + "Pasting the whole link works too - the id is taken out of it.",
                    Bounds = new Rectangle(14, 64, 442, 34),
                    ForeColor = Dim
                });

                var warning = new Label
                {
                    Bounds = new Rectangle(14, 100, 442, 18),
                    ForeColor = Color.FromArgb(225, 175, 90)
                };
                f.Controls.Add(warning);

                var install = new Button
                {
                    Text = "INSTALL",
                    Bounds = new Rectangle(f.ClientSize.Width - 212, 132, 96, 28),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(60, 95, 60),
                    ForeColor = Color.White,
                    Cursor = Cursors.Hand,
                    DialogResult = DialogResult.OK
                };
                install.FlatAppearance.BorderColor = Color.FromArgb(80, 120, 80);
                f.Controls.Add(install);

                var cancel = new Button
                {
                    Text = "Cancel",
                    Bounds = new Rectangle(f.ClientSize.Width - 106, 132, 92, 28),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Panel2,
                    ForeColor = Color.White,
                    Cursor = Cursors.Hand,
                    DialogResult = DialogResult.Cancel
                };
                cancel.FlatAppearance.BorderColor = Color.FromArgb(75, 75, 82);
                f.Controls.Add(cancel);

                // Live feedback rather than a rejection after pressing INSTALL:
                // the id is long and mistyping one is easy.
                box.TextChanged += (s, e) =>
                {
                    ulong id = Parse(box.Text);
                    bool typed = box.Text.Trim().Length > 0;

                    install.Enabled = id != 0;
                    warning.Text = !typed ? ""
                                 : id != 0 ? "Will install item " + id
                                 : "That does not contain a workshop id.";
                    warning.ForeColor = id != 0
                        ? Color.FromArgb(110, 200, 130)
                        : Color.FromArgb(225, 175, 90);
                };

                install.Enabled = false;
                f.AcceptButton = install;
                f.CancelButton = cancel;
                UiCursors.ApplyTo(f);

                box.Focus();

                return f.ShowDialog(owner) == DialogResult.OK ? Parse(box.Text) : 0;
            }
        }

        /// <summary>
        /// The workshop id in whatever was typed - a bare number, or a URL with
        /// "?id=" in it. Zero when there is nothing usable.
        /// </summary>
        public static ulong Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;

            string s = text.Trim();

            // A pasted workshop link.
            var m = Regex.Match(s, @"[?&]id=(\d{4,20})", RegexOptions.IgnoreCase);
            if (m.Success) s = m.Groups[1].Value;

            // Anything else has to be the number on its own. Deliberately not
            // "the first number found anywhere" - that would happily pull a
            // digit out of a sentence and install something unrelated.
            if (!Regex.IsMatch(s, @"^\d{4,20}$")) return 0;

            ulong id;
            return ulong.TryParse(s, out id) ? id : 0;
        }
    }
}
