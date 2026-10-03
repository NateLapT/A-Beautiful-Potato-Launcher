// ---------------------------------------------------------------------------
//  Which game the launcher is looking after.
//
//  The launcher began as a DayZ launcher and still is one - but the same
//  Steam master list, the same A2S queries and the same workshop calls serve
//  Project Zomboid just as well. What differs (app ids, install folder, how a
//  server lists its mods, how the game is started) lives in one place per
//  game: DayZ's in MainForm as it always has, Project Zomboid's in Zomboid.
//
//  ONE GAME PER RUN
//    Switching game restarts the launcher. Every cache, filter, index and the
//    Steam session itself belongs to one game, and a clean start is the only
//    way to be certain none of DayZ's state leaks into Zomboid's list or the
//    other way round. It also means the Steam session is opened as the right
//    app from the first moment, so Steam shows the game being browsed.
//
//  WHERE EACH GAME KEEPS ITS FILES
//    DayZ stays exactly where it always was, so an existing install loses
//    nothing. Project Zomboid gets a "zomboid" folder beside it. The choice
//    of game itself lives in the shared folder - see ServerStore.BaseDir.
// ---------------------------------------------------------------------------

using System;
using System.IO;

namespace ABeautifulPotatoLauncher
{
    internal enum Game { DayZ = 0, Zomboid = 1 }

    internal static class Games
    {
        /// <summary>The game this run of the launcher is for. Set once, at startup.</summary>
        public static Game Current { get; private set; }

        public static bool IsZomboid { get { return Current == Game.Zomboid; } }

        public static string Name { get { return NameOf(Current); } }

        public static string NameOf(Game g)
        {
            return g == Game.Zomboid ? "Project Zomboid" : "DayZ";
        }

        /// <summary>
        /// The Steam app the launcher's own session claims, and the one that owns
        /// the workshop content. For DayZ that is stable DayZ even when browsing
        /// Experimental - see SteamWorkshop.
        /// </summary>
        public static uint WorkshopApp
        {
            get { return IsZomboid ? Zomboid.AppId : SteamWorkshop.DayZAppId; }
        }

        /// <summary>
        /// The sub-folder this game's data lives in, under the launcher's data
        /// folder - or null for DayZ, which keeps the top level it always had.
        /// </summary>
        public static string DataFolder
        {
            get { return IsZomboid ? "zomboid" : null; }
        }

        private const string ChoiceFile = "game.txt";

        /// <summary>Reads the saved choice. Anything unreadable means DayZ.</summary>
        public static void Load(string baseDir)
        {
            Current = Game.DayZ;
            try
            {
                string path = Path.Combine(baseDir, ChoiceFile);
                if (!File.Exists(path)) return;

                string v = File.ReadAllText(path).Trim();
                if (v.Equals("zomboid", StringComparison.OrdinalIgnoreCase)) Current = Game.Zomboid;
            }
            catch { }
        }

        /// <summary>Saves the choice for the next start. Does not change this run.</summary>
        public static void Save(string baseDir, Game g)
        {
            try
            {
                File.WriteAllText(Path.Combine(baseDir, ChoiceFile),
                                  g == Game.Zomboid ? "zomboid" : "dayz");
            }
            catch { }
        }
    }
}
