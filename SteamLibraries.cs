//  Steam's library folders.
//
//  WHY THIS EXISTS: the launcher used to look for DayZ in exactly one place,
//  <SteamPath>\steamapps\common. Steam has not worked that way for years - a
//  player can add libraries on other drives, and a game the size of DayZ is
//  very often on one of them. For those players the launcher said "that build
//  is not installed" while the game sat on D:. Running as administrator does
//  not help and never did; the folder simply was not being looked at.
//
//  Steam lists its libraries in steamapps\libraryfolders.vdf, which is
//  world-readable, so this needs no special rights at all.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace ABeautifulPotatoLauncher
{
    internal static class SteamLibraries
    {
        /// <summary>
        /// Every library root Steam knows about, the main install first.
        /// Each is the folder that CONTAINS steamapps, so callers can keep
        /// building paths the way they always have.
        /// </summary>
        internal static List<string> All(string steamPath)
        {
            var roots = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Action<string> add = p =>
            {
                if (string.IsNullOrWhiteSpace(p)) return;

                // Only things that look like a path. The old vdf format keyed
                // libraries by number ("1" "D:\Games"), and so does the "apps"
                // block inside each library - which is full of id/size pairs
                // like "221100" "54826274064". A drive letter or a UNC prefix
                // tells the two apart.
                if (p.IndexOf(':') < 0 && !p.StartsWith(@"\\")) return;
                try { p = Path.GetFullPath(p.TrimEnd('\\')); }
                catch { return; }
                if (!seen.Add(p)) return;
                if (Directory.Exists(Path.Combine(p, "steamapps"))) roots.Add(p);
            };

            add(steamPath);

            try
            {
                string vdf = Path.Combine(steamPath ?? "", "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                {
                    // Both the old format ("1" "D:\Games") and the current one
                    // ("path" "D:\Games") are matched, because a player who has
                    // not run Steam in a long time may still have the old file.
                    string text = File.ReadAllText(vdf);
                    foreach (Match m in Regex.Matches(text,
                                 "\"(?:path|[0-9]+)\"\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase))
                        add(m.Groups[1].Value.Replace(@"\\", @"\"));
                }
            }
            catch { }

            return roots;
        }

        /// <summary>
        /// The library holding an installed copy of DayZ - stable or
        /// experimental - or the main Steam folder when none of them does.
        ///
        /// Everything downstream builds steamapps\common and
        /// steamapps\workshop paths off one root, and Steam keeps a game's
        /// workshop content in the same library as the game, so pointing that
        /// root at the library with the game in it fixes the mod folders too.
        /// </summary>
        internal static string WithDayZ(string steamPath, IEnumerable<string> folders)
        {
            foreach (string root in All(steamPath))
            {
                string common = Path.Combine(root, "steamapps", "common");
                foreach (string folder in folders)
                {
                    try
                    {
                        if (Directory.Exists(Path.Combine(common, folder))) return root;
                    }
                    catch { }
                }
            }
            return steamPath;
        }
    }
}
