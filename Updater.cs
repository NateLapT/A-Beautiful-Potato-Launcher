// ---------------------------------------------------------------------------
//  Update check and self-update, from the project's GitHub releases.
//
//  No server of our own: GitHub hosts the releases and answers the question
//  "what is the newest version" through its public API (60 unauthenticated
//  requests an hour per IP - one per launch is nowhere near that).
//
//  PRE-RELEASES COUNT. Every release so far is marked pre-release, and the
//  API's /releases/latest skips those entirely - it answered "Not Found" for a
//  repository with five releases. So this reads the release list and takes the
//  highest version from it, pre-release or not.
//
//  THE SWAP. Windows will not let a running exe be overwritten, but it WILL
//  let one be renamed. So an update is:
//      download   -> ABeautifulPotatoLauncher.update.exe   (checked before use)
//      rename     running exe -> ABeautifulPotatoLauncher.exe.old
//      rename     update.exe -> ABeautifulPotatoLauncher.exe
//      start the new one, wait for it to say it is running, exit this one
//  and the new copy deletes the .old on its next start. No installer, no
//  administrator - as long as the exe's folder is writable, which is why the
//  launcher belongs under LocalAppData rather than Program Files.
//
//  ANTIVIRUS. Players have had updates blocked. Nothing here can or should
//  get past an antivirus - the cure for a false alarm is a signed exe - but
//  a block must never leave a player WITHOUT a launcher, which the plain
//  swap could: the new exe in place, then quarantined when it started. So:
//    * The download is saved as an .exe, not ".new". Scanners look at
//      executables as they are written, so a verdict arrives while the old
//      launcher is still untouched - and the file is re-read after a pause
//      to make sure it is still there and readable before the swap.
//    * The new copy signals a named event as soon as it runs. If it cannot
//      be started, or exits without signalling, the swap is undone and the
//      player is told plainly, with the download page to fall back on.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ABeautifulPotatoLauncher
{
    internal sealed class ReleaseInfo
    {
        public string Tag = "";          // "v0.6"
        public string Version = "";      // "0.6"
        public string Name = "";
        public string Notes = "";        // the release text - the change log
        public string PageUrl = "";
        public string ExeUrl = "";
        public long ExeSize;
        public bool PreRelease;
        public DateTime Published;
    }

    /// <summary>The update was stopped - almost always by an antivirus - and nothing was changed.</summary>
    internal sealed class UpdateBlockedException : Exception
    {
        public UpdateBlockedException(string message) : base(message) { }
    }

    internal static class Updater
    {
        private const string Repo = "NateLapT/A-Beautiful-Potato-Launcher";
        private const string ReleasesApi = "https://api.github.com/repos/" + Repo + "/releases?per_page=20";
        public const string ReleasesPage = "https://github.com/" + Repo + "/releases";
        private const string ExeAssetName = "ABeautifulPotatoLauncher.exe";

        /// <summary>
        /// Every published release newer than this build, newest first. Empty
        /// when up to date, or when GitHub could not be reached - a failed check
        /// is not worth bothering the player about.
        /// </summary>
        public static Task<List<ReleaseInfo>> CheckAsync()
        {
            return Task.Run(() =>
            {
                try
                {
                    string json;
                    using (var wc = NewClient())
                        json = wc.DownloadString(ReleasesApi);

                    return Parse(json)
                        .Where(r => r.ExeUrl.Length > 0 && IsNewer(r.Version, Program.Version))
                        .OrderByDescending(r => r.Version, VersionComparer.Instance)
                        .ToList();
                }
                catch (Exception ex)
                {
                    Log("Update check failed: " + ex.Message);
                    return new List<ReleaseInfo>();
                }
            });
        }

        private static List<ReleaseInfo> Parse(string json)
        {
            var list = new List<ReleaseInfo>();
            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var arr = ser.DeserializeObject(json) as object[];
            if (arr == null) return list;

            foreach (var o in arr.OfType<Dictionary<string, object>>())
            {
                if (Bool(o, "draft")) continue;

                var r = new ReleaseInfo
                {
                    Tag = Str(o, "tag_name"),
                    Name = Str(o, "name"),
                    Notes = Str(o, "body"),
                    PageUrl = Str(o, "html_url"),
                    PreRelease = Bool(o, "prerelease")
                };
                r.Version = r.Tag.TrimStart('v', 'V').Trim();
                DateTime.TryParse(Str(o, "published_at"), CultureInfo.InvariantCulture,
                                  DateTimeStyles.AdjustToUniversal, out r.Published);

                var assets = o.ContainsKey("assets") ? o["assets"] as object[] : null;
                if (assets != null)
                {
                    foreach (var a in assets.OfType<Dictionary<string, object>>())
                    {
                        if (!string.Equals(Str(a, "name"), ExeAssetName, StringComparison.OrdinalIgnoreCase))
                            continue;
                        r.ExeUrl = Str(a, "browser_download_url");
                        long size;
                        long.TryParse(Convert.ToString(a.ContainsKey("size") ? a["size"] : null,
                                                       CultureInfo.InvariantCulture), out size);
                        r.ExeSize = size;
                    }
                }
                if (r.Version.Length > 0) list.Add(r);
            }
            return list;
        }

        // ------------------------------------------------------- versions --

        /// <summary>
        /// Versions here are written like decimals - 0.5, 0.55, 0.6 - and must
        /// compare like decimals. System.Version would read "0.55" as minor 55
        /// and rank it ABOVE "0.6" (minor 6), so v0.55 would never update to
        /// v0.6. Two-part versions are therefore compared as numbers; anything
        /// with more parts (1.2.3) falls back to System.Version.
        /// </summary>
        internal sealed class VersionComparer : IComparer<string>
        {
            public static readonly VersionComparer Instance = new VersionComparer();

            public int Compare(string a, string b)
            {
                a = (a ?? "").Trim().TrimStart('v', 'V');
                b = (b ?? "").Trim().TrimStart('v', 'V');

                decimal da, db;
                if (a.Count(c => c == '.') <= 1 && b.Count(c => c == '.') <= 1
                    && decimal.TryParse(a, NumberStyles.Number, CultureInfo.InvariantCulture, out da)
                    && decimal.TryParse(b, NumberStyles.Number, CultureInfo.InvariantCulture, out db))
                    return da.CompareTo(db);

                Version va, vb;
                if (System.Version.TryParse(Pad(a), out va) && System.Version.TryParse(Pad(b), out vb))
                    return va.CompareTo(vb);
                return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            }

            private static string Pad(string v) { return v.Contains(".") ? v : v + ".0"; }
        }

        public static bool IsNewer(string candidate, string current)
        {
            return VersionComparer.Instance.Compare(candidate, current) > 0;
        }

        // ---------------------------------------------------------- apply --

        /// <summary>Can the exe be swapped where it is? Not in Program Files.</summary>
        public static bool CanSelfUpdate()
        {
            try
            {
                string dir = Path.GetDirectoryName(ExePath);
                string probe = Path.Combine(dir, "update-test.tmp");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        private static string ExePath
        {
            get { return Path.GetFullPath(Assembly.GetExecutingAssembly().Location); }
        }

        /// <summary>
        /// Downloads the release's exe beside this one and checks it before
        /// anything is replaced: the size GitHub reported, a real .NET
        /// assembly, and the version the release claims to be. A half download
        /// or an HTML error page must never become the launcher.
        /// Returns the path of the verified download.
        /// </summary>
        public static async Task<string> DownloadAsync(ReleaseInfo r, Action<int> progress)
        {
            string target = DownloadPath;
            try { File.Delete(target); } catch { }

            using (var wc = NewClient())
            {
                wc.DownloadProgressChanged += (s, e) => { if (progress != null) progress(e.ProgressPercentage); };
                await wc.DownloadFileTaskAsync(new Uri(r.ExeUrl), target);
            }

            // Removed the moment it was written: that is an antivirus.
            if (!File.Exists(target))
            {
                Log("Update: the download vanished as soon as it was saved.");
                throw Blocked("Your antivirus removed the new version as it downloaded.");
            }

            var fi = new FileInfo(target);
            if (r.ExeSize > 0 && fi.Length != r.ExeSize)
                throw new Exception("The download is " + fi.Length + " bytes, but GitHub lists "
                                    + r.ExeSize + ". Try again.");

            string got;
            try
            {
                var name = AssemblyName.GetAssemblyName(target);
                if (!string.Equals(name.Name, "ABeautifulPotatoLauncher", StringComparison.OrdinalIgnoreCase))
                    throw new Exception("it is " + name.Name);
                got = InformationalVersionOf(target) ?? name.Version.ToString();
            }
            catch (Exception ex)
            {
                try { File.Delete(target); } catch { }
                throw new Exception("The downloaded file is not the launcher (" + ex.Message + ").");
            }

            int plus = got.IndexOf('+');
            if (plus > 0) got = got.Substring(0, plus);
            if (VersionComparer.Instance.Compare(got, r.Version) != 0)
            {
                try { File.Delete(target); } catch { }
                throw new Exception("The release says v" + r.Version + " but the file inside it is v" + got
                                    + ". Nothing was changed.");
            }

            // Give a scanner the moment it takes to decide, then read the file
            // through. Quarantined or locked shows up here - while the old
            // launcher is still in place - instead of after the swap.
            await Task.Delay(2000);
            try
            {
                using (var fs = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var buf = new byte[81920];
                    while (fs.Read(buf, 0, buf.Length) > 0) { }
                }
            }
            catch (Exception ex)
            {
                Log("Update: the download could not be read back - " + ex.GetType().Name + ": " + ex.Message);
                try { File.Delete(target); } catch { }
                throw Blocked("Your antivirus blocked the new version after it downloaded.");
            }
            return target;
        }

        private static string DownloadPath
        {
            get { return Path.Combine(Path.GetDirectoryName(ExePath), "ABeautifulPotatoLauncher.update.exe"); }
        }

        /// <summary>The argument the new copy signals through - see SignalStarted.</summary>
        private const string SignalArg = "--started-signal";

        /// <summary>
        /// Swaps the verified download in, starts it, and waits for it to say
        /// it is running. The caller exits once this returns. If the new copy
        /// is blocked - it cannot start, or dies without signalling - the
        /// swap is undone and UpdateBlockedException says so; the player is
        /// never left without a launcher.
        /// </summary>
        public static async Task ApplyAndRestartAsync(string downloaded)
        {
            string exe = ExePath;
            string old = exe + ".old";

            try { File.Delete(old); } catch { }
            File.Move(exe, old);
            try
            {
                File.Move(downloaded, exe);
            }
            catch
            {
                try { File.Move(old, exe); } catch { }
                throw;
            }

            string name = "ABeautifulPotatoLauncher-started-" + Guid.NewGuid().ToString("N");
            using (var started = new EventWaitHandle(false, EventResetMode.ManualReset, name))
            {
                Process p;
                try
                {
                    p = Process.Start(new ProcessStartInfo(exe,
                            "--updated-from " + Program.Version + " " + SignalArg + " " + name)
                    {
                        UseShellExecute = false,
                        WorkingDirectory = Path.GetDirectoryName(exe)
                    });
                }
                catch (Exception ex)
                {
                    // 225 ERROR_VIRUS_INFECTED, 226 ERROR_VIRUS_DELETED - and
                    // access denied or not found when it was quarantined.
                    var w = ex as System.ComponentModel.Win32Exception;
                    Log("Update: the new version would not start - " + ex.GetType().Name
                        + (w != null ? " (error " + w.NativeErrorCode + ")" : "") + ": " + ex.Message);
                    RollBack(exe, old);
                    throw Blocked("Your antivirus stopped the new version from starting.");
                }

                // Up to half a minute: a first run can wait on a cloud scan.
                // Still running at the end of it is a slow start, not a block,
                // and is left alone - undoing the swap under a running copy
                // would do more harm than good.
                bool ok = await Task.Run(() =>
                {
                    var until = DateTime.UtcNow.AddSeconds(30);
                    while (DateTime.UtcNow < until)
                    {
                        if (started.WaitOne(250)) return true;
                        try { if (p != null && p.HasExited) return started.WaitOne(0); }
                        catch { }
                    }
                    return true;
                });

                if (!ok)
                {
                    int code = -1;
                    try { code = p.ExitCode; } catch { }
                    Log("Update: the new version closed before it started (exit code " + code + ").");
                    RollBack(exe, old);
                    throw Blocked("The new version was closed as it started - most likely by your antivirus.");
                }
            }
            Log("Updated to the downloaded version; restarting.");
        }

        /// <summary>Puts the old launcher back where it was, whatever is left of the new one.</summary>
        private static void RollBack(string exe, string old)
        {
            try
            {
                if (File.Exists(exe))
                {
                    try { File.Delete(exe); }
                    catch { File.Move(exe, exe + ".blocked"); }
                }
                File.Move(old, exe);
                Log("Update: undone - the previous version is back in place.");
            }
            catch (Exception ex)
            {
                Log("Update: could not put the previous version back - " + ex.Message);
            }
        }

        private static UpdateBlockedException Blocked(string what)
        {
            return new UpdateBlockedException(what + " This launcher is unchanged.\r\n\r\n"
                + "The new version can be downloaded from the release page instead - your "
                + "antivirus may ask you to allow it.");
        }

        /// <summary>
        /// Called first thing by a copy an update started: tells the old copy
        /// it is running, so the old one can exit. See ApplyAndRestartAsync.
        /// </summary>
        public static void SignalStarted(string[] args)
        {
            int i = Array.FindIndex(args, a => a.Equals(SignalArg, StringComparison.OrdinalIgnoreCase));
            if (i < 0 || i + 1 >= args.Length) return;
            try
            {
                EventWaitHandle h;
                if (EventWaitHandle.TryOpenExisting(args[i + 1], out h))
                    using (h) h.Set();
            }
            catch { }
        }

        /// <summary>
        /// Called at startup: removes what the last update left behind. The
        /// old exe may still be closing for a second or two, so this retries.
        /// </summary>
        public static void CleanUpAfterUpdate()
        {
            // ".new" is where versions before 0.9.2 downloaded to.
            var leftovers = new[] { ExePath + ".old", ExePath + ".new", ExePath + ".blocked", DownloadPath };
            Task.Run(async () =>
            {
                for (int i = 0; i < 10 && leftovers.Any(File.Exists); i++)
                {
                    foreach (string f in leftovers)
                        try { if (File.Exists(f)) File.Delete(f); } catch { }
                    await Task.Delay(1000);
                }
            });
        }

        // -------------------------------------------------------- helpers --

        private static WebClient NewClient()
        {
            // .NET Framework defaults can leave TLS 1.2 off, and GitHub refuses
            // anything older.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var wc = new WebClient { Encoding = System.Text.Encoding.UTF8 };
            wc.Headers[HttpRequestHeader.UserAgent] = "ABeautifulPotatoLauncher/" + Program.Version;
            wc.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
            return wc;
        }

        private static string InformationalVersionOf(string file)
        {
            try
            {
                var fvi = FileVersionInfo.GetVersionInfo(file);
                return string.IsNullOrEmpty(fvi.ProductVersion) ? null : fvi.ProductVersion;
            }
            catch { return null; }
        }

        private static string Str(Dictionary<string, object> o, string k)
        {
            object v;
            return o.TryGetValue(k, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : "";
        }

        private static bool Bool(Dictionary<string, object> o, string k)
        {
            object v;
            return o.TryGetValue(k, out v) && v is bool && (bool)v;
        }

        private static void Log(string line)
        {
            try
            {
                File.AppendAllText(Program.LogFile,
                    "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + line + Environment.NewLine);
            }
            catch { }
        }
    }
}
