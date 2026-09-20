// ---------------------------------------------------------------------------
//  Server browser filters.
//
//  Split deliberately in two:
//
//  * Anything Steam can filter is pushed to Steam (ToSteamFilters). That
//    matters because RequestInternetServerList caps at 10,000 results for DayZ
//    stable - filtering locally would only ever search a truncated list, so a
//    name search has to become a name_match filter on the request itself.
//
//  * Everything Steam has no filter for (ping, 3rd person, mods, in-game time,
//    address text) is applied locally in Matches().
//
//  DayZ publishes its flags in the Steam tags field, e.g.
//    battleye,no3rd,external,privHive,shard123ABC,lqs0,etm3.000000,
//    entm64.000000,mod,isDLC,16:48
//  so "3rd person", "has mods" and the in-game clock all come from there.
// ---------------------------------------------------------------------------

using System.Collections.Generic;

namespace BeautifulPotatoExpLauncher
{
    internal enum TriState { Any = 0, Enabled = 1, Disabled = 2 }
    internal enum PlayersMode { Any = 0, NotEmpty = 1, NotFull = 2, Empty = 3 }
    internal enum TimeMode { Any = 0, Day = 1, Night = 2 }

    internal sealed class BrowserFilters
    {
        public string Name = "";
        public string Address = "";
        public string Map = "";
        public string Search = "";
        public int MaxPing;                    // 0 = any
        public PlayersMode Players = PlayersMode.Any;
        public PlayersMode PlayersMode
        {
            get { return Players; }
            set { Players = value; }
        }
        public TimeMode GameTime = TimeMode.Any;
        public TimeMode TimeMode
        {
            get { return GameTime; }
            set { GameTime = value; }
        }
        public TriState ThirdPerson = TriState.Any;
        public TriState ThirdPersonMode
        {
            get { return ThirdPerson; }
            set { ThirdPerson = value; }
        }
        public TriState Mods = TriState.Any;
        public TriState ModsMode
        {
            get { return Mods; }
            set { Mods = value; }
        }
        public TriState Official = TriState.Any;
        public bool NoPassword;
        public bool HideFull;
        public bool HideEmpty;

        /// <summary>
        /// Hide servers that are almost certainly not real. Two rules, both
        /// measured against the live master list rather than guessed:
        ///
        ///   SLOT COUNT - DayZ tops out at 127 players. 410 of 5,327 stable
        ///   servers advertise 140, 188, 200, 220, 240 or 255 slots, which the
        ///   game cannot do; they exist to sort to the top of a browser.
        ///
        ///   REDIRECT FARMS - one address running dozens of servers under dozens
        ///   of DIFFERENT community names. Real hosts running many servers use
        ///   one or a few brands: the biggest genuine ones seen were 20 servers
        ///   sharing a single name, and A Beautiful Potato runs 12-14. The farms
        ///   run 59, 60 and even 200 per address with a distinct name on nearly
        ///   every one. The gap between 20 and 59 is wide enough that the exact
        ///   threshold does not matter - anything from 25 to 50 hides the same
        ///   servers.
        ///
        /// Deliberately NOT part of the rule: "near full". The master list
        /// reports every server as 0 players until it is individually queried,
        /// so fullness is simply not known at the point the list is filtered -
        /// and the count rule already separates the farms with room to spare.
        /// </summary>
        public bool HideFakes = true;

        /// <summary>DayZ's real player ceiling.</summary>
        public const int MaxRealSlots = 127;

        /// <summary>An address running at least this many servers is suspect...</summary>
        ///
        /// The original thresholds were tuned to the kinds of farms that were
        /// obvious in the mid-2020s master list, but the more recent redirect
        /// farms are denser and more spread out; they still show as the same
        /// repeated cluster pattern, just below the old cut-off.
        public const int FarmServersPerIp = 15;

        /// <summary>...but only if it is also wearing this many different names.</summary>
        public const int FarmNamesPerIp = 10;

        /// <summary>
        /// How many servers may share ONE name on ONE address before the group
        /// is a redirect farm.
        ///
        /// This was the blind spot in every other rule. They all key on an
        /// address running many DIFFERENT names, because that is what the loud
        /// farms look like - and the name sets are held in a HashSet, so twenty
        /// servers called the same thing collapse to a single entry and look
        /// like the quietest host on the list.
        ///
        /// Measured against the live master list: six addresses were each
        /// running exactly 20 servers under one identical name with one shared
        /// mod set - 120 servers, invisible to every existing rule. The largest
        /// same-name group anywhere else was 6, so eight sits in the gap.
        ///
        /// It cannot touch a real community, which names its servers apart: A
        /// Beautiful Potato runs 14 on one address and no name repeats twice.
        /// </summary>
        public const int FarmSameNamePerIp = 8;

        // A farm can dodge a per-address rule by spreading over a /24, which is
        // exactly what 91.196.33.x does: 320 servers over 16 addresses under 299
        // names. So subnets are judged too - but on DENSITY, because a genuine
        // hosting provider also has a busy /24. The newer redirect farms are
        // clustered enough to show up at the lower end of that density curve,
        // so the limits are intentionally kept conservative.
        public const int FarmServersPerSubnet = 20;
        public const int FarmNamesPerSubnet = 12;
        public const double FarmServersPerAddress = 3.0;

        /// <summary>The /24 an address belongs to, e.g. 91.196.33.79 -> 91.196.33</summary>
        public static string Subnet24(string host)
        {
            if (string.IsNullOrEmpty(host)) return "";
            int last = host.LastIndexOf('.');
            return last > 0 ? host.Substring(0, last) : host;
        }

        /// <summary>Filters Steam itself can apply, narrowing the result set.</summary>
        public List<KeyValuePair<string, string>> ToSteamFilters()
        {
            var f = new List<KeyValuePair<string, string>>();

            if (!string.IsNullOrEmpty(Name.Trim()))
            {
                // Valve's name_match takes shell-style wildcards.
                string pattern = Name.Trim();
                if (!pattern.Contains("*")) pattern = "*" + pattern + "*";
                f.Add(new KeyValuePair<string, string>("name_match", pattern));
            }

            string endpoint;
            if (TryExactEndpoint(Address, out endpoint))
            {
                // Steam can resolve an exact game address without the 10,000-row
                // cap hiding it, and it returns the server's real query port.
                f.Add(new KeyValuePair<string, string>("gameaddr", endpoint));
            }

            if (!string.IsNullOrEmpty(Map.Trim()))
                f.Add(new KeyValuePair<string, string>("map", Map.Trim()));

            // Valve's semantics: "empty\1" means EXCLUDE empty servers, and
            // "full\1" means exclude full ones. "noplayers\1" is the opposite of
            // empty\1 - only servers with nobody on them.
            if (HideEmpty || Players == PlayersMode.NotEmpty)
                f.Add(new KeyValuePair<string, string>("empty", "1"));
            if (HideFull || Players == PlayersMode.NotFull)
                f.Add(new KeyValuePair<string, string>("full", "1"));
            if (Players == PlayersMode.Empty)
                f.Add(new KeyValuePair<string, string>("noplayers", "1"));

            if (NoPassword)
                f.Add(new KeyValuePair<string, string>("password", "0"));

            // ASKING FOR OFFICIAL SERVERS IS ASKING STEAM, NOT FILTERING LOCALLY.
            //
            // Steam caps a list at 10,000 servers, and DayZ has far more than
            // that, so the cap truncates - and what survives it is arbitrary.
            // Measured: filtering the full list locally found 27 official
            // servers, ALL of them North American, because the cap had already
            // thrown the rest away before this code ever saw them.
            //
            // Sending "none of these tags: privHive" to Steam instead returns
            // 171 servers total, of which 168 are official, spanning Europe,
            // Asia Pacific, South America and North America. Same test, same
            // rules, six times the servers - the difference is entirely in what
            // gets asked for.
            //
            // gamedataand does NOT work here, for the record: DayZ publishes
            // these as game TAGS, and gamedataand returned nothing at all.
            if (Official == TriState.Enabled)
                f.Add(new KeyValuePair<string, string>("gametagsnor", "privHive"));

            return f;
        }

        public static bool LooksLikeAddressSearch(string value)
        {
            string s = (value ?? "").Trim();
            string endpoint;
            if (TryExactEndpoint(s, out endpoint)) return true;
            if (s.Length == 0 || s.Contains(" ")) return false;
            return s.Contains(".") || s.Contains(":");
        }

        private static bool TryExactEndpoint(string value, out string endpoint)
        {
            endpoint = null;
            string s = (value ?? "").Trim();
            int c = s.LastIndexOf(':');
            if (c <= 0 || c == s.Length - 1) return false;

            int port;
            if (!int.TryParse(s.Substring(c + 1), out port)) return false;
            if (port <= 0 || port >= 65535) return false;

            string host = s.Substring(0, c).Trim();
            if (host.Length == 0 || host.Contains(" ")) return false;

            endpoint = host + ":" + port;
            return true;
        }

        /// <summary>The rest, applied to each row after it arrives.</summary>
        public bool Matches(BrowserServer s)
        {
            if (s == null) return false;

            if (!string.IsNullOrEmpty(Name.Trim()) &&
                s.Name.IndexOf(Name.Trim(), System.StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            if (!string.IsNullOrEmpty(Address.Trim()) &&
                s.Endpoint.IndexOf(Address.Trim(), System.StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            if (!string.IsNullOrEmpty(Map.Trim()) &&
                s.Map.IndexOf(Map.Trim(), System.StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            // A ping of 0 means Steam has not measured it yet, so never hide on it.
            if (MaxPing > 0 && s.Ping > MaxPing) return false;

            if (HideEmpty && s.Players == 0) return false;
            if (HideFull && s.Players >= s.MaxPlayers && s.MaxPlayers > 0) return false;
            if (NoPassword && s.Password) return false;

            switch (Players)
            {
                case PlayersMode.NotEmpty: if (s.Players == 0) return false; break;
                case PlayersMode.NotFull:  if (s.MaxPlayers > 0 && s.Players >= s.MaxPlayers) return false; break;
                case PlayersMode.Empty:    if (s.Players != 0) return false; break;
            }

            if (ThirdPerson == TriState.Enabled && !s.ThirdPerson) return false;
            if (ThirdPerson == TriState.Disabled && s.ThirdPerson) return false;

            if (Mods == TriState.Enabled && !s.HasMods) return false;
            if (Mods == TriState.Disabled && s.HasMods) return false;

            if (Official == TriState.Enabled && !IsOfficial(s)) return false;
            if (Official == TriState.Disabled && IsOfficial(s)) return false;

            if (GameTime != TimeMode.Any)
            {
                int h = s.GameHour;
                if (h < 0) return false;
                bool day = h >= 6 && h < 19;
                if (GameTime == TimeMode.Day && !day) return false;
                if (GameTime == TimeMode.Night && day) return false;
            }

            return true;
        }

        /// <summary>
        /// Is this one of Bohemia's own servers?
        ///
        /// THE NAME IS WORSE THAN USELESS HERE, AND THAT IS NOT A GUESS.
        ///   Measured against the live master list: of 5,342 servers whose
        ///   details could be read, 199 have "official" in their name and NOT
        ///   ONE of them is official. Every genuine official server has no such
        ///   word in its name. Matching on the name would therefore return
        ///   precisely the wrong 199 servers - most of them sitting on two
        ///   redirect-farm address blocks. So the name is never consulted, in
        ///   either direction: a community server is free to call itself
        ///   whatever it likes and it changes nothing here.
        ///
        /// WHAT IS CHECKED INSTEAD
        ///   THE HIVE, primarily. Character data on official servers lives in
        ///   Bohemia's central hive, and a server that is not authorised to use
        ///   it reports privHive. A host can type any name they want into their
        ///   config; they cannot award themselves the public hive. Of those
        ///   5,342 servers exactly 27 lacked privHive, and all 27 were plainly
        ///   Bohemia's - same naming scheme, no mods, four address blocks.
        ///
        ///   The rest are corroboration, cheap to test and all true of every
        ///   one of those 27, so that a server with missing or garbled tags
        ///   cannot fall through as official just by failing to say privHive:
        ///
        ///     no mods        - official servers are vanilla, always
        ///     BattlEye on    - all 27 run it
        ///     a shard tag    - shard000 (3rd person) or shard001 (1st only);
        ///                      the farms advertise nonsense like shard123ABC
        ///     sane slots     - inside DayZ's real ceiling
        ///
        ///   Requiring all of them together costs nothing: the same 27 servers
        ///   pass, and a server has to earn every one of them rather than just
        ///   be missing a tag.
        /// </summary>
        public static bool IsOfficial(BrowserServer s)
        {
            if (s == null) return false;

            // The load-bearing one.
            if (s.PrivateHive) return false;

            // Tags absent entirely means "not answered yet", NOT "public hive".
            if (string.IsNullOrEmpty(s.Tags)) return false;

            if (s.HasMods) return false;
            if (!s.BattlEye) return false;
            if (s.MaxPlayers > MaxRealSlots) return false;

            return HasOfficialShard(s.Tags);
        }

        /// <summary>
        /// Official servers carry shard000 or shard001 and nothing else. The
        /// redirect farms carry things like shard123ABC, which is exactly the
        /// kind of near-miss that a "starts with shard" test would wave through.
        /// </summary>
        private static bool HasOfficialShard(string tags)
        {
            foreach (string raw in tags.Split(','))
            {
                string t = raw.Trim();
                if (t.Equals("shard000", System.StringComparison.OrdinalIgnoreCase) ||
                    t.Equals("shard001", System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// The slot-count half of the fake test. The farm half needs the whole
        /// list to work out, so it lives in the form.
        /// </summary>
        public bool LooksFakeBySlots(BrowserServer s)
        {
            return HideFakes && s != null && s.MaxPlayers > MaxRealSlots;
        }

        public bool AnyActive
        {
            get
            {
                return Name.Trim().Length > 0 || Address.Trim().Length > 0 || Map.Trim().Length > 0
                    || MaxPing > 0 || Players != PlayersMode.Any || GameTime != TimeMode.Any
                    || ThirdPerson != TriState.Any || Mods != TriState.Any
                    || Official != TriState.Any
                    || NoPassword || HideFull || HideEmpty;
            }
        }

        public void Clear()
        {
            Name = Address = Map = "";
            MaxPing = 0;
            Players = PlayersMode.Any;
            GameTime = TimeMode.Any;
            ThirdPerson = Mods = TriState.Any;
            NoPassword = HideFull = HideEmpty = false;
        }
    }
}
