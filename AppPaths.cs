//  Where the launcher puts its own files.
//
//  WHY THIS EXISTS: the logs folder and the diagnostics folder used to be made
//  beside the executable, full stop. That is fine when the launcher lives in
//  Downloads or on the desktop, and it is fine for the developer. It is not
//  fine when someone drops the exe into "C:\Program Files\..." - a normal user
//  cannot write there, every log line is silently swallowed, and the first
//  thing anyone asks for when reporting a problem ("send me your log") does not
//  exist. That is also the usual reason someone is told to "run it as
//  administrator": it appears to fix things because an elevated process CAN
//  write to Program Files.
//
//  So: beside the exe when that is genuinely writable, otherwise the user's own
//  LocalAppData. Never elevation.

using System;
using System.IO;

namespace ABeautifulPotatoLauncher
{
    internal static class AppPaths
    {
        /// <summary>
        /// A folder the launcher may write to, named <paramref name="name"/>.
        /// Beside the executable when that works, otherwise under
        /// %LOCALAPPDATA%\A Beautiful Potato Launcher. Returns the path either
        /// way; callers do not have to care which one they got.
        /// </summary>
        internal static string Writable(string name)
        {
            string beside = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name);
            if (CanWrite(beside)) return beside;

            string mine = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "A Beautiful Potato Launcher", name);
            try { Directory.CreateDirectory(mine); }
            catch { return beside; }          // nothing left to try; caller's try/catch handles it
            return mine;
        }

        /// <summary>
        /// Can a file actually be created here? Directory.CreateDirectory
        /// succeeding is not enough on its own - the folder may already exist
        /// and be read-only to this user - so a real file is written and
        /// deleted again.
        /// </summary>
        private static bool CanWrite(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, "write-test.tmp");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }
    }
}
