// ---------------------------------------------------------------------------
//  Copies of a mod on this PC that could be loaded instead of the normal one.
//
//  A copy is recognised by its PBOs, not its name. Each PBO carries a PREFIX in
//  its header - "JM\CF\Scripts", "Motocross\world" - and a folder counts as
//  the same mod when it shares at least half of this mod's prefixes. Names
//  were a poor guess - "BeautifulEarplugs" cut at its first separator is
//  "Beautiful", which made every @Beautiful... mod a "copy" - and the old list
//  then added every other local mod on top.
//
//  WHY PREFIXES, AND WHY HALF (measured on this PC's mods):
//    * File names are often generic. Community Framework is gui/legacy/
//      scripts.pbo, and so is half the library; by file name, COT and eight
//      MotoX builds were all "copies" of CF. Their prefixes - JM\CF\...,
//      JM\COT\..., Motocross\... - never collide.
//    * Mods bundle each other's PBOs. FreshTatoIsland carries Carfluidsmod,
//      which MotoX also has: one shared prefix out of MotoX's seven. Sharing
//      half is a build of the same mod; sharing one is a neighbour.
//
//  Only the mod's OWN copy and the player's LOCAL mods are offered, never
//  other Workshop items. PBO names are not unique across authors - measured:
//  EarPlugs.pbo is in 20+ unrelated Workshop mods - but a local folder sharing
//  a PBO with this mod is the player's own build of it.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ABeautifulPotatoLauncher
{
    internal sealed class ModCopy
    {
        public string Label;
        public string Folder;

        /// <summary>The copy loaded when the player has made no choice.</summary>
        public bool IsDefault;

        /// <summary>The copy that will be loaded now.</summary>
        public bool InUse;

        public override string ToString()
        {
            return string.IsNullOrEmpty(Folder) ? Label : Label + "   -   " + Folder;
        }
    }

    internal static class ModCopies
    {
        /// <summary>
        /// Where the mod loads from when no choice has been made: the Workshop
        /// copy, or for a mod the server loads from its own disk, the local
        /// folder matched by name. Null when there is none.
        /// </summary>
        public static string DefaultFolder(Mod mod, string steamPath)
        {
            if (mod == null) return null;
            if (mod.WorkshopId != 0)
                return steamPath == null ? null : SteamWorkshop.ItemPath(steamPath, mod.WorkshopId);
            var m = ModIndex.FindLocalByName(mod.BareName, steamPath);
            return m == null ? null : m.Folder;
        }

        /// <summary>The folder that will really be loaded: the player's choice if it exists, else the default.</summary>
        public static string InUseFolder(Mod mod, string steamPath)
        {
            var ov = ModOverrides.For(mod);
            if (ov != null && !string.IsNullOrEmpty(ov.Folder) && Directory.Exists(ov.Folder)) return ov.Folder;
            return DefaultFolder(mod, steamPath);
        }

        /// <summary>
        /// The mod's own copy first, then every local folder sharing a PBO with
        /// it, then the player's current choice if it is none of those.
        /// </summary>
        public static List<ModCopy> For(Mod mod, string steamPath)
        {
            var list = new List<ModCopy>();
            if (mod == null) return list;

            ModIndex.EnsureScanned(steamPath);
            string def = DefaultFolder(mod, steamPath);
            var ov = ModOverrides.For(mod);
            string chosen = ov != null && !string.IsNullOrEmpty(ov.Folder) ? ov.Folder : null;

            // The PBOs that identify this mod - from its own copy and from the
            // copy the player chose, so either one finds the other.
            var pbos = new HashSet<string>(PboIds(def), StringComparer.OrdinalIgnoreCase);
            if (chosen != null) pbos.UnionWith(PboIds(chosen));
            int needed = Math.Max(1, (pbos.Count + 1) / 2);

            list.Add(new ModCopy
            {
                Label = mod.WorkshopId != 0 ? "Workshop copy (normal)" : "Matched by name (normal)",
                Folder = def ?? "(not found on this PC)",
                IsDefault = true
            });

            if (pbos.Count > 0)
            {
                List<ModEntry> all;
                try { all = ModIndex.All(); }
                catch { all = new List<ModEntry>(); }

                foreach (var m in all.Where(e => e.IsLocal && !string.IsNullOrEmpty(e.Folder))
                                     .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
                {
                    if (Same(m.Folder, def)) continue;
                    if (PboIds(m.Folder).Count(pbos.Contains) < needed) continue;
                    list.Add(new ModCopy { Label = "Local: @" + m.Name.TrimStart('@'), Folder = m.Folder });
                }
            }

            // A folder picked with Browse that matches nothing still belongs here.
            if (chosen != null && !list.Any(c => Same(c.Folder, chosen)))
                list.Add(new ModCopy { Label = "Your folder", Folder = chosen });

            string inUse = chosen != null && Directory.Exists(chosen) ? chosen : def;
            foreach (var c in list) c.InUse = Same(c.Folder, inUse);
            return list;
        }

        /// <summary>
        /// Reads every local mod's PBO headers ahead of time, on a background
        /// thread, so the first drop-down opens at once instead of after the
        /// few seconds the first read takes (measured: 3.3 s for 57 folders).
        /// </summary>
        public static void WarmUp(string steamPath)
        {
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    ModIndex.EnsureScanned(steamPath);
                    foreach (var m in ModIndex.All())
                        if (m.IsLocal) PboIds(m.Folder);
                }
                catch { }
            });
        }

        private static bool Same(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
            catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        }

        // ------------------------------------------------------------ pbos --

        private static readonly Dictionary<string, KeyValuePair<DateTime, string[]>> Cache =
            new Dictionary<string, KeyValuePair<DateTime, string[]>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// What identifies each PBO in a mod's addons folder: the prefix in its
        /// header, or its file name when it has none. Distinct, cached per
        /// folder until the folder changes.
        /// </summary>
        public static string[] PboIds(string modFolder)
        {
            if (string.IsNullOrEmpty(modFolder)) return new string[0];
            try
            {
                string addons = Path.Combine(modFolder, "addons");
                if (!Directory.Exists(addons)) addons = Path.Combine(modFolder, "Addons");
                if (!Directory.Exists(addons)) return new string[0];

                DateTime stamp = Directory.GetLastWriteTimeUtc(addons);
                lock (Cache)
                {
                    KeyValuePair<DateTime, string[]> hit;
                    if (Cache.TryGetValue(addons, out hit) && hit.Key == stamp) return hit.Value;
                }

                var ids = Directory.GetFiles(addons, "*.pbo")
                                   .Select(p => PboPrefix(p) ?? Path.GetFileNameWithoutExtension(p))
                                   .Select(s => s.Replace('/', '\\').Trim('\\').ToLowerInvariant())
                                   .Distinct()
                                   .ToArray();
                lock (Cache) Cache[addons] = new KeyValuePair<DateTime, string[]>(stamp, ids);
                return ids;
            }
            catch { return new string[0]; }
        }

        /// <summary>
        /// The "prefix" property from a PBO's header, or null. Only the first
        /// few KB are read: the header is an empty-named first entry, five
        /// 32-bit fields, then NUL-terminated key/value pairs ending in an
        /// empty key.
        /// </summary>
        private static string PboPrefix(string path)
        {
            try
            {
                var buf = new byte[4096];
                int n;
                using (var fs = File.OpenRead(path)) n = fs.Read(buf, 0, buf.Length);
                if (n < 21 || buf[0] != 0) return null;

                int i = 1 + 20;
                while (i < n)
                {
                    string key = CString(buf, n, ref i);
                    if (key.Length == 0) break;
                    string value = CString(buf, n, ref i);
                    if (key.Equals("prefix", StringComparison.OrdinalIgnoreCase))
                        return value.Length > 0 ? value : null;
                }
            }
            catch { }
            return null;
        }

        private static string CString(byte[] b, int n, ref int i)
        {
            int start = i;
            while (i < n && b[i] != 0) i++;
            string s = System.Text.Encoding.ASCII.GetString(b, start, i - start);
            i++;
            return s;
        }
    }
}
