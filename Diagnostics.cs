// ---------------------------------------------------------------------------
//  Local dumps of what the browser found, for tuning the screening rules.
//
//  WHY THESE ARE NOT IN THE LOG
//    The log panel is part of the launcher, and the launcher is public. A line
//    saying "hidden: address runs 20+ servers under one name" tells a redirect
//    farm exactly which knob to turn. The rules stay unpublished; the evidence
//    is written here instead, on the machine of whoever is doing the tuning,
//    and goes no further.
//
//  The folder is listed in .gitignore. It must never be committed, both because
//  it is large and because it is a map of the screening.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ABeautifulPotatoLauncher
{
    internal static class Diagnostics
    {
        /// <summary>
        /// Where the dumps go: a "serverdata" folder beside the executable.
        ///
        /// Beside the exe rather than in AppData so that whoever is tuning the
        /// rules can find it without hunting, and because it belongs with the
        /// build it came from rather than following the user around.
        /// </summary>
        public static string Folder
        {
            get
            {
                // AppPaths keeps this beside the exe when that folder can
                // actually be written to, and moves it under LocalAppData when
                // it cannot - an exe in Program Files, say. Making it
                // unconditionally beside the exe used to throw here.
                return AppPaths.Writable("serverdata");
            }
        }

        /// <summary>
        /// Writes the whole browsed list and, separately, everything screened
        /// out with the reason for each.
        ///
        /// Overwrites rather than appends: this is a snapshot of the current
        /// list, and a file that grows without limit would be useless for
        /// comparing one sweep against the next.
        /// </summary>
        public static void WriteServerList(IEnumerable<BrowserServer> all,
                                           IEnumerable<FlaggedServer> screened,
                                           IDictionary<string, int> serversPerIp)
        {
            try
            {
                string dir = Folder;
                string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

                var sb = new StringBuilder();
                sb.AppendLine("# every server the browser returned - " + stamp);
                sb.AppendLine("name\tmap\thost\tport\tqueryport\tplayers\tmaxplayers\tping\tappid\tpassword\tonip\ttags");

                int rows = 0;
                foreach (var s in all)
                {
                    if (s == null) continue;
                    int onIp;
                    serversPerIp.TryGetValue(s.Host ?? "", out onIp);

                    sb.Append(Clean(s.Name)).Append('\t')
                      .Append(Clean(s.Map)).Append('\t')
                      .Append(s.Host).Append('\t')
                      .Append(s.Port).Append('\t')
                      .Append(s.QueryPort).Append('\t')
                      .Append(s.Players).Append('\t')
                      .Append(s.MaxPlayers).Append('\t')
                      .Append(s.Ping).Append('\t')
                      .Append(s.AppId).Append('\t')
                      .Append(s.Password ? 1 : 0).Append('\t')
                      .Append(onIp).Append('\t')
                      .Append(Clean(s.Tags)).AppendLine();
                    rows++;
                }

                File.WriteAllText(Path.Combine(dir, "servers.tsv"), sb.ToString(), Encoding.UTF8);

                var bad = new StringBuilder();
                bad.AppendLine("# servers screened out, and why - " + stamp);
                bad.AppendLine("name\thost\tport\treason");

                int screenedRows = 0;
                foreach (var f in screened)
                {
                    if (f == null) continue;
                    bad.Append(Clean(f.Name)).Append('\t')
                       .Append(f.Host).Append('\t')
                       .Append(f.Port).Append('\t')
                       .Append(Clean(f.Reason)).AppendLine();
                    screenedRows++;
                }

                File.WriteAllText(Path.Combine(dir, "screened.tsv"), bad.ToString(), Encoding.UTF8);

                File.WriteAllText(Path.Combine(dir, "README.txt"),
                    "Diagnostics from A Beautiful Potato Launcher, written " + stamp + ".\r\n\r\n"
                    + "servers.tsv   every server the browser returned (" + rows.ToString("N0", CultureInfo.InvariantCulture) + " rows),\r\n"
                    + "              including the ones hidden in the launcher. The 'onip'\r\n"
                    + "              column is how many servers share that address.\r\n\r\n"
                    + "screened.tsv  the " + screenedRows.ToString("N0", CultureInfo.InvariantCulture) + " that were screened out, with the rule that\r\n"
                    + "              caught each one. Use this to tune the rules.\r\n\r\n"
                    + "Both are rewritten on every completed sweep.\r\n"
                    + "This folder is in .gitignore and must stay out of the repository:\r\n"
                    + "it is a description of what the screening looks for.\r\n",
                    Encoding.UTF8);
            }
            catch
            {
                // Diagnostics failing must never stop the launcher working.
            }
        }

        /// <summary>Strips the characters that would break a tab-separated line.</summary>
        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        }
    }
}
