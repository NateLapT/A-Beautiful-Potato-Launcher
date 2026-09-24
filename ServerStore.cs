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
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ABeautifulPotatoLauncher
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
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string d = Path.Combine(appData, "ABeautifulPotatoLauncher");
                Directory.CreateDirectory(d);

                BringForwardOldData(appData, d);
                return d;
            }
        }

        /// <summary>
        /// Moves a previous installation's data to the current folder.
        ///
        /// The launcher began life covering DayZ Experimental only, and kept
        /// its files under "BeautifulPotatoExpLauncher", then briefly under
        /// "BeautifulPotatoLauncher". It covers every DayZ
        /// server now and the "Exp" is gone from the name - but a player's
        /// favourites, settings and the server index behind them are worth
        /// real time, and the index in particular is built up over many
        /// sessions. Renaming the folder without this would silently hand them
        /// an empty launcher.
        ///
        /// Runs once, recorded by a marker file rather than by whether the new
        /// folder exists. That folder can already be there and still be empty -
        /// an abandoned build, a half-started install - and testing for it
        /// meant the real data sat in the old folder untouched while the player
        /// stared at an empty launcher.
        ///
        /// Anything already present in the new folder WINS: this fills gaps, it
        /// never overwrites. And the old folder is copied rather than moved, so
        /// a failure part way through cannot destroy the only copy, and an
        /// older build still installed keeps working from it.
        /// </summary>
        private static void BringForwardOldData(string appData, string target)
        {
            try
            {
                string marker = Path.Combine(target, "migrated-from-exp.txt");
                if (File.Exists(marker)) return;

                // Two previous homes, newest first so it wins any tie: the
                // launcher was "BeautifulPotatoExpLauncher" while it covered
                // Experimental only, then briefly "BeautifulPotatoLauncher"
                // before the name was settled with its leading "A".
                string[] previous =
                {
                    Path.Combine(appData, "BeautifulPotatoLauncher"),
                    Path.Combine(appData, "BeautifulPotatoExpLauncher")
                };

                int copied = 0;
                var from = new List<string>();

                foreach (string old in previous)
                {
                    if (!Directory.Exists(old)) continue;
                    from.Add(Path.GetFileName(old));

                    copied += CopyMissing(old, target);

                    foreach (string sub in Directory.GetDirectories(old))
                    {
                        string into = Path.Combine(target, Path.GetFileName(sub));
                        Directory.CreateDirectory(into);
                        copied += CopyMissing(sub, into);
                    }
                }

                File.WriteAllText(marker, from.Count == 0
                    ? "Nothing to migrate.\r\n"
                    : "Brought " + copied + " file(s) forward from "
                      + string.Join(", ", from.ToArray()) + " on "
                      + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + ".\r\n"
                      + "The old folder(s) were left untouched and can be deleted by hand.\r\n");
            }
            catch
            {
                // Starting fresh is a poor outcome but a working one. Failing to
                // open because an old file could not be copied is not.
            }
        }

        /// <summary>Copies files that the target does not already have. Returns how many.</summary>
        private static int CopyMissing(string from, string to)
        {
            int copied = 0;
            foreach (string file in Directory.GetFiles(from))
            {
                string dest = Path.Combine(to, Path.GetFileName(file));
                if (File.Exists(dest)) continue;
                try { File.Copy(file, dest); copied++; }
                catch { }
            }
            return copied;
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
                    "# A Beautiful Potato Launcher - server list",
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

        public static void SetName(string endpoint, string name)
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

        public static bool LoadWindow(out Point location, out Size size)
        {
            int x, y, w, h;
            bool maximised;
            if (!LoadWindow(out x, out y, out w, out h, out maximised))
            {
                location = Point.Empty;
                size = Size.Empty;
                return false;
            }

            location = new Point(x, y);
            size = new Size(w, h);
            return true;
        }

        public static void SaveWindow(Point location, Size size)
        {
            SaveWindow(location.X, location.Y, size.Width, size.Height, false);
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

        // ---- per-mod launch choices ----

        private static string ModOverrideFile { get { return Path.Combine(Dir, "modchoices.tsv"); } }

        public static Dictionary<string, ModOverride> LoadModOverrides()
        {
            var result = new Dictionary<string, ModOverride>();
            try
            {
                if (!File.Exists(ModOverrideFile)) return result;
                foreach (string line in File.ReadAllLines(ModOverrideFile, Encoding.UTF8))
                {
                    if (line.Length == 0) continue;
                    string[] f = line.Split('\t');
                    if (f.Length < 3) continue;
                    if (f[0].Length == 0) continue;
                    result[f[0]] = new ModOverride { Enabled = f[1] == "1", Folder = f[2] };
                }
            }
            catch { }
            return result;
        }

        public static void SaveModOverrides(IDictionary<string, ModOverride> map)
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var kv in map)
                    sb.Append(kv.Key).Append('\t')
                      .Append(kv.Value.Enabled ? 1 : 0).Append('\t')
                      .Append(Clean(kv.Value.Folder)).Append('\n');
                File.WriteAllText(ModOverrideFile, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        // ---- the mod library index ----

        private static string ModIndexFile { get { return Path.Combine(Dir, "mods.tsv"); } }

        /// <summary>
        /// The saved mod index. Tab separated, one mod per line, with the
        /// free-text fields stripped of tabs and newlines so a mod name can
        /// never shift the columns of its own row.
        /// </summary>
        public static void SaveModIndex(IEnumerable<ModEntry> mods)
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var m in mods)
                {
                    sb.Append(m.WorkshopId).Append('\t')
                      .Append(Clean(m.Name)).Append('\t')
                      .Append(Clean(m.Folder)).Append('\t')
                      .Append(m.MetaStamp).Append('\t')
                      .Append(Stamp(m.InstalledAt)).Append('\t')
                      .Append(m.SizeBytes).Append('\t')
                      .Append(Clean(m.Author)).Append('\t')
                      .Append(Clean(m.Version)).Append('\t')
                      .Append(m.HasMeta ? 1 : 0).Append('\t')
                      .Append(m.HasKeysFolder ? 1 : 0).Append('\t')
                      .Append(m.KeyFileCount).Append('\t')
                      .Append(m.PboCount).Append('\t')
                      .Append(m.SignatureCount).Append('\t')
                      .Append(Stamp(m.LastLoaded)).Append('\t')
                      .Append(Stamp(m.DeepScannedAt)).Append('\n');
                }
                File.WriteAllText(ModIndexFile, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        public static List<ModEntry> LoadModIndex()
        {
            var result = new List<ModEntry>();
            try
            {
                if (!File.Exists(ModIndexFile)) return result;
                foreach (string line in File.ReadAllLines(ModIndexFile, Encoding.UTF8))
                {
                    if (line.Length == 0) continue;
                    string[] f = line.Split('\t');
                    if (f.Length < 15) continue;
                    try
                    {
                        result.Add(new ModEntry
                        {
                            WorkshopId = ulong.Parse(f[0]),
                            Name = f[1],
                            Folder = f[2],
                            MetaStamp = ulong.Parse(f[3]),
                            InstalledAt = FromStamp(f[4]),
                            SizeBytes = long.Parse(f[5]),
                            Author = f[6],
                            Version = f[7],
                            HasMeta = f[8] == "1",
                            HasKeysFolder = f[9] == "1",
                            KeyFileCount = int.Parse(f[10]),
                            PboCount = int.Parse(f[11]),
                            SignatureCount = int.Parse(f[12]),
                            LastLoaded = FromStamp(f[13]),
                            DeepScannedAt = FromStamp(f[14])
                        });
                    }
                    catch { }
                }
            }
            catch { }
            return result;
        }

        private static string Stamp(DateTime t)
        {
            return t == DateTime.MinValue ? "0"
                 : t.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
        }

        private static DateTime FromStamp(string v)
        {
            long ticks;
            if (!long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks) || ticks <= 0)
                return DateTime.MinValue;
            // Kept in UTC, exactly as written. An earlier version converted to
            // local time here, which made every InstalledAt differ from the
            // File.GetLastWriteTimeUtc it is compared against - so the index
            // looked stale on every run and the expensive size scan was redone
            // from scratch each time. Display converts; storage does not.
            try { return new DateTime(ticks, DateTimeKind.Utc); }
            catch { return DateTime.MinValue; }
        }

        // ---- an extra folder the player keeps mods in ----

        private static string ExtraModFile { get { return Path.Combine(Dir, "extramods.txt"); } }

        /// <summary>
        /// A second place to look for mods, chosen by the player. Empty when
        /// they have not set one, which is the normal case.
        /// </summary>
        public static string LoadExtraModPath()
        {
            try
            {
                if (!File.Exists(ExtraModFile)) return "";
                return File.ReadAllText(ExtraModFile).Trim();
            }
            catch { return ""; }
        }

        public static void SaveExtraModPath(string path)
        {
            try { File.WriteAllText(ExtraModFile, path ?? ""); }
            catch { }
        }

        // ---- the map index ----
        //
        // Every map name ever seen, one per line, kept forever.
        //
        // This is the key to getting past Steam's 10,000-server cap: the cap is
        // per REQUEST, so asking map by map returns far more of the list than
        // asking once. To ask map by map you have to know the maps, and there
        // is no endpoint that lists them - they are whatever community map
        // makers have released. So the launcher learns them from the servers it
        // sees, and never forgets: a map seen once is worth asking about again,
        // even if nobody happens to be running it today.

        private static string MapsFile { get { return Path.Combine(Dir, "maps.txt"); } }

        public static HashSet<string> LoadKnownMaps()
        {
            var maps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(MapsFile)) return maps;
                foreach (string line in File.ReadAllLines(MapsFile))
                {
                    string m = line.Trim();
                    if (m.Length > 0) maps.Add(m);
                }
            }
            catch { }
            return maps;
        }

        /// <summary>
        /// Adds these maps to the ones already known and writes the file back.
        /// Returns how many were new, so the caller can say so.
        /// </summary>
        public static int RememberMaps(IEnumerable<string> maps)
        {
            if (maps == null) return 0;
            try
            {
                var known = LoadKnownMaps();
                int before = known.Count;

                foreach (string m in maps)
                {
                    if (m == null) continue;
                    string t = m.Trim();
                    // A map name with whitespace or control characters in it is
                    // a misread, not a map - it would poison every later query.
                    if (t.Length == 0 || t.Length > 64) continue;
                    if (t.IndexOfAny(new[] { '\t', '\r', '\n' }) >= 0) continue;
                    known.Add(t);
                }

                if (known.Count == before) return 0;

                var sorted = new List<string>(known);
                sorted.Sort(StringComparer.OrdinalIgnoreCase);
                File.WriteAllLines(MapsFile, sorted.ToArray());
                return known.Count - before;
            }
            catch { return 0; }
        }

        private static string MapStatsFile { get { return Path.Combine(Dir, "map-counts.tsv"); } }

        /// <summary>
        /// How many servers each map returned the last two times it was asked
        /// about. Tab separated: map, previous count, latest count.
        /// </summary>
        public static Dictionary<string, int[]> LoadMapCounts()
        {
            var result = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(MapStatsFile)) return result;
                foreach (string line in File.ReadAllLines(MapStatsFile))
                {
                    var f = line.Split('\t');
                    if (f.Length < 3) continue;
                    int a, b;
                    if (!int.TryParse(f[1], out a) || !int.TryParse(f[2], out b)) continue;
                    result[f[0]] = new[] { a, b };
                }
            }
            catch { }
            return result;
        }

        /// <summary>
        /// Files away what each map returned this time, keeping the previous
        /// figure alongside it. Two readings rather than one so a single
        /// hiccup - Steam busy, a dropped packet - cannot condemn a map.
        /// </summary>
        public static void RecordMapCounts(IDictionary<string, int> latest)
        {
            if (latest == null || latest.Count == 0) return;
            try
            {
                var all = LoadMapCounts();

                foreach (var kv in latest)
                {
                    int[] prev;
                    int before = all.TryGetValue(kv.Key, out prev) ? prev[1] : -1;
                    all[kv.Key] = new[] { before, kv.Value };
                }

                var lines = new List<string>();
                foreach (var kv in all)
                    lines.Add(kv.Key + "\t" + kv.Value[0] + "\t" + kv.Value[1]);
                lines.Sort(StringComparer.OrdinalIgnoreCase);

                File.WriteAllLines(MapStatsFile, lines.ToArray());
            }
            catch { }
        }

        /// <summary>
        /// Maps that returned nothing on BOTH of the last two sweeps.
        ///
        /// Asking about these again is a round trip that reliably returns zero,
        /// so the sweep skips them. They stay in the map index, and one
        /// non-zero reading is enough to bring a map straight back.
        /// </summary>
        public static HashSet<string> BarrenMaps()
        {
            var barren = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in LoadMapCounts())
                if (kv.Value[0] == 0 && kv.Value[1] == 0) barren.Add(kv.Key);
            return barren;
        }

        // ---- which mods each server runs ----
        //
        // A server's mod list only arrives from A2S_RULES, one server at a
        // time, and a full sweep of twelve thousand takes minutes. Filtering by
        // mod would be useless if it started from nothing every launch, so what
        // has been learnt is kept: the filter answers instantly from this, and
        // servers that have never been asked fill in as the sweep reaches them.

        private static string ModListsFile { get { return Path.Combine(Dir, "server-mods.tsv"); } }

        private static string ConfirmedFile { get { return Path.Combine(Dir, "steam-confirmed.tsv"); } }

        /// <summary>
        /// workshop id -> the publication time (UTC ticks) at which Steam last
        /// confirmed our copy was current. See SteamWorkshop.ConfirmCurrent.
        /// </summary>
        public static Dictionary<ulong, long> LoadSteamConfirmed()
        {
            var result = new Dictionary<ulong, long>();
            try
            {
                if (!File.Exists(ConfirmedFile)) return result;
                foreach (string line in File.ReadAllLines(ConfirmedFile))
                {
                    var f = line.Split('\t');
                    ulong id; long ticks;
                    if (f.Length < 2 || !ulong.TryParse(f[0], out id) || !long.TryParse(f[1], out ticks))
                        continue;
                    result[id] = ticks;
                }
            }
            catch { }
            return result;
        }

        public static void SaveSteamConfirmed(IDictionary<ulong, long> map)
        {
            if (map == null) return;
            try
            {
                var sb = new StringBuilder();
                foreach (var kv in map) sb.Append(kv.Key).Append('\t').Append(kv.Value).AppendLine();
                File.WriteAllText(ConfirmedFile, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>endpoint -> the mod names that server asked for.</summary>
        public static Dictionary<string, List<string>> LoadServerMods()
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(ModListsFile)) return result;

                foreach (string line in File.ReadAllLines(ModListsFile))
                {
                    if (line.Length == 0 || line[0] == '#') continue;

                    var f = line.Split('\t');
                    if (f.Length < 2) continue;

                    var mods = new List<string>();
                    for (int i = 1; i < f.Length; i++)
                        if (f[i].Length > 0) mods.Add(f[i]);

                    result[f[0]] = mods;
                }
            }
            catch { }
            return result;
        }

        public static void SaveServerMods(IDictionary<string, List<string>> byEndpoint)
        {
            if (byEndpoint == null) return;
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# server endpoint, then one column per mod name");

                foreach (var kv in byEndpoint)
                {
                    if (kv.Value == null || kv.Value.Count == 0) continue;

                    sb.Append(kv.Key);
                    foreach (string m in kv.Value)
                    {
                        if (string.IsNullOrEmpty(m)) continue;
                        sb.Append('\t').Append(m.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' '));
                    }
                    sb.AppendLine();
                }

                File.WriteAllText(ModListsFile, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        private static string DescFile { get { return Path.Combine(Dir, "server-desc.tsv"); } }

        /// <summary>
        /// endpoint -> the server's description line, for the servers that have
        /// been queried. Kept beside the mod lists and for the same reason:
        /// filtering on it has to work before the sweep has run again.
        /// </summary>
        public static Dictionary<string, string> LoadServerDescriptions()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(DescFile)) return result;

                foreach (string line in File.ReadAllLines(DescFile))
                {
                    if (line.Length == 0 || line[0] == '#') continue;
                    var f = line.Split('\t');
                    if (f.Length < 2 || f[0].Length == 0) continue;
                    result[f[0]] = f[1];
                }
            }
            catch { }
            return result;
        }

        public static void SaveServerDescriptions(IDictionary<string, string> byEndpoint)
        {
            if (byEndpoint == null) return;
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# server endpoint, then its description");

                foreach (var kv in byEndpoint)
                {
                    if (string.IsNullOrEmpty(kv.Value)) continue;
                    sb.Append(kv.Key).Append('\t')
                      .Append(kv.Value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' '))
                      .AppendLine();
                }

                File.WriteAllText(DescFile, sb.ToString(), Encoding.UTF8);
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
        /// <summary>
        /// How long a remembered server stays in the index unseen.
        ///
        /// WHY THIS IS NOT A WEEK ANY MORE
        ///   An ordinary refresh only asks about a SLICE of the known maps -
        ///   twelve out of well over a hundred - so a live server on a quiet
        ///   map can easily go a fortnight without turning up in a reply. At
        ///   seven days the index was throwing away perfectly good servers for
        ///   the crime of being on an unfashionable map, which is the opposite
        ///   of what an index is for. A month outlasts the rotation.
        /// </summary>
        public static readonly TimeSpan ListMaxAge = TimeSpan.FromDays(30);

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
                IEnumerable<string> lines;
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                bool seeded = false;
                if (File.Exists(file))
                    lines = File.ReadAllLines(file, System.Text.Encoding.UTF8);
                else if ((lines = DefaultListLines(key)) != null)
                    seeded = true;
                else
                    return result;

                long cutoff = now - (long)ListMaxAge.TotalSeconds;
                foreach (string line in lines)
                {
                    if (line.Length == 0) continue;
                    string[] f = line.Split('\t');
                    if (f.Length < 14) continue;
                    try
                    {
                        long seen = long.Parse(f[13]);
                        if (seen > 0 && seen < cutoff) continue;

                        // The built-in list carries no dates. Stamped as seen
                        // now, so anything a real refresh never finds again
                        // ages out after ListMaxAge like any other server,
                        // rather than living for ever at 0.
                        if (seeded) seen = now;

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

        /// <summary>
        /// The server list built into the exe (assets/default-servers.tsv.gz,
        /// see tools/build-default-servers.py), for a browsing tab that has no
        /// saved list yet - so a fresh install opens full instead of empty and
        /// waiting a minute on Steam. Null for the personal tabs (Recent,
        /// Friends, LAN, Favourites), which are the player's own and must not
        /// be filled from anywhere else, and for Experimental: the built-in
        /// list holds stable servers only.
        /// </summary>
        private static IEnumerable<string> DefaultListLines(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            bool browsing = key.StartsWith("Community/", StringComparison.OrdinalIgnoreCase)
                         || key.StartsWith("Official/", StringComparison.OrdinalIgnoreCase);
            if (!browsing || key.EndsWith("/exp", StringComparison.OrdinalIgnoreCase)) return null;

            try
            {
                var asm = typeof(ServerStore).Assembly;
                using (var raw = asm.GetManifestResourceStream("default-servers.tsv.gz"))
                {
                    if (raw == null) return null;
                    using (var gz = new System.IO.Compression.GZipStream(raw, System.IO.Compression.CompressionMode.Decompress))
                    using (var rd = new StreamReader(gz, System.Text.Encoding.UTF8))
                    {
                        var lines = new List<string>(120000);
                        string line;
                        while ((line = rd.ReadLine()) != null) lines.Add(line);
                        return lines;
                    }
                }
            }
            catch { return null; }
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
