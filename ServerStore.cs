// ---------------------------------------------------------------------------
//  The server list and player settings, kept in the user's own AppData.
//
//  Deliberately a plain tab-separated text file rather than JSON: net48 has no
//  System.Text.Json, and a file a player can open in Notepad and fix by hand is
//  worth more here than a tidy serializer.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace BeautifulPotatoExpLauncher
{
    internal sealed class ServerEntry
    {
        public string Name;
        public string Host;
        public int Port;


        public ServerEntry(string name, string host, int port)
        {
            Name = name; Host = host; Port = port;
        }

        public string Endpoint { get { return Host + ":" + Port; } }
    }

    internal static class ServerStore
    {
        // Every one of these was confirmed live over A2S while building this.
        private static readonly ServerEntry[] Defaults =
        {
            new ServerEntry("A Beautiful Potato MotoX Motocross",    "104.218.48.62", 4902),
            new ServerEntry("A Beautiful Potato | 3PP | Experimental","104.218.48.62", 2402),
            new ServerEntry("A Beautiful Potato | Shooting Range",   "104.218.48.62", 2302),
        };

        private static string Dir
        {
            get
            {
                string d = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "BeautifulPotatoExpLauncher");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        private static string ServersFile { get { return Path.Combine(Dir, "servers.txt"); } }
        private static string SettingsFile { get { return Path.Combine(Dir, "settings.txt"); } }

        public static List<ServerEntry> Load()
        {
            var list = new List<ServerEntry>();
            try
            {
                if (File.Exists(ServersFile))
                {
                    foreach (string line in File.ReadAllLines(ServersFile))
                    {
                        if (line.Trim().Length == 0 || line.StartsWith("#")) continue;
                        string[] p = line.Split('\t');
                        int port;
                        if (p.Length >= 3 && int.TryParse(p[2], out port))
                            list.Add(new ServerEntry(p[0], p[1], port));
                    }
                }
            }
            catch { /* fall through to defaults */ }

            if (list.Count == 0) list.AddRange(Defaults);
            return list;
        }

        public static void Save(IEnumerable<ServerEntry> servers)
        {
            try
            {
                var lines = new List<string>
                {
                    "# Beautiful Potato Experimental Launcher - server list",
                    "# name <TAB> host <TAB> game port   (the query port is game port + 1)"
                };
                foreach (var s in servers)
                    lines.Add(s.Name + "\t" + s.Host + "\t" + s.Port);
                File.WriteAllLines(ServersFile, lines.ToArray());
            }
            catch { /* not worth interrupting anything over */ }
        }

        // ---- favourites, stored as plain "host:port" lines ----

        private static string FavouritesFile { get { return Path.Combine(Dir, "favourites.txt"); } }

        // Each line is "host:port" optionally followed by a tab and the server
        // name, so a saved server still reads sensibly while it is offline.
        private static readonly Dictionary<string, string> Names =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static HashSet<string> LoadFavourites()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(FavouritesFile)) return set;
                foreach (string line in File.ReadAllLines(FavouritesFile))
                {
                    if (line.Trim().Length == 0 || line.StartsWith("#")) continue;
                    string[] parts = line.Split('\t');
                    string ep = parts[0].Trim();
                    if (ep.Length == 0) continue;
                    set.Add(ep);
                    if (parts.Length > 1 && parts[1].Trim().Length > 0)
                        Names[ep] = parts[1].Trim();
                }
            }
            catch { }
            return set;
        }

        /// <summary>The remembered display name for a saved server, or the address.</summary>
        public static string NameFor(string endpoint)
        {
            string n;
            return Names.TryGetValue(endpoint, out n) && n.Length > 0 ? n : endpoint;
        }

        public static void RememberName(string endpoint, string name)
        {
            if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(name)) return;
            Names[endpoint] = name;
        }

        public static void SaveFavourites(IEnumerable<string> endpoints)
        {
            try
            {
                var lines = new List<string>();
                foreach (string ep in endpoints)
                {
                    string n;
                    lines.Add(Names.TryGetValue(ep, out n) && n.Length > 0 ? ep + "\t" + n : ep);
                }
                File.WriteAllLines(FavouritesFile, lines.ToArray());
            }
            catch { }
        }

        public static void SaveNameFor(string endpoint, string name)
        {
            RememberName(endpoint, name);
            SaveFavourites(Names.Keys);
        }

        public static Rectangle? LoadWindowBounds()
        {
            int x, y, w, h;
            bool maximised;
            if (!LoadWindow(out x, out y, out w, out h, out maximised)) return null;
            return new Rectangle(x, y, w, h);
        }

        public static void SaveWindowBounds(Rectangle bounds)
        {
            SaveWindow(bounds.X, bounds.Y, bounds.Width, bounds.Height, false);
        }

        // ---- last tab used, so the launcher reopens where you left it ----

        private static string TabFile { get { return Path.Combine(Dir, "tab.txt"); } }

        public static int LoadTab()
        {
            try
            {
                int t;
                // Deliberately NOT range-checked here. This file has already
                // silently reset the player's tab twice, both times because a
                // tab was added and this number was left behind. The caller
                // validates it against the actual enum, which cannot go stale.
                if (File.Exists(TabFile) && int.TryParse(File.ReadAllText(TabFile).Trim(), out t))
                    return t;
            }
            catch { }
            return 0;
        }

        public static void SaveTab(int tab)
        {
            try { File.WriteAllText(TabFile, tab.ToString()); }
            catch { }
        }

        // Which build the browser was last pointed at, so it reopens there.
        private static string BuildFile { get { return Path.Combine(Dir, "build.txt"); } }

        /// <summary>
        /// Which builds to browse: "all", "exp" or "stable". Older installs
        /// wrote only "exp" or "stable", and both still read back correctly, so
        /// upgrading does not reset the player's choice.
        /// </summary>
        public static string LoadBrowseMode()
        {
            try
            {
                if (File.Exists(BuildFile))
                {
                    string v = File.ReadAllText(BuildFile).Trim().ToLowerInvariant();
                    if (v == "all" || v == "exp" || v == "stable") return v;
                }
            }
            catch { }
            return "stable";       // default branch is Stable
        }

        public static void SaveBrowseMode(string mode)
        {
            try { File.WriteAllText(BuildFile, mode); }
            catch { }
        }

        // ---- the window's last size and place ----

        private static string WindowFile { get { return Path.Combine(Dir, "window.txt"); } }

        /// <summary>
        /// Remembers where the window was and how big. Returns false when there
        /// is nothing saved or the file is unreadable, in which case the caller
        /// keeps its own defaults.
        /// </summary>
        public static bool LoadWindow(out int x, out int y, out int w, out int h, out bool maximised)
        {
            x = y = w = h = 0;
            maximised = false;
            try
            {
                if (!File.Exists(WindowFile)) return false;
                string[] f = File.ReadAllText(WindowFile).Trim().Split(',');
                if (f.Length < 5) return false;
                x = int.Parse(f[0]);
                y = int.Parse(f[1]);
                w = int.Parse(f[2]);
                h = int.Parse(f[3]);
                maximised = f[4] == "1";
                return w > 0 && h > 0;
            }
            catch { return false; }
        }

        public static void SaveWindow(int x, int y, int w, int h, bool maximised)
        {
            try
            {
                File.WriteAllText(WindowFile, string.Format("{0},{1},{2},{3},{4}",
                    x, y, w, h, maximised ? 1 : 0));
            }
            catch { }
        }

        // ---- panel splitter positions ----

        private static string PanelFile { get { return Path.Combine(Dir, "panels.txt"); } }

        /// <summary>
        /// Splitter positions, held as a FRACTION of the space available rather
        /// than a pixel count.
        ///
        /// Pixels do not survive a change of window size, and the window size is
        /// itself remembered and restorable on a different monitor - so a pixel
        /// value saved on a 3440-wide screen would put the divider somewhere
        /// absurd on a 1920-wide one. A fraction means the layout the player
        /// arranged keeps its proportions wherever it reopens.
        /// </summary>
        public static Dictionary<string, double> LoadPanels()
        {
            var result = new Dictionary<string, double>();
            try
            {
                if (!File.Exists(PanelFile)) return result;
                foreach (string line in File.ReadAllLines(PanelFile))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    double v;
                    if (!double.TryParse(line.Substring(eq + 1).Trim(),
                                         System.Globalization.NumberStyles.Float,
                                         System.Globalization.CultureInfo.InvariantCulture, out v))
                        continue;
                    if (v > 0.0 && v < 1.0) result[key] = v;
                }
            }
            catch { }
            return result;
        }

        public static void SavePanels(IDictionary<string, double> panels)
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var kv in panels)
                    sb.Append(kv.Key).Append('=')
                      .Append(kv.Value.ToString("0.#####",
                              System.Globalization.CultureInfo.InvariantCulture))
                      .Append(Environment.NewLine);
                File.WriteAllText(PanelFile, sb.ToString());
            }
            catch { }
        }

        // ---- the cached server lists ----

        private static string ListDir
        {
            get
            {
                string d = Path.Combine(Dir, "lists");
                try { if (!Directory.Exists(d)) Directory.CreateDirectory(d); }
                catch { }
                return d;
            }
        }

        private static string ListFile(string key)
        {
            var safe = new System.Text.StringBuilder();
            foreach (char c in key)
                safe.Append(char.IsLetterOrDigit(c) ? c : '_');
            return Path.Combine(ListDir, safe + ".tsv");
        }

        /// <summary>When this list was last written, or MinValue if never.</summary>
        public static DateTime ListSavedAt(string key)
        {
            try
            {
                string f = ListFile(key);
                return File.Exists(f) ? File.GetLastWriteTime(f) : DateTime.MinValue;
            }
            catch { return DateTime.MinValue; }
        }

        /// <summary>How long a remembered server stays in the cache unseen.</summary>
        public static readonly TimeSpan ListMaxAge = TimeSpan.FromDays(7);

        /// <summary>
        /// Writes a browsed list to disk so the next launch can show it at once
        /// instead of staring at an empty table for a minute.
        ///
        /// Tab separated, one server per line, with the tab and newline
        /// characters stripped out of the free-text fields - server names
        /// contain almost anything, and one stray tab would shift every later
        /// column on that line.
        /// </summary>
        public static void SaveList(string key, IEnumerable<BrowserServer> servers,
                                    IDictionary<string, long> lastSeen)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                foreach (var s in servers)
                {
                    long seen;
                    if (lastSeen == null || !lastSeen.TryGetValue(s.Endpoint, out seen)) seen = 0;
                    sb.Append(Clean(s.Name)).Append('\t')
                      .Append(Clean(s.Map)).Append('\t')
                      .Append(Clean(s.GameDir)).Append('\t')
                      .Append(Clean(s.Tags)).Append('\t')
                      .Append(s.Host).Append('\t')
                      .Append(s.Port).Append('\t')
                      .Append(s.QueryPort).Append('\t')
                      .Append(s.Players).Append('\t')
                      .Append(s.MaxPlayers).Append('\t')
                      .Append(s.Ping).Append('\t')
                      .Append(s.AppId).Append('\t')
                      .Append(s.Password ? 1 : 0).Append('\t')
                      .Append(s.Secure ? 1 : 0).Append('\t')
                      .Append(seen).Append('\n');
                }
                File.WriteAllText(ListFile(key), sb.ToString(), System.Text.Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>
        /// Reads a list back. Anything not seen within ListMaxAge is dropped, so
        /// servers that have genuinely gone away do not accumulate for ever.
        /// A malformed line is skipped rather than failing the whole file.
        /// </summary>
        public static List<BrowserServer> LoadList(string key, IDictionary<string, long> lastSeen)
        {
            var result = new List<BrowserServer>();
            try
            {
                string file = ListFile(key);
                if (!File.Exists(file)) return result;

                long cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)ListMaxAge.TotalSeconds;
                foreach (string line in File.ReadAllLines(file, System.Text.Encoding.UTF8))
                {
                    if (line.Length == 0) continue;
                    string[] f = line.Split('\t');
                    if (f.Length < 14) continue;
                    try
                    {
                        long seen = long.Parse(f[13]);
                        if (seen > 0 && seen < cutoff) continue;

                        var s = new BrowserServer
                        {
                            Name = f[0], Map = f[1], GameDir = f[2], Tags = f[3],
                            Host = f[4],
                            Port = int.Parse(f[5]),
                            QueryPort = int.Parse(f[6]),
                            Players = int.Parse(f[7]),
                            MaxPlayers = int.Parse(f[8]),
                            Ping = int.Parse(f[9]),
                            AppId = uint.Parse(f[10]),
                            Password = f[11] == "1",
                            Secure = f[12] == "1"
                        };
                        if (s.Host.Length == 0 || s.Port <= 0) continue;
                        result.Add(s);
                        if (lastSeen != null) lastSeen[s.Endpoint] = seen;
                    }
                    catch { }
                }
            }
            catch { }
            return result;
        }

        private static string Clean(string v)
        {
            if (string.IsNullOrEmpty(v)) return "";
            return v.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        }

        // ---- addresses or /24 subnets the player has un-flagged ----

        private static string AllowFile { get { return Path.Combine(Dir, "allowed.txt"); } }

        public static HashSet<string> LoadAllowed()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(AllowFile))
                    foreach (string line in File.ReadAllLines(AllowFile))
                        if (line.Trim().Length > 0 && !line.StartsWith("#")) set.Add(line.Trim());
            }
            catch { }
            return set;
        }

        public static void SaveAllowed(IEnumerable<string> entries)
        {
            try
            {
                var lines = new List<string> { "# addresses or subnets always shown, one per line" };
                lines.AddRange(entries);
                File.WriteAllLines(AllowFile, lines.ToArray());
            }
            catch { }
        }

        public static string LoadName()
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    string saved = File.ReadAllText(SettingsFile).Trim();
                    if (saved.Length > 0) return saved;
                }
            }
            catch { }

            // Nothing saved here yet, so fall back to whatever DayZ was last
            // launched with. There is no registry key or config file holding the
            // in-game name - the official launcher only ever passes it as
            // "-name=" on the command line - but DayZ echoes its own command
            // line into the top of every RPT, so that is where it can be read.
            return DetectNameFromDayZ();
        }

        private static string DetectNameFromDayZ()
        {
            try
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var roots = new[]
                {
                    Path.Combine(local, "DayZ Exp"),
                    Path.Combine(local, "DayZ"),
                };

                var logs = new List<FileInfo>();
                foreach (string root in roots)
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (var f in new DirectoryInfo(root).GetFiles("*.RPT")) logs.Add(f);
                }
                if (logs.Count == 0) return "";

                logs.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));

                // The command line sits in the first few lines; RPTs get large,
                // so only the head of each is read, newest first.
                foreach (var f in logs.GetRange(0, Math.Min(logs.Count, 8)))
                {
                    string head = ReadHead(f.FullName, 4096);
                    // DayZ logs the whole argument quoted - "-name=Somebody" -
                    // so the unquoted branch must exclude quotes, or the closing
                    // one gets captured as part of the name.
                    var m = Regex.Match(head, @"-name=(?:""([^""]+)""|([^""\s]+))");
                    if (!m.Success) continue;

                    string name = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim();
                    if (name.Length > 0) return name;
                }
            }
            catch { }
            return "";
        }

        private static string ReadHead(string path, int bytes)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                           FileShare.ReadWrite | FileShare.Delete))
            {
                var buf = new byte[Math.Min(bytes, (int)Math.Min(fs.Length, int.MaxValue))];
                int read = fs.Read(buf, 0, buf.Length);
                return Encoding.UTF8.GetString(buf, 0, read);
            }
        }

        public static void SaveName(string name)
        {
            try { File.WriteAllText(SettingsFile, name ?? ""); }
            catch { }
        }
    }
}
