// ---------------------------------------------------------------------------
//  "Mod is locked": which programs on this PC hold a mod's files open.
//
//  The launcher never writes a mod's files itself - Steam does, when it
//  updates, repairs or removes one. Steam cannot replace or delete a PBO that
//  is open, and DayZ keeps every PBO it loaded open until it closes: the game
//  itself, or a DayZ server here whose -mod points at the mod, directly or
//  through a junction (the open file is the real one either way). Steam then
//  drops the item without a word, which looked exactly like a download that
//  failed.
//
//  Windows' Restart Manager answers "who has these files open" without
//  opening them, so asking can never get in Steam's way.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace ABeautifulPotatoLauncher
{
    /// <summary>One program holding a mod's files open.</summary>
    internal sealed class ModLock
    {
        public int Pid;

        /// <summary>The exe without ".exe", e.g. DayZServer_x64.</summary>
        public string Exe = "";

        /// <summary>For the player: "DayZ", "a DayZ server", or the program's own name.</summary>
        public string What = "";

        public override string ToString() { return What + " (PID " + Pid + ")"; }
    }

    internal static class ModLocks
    {
        /// <summary>
        /// The most files handed to one question. A DayZ mod is its addons
        /// folder - a few dozen PBOs - but a Project Zomboid mod can be
        /// thousands of loose files, and asking about every one gets slow.
        /// </summary>
        private const int MaxFiles = 2000;

        /// <summary>The programs holding any file of the mod in <paramref name="folder"/> open; empty when none (or when Windows cannot say).</summary>
        public static List<ModLock> Holders(string folder)
        {
            return Holders(new[] { folder }, MaxFiles);
        }

        /// <summary>The programs holding any file of these mods open, all asked at once.</summary>
        /// <param name="maxFiles">How many files to ask about at most; a whole library needs more than one mod.</param>
        public static List<ModLock> Holders(IEnumerable<string> folders, int maxFiles = MaxFiles)
        {
            var files = new List<string>();
            foreach (string folder in folders)
            {
                if (files.Count >= maxFiles) break;
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
                try
                {
                    // DayZ holds the PBOs; everything else in the folder is read once and let go.
                    string addons = Path.Combine(folder, "addons");
                    var these = Directory.Exists(addons)
                        ? Directory.EnumerateFiles(addons, "*.pbo")
                        : Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories);
                    files.AddRange(these.Take(maxFiles - files.Count));
                }
                catch { }
            }
            return files.Count == 0 ? new List<ModLock>() : Ask(files.ToArray());
        }

        /// <summary>"DayZ (PID 1234) and a DayZ server (PID 5678)".</summary>
        public static string Describe(List<ModLock> holders)
        {
            var parts = holders.Select(h => h.ToString()).ToList();
            if (parts.Count <= 1) return parts.FirstOrDefault() ?? "";
            return string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[parts.Count - 1];
        }

        /// <summary>The text of the "Mod is locked" box, for <paramref name="doing"/> ("updated", "repaired", "removed").</summary>
        public static string Message(string modName, List<ModLock> holders, string doing)
        {
            bool server = holders.Any(h => IsServer(h.Exe));
            return modName + " is in use by " + Describe(holders) + ", so Steam cannot " + doing + " it."
                 + "\r\n\r\nSteam can't replace or delete a mod's files while a program has them open"
                 + (server ? " - a server here that loads the mod, even through a junction, holds it the same as the game does." : ".")
                 + "\r\n\r\nClose " + (holders.Count == 1 ? "it" : "them") + ", then try again.";
        }

        private static bool IsServer(string exe)
        {
            return exe.StartsWith("DayZServer", StringComparison.OrdinalIgnoreCase);
        }

        private static string Friendly(string exe, string appName)
        {
            if (IsServer(exe)) return "a DayZ server";
            if (exe.Equals("DayZ_x64", StringComparison.OrdinalIgnoreCase)
                || exe.Equals("DayZ_BE", StringComparison.OrdinalIgnoreCase)
                || exe.Equals("DayZ", StringComparison.OrdinalIgnoreCase)) return "DayZ";
            if (exe.StartsWith("DayZDiag", StringComparison.OrdinalIgnoreCase)) return "DayZDiag";
            if (exe.StartsWith("ProjectZomboid", StringComparison.OrdinalIgnoreCase)) return "Project Zomboid";
            if (exe.StartsWith("StartServer", StringComparison.OrdinalIgnoreCase)) return "a Project Zomboid server";
            return !string.IsNullOrWhiteSpace(appName) ? appName.Trim() : exe.Length > 0 ? exe : "another program";
        }

        private static List<ModLock> Ask(string[] files)
        {
            var found = new List<ModLock>();
            uint session;
            if (RmStartSession(out session, 0, new StringBuilder(CchRmSessionKey + 1)) != 0) return found;
            try
            {
                if (RmRegisterResources(session, (uint)files.Length, files, 0, null, 0, null) != 0) return found;

                uint needed = 0, count = 0, reasons = 0;
                RmProcessInfo[] info = null;
                int rc = RmGetList(session, out needed, ref count, null, ref reasons);
                // ERROR_MORE_DATA asks again with room; a holder appearing in between asks once more.
                for (int tries = 0; rc == ErrorMoreData && tries < 3; tries++)
                {
                    count = needed;
                    info = new RmProcessInfo[count];
                    rc = RmGetList(session, out needed, ref count, info, ref reasons);
                }
                if (rc != 0 || info == null) return found;

                int me = Process.GetCurrentProcess().Id;
                for (int i = 0; i < count; i++)
                {
                    int pid = info[i].Process.dwProcessId;
                    if (pid == me || found.Any(f => f.Pid == pid)) continue;
                    string exe = "";
                    try { using (var p = Process.GetProcessById(pid)) exe = p.ProcessName; } catch { }
                    found.Add(new ModLock { Pid = pid, Exe = exe, What = Friendly(exe, info[i].strAppName) });
                }
            }
            catch { }
            finally { RmEndSession(session); }
            return found;
        }

        // ---- Restart Manager ----

        private const int CchRmSessionKey = 32;
        private const int ErrorMoreData = 234;

        [StructLayout(LayoutKind.Sequential)]
        private struct RmUniqueProcess
        {
            public int dwProcessId;
            public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RmProcessInfo
        {
            public RmUniqueProcess Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
            public int ApplicationType;
            public uint AppStatus;
            public uint TSSessionId;
            [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmStartSession(out uint session, int flags, StringBuilder key);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmEndSession(uint session);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmRegisterResources(uint session, uint nFiles, string[] files,
                                                      uint nApps, RmUniqueProcess[] apps,
                                                      uint nServices, string[] services);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmGetList(uint session, out uint needed, ref uint count,
                                            [In, Out] RmProcessInfo[] info, ref uint rebootReasons);
    }
}
