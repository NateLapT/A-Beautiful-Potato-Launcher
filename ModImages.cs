// ---------------------------------------------------------------------------
//  Workshop preview images for mods.
//
//  WHY THIS TALKS TO STEAM'S WEB API
//    Steam does not keep a mod's preview picture on disk. The workshop folder
//    holds only the mod's own files, and checked against a real library just 5
//    of the first 300 mods ship any png or jpg at all - DayZ authors use .paa
//    and .tga, which are Bohemia formats nothing here can draw.
//
//    The UGC details query does carry a preview handle, but the URL field it
//    returns is empty, so there is nothing to follow from the local API.
//
//    Steam's own published-file endpoint does return a usable URL, so that is
//    what this uses. It is Steam, not a third party, and it is asked at most
//    once per mod: every image is cached on disk afterwards and never fetched
//    again.
//
//  NOTHING HERE BLOCKS
//    Every lookup happens on a background thread and calls back when it has
//    something. A mod with no preview, or no network, simply never calls back
//    and the caller keeps whatever placeholder it drew.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace ABeautifulPotatoLauncher
{
    internal static class ModImages
    {
        private static readonly object Lock = new object();
        private static readonly HashSet<ulong> InFlight = new HashSet<ulong>();
        private static readonly HashSet<ulong> Hopeless = new HashSet<ulong>();

        private static string CacheDir
        {
            get
            {
                string d = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "ABeautifulPotatoLauncher", "images");
                try { if (!Directory.Exists(d)) Directory.CreateDirectory(d); }
                catch { }
                return d;
            }
        }

        /// <summary>
        /// Where a mod's preview is cached.
        ///
        /// Named for what the bytes ARE, not what the download was called. The
        /// workshop serves JPEG and PNG and the URL says neither, so the format
        /// is read from the first few bytes and the file gets the matching
        /// extension - which means Explorer previews it and anything else can
        /// open it.
        /// </summary>
        private static string CachePath(ulong id, string extension)
        {
            return Path.Combine(CacheDir, id + extension);
        }

        /// <summary>Any cached preview for this mod, whatever its format.</summary>
        private static string FindCached(ulong id)
        {
            foreach (string ext in new[] { ".jpg", ".png", ".gif", ".img" })
            {
                string p = Path.Combine(CacheDir, id + ext);
                if (File.Exists(p)) return p;
            }
            return null;
        }

        /// <summary>
        /// The right extension for these bytes, by magic number. Falls back to
        /// .img when nothing recognisable is there, so a bad download is still
        /// cached rather than fetched over and over.
        /// </summary>
        private static string ExtensionFor(byte[] data)
        {
            if (data == null || data.Length < 4) return ".img";

            if (data[0] == 0xFF && data[1] == 0xD8) return ".jpg";                 // JPEG
            if (data[0] == 0x89 && data[1] == 0x50 &&
                data[2] == 0x4E && data[3] == 0x47) return ".png";                 // PNG
            if (data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46) return ".gif";

            return ".img";
        }

        /// <summary>
        /// The image for a mod if it is already on disk, otherwise null. Never
        /// touches the network, so it is safe to call while painting.
        /// </summary>
        public static Image Cached(ulong id)
        {
            try
            {
                string path = FindCached(id);
                if (!File.Exists(path)) return null;

                // Loaded through a byte array on purpose: Image.FromFile keeps
                // the file locked for the lifetime of the image, which would
                // stop the cache ever being replaced.
                byte[] bytes = File.ReadAllBytes(path);
                using (var ms = new MemoryStream(bytes)) return Image.FromStream(ms);
            }
            catch { return null; }
        }

        /// <summary>
        /// Fetches the preview for a mod in the background and calls back on
        /// success. Does nothing if the image is already cached, a fetch is
        /// already running, or this mod has been tried and has no preview.
        /// </summary>
        public static void Fetch(ulong id, Action<Image> onReady)
        {
            if (id == 0 || onReady == null) return;

            lock (Lock)
            {
                if (InFlight.Contains(id) || Hopeless.Contains(id)) return;
                if (FindCached(id) != null) return;
                InFlight.Add(id);
            }

            new Thread(() =>
            {
                Image img = null;
                try
                {
                    string url = PreviewUrl(id);
                    if (!string.IsNullOrEmpty(url))
                    {
                        byte[] data = Download(url);
                        if (data != null && data.Length > 64)
                        {
                            try { File.WriteAllBytes(CachePath(id, ExtensionFor(data)), data); }
                            catch { }
                            using (var ms = new MemoryStream(data)) img = Image.FromStream(ms);
                        }
                    }
                }
                catch { }
                finally
                {
                    lock (Lock)
                    {
                        InFlight.Remove(id);
                        if (img == null) Hopeless.Add(id);   // do not keep retrying
                    }
                }

                if (img != null)
                {
                    try { onReady(img); } catch { }
                }
            })
            { IsBackground = true }.Start();
        }

        /// <summary>
        /// Asks Steam for a published file's details and pulls the preview URL
        /// out of the reply.
        ///
        /// The reply is JSON, and .NET Framework 4.8 ships no JSON reader that
        /// can be used without dragging in a package. One field is wanted from
        /// a reply this launcher does not control, so it is read directly
        /// rather than modelling the whole document.
        /// </summary>
        private static string PreviewUrl(ulong id)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(
                    "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/");
                req.Method = "POST";
                req.ContentType = "application/x-www-form-urlencoded";
                req.Timeout = 8000;
                req.ReadWriteTimeout = 8000;
                req.UserAgent = "ABeautifulPotatoLauncher";

                byte[] body = Encoding.ASCII.GetBytes("itemcount=1&publishedfileids%5B0%5D=" + id);
                req.ContentLength = body.Length;
                using (var rs = req.GetRequestStream()) rs.Write(body, 0, body.Length);

                string json;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    json = sr.ReadToEnd();

                return ExtractString(json, "preview_url");
            }
            catch { return null; }
        }

        /// <summary>Pulls "key":"value" out of a JSON document, unescaping slashes.</summary>
        private static string ExtractString(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            string needle = "\"" + key + "\"";
            int i = json.IndexOf(needle, StringComparison.Ordinal);
            if (i < 0) return null;

            i = json.IndexOf(':', i + needle.Length);
            if (i < 0) return null;
            i = json.IndexOf('"', i);
            if (i < 0) return null;
            i++;

            var sb = new StringBuilder();
            while (i < json.Length && json[i] != '"')
            {
                if (json[i] == '\\' && i + 1 < json.Length)
                {
                    i++;
                    if (json[i] == '/') sb.Append('/');
                    else if (json[i] == '\\') sb.Append('\\');
                    else if (json[i] == 'n') sb.Append('\n');
                    else sb.Append(json[i]);
                }
                else sb.Append(json[i]);
                i++;
            }
            string v = sb.ToString();
            return v.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? v : null;
        }

        private static byte[] Download(string url)
        {
            try
            {
                // Steam's image hosts are TLS 1.2 only, and .NET Framework 4.8
                // does not always negotiate it by default depending on how the
                // process was configured.
                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
                catch { }

                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Timeout = 10000;
                req.ReadWriteTimeout = 10000;
                req.UserAgent = "ABeautifulPotatoLauncher";

                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var rs = resp.GetResponseStream())
                using (var ms = new MemoryStream())
                {
                    var buf = new byte[16384];
                    int n;
                    while ((n = rs.Read(buf, 0, buf.Length)) > 0)
                    {
                        ms.Write(buf, 0, n);
                        if (ms.Length > 8 * 1024 * 1024) break;   // a preview is never this big
                    }
                    return ms.ToArray();
                }
            }
            catch { return null; }
        }
    }
}
