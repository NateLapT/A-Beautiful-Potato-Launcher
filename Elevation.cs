//  Who is running as administrator - this launcher, and Steam.
//
//  WHY THIS MATTERS: Steam's API is not a network service, it is inter-process
//  communication with the running Steam client. Windows blocks a normal
//  program from talking to an elevated one, so if Steam was started with "Run
//  as administrator" and the launcher was not, SteamAPI_Init simply fails -
//  no server list, no mod downloads - and the honest-looking message
//  "is Steam running?" is wrong, because it plainly is. That is the real
//  reason some players find the launcher only works "in admin mode": they are
//  not fixing the launcher, they are matching Steam.
//
//  The mismatch is worth naming in both directions. Elevated launcher plus
//  normal Steam is just as broken, and it leaves administrator-owned files in
//  the player's own mod folders that they cannot later delete.
//
//  Nothing here needs elevation to run; it only reports.

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace ABeautifulPotatoLauncher
{
    internal static class Elevation
    {
        /// <summary>True when this process is running elevated.</summary>
        internal static bool Self
        {
            get
            {
                try
                {
                    using (var me = WindowsIdentity.GetCurrent())
                        return new WindowsPrincipal(me).IsInRole(WindowsBuiltInRole.Administrator);
                }
                catch { return false; }
            }
        }

        /// <summary>
        /// True/false when the Steam client is running and its elevation could
        /// be established, null when Steam is not running or cannot be read.
        /// </summary>
        internal static bool? Steam
        {
            get
            {
                if ((DateTime.UtcNow - _askedAt).TotalSeconds < 10) return _answer;

                bool? found = null;
                try
                {
                    foreach (var p in Process.GetProcessesByName("steam"))
                    {
                        using (p)
                        {
                            bool? state = IsElevated(p.Id);
                            if (state != null) { found = state; break; }
                        }
                    }
                }
                catch { }

                _answer = found;
                _askedAt = DateTime.UtcNow;
                return found;
            }
        }

        private static bool? _answer;
        private static DateTime _askedAt = DateTime.MinValue;

        /// <summary>
        /// The mismatch, in words a player can act on, or null when the two
        /// agree (or when there is nothing to compare against).
        /// </summary>
        internal static string Mismatch()
        {
            bool? steam = Steam;
            if (steam == null) return null;

            if (steam == true && !Self)
                return "Steam is running as administrator and this launcher is not.\r\n\r\n"
                     + "That is a known cause of exactly this kind of failure. Everything an "
                     + "elevated Steam writes - the game itself, and every mod it downloads - "
                     + "belongs to the administrator account, and your own account can then be "
                     + "refused when it tries to run or update those files. It is why the game "
                     + "can fail to start with \"Windows cannot access the specified "
                     + "device, path, or file\", and why mod downloads can appear to do "
                     + "nothing.\r\n\r\n"
                     + "Close Steam completely and start it normally, WITHOUT \"Run as "
                     + "administrator\". DayZ has never needed it, and neither does this "
                     + "launcher.\r\n\r\n"
                     + "If the game still will not start afterwards, its files are still owned "
                     + "by the administrator account: in Steam, started normally, DayZ - "
                     + "Properties - Installed Files - Verify integrity of game files rewrites "
                     + "them under your own account.";

            if (steam == false && Self)
                return "This launcher is running as administrator and Steam is not.\r\n\r\n"
                     + "Anything it writes into your mod folders then belongs to the "
                     + "administrator account, which you may not be able to update or delete "
                     + "later - the same trap the other way round.\r\n\r\n"
                     + "Close the launcher and start it normally. It has never needed "
                     + "administrator rights.";

            return null;
        }

        /// <summary>
        /// The same thing in one line, for a status strip or a hint label that
        /// has no room for the full explanation. Null when the levels agree.
        /// </summary>
        internal static string Short()
        {
            bool? steam = Steam;
            if (steam == null) return null;

            if (steam == true && !Self)
                return "Steam is running as administrator. Close it and start it normally - "
                     + "not as administrator - or the game and your mods can be refused to "
                     + "your own account.";

            if (steam == false && Self)
                return "This launcher is running as administrator and Steam is not. Close it "
                     + "and start it normally.";

            return null;
        }

        /// <summary>One line for the log, so a pasted log answers this question.</summary>
        internal static string Describe()
        {
            bool? steam = Steam;
            return "Elevation: launcher " + (Self ? "ADMINISTRATOR" : "normal")
                 + ", Steam " + (steam == null ? "not running"
                                               : (steam == true ? "ADMINISTRATOR" : "normal"));
        }

        // ------------------------------------------------------------ win32 --

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint TOKEN_QUERY = 0x0008;
        private const int  TokenElevation = 20;

        [DllImport("kernel32", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("advapi32", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr token, int cls,
            out uint value, int size, out int returned);

        private static bool? IsElevated(int pid)
        {
            IntPtr process = IntPtr.Zero, token = IntPtr.Zero;
            try
            {
                process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (process == IntPtr.Zero)
                {
                    // Cannot even open it. A process of our own user that we
                    // are not allowed to look at is an elevated one - which is
                    // the case this whole file exists for - but only say so
                    // when we are not elevated ourselves, because an elevated
                    // process can open anything and a refusal would mean
                    // something else entirely.
                    return Self ? (bool?)null : true;
                }

                if (!OpenProcessToken(process, TOKEN_QUERY, out token))
                    return Self ? (bool?)null : true;   // same reasoning

                uint elevated;
                int returned;
                if (!GetTokenInformation(token, TokenElevation, out elevated, sizeof(uint), out returned))
                    return null;

                return elevated != 0;
            }
            catch { return null; }
            finally
            {
                if (token != IntPtr.Zero) CloseHandle(token);
                if (process != IntPtr.Zero) CloseHandle(process);
            }
        }
    }
}
