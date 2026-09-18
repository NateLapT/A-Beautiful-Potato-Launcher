// ---------------------------------------------------------------------------
//  Mod details, in the spirit of the official launcher's expanded mod row.
//
//  Everything here is read locally - from Steam's install info and from the
//  mod's own mod.cpp / meta.cpp - rather than from a UGC web query. That keeps
//  it instant and working offline, at the cost of a couple of fields that only
//  the website knows. The Workshop page is one click away for those.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace BeautifulPotatoExpLauncher
{
    internal static class ModInfoDialog
    {
        private static readonly Color Ink    = Color.FromArgb(24, 24, 27);
        private static readonly Color Panel2 = Color.FromArgb(38, 38, 42);
        private static readonly Color Dim    = Color.FromArgb(150, 150, 158);

        public static void Show(IWin32Window owner, Mod mod, string steamPath)
        {
            var facts = Gather(mod, steamPath);

            using (var f = new Form())
            {
                f.Text = "Mod info";
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.MinimizeBox = f.MaximizeBox = false;
                f.ClientSize = new Size(560, 430);
                f.BackColor = Ink;
                f.ForeColor = Color.Gainsboro;
                f.Font = new Font("Segoe UI", 9f);

                f.Controls.Add(new Label
                {
                    Text = mod.Name,
                    Bounds = new Rectangle(16, 14, 528, 26),
                    Font = new Font("Segoe UI", 13f, FontStyle.Bold),
                    ForeColor = Color.White,
                    AutoEllipsis = true
                });

                int y = 50;
                foreach (var kv in facts)
                {
                    f.Controls.Add(new Label
                    {
                        Text = kv.Key,
                        Bounds = new Rectangle(16, y, 110, 20),
                        TextAlign = ContentAlignment.MiddleRight,
                        ForeColor = Dim
                    });
                    f.Controls.Add(new Label
                    {
                        Text = kv.Value,
                        Bounds = new Rectangle(134, y, 410, 20),
                        ForeColor = Color.Gainsboro,
                        AutoEllipsis = true
                    });
                    y += 23;
                }

                string modCpp = ModCpp(steamPath, mod.WorkshopId);
                string overview = ReadKey(modCpp, "overview");
                if (overview.Length > 0 && y < 360)
                {
                    f.Controls.Add(new TextBox
                    {
                        Text = overview,
                        Bounds = new Rectangle(16, y + 6, 528, 376 - y - 14),
                        Multiline = true,
                        ReadOnly = true,
                        ScrollBars = ScrollBars.Vertical,
                        BackColor = Panel2,
                        ForeColor = Color.Gainsboro,
                        BorderStyle = BorderStyle.FixedSingle
                    });
                }

                var workshop = MakeBtn("Workshop page", new Rectangle(16, 386, 130, 30));
                workshop.Click += (s, e) => SteamWorkshop.OpenWorkshopPage(mod.WorkshopId);
                f.Controls.Add(workshop);

                string folder = ItemFolder(steamPath, mod.WorkshopId);
                var openFolder = MakeBtn("Open folder", new Rectangle(154, 386, 120, 30));
                openFolder.Enabled = folder != null && Directory.Exists(folder);
                openFolder.Click += (s, e) =>
                {
                    try { Process.Start("explorer.exe", Quote(folder)); } catch { }
                };
                f.Controls.Add(openFolder);

                string action = ReadKey(modCpp, "action");
                if (action.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    var site = MakeBtn("Mod website", new Rectangle(282, 386, 120, 30));
                    site.Click += (s, e) => { try { Process.Start(action); } catch { } };
                    f.Controls.Add(site);
                }

                var close = new Button
                {
                    Text = "Close",
                    Bounds = new Rectangle(454, 386, 90, 30),
                    DialogResult = DialogResult.OK,
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(60, 60, 64),
                    ForeColor = Color.White
                };
                f.Controls.Add(close);
                f.AcceptButton = close;
                f.CancelButton = close;

                f.ShowDialog(owner);
            }
        }

        private static string Quote(string s)
        {
            return "\"" + s + "\"";
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

        // ------------------------------------------------------------ facts --

        private static List<KeyValuePair<string, string>> Gather(Mod mod, string steamPath)
        {
            var list = new List<KeyValuePair<string, string>>();
            Action<string, string> add = (k, v) =>
                list.Add(new KeyValuePair<string, string>(k, v));

            string modCpp = ModCpp(steamPath, mod.WorkshopId);
            string folder = ItemFolder(steamPath, mod.WorkshopId);
            bool installed = steamPath != null && SteamWorkshop.IsInstalled(steamPath, mod.WorkshopId);

            add("Workshop ID", mod.WorkshopId.ToString());
            add("Source", "Steam Workshop (UGC)");

            string author = ReadKey(modCpp, "author");
            if (author.Length > 0) add("Author", author);

            string version = ReadKey(modCpp, "version");
            if (version.Length > 0) add("Version", version);

            // Two dates, because the interesting thing is the GAP between them:
            // when the copy on this disk was published, out of its own meta.cpp,
            // against what the workshop is serving now.
            DateTime local = steamPath == null
                ? DateTime.MinValue
                : SteamWorkshop.LocalPublishTime(steamPath, mod.WorkshopId);
            if (local > DateTime.MinValue)
                add("Installed version", local.ToLocalTime().ToString("dddd d MMMM yyyy, HH:mm"));

            // Cache only. Selecting the server already fetched this on a
            // background thread; asking again here would run a network round
            // trip on the UI thread and freeze the dialog open.
            DateTime published = SteamWorkshop.WorkshopUpdated(mod.WorkshopId);
            if (published > DateTime.MinValue)
                add("Workshop version", published.ToLocalTime().ToString("dddd d MMMM yyyy, HH:mm"));

            long size = installed ? SteamWorkshop.SizeOnDisk(steamPath, mod.WorkshopId) : 0;
            if (size > 0) add("File size", (size / 1048576.0).ToString("N1") + " MB");

            var state = SteamWorkshop.GetState(mod.WorkshopId);
            TimeSpan behind = steamPath == null
                ? TimeSpan.Zero
                : SteamWorkshop.StaleBy(steamPath, mod.WorkshopId);
            add("Status", DescribeState(state, installed, behind));

            if (folder != null && Directory.Exists(folder))
            {
                int pbos = SafeCount(folder, "*.pbo");
                int signs = SafeCount(folder, "*.bisign");
                add("Content", pbos + (pbos == 1 ? " pbo" : " pbos"));

                // A server running verifySignatures will reject unsigned
                // content, so this is the field that predicts a kick.
                add("Multiplayer", signs > 0
                    ? "Signed (" + signs + (signs == 1 ? " signature)" : " signatures)")
                    : "NOT SIGNED - some servers will reject it");
            }

            if (folder != null) add("Installed at", folder);
            return list;
        }

        /// <summary>
        /// Steam's flag is checked, but it is NOT trusted on its own: on this
        /// machine every one of the nine genuinely outdated mods has the flag
        /// clear. The timestamp comparison is the signal that actually catches
        /// them, so it is tested first and reported with the size of the gap.
        /// </summary>
        private static string DescribeState(ItemState st, bool onDisk, TimeSpan behind)
        {
            if (st.HasFlag(ItemState.Downloading)) return "Downloading";
            if (st.HasFlag(ItemState.DownloadPending)) return "Queued by Steam";
            if (behind > TimeSpan.Zero)
                return "Out of date by " + Age(behind) + " - needs updating";
            if (st.HasFlag(ItemState.NeedsUpdate)) return "Out of date - needs updating";
            if (st.HasFlag(ItemState.Installed)) return "Ready";
            if (onDisk) return "On disk";
            if (st.HasFlag(ItemState.Subscribed)) return "Subscribed, not downloaded yet";
            return "Not installed";
        }

        /// <summary>A time span in the largest unit that still reads naturally.</summary>
        private static string Age(TimeSpan t)
        {
            if (t.TotalDays >= 365) return string.Format("{0:F1} years", t.TotalDays / 365.0);
            if (t.TotalDays >= 1)   return string.Format("{0:F0} days", t.TotalDays);
            if (t.TotalHours >= 1)  return string.Format("{0:F0} hours", t.TotalHours);
            return string.Format("{0:F0} minutes", t.TotalMinutes);
        }

        private static int SafeCount(string dir, string pattern)
        {
            try { return Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories).Count(); }
            catch { return 0; }
        }

        private static string ItemFolder(string steamPath, ulong id)
        {
            return steamPath == null ? null : SteamWorkshop.ItemPath(steamPath, id);
        }

        private static string ModCpp(string steamPath, ulong id)
        {
            try
            {
                string folder = ItemFolder(steamPath, id);
                if (folder == null) return null;
                string path = Path.Combine(folder, "mod.cpp");
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Pulls one quoted value out of a mod.cpp. Not every mod ships one -
        /// MotoX does not - so a missing file simply yields an empty string.
        /// </summary>
        private static string ReadKey(string modCpp, string key)
        {
            if (string.IsNullOrEmpty(modCpp)) return "";
            foreach (string raw in modCpp.Split('\n'))
            {
                string line = raw.Trim();
                int eq = line.IndexOf('=');
                if (eq < 0) continue;

                // Compare the whole left-hand side, or a request for "author"
                // would match "authorID" as well.
                string lhs = line.Substring(0, eq).Trim();
                if (!lhs.Equals(key, StringComparison.OrdinalIgnoreCase)) continue;

                string v = line.Substring(eq + 1).Trim().TrimEnd(';').Trim();
                if (v.StartsWith("\"")) v = v.Trim('"');
                return v;
            }
            return "";
        }
    }
}
