// ---------------------------------------------------------------------------
//  THE STEAM LINK: the launcher's Steam session, in a helper process.
//
//  WHY
//    To reach the Workshop the launcher has to open a Steam session AS DayZ
//    (221100), and Steam then counts DayZ as running for as long as the
//    process that opened it is alive - measured 2026-11-28: SteamAPI_Shutdown
//    and even unloading every Steam library left DayZ "running" until the
//    process exited. While DayZ is running, Steam won't delete a mod the
//    player has unsubscribed from, so Remove did nothing until the launcher
//    closed.
//
//    So the session lives in a small helper instead: this same exe, started
//    with --steam-link, no window. Ending the helper is Steam seeing DayZ
//    close - "unlink" - and the next thing that needs Steam starts a new one -
//    "relink". The launcher itself never opens a Steam session.
//
//  HOW
//    Only the native Steam calls cross over. In the launcher, SteamWorkshop's
//    and SteamServerList's function pointers are replaced by stand-ins that
//    send the call down a named pipe; in the helper, the same classes run as
//    they always have and answer. Every bit of the launcher's own logic and
//    caching stays where it was. The pipe is the current Windows user's only.
//
//    The helper ends itself when the launcher does (it watches the launcher's
//    process) or when the pipe closes, so it can never be left behind holding
//    DayZ "running".
//
//    If the helper can't be started, the launcher opens the session itself,
//    as before - everything works, and Remove leaves the deleting to Steam
//    when the launcher closes.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace ABeautifulPotatoLauncher
{
    internal static class SteamLink
    {
        public const string HostSwitch = "--steam-link";

        private static readonly object PipeLock = new object();
        private static Process _helper;
        private static NamedPipeClientStream _pipe;
        private static BinaryReader _in;
        private static BinaryWriter _out;

        /// <summary>A helper is up and connected.</summary>
        public static bool Running
        {
            get { lock (PipeLock) return _pipe != null && _pipe.IsConnected && _helper != null && !_helper.HasExited; }
        }

        /// <summary>Raised when the helper goes away unasked (crashed, killed).</summary>
        public static event Action Lost;

        // ------------------------------------------------------- launcher --

        /// <summary>Starts the helper if none is running. False when it can't be.</summary>
        public static bool EnsureStarted(Action<string> log)
        {
            lock (PipeLock)
            {
                if (_pipe != null && _pipe.IsConnected && _helper != null && !_helper.HasExited) return true;
                CloseLocked();

                string pipeName = "ABPL-steam-" + Guid.NewGuid().ToString("N");
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = typeof(SteamLink).Assembly.Location,       // this exe, always
                        Arguments = HostSwitch + " " + pipeName + " " + Process.GetCurrentProcess().Id,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    // The helper sets Steam's variables in its own environment;
                    // none should come from here.
                    psi.EnvironmentVariables.Remove("SteamAppId");
                    psi.EnvironmentVariables.Remove("SteamGameId");
                    _helper = Process.Start(psi);

                    var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
                    pipe.Connect(10000);
                    _pipe = pipe;
                    _in = new BinaryReader(pipe, Encoding.UTF8);
                    _out = new BinaryWriter(pipe, Encoding.UTF8);
                    return true;
                }
                catch (Exception ex)
                {
                    log("Steam link: the helper could not be started (" + ex.Message + ").");
                    CloseLocked();
                    return false;
                }
            }
        }

        /// <summary>
        /// Sends one call and returns its answer. Lines the helper logged on
        /// the way go to <paramref name="log"/>. Throws when the helper is gone
        /// - and the link is then dropped, so the next session starts afresh.
        /// </summary>
        public static BinaryReader Call(string op, Action<BinaryWriter> args, Action<string> log = null)
        {
            lock (PipeLock)
            {
                if (_pipe == null) throw new IOException("the Steam link is not running");
                try
                {
                    _out.Write(op);
                    if (args != null) args(_out);
                    _out.Flush();

                    bool ok = _in.ReadBoolean();
                    int lines = _in.ReadInt32();
                    for (int i = 0; i < lines; i++)
                    {
                        string line = _in.ReadString();
                        if (log != null) try { log(line); } catch { }
                    }
                    int n = _in.ReadInt32();
                    byte[] payload = _in.ReadBytes(n);
                    if (!ok) throw new InvalidOperationException(Encoding.UTF8.GetString(payload));
                    return new BinaryReader(new MemoryStream(payload), Encoding.UTF8);
                }
                catch (IOException)
                {
                    CloseLocked();
                    var lost = Lost;
                    if (lost != null) try { lost(); } catch { }
                    throw;
                }
            }
        }

        /// <summary>Ends the helper - Steam then sees the game close. Waits for it to go.</summary>
        public static void Stop()
        {
            lock (PipeLock)
            {
                if (_pipe != null)
                {
                    try
                    {
                        _out.Write("quit");
                        _out.Flush();
                        _in.ReadBoolean();      // it answers before it goes
                    }
                    catch { }
                }
                var helper = _helper;
                CloseLocked();
                try { if (helper != null && !helper.WaitForExit(5000)) helper.Kill(); } catch { }
            }
        }

        private static void CloseLocked()
        {
            try { if (_pipe != null) _pipe.Dispose(); } catch { }
            _pipe = null; _in = null; _out = null;
            if (_helper != null)
            {
                try { if (!_helper.HasExited && !_helper.WaitForExit(2000)) _helper.Kill(); } catch { }
                try { _helper.Dispose(); } catch { }
                _helper = null;
            }
        }

        // --------------------------------------------------------- helper --

        public static bool IsHost(string[] args)
        {
            return args != null && args.Length >= 4 && args[1] == HostSwitch;
        }

        /// <summary>
        /// The helper's whole life: one connection, one call at a time, until
        /// the launcher says quit, closes the pipe, or exits.
        /// </summary>
        public static void RunHost(string[] args)
        {
            string pipeName = args[2];
            int parentId;
            int.TryParse(args[3], out parentId);

            SteamWorkshop.Remote = false;            // this IS the session

            // The launcher gone, the helper goes - never left holding the game "running".
            try
            {
                var parent = Process.GetProcessById(parentId);
                new Thread(() =>
                {
                    try { parent.WaitForExit(); } catch { }
                    try { SteamWorkshop.Shutdown(); } catch { }
                    Environment.Exit(0);
                }) { IsBackground = true, Name = "watch-launcher" }.Start();
            }
            catch { return; }                        // no launcher: nothing to serve

            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User,
                                                      PipeAccessRights.FullControl, AccessControlType.Allow));
            using (var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                                                        PipeOptions.Asynchronous, 65536, 65536, security))
            {
                var connected = pipe.BeginWaitForConnection(null, null);
                if (!connected.AsyncWaitHandle.WaitOne(15000)) return;
                pipe.EndWaitForConnection(connected);

                var input = new BinaryReader(pipe, Encoding.UTF8);
                var output = new BinaryWriter(pipe, Encoding.UTF8);
                while (true)
                {
                    string op;
                    try { op = input.ReadString(); }
                    catch { break; }                 // the launcher closed the pipe

                    if (op == "quit")
                    {
                        try { SteamWorkshop.Shutdown(); } catch { }
                        try { output.Write(true); output.Flush(); } catch { }
                        break;
                    }

                    var lines = new List<string>();
                    Action<string> log = s => { lock (lines) lines.Add(s); };
                    var answer = new MemoryStream();
                    bool ok = true;
                    try
                    {
                        using (var w = new BinaryWriter(answer, Encoding.UTF8, true))
                        {
                            if (op.StartsWith("mm-")) SteamServerList.Serve(op, input, w, log);
                            else SteamWorkshop.Serve(op, input, w, log);
                        }
                    }
                    catch (Exception ex)
                    {
                        ok = false;
                        answer = new MemoryStream(Encoding.UTF8.GetBytes(ex.Message));
                    }

                    try
                    {
                        output.Write(ok);
                        lock (lines)
                        {
                            output.Write(lines.Count);
                            foreach (string l in lines) output.Write(l);
                        }
                        byte[] data = answer.ToArray();
                        output.Write(data.Length);
                        output.Write(data);
                        output.Flush();
                    }
                    catch { break; }
                }
            }
            try { SteamWorkshop.Shutdown(); } catch { }
        }
    }

    // ===================================================================
    //  SteamWorkshop over the link.
    // ===================================================================

    internal static partial class SteamWorkshop
    {
        /// <summary>
        /// True in the launcher: the session lives in the helper (see SteamLink).
        /// False in the helper itself, and in the launcher when the helper could
        /// not be started.
        /// </summary>
        internal static bool Remote = true;

        /// <summary>The game folder the session was last opened from - a new helper needs it.</summary>
        private static string _lastGameDir;

        private static bool _lostHooked;

        /// <summary>Opens the session in the helper and points every call at it.</summary>
        private static bool TryInitRemote(string gameDir, Action<string> log)
        {
            if (!string.IsNullOrEmpty(gameDir)) _lastGameDir = gameDir;
            if (!_lostHooked) { SteamLink.Lost += LinkLost; _lostHooked = true; }

            if (!SteamLink.EnsureStarted(log))
            {
                // Everything still works with the session in this process; only
                // Remove's unlinking does not. See SteamLink.
                Remote = false;
                log("Steam link: connecting to Steam directly instead.");
                return TryInit(gameDir, log);
            }

            uint app = _sessionApp;
            string dir = _lastGameDir ?? "";
            bool ok;
            try { ok = SteamLink.Call("init", w => { w.Write(app); w.Write(dir); }, log).ReadBoolean(); }
            catch (Exception ex) { log("Steam link: " + ex.Message); return false; }
            if (!ok) return false;

            _initialised = true;
            _ugc = new IntPtr(1);          // the real pointers live in the helper; these only say "open"
            _utils = new IntPtr(1);
            BindRemote();
            return true;
        }

        /// <summary>The helper went away unasked: forget the session, so the next use starts a new one.</summary>
        private static void LinkLost()
        {
            ForgetSession();
            SteamServerList.LinkLost();
        }

        /// <summary>Stand-ins for the native calls: each one is answered by the helper.</summary>
        private static void BindRemote()
        {
            _shutdown = () => SteamLink.Call("shutdown", null);
            _runCallbacks = () => SteamLink.Call("cb", null);
            _subscribe = (u, id) => SteamLink.Call("sub", w => w.Write(id)).ReadUInt64();
            _unsubscribe = (u, id) => SteamLink.Call("unsub", w => w.Write(id)).ReadUInt64();
            _getState = (u, id) => SteamLink.Call("state", w => w.Write(id)).ReadUInt32();
            _download = (u, id, high) => SteamLink.Call("dl", w => { w.Write(id); w.Write(high); }).ReadBoolean();
            _downloadInfo = (IntPtr u, ulong id, out ulong done, out ulong total) =>
            {
                var r = SteamLink.Call("dlinfo", w => w.Write(id));
                bool ok = r.ReadBoolean(); done = r.ReadUInt64(); total = r.ReadUInt64();
                return ok;
            };
            _installInfo = (IntPtr u, ulong id, out ulong size, StringBuilder folder, uint cap, out uint stamp) =>
            {
                var r = SteamLink.Call("instinfo", w => { w.Write(id); w.Write(cap); });
                bool ok = r.ReadBoolean(); size = r.ReadUInt64(); string f = r.ReadString(); stamp = r.ReadUInt32();
                folder.Clear();
                folder.Append(f);
                return ok;
            };
            _createDetails = (u, ids, count) => SteamLink.Call("qdetails", w =>
            {
                w.Write((int)count);
                for (int i = 0; i < count; i++) w.Write(ids[i]);
            }).ReadUInt64();
            _sendQuery = (u, h) => SteamLink.Call("qsend", w => w.Write(h)).ReadUInt64();
            _queryResult = (u, h, index, details) =>
            {
                var r = SteamLink.Call("qresult", w => { w.Write(h); w.Write(index); });
                bool ok = r.ReadBoolean();
                byte[] block = r.ReadBytes(r.ReadInt32());
                System.Runtime.InteropServices.Marshal.Copy(block, 0, details, Math.Min(block.Length, DetailsSize));
                return ok;
            };
            _releaseQuery = (u, h) => SteamLink.Call("qrelease", w => w.Write(h)).ReadBoolean();
            _callDone = (IntPtr u, ulong call, out bool failed) =>
            {
                var r = SteamLink.Call("calldone", w => w.Write(call));
                bool done = r.ReadBoolean(); failed = r.ReadBoolean();
                return done;
            };
            _createAll = (u, qt, mt, creator, consumer, page) => SteamLink.Call("qall", w =>
            {
                w.Write(qt); w.Write(mt); w.Write(creator); w.Write(consumer); w.Write(page);
            }).ReadUInt64();
            _setSearchText = (u, h, text) => SteamLink.Call("qtext", w => { w.Write(h); w.Write(text.Length); w.Write(text); }).ReadBoolean();
            _setLongDescription = (u, h, v) => SteamLink.Call("qlong", w => { w.Write(h); w.Write(v); }).ReadBoolean();
        }

        /// <summary>In the helper: answers one call with the real Steam function.</summary>
        internal static void Serve(string op, BinaryReader r, BinaryWriter w, Action<string> log)
        {
            lock (ApiLock)
            {
                switch (op)
                {
                    case "init":
                    {
                        uint app = r.ReadUInt32();
                        string dir = r.ReadString();
                        if (_initialised && _sessionApp != app) { try { _shutdown(); } catch { } ForgetSession(); }
                        _sessionApp = app;
                        w.Write(TryInit(dir, log));
                        return;
                    }
                    case "shutdown":
                        SteamServerList.Forget();
                        if (_initialised) { try { if (_shutdown != null) _shutdown(); } catch { } }
                        ForgetSession();
                        return;
                    case "cb": RunCallbacks(); return;
                    case "sub": w.Write(_subscribe(_ugc, r.ReadUInt64())); return;
                    case "unsub": w.Write(_unsubscribe(_ugc, r.ReadUInt64())); return;
                    case "state": w.Write(_getState(_ugc, r.ReadUInt64())); return;
                    case "dl": { ulong id = r.ReadUInt64(); w.Write(_download(_ugc, id, r.ReadBoolean())); return; }
                    case "dlinfo":
                    {
                        ulong done, total;
                        bool ok = _downloadInfo(_ugc, r.ReadUInt64(), out done, out total);
                        w.Write(ok); w.Write(done); w.Write(total);
                        return;
                    }
                    case "instinfo":
                    {
                        ulong id = r.ReadUInt64();
                        uint cap = r.ReadUInt32();
                        var sb = new StringBuilder((int)Math.Max(260, cap));
                        ulong size; uint stamp;
                        bool ok = _installInfo(_ugc, id, out size, sb, cap, out stamp);
                        w.Write(ok); w.Write(size); w.Write(sb.ToString()); w.Write(stamp);
                        return;
                    }
                    case "qdetails":
                    {
                        int n = r.ReadInt32();
                        var ids = new ulong[n];
                        for (int i = 0; i < n; i++) ids[i] = r.ReadUInt64();
                        w.Write(_createDetails(_ugc, ids, (uint)n));
                        return;
                    }
                    case "qsend": w.Write(_sendQuery(_ugc, r.ReadUInt64())); return;
                    case "qresult":
                    {
                        ulong h = r.ReadUInt64();
                        uint index = r.ReadUInt32();
                        IntPtr det = System.Runtime.InteropServices.Marshal.AllocHGlobal(DetailsSize);
                        try
                        {
                            var zero = new byte[DetailsSize];
                            System.Runtime.InteropServices.Marshal.Copy(zero, 0, det, DetailsSize);
                            bool ok = _queryResult(_ugc, h, index, det);
                            var block = new byte[DetailsSize];
                            System.Runtime.InteropServices.Marshal.Copy(det, block, 0, DetailsSize);
                            w.Write(ok); w.Write(block.Length); w.Write(block);
                        }
                        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(det); }
                        return;
                    }
                    case "qrelease": w.Write(_releaseQuery(_ugc, r.ReadUInt64())); return;
                    case "calldone":
                    {
                        bool failed;
                        bool done = _callDone(_utils, r.ReadUInt64(), out failed);
                        w.Write(done); w.Write(failed);
                        return;
                    }
                    case "qall":
                    {
                        int qt = r.ReadInt32(), mt = r.ReadInt32();
                        uint creator = r.ReadUInt32(), consumer = r.ReadUInt32(), page = r.ReadUInt32();
                        w.Write(_createAll(_ugc, qt, mt, creator, consumer, page));
                        return;
                    }
                    case "qtext":
                    {
                        ulong h = r.ReadUInt64();
                        byte[] text = r.ReadBytes(r.ReadInt32());
                        w.Write(_setSearchText(_ugc, h, text));
                        return;
                    }
                    case "qlong": { ulong h = r.ReadUInt64(); w.Write(_setLongDescription(_ugc, h, r.ReadBoolean())); return; }
                }
                throw new InvalidOperationException("unknown Steam link call: " + op);
            }
        }
    }

    // ===================================================================
    //  SteamServerList over the link: the query runs in the helper, the
    //  launcher asks for what it has found so far.
    // ===================================================================

    internal static partial class SteamServerList
    {
        private static bool _remoteReady;

        internal static void LinkLost() { _remoteReady = false; }

        private static bool RemoteTryInit(string gameDir, Action<string> log)
        {
            if (_remoteReady && SteamWorkshop.Available) return true;
            if (!SteamWorkshop.TryInit(gameDir, log)) return false;
            if (!SteamWorkshop.Remote) return TryInit(gameDir, log);      // fell back to a session here
            try { _remoteReady = SteamLink.Call("mm-init", w => w.Write(gameDir ?? ""), log).ReadBoolean(); }
            catch (Exception ex) { log("Server browser unavailable: " + ex.Message); _remoteReady = false; }
            return _remoteReady;
        }

        private static bool RemoteStart(ListKind kind, uint appId, IEnumerable<KeyValuePair<string, string>> filters)
        {
            if (!_remoteReady) return false;
            var list = new List<KeyValuePair<string, string>>(filters ?? new KeyValuePair<string, string>[0]);
            try
            {
                return SteamLink.Call("mm-start", w =>
                {
                    w.Write((int)kind); w.Write(appId); w.Write(list.Count);
                    foreach (var kv in list) { w.Write(kv.Key ?? ""); w.Write(kv.Value ?? ""); }
                }).ReadBoolean();
            }
            catch { return false; }
        }

        private static List<BrowserServer> RemotePoll(out bool done, out int rawCount)
        {
            done = true; rawCount = 0;
            var results = new List<BrowserServer>();
            if (!_remoteReady) return results;
            try
            {
                var r = SteamLink.Call("mm-poll", null);
                done = r.ReadBoolean();
                rawCount = r.ReadInt32();
                int n = r.ReadInt32();
                for (int i = 0; i < n; i++)
                    results.Add(new BrowserServer
                    {
                        Name = r.ReadString(), Map = r.ReadString(), GameDir = r.ReadString(), Tags = r.ReadString(),
                        Host = r.ReadString(), Port = r.ReadInt32(), QueryPort = r.ReadInt32(), Players = r.ReadInt32(),
                        MaxPlayers = r.ReadInt32(), Ping = r.ReadInt32(), AppId = r.ReadUInt32(),
                        Password = r.ReadBoolean(), Secure = r.ReadBoolean()
                    });
            }
            catch { done = true; }
            return results;
        }

        private static void RemoteStop()
        {
            if (!_remoteReady || !SteamLink.Running) return;
            try { SteamLink.Call("mm-stop", null); } catch { }
        }

        private static void RemoteForget()
        {
            if (_remoteReady && SteamLink.Running)
                try { SteamLink.Call("mm-forget", null); } catch { }
            _remoteReady = false;
        }

        /// <summary>In the helper: the real server browser.</summary>
        internal static void Serve(string op, BinaryReader r, BinaryWriter w, Action<string> log)
        {
            switch (op)
            {
                case "mm-init": w.Write(TryInit(r.ReadString(), log)); return;
                case "mm-start":
                {
                    var kind = (ListKind)r.ReadInt32();
                    uint appId = r.ReadUInt32();
                    int n = r.ReadInt32();
                    var filters = new List<KeyValuePair<string, string>>();
                    for (int i = 0; i < n; i++) filters.Add(new KeyValuePair<string, string>(r.ReadString(), r.ReadString()));
                    w.Write(Start(kind, appId, filters));
                    return;
                }
                case "mm-poll":
                {
                    bool done; int raw;
                    var list = Poll(out done, out raw);
                    w.Write(done); w.Write(raw); w.Write(list.Count);
                    foreach (var s in list)
                    {
                        w.Write(s.Name ?? ""); w.Write(s.Map ?? ""); w.Write(s.GameDir ?? ""); w.Write(s.Tags ?? "");
                        w.Write(s.Host ?? ""); w.Write(s.Port); w.Write(s.QueryPort); w.Write(s.Players);
                        w.Write(s.MaxPlayers); w.Write(s.Ping); w.Write(s.AppId);
                        w.Write(s.Password); w.Write(s.Secure);
                    }
                    return;
                }
                case "mm-stop": Stop(); return;
                case "mm-forget": Forget(); return;
            }
            throw new InvalidOperationException("unknown Steam link call: " + op);
        }
    }
}
