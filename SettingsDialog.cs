// ---------------------------------------------------------------------------
//  Settings, and the record of what the fake-server rules rejected.
//
//  Hiding roughly a quarter of the master list is a big claim to make on the
//  player's behalf, so nothing is hidden invisibly: every rejected server is
//  listed here, and anything wrongly caught can be allowed back permanently.
//
//  The REASON a server was rejected is deliberately not shown or copied. This
//  list gets screenshotted and pasted around, and spelling out the rules would
//  tell the people running fake servers exactly what to change.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace ABeautifulPotatoLauncher
{
    internal static class SettingsDialog
    {
        private static readonly Color Ink    = Color.FromArgb(24, 24, 27);
        private static readonly Color Panel  = Color.FromArgb(28, 28, 30);
        private static readonly Color Panel2 = Color.FromArgb(38, 38, 42);
        private static readonly Color Dim    = Color.FromArgb(150, 150, 158);

        /// <summary>
        /// Returns true when something changed that the list must be redrawn for.
        /// </summary>
        public static bool Show(IWin32Window owner,
                                List<FlaggedServer> flagged,
                                HashSet<string> allowed,
                                bool hideFakes,
                                out bool newHideFakes,
                                string steam = null)
        {
            bool changed = false;
            newHideFakes = hideFakes;

            using (var f = new Form())
            {
                f.Text = "Settings";
                f.StartPosition = FormStartPosition.CenterParent;
                f.ClientSize = new Size(900, 560);
                f.MinimumSize = new Size(700, 420);
                f.BackColor = Ink;
                f.ForeColor = Color.Gainsboro;
                f.Font = new Font("Segoe UI", 9f);

                var tabs = new TabControl { Dock = DockStyle.Fill };
                f.Controls.Add(tabs);

                // Built further down, but added FIRST: this is the page people
                // open Settings for.
                var modsPage = new TabPage("Mods") { BackColor = Panel };
                tabs.TabPages.Add(modsPage);

                // ------------------------------------------ flagged servers --
                var flaggedPage = new TabPage("Flagged servers") { BackColor = Panel };
                tabs.TabPages.Add(flaggedPage);

                var searchBox = new TextBox
                {
                    Dock = DockStyle.Top,
                    Height = 24,
                    Margin = new Padding(8, 8, 8, 0),
                    BackColor = Panel2,
                    ForeColor = Color.Gainsboro,
                    BorderStyle = BorderStyle.FixedSingle
                };
                // Added after the list below, deliberately. Docking is applied
                // in reverse z-order, so the control added FIRST ends up
                // innermost - and with the search box first it was laid over
                // the list's column headers.

                var list = new ListView
                {
                    Dock = DockStyle.Fill,
                    View = View.Details,
                    FullRowSelect = true,
                    MultiSelect = true,
                    BackColor = Panel,
                    ForeColor = Color.Gainsboro,
                    BorderStyle = BorderStyle.FixedSingle,
                    Margin = new Padding(8, 8, 8, 8)
                };
                list.Columns.Add("Server", 300);
                list.Columns.Add("Address", 200);

                // Rows are built ONCE and only re-shown on each search. Clearing
                // and adding thousands of items one at a time, with the list
                // repainting after every add, on every keystroke, is what made
                // filtering crawl.
                var flagColour = Color.FromArgb(205, 150, 150);
                var allRows = flagged.OrderBy(x => x.Host)
                    .Select(x => new ListViewItem(new[] { x.Name ?? "", x.Endpoint ?? "" })
                                 { Tag = x, ForeColor = flagColour })
                    .ToArray();

                Action refreshFlagList = () =>
                {
                    string q = searchBox.Text.Trim();
                    var rows = string.IsNullOrEmpty(q)
                        ? allRows
                        : allRows.Where(r =>
                          {
                              var x = (FlaggedServer)r.Tag;
                              return (x.Name ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                                  || (x.Endpoint ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
                          }).ToArray();

                    list.BeginUpdate();
                    try
                    {
                        list.Items.Clear();
                        list.Items.AddRange(rows);
                    }
                    finally { list.EndUpdate(); }
                };

                // And wait for a pause in the typing before filtering at all.
                var searchDelay = new Timer { Interval = 200 };
                searchDelay.Tick += (s, e) => { searchDelay.Stop(); refreshFlagList(); };
                f.Disposed += (s, e) => searchDelay.Dispose();
                searchBox.TextChanged += (s, e) => { searchDelay.Stop(); searchDelay.Start(); };
                refreshFlagList();

                flaggedPage.Controls.Add(list);        // Fill, so it goes in first
                flaggedPage.Controls.Add(searchBox);   // then the bar above it

                // Docked, so it is laid out after this returns - the X follows
                // it either way because it tracks the box's own bounds.
                ClearBox.AddTo(searchBox);

                var flaggedBar = new Panel { Dock = DockStyle.Bottom, Height = 76, BackColor = Panel };
                flaggedPage.Controls.Add(flaggedBar);

                flaggedBar.Controls.Add(new Label
                {
                    Text = flagged.Count + " server(s) rejected as fake.  "
                         + "Select any that are genuine and allow them back - the choice is "
                         + "remembered by address, so the rest of that host stays hidden.",
                    Bounds = new Rectangle(8, 6, 700, 34),
                    ForeColor = Dim
                });

                var allowAddr = MakeBtn("Always show this address", new Rectangle(8, 42, 180, 26));
                var allowNet = MakeBtn("Always show this subnet", new Rectangle(196, 42, 180, 26));
                var copy = MakeBtn("Copy list", new Rectangle(384, 42, 100, 26));
                flaggedBar.Controls.Add(allowAddr);
                flaggedBar.Controls.Add(allowNet);
                flaggedBar.Controls.Add(copy);

                Action<bool> allow = wholeSubnet =>
                {
                    int n = 0;
                    foreach (ListViewItem it in list.SelectedItems)
                    {
                        var x = it.Tag as FlaggedServer;
                        if (x == null) continue;
                        string key = wholeSubnet ? BrowserFilters.Subnet24(x.Host) : x.Host;
                        if (allowed.Add(key)) n++;
                    }
                    if (n == 0) return;

                    ServerStore.SaveAllowed(allowed);
                    changed = true;
                    MessageBox.Show(f, n + " entr" + (n == 1 ? "y" : "ies") +
                                    " allowed. They will reappear after the list redraws.",
                                    "Allowed", MessageBoxButtons.OK, MessageBoxIcon.Information);
                };
                allowAddr.Click += (s, e) => allow(false);
                allowNet.Click += (s, e) => allow(true);

                copy.Click += (s, e) =>
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var x in flagged.OrderBy(x => x.Host))
                        sb.AppendLine(x.Endpoint + "\t" + x.Name);
                    try
                    {
                        if (sb.Length > 0) Clipboard.SetText(sb.ToString());
                    }
                    catch { /* the clipboard can be locked by another app */ }
                };

                // ------------------------------------------------- the rules --
                // ---- Mods ----
                modsPage.Controls.Add(new Label
                {
                    Text = "DayZ workshop folder",
                    Bounds = new Rectangle(14, 16, 300, 18),
                    ForeColor = Color.Gainsboro,
                    Font = new Font("Segoe UI", 9f, FontStyle.Bold)
                });

                string steamPath = steam;

                // THE JUNCTION FOLDER, NOT THE RAW WORKSHOP ONE.
                //
                // Steam downloads into steamapps\workshop\content\221100\<id>,
                // which is all numbers and tells nobody anything. DayZ then
                // makes a folder of junctions named after the mods -
                // steamapps\common\DayZ\!Workshop\@SomeMod - and THAT is what
                // the game loads and what a player recognises. Showing the
                // numeric path here meant the folder they opened never looked
                // like the mods they had installed.
                string workshop = string.IsNullOrEmpty(steamPath)
                    ? "(Steam not found)"
                    : (WorkshopLinks.Root(steamPath) ?? SteamWorkshop.WorkshopRoot(steamPath));

                var workshopBox = new TextBox
                {
                    Text = workshop,
                    Bounds = new Rectangle(14, 38, 560, 23),
                    ReadOnly = true,                 // Steam decides this one, not us
                    BackColor = Panel2,
                    ForeColor = Dim,
                    BorderStyle = BorderStyle.FixedSingle
                };
                modsPage.Controls.Add(workshopBox);

                modsPage.Controls.Add(MakeExplorerButton(f, 580, 37, () => workshopBox.Text));

                modsPage.Controls.Add(new Label
                {
                    Text = "Where DayZ loads mods from. Steam downloads them into a folder named "
                         + "by number;\r\nthis is the one named after the mods themselves.",
                    Bounds = new Rectangle(14, 64, 560, 32),
                    ForeColor = Dim
                });

                modsPage.Controls.Add(new Label
                {
                    Text = "Additional mods folder",
                    Bounds = new Rectangle(14, 106, 300, 18),
                    ForeColor = Color.Gainsboro,
                    Font = new Font("Segoe UI", 9f, FontStyle.Bold)
                });

                var extraBox = new TextBox
                {
                    Text = ServerStore.LoadExtraModPath(),
                    Bounds = new Rectangle(14, 128, 440, 23),
                    BackColor = Panel2,
                    ForeColor = Color.Gainsboro,
                    BorderStyle = BorderStyle.FixedSingle
                };
                modsPage.Controls.Add(extraBox);

                var browse = new Button
                {
                    Text = "Browse",
                    Bounds = new Rectangle(462, 127, 74, 25),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Panel2,
                    ForeColor = Color.White
                };
                browse.FlatAppearance.BorderColor = Color.FromArgb(75, 75, 82);
                browse.Click += (s2, e2) =>
                {
                    using (var fb = new FolderBrowserDialog
                    {
                        Description = "Pick a folder that holds extra mods",
                        ShowNewFolderButton = false
                    })
                    {
                        if (Directory.Exists(extraBox.Text)) fb.SelectedPath = extraBox.Text;
                        if (fb.ShowDialog(f) == DialogResult.OK) extraBox.Text = fb.SelectedPath;
                    }
                };
                modsPage.Controls.Add(browse);
                modsPage.Controls.Add(MakeExplorerButton(f, 544, 127, () => extraBox.Text));

                modsPage.Controls.Add(new Label
                {
                    Text = "A second place to look for mods - useful for hand-installed ones that "
                         + "Steam does not manage.\r\nLeave it empty if you keep everything in the "
                         + "workshop folder.",
                    Bounds = new Rectangle(14, 158, 600, 40),
                    ForeColor = Dim
                });

                // Saved when the dialog is accepted, alongside everything else.
                f.FormClosing += (s2, e2) =>
                {
                    if (f.DialogResult == DialogResult.OK)
                        ServerStore.SaveExtraModPath(extraBox.Text.Trim());
                };

                // THE RULES ARE NOT WRITTEN DOWN IN THE INTERFACE ANY MORE.
                //
                // There used to be a "Fake server rules" tab spelling out every
                // check - the slot counts, the address thresholds, the name
                // tests. Anyone running a redirect farm could open it and read
                // off exactly what to change. The switch stays, on the Mods
                // page with the other settings; the recipe does not.
                var chk = new CheckBox
                {
                    Text = "Hide fake and redirect servers",
                    Checked = hideFakes,
                    Bounds = new Rectangle(14, 210, 400, 24),
                    ForeColor = Color.Gainsboro,
                    FlatStyle = FlatStyle.Flat
                };
                modsPage.Controls.Add(chk);

                modsPage.Controls.Add(new Label
                {
                    Text = "Screens out redirect farms and fabricated listings. Use the Flagged "
                         + "servers tab\r\nto see what was caught, and to allow an address back in.",
                    Bounds = new Rectangle(32, 236, 600, 34),
                    ForeColor = Dim
                });



                // ------------------------------------------------------ close --
                var close = new Button
                {
                    Text = "Close",
                    Dock = DockStyle.Bottom,
                    Height = 34,
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(60, 60, 64),
                    ForeColor = Color.White,
                    DialogResult = DialogResult.OK
                };
                f.Controls.Add(close);
                f.AcceptButton = close;

                f.ShowDialog(owner);
                newHideFakes = chk.Checked;
                if (newHideFakes != hideFakes) changed = true;
            }

            return changed;
        }

        private static Button MakeBtn(string text, Rectangle bounds)
        {
            var b = new Button
            {
                Text = text,
                Bounds = bounds,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(55, 55, 60),
                ForeColor = Color.White
            };
            b.FlatAppearance.BorderColor = Color.FromArgb(75, 75, 82);
            return b;
        }

        /// <summary>
        /// A small button that opens a path in Explorer.
        ///
        /// The path is fetched when the button is PRESSED, not when it is made,
        /// so it follows whatever the box currently holds - including a folder
        /// the player has just browsed to but not yet saved.
        /// </summary>
        private static Button MakeExplorerButton(Form owner, int x, int y, Func<string> path)
        {
            var b = new Button
            {
                Text = "Explorer",
                Bounds = new Rectangle(x, y, 74, 25),
                FlatStyle = FlatStyle.Flat,
                BackColor = Panel2,
                ForeColor = Color.White,
                Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderColor = Color.FromArgb(75, 75, 82);

            b.Click += (s, e) =>
            {
                string target = (path() ?? "").Trim();

                if (target.Length == 0 || target.StartsWith("("))
                {
                    MessageBox.Show(owner, "There is no folder set here yet.",
                                    "Nothing to open", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                try
                {
                    // GetFullPath because the registry hands paths back with
                    // forward slashes, and Explorer opens Documents when given
                    // one of those.
                    target = Path.GetFullPath(target);

                    if (!Directory.Exists(target))
                    {
                        MessageBox.Show(owner, "That folder does not exist:\r\n\r\n" + target,
                                        "Not found", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    System.Diagnostics.Process.Start("explorer.exe", "\"" + target + "\"");
                }
                catch (Exception ex)
                {
                    MessageBox.Show(owner, "Could not open that folder:\r\n\r\n" + ex.Message,
                                    "Explorer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };

            return b;
        }
    }
}
