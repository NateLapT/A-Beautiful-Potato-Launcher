// ---------------------------------------------------------------------------
//  The Beautiful Potato server list: a ready-made copy of Steam's list.
//
//  WHY
//    Asking Steam for a full list from the launcher is slow and rationed:
//    Steam caps a request at 10,000 servers, and after the first few requests
//    answers about one every 40-50 seconds, so a complete Project Zomboid
//    index takes half an hour. A server run for the launcher builds the whole
//    list continuously, from Steam's Web API, and the launcher downloads it -
//    a few hundred KB, in seconds, with nothing asked of Steam at all. The
//    same service reads every server's mod list and description, and ANSWERS
//    the mod filter and the game mode buttons when asked (Search) - a few KB
//    for the servers that match, instead of every server's mods and
//    descriptions (3.9 MB for DayZ) downloaded on every refresh.
//
//  FALLING BACK
//    Anything at all going wrong - no answer, an error, a list that has not
//    been rebuilt for a while - and the launcher asks Steam itself, exactly as
//    it did before this existed. The service is an accelerator, never a
//    dependency.
//
//  WHAT IS SENT
//    A plain GET for the current game's file, with the tag of the copy already
//    held so an unchanged file costs a few bytes. Nothing about the player.
//    A mod or game mode search sends the mods or modes picked - nothing else.
//    The exceptions are feedback and server reports, sent only when the player
//    presses SEND in those windows - see SendFeedback and SendReport.
//
//  ADDRESS
//    DefaultBase below. A "relay.txt" in the launcher's data folder overrides
//    it - another address for testing, or "off".
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;

namespace ABeautifulPotatoLauncher
{
    internal static class Relay
    {
        private const string DefaultBase = "https://serverlist.beautifulpotato.com";

        /// <summary>A list not rebuilt for this long is treated as unavailable.</summary>
        public static readonly TimeSpan MaxAge = TimeSpan.FromHours(2);

        static Relay()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        /// <summary>The service address, or null when turned off in relay.txt.</summary>
        public static string Base
        {
            get
            {
                try
                {
                    string f = Path.Combine(ServerStore.BaseDir, "relay.txt");
                    if (File.Exists(f))
                    {
                        string v = (File.ReadAllLines(f).FirstOrDefault() ?? "").Trim();
                        if (v.Equals("off", StringComparison.OrdinalIgnoreCase)) return null;
                        if (v.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return v.TrimEnd('/');
                    }
                }
                catch { }
                return DefaultBase;
            }
        }

        private static string GameId { get { return Games.IsZomboid ? "zomboid" : "dayz"; } }

        // ------------------------------------------------------------ list --

        /// <summary>
        /// The current game's full server list, or null with the reason in
        /// <paramref name="problem"/>. <paramref name="builtUtc"/> is when the
        /// service last saw the newest of them; a list older than MaxAge is
        /// returned as null too, so the caller asks Steam.
        /// </summary>
        public static List<BrowserServer> TryList(IDictionary<string, long> lastSeen,
                                                  out DateTime builtUtc, out string problem)
        {
            builtUtc = DateTime.MinValue;
            byte[] gz = Download("list", out problem);
            if (gz == null) return null;

            List<BrowserServer> list;
            try { list = ServerStore.ParseList(Lines(gz), lastSeen, false); }
            catch (Exception ex) { problem = "unreadable list (" + ex.Message + ")"; return null; }

            long newest = 0;
            if (lastSeen != null)
                foreach (var s in list)
                {
                    long t;
                    if (lastSeen.TryGetValue(s.Endpoint, out t) && t > newest) newest = t;
                }
            builtUtc = newest > 0 ? DateTimeOffset.FromUnixTimeSeconds(newest).UtcDateTime : DateTime.UtcNow;

            if (list.Count == 0) { problem = "the list is empty"; return null; }
            if (DateTime.UtcNow - builtUtc > MaxAge)
            {
                problem = "the list was last updated " + builtUtc.ToLocalTime().ToString("HH:mm");
                return null;
            }
            return list;
        }

        // ------------------------------------------------------------ maps --

        /// <summary>
        /// Every map name in the current game's list - for the Map dropdown,
        /// which would otherwise know only the maps of the servers on screen.
        /// Uses the copy already downloaded while it is recent, downloads it
        /// otherwise, and settles for an older copy if that fails: map names
        /// do not go stale the way player counts do. Null if there is no copy
        /// at all. Reads the whole list, so call it off the UI thread.
        /// </summary>
        public static List<string> TryMaps()
        {
            string cache = CacheFile("list");
            byte[] gz = null;
            try
            {
                if (File.Exists(cache) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cache) < MaxAge)
                    gz = File.ReadAllBytes(cache);
            }
            catch { }

            string problem;
            if (gz == null) gz = Download("list", out problem);

            try { if (gz == null && File.Exists(cache)) gz = File.ReadAllBytes(cache); }
            catch { }
            if (gz == null) return null;

            var maps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string line in Lines(gz))
                {
                    string[] f = line.Split('\t');
                    if (f.Length < 14) continue;
                    string m = f[1].Trim();
                    if (m.Length > 0) maps.Add(m);
                }
            }
            catch { return null; }
            return maps.ToList();
        }

        // --------------------------------------------------------- players --

        /// <summary>
        /// The players every server in the copy of the list kept last time
        /// reports, and on how many servers, and when that copy was
        /// downloaded - for the bottom bar before (or without) a download this
        /// session. False when there is no copy, or it is older than MaxAge.
        /// Reads the whole list, so call it off the UI thread.
        /// </summary>
        public static bool TrySavedPlayers(out long players, out int servers, out DateTime savedUtc)
        {
            players = 0;
            servers = 0;
            savedUtc = DateTime.MinValue;
            try
            {
                string cache = CacheFile("list");
                if (!File.Exists(cache)) return false;
                savedUtc = File.GetLastWriteTimeUtc(cache);
                if (DateTime.UtcNow - savedUtc > MaxAge) return false;
                foreach (string line in Lines(File.ReadAllBytes(cache)))
                {
                    string[] f = line.Split('\t');
                    int n;
                    if (f.Length < 14 || !int.TryParse(f[7], out n) || n <= 0) continue;
                    players += n;
                    servers++;
                }
                return true;
            }
            catch { return false; }
        }

        // ------------------------------------------------------------ mods --

        /// <summary>
        /// The commonest mods across every listed server, with how many run
        /// each, commonest first - for the mod picker. From the service (a few
        /// bytes when unchanged), or the copy kept last time. Null when there
        /// is neither.
        /// </summary>
        public static List<KeyValuePair<string, int>> TryModIndex()
        {
            string problem;
            byte[] gz = Download("mods", out problem);
            try { if (gz == null && File.Exists(CacheFile("mods"))) gz = File.ReadAllBytes(CacheFile("mods")); }
            catch { }
            if (gz == null) return null;

            var mods = new List<KeyValuePair<string, int>>();
            try
            {
                foreach (string line in Lines(gz))
                {
                    int tab = line.LastIndexOf('\t');
                    int n;
                    if (tab > 0 && int.TryParse(line.Substring(tab + 1), out n))
                        mods.Add(new KeyValuePair<string, int>(line.Substring(0, tab), n));
                }
            }
            catch { return null; }
            return mods;
        }

        /// <summary>
        /// Asks the service which listed servers run EVERY one of
        /// <paramref name="mods"/> (part of the name, any case) and claim EVERY
        /// one of <paramref name="modes"/> in their name or description - the
        /// launcher's own mod and game mode rules, answered over every server's
        /// mods and description. "host:port" of each; null with the reason in
        /// <paramref name="problem"/> when it could not be asked.
        /// </summary>
        public static HashSet<string> Search(IEnumerable<string> mods, IEnumerable<string> modes, out string problem)
        {
            problem = null;
            string root = Base;
            if (root == null) { problem = "turned off in relay.txt"; return null; }

            var query = new StringBuilder();
            foreach (string m in mods ?? Enumerable.Empty<string>())
                query.Append(query.Length == 0 ? "?" : "&").Append("mod=").Append(Uri.EscapeDataString(m));
            foreach (string m in modes ?? Enumerable.Empty<string>())
                query.Append(query.Length == 0 ? "?" : "&").Append("mode=").Append(Uri.EscapeDataString(m));
            if (query.Length == 0) { problem = "nothing to search for"; return null; }

            try
            {
                var req = (HttpWebRequest)WebRequest.Create(root + "/v1/" + GameId + "/search" + query);
                req.Method = "GET";
                req.Timeout = 8000;
                req.ReadWriteTimeout = 15000;
                req.UserAgent = "ABeautifulPotatoLauncher/" + Program.Version;
                byte[] gz;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var body = resp.GetResponseStream())
                using (var ms = new MemoryStream())
                {
                    body.CopyTo(ms);
                    gz = ms.ToArray();
                }

                var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in Lines(gz))
                {
                    string ep = line.Trim();
                    if (ep.IndexOf(':') > 0) found.Add(ep);
                }
                return found;
            }
            catch (WebException ex)
            {
                var r = ex.Response as HttpWebResponse;
                problem = r == null ? ex.Message : "HTTP " + (int)r.StatusCode;
                return null;
            }
            catch (Exception ex)
            {
                problem = ex.Message;
                return null;
            }
        }

        // -------------------------------------------------------- feedback --

        /// <summary>
        /// Sends what the player wrote in the Feedback window, with the
        /// launcher version, the game and the Windows version - plus the
        /// server they name, and the mods list and launcher log only when they
        /// tick those boxes (null otherwise). True when the service has it;
        /// otherwise false with the reason in <paramref name="problem"/>.
        /// </summary>
        public static bool SendFeedback(string message, string contact, string server, string mods, string log,
                                        out string problem)
        {
            return Post("feedback", new[]
            {
                Pair("version", Program.VersionLabel),
                Pair("game", Games.Name),
                Pair("os", Environment.OSVersion.VersionString + (Environment.Is64BitOperatingSystem ? " 64-bit" : "")),
                Pair("contact", contact),
                Pair("server", server),
                Pair("message", message),
                Pair("mods", mods),
                Pair("log", log)
            }, "messages", out problem);
        }

        // --------------------------------------------------------- reports --

        /// <summary>
        /// Reports a server from the flag at the end of its row: which
        /// server (address, name, map, the player count it claimed), the
        /// reason picked, and what the player wrote. Reviewed by hand at the
        /// other end; several reports of one server are read together there.
        /// A server owner's review request goes the same way, with reason
        /// ReportServerDialog.ReviewReason, their website and a contact.
        /// </summary>
        public static bool SendReport(string host, int port, int queryPort, string name, string map,
                                      int players, int maxPlayers, string reason, string details, string contact,
                                      string website, out string problem)
        {
            return Post("report", new[]
            {
                Pair("version", Program.VersionLabel),
                Pair("game", Games.Name),
                Pair("endpoint", host + ":" + port),
                Pair("queryport", queryPort > 0 ? queryPort.ToString() : ""),
                Pair("name", name),
                Pair("map", map),
                Pair("players", players + "/" + maxPlayers),
                Pair("reason", reason),
                Pair("details", details),
                Pair("contact", contact),
                Pair("website", website)
            }, "reports", out problem);
        }

        private static KeyValuePair<string, string> Pair(string k, string v) { return new KeyValuePair<string, string>(k, v); }

        /// <summary>
        /// A form post to /v1/{path}; empty fields are left out. The words for
        /// a 429 name what was sent too often ("messages", "reports").
        /// </summary>
        private static bool Post(string path, IEnumerable<KeyValuePair<string, string>> fields, string what,
                                 out string problem)
        {
            problem = null;
            string root = Base;
            if (root == null) { problem = "the server list service is turned off in relay.txt"; return false; }

            try
            {
                var body = new StringBuilder();
                foreach (var kv in fields)
                {
                    if (string.IsNullOrEmpty(kv.Value)) continue;
                    if (body.Length > 0) body.Append('&');
                    body.Append(kv.Key).Append('=').Append(EscapeLong(kv.Value));
                }

                byte[] data = Encoding.UTF8.GetBytes(body.ToString());
                var req = (HttpWebRequest)WebRequest.Create(root + "/v1/" + path);
                req.Method = "POST";
                req.ContentType = "application/x-www-form-urlencoded; charset=utf-8";
                req.ContentLength = data.Length;
                req.Timeout = 15000;
                req.ReadWriteTimeout = 30000;
                req.UserAgent = "ABeautifulPotatoLauncher/" + Program.Version;
                using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);
                using (var resp = (HttpWebResponse)req.GetResponse()) { }
                return true;
            }
            catch (WebException ex)
            {
                var r = ex.Response as HttpWebResponse;
                problem = r == null ? ex.Message
                        : (int)r.StatusCode == 429 ? "too many " + what + " from here in the last hour - please try again later"
                        : (int)r.StatusCode == 413 ? "it is too long - try leaving the log or the mods list out"
                        : "HTTP " + (int)r.StatusCode;
                return false;
            }
            catch (Exception ex)
            {
                problem = ex.Message;
                return false;
            }
        }

        /// <summary>Uri.EscapeDataString, which refuses strings over ~32 K, in pieces.</summary>
        private static string EscapeLong(string v)
        {
            var sb = new StringBuilder(v.Length + v.Length / 4);
            int i = 0;
            while (i < v.Length)
            {
                int n = Math.Min(16000, v.Length - i);
                // Never split a surrogate pair across two pieces.
                if (i + n < v.Length && char.IsHighSurrogate(v[i + n - 1])) n--;
                sb.Append(Uri.EscapeDataString(v.Substring(i, n)));
                i += n;
            }
            return sb.ToString();
        }

        // ----------------------------------------------------------- flags --

        /// <summary>
        /// Servers a person has reviewed, by "host:port": 1 caution, 2 danger,
        /// 3 trusted - the colour of the report flag, and nothing else. From the service, or the copy kept last time when it cannot
        /// be reached. Null when there is neither.
        /// </summary>
        public static Dictionary<string, int> TryFlags()
        {
            string problem;
            byte[] gz = Download("flags", out problem);
            try { if (gz == null && File.Exists(CacheFile("flags"))) gz = File.ReadAllBytes(CacheFile("flags")); }
            catch { }
            if (gz == null) return null;

            var flags = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (gz.Length == 0) return flags;       // nothing flagged: an empty file, not a gzip one
            try
            {
                foreach (string line in Lines(gz))
                {
                    string[] f = line.Split('	');
                    int level;
                    if (f.Length >= 2 && f[0].IndexOf(':') > 0 && int.TryParse(f[1], out level) && level >= 1 && level <= 3)
                        flags[f[0].Trim()] = level;
                }
            }
            catch { return null; }
            return flags;
        }

        // -------------------------------------------------------- approved --

        /// <summary>
        /// Servers over 127 but under 180 slots that a person has checked and
        /// approved, by "host:port" - real servers running more than DayZ's
        /// usual 127, which the slot rule would otherwise hide. From the
        /// service, or the copy kept last time. Null when there is neither.
        /// </summary>
        public static HashSet<string> TryApproved()
        {
            string problem;
            byte[] gz = Download("approved", out problem);
            try { if (gz == null && File.Exists(CacheFile("approved"))) gz = File.ReadAllBytes(CacheFile("approved")); }
            catch { }
            if (gz == null) return null;

            var approved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (gz.Length == 0) return approved;
            try
            {
                foreach (string line in Lines(gz))
                {
                    string ep = line.Trim();
                    if (ep.IndexOf(':') > 0) approved.Add(ep);
                }
            }
            catch { return null; }
            return approved;
        }

        // --------------------------------------------------------- malware --

        /// <summary>
        /// Mods flagged by hand as malware, and the listed servers running them
        /// - see MalwareInfo. From the service, or the copy kept last time.
        /// Null when there is neither (or this relay doesn't publish it yet).
        /// </summary>
        public static MalwareInfo TryMalware()
        {
            string problem;
            byte[] gz = Download("malware", out problem);
            try { if (gz == null && File.Exists(CacheFile("malware"))) gz = File.ReadAllBytes(CacheFile("malware")); }
            catch { }
            if (gz == null) return null;

            var info = new MalwareInfo();
            if (gz.Length == 0) return info;
            try
            {
                foreach (string line in Lines(gz))
                {
                    string[] f = line.Split('\t');
                    if (f.Length >= 3 && f[0] == "mod")
                    {
                        ulong id;
                        if (ulong.TryParse(f[1], out id) && id != 0) info.Ids.Add(id);
                        string name = f[2].Trim();
                        if (name.Length > 0) info.Names.Add(name);
                        info.Shown[id != 0 ? id.ToString() : name] = name.Length > 0 ? name : id.ToString();
                    }
                    else if (f.Length >= 2 && f[0] == "server" && f[1].IndexOf(':') > 0)
                        info.Servers.Add(f[1].Trim());
                    else if (f.Length >= 2 && f[0] == "block" && f[1].Trim().Length > 0)
                    {
                        // "ip:port" one server, "ip" every server on the address.
                        string a = f[1].Trim();
                        if (a.IndexOf(':') > 0) info.BlockedServers.Add(a); else info.BlockedHosts.Add(a);
                    }
                }
            }
            catch { return null; }
            return info;
        }

        // ------------------------------------------------------------ wire --

        private static string CacheFile(string kind) { return Path.Combine(ServerStore.GameDir, "relay-" + kind + ".gz"); }
        private static string TagFile(string kind) { return CacheFile(kind) + ".etag"; }

        private static string ReadTag(string kind)
        {
            try { return File.Exists(TagFile(kind)) ? File.ReadAllText(TagFile(kind)).Trim() : null; }
            catch { return null; }
        }

        /// <summary>
        /// The file, from the service or - when it answers "not modified" - from
        /// the copy kept last time. Null on any failure.
        /// </summary>
        private static byte[] Download(string kind, out string problem)
        {
            problem = null;
            string root = Base;
            if (root == null) { problem = "turned off in relay.txt"; return null; }

            string cache = CacheFile(kind);
            string tag = File.Exists(cache) ? ReadTag(kind) : null;

            try
            {
                var req = (HttpWebRequest)WebRequest.Create(root + "/v1/" + GameId + "/" + kind);
                req.Method = "GET";
                req.Timeout = 8000;
                req.ReadWriteTimeout = 30000;
                req.UserAgent = "ABeautifulPotatoLauncher/" + Program.Version;
                if (tag != null) req.Headers[HttpRequestHeader.IfNoneMatch] = tag;

                try
                {
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (var body = resp.GetResponseStream())
                    using (var ms = new MemoryStream())
                    {
                        body.CopyTo(ms);
                        byte[] data = ms.ToArray();
                        File.WriteAllBytes(cache, data);
                        string newTag = resp.Headers[HttpResponseHeader.ETag];
                        if (!string.IsNullOrEmpty(newTag)) File.WriteAllText(TagFile(kind), newTag);
                        return data;
                    }
                }
                catch (WebException ex)
                {
                    var r = ex.Response as HttpWebResponse;
                    if (r != null && r.StatusCode == HttpStatusCode.NotModified && File.Exists(cache))
                        return File.ReadAllBytes(cache);
                    problem = r == null ? ex.Message : "HTTP " + (int)r.StatusCode;
                    return null;
                }
            }
            catch (Exception ex)
            {
                problem = ex.Message;
                return null;
            }
        }

        private static IEnumerable<string> Lines(byte[] gz)
        {
            using (var ms = new MemoryStream(gz))
            using (var z = new GZipStream(ms, CompressionMode.Decompress))
            using (var rd = new StreamReader(z, Encoding.UTF8))
            {
                var lines = new List<string>(40000);
                string line;
                while ((line = rd.ReadLine()) != null) lines.Add(line);
                return lines;
            }
        }
    }
}
