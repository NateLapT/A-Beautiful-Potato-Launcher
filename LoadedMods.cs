// ---------------------------------------------------------------------------
//  The Mod Manager's checked mods - "loaded" - and its saved presets.
//
//  The loaded set is what START WITH MODS starts DayZ with. Joining a server
//  replaces it with the mods that server loaded, so the Mod Manager always
//  opens on the last thing played; a preset replaces it with a saved set.
//
//  A mod is remembered by what identifies it in the index (see ModEntry.Key):
//  a workshop mod by its id, a local one by its folder - several local builds
//  of one mod are different folders and must stay apart. A mod loaded from a
//  folder the player chose (see ModOverrides) is remembered by that folder,
//  because that copy is what was played.
//
//  Kept per game, beside everything else in ServerStore's folder:
//    loaded.txt    first line "from<TAB>label", then one key per line
//    presets.tsv   name<TAB>key|key|key   (a path never contains "|")
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ABeautifulPotatoLauncher
{
    internal sealed class ModPreset
    {
        public string Name;
        public List<string> Keys = new List<string>();
    }

    internal static class LoadedMods
    {
        /// <summary>Raised on the UI thread whenever the loaded set is replaced or cleared.</summary>
        public static event Action Changed;

        private static HashSet<string> _keys;
        private static string _from = "";
        private static string _preset;

        private static string LoadedFile { get { return Path.Combine(ServerStore.Dir, "loaded.txt"); } }
        private static string PresetsFile { get { return Path.Combine(ServerStore.Dir, "presets.tsv"); } }
        private static string BuildFile { get { return Path.Combine(ServerStore.Dir, "start-build.txt"); } }

        // ------------------------------------------------------------ keys --

        public static string FolderKey(string folder)
        {
            string f = folder ?? "";
            try { f = Path.GetFullPath(f); } catch { }
            return "local:" + f.TrimEnd('\\').ToLowerInvariant();
        }

        /// <summary>The key a checked row is stored under.</summary>
        public static string KeyOf(ModEntry m)
        {
            return m.WorkshopId != 0 ? m.WorkshopId.ToString() : FolderKey(m.Folder);
        }

        // ------------------------------------------------------ the set --

        private static HashSet<string> Keys
        {
            get
            {
                if (_keys == null) Load();
                return _keys;
            }
        }

        public static int Count { get { return Keys.Count; } }

        /// <summary>Where the set came from: a server's name, a preset, or "" when picked by hand.</summary>
        public static string From { get { if (_keys == null) Load(); return _from; } }

        /// <summary>The preset last applied, or null once anything else replaced the set.</summary>
        public static string Preset { get { return _preset; } }

        /// <summary>
        /// Checked by its own key, or - for a workshop id found in a local
        /// folder - by that folder, which is how a chosen copy is remembered.
        /// </summary>
        public static bool Contains(ModEntry m)
        {
            var k = Keys;
            return k.Contains(KeyOf(m)) || (m.WorkshopId != 0 && k.Contains(FolderKey(m.Folder)));
        }

        public static void Set(ModEntry m, bool on)
        {
            if (on) Keys.Add(KeyOf(m));
            else
            {
                Keys.Remove(KeyOf(m));
                Keys.Remove(FolderKey(m.Folder));
            }
            Save();
        }

        /// <summary>The entries of <paramref name="library"/> that are checked, and how many keys matched nothing.</summary>
        public static List<ModEntry> In(IEnumerable<ModEntry> library, out int notFound)
        {
            List<string> missing;
            var list = In(library, out missing);
            notFound = missing.Count;
            return list;
        }

        /// <summary>The checked entries of <paramref name="library"/>, and the keys that matched nothing.</summary>
        public static List<ModEntry> In(IEnumerable<ModEntry> library, out List<string> missing)
        {
            var list = library.Where(Contains).ToList();
            var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in list) { matched.Add(KeyOf(m)); matched.Add(FolderKey(m.Folder)); }
            missing = Keys.Where(k => !matched.Contains(k)).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
            return list;
        }

        /// <summary>Forgets a key that matches no mod on this PC.</summary>
        public static void Remove(string key)
        {
            if (Keys.Remove(key)) Save();
        }

        public static void Replace(IEnumerable<string> keys, string from, string preset)
        {
            _keys = new HashSet<string>(keys.Where(k => !string.IsNullOrEmpty(k)), StringComparer.OrdinalIgnoreCase);
            _from = from ?? "";
            _preset = preset;
            Save();
            var h = Changed;
            if (h != null) h();
        }

        public static void Clear() { Replace(new string[0], "", null); }

        /// <summary>Checked by hand: the set no longer is what it came from.</summary>
        public static void MarkEdited() { _from = ""; Save(); }

        private static void Load()
        {
            _keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _from = "";
            try
            {
                if (!File.Exists(LoadedFile)) return;
                foreach (string line in File.ReadAllLines(LoadedFile))
                {
                    if (line.StartsWith("from\t")) { _from = line.Substring(5); continue; }
                    if (line.Trim().Length > 0) _keys.Add(line.Trim());
                }
            }
            catch { }
        }

        private static void Save()
        {
            try
            {
                var lines = new List<string> { "from\t" + Clean(_from) };
                lines.AddRange(Keys);
                File.WriteAllLines(LoadedFile, lines);
            }
            catch { }
        }

        private static string Clean(string s)
        {
            return (s ?? "").Replace("\t", " ").Replace("\r", " ").Replace("\n", " ").Trim();
        }

        // ---------------------------------------------------------- presets --

        public static List<ModPreset> Presets()
        {
            var list = new List<ModPreset>();
            try
            {
                if (!File.Exists(PresetsFile)) return list;
                foreach (string line in File.ReadAllLines(PresetsFile))
                {
                    int tab = line.IndexOf('\t');
                    if (tab <= 0) continue;
                    list.Add(new ModPreset
                    {
                        Name = line.Substring(0, tab),
                        Keys = line.Substring(tab + 1).Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries).ToList()
                    });
                }
            }
            catch { }
            return list.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void SavePresets(List<ModPreset> list)
        {
            try
            {
                File.WriteAllLines(PresetsFile,
                    list.Select(p => Clean(p.Name) + "\t" + string.Join("|", p.Keys)));
            }
            catch { }
        }

        public static string CleanName(string name) { return Clean(name).Replace("|", "/"); }

        /// <summary>Saves the loaded set as <paramref name="name"/>, replacing a preset of that name.</summary>
        public static void SavePreset(string name)
        {
            name = CleanName(name);
            var list = Presets();
            list.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            list.Add(new ModPreset { Name = name, Keys = Keys.ToList() });
            SavePresets(list);
            _preset = name;
            _from = "Preset: " + name;
            Save();
        }

        /// <summary>Renames a preset and, when asked, gives it the loaded set.</summary>
        public static void EditPreset(string oldName, string newName, bool takeLoaded)
        {
            newName = CleanName(newName);
            var list = Presets();
            var p = list.FirstOrDefault(x => string.Equals(x.Name, oldName, StringComparison.OrdinalIgnoreCase));
            if (p == null) return;
            list.RemoveAll(x => x != p && string.Equals(x.Name, newName, StringComparison.OrdinalIgnoreCase));
            p.Name = newName;
            if (takeLoaded) p.Keys = Keys.ToList();
            SavePresets(list);
            if (takeLoaded)
            {
                _preset = newName;
                _from = "Preset: " + newName;
                Save();
            }
            else if (string.Equals(_preset, oldName, StringComparison.OrdinalIgnoreCase))
            {
                if (_from == "Preset: " + oldName) { _from = "Preset: " + newName; Save(); }
                _preset = newName;
            }
        }

        public static void DeletePreset(string name)
        {
            var list = Presets();
            list.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            SavePresets(list);
            if (string.Equals(_preset, name, StringComparison.OrdinalIgnoreCase)) _preset = null;
        }

        public static void ApplyPreset(string name)
        {
            var p = Presets().FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (p != null) Replace(p.Keys, "Preset: " + p.Name, p.Name);
        }

        // ------------------------------------------------------------ build --

        /// <summary>Which build START WITH MODS starts: stable unless Experimental was picked last.</summary>
        public static ulong Build
        {
            get
            {
                try
                {
                    if (File.Exists(BuildFile) && File.ReadAllText(BuildFile).Trim() == "exp")
                        return A2S.ExperimentalAppId;
                }
                catch { }
                return A2S.StableAppId;
            }
            set
            {
                try { File.WriteAllText(BuildFile, value == A2S.ExperimentalAppId ? "exp" : "stable"); }
                catch { }
            }
        }
    }
}
