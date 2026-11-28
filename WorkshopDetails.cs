// ---------------------------------------------------------------------------
//  A Workshop item's publisher and preview image, for the Mod info window.
//
//  Both come from Steam's public web API - ISteamRemoteStorage/
//  GetPublishedFileDetails needs no key, and gives the publisher's SteamID64
//  even when that profile is private. The publisher's name comes from their
//  community profile's XML, which carries the name for private profiles too.
//  Kept for the session, so reopening Mod info asks nothing again.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ABeautifulPotatoLauncher
{
    internal sealed class WorkshopDetails
    {
        public string CreatorId = "";       // SteamID64, "" when unknown
        public string CreatorName = "";
        public string PreviewUrl = "";
        public Image Preview;               // null when there is none

        public string ProfileUrl
        {
            get { return CreatorId.Length > 0 ? "https://steamcommunity.com/profiles/" + CreatorId : ""; }
        }

        private static readonly Dictionary<ulong, WorkshopDetails> Cache = new Dictionary<ulong, WorkshopDetails>();
        private static readonly Dictionary<string, string> Names = new Dictionary<string, string>();

        /// <summary>
        /// Looks the item up (blocking - call it off the UI thread). Never
        /// throws; what could not be found is left empty.
        /// </summary>
        public static WorkshopDetails Get(ulong id)
        {
            lock (Cache) { WorkshopDetails had; if (Cache.TryGetValue(id, out had)) return had; }

            var d = new WorkshopDetails();
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                using (var web = new WebClient())
                {
                    web.Headers[HttpRequestHeader.UserAgent] = "ABeautifulPotatoLauncher/" + Program.Version;
                    var form = new NameValueCollection { { "itemcount", "1" }, { "publishedfileids[0]", id.ToString() } };
                    string json = Encoding.UTF8.GetString(web.UploadValues(
                        "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/", "POST", form));
                    d.CreatorId = JsonString(json, "creator");
                    d.PreviewUrl = JsonString(json, "preview_url");
                }
            }
            catch { }

            if (d.CreatorId.Length > 0) d.CreatorName = NameOf(d.CreatorId);

            if (d.PreviewUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using (var web = new WebClient())
                    {
                        byte[] img = web.DownloadData(d.PreviewUrl);
                        if (img.Length < 8 * 1024 * 1024)
                            using (var ms = new MemoryStream(img))
                            using (var loaded = Image.FromStream(ms))
                                d.Preview = new Bitmap(loaded);       // a copy, so the stream can go
                    }
                }
                catch { }
            }

            lock (Cache) Cache[id] = d;
            return d;
        }

        /// <summary>A profile's display name, from its public XML; "" when it can't be had.</summary>
        private static string NameOf(string steamId)
        {
            lock (Names) { string had; if (Names.TryGetValue(steamId, out had)) return had; }
            string name = "";
            try
            {
                using (var web = new WebClient())
                {
                    web.Encoding = Encoding.UTF8;
                    web.Headers[HttpRequestHeader.UserAgent] = "ABeautifulPotatoLauncher/" + Program.Version;
                    string xml = web.DownloadString("https://steamcommunity.com/profiles/" + steamId + "/?xml=1");
                    var m = Regex.Match(xml, @"<steamID><!\[CDATA\[(.*?)\]\]></steamID>", RegexOptions.Singleline);
                    if (m.Success) name = m.Groups[1].Value.Trim();
                }
            }
            catch { }
            lock (Names) Names[steamId] = name;
            return name;
        }

        /// <summary>One string field from Steam's JSON reply ("key":"value"); "" when absent.</summary>
        private static string JsonString(string json, string key)
        {
            var m = Regex.Match(json ?? "", "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            return m.Success ? Regex.Unescape(m.Groups[1].Value) : "";
        }
    }
}
