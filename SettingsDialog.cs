// ---------------------------------------------------------------------------
//  Settings, and the record of what the fake-server rules rejected.
//
//  Hiding roughly a quarter of the master list is a big claim to make on the
//  player's behalf, so nothing is hidden invisibly: every rejected server is
//  listed here with the reason it was rejected, and anything wrongly caught can
//  be allowed back permanently.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace BeautifulPotatoExpLauncher
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
                                out bool newHideFakes)
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
                flaggedPage.Controls.Add(searchBox);

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
                list.Columns.Add("Address", 160);
                list.Columns.Add("Why it was flagged", 400);

                var allFlagged = flagged.OrderBy(x => x.Host).ToList();
                Action refreshFlagList = () =>
                {
                    string q = searchBox.Text.Trim();
                    list.Items.Clear();
                    foreach (var x in allFlagged.Where(x =>
                        string.IsNullOrEmpty(q)
                        || x.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                        || x.Host.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                        || x.Reason.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        list.Items.Add(new ListViewItem(new[] { x.Name, x.Endpoint, x.Reason })
                        { Tag = x, ForeColor = Color.FromArgb(205, 150, 150) });
                    }
                };
                searchBox.TextChanged += (s, e) => refreshFlagList();
                refreshFlagList();

                flaggedPage.Controls.Add(list);

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
                        sb.AppendLine(x.Endpoint + "\t" + x.Name + "\t" + x.Reason);
                    try
                    {
                        if (sb.Length > 0) Clipboard.SetText(sb.ToString());
                    }
                    catch { /* the clipboard can be locked by another app */ }
                };

                // ------------------------------------------------- the rules --
                var rulesPage = new TabPage("Fake server rules") { BackColor = Panel };
                tabs.TabPages.Add(rulesPage);

                var chk = new CheckBox
                {
                    Text = "Hide servers that fail these checks",
                    Checked = hideFakes,
                    Bounds = new Rectangle(16, 16, 400, 24),
                    ForeColor = Color.Gainsboro,
                    FlatStyle = FlatStyle.Flat
                };
                rulesPage.Controls.Add(chk);

                var body = new TextBox
                {
                    Multiline = true,
                    ReadOnly = true,
                    ScrollBars = ScrollBars.Vertical,
                    BackColor = Panel2,
                    ForeColor = Color.Gainsboro,
                    BorderStyle = BorderStyle.FixedSingle,
                    Bounds = new Rectangle(16, 50, 850, 420),
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
                    Text = string.Join(Environment.NewLine, new[]
                    {
                        "TOO MANY SLOTS",
                        "    DayZ supports at most " + BrowserFilters.MaxRealSlots + " players. A server",
                        "    advertising 140, 200 or 255 slots cannot deliver them; the number is",
                        "    there to sort it to the top of a browser.",
                        "",
                        "ONE ADDRESS, MANY IDENTITIES",
                        "    An address running " + BrowserFilters.FarmServersPerIp + "+ servers under "
                            + BrowserFilters.FarmNamesPerIp + "+ different community",
                        "    names is a redirect farm. A real host running many servers uses one",
                        "    brand across them.",
                        "",
                        "A SUBNET PACKED WITH THEM",
                        "    Farms dodge the rule above by spreading over a /24. That is caught by",
                        "    density: " + BrowserFilters.FarmServersPerSubnet + "+ servers, "
                            + BrowserFilters.FarmNamesPerSubnet + "+ names, and at least "
                            + BrowserFilters.FarmServersPerAddress + " servers per address.",
                        "",
                        "    Density is what separates a farm from a hosting provider. Measured",
                        "    against the live list: farms run 20 to 200 servers per address, while",
                        "    genuine providers run 1.1 to 2.5. Nothing sits in between, so the",
                        "    threshold is not finely balanced.",
                        "",
                        "WHAT IS DELIBERATELY NOT USED",
                        "    \"Nearly full\" sounds like a good signal, and the farms do report 125",
                        "    of 127 when queried. But the master list reports every server as empty",
                        "    until it is individually asked, so fullness is unknown at the moment",
                        "    the list is filtered - and the rules above already separate them.",
                        "",
                        "IF SOMETHING GENUINE IS CAUGHT",
                        "    Use the Flagged servers tab to allow it back. Allowing is remembered",
                        "    in allowed.txt next to the other settings."
                    })
                };
                rulesPage.Controls.Add(body);

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
    }
}
