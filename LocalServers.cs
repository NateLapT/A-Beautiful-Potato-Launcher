// ---------------------------------------------------------------------------
//  DayZ servers running on THIS machine, and their real ports.
//
//  A query port cannot be worked out from a game port: it is a separate
//  setting (steamQueryPort in the server's cfg). Game port + 1 is only a
//  convention, and a server with no steamQueryPort set listens on 27015, or
//  27016 if 27015 is taken - both measured on -port=2402 with the setting
//  removed.
//
//  For a server on this machine there is no need to guess at all. Windows
//  knows which UDP ports each DayZServer process has open, so each of those
//  is asked for A2S info; the one that answers IS the query port, and its
//  reply carries the game port.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace ABeautifulPotatoLauncher
{
    internal sealed class LocalServer
    {
        /// <summary>127.0.0.1 for this PC, the LAN address for one found on the network.</summary>
        public string Host = "127.0.0.1";
        public int GamePort;
        public int QueryPort;
        public ServerInfo Info;
    }

    internal static class LocalServers
    {
        /// <summary>
        /// Where a DayZ server with no steamQueryPort set answers: 27015, or
        /// 27016 when 27015 was taken. Both measured on the same server, one
        /// restart apart - the second start found 27015 still held.
        /// </summary>
        public static readonly int[] DefaultQueryPorts = { 27015, 27016 };

        /// <summary>
        /// The query ports a DayZ server on a home network is all but certain
        /// to use, asked on every LAN refresh and in a network scan:
        ///   27015-27030   no steamQueryPort set, or a manually assigned
        ///                 query port in the common DayZ range
        ///   2303 ... 3003 steamQueryPort = game port + 1, for game ports
        ///                 2302, 2402, 2502 ... 3002 - one server per 100 ports,
        ///                 since each takes its game port, +1 and +2.
        /// </summary>
        public static readonly int[] CommonQueryPorts =
            {
                27015, 27016, 27017, 27018, 27019, 27020, 27021, 27022,
                27023, 27024, 27025, 27026, 27027, 27028, 27029, 27030,
                2303, 2403, 2503, 2603, 2703, 2803, 2903, 3003
            };

        /// <summary>CommonQueryPorts in words, for the player.</summary>
        public const string CommonPortsText = "27015-27030, and 2303, 2403 ... 3003 (game ports 2302, 2402 ... 3002)";

        private static readonly string[] ServerExes = { "DayZServer_x64", "DayZServer" };

        /// <summary>
        /// Every DayZ server running here that answers a query, by asking each
        /// UDP port its process has open. Empty when none is running.
        /// </summary>
        public static List<LocalServer> Find(int timeoutMs = 600)
        {
            var pids = new HashSet<int>();
            foreach (string exe in ServerExes)
            {
                try { foreach (var p in Process.GetProcessesByName(exe)) pids.Add(p.Id); }
                catch { }
            }
            if (pids.Count == 0) return new List<LocalServer>();

            var ports = UdpPortsOwnedBy(pids);
            var found = new System.Collections.Concurrent.ConcurrentBag<LocalServer>();
            Parallel.ForEach(ports, new ParallelOptions { MaxDegreeOfParallelism = 8 }, port =>
            {
                var info = A2S.GetInfoAt("127.0.0.1", port, timeoutMs);
                if (info == null || !info.Online) return;
                found.Add(new LocalServer
                {
                    QueryPort = port,
                    GamePort = info.GamePort > 0 ? info.GamePort : port - 1,
                    Info = info
                });
            });

            return found.GroupBy(s => s.GamePort).Select(g => g.First()).ToList();
        }

        /// <summary>
        /// A QUICK look for servers on this PC: every port a DayZ server
        /// process here holds (see Find), plus <paramref name="ports"/> - the
        /// common ones - for a server Windows does not show as DayZServer (a
        /// renamed exe, another game). All asked at once; a closed port on
        /// loopback is refused straight away, so this takes well under a second.
        /// </summary>
        public static List<LocalServer> FindQuick(int[] ports, int timeoutMs = 500)
        {
            var pids = new HashSet<int>();
            foreach (string exe in ServerExes)
            {
                try { foreach (var p in Process.GetProcessesByName(exe)) pids.Add(p.Id); }
                catch { }
            }

            var ask = new List<int>(ports ?? new int[0]);
            if (pids.Count > 0)
                foreach (int p in UdpPortsOwnedBy(pids))
                    if (!ask.Contains(p)) ask.Add(p);

            var found = new System.Collections.Concurrent.ConcurrentBag<LocalServer>();
            Parallel.ForEach(ask, new ParallelOptions { MaxDegreeOfParallelism = 16 }, port =>
            {
                var info = A2S.GetInfoAt("127.0.0.1", port, timeoutMs);
                if (info == null || !info.Online) return;
                found.Add(new LocalServer
                {
                    QueryPort = port,
                    GamePort = info.GamePort > 0 ? info.GamePort : port - 1,
                    Info = info
                });
            });

            return found.GroupBy(s => s.GamePort).Select(g => g.OrderBy(x => x.QueryPort).First()).ToList();
        }

        // ------------------------------------------------- network scan --

        /// <summary>Most addresses one scan asks: four /24 networks.</summary>
        private const int MaxScanHosts = 1024;

        /// <summary>
        /// The home networks this PC is on, as (first host, count) ranges -
        /// private addresses only (10.x, 172.16-31.x, 192.168.x), never the
        /// internet, and never 169.254.x, which is what an adapter with no
        /// network gives itself. A network wider than /24 is scanned as the
        /// /24 around this PC: home servers sit beside the PC that runs them.
        /// </summary>
        public static List<KeyValuePair<uint, int>> HomeNetworks(out List<string> described)
        {
            var ranges = new List<KeyValuePair<uint, int>>();
            described = new List<string>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up
                        || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback
                        || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                    foreach (var a in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (a.Address.AddressFamily != AddressFamily.InterNetwork || a.IPv4Mask == null) continue;
                        uint ip = ToUInt(a.Address);
                        if (!IsPrivate(ip)) continue;

                        uint mask = ToUInt(a.IPv4Mask);
                        if (mask < 0xFFFFFF00u) mask = 0xFFFFFF00u;          // wider than /24
                        if (mask >= 0xFFFFFFFEu) continue;                   // /31, /32: nothing beside it

                        uint net = ip & mask;
                        int count = (int)(~mask) - 1;                         // less network and broadcast
                        if (ranges.Any(r => r.Key == net + 1)) continue;
                        ranges.Add(new KeyValuePair<uint, int>(net + 1, count));

                        int prefix = 32;
                        for (uint m = ~mask; m != 0; m >>= 1) prefix--;
                        described.Add(FromUInt(net) + "/" + prefix);
                    }
                }
            }
            catch { }
            return ranges;
        }

        /// <summary>
        /// Every server on the home network answering on one of
        /// <paramref name="ports"/>. One A2S_INFO goes to each port of each
        /// address, all from ONE socket - an address with nothing on it simply
        /// never answers, so the whole network costs one short wait rather
        /// than a timeout per address. Whatever answers like a Steam query
        /// port is then asked properly. A few seconds for a /24.
        /// This PC's own addresses are left out; FindQuick covers them.
        /// </summary>
        public static List<LocalServer> ScanNetwork(int[] ports, out List<string> networks)
        {
            var ranges = HomeNetworks(out networks);
            var own = new HashSet<uint>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                    foreach (var a in ni.GetIPProperties().UnicastAddresses)
                        if (a.Address.AddressFamily == AddressFamily.InterNetwork) own.Add(ToUInt(a.Address));
            }
            catch { }

            var targets = new List<uint>();
            foreach (var r in ranges)
                for (int i = 0; i < r.Value && targets.Count < MaxScanHosts; i++)
                {
                    uint h = r.Key + (uint)i;
                    if (!own.Contains(h)) targets.Add(h);
                }

            var answered = new HashSet<KeyValuePair<uint, int>>();
            if (targets.Count == 0 || ports == null || ports.Length == 0) return new List<LocalServer>();

            using (var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                // See ScanForQueryPort: a refused port must not end the scan.
                try { sock.IOControl(unchecked((int)0x9800000C), new byte[] { 0, 0, 0, 0 }, null); }
                catch { }
                sock.Bind(new IPEndPoint(IPAddress.Any, 0));

                var buf = new byte[1400];
                Action drain = () =>
                {
                    while (sock.Available > 0)
                    {
                        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                        int n;
                        try { n = sock.ReceiveFrom(buf, ref from); }
                        catch { continue; }
                        if (n >= 5 && buf[0] == 0xFF && buf[1] == 0xFF && buf[2] == 0xFF && buf[3] == 0xFF)
                        {
                            var ep = (IPEndPoint)from;
                            answered.Add(new KeyValuePair<uint, int>(ToUInt(ep.Address), ep.Port));
                        }
                    }
                };

                int sent = 0;
                foreach (uint h in targets)
                {
                    var addr = FromUInt(h);
                    foreach (int port in ports)
                    {
                        try { sock.SendTo(InfoRequest, new IPEndPoint(addr, port)); }
                        catch { }
                    }
                    // A little pacing, so a router is not handed a few
                    // thousand packets in one instant.
                    if (++sent % 32 == 0) { drain(); System.Threading.Thread.Sleep(15); }
                }

                var until = DateTime.UtcNow.AddMilliseconds(1500);
                while (DateTime.UtcNow < until)
                {
                    drain();
                    System.Threading.Thread.Sleep(50);
                }
            }

            var found = new System.Collections.Concurrent.ConcurrentBag<LocalServer>();
            Parallel.ForEach(answered, new ParallelOptions { MaxDegreeOfParallelism = 16 }, hp =>
            {
                string host = FromUInt(hp.Key).ToString();
                var info = A2S.GetInfoAt(host, hp.Value, 1500);
                if (info == null || !info.Online) return;
                found.Add(new LocalServer
                {
                    Host = host,
                    QueryPort = hp.Value,
                    GamePort = info.GamePort > 0 ? info.GamePort : hp.Value - 1,
                    Info = info
                });
            });

            return found.GroupBy(s => s.Host + ":" + s.GamePort)
                        .Select(g => g.OrderBy(x => x.QueryPort).First()).ToList();
        }

        private static readonly byte[] InfoRequest =
            { 0xFF, 0xFF, 0xFF, 0xFF, 0x54, (byte)'S', (byte)'o', (byte)'u', (byte)'r', (byte)'c',
              (byte)'e', (byte)' ', (byte)'E', (byte)'n', (byte)'g', (byte)'i', (byte)'n', (byte)'e',
              (byte)' ', (byte)'Q', (byte)'u', (byte)'e', (byte)'r', (byte)'y', 0 };

        private static uint ToUInt(IPAddress a)
        {
            byte[] b = a.GetAddressBytes();
            return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        }

        private static IPAddress FromUInt(uint v)
        {
            return new IPAddress(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });
        }

        private static bool IsPrivate(uint ip)
        {
            return (ip >> 24) == 10
                || (ip >> 20) == 0xAC1                                    // 172.16.0.0/12
                || (ip >> 16) == 0xC0A8;                                  // 192.168.0.0/16
        }

        /// <summary>The query port of the local server on this game port, or 0.</summary>
        public static int QueryPortFor(int gamePort)
        {
            try
            {
                var s = Find().FirstOrDefault(x => x.GamePort == gamePort);
                return s == null ? 0 : s.QueryPort;
            }
            catch { return 0; }
        }

        /// <summary>Is a DayZ server process running on this PC at all?</summary>
        public static bool AnyRunning()
        {
            foreach (string exe in ServerExes)
            {
                try { if (Process.GetProcessesByName(exe).Length > 0) return true; }
                catch { }
            }
            return false;
        }

        /// <summary>
        /// The last resort, and only with the player's permission: every UDP
        /// port on 127.0.0.1 is sent one A2S_INFO, from ONE socket, and whatever
        /// answers like a Steam query port is then asked properly - only an
        /// answer naming this game port counts. A few seconds for all 64,511
        /// ports above 1024, because a closed port on loopback is refused at
        /// once. Returns the query port, or 0.
        /// </summary>
        public static int ScanForQueryPort(int gamePort)
        {
            var answered = new List<int>();
            using (var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                // Windows reports a refused port by failing the NEXT receive with
                // "connection reset"; switch that off, or one closed port ends
                // the scan. SIO_UDP_CONNRESET = 0x9800000C.
                try { sock.IOControl(unchecked((int)0x9800000C), new byte[] { 0, 0, 0, 0 }, null); }
                catch { }
                sock.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                sock.ReceiveTimeout = 400;

                byte[] info = { 0xFF, 0xFF, 0xFF, 0xFF, 0x54, (byte)'S', (byte)'o', (byte)'u', (byte)'r', (byte)'c',
                                (byte)'e', (byte)' ', (byte)'E', (byte)'n', (byte)'g', (byte)'i', (byte)'n', (byte)'e',
                                (byte)' ', (byte)'Q', (byte)'u', (byte)'e', (byte)'r', (byte)'y', 0 };
                int self = ((IPEndPoint)sock.LocalEndPoint).Port;

                var buf = new byte[1400];
                Action drain = () =>
                {
                    while (sock.Available > 0)
                    {
                        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                        int n;
                        try { n = sock.ReceiveFrom(buf, ref from); }
                        catch { continue; }
                        if (n >= 5 && buf[0] == 0xFF && buf[1] == 0xFF && buf[2] == 0xFF && buf[3] == 0xFF)
                        {
                            int p = ((IPEndPoint)from).Port;
                            if (!answered.Contains(p)) answered.Add(p);
                        }
                    }
                };

                for (int port = 1025; port <= 65535; port++)
                {
                    if (port == self) continue;
                    try { sock.SendTo(info, new IPEndPoint(IPAddress.Loopback, port)); }
                    catch { }
                    if ((port & 255) == 0) drain();
                }

                // Stragglers.
                var until = DateTime.UtcNow.AddMilliseconds(800);
                while (DateTime.UtcNow < until)
                {
                    drain();
                    System.Threading.Thread.Sleep(50);
                }
            }

            // Confirm with a real query: the right game port, not another server.
            foreach (int p in answered)
            {
                var i = A2S.GetInfoAt("127.0.0.1", p, 1000);
                if (i != null && i.Online && (i.GamePort == gamePort || (i.GamePort == 0 && answered.Count == 1)))
                    return p;
            }
            return 0;
        }

        /// <summary>
        /// Is this a server on the player's own network - this PC, or a
        /// private address (10.x, 172.16-31.x, 192.168.x)?
        /// </summary>
        public static bool IsHomeAddress(string host)
        {
            if (IsThisMachine(host)) return true;
            IPAddress a;
            return IPAddress.TryParse((host ?? "").Trim(), out a)
                && a.AddressFamily == AddressFamily.InterNetwork && IsPrivate(ToUInt(a));
        }

        /// <summary>Is this address one of this machine's own?</summary>
        public static bool IsThisMachine(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return false;
            host = host.Trim();
            if (host.StartsWith("127.", StringComparison.Ordinal)
                || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;

            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                    foreach (var a in ni.GetIPProperties().UnicastAddresses)
                        if (a.Address.AddressFamily == AddressFamily.InterNetwork
                            && a.Address.ToString() == host) return true;
            }
            catch { }
            return false;
        }

        // --------------------------------------------------- UDP port table --

        private const int AF_INET = 2;
        private const int UDP_TABLE_OWNER_PID = 1;

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool order,
                                                       int af, int tableClass, uint reserved);

        /// <summary>
        /// UDP ports (IPv4) held open by any of these processes. Rows are
        /// { localAddr, localPort, owningPid }, the port in network byte order.
        /// </summary>
        private static List<int> UdpPortsOwnedBy(HashSet<int> pids)
        {
            var ports = new List<int>();
            int size = 0;
            GetExtendedUdpTable(IntPtr.Zero, ref size, false, AF_INET, UDP_TABLE_OWNER_PID, 0);
            if (size <= 0) return ports;

            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedUdpTable(buf, ref size, false, AF_INET, UDP_TABLE_OWNER_PID, 0) != 0)
                    return ports;

                int rows = Marshal.ReadInt32(buf);
                IntPtr row = buf + 4;
                for (int i = 0; i < rows; i++, row += 12)
                {
                    int pid = Marshal.ReadInt32(row, 8);
                    if (!pids.Contains(pid)) continue;
                    int raw = Marshal.ReadInt32(row, 4);
                    int port = ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);
                    if (port > 0 && !ports.Contains(port)) ports.Add(port);
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return ports;
        }
    }
}
