// ---------------------------------------------------------------------------
//  "Load this mod, and load it from here."
//
//  Reached from the Edit button in a mod's info window. It does two things:
//  turn a required mod off, and point it at a different copy on disk.
//
//  The candidate list is built from what is actually installed, with copies of
//  the SAME mod offered first - a player keeping @Thing_v51 and @Thing_v52
//  beside the workshop copy is choosing between builds, and that choice should
//  be one click, not a hunt through eight hundred entries.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace BeautifulPotatoExpLauncher
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
            var candidates = Candidates(mod, steamPath);

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
                int startIndex = 0;
                if (!string.IsNullOrEmpty(current.Folder))
                {
                    for (int i = 0; i < candidates.Count; i++)
                    {
                        if (!string.Equals(candidates[i].Folder, current.Folder,
                                           StringComparison.OrdinalIgnoreCase)) continue;
                        startIndex = i;
                        break;
                    }
                }
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
                        var pick = new Candidate("Chosen folder", fb.SelectedPath, false);
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

                var chosen = list.SelectedItem as Candidate;
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

        /// <summary>
        /// Where this mod could be loaded from: its own workshop copy first,
        /// then any local install that looks like the same mod, then the rest.
        /// </summary>
        private static List<Candidate> Candidates(Mod mod, string steamPath)
        {
            var list = new List<Candidate>();

            if (mod.WorkshopId != 0)
            {
                string workshop = steamPath == null ? null : SteamWorkshop.ItemPath(steamPath, mod.WorkshopId);
                list.Add(new Candidate("Workshop copy (normal)", workshop ?? "", true));
            }
            else
            {
                // No workshop copy exists; the default is whatever the library
                // match finds by name.
                var match = ModIndex.FindLocalByName(mod.BareName);
                list.Add(new Candidate("Matched by name (normal)",
                                       match != null ? match.Folder : "(not found)", true));
            }

            List<ModEntry> installed;
            try { installed = ModIndex.All(); }
            catch { installed = new List<ModEntry>(); }

            // Local builds whose name resembles this mod come first: that is
            // the version-switching case, and it should not need scrolling.
            string stem = Stem(mod.Name);
            var related = installed
                .Where(m => m.IsLocal && Stem(m.Name).StartsWith(stem, StringComparison.OrdinalIgnoreCase))
                .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var m in related)
                list.Add(new Candidate("Local: " + m.Name, m.Folder, false));

            foreach (var m in installed
                        .Where(m => m.IsLocal && !related.Contains(m))
                        .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
                list.Add(new Candidate("Local: " + m.Name, m.Folder, false));

            return list;
        }

        /// <summary>
        /// The part of a name before any version suffix, so "Thing_v52" and
        /// "Thing" are recognised as the same mod.
        /// </summary>
        private static string Stem(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            string s = name.TrimStart('@');
            int cut = s.IndexOfAny(new[] { '_', '-', ' ' });
            return (cut > 2 ? s.Substring(0, cut) : s).Trim();
        }

        private sealed class Candidate
        {
            public readonly string Label;
            public readonly string Folder;
            public readonly bool IsDefault;

            public Candidate(string label, string folder, bool isDefault)
            {
                Label = label;
                Folder = folder;
                IsDefault = isDefault;
            }

            public override string ToString()
            {
                return string.IsNullOrEmpty(Folder) ? Label : Label + "   -   " + Folder;
            }
        }
    }
}
