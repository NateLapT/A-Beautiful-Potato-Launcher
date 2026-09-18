// ---------------------------------------------------------------------------
//  Talking to DayZ servers directly over UDP (Valve's A2S protocol).
//
//  WHY THIS EXISTS
//    The official Experimental launcher can tell you a server needs a mod, but
//    it can only offer "Load from library" - it cannot subscribe, because
//    Experimental is a different Steam app (1024020) from the one that owns
//    DayZ's workshop content (221100). That is the "MANUAL SETUP MAY BE
//    REQUIRED" banner players keep hitting.
//
//    A2S sidesteps all of it. The server itself publishes the list of mods it
//    requires, WITH their workshop ids, so this launcher can read that list,
//    subscribe to anything missing, and launch - no manual setup at all.
//
//  TWO QUERIES ARE USED
//    A2S_INFO  (0x54) - name, map, player counts. Cheap; used for the list.
//    A2S_RULES (0x56) - carries DayZ's packed mod list. Used before launching.
//
//  QUERY PORT
//    Verified against the live servers: the query port is the GAME port + 1
//    (4902 -> 4903, 2402 -> 2403, 2302 -> 2303).
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BeautifulPotatoExpLauncher
{
    internal sealed class ServerInfo
    {
        public string Name = "";
        public string Map = "";
        public int Players;
        public int MaxPlayers;
        public int PingMs = -1;
        public string Description = "";
        public bool Online;
        public string Error = "";

        /// <summary>
        /// Which Steam app the server runs: 221100 is DayZ stable, 1024020 is
        /// Experimental. This decides which game folder to launch, so getting it
        /// wrong means starting the wrong build against the server.
        /// </summary>
        public ulong AppId;

        public string Version = "";
        public string Keywords = "";

        public bool IsExperimental { get { return AppId == A2S.ExperimentalAppId; } }
        public bool IsStable       { get { return AppId == A2S.StableAppId; } }

        public string GameLabel
        {
            get
            {
                if (IsExperimental) return "Experimental";
                if (IsStable) return "Stable";
                return AppId == 0 ? "" : "app " + AppId;
            }
        }
    }

    /// <summary>
    /// Everything A2S_RULES carries: the mod list, the signing keys the server
    /// accepts, the free-text description shown in the launcher, and the plain
    /// text rules that sit alongside the packed blob.
    /// </summary>
    internal sealed class ServerRules
    {
        public List<Mod> Mods = new List<Mod>();
        public List<string> Signatures = new List<string>();

        /// <summary>
        /// True when the mod list was proven to end cleanly - see A2S.TailFits.
        /// The count byte in the header is NOT used for this: one server claims
        /// 109 there while listing 34, and trusting it produced a bogus
        /// "reply was cut short" warning on a perfectly complete list.
        /// </summary>
        public bool Complete;
        public string Description = "";
        public readonly Dictionary<string, string> Text =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string Get(string key)
        {
            string v;
            return Text.TryGetValue(key, out v) ? v : "";
        }

        /// <summary>"129" and "1.30" both appear in the wild; show them alike.</summary>
        public string RequiredVersion
        {
            get
            {
                string v = Get("requiredVersion");
                if (v.Length == 3 && !v.Contains(".")) return v[0] + "." + v.Substring(1) + ".*";
                return v.Length > 0 ? v + ".*" : "";
            }
        }
    }

    internal static class A2S
    {
        private static readonly byte[] Header = { 0xFF, 0xFF, 0xFF, 0xFF };
        private static readonly byte[] InfoPayload =
            Header.Concat(Encoding.ASCII.GetBytes("TSource Engine Query\0")).ToArray();

        public const ulong StableAppId       = 221100;
        public const ulong ExperimentalAppId  = 1024020;

        /// <summary>
        /// The usual DayZ convention, and the only guess available when all you
        /// have is a game port. It is NOT universal - plenty of hosts run the
        /// query on 27016 or elsewhere - so prefer the port the master list
        /// reports whenever there is one. See Effective().
        /// </summary>
        public static int QueryPort(int gamePort) { return gamePort + 1; }

        /// <summary>
        /// Picks the port to query: the one Steam reported if it looks sane,
        /// otherwise game port + 1. Steam uses 0 and 65535 to mean "unknown".
        /// </summary>
        public static int Effective(int gamePort, int reportedQueryPort)
        {
            if (reportedQueryPort > 0 && reportedQueryPort < 65535) return reportedQueryPort;
            return QueryPort(gamePort);
        }

        // ------------------------------------------------------------ wire --

        private static byte[] Exchange(string host, int port, byte[] payload, int timeoutMs, out int pingMs)
        {
            pingMs = -1;
            using (var udp = new UdpClient())
            {
                udp.Client.ReceiveTimeout = timeoutMs;
                udp.Client.SendTimeout = timeoutMs;
                var ep = new IPEndPoint(IPAddress.Any, 0);

                var sw = Stopwatch.StartNew();
                udp.Send(payload, payload.Length, host, port);
                byte[] data = udp.Receive(ref ep);
                pingMs = (int)sw.ElapsedMilliseconds;

                // Valve servers answer 'A' with a challenge that has to be
                // echoed back before they will part with the real reply.
                if (data.Length >= 9 && data[4] == (byte)'A')
                {
                    var challenge = new byte[4];
                    Array.Copy(data, 5, challenge, 0, 4);

                    byte[] second;
                    if (payload.Length >= 4 && payload.Skip(payload.Length - 4).All(b => b == 0xFF))
                        second = payload.Take(payload.Length - 4).Concat(challenge).ToArray();
                    else
                        second = payload.Concat(challenge).ToArray();

                    udp.Send(second, second.Length, host, port);
                    data = udp.Receive(ref ep);
                }
                return data;
            }
        }

        private static string ReadCString(byte[] b, ref int i)
        {
            int start = i;
            while (i < b.Length && b[i] != 0) i++;
            string s = Encoding.UTF8.GetString(b, start, i - start);
            i++;                               // step over the NUL
            return s;
        }

        // ------------------------------------------------------- A2S_INFO --

        public static ServerInfo GetInfo(string host, int gamePort, int timeoutMs = 2000)
        {
            return GetInfoAt(host, QueryPort(gamePort), timeoutMs);
        }

        /// <summary>As GetInfo, but the caller already knows the query port.</summary>
        public static ServerInfo GetInfoAt(string host, int queryPort, int timeoutMs = 2000)
        {
            var info = new ServerInfo();
            try
            {
                int ping;
                byte[] d = Exchange(host, queryPort, InfoPayload, timeoutMs, out ping);
                if (d.Length < 6 || d[4] != (byte)'I') { info.Error = "unexpected reply"; return info; }

                int i = 5;
                i++;                                   // protocol
                info.Name = ReadCString(d, ref i);
                info.Map = ReadCString(d, ref i);
                ReadCString(d, ref i);                 // folder ("dayz")
                info.Description = ReadCString(d, ref i);
                // This 16-bit app id is always 0 for DayZ - both its app ids are
                // far larger than 65535, so the real one only appears in the
                // GameID field of the Extra Data Flag block at the very end.
                i += 2;
                info.Players = d[i++];
                info.MaxPlayers = d[i++];
                i++;                                   // bots
                i++;                                   // server type
                i++;                                   // environment
                i++;                                   // visibility
                i++;                                   // VAC
                info.Version = ReadCString(d, ref i);

                if (i < d.Length)
                {
                    byte edf = d[i++];
                    if ((edf & 0x80) != 0) i += 2;                       // port
                    if ((edf & 0x10) != 0) i += 8;                       // steam id
                    if ((edf & 0x40) != 0) { i += 2; ReadCString(d, ref i); }
                    if ((edf & 0x20) != 0) info.Keywords = ReadCString(d, ref i);
                    if ((edf & 0x01) != 0 && i + 8 <= d.Length)
                    {
                        ulong gameId = BitConverter.ToUInt64(d, i); i += 8;
                        info.AppId = gameId & 0xFFFFFF;                  // low 24 bits
                    }
                }

                info.PingMs = ping;
                info.Online = true;
            }
            catch (SocketException)
            {
                info.Error = "no reply";
            }
            catch (Exception ex)
            {
                info.Error = ex.Message;
            }
            return info;
        }

        // ------------------------------------------------------ A2S_RULES --

        /// <summary>
        /// Fetches the server's rules and reassembles DayZ's packed blob.
        ///
        /// The reply is chunked: each "rule" is a two-byte key holding
        /// (chunk index, chunk count) and a value carrying up to 127 bytes of
        /// one long binary payload, which is split mid-string without regard
        /// for content. Ordinary text rules (island, timeLeft, ...) sit
        /// alongside and are skipped.
        /// </summary>
        private static byte[] RulesBlob(string host, int queryPort, int timeoutMs)
        {
            Dictionary<string, string> ignored;
            return RulesBlob(host, queryPort, timeoutMs, out ignored);
        }

        private static byte[] RulesBlob(string host, int queryPort, int timeoutMs,
                                        out Dictionary<string, string> textRules)
        {
            textRules = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var payload = Header.Concat(new byte[] { (byte)'V', 0xFF, 0xFF, 0xFF, 0xFF }).ToArray();
            int ping;
            byte[] d = Exchange(host, queryPort, payload, timeoutMs, out ping);
            if (d.Length < 7 || d[4] != (byte)'E') return null;

            int count = BitConverter.ToUInt16(d, 5);
            var tokens = new List<byte[]>();
            int i = 7;
            for (int n = 0; n < count * 2 && i < d.Length; n++)
            {
                int start = i;
                while (i < d.Length && d[i] != 0) i++;
                var tok = new byte[i - start];
                Array.Copy(d, start, tok, 0, tok.Length);
                tokens.Add(tok);
                i++;
            }

            var chunks = new SortedDictionary<int, byte[]>();
            for (int k = 0; k + 1 < tokens.Count; k += 2)
            {
                byte[] key = tokens[k];
                if (key.Length == 2 && key[0] >= 1 && key[0] <= key[1])
                {
                    chunks[key[0]] = tokens[k + 1];
                    continue;
                }

                // Ordinary text rules - island, requiredVersion, timeLeft and
                // so on - sit interleaved with the chunks of the binary blob.
                try
                {
                    string kk = Encoding.UTF8.GetString(key);
                    string vv = Encoding.UTF8.GetString(tokens[k + 1]);
                    if (kk.Length > 0 && kk.All(c => c >= 32)) textRules[kk] = vv;
                }
                catch { }
            }
            if (chunks.Count == 0) return null;

            var all = new List<byte>();
            foreach (var kv in chunks) all.AddRange(kv.Value);
            return all.ToArray();
        }

        /// <summary>
        /// Reads the mod list out of the reassembled blob.
        ///
        /// Layout, after a header whose length is not documented and which
        /// differs between servers:
        ///     byte modCount, modCount x { uint32 hash, byte idSize,
        ///                                 idSize-byte workshop id, byte len, name }
        ///     byte sigCount, sigCount x { byte len, name }
        ///     [ byte len, description ]
        ///
        /// So rather than guess the header, EVERY plausible header length is
        /// tried and only a parse that consumes the buffer exactly is accepted.
        /// A server with no mods omits the mod section altogether instead of
        /// writing a zero count, so that variant is tried too. Verified against
        /// three live servers - 3 mods, 16 mods, and none.
        /// </summary>
        public static List<Mod> ParseMods(byte[] blob)
        {
            var r = ParseBlob(blob);
            return r == null ? null : r.Mods;
        }

        /// <summary>
        /// Pulls the mod list out of the packed blob. Each record is one STEAM
        /// WORKSHOP ITEM - the thing with a workshop id and a page - not a pbo
        /// inside it.
        ///
        /// WHY THIS SCANS INSTEAD OF WALKING
        ///   A record is: uint32 hash, a 0x04 marker, uint32 workshop id, a
        ///   length byte, then the name. Walking them in sequence works on most
        ///   servers and then derails - a stray byte turns up between records on
        ///   some, and the container around the list is not documented. Walking
        ///   strictly meant servers with 13, 34 or 63 mods reported NONE, which
        ///   is worse than useless because it reads as "no mods required".
        ///
        ///   So each position is tested against the record shape and the strong
        ///   constraints it carries - the marker, a workshop id in a plausible
        ///   range, a sane name length, a name free of control characters. A
        ///   mismatch costs one byte rather than the whole list.
        ///
        /// WHERE THE LIST ENDS
        ///   NOT at the count byte in the header: that byte is unreliable. One
        ///   server advertised 109 there while genuinely listing 34, which had
        ///   this launcher claiming the reply was "cut short" when it was whole.
        ///   Instead the end is found by proof: at each record boundary, test
        ///   whether everything remaining parses as the signature list plus the
        ///   description AND lands exactly on the final byte. The first position
        ///   where that holds is the real end. That also stops the scan wandering
        ///   into the signature names and inventing mods out of them.
        ///
        ///   Verified against fifteen live servers - 0, 6, 8, 9, 11, 13, 16, 20,
        ///   22, 23, 32, 34, 46, 58 and 63 mods - all ending exactly.
        /// </summary>
        public static ServerRules ParseBlob(byte[] blob)
        {
            var result = new ServerRules();
            if (blob == null || blob.Length < 12) return result;

            // Find where the records start. The header length varies, so it is
            // discovered - but it is always short, and that bound is what keeps
            // the description at the END of the blob from being read as a mod on
            // a server that has none.
            int first = -1;
            int limit = Math.Min(blob.Length, MaxHeaderScan);
            for (int i = 1; i < limit; i++)
            {
                ulong id; string name; int next;
                if (TryRecord(blob, i, out id, out name, out next)) { first = i; break; }
            }
            if (first < 0)
            {
                // No records anywhere in the header window: this server really
                // has no mods, and that is a complete answer, not a failure.
                result.Complete = true;
                return result;
            }

            int at = first;
            while (at < blob.Length)
            {
                if (result.Mods.Count > 0 && TailFits(blob, at))
                {
                    ReadTail(blob, at, result);        // proven end of the list
                    result.Complete = true;
                    return result;
                }

                ulong id; string name; int next;
                if (TryRecord(blob, at, out id, out name, out next))
                {
                    result.Mods.Add(new Mod(name, id));
                    at = next;
                }
                else
                {
                    at++;                              // resynchronise
                }
            }

            return result;
        }

        /// <summary>How far in the mod records may start; see ParseBlob.</summary>
        private const int MaxHeaderScan = 14;

        /// <summary>Tests one position against the record shape.</summary>
        private static bool TryRecord(byte[] b, int i, out ulong id, out string name, out int next)
        {
            id = 0; name = null; next = i;
            if (i + 10 > b.Length) return false;
            if (b[i + 4] != 0x04) return false;        // the marker before the id

            id = BitConverter.ToUInt32(b, i + 5);
            if (id < 100000 || id > 4000000000) return false;

            int len = b[i + 9];
            if (len < 2 || len > 64) return false;
            if (i + 10 + len > b.Length) return false;

            for (int k = i + 10; k < i + 10 + len; k++)
                if (b[k] < 32 || b[k] == 127) return false;   // names are printable

            try { name = Encoding.UTF8.GetString(b, i + 10, len); }
            catch { return false; }

            next = i + 10 + len;
            return true;
        }

        /// <summary>
        /// Does everything from here parse as the signature list plus the
        /// description, finishing exactly on the last byte? That exactness is
        /// the proof that the mod list ended here and nothing was lost.
        ///
        /// Entry CONTENT is deliberately not policed - some signature entries
        /// carry bytes that are not text, and rejecting those made whole servers
        /// look truncated when they were complete.
        /// </summary>
        private static bool TailFits(byte[] b, int i)
        {
            if (i >= b.Length) return false;

            int count = b[i]; i++;
            if (count > 200) return false;

            for (int k = 0; k < count; k++)
            {
                if (i >= b.Length) return false;
                int len = b[i]; i++;
                if (i + len > b.Length) return false;
                i += len;
            }

            if (i < b.Length)
            {
                int len = b[i]; i++;
                if (i + len > b.Length) return false;
                i += len;
            }
            return i == b.Length;
        }

        /// <summary>Reads the signatures and description once TailFits agrees.</summary>
        private static void ReadTail(byte[] b, int i, ServerRules result)
        {
            try
            {
                int count = b[i]; i++;
                for (int k = 0; k < count && i < b.Length; k++)
                {
                    int len = b[i]; i++;
                    if (len <= 0 || i + len > b.Length) return;
                    result.Signatures.Add(Encoding.UTF8.GetString(b, i, len));
                    i += len;
                }

                if (i < b.Length)
                {
                    int len = b[i]; i++;
                    if (len > 0 && i + len <= b.Length)
                        result.Description = Encoding.UTF8.GetString(b, i, len);
                }
            }
            catch { /* the tail is a bonus, never worth losing the mods over */ }
        }

        /// <summary>
        /// The mods a server requires, or null when that could not be
        /// determined. An empty list is a real answer: the server needs none.
        /// </summary>
        public static List<Mod> GetRequiredMods(string host, int gamePort, int timeoutMs = 3000)
        {
            var r = GetRules(host, gamePort, timeoutMs);
            return r == null ? null : r.Mods;
        }

        /// <summary>
        /// The server's full rules: mods, signing keys, description and text
        /// rules. Null when the server did not answer at all - an empty mod
        /// list is a real answer meaning "this server needs none".
        /// </summary>
        public static ServerRules GetRules(string host, int gamePort, int timeoutMs = 3000)
        {
            return GetRulesAt(host, QueryPort(gamePort), timeoutMs);
        }

        /// <summary>As GetRules, but the caller already knows the query port.</summary>
        public static ServerRules GetRulesAt(string host, int queryPort, int timeoutMs = 3000)
        {
            try
            {
                Dictionary<string, string> text;
                byte[] blob = RulesBlob(host, queryPort, timeoutMs, out text);
                if (blob == null) return null;

                var parsed = ParseBlob(blob);
                if (parsed == null) return null;      // never report failure as "no mods"
                foreach (var kv in text) parsed.Text[kv.Key] = kv.Value;
                return parsed;
            }
            catch
            {
                return null;
            }
        }
    }
}
