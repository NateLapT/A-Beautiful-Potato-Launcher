// ---------------------------------------------------------------------------
//  The local mod library, indexed for speed.
//
//  WHY AN INDEX EXISTS AT ALL
//    DayZ mod folders get enormous - 868 of them on the machine this was built
//    against, totalling 357 GB. The official launcher walks all of that before
//    it will show you a list, which is why it takes minutes and hammers the
//    drive. Measured here, that full walk costs 5,370 ms.
//
//    But almost nothing a player wants to SEE requires it. Timed over the same
//    868 mods:
//
//        list the folders ................     1 ms
//        + read every meta.cpp ..........     90 ms   <- names and ids
//        + check for a keys folder ......     47 ms
//        + count pbos in addons .........    163 ms
//        + total every file on disk .....  5,370 ms   <- the only slow one
//
//    So the list is built from the cheap facts and shown immediately, and the
//    one expensive fact - size on disk - is filled in afterwards by a
//    background pass. The result is saved, so the next launch shows everything
//    at once and only re-reads folders that actually changed.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ABeautifulPotatoLauncher
{
    /// <summary>What the manager knows about one installed mod.</summary>
    internal sealed class ModEntry
    {
        public ulong WorkshopId;
        public string Name = "";
        public string Folder = "";

        /// <summary>
        /// The name inside a local mod's mod.cpp, which is what a server
        /// reports - "BeautifulEarplugs (testing)" for the @BeautifulEarplugs
        /// folder. Empty when there is no mod.cpp or no name in it.
        /// </summary>
        public string ModCppName = "";

        /// <summary>The publication stamp inside meta.cpp; 0 when unreadable.</summary>
        public ulong MetaStamp;

        /// <summary>
        /// When Steam last wrote meta.cpp - i.e. when this copy arrived.
        /// Held in UTC so it can be compared directly with what the filesystem
        /// reports; callers convert for display.
        /// </summary>
        public DateTime InstalledAt = DateTime.MinValue;

        /// <summary>Filled by the deep pass. -1 means "not measured yet".</summary>
        public long SizeBytes = -1;

        public string Author = "";
        public string Version = "";

        public bool HasMeta;

        /// <summary>
        /// The folder holds something DayZ could load: an addons directory with
        /// files in it, packed or not.
        /// </summary>
        public bool HasContent = true;

        /// <summary>
        /// Why this mod is in the state it is, in words, for the tooltip.
        /// </summary>
        public string StatusDetail(string steamPath)
        {
            string st = Status(steamPath);

            switch (st)
            {
                case "Unpacked":
                    return "This mod has no meta.cpp, so Steam did not install it as a package - "
                         + "but its addons folder does contain content.\r\n\r\n"
                         + "That is what an extracted or hand-built mod looks like. It is not "
                         + "damaged, and Repair would replace your copy with the workshop one.";

                case "Corrupt":
                    return IsLocal
                        ? "This folder has no loadable content - no addons directory with files "
                          + "in it.\r\n\r\nIt is either an empty folder or an unfinished copy."
                        : "Steam has no meta.cpp for this mod AND the folder has no content, "
                          + "which means the download never finished.\r\n\r\n"
                          + "Repair re-downloads it. REPAIR CORRUPTED does all of these at once.";

                case "Needs update":
                    return "The workshop copy is newer than the one on disk. Repair downloads it.";

                default:
                    return IsLocal
                        ? "Installed by hand rather than through the workshop, and ready to load."
                        : "Installed, complete, and up to date.";
            }
        }
        public bool HasKeysFolder;
        public int KeyFileCount;
        public int PboCount;
        public int SignatureCount;

        /// <summary>When a server using this mod was last joined.</summary>
        public DateTime LastLoaded = DateTime.MinValue;   // UTC

        /// <summary>
        /// DayZ's readable "@Name" folder for this mod, when one exists. This
        /// is what a player recognises and what Explorer should open - the
        /// numeric workshop folder means nothing to them.
        /// </summary>
        public string LinkFolder = "";

        /// <summary>The folder to show a person: the @Name link if there is one.</summary>
        public string DisplayFolder
        {
            get { return string.IsNullOrEmpty(LinkFolder) ? Folder : LinkFolder; }
        }

        /// <summary>When the expensive pass last ran for this mod.</summary>
        public DateTime DeepScannedAt = DateTime.MinValue;

        public bool Signed { get { return SignatureCount > 0; } }

        /// <summary>
        /// A mod that does not come from the workshop at all - a folder the
        /// player put there themselves, usually under an "@Name" directory in
        /// the additional mods path. It has no published id, so it is
        /// identified by where it lives.
        /// </summary>
        public bool IsLocal { get { return WorkshopId == 0; } }

        /// <summary>
        /// What identifies this mod in the index.
        ///
        /// A workshop mod is its id. A local one has no id - and there can be
        /// several of them, being different builds of the same mod - so the
        /// folder is what tells them apart. Keying everything by id alone would
        /// collapse every local mod into a single zero.
        /// </summary>
        public string Key
        {
            get
            {
                return WorkshopId != 0
                    ? WorkshopId.ToString()
                    : "local:" + (Folder ?? "").ToLowerInvariant();
            }
        }

        /// <summary>What to show in the Workshop ID column.</summary>
        public string IdText { get { return WorkshopId != 0 ? WorkshopId.ToString() : "Local"; } }

        /// <summary>
        /// Ready / Needs update / Corrupt.
        ///
        /// Corrupt means the folder is there with content but no meta.cpp - the
        /// manifest Steam writes last, so its absence means the install never
        /// finished. See SteamWorkshop.IsInstalled for why that file and not a
        /// file count.
        /// </summary>
        public string Status(string steamPath)
        {
            // A LOCAL mod has no meta.cpp and never will - it did not come from
            // the workshop, so there is no manifest for Steam to write. Judging
            // it by that file marked every hand-installed mod "Corrupt", which
            // is both wrong and alarming. What matters for these is whether the
            // folder actually holds something DayZ can load, and nothing gets
            // into the index without that (see AddLocal).
            if (IsLocal) return HasContent ? "Ready" : "Corrupt";

            // NO meta.cpp, BUT THE CONTENT IS THERE = UNPACKED, NOT BROKEN.
            //
            // Steam writes meta.cpp last, so its absence usually does mean a
            // half-finished download. Not always: a mod extracted for editing
            // has an addons folder full of loose source - .paa, .rvmat, .p3d,
            // .cpp - and no manifest, because it never came down as a package.
            // Measured on this machine: all 15 workshop folders without a
            // meta.cpp were of exactly that kind, several hundred MB each, and
            // calling them "Corrupt" told the owner their own working copies
            // were broken.
            if (!HasMeta) return HasContent ? "Unpacked" : "Corrupt";

            try
            {
                if (steamPath != null && SteamWorkshop.NeedsUpdate(steamPath, WorkshopId))
                    return "Needs update";
            }
            catch { }
            return "Ready";
        }

        public string SizeText
        {
            get
            {
                if (SizeBytes < 0) return "";
                if (SizeBytes >= 1073741824L) return (SizeBytes / 1073741824.0).ToString("N2") + " GB";
                if (SizeBytes >= 1048576L) return (SizeBytes / 1048576.0).ToString("N1") + " MB";
                if (SizeBytes >= 1024L) return (SizeBytes / 1024.0).ToString("N0") + " KB";
                return SizeBytes + " B";
            }
        }
    }

    internal static class ModIndex
    {
        private static readonly object Lock = new object();
        private static readonly Dictionary<string, ModEntry> Entries = new Dictionary<string, ModEntry>();

        private static bool _scanned;
        private static string _lastSteamPath;

        /// <summary>
        /// Makes sure the library has been read at least once.
        ///
        /// WHY THIS EXISTS
        ///   The index used to be built only by the Mod Manager window. Anything
        ///   else that asked it a question - the mod panel deciding whether a
        ///   server's local mod is present, the launcher resolving one by name -
        ///   got an empty answer unless the player happened to have opened that
        ///   window first. A mod sitting right there in the additional mods
        ///   folder was reported missing.
        ///
        ///   It costs about a tenth of a second, so it simply happens on demand.
        /// </summary>
        public static void EnsureScanned(string steamPath)
        {
            lock (Lock)
            {
                if (_scanned && Entries.Count > 0) return;
            }

            try
            {
                Load();
                FastScan(steamPath);
                lock (Lock) { _scanned = true; _lastSteamPath = steamPath; }
            }
            catch { }
        }

        /// <summary>Forces the next question to re-read the folders.</summary>
        public static void Invalidate()
        {
            lock (Lock) _scanned = false;
        }

        /// <summary>Every mod currently known, newest activity first is the caller's business.</summary>
        public static List<ModEntry> All()
        {
            lock (Lock) return Entries.Values.ToList();
        }

        public static ModEntry Get(ulong id)
        {
            if (id == 0) return null;
            lock (Lock)
            {
                ModEntry e;
                return Entries.TryGetValue(id.ToString(), out e) ? e : null;
            }
        }

        /// <summary>
        /// A locally installed mod with this name, or null.
        ///
        /// Name is all there is to go on: a server running a mod from its own
        /// disk publishes no workshop id, so "@GhostRider" has to be paired
        /// with the @GhostRider folder in the player's library. DayZ's own
        /// launcher matches these the same way.
        ///
        /// The "@" is ignored on both sides, since the server sends it and the
        /// folder carries it but neither is part of the name.
        /// </summary>
        public static ModEntry FindLocalByName(string name)
        {
            return FindLocalByName(name, null);
        }

        public static ModEntry FindLocalByName(string name, string steamPath)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;

            // Asking before anything has been read would always answer "no".
            EnsureScanned(steamPath ?? _lastSteamPath);

            string want = name.TrimStart('@').Trim();

            lock (Lock)
            {
                // Best match first, across every local mod, before settling for
                // a weaker one. See MatchRank.
                ModEntry best = null;
                int bestRank = 0;
                foreach (var e in Entries.Values)
                {
                    if (!e.IsLocal) continue;
                    int r = MatchRank(want, e.Name, e.ModCppName);
                    if (r > bestRank) { best = e; bestRank = r; }
                }
                if (best != null) return best;

                // Then a workshop mod of the same name - the player may well
                // have the same thing installed from the workshop instead.
                foreach (var e in Entries.Values)
                {
                    if (e.IsLocal) continue;
                    if (string.Equals(e.Name, want, StringComparison.OrdinalIgnoreCase)) return e;
                }
            }
            return null;
        }

        /// <summary>
        /// How well a local folder matches the name a server reported. 0 is no
        /// match; higher is better.
        ///
        /// A server does NOT report the folder name. It reports the name in
        /// the mod's mod.cpp, so @BeautifulEarplugs arrives as
        /// "BeautifulEarplugs (testing)". Matching on the folder alone never
        /// paired those, however deep the scan looked.
        ///
        ///   3  the mod.cpp name, exactly
        ///   2  the folder name, exactly
        ///   1  the folder name, once a trailing "(...)" tag is dropped
        /// </summary>
        public static int MatchRank(string want, string folderName, string modCppName)
        {
            want = (want ?? "").TrimStart('@').Trim();
            if (want.Length == 0) return 0;

            if (!string.IsNullOrEmpty(modCppName)
                && string.Equals(modCppName.Trim(), want, StringComparison.OrdinalIgnoreCase)) return 3;

            string folder = (folderName ?? "").TrimStart('@').Trim();
            if (folder.Length == 0) return 0;
            if (string.Equals(folder, want, StringComparison.OrdinalIgnoreCase)) return 2;

            string stem = StripTag(want);
            if (stem.Length > 0 && stem.Length < want.Length
                && string.Equals(folder, stem, StringComparison.OrdinalIgnoreCase)) return 1;
            return 0;
        }

        /// <summary>"BeautifulEarplugs (testing)" -> "BeautifulEarplugs".</summary>
        private static string StripTag(string s)
        {
            s = s.Trim();
            if (s.EndsWith(")"))
            {
                int open = s.LastIndexOf('(');
                if (open > 0) return s.Substring(0, open).Trim();
            }
            return s;
        }

        /// <summary>The name = "..." line of a mod.cpp in this folder, or "".</summary>
        public static string ReadModCppName(string dir)
        {
            try
            {
                string path = Path.Combine(dir, "mod.cpp");
                if (!File.Exists(path)) return "";
                foreach (string raw in File.ReadLines(path))
                {
                    string line = raw.Trim();
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    if (!line.Substring(0, eq).Trim().Equals("name", StringComparison.OrdinalIgnoreCase)) continue;
                    return line.Substring(eq + 1).Trim().TrimEnd(';').Trim().Trim('"').Trim();
                }
            }
            catch { }
            return "";
        }

        public static ModEntry GetByKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            lock (Lock)
            {
                ModEntry e;
                return Entries.TryGetValue(key, out e) ? e : null;
            }
        }

        // ------------------------------------------------------- the roots --

        /// <summary>
        /// Where mods live: Steam's workshop folder for DayZ, plus any extra
        /// folder the player has pointed at in Settings.
        /// </summary>
        public static List<string> Roots(string steamPath)
        {
            var roots = new List<string>();
            if (!string.IsNullOrEmpty(steamPath))
            {
                string w = SteamWorkshop.WorkshopRoot(steamPath);
                if (Directory.Exists(w)) roots.Add(w);
            }

            string extra = ServerStore.LoadExtraModPath();
            if (!string.IsNullOrEmpty(extra) && Directory.Exists(extra)
                && !roots.Any(r => string.Equals(r, extra, StringComparison.OrdinalIgnoreCase)))
                roots.Add(extra);

            return roots;
        }

        /// <summary>
        /// How far below an extra mods folder to look. A player's folder is
        /// usually mods\@Thing, but a working tree is just as often
        /// P:\Published\COT\@COT_dzpmtest - so one level is not enough and
        /// unlimited is a good way to walk into a source tree and stay there.
        /// </summary>
        private const int MaxDepth = 4;

        /// <summary>
        /// Every mod folder at or below this one.
        ///
        /// A folder counts as a mod when it is named @Something, or when it
        /// holds an addons folder or a manifest. Those are not descended into:
        /// a mod's own addons folder holds no further mods, and stopping there
        /// keeps this cheap.
        /// </summary>
        private static List<string> ModFolders(string root, int depth)
        {
            var found = new List<string>();
            if (depth < 0) return found;

            string[] dirs;
            try { dirs = Directory.GetDirectories(root); }
            catch { return found; }          // unreadable folder: not an error here

            foreach (string dir in dirs)
            {
                if (IsModFolder(dir)) found.Add(dir);
                else found.AddRange(ModFolders(dir, depth - 1));
            }
            return found;
        }

        private static bool IsModFolder(string dir)
        {
            string leaf = Path.GetFileName(dir) ?? "";
            if (leaf.StartsWith("@", StringComparison.Ordinal)) return true;

            try
            {
                return Directory.Exists(Path.Combine(dir, "addons"))
                    || File.Exists(Path.Combine(dir, "meta.cpp"))
                    || File.Exists(Path.Combine(dir, "mod.cpp"));
            }
            catch { return false; }
        }

        // -------------------------------------------------------- fast pass --

        /// <summary>
        /// Everything cheap: the folder list, and one small read of each
        /// meta.cpp for the id, name and publication stamp. ~150ms for 868
        /// mods, which is what makes the window open instantly.
        ///
        /// Folders whose meta.cpp has not changed since the saved index was
        /// written keep everything already known about them, including the size
        /// the deep pass measured, so a second run costs almost nothing.
        /// </summary>
        public static List<ModEntry> FastScan(string steamPath)
        {
            lock (Lock) { _lastSteamPath = steamPath; _scanned = true; }
            var found = new Dictionary<string, ModEntry>();

            string workshop = string.IsNullOrEmpty(steamPath)
                ? null : SteamWorkshop.WorkshopRoot(steamPath);

            foreach (string root in Roots(steamPath))
            {
                // Steam's own folder is one flat level of numbered folders, and
                // walking into those would be pointless work. Anywhere the
                // player has pointed us is searched properly - see ModFolders.
                bool deep = !string.Equals(root, workshop, StringComparison.OrdinalIgnoreCase);

                string[] dirs;
                try { dirs = deep ? ModFolders(root, MaxDepth).ToArray() : Directory.GetDirectories(root); }
                catch { continue; }

                foreach (string dir in dirs)
                {
                    string leaf = Path.GetFileName(dir);
                    ulong id;
                    if (!ulong.TryParse(leaf, out id))
                    {
                        // Not named after a workshop id. It may still BE a
                        // workshop mod - DayZ's own "@Name" junctions are - so
                        // the id is looked for inside before giving up.
                        id = PublishedIdInside(dir);
                        if (id == 0)
                        {
                            AddLocal(found, dir, leaf);
                            continue;
                        }
                    }

                    string meta = Path.Combine(dir, "meta.cpp");
                    bool hasMeta = File.Exists(meta);
                    DateTime written = DateTime.MinValue;
                    if (hasMeta)
                    {
                        try { written = File.GetLastWriteTimeUtc(meta); }
                        catch { }
                    }

                    // Unchanged since the index was built? Keep what we have.
                    ModEntry known;
                    lock (Lock) Entries.TryGetValue(id.ToString(), out known);
                    if (known != null && known.HasMeta == hasMeta && known.InstalledAt == written
                        && string.Equals(known.Folder, dir, StringComparison.OrdinalIgnoreCase))
                    {
                        // Cheap, and links can appear or vanish without the mod
                        // folder itself changing.
                        known.LinkFolder = WorkshopLinks.LinkFolder(steamPath, id) ?? "";

                        // A retained entry from an older index has this set to
                        // its default of true, which would call an empty folder
                        // "Unpacked". Only the no-manifest case needs checking.
                        if (!known.HasMeta) known.HasContent = HasAddons(dir);

                        if (string.IsNullOrEmpty(known.Name) || known.Name == id.ToString())
                        {
                            string ln = WorkshopLinks.LinkName(steamPath, id);
                            if (!string.IsNullOrEmpty(ln)) known.Name = ln;
                        }
                        found[known.Key] = known;
                        continue;
                    }

                    var e = new ModEntry
                    {
                        WorkshopId = id,
                        Folder = dir,
                        HasMeta = hasMeta,
                        InstalledAt = written,
                        LastLoaded = known != null ? known.LastLoaded : DateTime.MinValue,

                        // Only worth asking when there is no manifest, since
                        // that is the only case where it changes the verdict -
                        // and it is a directory check, not a walk.
                        HasContent = hasMeta || HasAddons(dir)
                    };
                    if (hasMeta) ReadMeta(meta, e);

                    // DayZ names its own link after the mod, so that beats
                    // falling back to a bare workshop id.
                    e.LinkFolder = WorkshopLinks.LinkFolder(steamPath, id) ?? "";
                    if (string.IsNullOrEmpty(e.Name))
                    {
                        string linkName = WorkshopLinks.LinkName(steamPath, id);
                        e.Name = !string.IsNullOrEmpty(linkName) ? linkName : FallbackName(dir, id);
                    }
                    found[e.Key] = e;
                }
            }

            lock (Lock)
            {
                Entries.Clear();
                foreach (var kv in found) Entries[kv.Key] = kv.Value;
            }
            return found.Values.ToList();
        }

        /// <summary>
        /// Records a folder that is a mod but has no workshop identity - the
        /// player installed it by hand.
        ///
        /// The folder name IS the mod name here, which is how DayZ itself
        /// treats these: "@GhostRider" loads as GhostRider. That also means
        /// two builds of the same mod kept side by side - @Thing and
        /// @Thing_v2 - stay separate entries, which is the point of having
        /// them.
        /// </summary>
        private static void AddLocal(Dictionary<string, ModEntry> found, string dir, string leaf)
        {
            string name = (leaf ?? "").TrimStart('@').Trim();
            if (name.Length == 0) return;

            // A folder with nothing loadable in it is not a mod.
            if (!HasAddons(dir)) return;

            var e = new ModEntry
            {
                WorkshopId = 0,
                Folder = dir,
                Name = name,
                HasMeta = false,
                HasContent = true,      // AddLocal is only reached when it does
                ModCppName = ReadModCppName(dir)
            };
            try { e.InstalledAt = Directory.GetLastWriteTimeUtc(dir); }
            catch { }

            ModEntry known;
            lock (Lock) Entries.TryGetValue(e.Key, out known);
            if (known != null)
            {
                // Keep what the deep pass already measured.
                known.Name = name;
                known.ModCppName = e.ModCppName;
                known.InstalledAt = e.InstalledAt;
                found[known.Key] = known;
                return;
            }
            found[e.Key] = e;
        }

        /// <summary>Does this folder actually hold mod content?</summary>
        private static bool HasAddons(string dir)
        {
            foreach (string sub in new[] { "addons", "Addons" })
            {
                try
                {
                    string path = Path.Combine(dir, sub);
                    if (Directory.Exists(path)) return true;
                }
                catch { }
            }
            return false;
        }

        /// <summary>The published id declared inside a folder, or 0.</summary>
        private static ulong PublishedIdInside(string dir)
        {
            foreach (string file in new[] { "meta.cpp", "mod.cpp" })
            {
                try
                {
                    string path = Path.Combine(dir, file);
                    if (!File.Exists(path)) continue;
                    foreach (string line in File.ReadAllLines(path))
                    {
                        string t = line.Trim();
                        if (!t.StartsWith("publishedid", StringComparison.OrdinalIgnoreCase)) continue;
                        int eq = t.IndexOf('=');
                        if (eq < 0) continue;
                        ulong v;
                        if (ulong.TryParse(t.Substring(eq + 1).Trim().TrimEnd(';').Trim().Trim('"'), out v))
                            return v;
                    }
                }
                catch { }
            }
            return 0;
        }

        /// <summary>
        /// meta.cpp is a handful of lines - name, publishedid, timestamp - so
        /// this reads the whole thing rather than streaming it.
        /// </summary>
        private static void ReadMeta(string path, ModEntry e)
        {
            try
            {
                foreach (string line in File.ReadAllLines(path))
                {
                    string t = line.Trim();
                    int eq = t.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = t.Substring(0, eq).Trim();
                    string val = t.Substring(eq + 1).Trim().TrimEnd(';').Trim().Trim('"');

                    if (key.Equals("name", StringComparison.OrdinalIgnoreCase)) e.Name = val;
                    else if (key.Equals("timestamp", StringComparison.OrdinalIgnoreCase))
                    {
                        ulong ts;
                        if (ulong.TryParse(val, out ts)) e.MetaStamp = ts;
                    }
                }
            }
            catch { }
        }

        /// <summary>A mod with no readable name still needs something to show.</summary>
        private static string FallbackName(string dir, ulong id)
        {
            try
            {
                string modCpp = Path.Combine(dir, "mod.cpp");
                if (File.Exists(modCpp))
                {
                    foreach (string line in File.ReadAllLines(modCpp))
                    {
                        string t = line.Trim();
                        if (!t.StartsWith("name", StringComparison.OrdinalIgnoreCase)) continue;
                        int eq = t.IndexOf('=');
                        if (eq < 0) continue;
                        string v = t.Substring(eq + 1).Trim();
                        int semi = v.IndexOf(';');
                        if (semi >= 0) v = v.Substring(0, semi);
                        v = v.Trim().Trim('"');
                        if (v.Length > 0) return v;
                    }
                }
            }
            catch { }

            // Still nothing? A broken install usually still has its pbos, and
            // their name is closer to a mod name than a bare number is.
            try
            {
                foreach (string sub in new[] { "addons", "Addons" })
                {
                    string dirPath = Path.Combine(dir, sub);
                    if (!Directory.Exists(dirPath)) continue;
                    var pbo = Directory.EnumerateFiles(dirPath, "*.pbo").FirstOrDefault();
                    if (pbo != null) return Path.GetFileNameWithoutExtension(pbo);
                }
            }
            catch { }

            return id.ToString();
        }

        // -------------------------------------------------------- deep pass --

        /// <summary>
        /// The expensive facts for ONE mod: total size on disk, pbo count, key
        /// files, signatures, and the author from mod.cpp.
        ///
        /// Kept per-mod on purpose so the caller can run it in the background
        /// and report progress, rather than disappearing for five seconds.
        /// </summary>
        public static void DeepScan(ModEntry e)
        {
            if (e == null || string.IsNullOrEmpty(e.Folder)) return;
            try
            {
                var dir = new DirectoryInfo(e.Folder);
                if (!dir.Exists) return;

                long bytes = 0;
                int pbos = 0, signatures = 0;
                foreach (var fi in dir.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    bytes += fi.Length;
                    if (fi.Extension.Equals(".pbo", StringComparison.OrdinalIgnoreCase)) pbos++;
                    else if (fi.Extension.Equals(".bisign", StringComparison.OrdinalIgnoreCase)) signatures++;
                }
                e.SizeBytes = bytes;
                e.PboCount = pbos;
                e.SignatureCount = signatures;

                string keys = Path.Combine(e.Folder, "keys");
                if (!Directory.Exists(keys)) keys = Path.Combine(e.Folder, "Keys");
                e.HasKeysFolder = Directory.Exists(keys);
                e.KeyFileCount = 0;
                if (e.HasKeysFolder)
                {
                    try { e.KeyFileCount = Directory.GetFiles(keys, "*.bikey").Length; }
                    catch { }
                }

                ReadModCpp(e);
                e.DeepScannedAt = DateTime.UtcNow;
            }
            catch { }
        }

        /// <summary>Author and version, when the mod ships a mod.cpp.</summary>
        private static void ReadModCpp(ModEntry e)
        {
            try
            {
                string path = Path.Combine(e.Folder, "mod.cpp");
                if (!File.Exists(path)) return;
                foreach (string line in File.ReadAllLines(path))
                {
                    string t = line.Trim();
                    int eq = t.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = t.Substring(0, eq).Trim();
                    string val = t.Substring(eq + 1).Trim();
                    int semi = val.IndexOf(';');
                    if (semi >= 0) val = val.Substring(0, semi);
                    val = val.Trim().Trim('"');

                    if (key.Equals("author", StringComparison.OrdinalIgnoreCase)) e.Author = val;
                    else if (key.Equals("version", StringComparison.OrdinalIgnoreCase)) e.Version = val;
                }
            }
            catch { }
        }

        // ------------------------------------------------------ last loaded --

        /// <summary>
        /// Records that these mods were just used to join a server. Written
        /// straight through to disk: the launcher may be closed while the game
        /// runs, and a value kept only in memory would be lost.
        /// </summary>
        public static void MarkLoaded(IEnumerable<ulong> ids)
        {
            if (ids == null) return;
            var now = DateTime.UtcNow;
            bool changed = false;
            lock (Lock)
            {
                foreach (ulong id in ids)
                {
                    ModEntry e;
                    if (id == 0 || !Entries.TryGetValue(id.ToString(), out e)) continue;
                    e.LastLoaded = now;
                    changed = true;
                }
            }
            if (changed) Save();
        }

        // ------------------------------------------------------- persistence --

        /// <summary>
        /// Reads the saved index. Nothing here is trusted as current - FastScan
        /// re-checks every folder - but everything it holds saves work.
        /// </summary>
        public static void Load()
        {
            var rows = ServerStore.LoadModIndex();
            lock (Lock)
            {
                Entries.Clear();
                foreach (var e in rows) Entries[e.Key] = e;
            }
        }

        public static void Save()
        {
            List<ModEntry> rows;
            lock (Lock) rows = Entries.Values.ToList();
            ServerStore.SaveModIndex(rows);
        }
    }
}
