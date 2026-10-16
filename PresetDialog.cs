// ---------------------------------------------------------------------------
//  Names a Mod Manager preset - for SAVE, and for EDIT with its "replace its
//  mods" tick. See LoadedMods.
// ---------------------------------------------------------------------------

using System;
using System.Drawing;
using System.Windows.Forms;

namespace ABeautifulPotatoLauncher
{
    internal sealed class PresetDialog : Form
    {
        private static readonly Color Ink = Color.FromArgb(24, 24, 27);
        private static readonly Color Panel2 = Color.FromArgb(38, 38, 42);

        private readonly TextBox _box;
        private readonly CheckBox _check;

        private PresetDialog(string title, string prompt, string name, string checkText, bool check)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(400, checkText == null ? 118 : 146);
            BackColor = Ink;
            ForeColor = Color.Gainsboro;
            Font = new Font("Segoe UI", 9f);

            Controls.Add(new Label { Text = prompt, Bounds = new Rectangle(14, 14, 372, 18), UseMnemonic = false });

            _box = new TextBox
            {
                Text = name ?? "",
                Bounds = new Rectangle(14, 38, 372, 24),
                BackColor = Panel2,
                ForeColor = Color.Gainsboro,
                BorderStyle = BorderStyle.FixedSingle,
                MaxLength = 60
            };
            Controls.Add(_box);

            if (checkText != null)
            {
                _check = new CheckBox
                {
                    Text = checkText,
                    Checked = check,
                    Bounds = new Rectangle(14, 70, 372, 22),
                    FlatStyle = FlatStyle.Flat,
                    ForeColor = Color.Gainsboro
                };
                Controls.Add(_check);
            }

            int by = ClientSize.Height - 42;
            var ok = MakeButton("OK", new Rectangle(208, by, 86, 28), Color.FromArgb(60, 95, 60));
            var cancel = MakeButton("Cancel", new Rectangle(300, by, 86, 28), Panel2);
            ok.Click += (s, e) =>
            {
                if (LoadedMods.CleanName(_box.Text).Length == 0) { _box.Focus(); return; }
                DialogResult = DialogResult.OK;
            };
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;

            Shown += (s, e) => { _box.Focus(); _box.SelectAll(); };
        }

        private static Button MakeButton(string text, Rectangle bounds, Color back)
        {
            var b = new Button { Text = text, Bounds = bounds, FlatStyle = FlatStyle.Flat, BackColor = back, ForeColor = Color.White };
            b.FlatAppearance.BorderColor = Color.FromArgb(75, 75, 82);
            return b;
        }

        /// <summary>
        /// The name typed, or null when cancelled. With <paramref name="checkText"/>,
        /// a tick box is shown and its answer comes back in <paramref name="check"/>.
        /// </summary>
        internal static string Ask(IWin32Window owner, string title, string prompt, string name,
                                   string checkText, ref bool check)
        {
            using (var dlg = new PresetDialog(title, prompt, name, checkText, check))
            {
                if (dlg.ShowDialog(owner) != DialogResult.OK) return null;
                if (dlg._check != null) check = dlg._check.Checked;
                return LoadedMods.CleanName(dlg._box.Text);
            }
        }
    }
}
