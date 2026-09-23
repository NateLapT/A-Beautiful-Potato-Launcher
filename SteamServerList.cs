// ---------------------------------------------------------------------------
//  The Steam master server list, via ISteamMatchmakingServers.
//
//  WHY NOT THE OLD UDP MASTER PROTOCOL
//    Because it is gone. hl2master.steampowered.com and every other master
//    hostname now return NXDOMAIN, and all the historical master IPs time out.
//    Valve retired it; the only supported route is through the Steam client.
//
//  WHY NOT THE STEAM WEB API
//    IGameServersService/GetServerList needs a publisher API key. Going through
//    the local Steam client needs nothing and is what the real DayZ launcher
//    does.
//
//  THE AWKWARD PART
//    RequestInternetServerList wants a C++ object implementing
//    ISteamMatchmakingServerListResponse. C# cannot produce one, so a small
//    block of memory is forged whose first field points at a vtable of three
//    function pointers. The callbacks deliberately do nothing - results are
//    polled with GetServerCount/GetServerDetails - so a mistake in them cannot
//    corrupt Steam's state.
//
//  LIFETIME RULES, LEARNED THE HARD WAY
//    Releasing a request while it is still refreshing crashes the process with
//    an AccessViolation. Always CancelQuery first, then ReleaseRequest, and only
//    then free the filter memory - Steam reads those buffers for the life of
//    the query.
//
//  SCALE
//    App 221100 (stable) returns Steam's hard cap of 10,000. App 1024020
//    (Experimental) returns ~138. Since the cap is real, text search is pushed
//    to Steam as a name_match filter rather than done over a truncated list.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace ABeautifulPotatoLauncher
{
    /// <summary>One row from the master list.</summary>
    internal sealed class BrowserServer
    {
        public string Name = "";
        public string Map = "";
        public string GameDir = "";
        public string Tags = "";
        public string Host = "";
        public int Port;
        public int QueryPort;
        public int Players;
        public int MaxPlayers;
        public int Ping;
        public uint AppId;
        public bool Password;
        public bool Secure;

        public string Endpoint { get { return Host + ":" + Port; } }

        // DayZ packs its server flags into the Steam tags field, e.g.
        // "battleye,no3rd,external,privHive,shard123ABC,lqs0,etm3.000000,
        //  entm64.000000,mod,isDLC,16:48"
        public bool HasMods     { get { return HasTag("mod"); } }

        /// <summary>
        /// How many players are waiting to get in, or 0 when nobody is.
        ///
        /// DayZ publishes this as an "lqs" entry in the Steam tags - lqs3 is
        /// three waiting. It is NOT in any of the places one would look first:
        /// the Bots byte reads 0 even on a server with a dozen queued, no rule
        /// carries it, and the player count never exceeds the maximum, so the
        /// usual "players minus slots" trick finds nothing. Measured across
        /// 4,998 servers, every one carried an lqs tag, and of those reporting
        /// both a live player count and a non-zero queue, every single one was
        /// exactly full.
        /// </summary>
        public int Queue { get { return TagNumber("lqs"); } }

        /// <summary>The number attached to a tag, e.g. "lqs12" -> 12.</summary>
        private int TagNumber(string prefix)
        {
            if (string.IsNullOrEmpty(Tags)) return 0;
            foreach (var part in Tags.Split(','))
            {
                string t = part.Trim();
                if (t.Length <= prefix.Length) continue;
                if (!t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                int v;
                if (int.TryParse(t.Substring(prefix.Length), out v) && v >= 0) return v;
            }
            return 0;
        }
        public bool BattlEye    { get { return HasTag("battleye"); } }
        public bool ThirdPerson { get { return !HasTag("no3rd"); } }
        public bool PrivateHive { get { return HasTag("privHive"); } }

        private bool HasTag(string t)
        {
            if (string.IsNullOrEmpty(Tags)) return false;
            foreach (var part in Tags.Split(','))
                if (part.Trim().Equals(t, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>In-game clock, published as a bare HH:MM tag.</summary>
        public string GameTime
        {
            get
            {
                if (string.IsNullOrEmpty(Tags)) return "";
                foreach (var part in Tags.Split(','))
                {
                    string p = part.Trim();
                    if (Regex.IsMatch(p, @"^\d{1,2}:\d{2}$")) return p;
                }
                return "";
            }
        }

        public int GameHour
        {
            get
            {
                string t = GameTime;
                int h;
                if (t.Length > 0 && int.TryParse(t.Split(':')[0], out h)) return h;
                return -1;
            }
        }

        public bool IsExperimental { get { return AppId == A2S.ExperimentalAppId; } }
        public string GameLabel
        {
            get
            {
                if (AppId == A2S.ExperimentalAppId) return "Experimental";
                if (AppId == A2S.StableAppId) return "Stable";
                return "";
            }
        }
    }

    /// <summary>
    /// Which of Steam's server lists to ask for. They all come back through the
    /// same response object and the same polling; only the request differs.
    /// </summary>
    internal enum ListKind { Internet, Recent, Friends, Lan, SteamFavourites }

    internal static class SteamServerList
    {
        // ------------------------------------------------------- interop -----

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string path);
        [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr h, string name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr AccessorFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr RequestFn(IntPtr self, uint appId, IntPtr filters, uint nFilters, IntPtr response);
        // RequestLANServerList takes no filters - Valve's odd one out.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr RequestLanFn(IntPtr self, uint appId, IntPtr response);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CountFn(IntPtr self, IntPtr h);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr DetailsFn(IntPtr self, IntPtr h, int i);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool RefreshingFn(IntPtr self, IntPtr h);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void HandleFn(IntPtr self, IntPtr h);

        // The three vtable slots of ISteamMatchmakingServerListResponse.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RespFn(IntPtr self, IntPtr h, int i);

        private static IntPtr _mm = IntPtr.Zero;
        private static RequestFn _request;
        private static RequestFn _requestRecent;
        private static RequestFn _requestFriends;
        private static RequestFn _requestFavourites;
        private static RequestLanFn _requestLan;
        private static CountFn _count;
        private static DetailsFn _details;
        private static RefreshingFn _refreshing;
        private static HandleFn _cancel, _release;

        // Rooted so the GC cannot collect the delegates the vtable points at.
        private static RespFn _onResponded, _onFailed, _onComplete;
        private static IntPtr _responseObj = IntPtr.Zero;

        private static IntPtr _active = IntPtr.Zero;
        private static IntPtr _filterArray = IntPtr.Zero;
        private static IntPtr[] _filterBlocks = new IntPtr[0];

        public static bool Available { get { return _mm != IntPtr.Zero && _request != null; } }

        /// <summary>
        /// Drops everything bound to the Steam session, so the next TryInit
        /// binds it again. Called when the session is being re-opened under a
        /// different app id (see SteamWorkshop.SwitchApp): the matchmaking
        /// interface pointer belongs to the old session and reading it
        /// afterwards is reading freed memory.
        ///
        /// The forged response object is deliberately NOT freed. Steam may
        /// still hold a pointer to it from a query that has not finished
        /// unwinding, and a few dozen bytes left behind is a far better bargain
        /// than a callback into memory that has been handed back.
        /// </summary>
        public static void Forget()
        {
            Stop();          // release the in-flight query while its interface is still valid

            _mm = IntPtr.Zero;
            _request = null;
            _requestRecent = null;
            _requestFriends = null;
            _requestFavourites = null;
            _requestLan = null;
            _count = null;
            _details = null;
            _refreshing = null;
            _cancel = null;
            _release = null;
        }

        /// <summary>
        /// Binds ISteamMatchmakingServers. SteamWorkshop.TryInit must have run
        /// first - it is what loads steam_api64.dll and calls SteamAPI_Init.
        /// </summary>
        public static bool TryInit(string gameDir, Action<string> log)
        {
            if (Available) return true;
            try
            {
                if (!SteamWorkshop.TryInit(gameDir, log)) return false;

                IntPtr lib = LoadLibrary(Path.Combine(gameDir, "steam_api64.dll"));
                if (lib == IntPtr.Zero) return false;

                Func<string, Type, object> bind = (n, t) =>
                {
                    IntPtr p = GetProcAddress(lib, n);
                    return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer(p, t);
                };

                var acc = (AccessorFn)bind("SteamAPI_SteamMatchmakingServers_v002", typeof(AccessorFn));
                if (acc == null) { log("Server browser: no ISteamMatchmakingServers accessor."); return false; }
                _mm = acc();
                if (_mm == IntPtr.Zero) { log("Server browser: accessor returned null."); return false; }

                _request    = (RequestFn)bind("SteamAPI_ISteamMatchmakingServers_RequestInternetServerList", typeof(RequestFn));
                _requestRecent = (RequestFn)bind("SteamAPI_ISteamMatchmakingServers_RequestHistoryServerList", typeof(RequestFn));
                _requestFriends = (RequestFn)bind("SteamAPI_ISteamMatchmakingServers_RequestFriendsServerList", typeof(RequestFn));
                _requestFavourites = (RequestFn)bind("SteamAPI_ISteamMatchmakingServers_RequestFavoritesServerList", typeof(RequestFn));
                _requestLan = (RequestLanFn)bind("SteamAPI_ISteamMatchmakingServers_RequestLANServerList", typeof(RequestLanFn));
                _count      = (CountFn)bind("SteamAPI_ISteamMatchmakingServers_GetServerCount", typeof(CountFn));
                _details    = (DetailsFn)bind("SteamAPI_ISteamMatchmakingServers_GetServerDetails", typeof(DetailsFn));
                _refreshing = (RefreshingFn)bind("SteamAPI_ISteamMatchmakingServers_IsRefreshing", typeof(RefreshingFn));
                _cancel     = (HandleFn)bind("SteamAPI_ISteamMatchmakingServers_CancelQuery", typeof(HandleFn));
                _release    = (HandleFn)bind("SteamAPI_ISteamMatchmakingServers_ReleaseRequest", typeof(HandleFn));

                if (_request == null || _count == null || _details == null) return false;

                // Forge the C++ response object: [ptr to vtable][3 function ptrs]
                _onResponded = (s, h, i) => { };
                _onFailed = (s, h, i) => { };
                _onComplete = (s, h, i) => { };

                IntPtr vt = Marshal.AllocHGlobal(IntPtr.Size * 3);
                Marshal.WriteIntPtr(vt, 0 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(_onResponded));
                Marshal.WriteIntPtr(vt, 1 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(_onFailed));
                Marshal.WriteIntPtr(vt, 2 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(_onComplete));
                _responseObj = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(_responseObj, vt);

                log("Server browser: connected to Steam.");
                return true;
            }
            catch (Exception ex)
            {
                log("Server browser unavailable: " + ex.Message);
                _mm = IntPtr.Zero;
                return false;
            }
        }

        // ------------------------------------------------------- querying ----

        /// <summary>
        /// Starts a master-list query. Only one runs at a time; starting a new
        /// one cancels the old. Poll <see cref="Poll"/> until it reports done.
        /// </summary>
        public static bool Start(uint appId, IEnumerable<KeyValuePair<string, string>> filters)
        {
            return Start(ListKind.Internet, appId, filters);
        }

        public static bool Start(ListKind kind, uint appId,
                                 IEnumerable<KeyValuePair<string, string>> filters)
        {
            if (!Available) return false;
            Stop();

            var list = new List<KeyValuePair<string, string>>(filters ?? new KeyValuePair<string, string>[0]);
            _filterBlocks = new IntPtr[list.Count];
            _filterArray = Marshal.AllocHGlobal(IntPtr.Size * Math.Max(1, list.Count));

            for (int i = 0; i < list.Count; i++)
            {
                // MatchMakingKeyValuePair_t is char key[256]; char value[256];
                IntPtr blk = Marshal.AllocHGlobal(512);
                for (int z = 0; z < 512; z++) Marshal.WriteByte(blk, z, 0);
                WriteAscii(blk, list[i].Key, 255);
                WriteAscii(blk + 256, list[i].Value, 255);
                _filterBlocks[i] = blk;
                Marshal.WriteIntPtr(_filterArray, i * IntPtr.Size, blk);
            }

            // Steam applies filters to the internet list only; the others are
            // already scoped to you, so they are filtered locally instead.
            switch (kind)
            {
                case ListKind.Lan:
                    _active = _requestLan != null
                            ? _requestLan(_mm, appId, _responseObj) : IntPtr.Zero;
                    break;
                case ListKind.Recent:
                    _active = Call(_requestRecent, appId, list.Count);
                    break;
                case ListKind.Friends:
                    _active = Call(_requestFriends, appId, list.Count);
                    break;
                case ListKind.SteamFavourites:
                    _active = Call(_requestFavourites, appId, list.Count);
                    break;
                default:
                    _active = Call(_request, appId, list.Count);
                    break;
            }

            if (_active == IntPtr.Zero) { FreeFilters(); return false; }
            return true;
        }

        private static IntPtr Call(RequestFn fn, uint appId, int filterCount)
        {
            if (fn == null) return IntPtr.Zero;
            return fn(_mm, appId,
                      filterCount > 0 ? _filterArray : IntPtr.Zero,
                      (uint)filterCount, _responseObj);
        }

        private static void WriteAscii(IntPtr dst, string s, int max)
        {
            var b = Encoding.ASCII.GetBytes(s ?? "");
            Marshal.Copy(b, 0, dst, Math.Min(b.Length, max));
        }

        /// <summary>
        /// Reads whatever Steam has gathered so far.
        /// </summary>
        /// <param name="done">true once the refresh has finished.</param>
        public static List<BrowserServer> Poll(out bool done, out int rawCount)
        {
            var results = new List<BrowserServer>();
            done = true;
            rawCount = 0;
            if (!Available || _active == IntPtr.Zero) return results;

            SteamWorkshop.RunCallbacks();

            int n = _count(_mm, _active);
            rawCount = n;
            done = !(_refreshing != null && _refreshing(_mm, _active));

            for (int i = 0; i < n; i++)
            {
                IntPtr p = _details(_mm, _active, i);
                if (p == IntPtr.Zero) continue;
                var s = Read(p);
                if (s != null) results.Add(s);
            }
            return results;
        }

        /// <summary>
        /// gameserveritem_t. Offsets below were confirmed against live data -
        /// app id, player counts, name, map and tags all read correctly.
        /// Fixed-width char arrays must be cut at the first NUL or the rest of
        /// the struct bleeds into the string.
        /// </summary>
        private static BrowserServer Read(IntPtr p)
        {
            try
            {
                uint ip = (uint)Marshal.ReadInt32(p, 4);
                var s = new BrowserServer
                {
                    Port       = (ushort)Marshal.ReadInt16(p, 0),
                    QueryPort  = (ushort)Marshal.ReadInt16(p, 2),
                    Host       = string.Format("{0}.{1}.{2}.{3}",
                                   (ip >> 24) & 0xFF, (ip >> 16) & 0xFF, (ip >> 8) & 0xFF, ip & 0xFF),
                    Ping       = Marshal.ReadInt32(p, 8),
                    GameDir    = Field(p, 14, 32),
                    Map        = Field(p, 46, 32),
                    AppId      = (uint)Marshal.ReadInt32(p, 144),
                    Players    = Marshal.ReadInt32(p, 148),
                    MaxPlayers = Marshal.ReadInt32(p, 152),
                    Password   = Marshal.ReadByte(p, 160) != 0,
                    Secure     = Marshal.ReadByte(p, 161) != 0,
                    Name       = Field(p, 172, 64),
                    Tags       = Field(p, 236, 128),
                };
                return s.MaxPlayers > 0 ? s : null;
            }
            catch { return null; }
        }

        private static string Field(IntPtr p, int offset, int max)
        {
            var bytes = new byte[max];
            Marshal.Copy(p + offset, bytes, 0, max);
            int n = Array.IndexOf(bytes, (byte)0);
            if (n < 0) n = max;
            return Encoding.UTF8.GetString(bytes, 0, n);
        }

        /// <summary>
        /// Cancels and releases the active query. Order matters: releasing a
        /// request that is still refreshing crashes the process, and the filter
        /// buffers must outlive the query Steam is reading them for.
        /// </summary>
        public static void Stop()
        {
            // _mm is checked as well as _active: these are calls THROUGH the
            // matchmaking interface, and making them once the session behind
            // it has gone is an access violation, not a caught exception.
            if (_active != IntPtr.Zero && _mm != IntPtr.Zero)
            {
                try { if (_cancel != null) _cancel(_mm, _active); } catch { }
                try { if (_release != null) _release(_mm, _active); } catch { }
                _active = IntPtr.Zero;
            }
            FreeFilters();
        }

        private static void FreeFilters()
        {
            try
            {
                foreach (var b in _filterBlocks) if (b != IntPtr.Zero) Marshal.FreeHGlobal(b);
                if (_filterArray != IntPtr.Zero) Marshal.FreeHGlobal(_filterArray);
            }
            catch { }
            _filterBlocks = new IntPtr[0];
            _filterArray = IntPtr.Zero;
        }
    }
}
