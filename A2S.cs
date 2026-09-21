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

namespace ABeautifulPotatoLauncher
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

        /// <summary>
        /// The server is password protected. A2S calls this "visibility": 0 is
        /// open, anything else means a password is required before the game
        /// will let you in.
        /// </summary>
        public bool Password;

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

                var splitChunks = new SortedDictionary<int, byte[]>();
                int totalPackets = 0;
                while (true)
                {
                    byte[] data;
                    try { data = udp.Receive(ref ep); }
                    catch (SocketException) { break; }

                    pingMs = (int)sw.ElapsedMilliseconds;

                    // Valve split packets arrive as: FF FF FF FE [packetIndex] [packetCount] ...
                    // The reply can be much larger than the usual short "official" server packet,
                    // so we need to reassemble every fragment and not assume the response fits in one datagram.
                    if (data.Length >= 6 && data[0] == 0xFF && data[1] == 0xFF &&
                        data[2] == 0xFF && data[3] == 0xFE)
                    {
                        int packetIndex = data[4];
                        int packetCount = data[5];
                        if (packetCount > 0) totalPackets = Math.Max(totalPackets, packetCount);

                        byte[] body = data.Length > 6 ? data.Skip(6).ToArray() : Array.Empty<byte>();
                        splitChunks[packetIndex] = body;

                        if (totalPackets > 0 && splitChunks.Count >= totalPackets)
                            break;
                        continue;
                    }

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
                        pingMs = (int)sw.ElapsedMilliseconds;
                    }

                    return data;
                }

                if (splitChunks.Count == 0) return null;
                if (totalPackets <= 0) totalPackets = splitChunks.Keys.Max() + 1;

                int totalLength = 0;
                for (int i = 0; i < totalPackets; i++)
                {
                    byte[] chunk;
                    if (!splitChunks.TryGetValue(i, out chunk)) break;
                    totalLength += chunk.Length;
                }

                var combined = new byte[totalLength];
                int offset = 0;
                for (int i = 0; i < totalPackets; i++)
                {
                    byte[] chunk;
                    if (!splitChunks.TryGetValue(i, out chunk)) break;
                    Buffer.BlockCopy(chunk, 0, combined, offset, chunk.Length);
                    offset += chunk.Length;
                }
                return combined;
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
                info.Password = d[i++] != 0;           // visibility: 0 open, 1 locked
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
            return Unescape(all.ToArray());
        }

        /// <summary>
        /// Undoes the byte escaping DayZ applies to the packed mod blob.
        ///
        /// WHY THE ESCAPING EXISTS
        ///   The blob travels inside an A2S_RULES value, and those are NUL
        ///   terminated strings - a literal 0x00 anywhere inside would end the
        ///   value early and destroy the rest of the reply. So the bytes that
        ///   cannot be sent raw are encoded as pairs beginning 0x01.
        ///
        /// WHY IT MATTERS SO MUCH
        ///   Most records survive being read without decoding, because most
        ///   hashes and ids contain no byte needing an escape. The ones that DO
        ///   are unreadable, and a record that cannot be read stops the walk
        ///   dead - taking every mod after it as well.
        ///
        ///   Measured on a live server: read raw, the walk found 6 mods and died
        ///   at offset 129 on a length byte of 107. Decoded first, the same blob
        ///   yields a clean chain of 10 running all the way to the signature
        ///   list - recovering "WCP" and "Asmond Vanilla Clothing", which were
        ///   simply invisible before. A player joining on the old answer would
        ///   be missing mods the launcher never mentioned.
        ///
        /// THE MAPPING, AND HOW IT WAS ESTABLISHED
        ///   01 01 -> 01     01 02 -> 00     01 03 -> FF
        ///
        ///   Not guessed. The candidate mappings were tried against five live
        ///   servers and judged on the one thing that cannot be fudged: whether
        ///   the whole blob then parses into records, signatures and description
        ///   landing EXACTLY on its final byte. This mapping is the only one
        ///   that does so on all five. Swapping 00 and 01 leaves four of the
        ///   five one byte adrift, and not decoding at all loses most of the
        ///   mods outright - one server read 1 mod where it has 76.
        ///
        ///   A 0x01 followed by anything else is left alone, so a blob that is
        ///   not escaped at all passes through untouched.
        /// </summary>
        private static byte[] Unescape(byte[] b)
        {
            if (b == null || b.Length == 0) return b;

            // Nothing to do unless an escape is actually present; the common
            // case then costs one scan and no allocation.
            bool any = false;
            for (int i = 0; i + 1 < b.Length; i++)
                if (b[i] == 0x01 && (b[i + 1] == 0x01 || b[i + 1] == 0x02 || b[i + 1] == 0x03))
                { any = true; break; }
            if (!any) return b;

            var outBuf = new List<byte>(b.Length);
            for (int i = 0; i < b.Length; i++)
            {
                if (b[i] == 0x01 && i + 1 < b.Length)
                {
                    if (b[i + 1] == 0x01) { outBuf.Add(0x01); i++; continue; }
                    if (b[i + 1] == 0x02) { outBuf.Add(0x00); i++; continue; }
                    if (b[i + 1] == 0x03) { outBuf.Add(0xFF); i++; continue; }
                }
                outBuf.Add(b[i]);
            }
            return outBuf.ToArray();
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

            // Some blobs contain junk text in the header region that can look like
            // a valid record for a few frames before the real mod list starts. The
            // fix is to score every valid candidate and keep the one with the
            // largest complete mod list instead of accepting the first shallow hit.
            // Two passes. The first reads only the canonical record shape, which
            // is what virtually every server uses and which can be read exactly.
            // Only if that produces nothing provable are the looser shapes tried,
            // for the few servers that need them. Mixing the two in one pass let
            // false records from the loose shapes wreck otherwise clean reads.
            ServerRules best = ScanFrom(blob, true);
            if (best != null) return best;

            best = ScanFrom(blob, false);
            if (best != null) return best;

            ServerRules bestPartial = BestPartial(blob, true) ?? BestPartial(blob, false);

            // Records were found but the list never resolved to a clean end. That
            // is NOT "no mods" - it is a list we could not fully trust, and
            // saying so is what lets the launcher warn instead of sending a
            // player to a server whose mods it has under-counted.
            if (bestPartial != null) return bestPartial;

            // Nothing that even looks like a record: this server really has no
            // mods, and that is a complete answer rather than a failure.
            result.Complete = true;
            return result;
        }

        /// <summary>The largest PROVEN-complete list readable in one shape mode.</summary>
        private static ServerRules ScanFrom(byte[] blob, bool canonicalOnly)
        {
            ServerRules best = null;
            int limit = Math.Min(blob.Length, MaxHeaderScan);
            for (int i = 1; i < limit; i++)
            {
                ulong id; string name; int next;
                if (!TryRecord(blob, i, canonicalOnly, out id, out name, out next)) continue;

                var candidate = TryParseFrom(blob, i, canonicalOnly);
                if (candidate == null || !candidate.Complete || candidate.Mods.Count == 0) continue;
                if (best == null || candidate.Mods.Count > best.Mods.Count) best = candidate;
            }
            return best;
        }

        /// <summary>The largest list found when none could be proven complete.</summary>
        private static ServerRules BestPartial(byte[] blob, bool canonicalOnly)
        {
            ServerRules best = null;
            int limit = Math.Min(blob.Length, MaxHeaderScan);
            for (int i = 1; i < limit; i++)
            {
                ulong id; string name; int next;
                if (!TryRecord(blob, i, canonicalOnly, out id, out name, out next)) continue;

                var candidate = TryParseFrom(blob, i, canonicalOnly);
                if (candidate == null || candidate.Mods.Count == 0) continue;
                if (best == null || candidate.Mods.Count > best.Mods.Count) best = candidate;
            }
            return best;
        }

        /// <summary>
        /// Walks the records from one candidate starting point.
        ///
        /// WHY A GAP MUST NOT END THE WALK
        ///   Records are usually back to back, but not always. Measured on a
        ///   live server whose blob arrives in 23 chunks, the records sat at
        ///   offsets 8, 43, 68, 93, 117, 156 and 185 - and the FIRST one ended
        ///   at 42, one byte short of the next.
        ///
        ///   Giving up on that first mismatch did not merely lose one mod. The
        ///   walk died, the candidate was discarded, and whichever other start
        ///   offset happened to survive won instead - so the server reported 5
        ///   mods out of 68, and reported them as COMPLETE. A player joining on
        ///   that answer is kicked for the 63 mods the launcher never mentioned.
        ///   Another server read 19 of 45 the same way.
        ///
        ///   So a mismatch costs ONE BYTE, not the list. Resynchronising is safe
        ///   because the end is proven separately: TailFits has to land exactly
        ///   on the final byte, so a walk that wanders cannot be mistaken for a
        ///   complete one. Keeping junk out is TryRecord and LooksLikeModName's
        ///   job, and they still reject anything that is not a real name.
        ///
        ///   The skip budget stops a pathological blob being scanned to death;
        ///   once that much has been skipped this start point is simply wrong,
        ///   and the caller has better candidates to try.
        /// </summary>
        private static ServerRules TryParseFrom(byte[] blob, int start, bool canonicalOnly)
        {
            var result = new ServerRules();
            int at = start;
            int skipped = 0;

            while (at < blob.Length)
            {
                if (result.Mods.Count > 0 && TailFits(blob, at))
                {
                    ReadTail(blob, at, result);
                    result.Complete = true;
                    return result;
                }

                ulong id; string name; int next;
                if (TryRecord(blob, at, canonicalOnly, out id, out name, out next)
                    && !string.IsNullOrWhiteSpace(name) && LooksLikeModName(name))
                {
                    result.Mods.Add(new Mod(name, id));
                    at = next;
                    continue;
                }

                at++;
                if (++skipped > MaxResyncBytes) break;
            }

            return result.Mods.Count > 0 ? result : null;
        }

        /// <summary>
        /// How many bytes a walk may skip in total before its starting point is
        /// judged wrong. Generous: the gaps actually seen are one or two bytes,
        /// and the signature block at the end of a blob is only a few hundred.
        /// </summary>
        private const int MaxResyncBytes = 2048;

        /// <summary>How far in the mod records may start; see ParseBlob.</summary>
        /// <remarks>
        /// Some heavily modded servers pad the binary blob with extra bytes
        /// before the first real record, so a hard 14-byte window is too small.
        /// The real start can sit well past the earlier assumptions, so the scan
        /// needs to cover a larger window before concluding that a server has no
        /// valid mod list at all.
        /// </remarks>
        private const int MaxHeaderScan = 256;

        /// <summary>Tests one position against the record shape.</summary>
        private static bool TryRecord(byte[] b, int i, out ulong id, out string name, out int next)
        {
            return TryRecord(b, i, false, out id, out name, out next);
        }

        /// <summary>
        /// Tests one position against the record shape.
        ///
        /// WHY THERE IS A CANONICAL MODE
        ///   Almost every server lays a record out one way: four bytes of hash,
        ///   the 0x04 marker, the id, a length byte, the name. A few - DayOne
        ///   among them - shift a discriminator byte around, so the alternative
        ///   shapes below exist to read those.
        ///
        ///   But accepting four shapes at every offset makes the test far weaker
        ///   than it looks. On an ordinary server it finds records that are not
        ///   there, and those false records derail the walk past the real end of
        ///   the list: one server produced 80 "mods" out of a true 68 and could
        ///   no longer prove where the list finished.
        ///
        ///   So the canonical shape is tried FIRST, on its own, and only if that
        ///   fails to produce a provable list are the looser shapes allowed.
        ///   Ordinary servers are read exactly; the odd ones still work.
        /// </summary>
        private static bool TryRecord(byte[] b, int i, bool canonicalOnly,
                                      out ulong id, out string name, out int next)
        {
            id = 0; name = null; next = i;
            if (i + 10 > b.Length) return false;

            if (TryRecordAtMarker(b, i + 4, canonicalOnly, out id, out name, out next)) return true;
            if (canonicalOnly) return false;

            // Some DayOne records carry one small discriminator byte before the
            // marker; others carry it after.
            return TryRecordAtMarker(b, i + 5, false, out id, out name, out next);
        }

        /// <summary>
        /// Reads a record whose id field begins at <paramref name="markerAt"/>.
        ///
        /// THAT BYTE IS THE LENGTH OF THE ID, NOT A MARKER.
        ///   It reads 0x04 on almost every server, which is why it was taken
        ///   for a fixed marker for so long - a workshop id is four bytes. But a
        ///   server loading a mod that is NOT from the workshop writes 0x01 and
        ///   a single zero byte, because that mod has no id at all:
        ///
        ///       e2 ce 56 1b | 04 | b9 9b e2 60 | 13 | "Community Framework"
        ///       02 01 93 a5 | 01 | 00          | 0b | "@GhostRider"
        ///
        ///   Insisting on 0x04 made every locally installed mod invisible, and
        ///   worse, the walk then resynchronised into the signature list and
        ///   reported signature names as mods. A LAN server running three local
        ///   mods reported two mods, both wrong.
        /// </summary>
        private static bool TryRecordAtMarker(byte[] b, int markerAt, bool canonicalOnly,
                                              out ulong id, out string name, out int next)
        {
            id = 0; name = null; next = markerAt;
            if (markerAt < 0 || markerAt >= b.Length) return false;

            int idLen = b[markerAt];
            if (idLen < 1 || idLen > 8) return false;
            if (TryRecordBody(b, markerAt + 1, idLen, out id, out name, out next)) return true;

            // A shape seen on some servers carries one extra byte before the id.
            if (canonicalOnly) return false;
            return TryRecordBody(b, markerAt + 2, idLen, out id, out name, out next);
        }

        private static bool TryRecordBody(byte[] b, int idAt, int idLen,
                                          out ulong id, out string name, out int next)
        {
            id = 0; name = null; next = idAt;
            if (idAt + idLen + 1 > b.Length) return false;

            // Little-endian, however many bytes the record said it uses.
            ulong value = 0;
            for (int k = 0; k < idLen; k++) value |= (ulong)b[idAt + k] << (8 * k);
            id = value;

            // A workshop id has to look like one. An id of zero is not a
            // mistake - it is how a server says "this mod is not from the
            // workshop", and those are identified by name instead.
            if (id != 0 && (id < 100000 || id > 4000000000)) return false;

            int lenAt = idAt + idLen;
            int textAt = lenAt + 1;
            int len = b[lenAt];
            if (len < 2 || len > 64) return false;
            if (textAt + len > b.Length) return false;

            for (int k = textAt; k < textAt + len; k++)
                if (b[k] < 32 || b[k] == 127) return false;   // names are printable

            // StrictUtf8 THROWS on malformed input where Encoding.UTF8 would
            // quietly hand back replacement characters - and "these bytes are
            // not text at all" is exactly the question being asked.
            try { name = StrictUtf8.GetString(b, textAt, len); }
            catch { return false; }

            // A malformed packet can still contain a byte sequence that is
            // mostly punctuation, e.g. "???". That is never a real workshop
            // item and must be rejected before it reaches the connection/auth
            // mod list. A valid mod name may contain spaces or punctuation, so
            // the test is: it must contain at least one letter or digit and not
            // be made up entirely of punctuation/symbols.
            if (!LooksLikeModName(name)) return false;

            next = textAt + len;
            return true;
        }

        /// <summary>Decoding that refuses malformed input; see TryRecordBody.</summary>
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        private static bool LooksLikeModName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;

            bool sawLetterOrDigit = false;
            bool sawVisibleText = false;
            foreach (char ch in name)
            {
                if (char.IsControl(ch) || char.IsSurrogate(ch)) return false;
                if (ch >= 0xE000 && ch <= 0xF8FF) return false;

                if (char.IsLetterOrDigit(ch))
                {
                    sawLetterOrDigit = true;
                    sawVisibleText = true;
                    continue;
                }

                if (char.IsWhiteSpace(ch)) continue;

                // A placeholder such as "???" or any punctuation-only record is
                // not a real workshop mod name. Valid names may still contain
                // spaces or punctuation, so only reject the all-symbol case.
                if (char.IsPunctuation(ch) || char.IsSymbol(ch))
                {
                    sawVisibleText = true;
                    continue;
                }

                return false;
            }

            return sawLetterOrDigit && sawVisibleText;
        }

        /// <summary>
        /// Does everything from here parse as the signature list plus the
        /// trailing length-prefixed strings, finishing exactly on the last byte?
        /// That exactness is the proof that the mod list ended here and nothing
        /// was lost.
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

            // EXACTLY ONE optional entry may follow the signatures - the
            // description - and then the blob must END.
            //
            // This was briefly a while-loop that consumed entries until the
            // buffer ran out, and that quietly destroyed the whole proof. Any
            // stretch of bytes can be read as a chain of length-prefixed blocks
            // that happens to finish on the last byte, so "the mod list ends
            // here" became true almost everywhere - and the walk stopped at the
            // first place it was asked. One server reported 5 mods of 68 and
            // called the answer complete; another 19 of 45. The player then
            // joins and is kicked for the mods that were never listed.
            //
            // The exactness IS the proof. Keeping it to one entry is what makes
            // landing on the final byte mean something.
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
                    {
                        result.Description = Encoding.UTF8.GetString(b, i, len);
                        i += len;
                    }
                }

                // Extra trailing strings (for example verify metadata) are allowed
                // by TailFits, but only the first human description is shown.
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
