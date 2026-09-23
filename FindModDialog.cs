// ---------------------------------------------------------------------------
//  "Where is this mod?"
//
//  A server can load a mod from its own disk instead of the workshop. Those
//  arrive with no workshop id - only a name - so the launcher pairs "@COT" with
//  a folder called @COT somewhere in the player's mod folders. When there is no
//  such folder the old answer was a red "NOT FOUND in your mod folders" and
//  nothing else: no way to say "it is right here", even when it was.
//
//  This asks. Whatever folder is given is remembered against that mod (see
//  ModOverrides) and used from then on, so it is asked once, not every join.
// ---------------------------------------------------------------------------

using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace ABeautifulPotatoLauncher
{
    internal sealed class FindModDialog : Form
    {
        private static readonly Color Ink = Color.FromArgb(24, 24, 27);
        private static readonly Color Panel2 = Color.FromArgb(38, 38, 42);
        private static readonly Color Dim = Color.FromArgb(150, 150, 158);

        private readonly TextBox _path;
        private readonly Label _verdict;
        private readonly string _modName;

        private FindModDialog(string modName, string startAt)
        {
            _modName = modName;

            Text = "Where is " + modName + "?";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(560, 196);
            BackColor = Ink;
            ForeColor = Color.Gainsboro;
            Font = new Font("Segoe UI", 9f);

            Controls.Add(new Label
            {
                Text = "This server loads " + modName + " from its own disk.",
                Bounds = new Rectangle(16, 14, 528, 20),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                UseMnemonic = false
            });

            Controls.Add(new Label
            {
                Text = "It is not in your mod folders under that name. If you have it somewhere "
                     + "else, point at the\r\nfolder itself - the one containing addons - and the "
                     + "launcher will use it from now on.",
                Bounds = new Rectangle(16, 38, 528, 36),
                ForeColor = Dim,
                UseMnemonic = false
            });

            _path = new TextBox
            {
                Text = startAt ?? "",
                Bounds = new Rectangle(16, 84, 442, 23),
                BackColor = Panel2,
                ForeColor = Color.Gainsboro,
                BorderStyle = BorderStyle.FixedSingle
            };
            _path.TextChanged += (s, e) => Judge();
            Controls.Add(_path);

            var browse = new Button
            {
                Text = "Browse",
                Bounds = new Rectangle(466, 83, 78, 25),
                FlatStyle = FlatStyle.Flat,
                BackColor = Panel2,
                ForeColor = Color.White
            };
            browse.FlatAppearance.BorderColor = Color.FromArgb(75, 75, 82);
            browse.Click += OnBrowse;
            Controls.Add(browse);

            _verdict = new Label
            {
                Bounds = new Rectangle(16, 112, 528, 20),
                ForeColor = Dim,
                UseMnemonic = false
            };
            Controls.Add(_verdict);

            var ok = new Button
            {
                Text = "Use this folder",
                Bounds = new Rectangle(330, 152, 128, 30),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(60, 95, 60),
                ForeColor = Color.White,
                DialogResult = DialogResult.OK
            };
            ok.FlatAppearance.BorderColor = Color.FromArgb(80, 120, 80);
            Controls.Add(ok);

            var cancel = new Button
            {
                Text = "Skip",
                Bounds = new Rectangle(466, 152, 78, 30),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(70, 55, 55),
                ForeColor = Color.White,
                DialogResult = DialogResult.Cancel
            };
            cancel.FlatAppearance.BorderColor = Color.FromArgb(100, 70, 70);
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
            Judge();
        }

        private void OnBrowse(object sender, EventArgs e)
        {
            using (var fb = new FolderBrowserDialog
            {
                Description = "Pick the folder that IS " + _modName + " (the one holding addons)",
                ShowNewFolderButton = false
            })
            {
                if (Directory.Exists(_path.Text)) fb.SelectedPath = _path.Text;
                if (fb.ShowDialog(this) == DialogResult.OK) _path.Text = fb.SelectedPath;
            }
        }

        /// <summary>
        /// Says what is actually at the end of the path. A folder with no
        /// addons inside is very likely the PARENT of the mod rather than the
        /// mod - the commonest mistake here - so that is called out by name
        /// rather than refused, because an unpacked mod is a real thing too.
        /// </summary>
        private void Judge()
        {
            string p = (_path.Text ?? "").Trim();

            if (p.Length == 0) { _verdict.Text = ""; return; }

            if (!Directory.Exists(p))
            {
                _verdict.Text = "There is no folder there.";
                _verdict.ForeColor = Color.FromArgb(220, 140, 120);
                return;
            }

            if (Directory.Exists(Path.Combine(p, "addons")))
            {
                _verdict.Text = "Looks right: this folder has an addons folder in it.";
                _verdict.ForeColor = Color.FromArgb(130, 200, 130);
                return;
            }

            string inside = null;
            try
            {
                foreach (var d in Directory.GetDirectories(p, "@*"))
                {
                    inside = Path.GetFileName(d);
                    break;
                }
            }
            catch { }

            _verdict.Text = inside != null
                ? "No addons here - did you mean the " + inside + " folder inside it?"
                : "No addons folder inside. Usable if the mod is unpacked, otherwise wrong folder.";
            _verdict.ForeColor = Color.FromArgb(220, 190, 120);
        }

        /// <summary>
        /// The folder the player chose, or null if they skipped. Never returns
        /// a path that is not there.
        /// </summary>
        internal static string Ask(IWin32Window owner, string modName, string startAt)
        {
            using (var dlg = new FindModDialog(modName, startAt))
            {
                if (dlg.ShowDialog(owner) != DialogResult.OK) return null;
                string p = (dlg._path.Text ?? "").Trim();
                return p.Length > 0 && Directory.Exists(p) ? p : null;
            }
        }
    }
}
