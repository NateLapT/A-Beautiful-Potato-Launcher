// ---------------------------------------------------------------------------
//  Per-mod choices the player has made about joining.
//
//  A server names the mods it requires, and normally the launcher loads exactly
//  those, from wherever Steam put them. Two things make that too rigid:
//
//    * Sometimes a mod should NOT be loaded - it conflicts with something, or
//      the player is testing without it.
//    * Sometimes a DIFFERENT copy should be loaded - a local build kept beside
//      the workshop one. A player with @Thing, @Thing_v51 and @Thing_v52 on
//      disk is choosing between them deliberately.
//
//  Both are recorded here, against the workshop id the server asked for, and
//  applied when the command line is built.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;

namespace BeautifulPotatoExpLauncher
{
    /// <summary>What the player decided about one required mod.</summary>
    internal sealed class ModOverride
    {
        /// <summary>False means this mod is left off the command line entirely.</summary>
        public bool Enabled = true;

        /// <summary>
        /// A folder to load instead of the one Steam provides. Empty means the
        /// normal workshop copy.
        /// </summary>
        public string Folder = "";

        public bool IsDefault { get { return Enabled && string.IsNullOrEmpty(Folder); } }
    }

    internal static class ModOverrides
    {
        private static readonly object Lock = new object();
        private static Dictionary<string, ModOverride> _map;

        /// <summary>
        /// How a mod is identified in these choices.
        ///
        /// A workshop mod is its id. A mod the server loaded from its own disk
        /// has no id - every one of them would be "0" - so those are keyed by
        /// name, which is the only thing that distinguishes them.
        /// </summary>
        public static string KeyFor(Mod mod)
        {
            if (mod == null) return null;
            return mod.WorkshopId != 0 ? mod.WorkshopId.ToString()
                                       : "name:" + mod.BareName.ToLowerInvariant();
        }

        private static Dictionary<string, ModOverride> Map
        {
            get
            {
                lock (Lock)
                {
                    if (_map == null) _map = ServerStore.LoadModOverrides();
                    return _map;
                }
            }
        }

        /// <summary>The player's choice for a mod, or null if they made none.</summary>
        public static ModOverride For(Mod mod)
        {
            return ForKey(KeyFor(mod));
        }

        public static ModOverride For(ulong workshopId)
        {
            return workshopId == 0 ? null : ForKey(workshopId.ToString());
        }

        public static ModOverride ForKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            lock (Lock)
            {
                ModOverride ov;
                return Map.TryGetValue(key, out ov) ? ov : null;
            }
        }

        /// <summary>
        /// Records a choice. A choice that is really just the default is
        /// REMOVED rather than stored, so the file only ever holds genuine
        /// decisions and "reset to normal" needs no special case.
        /// </summary>
        public static void Set(Mod mod, ModOverride ov)
        {
            SetKey(KeyFor(mod), ov);
        }

        public static void Set(ulong workshopId, ModOverride ov)
        {
            if (workshopId != 0) SetKey(workshopId.ToString(), ov);
        }

        public static void SetKey(string key, ModOverride ov)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (Lock)
            {
                if (ov == null || ov.IsDefault) Map.Remove(key);
                else Map[key] = ov;
                ServerStore.SaveModOverrides(Map);
            }
        }

        /// <summary>How many mods the player has deliberately changed.</summary>
        public static int Count
        {
            get { lock (Lock) return Map.Count; }
        }

        public static void Forget()
        {
            lock (Lock) _map = null;
        }
    }
}
