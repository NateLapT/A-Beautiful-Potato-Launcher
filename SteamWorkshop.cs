// ---------------------------------------------------------------------------
//  Steam Workshop: check what is installed, subscribe to what is not, and wait.
//
//  WHY THIS TALKS TO steam_api64.dll DIRECTLY
//    Steamworks.NET would be the obvious choice, but every published version
//    targets netstandard2.1, which .NET Framework 4.8 cannot consume (it caps
//    at 2.0) - and none of them ship steam_api64.dll anyway, so it would also
//    mean redistributing Valve's binary.
//
//    DayZ already ships steam_api64.dll in its own folder, and it exports the
//    flat C API we need: SteamAPI_Init, SteamAPI_SteamUGC_v0xx,
//    SteamAPI_ISteamUGC_SubscribeItem / GetItemState / DownloadItem. So the
//    launcher loads THAT copy - already on the player's disk - and P/Invokes
//    it. Nothing is redistributed and the app stays a single net48 exe.
//
//  WHY SteamAppId IS SET TO 221100
//    Workshop content belongs to DayZ (221100), not to Experimental (1024020)
//    and certainly not to this launcher. SteamAPI_Init binds to whatever
//    SteamAppId says, so it has to claim DayZ's id for the subscription to
//    land in the right place. This is exactly why the official Experimental
//    launcher can only offer "load from library" and never "subscribe".
//
//  EVERY PATH IS OPTIONAL
//    If the DLL is missing, Steam is closed, or Valve changes the accessor
//    version, nothing here throws - TryInit just returns false and the caller
//    falls back to opening the Workshop page for the player to click.
//    Readiness is always judged from the FILESYSTEM, never from the API, so
//    the manual route reports progress just as accurately.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace BeautifulPotatoExpLauncher
{
    internal sealed class Mod
    {
        public readonly string Name;
        public ulong WorkshopId;

        public Mod(string name, ulong workshopId)
        {
            WorkshopId = NormalizeWorkshopId(workshopId);
            Name = SanitizeName(name, WorkshopId);
        }

        private static ulong NormalizeWorkshopId(ulong workshopId)
        {
            if (workshopId == 0) return 0;

            // DayZ sometimes emits a stray low-byte tag in the packed mod-record
            // that shifts the published workshop id by +2. The raw packet can
            // therefore contain values like 1797720066 instead of the real
            // published id 1797720064. Normalize only the unmistakable drift.
            if ((workshopId & 0xFF) == 0x02 && workshopId >= 100000)
            {
                ulong corrected = workshopId - 2;
                if (corrected >= 100000 && corrected <= 4000000000UL)
                    return corrected;
            }
            return workshopId;
        }

        private static string SanitizeName(string name, ulong workshopId)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "Workshop item " + workshopId;

            var sb = new StringBuilder();
            foreach (char ch in name)
            {
                if (char.IsControl(ch) || char.IsSurrogate(ch)) continue;
                if (ch >= 0xE000 && ch <= 0xF8FF) continue;
                if (char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch) ||
                    char.IsPunctuation(ch) || char.IsSymbol(ch))
                    sb.Append(ch);
            }

            string clean = sb.ToString().Trim();
            return clean.Length > 0 ? clean : "Workshop item " + workshopId;
        }

        public string DisplayName
        {
            get
            {
                // Prefer the label the server actually advertised for the required
                // mod. That is the authoritative answer for the current session,
                // even when the Steam page metadata is stale or points at a
                // different item. Only fall back to the workshop title when the
                // canonical server name is missing or generic.
                if (!string.IsNullOrWhiteSpace(Name) &&
                    !Name.StartsWith("Workshop item ", StringComparison.OrdinalIgnoreCase))
                    return Name;

                string title = SteamWorkshop.WorkshopTitle(WorkshopId);
                if (!string.IsNullOrWhiteSpace(title)) return title;
                if (!string.IsNullOrWhiteSpace(Name)) return Name;
                return "Workshop item " + WorkshopId;
            }
        }

        public override string ToString() { return DisplayName; }
    }

    [Flags]
    internal enum ItemState : uint
    {
        None = 0,
        Subscribed = 1,
        LegacyItem = 2,
        Installed = 4,
        NeedsUpdate = 8,
        Downloading = 16,
        DownloadPending = 32
    }

    internal static class SteamWorkshop
    {
        // Workshop content for DayZ. Experimental shares it - the files live
        // under the stable app's id no matter which build you run.
        public const uint DayZAppId = 221100;

        // ------------------------------------------------------ filesystem --

        public static string WorkshopRoot(string steamPath)
        {
            return Path.Combine(steamPath, "steamapps", "workshop", "content", DayZAppId.ToString());
        }

        private static IEnumerable<string> WorkshopRoots(string steamPath)
        {
            if (string.IsNullOrEmpty(steamPath)) yield break;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string std = WorkshopRoot(steamPath);
            if (seen.Add(std)) yield return std;

            string direct = Path.Combine(steamPath, "!Workshop");
            if (seen.Add(direct) && Directory.Exists(direct)) yield return direct;

            string common = Path.Combine(steamPath, "steamapps", "common");
            if (Directory.Exists(common))
            {
                foreach (var dir in Directory.EnumerateDirectories(common, "*", SearchOption.TopDirectoryOnly))
                {
                    string alt = Path.Combine(dir, "!Workshop");
                    if (seen.Add(alt) && Directory.Exists(alt)) yield return alt;
                }
            }
        }

        private static bool MatchesPublishedId(string folder, ulong id)
        {
            foreach (var file in new[] { "meta.cpp", "mod.cpp" })
            {
                string path = Path.Combine(folder, file);
                if (!File.Exists(path)) continue;

                try
                {
                    string text = File.ReadAllText(path);
                    foreach (Match match in Regex.Matches(text, @"(?:publishedid|publishedId|id)\s*[:=]\s*['""]?(\d{5,})", RegexOptions.IgnoreCase))
                    {
                        ulong found;
                        if (ulong.TryParse(match.Groups[1].Value, out found) && found == id)
                            return true;
                    }
                }
                catch { }
            }
            return false;
        }

        public static string ItemPath(string steamPath, ulong id)
        {
            foreach (var root in WorkshopRoots(steamPath))
            {
                if (!Directory.Exists(root)) continue;
                foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
                {
                    if (MatchesPublishedId(dir, id)) return dir;
                }
            }

            foreach (var root in WorkshopRoots(steamPath))
            {
                string idDir = Path.Combine(root, id.ToString());
                if (Directory.Exists(idDir)) return idDir;
            }

            return Path.Combine(WorkshopRoot(steamPath), id.ToString());
        }

        public static void CorrectInstalledIds(string steamPath, IEnumerable<Mod> mods)
        {
            if (string.IsNullOrEmpty(steamPath) || mods == null) return;

            foreach (var mod in mods)
            {
                if (mod == null) continue;

                ulong corrected = ResolveInstalledWorkshopId(steamPath, mod.Name, mod.WorkshopId);
                if (corrected != 0 && corrected != mod.WorkshopId)
                    mod.WorkshopId = corrected;
                else if ((mod.WorkshopId & 0xFF) == 0x02 && mod.WorkshopId >= 100000)
                {
                    ulong driftFix = mod.WorkshopId - 2;
                    if (driftFix >= 100000 && driftFix <= 4000000000UL)
                        mod.WorkshopId = driftFix;
                }
            }
        }

        private static ulong ResolveInstalledWorkshopId(string steamPath, string modName, ulong candidateId)
        {
            if (string.IsNullOrEmpty(steamPath)) return 0;

            // Prefer the installed folder whose published ID is known from the
            // mod's own meta.cpp / mod.cpp. This keeps the launcher anchored to
            // the actual DayZ item even when the server packet arrives with a
            // shifted or stale workshop id.
            foreach (var root in WorkshopRoots(steamPath))
            {
                if (!Directory.Exists(root)) continue;
                foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        ulong published = PublishedId(dir);
                        if (published == 0) continue;

                        string cleanDir = NormalizeName(Path.GetFileName(dir));
                        string cleanMod = NormalizeName(modName);
                        bool nameMatch = cleanDir.Length > 0 && cleanMod.Length > 0 &&
                            (cleanDir == cleanMod || cleanDir.Contains(cleanMod) || cleanMod.Contains(cleanDir));
                        bool idMatch = candidateId != 0 && published == candidateId;
                        if (published != 0 && (nameMatch || idMatch))
                            return published;
                    }
                    catch { }
                }
            }

            return 0;
        }

        private static ulong PublishedId(string folder)
        {
            foreach (var file in new[] { "meta.cpp", "mod.cpp" })
            {
                string path = Path.Combine(folder, file);
                if (!File.Exists(path)) continue;
                try
                {
                    string text = File.ReadAllText(path);
                    foreach (Match match in Regex.Matches(text, @"(?:publishedid|publishedId|id)\s*[:=]\s*['""]?(\d{5,})", RegexOptions.IgnoreCase))
                    {
                        ulong id;
                        if (ulong.TryParse(match.Groups[1].Value, out id)) return id;
                    }
                }
                catch { }
            }
            return 0;
        }

        private static string NormalizeName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder();
            foreach (char ch in s)
            {
                if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
                else if (ch == '@') continue;
            }
            return sb.ToString();
        }

        /// <summary>
        /// The authoritative readiness test: does the mod have a meta.cpp?
        ///
        /// WHY meta.cpp AND NOT "ARE THERE FILES"
        ///   meta.cpp is the manifest Steam writes when it finishes installing a
        ///   workshop item. It names the published id and the content version,
        ///   and it is the last thing to appear - so its presence means the
        ///   install completed, and its absence means it did not, however full
        ///   the folder looks.
        ///
        ///   Counting files does not work. One item on this machine holds 414
        ///   loose source files - paa, rvmat, p3d - under an Addons folder with
        ///   no pbo and no meta.cpp. An earlier version accepted "more than one
        ///   file is present", called it installed, launched the game, and the
        ///   server kicked the player. Looking for a .pbo instead was closer but
        ///   still indirect: it asks what a mod usually contains rather than
        ///   whether Steam says it finished. 834 of 867 installed items here
        ///   have a meta.cpp, and the 33 without it are the same 33 that have no
        ///   pbo - so this is no less strict, and it is strict about the right
        ///   thing.
        /// </summary>
        public static bool IsInstalled(string steamPath, ulong id)
        {
            try { return File.Exists(MetaPath(steamPath, id)); }
            catch { return false; }
        }

        /// <summary>The mod's own manifest, written by Steam on a completed install.</summary>
        public static string MetaPath(string steamPath, ulong id)
        {
            return Path.Combine(ItemPath(steamPath, id), "meta.cpp");
        }

        /// <summary>
        /// Present on disk but unusable: files are there, yet no meta.cpp, so
        /// the install never completed. Worth separating from "missing" because
        /// the cure differs - Steam already believes this one is installed, so
        /// it needs a forced re-download rather than a plain subscribe.
        /// </summary>
        public static bool IsBrokenInstall(string steamPath, ulong id)
        {
            try
            {
                string dir = ItemPath(steamPath, id);
                if (!Directory.Exists(dir)) return false;
                if (File.Exists(MetaPath(steamPath, id))) return false;
                return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any();
            }
            catch { return false; }
        }

        public static long SizeOnDisk(string steamPath, ulong id)
        {
            try
            {
                string dir = ItemPath(steamPath, id);
                if (!Directory.Exists(dir)) return 0;
                return new DirectoryInfo(dir)
                    .EnumerateFiles("*", SearchOption.AllDirectories)
                    .Sum(f => f.Length);
            }
            catch { return 0; }
        }

        // ------------------------------------------------------- native API --

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string path);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr module);

        // The flat API is __cdecl.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate bool InitFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ShutdownFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void RunCallbacksFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr UgcAccessorFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate ulong SubscribeFn(IntPtr ugc, ulong publishedFileId);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint GetItemStateFn(IntPtr ugc, ulong publishedFileId);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate bool DownloadItemFn(IntPtr ugc, ulong publishedFileId, bool highPriority);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate bool DownloadInfoFn(IntPtr ugc, ulong publishedFileId,
                                             out ulong bytesDownloaded, out ulong bytesTotal);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate bool InstallInfoFn(IntPtr ugc, ulong publishedFileId,
                                            out ulong sizeOnDisk, System.Text.StringBuilder folder,
                                            uint folderSize, out uint timeStamp);

        // The details query: ask Steam when each workshop item was last updated.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate ulong CreateDetailsQueryFn(IntPtr ugc, [In] ulong[] ids, uint count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate ulong SendQueryFn(IntPtr ugc, ulong handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate bool GetQueryResultFn(IntPtr ugc, ulong handle, uint index, IntPtr details);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate bool ReleaseQueryFn(IntPtr ugc, ulong handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate bool IsCallDoneFn(IntPtr utils, ulong call, out bool failed);

        private static IntPtr _lib = IntPtr.Zero;
        private static IntPtr _ugc = IntPtr.Zero;
        private static IntPtr _utils = IntPtr.Zero;
        private static bool _initialised;

        private static CreateDetailsQueryFn _createDetails;
        private static SendQueryFn _sendQuery;
        private static GetQueryResultFn _queryResult;
        private static ReleaseQueryFn _releaseQuery;
        private static IsCallDoneFn _callDone;

        private static ShutdownFn _shutdown;
        private static RunCallbacksFn _runCallbacks;
        private static SubscribeFn _subscribe;
        private static SubscribeFn _unsubscribe;
        private static GetItemStateFn _getState;
        private static DownloadItemFn _download;
        private static DownloadInfoFn _downloadInfo;
        private static InstallInfoFn _installInfo;

        public static bool Available { get { return _initialised && _ugc != IntPtr.Zero; } }

        /// <summary>
        /// Loads DayZ's own steam_api64.dll and initialises the Steam API as
        /// DayZ. Returns false for any reason at all - callers must treat the
        /// API as a bonus, never a requirement.
        /// </summary>
        public static bool TryInit(string gameDir, Action<string> log)
        {
            if (_initialised) return Available;

            try
            {
                string dll = Path.Combine(gameDir, "steam_api64.dll");
                if (!File.Exists(dll))
                {
                    log("Steam API: steam_api64.dll not found in the game folder.");
                    return false;
                }

                // SteamAPI_Init reads these, so they must be set BEFORE it runs.
                // 221100 is DayZ - the app that owns the workshop content.
                Environment.SetEnvironmentVariable("SteamAppId", DayZAppId.ToString());
                Environment.SetEnvironmentVariable("SteamGameId", DayZAppId.ToString());

                _lib = LoadLibrary(dll);
                if (_lib == IntPtr.Zero)
                {
                    log("Steam API: could not load steam_api64.dll.");
                    return false;
                }

                var init = Bind<InitFn>("SteamAPI_Init");
                if (init == null || !init())
                {
                    log("Steam API: SteamAPI_Init failed - is Steam running?");
                    return false;
                }

                _initialised = true;
                _shutdown = Bind<ShutdownFn>("SteamAPI_Shutdown");
                _runCallbacks = Bind<RunCallbacksFn>("SteamAPI_RunCallbacks");

                // The UGC accessor is version-suffixed and moves with the SDK, so
                // it is probed rather than hard-coded. DayZ ships v017 today.
                for (int v = 30; v >= 10; v--)
                {
                    var accessor = Bind<UgcAccessorFn>("SteamAPI_SteamUGC_v" + v.ToString("000"));
                    if (accessor == null) continue;
                    _ugc = accessor();
                    if (_ugc != IntPtr.Zero)
                    {
                        log("Steam API: connected (ISteamUGC v" + v.ToString("000") + ").");
                        break;
                    }
                }

                if (_ugc == IntPtr.Zero)
                {
                    log("Steam API: no usable ISteamUGC accessor.");
                    return false;
                }

                _subscribe = Bind<SubscribeFn>("SteamAPI_ISteamUGC_SubscribeItem");
            _unsubscribe = Bind<SubscribeFn>("SteamAPI_ISteamUGC_UnsubscribeItem");
                _getState  = Bind<GetItemStateFn>("SteamAPI_ISteamUGC_GetItemState");
                _download  = Bind<DownloadItemFn>("SteamAPI_ISteamUGC_DownloadItem");
            _downloadInfo = Bind<DownloadInfoFn>("SteamAPI_ISteamUGC_GetItemDownloadInfo");
            _installInfo = Bind<InstallInfoFn>("SteamAPI_ISteamUGC_GetItemInstallInfo");

                _createDetails = Bind<CreateDetailsQueryFn>("SteamAPI_ISteamUGC_CreateQueryUGCDetailsRequest");
                _sendQuery     = Bind<SendQueryFn>("SteamAPI_ISteamUGC_SendQueryUGCRequest");
                _queryResult   = Bind<GetQueryResultFn>("SteamAPI_ISteamUGC_GetQueryUGCResult");
                _releaseQuery  = Bind<ReleaseQueryFn>("SteamAPI_ISteamUGC_ReleaseQueryUGCRequest");
                _callDone      = Bind<IsCallDoneFn>("SteamAPI_ISteamUtils_IsAPICallCompleted");

                // The utils accessor is version-suffixed too, and it carries the
                // "has this async call finished" test the details query needs.
                for (int v = 30; v >= 5; v--)
                {
                    var ua = Bind<UgcAccessorFn>("SteamAPI_SteamUtils_v" + v.ToString("000"));
                    if (ua == null) continue;
                    _utils = ua();
                    if (_utils != IntPtr.Zero) break;
                }

                return _subscribe != null && _getState != null;
            }
            catch (Exception ex)
            {
                log("Steam API unavailable: " + ex.Message);
                return false;
            }
        }

        private static T Bind<T>(string export) where T : class
        {
            IntPtr p = GetProcAddress(_lib, export);
            if (p == IntPtr.Zero) return null;
            return Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
        }

        public static void RunCallbacks()
        {
            try { if (_runCallbacks != null) _runCallbacks(); } catch { }
        }

        /// <summary>Subscribes and asks Steam to start downloading now.</summary>
        public static bool Subscribe(ulong id)
        {
            if (!Available) return false;
            try
            {
                _subscribe(_ugc, id);
                if (_download != null) _download(_ugc, id, true);   // true = high priority
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Real download progress straight from Steam.
        ///
        /// This matters more than it looks: Steam downloads into
        /// steamapps\workshop\downloads and only MOVES the finished item into
        /// \content, so watching the content folder grow shows nothing at all
        /// and then a sudden jump. Only Steam knows how far along it is.
        ///
        /// Returns false when no download is running, which is also the answer
        /// for "queued but not started yet".
        /// </summary>
        public static bool TryGetProgress(ulong id, out long done, out long total)
        {
            done = 0; total = 0;
            if (!Available || _downloadInfo == null) return false;
            try
            {
                ulong d, t;
                if (!_downloadInfo(_ugc, id, out d, out t)) return false;
                done = (long)d;
                total = (long)t;
                return total > 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// True while Steam still has work queued for this item. Readiness has
        /// to consider this as well as the filesystem, because the content
        /// folder can hold a previous version while an update downloads.
        /// </summary>
        public static bool IsBusy(ulong id)
        {
            var st = GetState(id);
            return st.HasFlag(ItemState.Downloading) || st.HasFlag(ItemState.DownloadPending);
        }

        /// <summary>
        /// Whether Steam's OWN flag says the installed copy is out of date.
        /// One of the two signals NeedsUpdate uses; callers want that instead.
        /// </summary>
        public static bool SteamSaysOutOfDate(ulong id)
        {
            var st = GetState(id);
            return st.HasFlag(ItemState.Installed) && st.HasFlag(ItemState.NeedsUpdate);
        }

        // ------------------------------------------ workshop update times --

        private const int DetailsSize = 16384;      // SteamUGCDetails_t is ~9776
        private const int OffsetPublishedId = 0;
        private const int OffsetTimeUpdated = 8172;
        private const int DetailsPerQuery = 50;     // Steam's page size

        // Asking Steam is a network round trip, so answers are kept for the
        // session - a launch checks the same mods several times over.
        private static readonly Dictionary<ulong, DateTime> _workshopUpdated
            = new Dictionary<ulong, DateTime>();

        /// <summary>
        /// Asks Steam when each of these workshop items was last updated and
        /// remembers the answers. Call it once before checking a server's mod
        /// list; NeedsUpdate then costs nothing.
        ///
        /// WHERE m_rtimeUpdated LIVES
        ///   SteamUGCDetails_t is a large fixed struct with a 129-byte title and
        ///   an 8000-byte description buried in the middle, so its field offsets
        ///   are easy to get wrong by a few bytes and impossible to notice when
        ///   you do - a neighbouring field still reads as a plausible number.
        ///   Offset 8172 was not computed, it was found: the whole struct was
        ///   scanned for values that decode as sensible dates, across items
        ///   whose real update times were already known. 8168 turned out to be
        ///   m_rtimeCreated and 8172 m_rtimeUpdated, agreeing on every item.
        /// </summary>
        public static void PrefetchWorkshopTimes(IEnumerable<ulong> ids, Action<string> log)
        {
            if (!Available || _createDetails == null || _sendQuery == null
                || _queryResult == null || _callDone == null || _utils == IntPtr.Zero) return;

            var wanted = new List<ulong>();
            lock (_workshopUpdated)
            {
                foreach (ulong id in ids)
                    if (id != 0 && !_workshopUpdated.ContainsKey(id) && !wanted.Contains(id))
                        wanted.Add(id);
            }
            if (wanted.Count == 0) return;

            IntPtr det = Marshal.AllocHGlobal(DetailsSize);
            try
            {
                for (int start = 0; start < wanted.Count; start += DetailsPerQuery)
                {
                    int n = Math.Min(DetailsPerQuery, wanted.Count - start);
                    var batch = new ulong[n];
                    wanted.CopyTo(start, batch, 0, n);

                    ulong handle;
                    try { handle = _createDetails(_ugc, batch, (uint)n); }
                    catch { return; }
                    if (handle == 0 || handle == ulong.MaxValue) return;

                    try
                    {
                        ulong call = _sendQuery(_ugc, handle);
                        if (call == 0) continue;

                        // Steam answers asynchronously, so callbacks are pumped
                        // until it does. Ten seconds is generous; it normally
                        // takes under one.
                        bool failed = true, done = false;
                        for (int i = 0; i < 100 && !done; i++)
                        {
                            if (_runCallbacks != null) _runCallbacks();
                            System.Threading.Thread.Sleep(100);
                            done = _callDone(_utils, call, out failed);
                        }
                        if (!done || failed)
                        {
                            if (log != null) log("Steam API: the update-time query did not answer.");
                            continue;
                        }

                        for (uint i = 0; i < n; i++)
                        {
                            for (int z = 0; z < DetailsSize; z++) Marshal.WriteByte(det, z, 0);
                            if (!_queryResult(_ugc, handle, i, det)) continue;

                            ulong pid = (ulong)Marshal.ReadInt64(det, OffsetPublishedId);
                            uint updated = (uint)Marshal.ReadInt32(det, OffsetTimeUpdated);
                            if (pid == 0 || updated == 0) continue;

                            lock (_workshopUpdated)
                                _workshopUpdated[pid] = UnixEpoch.AddSeconds(updated);
                        }
                    }
                    finally
                    {
                        try { if (_releaseQuery != null) _releaseQuery(_ugc, handle); }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                if (log != null) log("Steam API: the update-time query failed - " + ex.Message);
            }
            finally { Marshal.FreeHGlobal(det); }
        }

        private static readonly DateTime UnixEpoch =
            new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static readonly Dictionary<ulong, string> _workshopTitles =
            new Dictionary<ulong, string>();

        /// <summary>
        /// The workshop item's public title, when Steam can resolve it.
        /// Some servers publish arbitrary folder names, so this is a safer label
        /// than the raw A2S mod name, while still falling back to the server's
        /// own text when Steam is offline or the item is unavailable.
        /// </summary>
        private static string WorkshopDetailsUrl(ulong id, bool english = true)
        {
            string url = "https://steamcommunity.com/sharedfiles/filedetails/?id=" + id
                       + "&appid=" + DayZAppId;
            if (english) url += "&l=english";
            return url;
        }

        public static string WorkshopTitle(ulong id)
        {
            if (id == 0) return "";

            lock (_workshopTitles)
            {
                string cached;
                if (_workshopTitles.TryGetValue(id, out cached)) return cached;
            }

            string title = "";
            try
            {
                using (var client = new WebClient())
                {
                    client.Headers[HttpRequestHeader.UserAgent] =
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                        "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";

                    string html = client.DownloadString(WorkshopDetailsUrl(id));
                    title = ParseWorkshopTitle(html);
                }
            }
            catch { }

            if (!string.IsNullOrWhiteSpace(title))
            {
                lock (_workshopTitles)
                {
                    if (!_workshopTitles.ContainsKey(id)) _workshopTitles[id] = title;
                }
            }

            return title;
        }

        private static string ParseWorkshopTitle(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return "";

            foreach (Match match in Regex.Matches(html,
                      "<meta\\s+(?:property|name)\\s*=\\s*\"(?:og:title|twitter:title)\"\\s+content=\"([^\"]+)\"",
                      RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                string title = WebUtility.HtmlDecode(match.Groups[1].Value).Trim();
                if (!string.IsNullOrWhiteSpace(title) &&
                    !title.Contains("Steam Community") &&
                    !title.Contains("Error"))
                    return title;
            }

            Match plainTitle = Regex.Match(html, "<title>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (plainTitle.Success)
            {
                string title = WebUtility.HtmlDecode(plainTitle.Groups[1].Value).Trim();
                if (!string.IsNullOrWhiteSpace(title) &&
                    !title.Contains("Steam Community") &&
                    !title.Contains("Error"))
                    return title;
            }

            return "";
        }

        /// <summary>
        /// When Steam says this item was last updated, or DateTime.MinValue when
        /// it has not been asked. PrefetchWorkshopTimes fills this in.
        /// </summary>
        public static DateTime WorkshopUpdated(ulong id)
        {
            lock (_workshopUpdated)
            {
                DateTime t;
                return _workshopUpdated.TryGetValue(id, out t) ? t : DateTime.MinValue;
            }
        }

        /// <summary>See StaleBy for why this is a day and not a second.</summary>
        public static readonly TimeSpan StaleTolerance = TimeSpan.FromDays(1);

        /// <summary>
        /// When Steam last WROTE this mod's meta.cpp - which is when it last
        /// installed or updated the item. Different from the timestamp inside
        /// the file; see StaleBy for why that difference matters.
        /// DateTime.MinValue when there is no meta.cpp.
        /// </summary>
        public static DateTime InstalledAt(string steamPath, ulong id)
        {
            try
            {
                string meta = MetaPath(steamPath, id);
                if (!File.Exists(meta)) return DateTime.MinValue;
                return File.GetLastWriteTimeUtc(meta);
            }
            catch { return DateTime.MinValue; }
        }

        /// <summary>
        /// How far the local copy lags the published one, or TimeSpan.Zero when
        /// it does not lag or cannot be judged.
        ///
        /// TWO SIGNALS MUST AGREE, AND HERE IS WHY
        ///   The obvious test - compare the timestamp inside meta.cpp against
        ///   Steam's m_rtimeUpdated - is wrong on its own, and measurably so.
        ///   Across all 835 installed mods it agreed with Steam to within a day
        ///   for 825 of them, which looks excellent, but SEVEN of the ten
        ///   disagreements were mods that were perfectly up to date.
        ///
        ///   The reason is that the two numbers do not measure the same event.
        ///   m_rtimeUpdated moves whenever the workshop ITEM changes - including
        ///   a description or tag edit that touches no files at all - while the
        ///   timestamp inside meta.cpp records when the author built the
        ///   CONTENT. Mod 1590841260 (Trader) shows it plainly: Steam last
        ///   updated the item on 2024-10-16, the file says 2024-01-27, and the
        ///   local copy was current the whole time.
        ///
        ///   What settles it is when Steam last WROTE meta.cpp, because that is
        ///   when this machine actually received the item. For that same mod the
        ///   file was written 2024-10-18 - two days AFTER the last workshop
        ///   update - so it cannot possibly be stale.
        ///
        ///   File mtime is not used alone either: on its own it drifts, matching
        ///   Steam within a day for only 250 of the 835, because revalidating or
        ///   recopying an item rewrites the file long after the content changed.
        ///   That drift is always in the safe direction - the file looks NEWER -
        ///   so it is reliable for proving a copy is current, and unreliable for
        ///   proving one is old.
        ///
        ///   So a mod is called outdated only when BOTH say so. On this machine
        ///   that is 2 mods rather than 9, and both of the 2 are genuinely
        ///   behind - 46 and 50 days.
        ///
        /// WHY THE TOLERANCE IS A WHOLE DAY
        ///   The clocks do not agree exactly. A copy that IS current reads about
        ///   5 seconds out at the median and 42 at the 90th percentile, with a
        ///   thin tail to a few hours. A day sits well clear of that and well
        ///   clear of the real lags, which start at 46 days.
        /// </summary>
        public static TimeSpan StaleBy(string steamPath, ulong id)
        {
            DateTime published = WorkshopUpdated(id);
            if (published == DateTime.MinValue) return TimeSpan.Zero;
            if (steamPath == null) return TimeSpan.Zero;

            // Signal 1: when this machine received the item.
            DateTime installed = InstalledAt(steamPath, id);
            if (installed == DateTime.MinValue) return TimeSpan.Zero;
            TimeSpan installedLag = published - installed;
            if (installedLag <= StaleTolerance) return TimeSpan.Zero;   // copy is newer: current

            // Signal 2: the content timestamp the author stamped into meta.cpp.
            DateTime built = LocalPublishTime(steamPath, id);
            if (built == DateTime.MinValue) return TimeSpan.Zero;
            if (published - built <= StaleTolerance) return TimeSpan.Zero;

            // Both agree. Report the lag of the INSTALL, which is the honest
            // answer to "how far behind is the copy I actually have".
            return installedLag;
        }

        /// <summary>
        /// Whether the installed copy needs updating, judged two ways: Steam's
        /// own NeedsUpdate flag, and the timestamp inside the mod's meta.cpp
        /// against the publication time Steam reports for the workshop item.
        ///
        /// Both are needed. Steam's flag is immediate but weak - it reported
        /// false for every one of the mods measured as genuinely behind, which
        /// is how a player reached the Emergence server with outdated mods and
        /// was kicked. The timestamps are ground truth about what is on disk,
        /// but only after PrefetchWorkshopTimes has run and only past the
        /// tolerance StaleBy explains, so the flag still covers the one case
        /// they cannot see: a mod published within the last day.
        /// </summary>
        public static bool NeedsUpdate(string steamPath, ulong id)
        {
            if (SteamSaysOutOfDate(id)) return true;
            return steamPath != null && StaleBy(steamPath, id) > TimeSpan.Zero;
        }

        /// <summary>Re-download an item, whether or not Steam thinks it needs it.</summary>
        public static bool ForceDownload(ulong id)
        {
            if (!Available || _download == null) return false;
            try { return _download(_ugc, id, true); }
            catch { return false; }
        }

        /// <summary>Unsubscribe, so Steam removes the item from disk.</summary>
        public static bool Unsubscribe(ulong id)
        {
            if (!Available || _unsubscribe == null) return false;
            try { _unsubscribe(_ugc, id); return true; }
            catch { return false; }
        }

        /// <summary>
        /// The raw publication timestamp recorded in the mod's own meta.cpp.
        /// Zero when unknown. See LocalPublishTime for what the number means.
        /// </summary>
        public static ulong MetaTimestamp(string steamPath, ulong id)
        {
            try
            {
                string meta = MetaPath(steamPath, id);
                if (!File.Exists(meta)) return 0;
                foreach (string line in File.ReadAllLines(meta))
                {
                    string t = line.Trim();
                    if (!t.StartsWith("timestamp", StringComparison.OrdinalIgnoreCase)) continue;
                    int eq = t.IndexOf('=');
                    if (eq < 0) continue;
                    string v = t.Substring(eq + 1).Trim().TrimEnd(';').Trim();
                    ulong ts;
                    if (ulong.TryParse(v, out ts)) return ts;
                }
            }
            catch { }
            return 0;
        }

        // meta.cpp counts in 100-nanosecond ticks, but not from an epoch anyone
        // documents, and two different epochs are in the wild. Both constants
        // below were measured, not looked up: every installed item's meta.cpp
        // timestamp was compared against the m_rtimeUpdated Steam reports for
        // the same workshop id, across 834 mods.
        //
        //   830 of them use the large epoch. Against the constant below the
        //   median disagreement is 5 seconds and the 90th percentile is 39.
        //
        //   4 use .NET's own ticks-since-year-1, the value DateTime.Ticks gives.
        //
        // The wobble is real but tiny, and it is why StaleBy below needs a
        // tolerance rather than an exact comparison.
        private const ulong TicksLargeEpoch = 5233041986331080000UL;
        private const ulong TicksYear1AtUnixEpoch = 621355968000000000UL;
        private const ulong EpochSplit = 2000000000000000000UL;

        /// <summary>
        /// When the installed content was published, decoded from meta.cpp.
        /// DateTime.MinValue when the mod has no meta.cpp or an unreadable one.
        /// </summary>
        public static DateTime LocalPublishTime(string steamPath, ulong id)
        {
            ulong ts = MetaTimestamp(steamPath, id);
            if (ts == 0) return DateTime.MinValue;

            ulong epoch = ts < EpochSplit ? TicksYear1AtUnixEpoch : TicksLargeEpoch;
            if (ts <= epoch) return DateTime.MinValue;

            double seconds = (ts - epoch) / 1e7;
            if (seconds > 4102444800.0) return DateTime.MinValue;    // past year 2100: not a time
            try
            {
                return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds);
            }
            catch { return DateTime.MinValue; }
        }

        /// <summary>
        /// Where Steam put the item, how big it is, and when the content it
        /// installed was published (UTC seconds). False if it is not installed.
        /// </summary>
        public static bool TryGetInstallInfo(ulong id, out string folder, out long size, out DateTime updated)
        {
            folder = null; size = 0; updated = DateTime.MinValue;
            if (!Available || _installInfo == null) return false;
            try
            {
                var sb = new System.Text.StringBuilder(1024);
                ulong bytes;
                uint stamp;
                if (!_installInfo(_ugc, id, out bytes, sb, (uint)sb.Capacity, out stamp)) return false;
                folder = sb.ToString();
                size = (long)bytes;
                if (stamp > 0)
                    updated = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                              .AddSeconds(stamp).ToLocalTime();
                return true;
            }
            catch { return false; }
        }

        public static ItemState GetState(ulong id)
        {
            if (!Available) return ItemState.None;
            try { return (ItemState)_getState(_ugc, id); }
            catch { return ItemState.None; }
        }

        public static void Shutdown()
        {
            try
            {
                if (_initialised && _shutdown != null) _shutdown();
                if (_lib != IntPtr.Zero) FreeLibrary(_lib);
            }
            catch { }
            finally
            {
                _initialised = false;
                _ugc = IntPtr.Zero;
                _lib = IntPtr.Zero;
            }
        }

        // ---------------------------------------------------------- manual --

        /// <summary>Fallback: open the Workshop page so the player can subscribe.</summary>
        public static void OpenWorkshopPage(ulong id)
        {
            try { Process.Start("steam://url/CommunityFilePage/" + id); }
            catch
            {
                try
                {
                    Process.Start(WorkshopDetailsUrl(id, english: false));
                }
                catch { }
            }
        }
    }

    /// <summary>
    /// Creates directory junctions the way DayZ's own !Workshop folder uses
    /// them - so a mod downloaded into steamapps\workshop\content\221100\&lt;id&gt;
    /// can also be reached under a readable @Name.
    ///
    /// WHY NOT JUST SHELL OUT TO "mklink /J"
    ///   Because it is not dependable. On the machine this was developed on,
    ///   cmd.exe's mklink refused every junction with "Local volumes are
    ///   required to complete the operation" while the native call below
    ///   succeeded in the very same folder, as the very same non-admin user.
    ///   Spawning a console window on a player's screen was never appealing
    ///   either. This talks to the filesystem directly: no shell, no window,
    ///   and no administrator rights - junctions have never needed them,
    ///   only symlinks do.
    /// </summary>
    internal static class Junction
    {
        private const uint FSCTL_SET_REPARSE_POINT      = 0x000900A4;
        private const uint IO_REPARSE_TAG_MOUNT_POINT   = 0xA0000003;
        private const uint GENERIC_WRITE                = 0x40000000;
        private const uint OPEN_EXISTING                = 3;
        private const uint FILE_FLAG_BACKUP_SEMANTICS   = 0x02000000;
        private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
        private static extern IntPtr CreateFile(string name, uint access, uint share,
            IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool DeviceIoControl(IntPtr handle, uint code,
            byte[] inBuffer, int inSize, IntPtr outBuffer, int outSize,
            out int returned, IntPtr overlapped);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        public static bool TryCreate(string link, string target)
        {
            IntPtr handle = IntPtr.Zero;
            bool madeDir = false;
            try
            {
                if (!Directory.Exists(target)) return false;
                if (Directory.Exists(link)) return true;

                // The substitute name is an NT path, and it must not carry a
                // trailing separator or the reparse point resolves to nothing.
                target = Path.GetFullPath(target).TrimEnd('\\');
                string substitute = @"\??\" + target;

                Directory.CreateDirectory(link);
                madeDir = true;

                handle = CreateFile(link, GENERIC_WRITE, 0, IntPtr.Zero, OPEN_EXISTING,
                                    FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
                                    IntPtr.Zero);
                if (handle == new IntPtr(-1))
                {
                    handle = IntPtr.Zero;
                    throw new IOException("could not open the new folder for reparse");
                }

                byte[] sub = Encoding.Unicode.GetBytes(substitute);
                byte[] print = Encoding.Unicode.GetBytes(target);
                int pathBytes = sub.Length + 2 + print.Length + 2;   // both NUL terminated

                // REPARSE_DATA_BUFFER: an 8-byte header, then the mount-point
                // block of four USHORTs, then the two names back to back.
                var buf = new byte[16 + pathBytes];
                int o = 0;
                BitConverter.GetBytes(IO_REPARSE_TAG_MOUNT_POINT).CopyTo(buf, o); o += 4;
                BitConverter.GetBytes((ushort)(8 + pathBytes)).CopyTo(buf, o);    o += 2;
                BitConverter.GetBytes((ushort)0).CopyTo(buf, o);                  o += 2;
                BitConverter.GetBytes((ushort)0).CopyTo(buf, o);                  o += 2;
                BitConverter.GetBytes((ushort)sub.Length).CopyTo(buf, o);         o += 2;
                BitConverter.GetBytes((ushort)(sub.Length + 2)).CopyTo(buf, o);   o += 2;
                BitConverter.GetBytes((ushort)print.Length).CopyTo(buf, o);       o += 2;
                sub.CopyTo(buf, o);   o += sub.Length + 2;
                print.CopyTo(buf, o);

                int returned;
                if (DeviceIoControl(handle, FSCTL_SET_REPARSE_POINT, buf, buf.Length,
                                    IntPtr.Zero, 0, out returned, IntPtr.Zero))
                    return true;

                throw new IOException("FSCTL_SET_REPARSE_POINT failed with "
                                      + Marshal.GetLastWin32Error());
            }
            catch
            {
                // Leave nothing half-made behind: an empty folder named @Mod
                // would look like an installed mod and break the next launch.
                if (madeDir) { try { Directory.Delete(link); } catch { } }
                return false;
            }
            finally
            {
                if (handle != IntPtr.Zero) CloseHandle(handle);
            }
        }
    }
}
