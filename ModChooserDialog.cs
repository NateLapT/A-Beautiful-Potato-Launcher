// ---------------------------------------------------------------------------
//  "Load this mod, and load it from here."
//
//  Reached from the Edit button in a mod's info window. It does two things:
//  turn a required mod off, and point it at a different copy on disk.
//
//  The candidates are the copies of THIS mod on the PC - the normal one, and
//  local folders holding the same PBOs (see ModCopies) - the same list the
//  drop-down under a mod's name shows. A player keeping @Thing_v51 and
//  @Thing_v52 beside the workshop copy is choosing between builds of one mod,
//  not between every mod they own.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace ABeautifulPotatoLauncher
{
    internal static class ModChooserDialog
    {
        private static readonly Color Ink = Color.FromArgb(24, 24, 27);
        private static readonly Color Panel2 = Color.FromArgb(38, 38, 42);
        private static readonly Color Dim = Color.FromArgb(150, 150, 158);

        /// <summary>
        /// Shows the editor for one required mod. Returns true when something
        /// was changed.
        /// </summary>
        public static bool Show(IWin32Window owner, Mod mod, string steamPath)
        {
            if (mod == null) return false;

            var current = ModOverrides.For(mod) ?? new ModOverride();
            // The same list as the drop-down under the mod's name - see ModCopies.
            var candidates = ModCopies.For(mod, steamPath);

            using (var f = new Form())
            {
                f.Text = "Edit mod - " + mod.Name;
                f.FormBorderStyle = FormBorderStyle.Sizable;
                f.StartPosition = FormStartPosition.CenterParent;
                f.MinimizeBox = false;
                f.MaximizeBox = false;
                f.ShowInTaskbar = false;
                f.ClientSize = new Size(640, 340);
                f.MinimumSize = new Size(520, 300);
                f.BackColor = Ink;
                f.ForeColor = Color.Gainsboro;
                f.Font = new Font("Segoe UI", 9f);

                f.Controls.Add(new Label
                {
                    Text = mod.Name,
                    Bounds = new Rectangle(14, 12, 600, 22),
                    ForeColor = Color.White,
                    UseMnemonic = false,
                    Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
                });

                f.Controls.Add(new Label
                {
                    Text = mod.IsLocal
                        ? "Loaded from the server's own disk - matched by name"
                        : "Workshop ID " + mod.WorkshopId + " - required by this server",
                    Bounds = new Rectangle(14, 36, 600, 18),
                    ForeColor = Dim,
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
                });

                var enabled = new CheckBox
                {
                    Text = "Load this mod when joining",
                    Bounds = new Rectangle(14, 64, 320, 22),
                    Checked = current.Enabled,
                    ForeColor = Color.Gainsboro
                };
                f.Controls.Add(enabled);

                var warn = new Label
                {
                    Text = "The server requires this mod. Leaving it off will usually get you kicked.",
                    Bounds = new Rectangle(34, 86, 580, 18),
                    ForeColor = Color.FromArgb(225, 175, 90),
                    Visible = !current.Enabled,
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
                };
                f.Controls.Add(warn);
                enabled.CheckedChanged += (s, e) => warn.Visible = !enabled.Checked;

                f.Controls.Add(new Label
                {
                    Text = "Load it from",
                    Bounds = new Rectangle(14, 112, 200, 18),
                    ForeColor = Color.Gainsboro,
                    Font = new Font("Segoe UI", 9f, FontStyle.Bold)
                });

                var list = new ListBox
                {
                    Bounds = new Rectangle(14, 134, 610, 130),
                    BackColor = Panel2,
                    ForeColor = Color.Gainsboro,
                    BorderStyle = BorderStyle.FixedSingle,
                    IntegralHeight = false,
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
                };
                foreach (var c in candidates) list.Items.Add(c);

                // Select whatever is in force now.
                int startIndex = Math.Max(0, candidates.FindIndex(c => c.InUse));
                if (list.Items.Count > 0) list.SelectedIndex = startIndex;
                f.Controls.Add(list);

                var browse = new Button
                {
                    Text = "Browse...",
                    Bounds = new Rectangle(14, 272, 94, 27),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Panel2,
                    ForeColor = Color.White,
                    Anchor = AnchorStyles.Bottom | AnchorStyles.Left
                };
                browse.FlatAppearance.BorderColor = Color.FromArgb(75, 75, 82);
                browse.Click += (s, e) =>
                {
                    using (var fb = new FolderBrowserDialog { Description = "Pick the folder to load this mod from" })
                    {
                        if (fb.ShowDialog(f) != DialogResult.OK) return;
                        var pick = new ModCopy { Label = "Your folder", Folder = fb.SelectedPath };
                        list.Items.Add(pick);
                        list.SelectedItem = pick;
                    }
                };
                f.Controls.Add(browse);

                var save = new Button
                {
                    Text = "Save",
                    Bounds = new Rectangle(f.ClientSize.Width - 194, 272, 86, 27),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(60, 95, 60),
                    ForeColor = Color.White,
                    DialogResult = DialogResult.OK,
                    Anchor = AnchorStyles.Bottom | AnchorStyles.Right
                };
                save.FlatAppearance.BorderColor = Color.FromArgb(80, 120, 80);
                f.Controls.Add(save);

                var cancel = new Button
                {
                    Text = "Cancel",
                    Bounds = new Rectangle(f.ClientSize.Width - 100, 272, 86, 27),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Panel2,
                    ForeColor = Color.White,
                    DialogResult = DialogResult.Cancel,
                    Anchor = AnchorStyles.Bottom | AnchorStyles.Right
                };
                cancel.FlatAppearance.BorderColor = Color.FromArgb(75, 75, 82);
                f.Controls.Add(cancel);

                f.AcceptButton = save;
                f.CancelButton = cancel;
                UiCursors.ApplyTo(f);

                if (f.ShowDialog(owner) != DialogResult.OK) return false;

                var chosen = list.SelectedItem as ModCopy;
                var result = new ModOverride
                {
                    Enabled = enabled.Checked,
                    // The workshop copy is the default, so selecting it records
                    // nothing - see ModOverrides.Set.
                    Folder = chosen == null || chosen.IsDefault ? "" : chosen.Folder
                };
                ModOverrides.Set(mod, result);
                return true;
            }
        }

    }
}
