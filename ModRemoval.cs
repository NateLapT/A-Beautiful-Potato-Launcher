// ---------------------------------------------------------------------------
//  REMOVE: unsubscribing, and leaving the deleting to Steam.
//
//  Steam deletes an unsubscribed item only after the game quits - and while
//  the launcher is open, Steam counts DayZ as running, because the launcher
//  talks to Steam as DayZ (it has to, to read and manage the Workshop). So
//  the files stay until the launcher is closed.
//
//  The launcher does NOT delete them itself. It did for a day, and that also
//  took whatever the player had put in the mod's folder - players extract
//  PBOs there and keep the results. What leaves the disk, and when, is
//  Steam's call.
//
//  Instead, after unsubscribing, the launcher unlinks from Steam for a
//  moment - its Steam session lives in a helper process (see SteamLink), and
//  ending that is Steam seeing DayZ close, with the launcher staying open -
//  so Steam does its own deleting. Then it links again. If Steam hasn't
//  deleted it by then, the mod says it goes when the launcher closes.
// ---------------------------------------------------------------------------

using System;
using System.Threading.Tasks;

namespace ABeautifulPotatoLauncher
{
    internal static class ModRemoval
    {
        /// <summary>How long Steam gets to confirm the unsubscribe.</summary>
        private static readonly TimeSpan ConfirmWithin = TimeSpan.FromSeconds(15);

        /// <summary>
        /// Unsubscribes and waits for Steam to confirm it. Call on the UI
        /// thread. Returns what happened, for the log or a status line.
        /// </summary>
        public static async Task<string> RemoveAsync(string steamPath, ulong id, string name, Action<string> log)
        {
            string folder = steamPath == null ? null : SteamWorkshop.ItemPath(steamPath, id);
            if (!SteamWorkshop.Unsubscribe(id))
                return "Steam would not unsubscribe from " + name + " (" + id + ").";

            var waited = System.Diagnostics.Stopwatch.StartNew();
            while ((SteamWorkshop.GetState(id) & ItemState.Subscribed) != 0)
            {
                if (waited.Elapsed > ConfirmWithin)
                    return "Asked Steam to unsubscribe from " + name + " (" + id + "), but it hasn't confirmed yet.";
                await Task.Delay(500);
            }
            lock (Pending) Pending.Add(id);

            // Let Steam do its deleting now rather than when the launcher closes.
            if (await LetSteamFinish(folder, log))
            {
                Forget(id);
                return "Removed " + name + " (" + id + ") - Steam deleted it.";
            }
            return "Unsubscribed from " + name + " (" + id + "). " + StillOnDisk;
        }

        /// <summary>How long Steam gets to delete a removed mod while the session is let go.</summary>
        private static readonly TimeSpan SteamDeletesWithin = TimeSpan.FromSeconds(45);

        /// <summary>
        /// Unlinks from Steam (SteamWorkshop.Release - the launcher stays open)
        /// so Steam sees DayZ close and deletes the unsubscribed mod, waits
        /// until the folder has gone, then links again and puts the server
        /// browser back on it. True when the folder went.
        /// Nothing is let go while the game itself is running: Steam would
        /// still see DayZ running, so it would gain nothing.
        /// </summary>
        private static async Task<bool> LetSteamFinish(string folder, Action<string> log)
        {
            if (string.IsNullOrEmpty(folder) || !System.IO.Directory.Exists(folder)) return true;
            if (GameRunning()) return false;

            if (!SteamWorkshop.Release(SteamDeletesWithin + TimeSpan.FromSeconds(5), log)) return false;
            bool gone = false;
            try
            {
                var waited = System.Diagnostics.Stopwatch.StartNew();
                while (waited.Elapsed < SteamDeletesWithin)
                {
                    await Task.Delay(1000);
                    if (!System.IO.Directory.Exists(folder)) { gone = true; break; }
                }
            }
            finally
            {
                SteamWorkshop.Resume();
                var main = MainForm.Active;
                if (main != null) main.ReconnectSteam();
                else SteamWorkshop.EnsureWorkshopApp(null, log);
            }
            return gone;
        }

        private static bool GameRunning()
        {
            foreach (string exe in new[] { "DayZ_x64", "DayZ", "ProjectZomboid64", "ProjectZomboid32" })
                if (System.Diagnostics.Process.GetProcessesByName(exe).Length > 0) return true;
            return false;
        }

        // Removed this session and still on disk, so the mod panel can say so
        // rather than "installed". Subscribing again takes a mod back off it.
        private static readonly System.Collections.Generic.HashSet<ulong> Pending =
            new System.Collections.Generic.HashSet<ulong>();

        public static bool IsPending(ulong id) { lock (Pending) return Pending.Contains(id); }

        public static void Forget(ulong id) { lock (Pending) Pending.Remove(id); }

        /// <summary>The mod panel's status for a mod removed but still on disk.</summary>
        public const string PendingStatus = "removed - Steam deletes it when the launcher closes";

        /// <summary>Why the files are still there, and when they go.</summary>
        public const string StillOnDisk =
            "Steam deletes its files once the launcher (and DayZ) are closed - Steam counts DayZ as running while the launcher is open.";
    }
}
