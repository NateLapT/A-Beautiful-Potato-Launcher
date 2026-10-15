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
