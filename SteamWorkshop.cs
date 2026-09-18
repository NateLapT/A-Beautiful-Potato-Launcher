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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace BeautifulPotatoLauncher
{
    internal sealed class Mod
    {
        public readonly string Name;
        public readonly ulong WorkshopId;

        public Mod(string name, ulong workshopId)
        {
            Name = name;
            WorkshopId = workshopId;
        }

        public override string ToString() { return Name; }
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

        public static string ItemPath(string steamPath, ulong id)
        {
            return Path.Combine(WorkshopRoot(steamPath), id.ToString());
        }

        /// <summary>
        /// The authoritative readiness test. Deliberately filesystem-based so it
        /// works whether the subscription came from the API or from the player
        /// clicking Subscribe on the Workshop page.
        /// </summary>
        public static bool IsInstalled(string steamPath, ulong id)
        {
            try
            {
                string dir = ItemPath(steamPath, id);
                if (!Directory.Exists(dir)) return false;

                // A folder that exists but is still filling up is not ready. Any
                // .pbo present is a good signal the download has produced content.
                return Directory.EnumerateFiles(dir, "*.pbo", SearchOption.AllDirectories).Any()
                    || Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Take(2).Count() > 1;
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

        private static IntPtr _lib = IntPtr.Zero;
        private static IntPtr _ugc = IntPtr.Zero;
        private static bool _initialised;

        private static ShutdownFn _shutdown;
        private static RunCallbacksFn _runCallbacks;
        private static SubscribeFn _subscribe;
        private static GetItemStateFn _getState;
        private static DownloadItemFn _download;
        private static DownloadInfoFn _downloadInfo;

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
                _getState  = Bind<GetItemStateFn>("SteamAPI_ISteamUGC_GetItemState");
                _download  = Bind<DownloadItemFn>("SteamAPI_ISteamUGC_DownloadItem");
            _downloadInfo = Bind<DownloadInfoFn>("SteamAPI_ISteamUGC_GetItemDownloadInfo");

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
                try { Process.Start("https://steamcommunity.com/sharedfiles/filedetails/?id=" + id); }
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
