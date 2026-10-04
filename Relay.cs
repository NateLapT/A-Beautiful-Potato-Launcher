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
//    same service gathers every server's mod list and description, so the
//    "Has Mods" filter and descriptions work for every server straight away.
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

        // ----------------------------------------------------------- rules --

        /// <summary>
        /// Every server's raw A2S_RULES reply, by "host:port", for A2S to parse.
        /// Null when unavailable, or when it is the copy already applied
        /// (<paramref name="appliedTag"/>), so nothing is parsed twice.
        /// </summary>
        public static Dictionary<string, byte[]> TryRules(string appliedTag, out string tag, out string problem)
        {
            tag = null;
            byte[] gz = Download("rules", out problem);
            if (gz == null) return null;

            tag = ReadTag("rules");
            if (tag != null && tag == appliedTag) { problem = "unchanged"; return null; }

            var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var ms = new MemoryStream(gz))
                using (var z = new GZipStream(ms, CompressionMode.Decompress))
                using (var r = new BinaryReader(z))
                {
                    if (Encoding.ASCII.GetString(r.ReadBytes(6)) != "ABPLR1") { problem = "unknown format"; return null; }
                    while (true)
                    {
                        ushort len;
                        try { len = r.ReadUInt16(); }
                        catch (EndOfStreamException) { break; }
                        string ep = Encoding.UTF8.GetString(r.ReadBytes(len));
                        r.ReadInt64();                                  // when it answered
                        int n = r.ReadInt32();
                        if (n < 0 || n > 1 << 20) { problem = "corrupt"; return null; }
                        result[ep] = r.ReadBytes(n);
                    }
                }
            }
            catch (Exception ex) { problem = "unreadable (" + ex.Message + ")"; return null; }
            return result;
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
