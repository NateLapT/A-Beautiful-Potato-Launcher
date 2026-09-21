// ---------------------------------------------------------------------------
//  Which country - and therefore which part of the world - an address is in.
//
//  WHERE THE DATA COMES FROM
//    The five regional internet registries (ARIN, RIPE, APNIC, LACNIC,
//    AFRINIC) publish, free and daily, the list of which IPv4 ranges belong to
//    which country. tools/build-ip-table.ps1 turns 27 MB of their text into the
//    1.4 MB table embedded here. No API key, no lookup service, nothing leaves
//    the machine - which matters, because a launcher that phoned a geolocation
//    API would be handing someone a list of every server its players browse.
//
//  WHAT IT IS AND IS NOT
//    This is REGISTRATION data, not geolocation. It says where a block of
//    addresses is registered, which for hosting companies is almost always
//    where the hardware sits - but a provider holding one global allocation can
//    serve traffic from anywhere in it. Measured against a real DayZ browse of
//    3,548 distinct server addresses it resolved 100% of them, and the spread
//    (US 2084, DE 471, CA 379, RU 132, GB 93) matches where DayZ is actually
//    played. Treat it as reliable, not infallible.
//
//  COUNTRY FIRST, REGION DERIVED
//    The registries' own regions are NOT continents: RIPE covers Europe and the
//    Middle East together, ARIN takes in parts of the Caribbean. Since the
//    table carries the country anyway, the continent is worked out here from
//    the country, so "Europe" means Europe and not "Europe, Russia and Iran".
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace ABeautifulPotatoLauncher
{
    /// <summary>The broad areas a player picks between.</summary>
    internal enum WorldRegion
    {
        Unknown = 0,
        NorthAmerica,
        SouthAmerica,
        Europe,
        Asia,
        Africa,
        Oceania,
        MiddleEast
    }

    internal static class IpRegion
    {
        private static readonly object Lock = new object();
        private static bool _loaded;

        // Parallel arrays rather than an array of structs: one binary search
        // over _starts touches a third of the memory a struct array would, and
        // this is searched once per server in a list of thousands.
        private static uint[] _starts;
        private static uint[] _ends;      // exclusive
        private static byte[] _codes;     // two ASCII bytes per record

        /// <summary>
        /// Answers already worked out, keyed by /24.
        ///
        /// Servers cluster hard onto a few hosting ranges - 3,548 addresses in
        /// one browse sat in 2,140 /24 blocks - so this turns most lookups into
        /// a dictionary hit and costs about 25 KB.
        /// </summary>
        private static readonly Dictionary<uint, string> Cache = new Dictionary<uint, string>();

        /// <summary>
        /// The two-letter country code for an address, or "" if it is not in
        /// the table or does not parse.
        /// </summary>
        public static string Country(string host)
        {
            uint ip;
            if (!TryParse(host, out ip)) return "";

            uint block = ip & 0xFFFFFF00u;

            lock (Lock)
            {
                string hit;
                if (Cache.TryGetValue(block, out hit)) return hit;
            }

            string cc = Lookup(ip);

            lock (Lock) { Cache[block] = cc; }
            return cc;
        }

        /// <summary>The part of the world an address is in.</summary>
        public static WorldRegion Region(string host)
        {
            return RegionOf(Country(host));
        }

        /// <summary>
        /// A server's country, from the best source available.
        ///
        /// THE ONE PLACE THIS IS DECIDED. An official server's own name beats
        /// its address - Bohemia's fleet is on German and Luxembourgish blocks
        /// whatever continent the hardware is on - and everything that needs a
        /// country has to agree on that, or the column says US while the region
        /// filter files it under Europe. Which is exactly what happened when
        /// the row worked it out one way and the filter another.
        /// </summary>
        public static string CountryOf(string name, string host)
        {
            string cc = CountryFromOfficialName(name);
            return cc.Length > 0 ? cc : Country(host);
        }

        /// <summary>The part of the world a server is in, by the same rule.</summary>
        public static WorldRegion RegionOf(string name, string host)
        {
            return RegionOf(CountryOf(name, host));
        }

        /// <summary>
        /// The country a Bohemia official server says it is in, or "".
        ///
        /// WHY THE NAME BEATS THE ADDRESS HERE
        ///   Official servers are named "3208 | NORTH AMERICA - MI | Temp" -
        ///   the datacentre is in the name. Their addresses are not: the whole
        ///   official fleet sits in RIPE-allocated ranges registered to
        ///   Germany, so the registry table calls a New York server "DE", and
        ///   so does it for Los Angeles, Miami and Sydney. Measured: every one
        ///   of the 181 officials with this name shape is on a DE-registered
        ///   block.
        ///
        ///   The name is the better source for exactly these servers, and only
        ///   these - a community server's name is whatever its owner typed.
        ///
        /// THE CODES ARE CITIES, NOT STATES
        ///   MI is Miami, not Michigan. LA is Los Angeles, SP is Sao Paulo, SY
        ///   is Sydney. Reading them as states or countries is how a Florida
        ///   server ends up filed under the Great Lakes.
        /// </summary>
        public static string CountryFromOfficialName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";

            // "<number> | <REGION> - <CODE>" and then anything.
            var m = OfficialName.Match(name);
            if (!m.Success) return "";

            string code = m.Groups[2].Value.ToUpperInvariant();

            string cc;
            return DataCentres.TryGetValue(code, out cc) ? cc : "";
        }

        // Accepts both the code Bohemia ships and the expanded spelling this
        // launcher substitutes - "- MI" and "- Miami" both have to resolve, or
        // renaming the server for readability would cost it its country.
        private static readonly System.Text.RegularExpressions.Regex OfficialName =
            new System.Text.RegularExpressions.Regex(
                @"^\s*\d+\s*\|\s*([A-Za-z ]+?)\s*-\s*([A-Za-z]{2,12})\b",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Bohemia's datacentre codes, as they appear in official server names.
        /// </summary>
        private static readonly Dictionary<string, string> DataCentres =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "NY", "US" },     // New York
                { "LA", "US" },     // Los Angeles
                { "MI", "US" },     // MIAMI - not Michigan
                { "DE", "DE" },     // Germany
                { "SP", "BR" },     // Sao Paulo
                { "SY", "AU" },     // Sydney
                { "HK", "HK" },     // Hong Kong
                { "SG", "SG" },     // Singapore
                { "HC", "VN" },     // Ho Chi Minh City
                { "TY", "JP" },     // Tokyo
                { "LO", "GB" },     // London
                { "AM", "NL" },     // Amsterdam

                // The expanded spellings, so a renamed server still resolves.
                { "Miami", "US" },
            };

        /// <summary>
        /// Rewrites an official server's datacentre code to something a person
        /// can read.
        ///
        /// ONLY "MI". Bohemia's codes are datacentre cities, and that one reads
        /// as the state of Michigan to everybody who sees it - the server is in
        /// Florida. The rest are either unambiguous (NY, LA) or would only be
        /// made longer by expanding, so they are left exactly as Bohemia wrote
        /// them.
        ///
        /// Returns the name unchanged when there is nothing to do, so it is
        /// safe to call on everything and safe to call twice.
        /// </summary>
        public static string ReadableName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;

            var m = OfficialName.Match(name);
            if (!m.Success) return name;

            var code = m.Groups[2];
            string expanded;
            if (!Expansions.TryGetValue(code.Value, out expanded)) return name;

            return name.Substring(0, code.Index) + expanded + name.Substring(code.Index + code.Length);
        }

        /// <summary>
        /// Codes worth spelling out. Deliberately short: every entry changes
        /// what a player sees, and only an actively misleading one earns that.
        /// </summary>
        private static readonly Dictionary<string, string> Expansions =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "MI", "Miami" },
            };

        /// <summary>The part of the world a country code belongs to.</summary>
        public static WorldRegion RegionOf(string cc)
        {
            if (string.IsNullOrEmpty(cc)) return WorldRegion.Unknown;

            WorldRegion r;
            return Regions.TryGetValue(cc, out r) ? r : WorldRegion.Unknown;
        }

        /// <summary>How a region is written in the interface.</summary>
        public static string Name(WorldRegion r)
        {
            switch (r)
            {
                case WorldRegion.NorthAmerica: return "North America";
                case WorldRegion.SouthAmerica: return "South America";
                case WorldRegion.Europe:       return "Europe";
                case WorldRegion.Asia:         return "Asia";
                case WorldRegion.Africa:       return "Africa";
                case WorldRegion.Oceania:      return "Oceania";
                case WorldRegion.MiddleEast:   return "Middle East";
                default:                       return "Unknown";
            }
        }

        /// <summary>Every region a player can pick, in the order they are offered.</summary>
        public static WorldRegion[] All
        {
            get
            {
                return new[]
                {
                    WorldRegion.NorthAmerica, WorldRegion.Europe, WorldRegion.Asia,
                    WorldRegion.SouthAmerica, WorldRegion.Oceania,
                    WorldRegion.MiddleEast, WorldRegion.Africa
                };
            }
        }

        // ------------------------------------------------------------ table --

        private static string Lookup(uint ip)
        {
            EnsureLoaded();
            if (_starts == null || _starts.Length == 0) return "";

            // The last range that starts at or below this address. Ranges do
            // not overlap, so if it is not in that one it is in none of them.
            int lo = 0, hi = _starts.Length - 1, found = -1;
            while (lo <= hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                if (_starts[mid] <= ip) { found = mid; lo = mid + 1; }
                else hi = mid - 1;
            }

            if (found < 0 || ip >= _ends[found]) return "";

            return string.Concat((char)_codes[found * 2], (char)_codes[found * 2 + 1]);
        }

        private static void EnsureLoaded()
        {
            lock (Lock)
            {
                if (_loaded) return;
                _loaded = true;       // set first: a failure must not retry per lookup

                try
                {
                    var asm = Assembly.GetExecutingAssembly();
                    using (var raw = asm.GetManifestResourceStream("ip-country.bin"))
                    {
                        if (raw == null) return;

                        using (var r = new BinaryReader(raw))
                        {
                            if (new string(r.ReadChars(4)) != "IPC1") return;

                            int n = r.ReadInt32();
                            if (n <= 0 || n > 4000000) return;

                            _starts = new uint[n];
                            _ends = new uint[n];
                            _codes = new byte[n * 2];

                            for (int i = 0; i < n; i++)
                            {
                                uint start = r.ReadUInt32();
                                uint count = r.ReadUInt32();
                                _starts[i] = start;

                                // Clamp rather than wrap: a corrupt record at
                                // the top of the space would otherwise produce
                                // an end BELOW its start and match everything.
                                _ends[i] = start + count < start ? uint.MaxValue : start + count;

                                _codes[i * 2] = r.ReadByte();
                                _codes[i * 2 + 1] = r.ReadByte();
                            }
                        }
                    }
                }
                catch
                {
                    _starts = null;
                    _ends = null;
                    _codes = null;
                }
            }
        }

        private static bool TryParse(string host, out uint ip)
        {
            ip = 0;
            if (string.IsNullOrEmpty(host)) return false;

            uint value = 0;
            int part = 0, digits = 0, octet = 0;

            for (int i = 0; i <= host.Length; i++)
            {
                if (i == host.Length || host[i] == '.')
                {
                    if (digits == 0 || octet > 255) return false;
                    value = (value << 8) | (uint)octet;
                    part++;
                    octet = 0;
                    digits = 0;
                    if (part > 4) return false;
                    continue;
                }

                char c = host[i];
                if (c < '0' || c > '9') return false;      // a hostname, not an address
                octet = octet * 10 + (c - '0');
                if (++digits > 3) return false;
            }

            if (part != 4) return false;
            ip = value;
            return true;
        }

        // ----------------------------------------------------- the regions --
        //
        // Every country in the table, by continent. Written out rather than
        // derived from the registry, for the reason in the header: the
        // registries' areas are not continents.
        //
        // The Middle East is deliberately its own region rather than part of
        // Asia. Players think of it that way, and the alternative is Israeli
        // and Emirati servers appearing under an "Asia" filter next to Japan.

        private static readonly Dictionary<string, WorldRegion> Regions = Build();

        private static Dictionary<string, WorldRegion> Build()
        {
            var m = new Dictionary<string, WorldRegion>(StringComparer.OrdinalIgnoreCase);

            Add(m, WorldRegion.NorthAmerica,
                "US CA MX GT BZ SV HN NI CR PA CU DO HT JM PR BS BB TT AG DM GD KN LC VC " +
                "AI AW BM BQ CW KY MS MF BL SX TC VG VI GP MQ PM GL");

            Add(m, WorldRegion.SouthAmerica,
                "BR AR CL CO PE VE EC BO PY UY GY SR GF FK");

            Add(m, WorldRegion.Europe,
                "GB DE FR NL IT ES PL SE NO FI DK IE BE AT CH PT CZ SK HU RO BG GR HR SI " +
                "RS BA ME MK AL LT LV EE IS LU MT CY AD MC SM VA LI FO AX GG JE IM GI " +
                "RU UA BY MD EU");

            Add(m, WorldRegion.Asia,
                "CN JP KR IN SG HK TW TH VN MY ID PH KH LA MM BD PK LK NP BT MV MN KP " +
                "KZ KG TJ TM UZ MO TL BN IO");

            Add(m, WorldRegion.MiddleEast,
                "IL AE SA TR IR IQ JO LB SY KW QA BH OM YE PS GE AM AZ AF");

            Add(m, WorldRegion.Africa,
                "ZA NG EG KE MA DZ TN LY GH ET TZ UG SN CI CM AO MZ ZM ZW BW NA MU RW " +
                "MW ML BF NE TD SD SS SO DJ ER GA CG CD CF GN GW GM SL LR TG BJ BI KM " +
                "MG SC ST CV MR LS SZ RE YT SH EH");

            Add(m, WorldRegion.Oceania,
                "AU NZ FJ PG NC PF SB VU WS TO KI TV NR PW FM MH CK NU TK WF AS GU MP " +
                "NF AQ");

            return m;
        }

        private static void Add(Dictionary<string, WorldRegion> m, WorldRegion r, string codes)
        {
            foreach (string cc in codes.Split(' '))
                if (cc.Length == 2) m[cc] = r;
        }
    }
}
