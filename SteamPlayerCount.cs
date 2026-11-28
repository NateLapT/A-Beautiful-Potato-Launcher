// ---------------------------------------------------------------------------
//  How many people have a game running right now, as Steam counts them - the
//  figure SteamDB charts. Steam's public player-count service, no key needed;
//  a reply of a few dozen bytes, so asking every few minutes costs nothing.
//
//  It counts everyone with the game open: on a server, but also in the menu,
//  loading in, in a queue or playing offline. The launcher's other figure
//  (players on listed servers, from the server list) is shown beside it.
// ---------------------------------------------------------------------------

using System;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;

namespace ABeautifulPotatoLauncher
{
    internal static class SteamPlayerCount
    {
        private const string Url = "https://api.steampowered.com/ISteamUserStats/GetNumberOfCurrentPlayers/v1/?appid=";

        private static readonly Regex Count = new Regex(@"""player_count""\s*:\s*(\d+)", RegexOptions.Compiled);

        /// <summary>Players with this app running, or -1 when Steam could not be asked.</summary>
        public static long Fetch(uint appId)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(Url + appId);
                req.Timeout = 10000;
                req.ReadWriteTimeout = 10000;
                req.UserAgent = "ABeautifulPotatoLauncher";
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var reader = new StreamReader(resp.GetResponseStream()))
                {
                    var m = Count.Match(reader.ReadToEnd());
                    long n;
                    return m.Success && long.TryParse(m.Groups[1].Value, out n) ? n : -1;
                }
            }
            catch { return -1; }
        }
    }
}
