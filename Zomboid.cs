// ---------------------------------------------------------------------------
//  Project Zomboid: everything that differs from DayZ.
//
//  Verified against Build 42.21 and the live master list, 3 October 2026.
//
//  THE SERVER LIST
//    The same Steam master list, as app 108600. Steam caps any single request
//    at 10,000 like it does for DayZ, and the usual ways round it do not work
//    here: PZ separates its tags with ';' (";modded;pvp;VERSION:42.21"), so
//    Steam's tag filters match nothing, and the map filter returns nothing
//    either. Steam's name filter does work, so the list is swept by the first
//    character of the server name instead - see NameSlices.
//
//  QUERY PORT
//    The same as the game port. Every one of 10,000 servers sampled reported
//    them equal, unlike DayZ's game port + 1.
//
//  WHAT A SERVER SAYS ABOUT ITSELF
//    A2S_RULES carries plain key/values - version, pvp, open, public,
//    modCount, lastWipe - with long values split into numbered pieces:
//    "description:1/11" ... "description:11/11", "mods:1/1". The mod list is
//    MOD IDS ("damnlib;tsarslib"), sometimes with a leading backslash, and
//    never workshop ids. The game asks the server for the workshop ids while
//    it connects and downloads whatever is missing by itself, so the launcher
//    only has to show the list - but to offer Sub and Verify it needs the
//    workshop id, so one is found: from the mods already on disk first, then
//    by asking the Steam Workshop (see SteamWorkshop.FindZomboidMods).
//
//  JOINING
//    ProjectZomboid64.exe +connect host:port +password pw. The game opens its
//    own "connect to server" popup with the server password filled in; the
//    account username and password are the player's to type there, once per
//    server, and the game remembers them after that.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ABeautifulPotatoLauncher
{
    internal static class Zomboid
    {
        public const uint AppId = 108600;
        public const string InstallFolder = "ProjectZomboid";
        public const string GameExe = "ProjectZomboid64.exe";
        public const int DefaultPort = 16261;

        // ------------------------------------------------------------ tags --

        /// <summary>A tag string split on both separators, empty entries dropped.</summary>
        public static IEnumerable<string> Tags(string tags)
        {
            if (string.IsNullOrEmpty(tags)) yield break;
            foreach (string part in tags.Split(';', ','))
            {
                string t = part.Trim();
                if (t.Length > 0) yield return t;
            }
        }

        public static bool HasTag(string tags, string tag)
        {
            return Tags(tags).Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The server's game version from its tags, e.g. "42.21", or "".</summary>
        public static string VersionOf(string tags)
        {
            foreach (string t in Tags(tags))
                if (t.StartsWith("VERSION:", StringComparison.OrdinalIgnoreCase))
                    return t.Substring(8).Trim();
            return "";
        }

        /// <summary>
        /// Whether a list entry is a Project Zomboid server at all.
        ///
        /// Steam's list for app 108600 is not only Zomboid. Measured 3 October
        /// 2026, of 35,536 entries: 33,440 report the game folder "zomboid", and
        /// the rest are other games - 711 V Rising, 570 "usermaps", 228 CS:GO,
        /// 106 Left 4 Dead and a tail of others - all of which the game could
        /// never join. Every server states its game folder, so that is the test.
        /// An entry with none (one found by asking a single address directly)
        /// is given the benefit of the doubt.
        /// </summary>
        public static bool IsZomboidServer(BrowserServer s)
        {
            if (s == null) return false;
            string dir = (s.GameDir ?? "").Trim();
            return dir.Length == 0 || dir.Equals("zomboid", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsModded(string tags) { return HasTag(tags, "modded"); }
        public static bool IsPvp(string tags)    { return HasTag(tags, "pvp"); }

        // --------------------------------------------------------- version --

        private static string _localVersion;

        /// <summary>
        /// The installed game's version, e.g. "42.21.0", from the version.txt
        /// the game writes into the player's Zomboid folder each time it runs.
        /// Empty when the game has never been started.
        /// </summary>
        public static string LocalVersion
        {
            get
            {
                if (_localVersion != null) return _localVersion;
                _localVersion = "";
                try
                {
                    string path = Path.Combine(UserFolder, "version.txt");
                    if (File.Exists(path))
                    {
                        string first = File.ReadAllLines(path).FirstOrDefault() ?? "";
                        _localVersion = first.Split(' ')[0].Trim();
                    }
                }
                catch { }
                return _localVersion;
            }
        }

        /// <summary>
        /// Whether two versions are the same release. "42.21" and "42.21.0" are:
        /// servers publish two parts and the game writes three.
        /// </summary>
        public static bool SameVersion(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return Normalise(a) == Normalise(b);
        }

        private static string Normalise(string v)
        {
            var parts = v.Trim().Split('.').ToList();
            while (parts.Count > 2 && parts[parts.Count - 1] == "0") parts.RemoveAt(parts.Count - 1);
            return string.Join(".", parts);
        }

        /// <summary>Sorts version strings newest first, numerically.</summary>
        public static int CompareVersionsDescending(string a, string b)
        {
            var pa = (a ?? "").Split('.');
            var pb = (b ?? "").Split('.');
            for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
            {
                int x, y;
                int.TryParse(i < pa.Length ? pa[i] : "0", out x);
                int.TryParse(i < pb.Length ? pb[i] : "0", out y);
                if (x != y) return y.CompareTo(x);
            }
            return string.Compare(b, a, StringComparison.Ordinal);
        }

        // ----------------------------------------------------------- rules --

        private static readonly Regex Piece = new Regex(@"^(.+):(\d+)/(\d+)$");

        /// <summary>
        /// Turns a server's text rules into the launcher's ServerRules: long
        /// values rejoined from their numbered pieces, the description cleaned
        /// of the game's markup, and the mod ids as Mods.
        ///
        /// Complete is true only when every piece arrived and, where the server
        /// states a modCount, the list holds that many.
        /// </summary>
        public static ServerRules ParseRules(Dictionary<string, string> text)
        {
            var rules = new ServerRules();
            if (text == null) return rules;

            var plain = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var pieces = new Dictionary<string, SortedDictionary<int, string>>(StringComparer.OrdinalIgnoreCase);
            var expected = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var kv in text)
            {
                var m = Piece.Match(kv.Key);
                if (!m.Success) { plain[kv.Key] = kv.Value; continue; }

                string key = m.Groups[1].Value;
                int index = int.Parse(m.Groups[2].Value);
                int count = int.Parse(m.Groups[3].Value);

                SortedDictionary<int, string> parts;
                if (!pieces.TryGetValue(key, out parts)) pieces[key] = parts = new SortedDictionary<int, string>();
                parts[index] = kv.Value;
                expected[key] = count;
            }

            bool whole = true;
            foreach (var kv in pieces)
            {
                plain[kv.Key] = string.Concat(kv.Value.Values);
                if (kv.Value.Count != expected[kv.Key]) whole = false;
            }

            foreach (var kv in plain) rules.Text[kv.Key] = kv.Value;

            string desc;
            if (plain.TryGetValue("description", out desc)) rules.Description = CleanDescription(desc);

            string mods;
            if (plain.TryGetValue("mods", out mods))
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string raw in mods.Split(';'))
                {
                    string id = CleanModId(raw);
                    if (id.Length == 0 || !seen.Add(id)) continue;
                    rules.Mods.Add(new Mod(id, WorkshopIdFor(id)));
                }
            }

            int stated;
            string count2;
            if (plain.TryGetValue("modCount", out count2) && int.TryParse(count2, out stated)
                && stated != rules.Mods.Count) whole = false;

            rules.Complete = whole;
            return rules;
        }

        /// <summary>A mod id as the game knows it: no backslash prefix, no spaces round it.</summary>
        public static string CleanModId(string raw)
        {
            return (raw ?? "").Trim().TrimStart('\\').Trim();
        }

        /// <summary>
        /// The game's rich-text markup, turned into plain text: &lt;LINE&gt; is a
        /// line break, colour and size tags are dropped, and a literal "\n"
        /// typed into the server's ini becomes a real one.
        /// </summary>
        public static string CleanDescription(string d)
        {
            if (string.IsNullOrEmpty(d)) return "";
            string s = Regex.Replace(d, @"\s*<\s*(LINE|BR)\s*>\s*", "\r\n", RegexOptions.IgnoreCase);
            s = s.Replace("\\n", "\r\n");
            s = Regex.Replace(s, @"<\s*(RGB|RGBA|SIZE|CENTRE|CENTER|LEFT|RIGHT|PUSHRGB|POPRGB|SPACE|INDENT|IMAGE|IMAGECENTRE|H1|H2|TEXT)\b[^>]*>",
                              "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"[ \t]+\r\n", "\r\n");
            return s.Trim();
        }

        // ------------------------------------------------ mod id -> workshop --

        public static string UserFolder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Zomboid"); }
        }

        private static readonly object MapLock = new object();
        private static Dictionary<string, ulong> _installed;     // mod id -> workshop id, from disk
        private static HashSet<string> _userMods;                // mod ids in %USERPROFILE%\Zomboid\mods
        private static Dictionary<string, ModLookup> _found;     // mod id -> what the Workshop said

        internal sealed class ModLookup
        {
            public ulong WorkshopId;      // 0 = searched, nothing matched
            public string Title = "";
            public DateTime AskedUtc;
        }

        /// <summary>How long "not found on the Workshop" is believed before asking again.</summary>
        private static readonly TimeSpan RetryNotFound = TimeSpan.FromDays(1);

        /// <summary>Forgets what is on disk, for after a download or a removal.</summary>
        public static void Invalidate()
        {
            lock (MapLock) { _installed = null; _userMods = null; }
        }

        /// <summary>
        /// The workshop item that provides this mod id, or 0 when it is not known.
        /// What is installed wins over a search result: if the player has a copy,
        /// that copy is the one the game will find.
        /// </summary>
        public static ulong WorkshopIdFor(string modId)
        {
            if (string.IsNullOrEmpty(modId)) return 0;
            lock (MapLock)
            {
                EnsureScanned();
                ulong id;
                if (_installed.TryGetValue(modId, out id)) return id;

                EnsureFound();
                ModLookup look;
                if (_found.TryGetValue(modId, out look)) return look.WorkshopId;
            }
            return 0;
        }

        /// <summary>The Workshop title for a mod id, when a search has found one.</summary>
        public static string TitleFor(string modId)
        {
            lock (MapLock)
            {
                EnsureFound();
                ModLookup look;
                return _found.TryGetValue(modId ?? "", out look) ? look.Title : "";
            }
        }

        /// <summary>A mod sitting in the player's own Zomboid\mods folder rather than the Workshop.</summary>
        public static bool IsUserMod(string modId)
        {
            lock (MapLock)
            {
                EnsureScanned();
                return _userMods.Contains(modId ?? "");
            }
        }

        /// <summary>
        /// Mod ids whose workshop item is unknown and worth asking the Workshop
        /// about - not on disk, and not recently searched for without result.
        /// </summary>
        public static List<string> NeedLookup(IEnumerable<string> modIds)
        {
            var want = new List<string>();
            lock (MapLock)
            {
                EnsureScanned();
                EnsureFound();
                foreach (string id in modIds)
                {
                    if (string.IsNullOrEmpty(id) || _installed.ContainsKey(id) || _userMods.Contains(id)) continue;
                    ModLookup look;
                    if (_found.TryGetValue(id, out look)
                        && (look.WorkshopId != 0 || DateTime.UtcNow - look.AskedUtc < RetryNotFound)) continue;
                    if (!want.Contains(id)) want.Add(id);
                }
            }
            return want;
        }

        /// <summary>Records what the Workshop said about a mod id, and saves it.</summary>
        public static void RememberLookups(IDictionary<string, ModLookup> results)
        {
            if (results == null || results.Count == 0) return;
            lock (MapLock)
            {
                EnsureFound();
                foreach (var kv in results) _found[kv.Key] = kv.Value;
                ServerStore.SaveZomboidModIds(_found);
            }
        }

        private static void EnsureFound()
        {
            if (_found == null) _found = ServerStore.LoadZomboidModIds();
        }

        /// <summary>
        /// Reads every mod.info under the PZ workshop folder and the player's own
        /// mods folder. Layout, B41 and B42 both:
        ///   workshop\content\108600\&lt;workshop id&gt;\mods\&lt;folder&gt;\mod.info
        ///   ...\mods\&lt;folder&gt;\42\mod.info, ...\42.13\mod.info, ...\common\...
        /// so a few levels are searched, never the whole tree.
        /// </summary>
        private static void EnsureScanned()
        {
            if (_installed != null) return;

            var map = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
            var user = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (string root in WorkshopContentRoots())
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (string item in Directory.EnumerateDirectories(root))
                    {
                        ulong wsid;
                        if (!ulong.TryParse(Path.GetFileName(item), out wsid)) continue;
                        foreach (string modId in ModIdsUnder(Path.Combine(item, "mods")))
                            if (!map.ContainsKey(modId)) map[modId] = wsid;
                    }
                }
            }
            catch { }

            try
            {
                foreach (string modId in ModIdsUnder(Path.Combine(UserFolder, "mods"))) user.Add(modId);
            }
            catch { }

            _installed = map;
            _userMods = user;
        }

        private static IEnumerable<string> WorkshopContentRoots()
        {
            string steam = SteamPathForScan;
            if (string.IsNullOrEmpty(steam)) yield break;
            foreach (string lib in SteamLibraries.All(steam))
                yield return Path.Combine(lib, "steamapps", "workshop", "content", AppId.ToString());
        }

        /// <summary>Set by the form once Steam has been found; the scan reads it.</summary>
        public static string SteamPathForScan;

        private static IEnumerable<string> ModIdsUnder(string modsDir)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(modsDir)) return ids;

            foreach (string modFolder in Directory.EnumerateDirectories(modsDir))
            {
                var infos = new List<string> { Path.Combine(modFolder, "mod.info") };
                try
                {
                    foreach (string sub in Directory.EnumerateDirectories(modFolder))
                        infos.Add(Path.Combine(sub, "mod.info"));
                }
                catch { }

                foreach (string info in infos)
                {
                    string id = ReadModId(info);
                    if (id.Length > 0) ids.Add(id);
                }
            }
            return ids;
        }

        private static string ReadModId(string modInfo)
        {
            try
            {
                if (!File.Exists(modInfo)) return "";
                foreach (string line in File.ReadAllLines(modInfo))
                {
                    string t = line.Trim();
                    if (!t.StartsWith("id=", StringComparison.OrdinalIgnoreCase)) continue;
                    return CleanModId(t.Substring(3));
                }
            }
            catch { }
            return "";
        }

        /// <summary>
        /// Does a Workshop description say it provides this mod id? PZ authors
        /// write "Mod ID: Foo" (often with BBCode round it), and that line is what
        /// separates the real item from a reupload or a patch that merely
        /// mentions the name.
        /// </summary>
        public static bool DescriptionDeclares(string description, string modId)
        {
            if (string.IsNullOrEmpty(description) || string.IsNullOrEmpty(modId)) return false;
            string clean = Regex.Replace(description, @"\[/?[a-z0-9*]+[^\]]*\]", " ", RegexOptions.IgnoreCase);
            return Regex.IsMatch(clean,
                @"Mod\s*ID\s*[:=]\s*\\?" + Regex.Escape(modId) + @"(?![A-Za-z0-9_\-.])",
                RegexOptions.IgnoreCase);
        }

        // ------------------------------------------------- the 10,000 cap --

        /// <summary>
        /// Name prefixes for sweeping past Steam's 10,000 cap. Steam's
        /// name_match accepts a trailing wildcard, and "a*" alone returned 1,174
        /// servers when the plain request was capped at 10,000, so slicing by
        /// the first character reaches the servers the cap was hiding.
        /// Brackets and symbols are included because so many servers open with
        /// a tag like "[EU]" or "|PVP|".
        /// </summary>
        public static readonly string[] NameSlices = BuildSlices();

        private static string[] BuildSlices()
        {
            var list = new List<string>();
            for (char c = 'a'; c <= 'z'; c++) list.Add(c + "*");
            for (char c = '0'; c <= '9'; c++) list.Add(c + "*");
            foreach (string s in new[] { "[", "(", "{", "|", "-", "!", "#", "<", "~", "=" }) list.Add(s + "*");
            return list.ToArray();
        }

        // ---------------------------------------------------------- launch --

        /// <summary>The installed game folder, in any Steam library, or null.</summary>
        public static string FindGameDir(string steamPath)
        {
            if (string.IsNullOrEmpty(steamPath)) return null;
            foreach (string root in SteamLibraries.All(steamPath))
            {
                string candidate = Path.Combine(root, "steamapps", "common", InstallFolder);
                if (File.Exists(Path.Combine(candidate, GameExe))) return candidate;
            }
            return null;
        }

        /// <summary>
        /// The command line for joining a server. The password goes in quotes
        /// so one containing spaces survives.
        /// </summary>
        public static string ConnectArgs(string host, int port, string password)
        {
            var sb = new StringBuilder();
            sb.Append("+connect ").Append(host).Append(':').Append(port);
            if (!string.IsNullOrEmpty(password))
                sb.Append(" +password \"").Append(password.Replace("\"", "")).Append('"');
            return sb.ToString();
        }
    }
}
