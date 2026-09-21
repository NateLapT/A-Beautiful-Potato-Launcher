// ---------------------------------------------------------------------------
//  A Beautiful Potato Launcher
//
//  A replacement for DayZ's Experimental launcher, built around the two things
//  the official one cannot do.
//
//  1. SUBSCRIBE TO MODS
//     DayZ Experimental is Steam app 1024020; the workshop content lives under
//     DayZ, app 221100. A launcher running as Experimental can therefore only
//     offer "Load 'X' from library" and show "MANUAL SETUP MAY BE REQUIRED".
//     This one initialises the Steam API as 221100, so subscribing works, and
//     reads each server's required mods - with workshop ids - straight off the
//     server over A2S_RULES.
//
//  2. BROWSE THE WHOLE SERVER LIST
//     The master list comes from ISteamMatchmakingServers - the old UDP master
//     protocol is dead, every hostname now NXDOMAINs. Fetching it takes tens of
//     seconds, so it is fetched once per REFRESH and cached; searching and
//     filtering then run over that cache and are instant. REFRESH does send the
//     current filters to Steam, which is how a narrowed refresh can reach past
//     the 10,000-result cap that app 221100 hits.
//
//     Live player counts and pings are fetched per row, and only for the rows
//     actually on screen - pinging thousands of servers to show twenty would be
//     absurd. The arrow in the first column re-checks a single server.
//
//  THE LAUNCH ITSELF
//     Through DayZ_BE.exe, always. There is no "-BattlEye" parameter; start the
//     game any other way and the server kicks you with
//       Warning (0x000400F0) - BattlEye (Game restart required).
//     The build is chosen from the server's own AppID, so a stable server gets
//     the stable exe and an Experimental server gets the Experimental one.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ABeautifulPotatoLauncher
{
    internal static class Program
    {
        internal static readonly string LogDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
        internal static readonly string LogFile = Path.Combine(LogDirectory, "launcher.log");

        [STAThread]
        private static void Main()
        {
            try { Directory.CreateDirectory(LogDirectory); }
            catch { }

            try
            {
                File.AppendAllText(LogFile,
                    "[" + DateTime.Now.ToString("HH:mm:ss") + "] Launcher started" + Environment.NewLine);
            }
            catch { }

            // A CRASH SHOULD LEAVE EVIDENCE.
            //
            // An unhandled exception on a BACKGROUND thread terminates the
            // process outright - no dialog, no log line, the window simply
            // disappears. That is what a thread-safety bug in the mod sweep did,
            // and it was invisible: nothing in the log, nothing on screen.
            //
            // These handlers cannot make such a bug safe - the process is still
            // going down for a background failure - but they write down what
            // happened first, which is the difference between a fixable report
            // and "it just closed".
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                LogCrash("background thread", e.ExceptionObject as Exception);

            Application.ThreadException += (s, e) =>
            {
                LogCrash("UI thread", e.Exception);
                MessageBox.Show(
                    "Something went wrong:\r\n\r\n" + e.Exception.Message
                    + "\r\n\r\nThe details are in logs\\launcher.log.",
                    "A Beautiful Potato Launcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }

        private static void LogCrash(string where, Exception ex)
        {
            try
            {
                File.AppendAllText(LogFile,
                    "[" + DateTime.Now.ToString("HH:mm:ss") + "] UNHANDLED on the " + where + ": "
                    + (ex == null ? "(no exception object)" : ex.ToString())
                    + Environment.NewLine + Environment.NewLine);
            }
            catch { }
        }
    }

    /// <summary>
    /// The numbers are persisted, so a new tab is appended rather than slotted
    /// in where it appears on screen - Official sits FIRST in the bar but last
    /// here, and the two orders are deliberately independent.
    /// </summary>
    internal enum Tab { Community = 0, Recent = 1, Friends = 2, Lan = 3, Favourites = 4, Official = 5 }

    /// <summary>A server the fake-detection rules rejected, kept so Settings can show it.</summary>
    internal sealed class FlaggedServer
    {
        public string Name = "";
        public string Host = "";
        public int Port;
        public string Reason = "";
        public string Endpoint { get { return Host + ":" + Port; } }
    }

    /// <summary>One display row, from either the saved list or the master list.</summary>
    internal sealed class Row
    {
        public string Name = "";
        public string Map = "";
        public string Host = "";
        public int Port;

        /// <summary>
        /// The port to send queries to, as reported by Steam. Zero means it was
        /// never reported, in which case game port + 1 is the best guess - but
        /// many hosts do not follow that, which is why this is carried at all.
        /// </summary>
        public int QueryPort;
        public int EffectiveQueryPort { get { return A2S.Effective(Port, QueryPort); } }
        public int Players, MaxPlayers, Ping = -1;

        /// <summary>The server asks for a password before it will let you in.</summary>
        public bool Password;

        /// <summary>
        /// Players waiting to join. Read from this row's OWN tags rather than
        /// stored, so it stays right when a live query refreshes them.
        /// See BrowserServer.Queue for where DayZ hides it.
        /// </summary>
        public int Queue
        {
            get
            {
                if (string.IsNullOrEmpty(Tags)) return 0;
                foreach (var part in Tags.Split(','))
                {
                    string t = part.Trim();
                    if (t.Length <= 3) continue;
                    if (!t.StartsWith("lqs", StringComparison.OrdinalIgnoreCase)) continue;
                    int v;
                    if (int.TryParse(t.Substring(3), out v) && v >= 0) return v;
                }
                return 0;
            }
        }
        // ulong to match ServerInfo/A2S; BrowserServer's uint widens into it.
        public ulong AppId;
        public string Tags = "";

        /// <summary>
        /// Saved by the player, and therefore pinned above everything else in
        /// the browsable lists. Stamped once when the rows are built rather
        /// than looked up inside the sort: a comparison runs O(n log n) times,
        /// and on a five thousand server list that is a hundred thousand
        /// needless hash lookups.
        /// </summary>
        public bool Favourite;

        /// <summary>
        /// Tri-state on purpose: a server in the master list has not been asked
        /// anything yet, which is not the same as being offline.
        /// </summary>
        public bool? Online;
        public bool Offline { get { return Online.HasValue && !Online.Value; } }

        /// <summary>
        /// Two-letter country code for the server's address, worked out from
        /// the registry table rather than asked of anyone. Cached per row
        /// because a list of twelve thousand asks for this on every repaint.
        /// </summary>
        private string _country;

        public string Country
        {
            get
            {
                if (_country != null) return _country;

                // One shared rule - name first, then address. See
                // IpRegion.CountryOf.
                _country = IpRegion.CountryOf(Name, Host);

                // "??" rather than a blank when the address is in no registry
                // allocation - a hostname, a private range, or a block nobody
                // has claimed. An empty cell reads like "nobody looked"; two
                // question marks say we looked and could not tell.
                if (_country.Length == 0) _country = "??";

                return _country;
            }
        }

        public WorldRegion Region { get { return IpRegion.RegionOf(Country); } }

        // (Country already applied the name-before-address rule, so this does
        // not need to repeat it.)

        /// <summary>
        /// Set when this row is a heading rather than a server - the notice
        /// that separates the configured servers from the ones still carrying
        /// their host's stock name. Headings are never queried, never selected
        /// and never counted.
        /// </summary>
        public string Heading;

        public bool IsHeading { get { return Heading != null; } }
        public string Error = "";

        public string StatusText
        {
            get { return !Online.HasValue ? "" : Online.Value ? "Online" : "Offline"; }
        }

        public string Endpoint { get { return Host + ":" + Port; } }
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

    internal sealed class Segmented : Panel
    {
        private readonly Button[] _buttons;
        public event Action Changed;

        public int SelectedIndex
        {
            get { return _selectedIndex; }
            set { SetSelected(value); }
        }

        private int _selectedIndex;

        public Segmented(IEnumerable<string> labels, Point location)
        {
            Location = location;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            BorderStyle = BorderStyle.None;
            BackColor = Color.Transparent;

            var items = labels.ToArray();
            _buttons = new Button[items.Length];
            int x = 0;
            for (int i = 0; i < items.Length; i++)
            {
                var btn = new Button
                {
                    Text = items[i],
                    Tag = i,
                    FlatStyle = FlatStyle.Flat,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    Margin = new Padding(0),
                    Padding = new Padding(8, 2, 8, 2),
                    Location = new Point(x, 0),
                    TabStop = false,
                    BackColor = Color.FromArgb(40, 40, 46),
                    ForeColor = Color.FromArgb(220, 220, 220),
                    Font = new Font("Segoe UI", 9F)
                };
                btn.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 88);
                btn.FlatAppearance.BorderSize = 1;
                btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(60, 60, 66);
                btn.FlatAppearance.CheckedBackColor = Color.FromArgb(80, 80, 92);
                btn.Click += (s, e) => SetSelected((int)((Button)s).Tag);
                Controls.Add(btn);
                _buttons[i] = btn;
                x += btn.Width + 3;
            }

            SetSelected(0);
        }

        public void AddTo(Control parent)
        {
            parent.Controls.Add(this);
        }

        private void SetSelected(int index)
        {
            if (_buttons.Length == 0) return;
            if (index < 0) index = 0;
            if (index >= _buttons.Length) index = _buttons.Length - 1;

            bool changed = _selectedIndex != index;
            _selectedIndex = index;
            for (int i = 0; i < _buttons.Length; i++)
            {
                bool active = i == index;
                _buttons[i].BackColor = active ? Color.FromArgb(90, 120, 160) : Color.FromArgb(40, 40, 46);
                _buttons[i].ForeColor = active ? Color.White : Color.FromArgb(220, 220, 220);
                _buttons[i].FlatAppearance.BorderColor = active ? Color.FromArgb(120, 150, 190) : Color.FromArgb(80, 80, 88);
            }

            if (changed) Changed?.Invoke();
        }
    }

    internal sealed class DarkMenu : ProfessionalColorTable
    {
        private static readonly Color Back = Color.FromArgb(38, 38, 42);
        private static readonly Color Hot = Color.FromArgb(62, 62, 70);
        private static readonly Color Line = Color.FromArgb(78, 78, 86);

        public override Color ToolStripDropDownBackground { get { return Back; } }
        public override Color MenuItemSelected { get { return Hot; } }
        public override Color MenuItemSelectedGradientBegin { get { return Hot; } }
        public override Color MenuItemSelectedGradientEnd { get { return Hot; } }
        public override Color MenuItemPressedGradientBegin { get { return Hot; } }
        public override Color MenuItemPressedGradientEnd { get { return Hot; } }
        public override Color MenuItemBorder { get { return Line; } }
        public override Color MenuBorder { get { return Line; } }
        public override Color ImageMarginGradientBegin { get { return Back; } }
        public override Color ImageMarginGradientMiddle { get { return Back; } }
        public override Color ImageMarginGradientEnd { get { return Back; } }
        public override Color SeparatorDark { get { return Line; } }
        public override Color SeparatorLight { get { return Back; } }
    }

    internal sealed class MainForm : Form
    {
        // Column indices. The first column is a clickable refresh glyph - a
        // ListView cannot host real buttons, so the cell is hit-tested instead.
        private const int ColRefresh = 0, ColStar = 1, ColName = 2, ColGame = 3,
                          ColStatus = 4, ColMap = 5, ColCountry = 6, ColPlayers = 7,
                          ColTime = 8, ColPing = 9, ColMods = 10, ColPassword = 11,
                          ColAddress = 12;

        // Row colours, kept in one place so the list reads consistently:
        //   normal   - listed, and either answered or not yet asked
        //   offline  - did not answer its A2S query, so you cannot join it
        // Favourites are shown by the star, NOT by tinting the row, because
        // tinting made "favourite" and "offline" compete for the same signal.
        private static readonly Color RowNormal  = Color.Gainsboro;

        // Alternating row backgrounds, a couple of shades apart so the banding
        // guides the eye across a wide row without lifting off the dark theme.
        private static readonly Color RowBandA = Color.FromArgb(28, 28, 30);
        private static readonly Color RowBandB = Color.FromArgb(34, 34, 38);
        private static readonly Color RowOffline = Color.FromArgb(150, 96, 96);
        private static readonly Color StatusOn   = Color.FromArgb(120, 200, 120);
        private static readonly Color StatusOff  = Color.FromArgb(205, 105, 105);
        private const string RefreshGlyph = "\u21BB";
        private const string StarOn = "\u2605";     // filled - a favourite
        private const string StarOff = "\u2606";    // hollow - click to save

        // Mod panel columns; the last three are clickable actions.
        private const int MColName = 0, MColId = 1, MColStatus = 2,
                          MColRepair = 3, MColSub = 4, MColRemove = 5, MColInfo = 6;

        private const string HomeUrl = "https://Launcher.BeautifulPotato.com";
        private const string DonateUrl = "https://ko-fi.com/nate_lapt";

        private const string GameExe = "DayZ_x64.exe";
        private const string BeExe   = "DayZ_BE.exe";
        private const string BeArgs  = "2 1 0";

        private static readonly string[] ExpFolders =
        {
            "DayZ Exp", "DayZ Exp129", "DayZExp", "DayZ Experimental"
        };
        private static readonly string[] StableFolders = { "DayZ" };

        private static readonly Color Ink    = Color.FromArgb(18, 18, 20);
        private static readonly Color Panel  = Color.FromArgb(28, 28, 30);
        private static readonly Color Panel2 = Color.FromArgb(38, 38, 42);
        private static readonly Color Accent = Color.FromArgb(178, 34, 34);
        private static readonly Color Dim    = Color.FromArgb(150, 150, 155);
        private static readonly Color Good   = Color.FromArgb(140, 200, 140);

        // Max player threshold; DayZ rarely exceeds 120 slots without severe degradation.
        /// <summary>
        /// Above this, a claimed slot count is not believable.
        ///
        /// MEASURED 2026-09-20 across 12,357 cached servers. The old value of
        /// 120 was hiding 6,304 of them - HALF THE LIST - and 5,604 of those
        /// were reporting exactly 127. That is not a claim, it is 0x7F, the
        /// largest signed byte, which is what the field reads when a server
        /// does not report a real figure. Spot-checking those names turns up
        /// ordinary community servers - "Lois Pizza", "FlatLine PVP",
        /// "ANDROMEDA : Fresh Wipe - PvP" - not a farm.
        /// </summary>
        private const int MaxRealisticSlots = 127;

        /// <summary>
        /// Slot counts that are byte limits rather than real numbers: 127 is
        /// 0x7F and 255 is 0xFF.
        ///
        /// A server reporting one of these has told us nothing about its
        /// capacity - but "nothing" is itself worth something here, because
        /// fake servers overwhelmingly report 127 while real ones state a real
        /// figure. So this is a SIGNAL, weighed together with how crowded the
        /// address is, never a verdict on its own. See SentinelOnAFarm.
        /// </summary>
        private static bool SlotsUnreported(int slots)
        {
            return slots == 127 || slots == 255;
        }

        /// <summary>
        /// A sentinel slot count on an address that is running a crowd.
        ///
        /// WHY BOTH, AND NOT EITHER
        ///   MEASURED 2026-09-20 over 12,357 cached servers. Of the 5,604
        ///   reporting exactly 127, **94% sit on an address hosting 50 or more
        ///   servers**, against 17% of everything else. The worst offenders are
        ///   unanimous: 31.77.188.20 runs 860 servers and all 860 report 127;
        ///   31.77.188.21 runs 844 and so do all of those.
        ///
        ///   But only 75 of the 5,604 are alone on their address, and those
        ///   read as ordinary servers - "Lois Pizza", "FlatLine PVP",
        ///   "Project Civilization | Frontier". Hiding every 127 threw those
        ///   away; hiding none of them let 5,373 farm entries back in. Neither
        ///   signal decides alone, so both are required.
        ///
        /// THE THRESHOLD
        ///   A legitimate host really does run many servers on one address -
        ///   this player's own host runs 18, every one of them stating a true
        ///   capacity between 10 and 120 and never a sentinel. 20 clears that
        ///   with room to spare while still catching 5,613 farm entries.
        /// </summary>
        /// <summary>
        /// The slot count is evidence this server is not real - either an
        /// outright impossible figure, or a sentinel backed up by a crowded
        /// address.
        /// </summary>
        private bool ImpossibleCapacity(BrowserServer s)
        {
            if (s == null) return false;

            // 255 IS ALWAYS A FAKE. No corroboration, no exceptions.
            //
            // 0xFF is what a fabricated entry puts in the byte. Of the 272
            // servers reporting it, 242 also claim zero players - a server that
            // is simultaneously enormous and deserted. No real DayZ server runs
            // 255 slots; the engine cannot usefully host them.
            if (s.MaxPlayers == 255) return true;

            if (SlotsUnreported(s.MaxPlayers)) return SentinelIsFake(s);

            return s.MaxPlayers > MaxRealisticSlots;
        }

        /// <summary>
        /// Whether a 127-slot server is one of the fakes.
        ///
        /// 127 alone is not proof - a handful of real servers report it - so
        /// each test below needs a second thing to be true as well. Together
        /// they caught every address in a reported imposter network while
        /// leaving that network's REAL servers untouched.
        /// </summary>
        private bool SentinelIsFake(BrowserServer s)
        {
            // 1. CLAIMS TO BE EXACTLY FULL AT THE SENTINEL.
            //
            //    "127/127" is a fabricated entry advertising itself as busy: it
            //    copied the capacity into the player count. Real servers do sit
            //    exactly full - but at a real capacity, 55/55 or 90/90 - and
            //    those are untouched because the capacity is not a sentinel.
            if (s.Players == s.MaxPlayers) return true;

            int onThisAddress;
            if (!_serversPerIp.TryGetValue(s.Host ?? "", out onThisAddress)) return false;

            // 2. EVERY SERVER ON THE ADDRESS REPORTS A SENTINEL.
            //
            //    This is the one that catches the small farms. A genuine host
            //    running eighteen servers states eighteen real capacities; an
            //    address whose entire population reports 127 is not a host, it
            //    is a generator - and it works whether it made two entries or
            //    eight hundred.
            if (onThisAddress >= 2 && _sentinelOnlyIps.Contains(s.Host)) return true;

            // 3. A CROWD, whatever the individual entries say.
            return onThisAddress >= SentinelFarmSize;
        }

        /// <summary>
        /// Addresses hosting two or more servers of which every single one
        /// reports a sentinel capacity. Rebuilt with the farm scan.
        /// </summary>
        private readonly HashSet<string> _sentinelOnlyIps =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>How many distinct addresses each exact server name appears on.</summary>
        private readonly Dictionary<string, int> _addressesPerName =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>A name on this many addresses is being impersonated by someone.</summary>
        private const int ImpersonationAddresses = 3;

        /// <summary>
        /// Addresses where the other rules already condemned most of what is
        /// running there. Filled at the END of the farm scan, so it is one pass
        /// behind and can never feed itself.
        /// </summary>
        private readonly HashSet<string> _mostlyFakeIps =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private const int MostlyFakeMinServers = 5;
        private const int MostlyFakePercent = 60;

        /// <summary>
        /// A copy of somebody else's server name, rather than the original.
        ///
        /// WHY A TIEBREAKER IS NEEDED
        ///   When "KarmaKrew Chernarus #1 EU" turns up on thirty addresses, one
        ///   of them IS that server. Hiding all thirty would hide the very
        ///   server the player was looking for - so a duplicated name only
        ///   condemns a copy that ALSO looks fabricated.
        ///
        /// WHAT THE REAL ONE LOOKS LIKE
        ///   Measured across the nine genuine addresses of one impersonated
        ///   network: every one states a true capacity (55, 70, 90, 115 - never
        ///   a sentinel) and hosts one or two servers. Every surviving imposter
        ///   either reported 127, or sat on an address running ten or more.
        ///   Those two tests separated 40 imposters from 11 real servers
        ///   without a single mistake in either direction.
        /// </summary>
        private bool ImpersonatedCopy(BrowserServer s)
        {
            if (s == null || string.IsNullOrWhiteSpace(s.Name)) return false;

            int addresses;
            if (!_addressesPerName.TryGetValue(s.Name.Trim(), out addresses)) return false;
            if (addresses < ImpersonationAddresses) return false;

            // Fabricated entries do not know the real capacity.
            if (SlotsUnreported(s.MaxPlayers)) return true;

            // The genuine server is not one of a crowd. A real group runs a
            // couple of machines per address; a generator runs dozens.
            int onThisAddress;
            if (_serversPerIp.TryGetValue(s.Host ?? "", out onThisAddress)
                && onThisAddress >= ImpersonationCrowd) return true;

            return false;
        }

        /// <summary>
        /// Servers on one address before a duplicated name counts against it.
        /// Ten, because the genuine addresses measured ran one or two and the
        /// imposters ran eleven or more.
        /// </summary>
        private const int ImpersonationCrowd = 10;

        /// <summary>Servers on one address before a sentinel slot count condemns it.</summary>
        private const int SentinelFarmSize = 20;

        /// <summary>How many servers each address is running, rebuilt with the farm scan.</summary>
        private readonly Dictionary<string, int> _serversPerIp =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // ---- state ----
        private Tab _tab = Tab.Community;
        private readonly HashSet<string> _favourites;
        private readonly HashSet<string> _allowed;
        private List<BrowserServer> _browser = new List<BrowserServer>();
        private readonly BrowserFilters _filters = new BrowserFilters();

        private readonly Dictionary<string, List<BrowserServer>> _caches =
            new Dictionary<string, List<BrowserServer>>();
        private readonly Dictionary<string, DateTime> _cacheTimes =
            new Dictionary<string, DateTime>();

        private string CacheKey
        {
            get
            {
                return _tab + "/" + EffectiveBrowseMode;
            }
        }

        private readonly Dictionary<string, long> _lastSeen = new Dictionary<string, long>();

        private List<BrowserServer> _cache
        {
            get
            {
                List<BrowserServer> c;
                if (_caches.TryGetValue(CacheKey, out c)) return c;

                c = ServerStore.LoadList(CacheKey, _lastSeen);
                _caches[CacheKey] = c;
                if (c.Count > 0)
                {
                    _cacheTimes[CacheKey] = ServerStore.ListSavedAt(CacheKey);
                }
                return c;
            }
        }

        private readonly System.Windows.Forms.Timer _visTimer = new System.Windows.Forms.Timer();
        private int _lastTopIndex = -1;
        private int _lastRawCount = -1;
        private int _stableTicks;
        private int _lastShownCount;
        private string _lastSteamFilterKey = "";

        private List<Row> _rows = new List<Row>();
        private string _selectedEndpoint;
        private readonly Dictionary<string, int> _rowIndex =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private readonly HashSet<string> _farmIps =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _farmSubnets =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int _hiddenFakes;

        private int _sortColumn = -1;
        private bool _sortAscending = true;

        internal readonly List<FlaggedServer> Flagged = new List<FlaggedServer>();

        private readonly HashSet<string> _asked = new HashSet<string>();
        private readonly object _modLock = new object();
        /// <summary>
        /// Which server the mod panel is currently displaying, and when that
        /// data was gathered.
        ///
        /// These exist to stop the panel rebuilding itself over and over. The
        /// server list re-renders constantly - every ping that comes back
        /// updates a row - and each render re-applies the selection, which
        /// raises SelectedIndexChanged, which used to rebuild the mod list from
        /// scratch. The rules were cached so nothing was re-queried, but the
        /// ListView was still cleared and refilled each time, which is exactly
        /// what the flicker was.
        /// </summary>
        /// <summary>
        /// True while SetRows is swapping the list contents.
        ///
        /// Rebuilding a virtual ListView means clearing the selection and
        /// re-applying it, which raises SelectedIndexChanged TWICE - once with
        /// nothing selected. Acting on that first event tore the mod panel down
        /// (and forgot which server it was showing), and the second rebuilt it,
        /// so the panel visibly cleared and refilled every time a ping came
        /// back. The selection did not actually change; only the list object
        /// underneath it did.
        /// </summary>
        private bool _rebuildingList;

        /// <summary>
        /// While a fetch is running the list APPENDS rather than re-sorts.
        ///
        /// Sorting on every batch is what made the list impossible to read
        /// while it filled: a row the player was reaching for kept moving as
        /// servers arrived and the order was recomputed underneath them. New
        /// arrivals now go on the end, so everything already on screen keeps
        /// its position, and the player re-sorts when they are ready with
        /// RESORT LIST or a column header.
        /// </summary>
        private bool _appendWhileLoading;

        private string _modsShownFor;
        private DateTime _modsShownAt;

        private readonly Dictionary<string, ServerRules> _modCache =
            new Dictionary<string, ServerRules>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _modLoading =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ServerInfo> _live =
            new Dictionary<string, ServerInfo>(StringComparer.OrdinalIgnoreCase);

        private const int PingWorkers = 16;
        private readonly object _pingLock = new object();
        private readonly List<Row> _pingWanted = new List<Row>();
        private List<Row> _pingAll = new List<Row>();
        private int _sweep;
        private int _checked;
        private readonly HashSet<string> _pingBusy =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private Thread[] _pingPool;
        private volatile bool _closing = false;

        private readonly System.Windows.Forms.Timer _pollTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer _typeTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer _modRecheck = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer _saveTimer = new System.Windows.Forms.Timer();

        /// <summary>Sorts the list once the sweep stops turning up dead servers.</summary>
        private readonly System.Windows.Forms.Timer _idleSort = new System.Windows.Forms.Timer();

        /// <summary>Refreshes the view while mod lists are being collected.</summary>
        private readonly System.Windows.Forms.Timer _modSweepTimer = new System.Windows.Forms.Timer();

        /// <summary>Holds the mod sweep back until the window has settled.</summary>
        private readonly System.Windows.Forms.Timer _modIndexStart = new System.Windows.Forms.Timer();

        /// <summary>True while servers are still waiting to be queried.</summary>
        private bool PingsOutstanding
        {
            get { lock (_pingLock) return _pingBusy.Count > 0 || _sweep < _pingAll.Count; }
        }
        private string _browseMode = ServerStore.LoadBrowseMode();

        private uint[] AppsForQuery
        {
            get
            {
                string mode = EffectiveBrowseMode;
                if (mode == "all")
                    return new[] { (uint)A2S.StableAppId, (uint)A2S.ExperimentalAppId };
                return new[] { mode == "exp"
                             ? (uint)A2S.ExperimentalAppId : (uint)A2S.StableAppId };
            }
        }

        private static bool TabForcesAllBuilds(Tab t)
        {
            return t == Tab.Recent || t == Tab.Friends || t == Tab.Lan;
        }

        private static bool TabPicksBuild(Tab t)
        {
            return t == Tab.Community || t == Tab.Official;
        }

        private string EffectiveBrowseMode
        {
            get { return TabForcesAllBuilds(_tab) ? "all" : _browseMode; }
        }
        private bool _steamReady;

        // ---- controls ----
        private ListView _list, _mods;

        /// <summary>Narrows the mod panel to the mods being looked for.</summary>
        private ChipInput _modFind;
        private RichTextBox _desc;
        private Label _descHeader;
        private TextBox _name, _log, _search;
        private Label _status, _modsHeader, _searchHint, _searchClear;
        private Button _connect, _refresh, _filterToggle, _favBtn;
        private Panel _filterPanel;
        private readonly Dictionary<Tab, Button> _tabs = new Dictionary<Tab, Button>();
        private ComboBox _cbBuild;

        /// <summary>The in-game clock range, in six hour steps.</summary>
        private RangeSlider _timeRange;

        /// <summary>Holds the game mode toggles, on the search row.</summary>
        private Panel _modesBar;

        /// <summary>Keeps the filter dropdowns stocked while the panel is open.</summary>
        private readonly System.Windows.Forms.Timer _choicesTimer = new System.Windows.Forms.Timer();

        /// <summary>The player-count range, with a grip at each end.</summary>
        private RangeSlider _playerRange;
        private TextBox _fName, _fAddr, _fPing;

        /// <summary>
        /// Map filter. A ComboBox rather than a text box so the player can see
        /// what maps actually exist - there are dozens and nobody remembers how
        /// "deerisle" is spelt - while still being able to type to narrow it.
        /// </summary>
        private ComboBox _fMap;

        /// <summary>Country codes to include, or exclude with a leading minus.</summary>
        private ComboBox _fCountry;

        /// <summary>Mod picker. _fMod is its typing box, kept for the dropdown fill.</summary>
        private ChipInput _modFilter;
        private ComboBox _fMod;
        private CheckBox _chkNoPass, _chkHideFull, _chkHideEmpty, _chkHideFakes;
        private Segmented _segThird, _segMods;

        private Row SelectedRow
        {
            get
            {
                if (_list.SelectedIndices.Count == 0) return null;
                int idx = _list.SelectedIndices[0];
                if (idx < 0 || idx >= _rows.Count) return null;

                // Clicking the notice selects nothing. It is a label that
                // happens to live in the list, not a server, and letting it
                // through would have the mod panel query an empty address.
                return _rows[idx].IsHeading ? null : _rows[idx];
            }
        }

        public MainForm()
        {
            Text = "A Beautiful Potato Launcher";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1180, 800);
            MinimumSize = new Size(1020, 680);
            RestoreWindow();

            ResizeEnd += (s, e) => RememberWindow();
            BackColor = Ink;
            ForeColor = Color.Gainsboro;
            Font = new Font("Segoe UI", 9f);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            _favourites = ServerStore.LoadFavourites();
            _allowed = ServerStore.LoadAllowed();
            int savedTab = ServerStore.LoadTab();
            _tab = Enum.IsDefined(typeof(Tab), savedTab) ? (Tab)savedTab : Tab.Community;

            BuildUi();

            // Every clickable thing gets the hand cursor; see UiCursors.
            ListViewTweaks.Smooth(_list);
            _filters.KnownMods = KnownModsFor;
            _filters.KnownDescription = KnownDescriptionFor;
            PaintFilterToggle();
            UiCursors.ApplyTo(this);

            _pollTimer.Interval = InternetPollMs;
            _pollTimer.Tick += OnPoll;

            _visTimer.Interval = 500;
            _visTimer.Tick += OnVisTick;

            _typeTimer.Interval = 220;
            _typeTimer.Tick += (s, e) => { _typeTimer.Stop(); ApplyFilters(); };

            _autoSearchTimer.Interval = AutoSearchAfterMs;
            _autoSearchTimer.Tick += (s, e) => { _autoSearchTimer.Stop(); RunSteamSearch(false); };

            // Selecting a row asks the SERVER for its mod list, which is a
            // network round trip. While a player is typing the selection moves
            // with every redraw, so firing that immediately meant a query per
            // keystroke and an input box that lagged behind the keyboard. The
            // mod panel now waits until typing has stopped.
            _modsDelay.Interval = ModsAfterTypingMs;
            _modsDelay.Tick += (s, e) => { _modsDelay.Stop(); _typing = false; ShowMods(); };

            _modRecheck.Interval = 2500;
            // After Repair / Sub / Remove the STATUS of each mod has changed on
            // disk, but the server's list has not - so the rows are rebuilt from
            // what was already fetched rather than querying the server again.
            _modRecheck.Tick += (s, e) => { _modRecheck.Stop(); RepopulateModsFromCache(); };

            // Whether a server is up is only learnt when its reply arrives -
            // long after the list was sorted. Without this, a server found dead
            // sat wherever it happened to be and never sank. The delay lets a
            // burst of replies settle so the list is not reshuffled per packet.
            // The index is written back to disk while a long build runs, not
            // only when it finishes. A build takes minutes and closing the
            // launcher half way through used to throw away everything found
            // since the last completed sweep.
            _saveTimer.Interval = 30000;
            _saveTimer.Tick += (s, e) => { SaveIndex(); SaveServerMods(); };
            _saveTimer.Start();

            // Restarted by each dead server found, so it fires only once they
            // stop coming - the quiet moment to let the list settle.
            _idleSort.Interval = 4000;
            _idleSort.Tick += (s, e) =>
            {
                _idleSort.Stop();
                if (PingsOutstanding) { _idleSort.Start(); return; }
                ResortNow(true);
            };

            // While mod lists are being read the set of matching servers grows,
            // so re-render on a slow tick and let the player watch it fill.
            // Gives the list, the first render and the visible-row pings a
            // clear run before any of this starts.
            _modIndexStart.Interval = 6000;
            _modIndexStart.Tick += (s, e) =>
            {
                _modIndexStart.Stop();
                BeginBackgroundModIndex();
            };
            _modIndexStart.Start();

            // Polls for downloads started from the mod panel. Two seconds is
            // frequent enough to feel immediate and rare enough to cost
            // nothing; it stops as soon as everything has landed.
            _downloadWatch.Interval = 2000;
            _downloadWatch.Tick += (s, e) =>
            {
                _downloadWatchTicks++;

                string steam = FindSteam();
                if (steam == null) { _downloadWatch.Stop(); _awaitingDownload.Clear(); return; }

                var landed = new List<ulong>();
                foreach (ulong id in _awaitingDownload)
                {
                    try
                    {
                        if (SteamWorkshop.IsInstalled(steam, id)
                            && !SteamWorkshop.NeedsUpdate(steam, id)) landed.Add(id);
                    }
                    catch { }
                }

                foreach (ulong id in landed) _awaitingDownload.Remove(id);

                if (landed.Count > 0)
                {
                    Log("  " + landed.Count + " mod(s) finished downloading.");
                    RefreshInstalledState();
                }

                // Give up after ten minutes rather than polling forever: a
                // download that has not finished by then has stalled, and the
                // player can press REFRESH.
                if (_awaitingDownload.Count == 0 || _downloadWatchTicks > 300)
                {
                    _downloadWatch.Stop();
                    _awaitingDownload.Clear();
                }
            };

            // While the panel is open the choices go stale as servers arrive,
            // so they are topped up on a slow tick. Doing it here rather than
            // on DropDown is the whole point: the work never coincides with the
            // list being opened.
            _choicesTimer.Interval = 4000;
            _choicesTimer.Tick += (s, e) =>
            {
                if (_filterPanel == null || !_filterPanel.Visible) return;
                RefreshFilterChoices();
            };
            _choicesTimer.Start();

            _modSweepTimer.Interval = 3000;
            _modSweepTimer.Tick += (s, e) =>
            {
                SaveServerMods();

                // RE-RENDER ONLY WHEN THE ANSWER CHANGED.
                //
                // A render costs about 165 ms of RecomputeFarms plus the row
                // building, on the UI thread. Doing that every three seconds
                // whether or not a single new server matched is what made the
                // whole window feel sticky. Counting the matches first is cheap
                // by comparison, and most ticks now do nothing at all.
                if (_filters.RequiredMods.Count > 0)
                {
                    // Re-render only when a newly-read server actually matched.
                    //
                    // Counting the matches to find out cost 94 ms a tick, which
                    // is the same problem in a different place. The worker that
                    // reads a mod list already has that one list in its hands,
                    // so it tests it there - one server, not seventeen thousand
                    // - and sets this. Most ticks now do nothing.
                    bool changed;
                    lock (_modListLock)
                    {
                        changed = _newModMatches;
                        _newModMatches = false;
                    }

                    if (changed)
                    {
                        if (IsSteamTab(_tab)) RenderFromCache(); else RefreshMine();
                    }
                    else UpdateStatus(null);
                }

                if (!PingsOutstanding) _modSweepTimer.Stop();
            };

            RefreshCurrent();
        }

        // ------------------------------------------------------------- ui --
        private void BuildUi()
        {
            var rail = new Panel { Dock = DockStyle.Left, Width = 210, BackColor = Panel };
            Controls.Add(rail);
            BuildRail(rail);

            var bottomBar = new Panel { Dock = DockStyle.Bottom, Height = 28, BackColor = Panel };
            Controls.Add(bottomBar);

            var settings = new Button
            {
                Text = "Settings",
                Dock = DockStyle.Right,
                Width = 88,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(52, 52, 58),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            settings.FlatAppearance.BorderColor = Color.FromArgb(78, 78, 86);
            settings.Click += OnSettings;
            bottomBar.Controls.Add(settings);

            var donate = new Button
            {
                Text = "♥ Donate",
                Dock = DockStyle.Right,
                Width = 110,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(86, 48, 62),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            donate.FlatAppearance.BorderColor = Color.FromArgb(120, 70, 88);
            donate.Click += (s, e) => OpenLink(DonateUrl);
            new ToolTip().SetToolTip(donate, DonateUrl);
            bottomBar.Controls.Add(donate);

            _status = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(10, 0, 0, 0),
                BackColor = Panel,
                ForeColor = Good,
                Text = "Starting up..."
            };
            bottomBar.Controls.Add(_status);
            bottomBar.Controls.SetChildIndex(_status, 0);

            var main = new Panel { Dock = DockStyle.Fill, BackColor = Ink, Padding = new Padding(12) };
            Controls.Add(main);
            main.BringToFront();

            // 34 for the tabs, then a search row tall enough for two rows of
            // game mode buttons beside the search box.
            // 54 for the tabs and the game modes beside them, then the search
            // row underneath.
            var top = new Panel { Dock = DockStyle.Top, Height = 90, BackColor = Ink };
            main.Controls.Add(top);

            var searchRow = new Panel { Dock = DockStyle.Fill, BackColor = Ink };
            top.Controls.Add(searchRow);

            var tabBar = new Panel { Dock = DockStyle.Top, Height = 54, BackColor = Ink };
            top.Controls.Add(tabBar);

            top.Controls.SetChildIndex(searchRow, 0);
            top.Controls.SetChildIndex(tabBar, 1);

            var topRight = new Panel { Dock = DockStyle.Right, Width = 0, BackColor = Ink };
            searchRow.Controls.Add(topRight);

            var searchArea = new Panel { Dock = DockStyle.Fill, BackColor = Ink };
            searchRow.Controls.Add(searchArea);
            searchRow.Controls.SetChildIndex(searchArea, 0);
            searchRow.Controls.SetChildIndex(topRight, 1);

            int x = 0;
            foreach (var t in new[] { Tab.Official, Tab.Community, Tab.Recent,
                                      Tab.Friends, Tab.Lan, Tab.Favourites })
            {
                var b = new Button
                {
                    Text = TabName(t),
                    Bounds = new Rectangle(x, 0, 96, 30),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Panel2,
                    ForeColor = Color.White,
                    Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                    Tag = t
                };
                b.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 76);
                b.Click += OnTabClick;
                tabBar.Controls.Add(b);
                _tabs[t] = b;
                x += 99;
            }

            // The game modes sit level with the tabs, to their right. They are
            // used constantly and belong with the other things that change what
            // the list shows, not buried under the search box.
            _modesBar = new Panel { BackColor = Ink, Bounds = new Rectangle(x + 16, 1, 600, 50) };
            tabBar.Controls.Add(_modesBar);
            BuildGameModeButtons(_modesBar, 0, 0);

            tabBar.Resize += (s2, e2) =>
            {
                int room = Math.Max(0, tabBar.ClientSize.Width - _modesBar.Left - 8);
                _modesBar.Width = room;
            };

            _search = new TextBox
            {
                Bounds = new Rectangle(10, 4, 200, 23),
                BackColor = Panel2,
                ForeColor = Color.Gainsboro,
                BorderStyle = BorderStyle.FixedSingle
            };
            _search.KeyDown += OnSearchKey;
            searchArea.Controls.Add(_search);

            _searchClear = new Label
            {
                Text = "✕",
                Bounds = new Rectangle(0, 6, 18, 19),
                ForeColor = Color.FromArgb(150, 150, 158),
                BackColor = Panel2,
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand,
                Tag = "clickable",
                Visible = false
            };
            _searchClear.Click += (s, e) =>
            {
                _search.Text = "";
                _search.Focus();
                ApplyFilters();
            };
            _searchClear.MouseEnter += (s, e) => _searchClear.ForeColor = Color.White;
            _searchClear.MouseLeave += (s, e) => _searchClear.ForeColor = Color.FromArgb(150, 150, 158);
            searchArea.Controls.Add(_searchClear);
            _searchClear.BringToFront();

            _searchHint = new Label
            {
                Text = "  Search servers  (Ctrl+F)",
                Bounds = new Rectangle(11, 5, 198, 21),
                ForeColor = Color.FromArgb(105, 105, 112),
                BackColor = Panel2,
                TextAlign = ContentAlignment.MiddleLeft
            };
            _searchHint.Click += (s, e) => _search.Focus();
            _search.GotFocus += (s, e) => _searchHint.Visible = false;
            _search.LostFocus += (s, e) => _searchHint.Visible = _search.Text.Length == 0;
            _search.TextChanged += (s, e) =>
            {
                bool empty = _search.Text.Length == 0;
                _searchHint.Visible = empty;
                _searchClear.Visible = !empty;

                _typeTimer.Stop();
                _typeTimer.Start();
            };
            searchArea.Controls.Add(_searchHint);
            _searchHint.BringToFront();

            _filterToggle = MakeBtn("FILTERS", new Rectangle(4, 4, 92, 25), Panel2);
            _filterToggle.Click += (s, e) =>
            {
                _filterPanel.Visible = !_filterPanel.Visible;
                PaintFilterToggle();

                // Filled on the way in, so the lists are ready before the first
                // click rather than being built by it. If the mod index has not
                // been read yet, that is brought forward too - opening Filters
                // is a clear signal the player is about to want it, and waiting
                // out the startup delay would show them an empty list.
                if (_filterPanel.Visible)
                {
                    WarmModIndexNow();
                    RefreshFilterChoices();
                }

                Log("Filter panel " + (_filterPanel.Visible ? "shown." : "hidden."));
            };
            searchArea.Controls.Add(_filterToggle);

            var apply = MakeBtn("SEARCH", new Rectangle(102, 4, 92, 25), Color.FromArgb(60, 95, 60));
            apply.Click += (s, e) => RunSteamSearch(true);
            searchArea.Controls.Add(apply);



            EventHandler layoutSearch = (s, e) =>
            {
                const int MaxSearchWidth = 470;
                const int ButtonGroupWidth = 200;
                const int Gap = 10;
                int textWidth = Math.Min(Math.Max(240, searchArea.ClientSize.Width - ButtonGroupWidth - Gap - 20), MaxSearchWidth);
                int startX = 10;
                int buttonX = startX + textWidth + Gap;

                _search.Bounds = new Rectangle(startX, 4, textWidth, 23);
                _searchHint.Bounds = new Rectangle(startX + 1, 5, textWidth - 2, 21);
                _searchClear.Bounds = new Rectangle(startX + textWidth - 22, 6, 18, 19);
                _filterToggle.Bounds = new Rectangle(buttonX, 4, 92, 25);
                apply.Bounds = new Rectangle(buttonX + 92 + 8, 4, 92, 25);


                _searchClear.BringToFront();
                _searchHint.BringToFront();
                _filterToggle.BringToFront();
                apply.BringToFront();
            };
            searchArea.Resize += layoutSearch;
            searchArea.HandleCreated += layoutSearch;

            _filterPanel = BuildFilterPanel();
            main.Controls.Add(_filterPanel);

            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
                VirtualMode = true,
                BackColor = Panel,
                ForeColor = Color.Gainsboro,
                BorderStyle = BorderStyle.FixedSingle
            };
            _list.RetrieveVirtualItem += OnRetrieveItem;
            _list.ColumnClick += OnColumnClick;
            _list.SelectedIndexChanged += (s, e) =>
            {
                // Selection churn from rebuilding the list is not the player
                // choosing a different server; SetRows settles it afterwards.
                if (_rebuildingList) return;

                var r = SelectedRow;
                _selectedEndpoint = r != null ? r.Endpoint : null;

                // Filtering rebuilds the list, which moves the selection, which
                // lands here - so while the player is typing this fires on every
                // keystroke, and each call queries a server over the network.
                // That is what made the search box lag behind the keyboard. Hold
                // the mod panel until typing has stopped.
                if (_typing) { _modsDelay.Stop(); _modsDelay.Start(); return; }
                ShowMods();
            };
            _list.Columns.Add("", 26, HorizontalAlignment.Center);
            _list.Columns.Add("", 26, HorizontalAlignment.Center);
            _list.Columns.Add("Name", 244);
            _list.Columns.Add("Game", 84);
            _list.Columns.Add("Status", 62);
            _list.Columns.Add("Map", 106);
            _list.Columns.Add("Country", 58, HorizontalAlignment.Center);
            _list.Columns.Add("Players", 70, HorizontalAlignment.Center);
            _list.Columns.Add("Time", 54, HorizontalAlignment.Center);
            _list.Columns.Add("Ping", 56, HorizontalAlignment.Center);
            _list.Columns.Add("Mods", 48, HorizontalAlignment.Center);
            _list.Columns.Add("Password", 66, HorizontalAlignment.Center);
            _list.Columns.Add("Address", 132);
            _list.DoubleClick += OnConnect;
            _list.MouseClick += OnListClick;
            _list.MouseDown += OnListMouseDown;
            _list.ContextMenuStrip = BuildServerMenu();

            var outerSplit = MakeSplit(Orientation.Horizontal, 300, "list");
            var innerSplit = MakeSplit(Orientation.Horizontal, 190, "details");
            main.Controls.Add(outerSplit);

            outerSplit.Panel1.Controls.Add(_list);
            outerSplit.Panel2.Controls.Add(innerSplit);

            var detailArea = new Panel { Dock = DockStyle.Fill, BackColor = Ink };
            innerSplit.Panel1.Controls.Add(detailArea);

            _modsHeader = new Label
            {
                Dock = DockStyle.Top,
                Height = 24,
                UseMnemonic = false,
                Text = "Content required by server",
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 0, 0),
                BackColor = Panel2,
                ForeColor = Color.White
            };
            detailArea.Controls.Add(_modsHeader);

            var detailSplit = MakeSplit(Orientation.Vertical, 600, "mods");
            detailArea.Controls.Add(detailSplit);
            detailArea.Controls.SetChildIndex(detailSplit, 0);
            detailArea.Controls.SetChildIndex(_modsHeader, 1);

            var descBox = new Panel { Dock = DockStyle.Fill, BackColor = Ink };
            detailSplit.Panel2.Controls.Add(descBox);

            _descHeader = new Label
            {
                Dock = DockStyle.Top,
                Height = 22,
                UseMnemonic = false,
                Text = "  Description",
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = Panel2,
                ForeColor = Color.White
            };
            descBox.Controls.Add(_descHeader);

            _desc = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                DetectUrls = true,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                BackColor = Panel,
                ForeColor = Color.Gainsboro,
                BorderStyle = BorderStyle.FixedSingle
            };
            _desc.LinkClicked += (s, e) => OpenLink(e.LinkText);
            descBox.Controls.Add(_desc);
            descBox.Controls.SetChildIndex(_desc, 0);
            descBox.Controls.SetChildIndex(_descHeader, 1);

            _mods = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                BackColor = Panel,
                ForeColor = Color.Gainsboro,
                BorderStyle = BorderStyle.FixedSingle
            };
            _mods.Columns.Add("Mod", 180);
            _mods.Columns.Add("Workshop ID", 90);
            _mods.Columns.Add("Status", 120);
            _mods.Columns.Add("", 58, HorizontalAlignment.Center);
            _mods.Columns.Add("", 42, HorizontalAlignment.Center);
            _mods.Columns.Add("", 60, HorizontalAlignment.Center);
            _mods.Columns.Add("", 44, HorizontalAlignment.Center);
            _mods.MouseClick += OnModClick;
            ListViewTweaks.Smooth(_mods);

            // Info, Repair, Sub and Remove are all cells that do something when
            // clicked, so the pointer says so. A local mod's Steam actions are
            // drawn blank and HandOverColumns leaves those alone.
            UiCursors.HandOverColumns(_mods, null,
                                      MColInfo, MColRepair, MColSub, MColRemove);
            detailSplit.Panel1.Controls.Add(_mods);        // Fill, so it docks innermost

            // Search within THIS server's mod list. A busy server asks for
            // eighty mods and the question is usually "has it got X", which is
            // tedious to answer by scrolling. Several terms at once, because
            // "has it got X and Y" is the next question.
            var modSearchBar = new Panel { Dock = DockStyle.Top, Height = 78, BackColor = Ink };
            detailSplit.Panel1.Controls.Add(modSearchBar);

            modSearchBar.Controls.Add(new Label
            {
                Text = "Find mod",
                Bounds = new Rectangle(4, 5, 62, 18),
                ForeColor = Dim,
                TextAlign = ContentAlignment.MiddleRight
            });

            // Anchored across the bar so the chips have the full width of the
            // Mods panel to lay themselves out in, however it is resized.
            // Two rows of chips: mod names run long, and three of them fill a
            // line on their own.
            _modFind = new ChipInput(400, 2)
            {
                Location = new Point(70, 1),
                BackColor = Ink,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _modFind.EntriesChanged += (s2, e2) => RepopulateModsFromCache();
            modSearchBar.Controls.Add(_modFind);

            modSearchBar.Resize += (s2, e2) =>
                _modFind.Width = Math.Max(240, modSearchBar.ClientSize.Width - 78);

            new ToolTip { AutoPopDelay = 15000 }.SetToolTip(_modFind.Box,
                "Shows only the mods matching what you add here. Click the X on "
                + "a row to drop it, or clear them all to see the whole list.");

            _log = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Ink,
                ForeColor = Good,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 8.5f)
            };
            innerSplit.Panel2.Controls.Add(_log);

            main.Controls.SetChildIndex(outerSplit, 0);
            main.Controls.SetChildIndex(_filterPanel, 1);
            main.Controls.SetChildIndex(top, 2);

            HighlightTab();
            KeyPreview = true;
            KeyDown += (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.F) { _search.Focus(); _search.SelectAll(); e.Handled = true; }
            };
        }

        private void BuildRail(Panel rail)
        {
            rail.Controls.Add(new PictureBox
            {
                Image = LoadImage("dayz_logo.png"),
                SizeMode = PictureBoxSizeMode.Zoom,
                Bounds = new Rectangle(18, 14, 174, 74),
                BackColor = Color.Transparent
            });
            var potato = new PictureBox
            {
                Image = LoadImage("logo_potato.png"),
                SizeMode = PictureBoxSizeMode.Zoom,
                Bounds = new Rectangle(26, 114, 58, 58),
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand
            };
            var wordmark = new PictureBox
            {
                Image = LoadImage("logo_title.png"),
                SizeMode = PictureBoxSizeMode.Zoom,
                Bounds = new Rectangle(90, 120, 104, 48),
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand
            };

            var tip = new ToolTip();
            foreach (var logo in new[] { potato, wordmark })
            {
                tip.SetToolTip(logo, HomeUrl);
                logo.Click += (s, e) => OpenLink(HomeUrl);
                rail.Controls.Add(logo);
            }

            int ry = 184;
            rail.Controls.Add(new Label { Text = "Browse", Bounds = new Rectangle(20, ry, 170, 18), ForeColor = Dim });
            ry += 20;
            _cbBuild = new ComboBox
            {
                Bounds = new Rectangle(20, ry, 170, 24),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Panel2,
                ForeColor = Color.Gainsboro,
                FlatStyle = FlatStyle.Flat
            };
            // Owner-drawn so each build can carry the colour it has in the
            // Game column of the server list - green for stable, orange for
            // Experimental. A ComboBox has no per-item colour otherwise.
            _cbBuild.DrawMode = DrawMode.OwnerDrawFixed;
            _cbBuild.ItemHeight = 17;
            _cbBuild.DrawItem += DrawBuildItem;

            _cbBuild.Items.AddRange(new object[] { "All servers", "Experimental servers", "Stable servers" });
            _cbBuild.SelectedIndex = IndexForMode(_browseMode);
            _cbBuild.SelectedIndexChanged += (s, e) =>
            {
                if (_syncingBuild) return;

                _browseMode = ModeForIndex(_cbBuild.SelectedIndex);
                ServerStore.SaveBrowseMode(_browseMode);
                if (TabPicksBuild(_tab)) RefreshCurrent();
            };
            rail.Controls.Add(_cbBuild);
            ry += 34;

            rail.Controls.Add(new Label { Text = "In-game name", Bounds = new Rectangle(20, ry, 170, 18), ForeColor = Dim });
            ry += 20;
            _name = new TextBox
            {
                Bounds = new Rectangle(20, ry, 170, 24),
                BackColor = Panel2,
                ForeColor = Color.Gainsboro,
                BorderStyle = BorderStyle.FixedSingle,
                Text = ServerStore.LoadName()
            };
            rail.Controls.Add(_name);
            ry += 34;

            _refresh = MakeBtn("REFRESH", new Rectangle(20, ry, 170, 30), Panel2);
            _refresh.Click += (s, e) => RefreshCurrent(true);
            rail.Controls.Add(_refresh);
            ry += 34;

            var resort = MakeBtn("RESORT LIST", new Rectangle(20, ry, 170, 26), Panel2);
            resort.Font = new Font("Segoe UI", 8f, FontStyle.Bold);
            resort.Click += (s, e) => ResortNow();
            rail.Controls.Add(resort);
            ry += 30;

            // Steam will not hand over more than 10,000 servers in one request,
            // so the full list has to be collected map by map. That takes a few
            // minutes, which is why it is a button and not something that
            // happens behind every refresh.
            var buildIndex = MakeBtn("BUILD INDEX", new Rectangle(20, ry, 170, 26), Panel2);
            buildIndex.Font = new Font("Segoe UI", 8f, FontStyle.Bold);
            buildIndex.Click += (s, e) => BuildIndexNow();
            rail.Controls.Add(buildIndex);
            new ToolTip { AutoPopDelay = 15000 }.SetToolTip(buildIndex,
                "Asks Steam for every map the launcher knows about, which reaches past "
                + "its 10,000-server limit. Takes a few minutes; the list fills as it goes.");
            ry += 30;

            _favBtn = MakeBtn("ADD TO FAVORITES", new Rectangle(20, ry, 170, 26), Panel2);
            _favBtn.Font = new Font("Segoe UI", 8f, FontStyle.Bold);
            _favBtn.Click += OnToggleFavourite;
            rail.Controls.Add(_favBtn);
            ry += 30;

            var modMgr = MakeBtn("MOD MANAGER", new Rectangle(20, ry, 170, 26), Panel2);
            modMgr.Font = new Font("Segoe UI", 8f, FontStyle.Bold);
            modMgr.Click += OnModManager;
            rail.Controls.Add(modMgr);
            ry += 30;

            var direct = MakeBtn("DIRECT CONNECT", new Rectangle(20, ry, 170, 28),
                                 Color.FromArgb(58, 78, 104));
            direct.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            direct.Click += OnDirectConnect;
            rail.Controls.Add(direct);

            _connect = MakeBtn("CONNECT", new Rectangle(20, 0, 170, 54), Accent);
            _connect.Font = new Font("Segoe UI", 13f, FontStyle.Bold);
            _connect.Click += OnConnect;
            rail.Controls.Add(_connect);
            rail.Resize += (s, e) => _connect.Top = rail.ClientSize.Height - 72;
            _connect.Top = rail.ClientSize.Height - 72;
        }

        private Panel BuildFilterPanel()
        {
            var p = new Panel
            {
                Dock = DockStyle.Top,
                Height = 214,          // replaced at the end by the measured height
                BackColor = Panel,
                Visible = false

                // NO AutoScroll HERE.
                //
                // It was added so a short window could still reach the bottom
                // row, but an AutoScroll panel swallows the first click on any
                // child it decides to scroll into view - so every dropdown
                // needed clicking twice, once to focus and once to open. The
                // panel measures itself to fit its contents now, so there was
                // nothing left for the scrolling to solve.
            };

            Action<string, int, int> lab = (t, lx, ly) => p.Controls.Add(new Label
            {
                Text = t,
                Bounds = new Rectangle(lx, ly + 3, 108, 20),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = Dim
            });

            Func<int, int, TextBox> box = (bx, by) =>
            {
                var b = new TextBox
                {
                    Bounds = new Rectangle(bx, by, 250, 23),
                    BackColor = Panel2,
                    ForeColor = Color.Gainsboro,
                    BorderStyle = BorderStyle.FixedSingle
                };
                p.Controls.Add(b);

                var clear = new Label
                {
                    Text = "\u2715",
                    Bounds = new Rectangle(bx + 250 - 20, by + 2, 18, 19),
                    BackColor = Panel2,
                    ForeColor = Color.FromArgb(150, 150, 158),
                    TextAlign = ContentAlignment.MiddleCenter,
                    Cursor = Cursors.Hand,
                Tag = "clickable",
                    Visible = false
                };
                clear.Click += (s2, e2) => { b.Text = ""; b.Focus(); QueueFilter(); };
                clear.MouseEnter += (s2, e2) => clear.ForeColor = Color.White;
                clear.MouseLeave += (s2, e2) => clear.ForeColor = Color.FromArgb(150, 150, 158);
                p.Controls.Add(clear);
                clear.BringToFront();

                b.TextChanged += (s2, e2) =>
                {
                    clear.Visible = b.Text.Length > 0;
                    QueueFilter();
                };
                return b;
            };

            int y = 10;
            lab("Server Name", 4, y); _fName = box(118, y); ClearBox.AddTo(_fName); y += 28;
            lab("IP Address", 4, y);  _fAddr = box(118, y); ClearBox.AddTo(_fAddr); y += 28;
            lab("Map Name", 4, y);
            _fMap = new ComboBox
            {
                Bounds = new Rectangle(118, y, 250, 23),
                BackColor = Panel2,
                ForeColor = Color.Gainsboro,
                FlatStyle = FlatStyle.Flat,
                // Editable, so typing narrows the list rather than only picking.
                DropDownStyle = ComboBoxStyle.DropDown,
                AutoCompleteMode = AutoCompleteMode.SuggestAppend,
                AutoCompleteSource = AutoCompleteSource.ListItems
            };
            _fMap.TextChanged += (s2, e2) => QueueFilter();
            // Opening the list is when the choices need to be right, so they are
            // gathered from whatever is currently loaded rather than kept in
            // step with every arriving server.
            // NOT filled from DropDown. See RefreshFilterChoices.
            p.Controls.Add(_fMap);
            ClearBox.AddTo(_fMap);      // after Add: the X goes in the same parent
            y += 28;

            // Region: a button per part of the world, each cycling through
            // show-everything / only-this / not-this. A row of small buttons
            // rather than a dropdown because players want two or three regions
            // at once - "US and EU" is the common ask, and a single-select
            // control cannot say it.
            lab("Region", 4, y);
            BuildRegionButtons(p, 118, y);
            y += 30;

            lab("Country", 4, y);
            _fCountry = new ComboBox
            {
                Bounds = new Rectangle(118, y, 250, 23),
                BackColor = Panel2,
                ForeColor = Color.Gainsboro,
                FlatStyle = FlatStyle.Flat,
                DropDownStyle = ComboBoxStyle.DropDown,
                AutoCompleteMode = AutoCompleteMode.SuggestAppend,
                AutoCompleteSource = AutoCompleteSource.ListItems
            };
            _fCountry.TextChanged += (s2, e2) => QueueFilter();
            // NOT filled from DropDown. See RefreshFilterChoices.
            p.Controls.Add(_fCountry);
            ClearBox.AddTo(_fCountry);  // after Add, for the same reason

            new ToolTip { AutoPopDelay = 15000 }.SetToolTip(_fCountry,
                "Two-letter codes, comma separated - US, DE, CA. Put a minus in "
                + "front to hide one instead: -RU shows everything except Russia.");
            y += 28;

            // Mods: pick from what the launcher has seen, and each pick is
            // added to the list underneath. Several at once, because "has both
            // of these" is the question people actually ask.
            lab("Has Mods", 4, y);
            // 250 wide to match every other control in this column. It used to
            // be 660, which ran clean across the panel and covered the 3rd
            // Person, Mods and checkbox controls on the right-hand side.
            //
            // Three rows deep, and the area scrolls - mod names are long, so a
            // narrow column fills quickly and the rest has to stay reachable
            // rather than being silently dropped.
            _modFilter = new ChipInput(250, 3) { Location = new Point(118, y), BackColor = Panel };
            _modFilter.EntriesChanged += (s2, e2) => ModFilterChanged();
            _fMod = _modFilter.Box;
            p.Controls.Add(_modFilter);

            new ToolTip { AutoPopDelay = 15000 }.SetToolTip(_fMod,
                "Servers must run ALL of these. Click the X on a row to drop it."
                + "\r\n\r\nOnly servers whose mod list has been read can match, so "
                + "the results grow as the launcher works through the list.");

            y += _modFilter.Height + 6;

            // Game Modes used to live here. It is on the search row now,
            // directly above the list - it is the filter people reach for
            // constantly, and it should not need the panel open.
            lab("Max Ping", 4, y);    _fPing = box(118, y); ClearBox.AddTo(_fPing); y += 28;

            // Players: a range with two grips, because "about sixty" is a band
            // and a single value cannot say it. Dragging either end moves that
            // end only; leaving both at the extremes means no filter at all.
            lab("Players", 4, y);
            _playerRange = new RangeSlider
            {
                Bounds = new Rectangle(118, y - 4, 300, 38),
                BackColor = Panel,
                Minimum = 0,
                Maximum = BrowserFilters.PlayerCeiling
            };
            _playerRange.SetRange(0, BrowserFilters.PlayerCeiling);
            _playerRange.RangeChanged += (s2, e2) => QueueFilter();
            p.Controls.Add(_playerRange);

            new ToolTip { AutoPopDelay = 15000 }.SetToolTip(_playerRange,
                "Drag either end. At the far right the top of the range means "
                + "\"and above\", so a busy server is never hidden by it.");
            y += 40;

            // In-game clock, in quarter-day steps. A range rather than a
            // day/night switch because "dusk" and "early morning" are real
            // requests that two options cannot express.
            lab("Game Time", 4, y);
            _timeRange = new RangeSlider
            {
                Bounds = new Rectangle(118, y - 4, 300, 38),
                BackColor = Panel,
                Minimum = 0,
                Maximum = 24,
                Step = 6,
                ShowTicks = true,
                FormatRange = (lo, hi) => lo <= 0 && hi >= 24
                    ? "any time of day"
                    : string.Format("{0:00}:00 to {1:00}:00", lo, hi == 24 ? 24 : hi)
            };
            _timeRange.SetRange(0, 24);
            _timeRange.RangeChanged += (s2, e2) => QueueFilter();
            p.Controls.Add(_timeRange);

            new ToolTip { AutoPopDelay = 15000 }.SetToolTip(_timeRange,
                "The server's in-game clock, in six hour steps. Servers that do "
                + "not publish a clock are not shown while this is narrowed.");
            y += 40;
            const int rx = 500;
            lab("3rd Person View", rx - 114, 10);
            _segThird = new Segmented(new[] { "Any", "Enabled", "Disabled" }, new Point(rx, 10));
            _segThird.Changed += QueueFilter;
            _segThird.AddTo(p);

            lab("Mods", rx - 114, 42);
            _segMods = new Segmented(new[] { "Any", "Enabled", "Disabled" }, new Point(rx, 42));
            _segMods.Changed += QueueFilter;
            _segMods.AddTo(p);

            _chkNoPass    = MakeCheck("No password required", new Point(rx, 74));
            _chkHideFull  = MakeCheck("Hide full servers",    new Point(rx, 98));
            _chkHideEmpty = MakeCheck("Hide empty servers",   new Point(rx, 122));
            _chkHideFakes = MakeCheck("Hide fake servers",    new Point(rx, 146));
            _chkHideFakes.Checked = true;
            _chkHideFakes.ForeColor = Color.FromArgb(220, 190, 120);

            var tipFake = new ToolTip();
            tipFake.SetToolTip(_chkHideFakes,
                "Hides servers claiming more than " + MaxRealisticSlots +
                " slots (DayZ limits effective capacity to ~120); servers reporting no real " +
                "slot count that share an address with " + SentinelFarmSize + "+ others; and " +
                "addresses running " + BrowserFilters.FarmServersPerIp + "+ servers under " +
                BrowserFilters.FarmNamesPerIp + "+ different names or identical mod arrays - redirect farms.");

            _chkNoPass.CheckedChanged    += (s2, e2) => QueueFilter();
            _chkHideFull.CheckedChanged  += (s2, e2) => QueueFilter();
            _chkHideEmpty.CheckedChanged += (s2, e2) => QueueFilter();
            _chkHideFakes.CheckedChanged += (s2, e2) => QueueFilter();
            p.Controls.Add(_chkNoPass);
            p.Controls.Add(_chkHideFull);
            p.Controls.Add(_chkHideEmpty);
            p.Controls.Add(_chkHideFakes);

            var applyBtn = MakeBtn("APPLY NOW", new Rectangle(rx + 240, 100, 140, 27),
                                   Color.FromArgb(60, 95, 60));
            applyBtn.Click += (s, e) => ApplyFilters();
            p.Controls.Add(applyBtn);

            var clear = MakeBtn("CLEAR", new Rectangle(rx + 240, 133, 140, 25), Panel2);
            clear.Click += (s, e) => { ClearFilterUi(); ApplyFilters(); };
            p.Controls.Add(clear);

            // The footnote goes below everything else, wherever that turns out
            // to be, rather than at a measured-once position.
            int footY = 0;
            foreach (Control c in p.Controls) footY = Math.Max(footY, c.Bottom);

            p.Controls.Add(new Label
            {
                Text = "Filters apply as you change them, against the list already downloaded.  "
                     + "Name, map, players and password are also sent to Steam on the next REFRESH, "
                     + "which is how a narrowed refresh reaches past the 10,000 servers it returns at once.",
                Bounds = new Rectangle(8, footY + 8, 920, 18),
                ForeColor = Color.FromArgb(110, 110, 118),
                Font = new Font("Segoe UI", 7.5f)
            });

            // THE PANEL IS SIZED TO ITS CONTENTS, NOT TO A NUMBER.
            //
            // It was a fixed 214px, which was right until two more rows were
            // added and Game Time disappeared behind the server list. Measuring
            // means the next thing added cannot silently fall off the bottom.
            int bottom = 0;
            foreach (Control c in p.Controls) bottom = Math.Max(bottom, c.Bottom);
            p.Height = bottom + 10;

            return p;
        }

        private ComboBox MakeCombo(Rectangle bounds, object[] items)
        {
            var c = new ComboBox
            {
                Bounds = bounds,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Panel2,
                ForeColor = Color.Gainsboro,
                FlatStyle = FlatStyle.Flat
            };
            c.Items.AddRange(items);
            c.SelectedIndex = 0;
            return c;
        }

        private CheckBox MakeCheck(string text, Point at)
        {
            return new CheckBox
            {
                Text = text,
                Location = at,
                Size = new Size(220, 22),
                ForeColor = Color.Gainsboro,
                FlatStyle = FlatStyle.Flat
            };
        }

        private SplitContainer MakeSplit(Orientation orientation, int distance, string key)
        {
            var sc = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = orientation,
                BackColor = Color.FromArgb(58, 58, 64),
                SplitterWidth = 6,
                Panel1MinSize = 80,
                Panel2MinSize = 80
            };
            sc.Panel1.BackColor = Ink;
            sc.Panel2.BackColor = Ink;

            _splitters[key] = sc;
            sc.HandleCreated += (s, e) => ApplySplit(sc, key, distance);

            bool dragging = false;
            sc.SplitterMoving += (s, e) => dragging = true;
            sc.SplitterMoved += (s, e) =>
            {
                if (!dragging) return;
                dragging = false;
                RememberSplit(sc, key);
            };

            return sc;
        }

        private readonly Dictionary<string, SplitContainer> _splitters =
            new Dictionary<string, SplitContainer>();
        private readonly Dictionary<string, double> _panelFractions = ServerStore.LoadPanels();

        private static int SplitSpan(SplitContainer sc)
        {
            return (sc.Orientation == Orientation.Vertical ? sc.Width : sc.Height)
                 - sc.SplitterWidth;
        }

        private void ApplySplit(SplitContainer sc, string key, int fallback)
        {
            int span = SplitSpan(sc);
            int want = fallback;

            double fraction;
            if (span > 0 && _panelFractions.TryGetValue(key, out fraction))
                want = (int)Math.Round(fraction * span);

            int lo = sc.Panel1MinSize;
            int hi = span - sc.Panel2MinSize;
            if (hi < lo) return;
            want = Math.Max(lo, Math.Min(hi, want));

            try { sc.SplitterDistance = want; } catch { }
        }

        private void RememberSplit(SplitContainer sc, string key)
        {
            int span = SplitSpan(sc);
            if (span <= 0) return;
            double fraction = sc.SplitterDistance / (double)span;
            if (fraction <= 0.0 || fraction >= 1.0) return;
            _panelFractions[key] = fraction;
            ServerStore.SavePanels(_panelFractions);
        }

        private void RememberAllSplits()
        {
            try
            {
                foreach (var kv in _splitters) RememberSplit(kv.Value, kv.Key);
            }
            catch { }
        }

        private Button MakeBtn(string text, Rectangle bounds, Color back)
        {
            var b = new Button
            {
                Text = text,
                Bounds = bounds,
                FlatStyle = FlatStyle.Flat,
                BackColor = back,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            b.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 76);
            return b;
        }

        // ---------------------------------------------------------- tabs ----
        private void OnTabClick(object sender, EventArgs e)
        {
            _tab = (Tab)((Button)sender).Tag;
            ServerStore.SaveTab((int)_tab);
            HighlightTab();
            RefreshCurrent();
        }

        private static string TabName(Tab t)
        {
            switch (t)
            {
                case Tab.Recent: return "RECENT";
                case Tab.Friends: return "FRIENDS";
                case Tab.Lan: return "LAN";
                case Tab.Favourites: return "FAVORITES";
                case Tab.Official: return "OFFICIAL";
                default: return "COMMUNITY";
            }
        }

        private static bool IsSteamTab(Tab t) { return t != Tab.Favourites; }

        private static ListKind KindFor(Tab t)
        {
            switch (t)
            {
                case Tab.Official: return ListKind.Internet;
                case Tab.Recent: return ListKind.Recent;
                case Tab.Friends: return ListKind.Friends;
                case Tab.Lan: return ListKind.Lan;
                default: return ListKind.Internet;
            }
        }

        private bool _syncingBuild;

        /// <summary>
        /// Paints one row of the build dropdown in the same colour the server
        /// list uses for that build, so the two agree at a glance.
        /// </summary>
        private void DrawBuildItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;

            string text = Convert.ToString(_cbBuild.Items[e.Index]);

            bool highlighted = (e.State & DrawItemState.Selected) != 0;
            Color back = highlighted ? Color.FromArgb(60, 60, 68) : Panel2;

            using (var brush = new SolidBrush(back))
                e.Graphics.FillRectangle(brush, e.Bounds);

            // Same values as the Game column in the list - see MakeItem.
            Color ink = text.StartsWith("Experimental", StringComparison.OrdinalIgnoreCase)
                            ? Color.FromArgb(255, 170, 80)
                      : text.StartsWith("Stable", StringComparison.OrdinalIgnoreCase)
                            ? Color.FromArgb(110, 220, 140)
                      : Color.Gainsboro;          // "All servers" stays plain

            TextRenderer.DrawText(e.Graphics, text, _cbBuild.Font,
                new Rectangle(e.Bounds.X + 2, e.Bounds.Y, e.Bounds.Width - 4, e.Bounds.Height),
                ink, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        }

        private static int IndexForMode(string mode)
        {
            return mode == "all" ? 0 : mode == "exp" ? 1 : 2;
        }

        private static string ModeForIndex(int index)
        {
            return index == 0 ? "all" : index == 1 ? "exp" : "stable";
        }

        private void HighlightTab()
        {
            foreach (var kv in _tabs)
                kv.Value.BackColor = kv.Key == _tab ? Accent : Panel2;

            bool picks = TabPicksBuild(_tab);
            _cbBuild.Enabled = picks;

            _syncingBuild = true;
            try
            {
                _cbBuild.SelectedIndex = IndexForMode(EffectiveBrowseMode);
            }
            finally { _syncingBuild = false; }
        }

        private void RefreshCurrent(bool force = false)
        {
            _pollTimer.Stop();
            SteamServerList.Stop();

            if (!IsSteamTab(_tab)) { _visTimer.Stop(); RefreshMine(); return; }

            ReadFilterUi();

            int shown = 0;
            if (_cache.Count > 0)
            {
                shown = RenderFromCache();
                DateTime at;
                _cacheTimes.TryGetValue(CacheKey, out at);
                Log("Showing " + _cache.Count + " remembered " + TabName(_tab).ToLower()
                    + " servers" + (at == DateTime.MinValue ? "" : " from " + at.ToString("HH:mm"))
                    + " while Steam is asked for the current list.");
                _status.Text = string.Format("{0} of {1} servers shown  (updating...)",
                                             shown, _cache.Count);
            }

            DateTime fetched;
            bool fresh = !force
                      && _cache.Count > 0
                      && _cacheTimes.TryGetValue(CacheKey, out fetched)
                      && fetched > DateTime.MinValue
                      && DateTime.Now - fetched < CacheFreshFor;

            if (fresh)
            {
                _status.Text = string.Format("{0} of {1} servers shown", shown, _cache.Count);
                return;
            }

            StartCommunityQuery();
        }

        // ------------------------------------------------- saved servers ----
        private void RefreshMine()
        {
            var rows = _favourites.Select(MakeEntryFromEndpoint)
                                  .Where(x => x != null)
                                  .Select(x => new Row { Name = x.Name, Host = x.Host, Port = x.Port })
                                  .ToList();

            foreach (var r in rows)
            {
                ServerInfo known;
                if (_live.TryGetValue(r.Endpoint, out known)) Apply(r, known);
            }

            SortRows(rows);
            SetRows(rows, _selectedEndpoint);

            if (rows.Count == 0)
            {
                _status.Text = "No favourites yet - click the star beside a server to save it.";
                return;
            }

            _status.Text = "Querying " + rows.Count + " saved server(s)...";
            Enqueue(rows, true);
        }

        private void FinishMine()
        {
            int up = _rows.Count(r => r.Online == true);
            _status.Text = up + " of " + _rows.Count + " servers online.";
        }

        private static ServerEntry MakeEntryFromEndpoint(string ep)
        {
            int c = ep.LastIndexOf(':');
            int port;
            if (c <= 0 || !int.TryParse(ep.Substring(c + 1), out port)) return null;
            return new ServerEntry(ServerStore.NameFor(ep), ep.Substring(0, c), port);
        }

        private static void Apply(Row row, ServerInfo info)
        {
            row.Online = info.Online;
            if (info.Online)
            {
                if (!string.IsNullOrEmpty(info.Name)) row.Name = IpRegion.ReadableName(info.Name);
                row.Map = info.Map;
                row.Players = info.Players;
                row.MaxPlayers = info.MaxPlayers;
                row.Ping = info.PingMs;
                row.AppId = info.AppId;
                row.Tags = info.Keywords;
                row.Password = info.Password;
            }
            else
            {
                row.Error = info.Error;
            }
        }

        private void OnColumnClick(object sender, ColumnClickEventArgs e)
        {
            // Asking for a sort turns off append mode: the player has said what
            // order they want, so arrivals stop being pinned to the end.
            _appendWhileLoading = false;
            if (e.Column == ColRefresh || e.Column == ColStar) return;

            if (_sortColumn == e.Column) _sortAscending = !_sortAscending;
            else { _sortColumn = e.Column; _sortAscending = true; }

            MarkSortedColumn();
            if (IsSteamTab(_tab)) RenderFromCache();
            else RefreshMine();
        }

        private void MarkSortedColumn()
        {
            string[] titles = { "", "", "Name", "Game", "Status", "Map", "Country",
                                "Players", "Time", "Ping", "Mods", "Password", "Address" };
            for (int i = 0; i < _list.Columns.Count && i < titles.Length; i++)
            {
                string label = titles[i];
                if (i == _sortColumn && label.Length > 0)
                    label += _sortAscending ? "  \u25B2" : "  \u25BC";
                _list.Columns[i].Text = label;
            }
        }

        /// <summary>
        /// The Players cell: "40 / 40" normally, "40 / 40 (8)" when eight are
        /// queued. The queue is only shown when there IS one - a "(0)" on every
        /// row would be noise on a column players scan quickly.
        /// </summary>
        private static string PlayersCell(Row row)
        {
            if (row.MaxPlayers <= 0) return "";
            string cell = row.Players + " / " + row.MaxPlayers;
            int q = row.Queue;
            return q > 0 ? cell + " (" + q + ")" : cell;
        }

        /// <summary>
        /// Returns the rows in the order they are ALREADY displayed, with any
        /// server not previously shown appended in the order it arrived.
        /// </summary>
        /// <summary>
        /// A server left on its out-of-the-box name. DayZ ships "EXAMPLE NAME"
        /// and hosting panels leave their own placeholders behind, so a few
        /// obvious ones are treated the same way.
        /// </summary>
        /// <summary>
        /// A server still carrying the name its host gave it out of the box.
        ///
        /// Nobody is searching for one of these - the owner has not set it up
        /// yet - so they sort last whatever column is chosen. Matched on
        /// SUBSTRINGS as well as whole names, because the hosting companies
        /// each dress theirs up slightly differently ("DayZ gameserver hosted
        /// by nitrado.net", "nitrado.net gameserver") and chasing every exact
        /// spelling would be a losing game.
        /// </summary>
        private static bool Unconfigured(Row r)
        {
            if (r == null || string.IsNullOrWhiteSpace(r.Name)) return true;
            string n = r.Name.Trim();

            foreach (string exact in StockNames)
                if (n.Equals(exact, StringComparison.OrdinalIgnoreCase)) return true;

            foreach (string mark in HostDefaults)
                if (n.IndexOf(mark, StringComparison.OrdinalIgnoreCase) >= 0) return true;

            return false;
        }

        /// <summary>Names that mean "never configured" on their own.</summary>
        private static readonly string[] StockNames =
        {
            "EXAMPLE NAME",
            "DayZ",
            "Server Name",
            "DayZ Server"
        };

        /// <summary>
        /// Fragments the hosting companies leave in a default name. A server
        /// whose name still advertises its host has not been named by anyone.
        /// </summary>
        private static readonly string[] HostDefaults =
        {
            "nitrado.net gameserver",
            "gameserver hosted by nitrado",
            // A hosting company's own test boxes - 36 of them in one sweep,
            // named "NFOservers.com - Chicago test # 19" and so on. Real
            // machines, but nobody's server.
            "nfoservers.com",
            "hosted by gtxgaming",
            "server by hosthavoc",
            "hosted by hosthavoc",
            "gportal.com gameserver",
            "hosted by gportal"
        };

        private List<Row> KeepOrderThenAppend(List<Row> rows)
        {
            var byEndpoint = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rows) byEndpoint[r.Endpoint] = r;

            var ordered = new List<Row>(rows.Count);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var existing in _rows)
            {
                Row fresh;
                if (!byEndpoint.TryGetValue(existing.Endpoint, out fresh)) continue;  // filtered out now
                ordered.Add(fresh);
                used.Add(existing.Endpoint);
            }
            // New arrivals, in the order Steam handed them over.
            var arrived = new List<Row>();
            foreach (var r in rows)
                if (!used.Contains(r.Endpoint)) arrived.Add(r);

            ordered.AddRange(arrived);

            // The two groups that are positional rather than sorted stay where
            // they belong: favourites at the top, never-configured servers at
            // the very bottom. A STABLE partition, so within each group
            // everything keeps the order it already had and nothing visible
            // jumps. This is the only movement a loading list is allowed.
            var favourites = new List<Row>();
            var normal = new List<Row>();
            var stock = new List<Row>();

            foreach (var r in ordered)
            {
                if (Unconfigured(r)) stock.Add(r);
                else if (r.Favourite) favourites.Add(r);
                else normal.Add(r);
            }

            var result = new List<Row>(ordered.Count);
            result.AddRange(favourites);
            result.AddRange(normal);
            result.AddRange(stock);
            return result;
        }

        /// <summary>Re-sorts what is on screen using the last chosen column.</summary>
        private void ResortNow()
        {
            ResortNow(false);
        }

        /// <summary>
        /// <paramref name="quiet"/> is for the automatic re-sort that follows
        /// servers being found offline: it must not overwrite the status line,
        /// which is busy reporting the progress of that same sweep.
        /// </summary>
        private void ResortNow(bool quiet)
        {
            if (_rows == null || _rows.Count == 0) return;
            var rows = new List<Row>(_rows);
            SortRows(rows);
            SetRows(rows, _selectedEndpoint);
            if (!quiet) _status.Text = string.Format("Re-sorted {0} servers.", rows.Count);
        }

        private void SortRows(List<Row> rows)
        {
            Comparison<Row> byColumn;
            switch (_sortColumn)
            {
                case ColName:
                    byColumn = (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                    break;
                case ColGame:
                    byColumn = (a, b) => string.Compare(a.GameLabel, b.GameLabel, StringComparison.OrdinalIgnoreCase);
                    break;
                case ColStatus:
                    byColumn = (a, b) => Rank(a).CompareTo(Rank(b));
                    break;
                case ColMap:
                    byColumn = (a, b) => string.Compare(a.Map, b.Map, StringComparison.OrdinalIgnoreCase);
                    break;
                case ColCountry:
                    // Blank sorts last either way - a server we cannot place is
                    // not "country A", it is an unknown.
                    byColumn = (a, b) =>
                    {
                        // "??" is an unknown, not a country beginning with a
                        // question mark - it sorts last either way, like a blank.
                        bool na = Unknown(a.Country), nb = Unknown(b.Country);
                        if (na != nb) return na ? 1 : -1;
                        return string.Compare(a.Country, b.Country, StringComparison.OrdinalIgnoreCase);
                    };
                    break;
                case ColPlayers:
                    // A queue means the server is full and MORE people want in,
                    // so it sorts above an equally full server with none.
                    byColumn = (a, b) =>
                    {
                        int c = a.Players.CompareTo(b.Players);
                        return c != 0 ? c : a.Queue.CompareTo(b.Queue);
                    };
                    break;
                case ColTime:
                    byColumn = (a, b) => string.Compare(TagTime(a.Tags), TagTime(b.Tags), StringComparison.Ordinal);
                    break;
                case ColPing:
                    byColumn = (a, b) => PingKey(a).CompareTo(PingKey(b));
                    break;
                case ColMods:
                    byColumn = (a, b) => HasTag(a.Tags, "mod").CompareTo(HasTag(b.Tags, "mod"));
                    break;
                case ColPassword:
                    byColumn = (a, b) => a.Password.CompareTo(b.Password);
                    break;
                case ColAddress:
                    byColumn = (a, b) => string.Compare(a.Endpoint, b.Endpoint, StringComparison.OrdinalIgnoreCase);
                    break;
                default:
                    byColumn = null;
                    break;
            }

            rows.Sort((x, y) =>
            {
                // Servers still carrying the stock name have never been
                // configured - nobody is looking for one, so they go last
                // whatever else the sort says.
                int unconfigured = (Unconfigured(x) ? 1 : 0) - (Unconfigured(y) ? 1 : 0);
                if (unconfigured != 0) return unconfigured;

                // FAVOURITES ARE ALWAYS AT THE TOP - offline or not.
                //
                // A favourite is a server the player has chosen to watch, and
                // whether it is up is often the very thing they opened the
                // launcher to find out. Sinking it when it goes down hides the
                // answer. Offline still sorts to the bottom, but only within
                // each group: dead favourites sit under live ones, and dead
                // strangers under live strangers.
                int fav = (y.Favourite ? 1 : 0) - (x.Favourite ? 1 : 0);
                if (fav != 0) return fav;

                int offline = (x.Offline ? 1 : 0) - (y.Offline ? 1 : 0);
                if (offline != 0) return offline;

                if (byColumn != null)
                {
                    int c = byColumn(x, y);
                    if (!_sortAscending) c = -c;
                    if (c != 0) return c;
                }
                else
                {
                    int byPlayers = y.Players.CompareTo(x.Players);
                    if (byPlayers != 0) return byPlayers;
                }
                return string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
            });
        }

        /// <summary>
        /// The notice row. A ListViewItem carries its own Font, so this needs
        /// no owner-drawing - which matters, because owner-drawing the list
        /// would mean drawing every server row by hand as well.
        /// </summary>
        private ListViewItem HeadingItem(Row row)
        {
            // The text goes in the NAME column, not the first one - column 0
            // is the refresh arrow, about 26 pixels wide, so the notice was
            // being squeezed into it and was unreadable.
            var it = new ListViewItem("")
            {
                UseItemStyleForSubItems = true,
                ForeColor = Color.FromArgb(225, 175, 90),
                BackColor = Ink
            };

            while (it.SubItems.Count <= ColName) it.SubItems.Add("");
            it.SubItems[ColName].Text = row.Heading;

            // A VIRTUAL ListView DEMANDS ONE SUB-ITEM PER COLUMN.
            //
            // Give it fewer and it throws from inside WndProc - "RetrieveVirtual
            // ListItem event needs a list view SubItem for each ListView column"
            // - and it surfaces as a crash dialog when that row is scrolled into
            // view, not when it was built. The heading fills only the first
            // column, so the rest are padded.
            while (it.SubItems.Count < _list.Columns.Count) it.SubItems.Add("");

            if (row.Heading.Length > 0)
                it.Font = new Font("Segoe UI", 12f, FontStyle.Bold);

            return it;
        }

        /// <summary>A country cell with nothing real in it.</summary>
        private static bool Unknown(string cc)
        {
            return string.IsNullOrEmpty(cc) || cc == "??";
        }

        private static int Rank(Row r)
        {
            return !r.Online.HasValue ? 1 : r.Online.Value ? 0 : 2;
        }

        private static int PingKey(Row r)
        {
            return r.Ping > 0 ? r.Ping : int.MaxValue;
        }

        private void OnRetrieveItem(object sender, RetrieveVirtualItemEventArgs e)
        {
            if (e.ItemIndex >= 0 && e.ItemIndex < _rows.Count && _rows[e.ItemIndex].IsHeading)
            {
                e.Item = HeadingItem(_rows[e.ItemIndex]);
                return;
            }

            var rows = _rows;
            if (e.ItemIndex < 0 || e.ItemIndex >= rows.Count)
            {
                e.Item = new ListViewItem("");
                return;
            }
            e.Item = BuildItem(rows[e.ItemIndex],
                               (e.ItemIndex & 1) == 0 ? RowBandA : RowBandB);
        }

        private ListViewItem BuildItem(Row row)
        {
            return BuildItem(row, RowBandA);
        }

        private ListViewItem BuildItem(Row row, Color band)
        {
            bool fav = _favourites.Contains(row.Endpoint);

            var it = new ListViewItem(new[]
            {
                RefreshGlyph,
                fav ? StarOn : StarOff,
                row.Name,
                row.GameLabel,
                row.StatusText,
                row.Map,
                row.Country,
                PlayersCell(row),
                TagTime(row.Tags),
                row.Ping > 0 ? row.Ping + " ms" : (row.Offline ? "-" : ""),
                HasTag(row.Tags, "mod") ? "yes" : "",
                row.Password ? "Yes" : "No",
                row.Endpoint
            })
            {
                UseItemStyleForSubItems = false,
                ToolTipText = "Arrow re-checks this server; star saves it to Favorites"
            };

            // Same rule as the heading: a virtual list needs every column
            // filled, and columns get added over time.
            while (it.SubItems.Count < _list.Columns.Count) it.SubItems.Add("");

            Color body = row.Offline ? RowOffline : RowNormal;
            for (int i = ColName; i < it.SubItems.Count; i++) it.SubItems[i].ForeColor = body;

            it.SubItems[ColRefresh].ForeColor = Color.FromArgb(120, 150, 190);
            it.SubItems[ColStar].ForeColor = fav
                ? Color.FromArgb(235, 200, 90) : Color.FromArgb(95, 95, 102);
            it.SubItems[ColGame].ForeColor =
                row.AppId == A2S.ExperimentalAppId ? Color.FromArgb(255, 170, 80)
                : row.AppId == A2S.StableAppId ? Color.FromArgb(110, 220, 140)
                : body;
            it.SubItems[ColPassword].ForeColor = row.Password
                ? Color.FromArgb(225, 175, 90) : Color.FromArgb(110, 110, 118);
            it.SubItems[ColStatus].ForeColor =
                !row.Online.HasValue ? Color.FromArgb(120, 120, 128)
                : row.Online.Value ? StatusOn : StatusOff;

            it.BackColor = band;
            for (int i = 0; i < it.SubItems.Count; i++) it.SubItems[i].BackColor = band;
            return it;
        }

        private void SetRows(List<Row> rows, string keepSelected)
        {
            rows = WithHeadings(rows);

            // NOTHING CHANGED, SO CHANGE NOTHING.
            //
            // A refresh re-renders the whole list every time Steam's count
            // moves, which during a fetch is constantly - and most of those
            // renders produce exactly the same rows in exactly the same order.
            // Rebuilding a virtual ListView repaints every visible row, and
            // that is the flash. If the order is identical, the rows are the
            // same objects and their contents are read on demand, so a redraw
            // is all that is needed.
            if (SameOrder(rows))
            {
                _rows = rows;
                _rowIndex.Clear();
                for (int i = 0; i < rows.Count; i++) _rowIndex[rows[i].Endpoint] = i;
                _list.Invalidate();
                SweepAll(rows);
                return;
            }

            _rows = rows;
            _rowIndex.Clear();
            for (int i = 0; i < rows.Count; i++) _rowIndex[rows[i].Endpoint] = i;

            _rebuildingList = true;
            _list.BeginUpdate();
            try
            {
                _list.SelectedIndices.Clear();
                _list.VirtualListSize = rows.Count;

                int sel;
                if (keepSelected != null && _rowIndex.TryGetValue(keepSelected, out sel))
                {
                    _list.SelectedIndices.Add(sel);

                    // Deliberately NOT EnsureVisible. The list is rebuilt every
                    // time a ping comes back, and scrolling the view to the
                    // selected row each time dragged the player back to it the
                    // moment they tried to look anywhere else. The selection is
                    // kept; where they are looking is left alone.
                }
                else if (rows.Count > 0)
                {
                    _list.SelectedIndices.Add(0);
                }
            }
            catch { }
            finally
            {
                _list.EndUpdate();
                _rebuildingList = false;
            }

            // Now decide ONCE whether anything really changed. If the same
            // server is still selected, the mod panel is already correct and is
            // left completely alone - no clear, no re-query, no flicker.
            var current = SelectedRow;
            _selectedEndpoint = current != null ? current.Endpoint : null;
            if (_selectedEndpoint != _modsShownFor) ShowMods();

            FitNameColumn();

            _lastTopIndex = -1;
            _visTimer.Start();
            SweepAll(rows);
            _list.Invalidate();
        }

        /// <summary>
        /// Scrolls to a server on purpose - used when the player asks to go to
        /// one, never as part of a refresh.
        /// </summary>
        private void ScrollTo(string endpoint)
        {
            int i;
            if (endpoint == null || !_rowIndex.TryGetValue(endpoint, out i)) return;
            if (i < 0 || i >= _list.VirtualListSize) return;
            try { _list.EnsureVisible(i); } catch { }
        }

        private void Redraw(string endpoint)
        {
            int i;
            if (!_rowIndex.TryGetValue(endpoint, out i)) return;
            if (i < 0 || i >= _list.VirtualListSize) return;
            try { _list.RedrawItems(i, i, true); } catch { }
        }

        private static bool HasTag(string tags, string tag)
        {
            if (string.IsNullOrEmpty(tags)) return false;
            return tags.Split(',').Any(t => t.Trim().Equals(tag, StringComparison.OrdinalIgnoreCase));
        }

        private static string TagTime(string tags)
        {
            if (string.IsNullOrEmpty(tags)) return "";
            foreach (var t in tags.Split(','))
            {
                string s = t.Trim();
                if (System.Text.RegularExpressions.Regex.IsMatch(s, @"^\d{1,2}:\d{2}$")) return s;
            }
            return "";
        }

        // --------------------------------------------------- community tab ----
        private void StartCommunityQuery()
        {
            ReadFilterUi();

            string steam = FindSteam();
            string gameDir = steam == null ? null
                : (FindGameDir(steam, A2S.ExperimentalAppId) ?? FindGameDir(steam, A2S.StableAppId));

            if (gameDir == null)
            {
                _status.Text = "Cannot find DayZ, so the server browser is unavailable.";
                return;
            }

            if (!_steamReady)
            {
                _steamReady = SteamServerList.TryInit(gameDir, Log);
                if (!_steamReady)
                {
                    _status.Text = "Steam is not running, so the master server list is unavailable.";
                    MessageBox.Show(
                        "The community browser needs Steam running.\r\n\r\nStart Steam, then press REFRESH.",
                        "Steam not available", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
            }

            var kind = KindFor(_tab);

            _appQueue = new Queue<SweepPass>();
            foreach (uint a in AppsForQuery)
                _appQueue.Enqueue(new SweepPass(a, BuildLabel(a)));

            _merged.Clear();
            _mergedKeys.Clear();
            _deepSweepQueued = false;

            foreach (var srv in _cache)
                if (_mergedKeys.Add(srv.Endpoint)) _merged.Add(srv);

            var pass = _appQueue.Dequeue();
            uint app = pass.App;

            var steamFilters = FiltersFor(pass, kind);
            _lastSteamFilterKey = SteamFilterKey(steamFilters);

            _browser.Clear();
            if (_merged.Count == 0)
                _status.Text = "Asking Steam for the " + TabName(_tab).ToLower() + " list ("
                             + BuildLabel(app) + ")...";

            Log("Query: " + kind + ", app " + app + (steamFilters.Count == 0 ? ", no filters"
                : ", " + string.Join(", ", steamFilters.Select(f => f.Key + "=" + f.Value).ToArray())));

            if (!SteamServerList.Start(kind, app, steamFilters))
            {
                _status.Text = "Steam refused the server list request.";
                return;
            }
            _appendWhileLoading = true;
            _pollPass = pass;
            _pollApp = app;
            _pollTicks = 0;
            _stableTicks = 0;
            _lastRawCount = -1;
            _pollKind = kind;

            _pollTimer.Interval = PollingSmallList ? SmallListPollMs : InternetPollMs;
            _pollTimer.Start();
        }

        private int _pollTicks;
        private ListKind _pollKind = ListKind.Internet;

        // ----------------------------------------------- the 10,000 cap ----
        //
        // Steam truncates ANY ONE request at 10,000 servers, and DayZ has far
        // more than that. Nothing can raise the cap - but it applies per
        // REQUEST, so several narrower requests together reach past it.
        //
        // MEASURED, 20 September 2026, against the live master list:
        //
        //     one plain query .................  4,997 servers with details
        //     plus per-map queries ............  9,644   (+4,647)
        //     plus populated-only .............  12,337  (+2,693)
        //     plus modded .....................  12,597    (+260)
        //     plus first-person ...............  18,502  (+5,905)
        //
        // Nearly four times the servers, out of the same list Steam was already
        // willing to give - it simply will not give it all at once.
        //
        // The extra passes only run when the plain query actually hits the cap,
        // so a narrow search still finishes in one round trip.
        private struct SweepPass
        {
            public uint App;
            public string Label;

            /// <summary>The map this pass asked for, or null for a tag pass.</summary>
            public string Map;

            /// <summary>Narrowing added on top of the player's own filters.</summary>
            public List<KeyValuePair<string, string>> Extra;

            public SweepPass(uint app, string label, params string[] keyValuePairs)
            {
                App = app;
                Label = label;
                Map = null;
                Extra = new List<KeyValuePair<string, string>>();
                for (int i = 0; i + 1 < keyValuePairs.Length; i += 2)
                {
                    Extra.Add(new KeyValuePair<string, string>(keyValuePairs[i], keyValuePairs[i + 1]));
                    if (keyValuePairs[i] == "map") Map = keyValuePairs[i + 1];
                }
            }
        }

        private Queue<SweepPass> _appQueue = new Queue<SweepPass>();
        private readonly List<BrowserServer> _merged = new List<BrowserServer>();
        private readonly HashSet<string> _mergedKeys = new HashSet<string>();
        private SweepPass _pollPass;
        private uint _pollApp;

        /// <summary>Set once the extra passes are queued, so they are queued once.</summary>
        private bool _deepSweepQueued;

        /// <summary>
        /// Coming within a whisker of Steam's 10,000 means the list was cut short.
        /// </summary>
        private const int NearlyCapped = 9900;

        /// <summary>
        /// The extra requests to make when the plain one came back truncated.
        ///
        /// The three tag passes come first because they contributed the most in
        /// the measurement above, so a sweep the player cuts short has still
        /// gained the most it could. Then the maps.
        ///
        /// HOW MANY MAPS
        ///   An ordinary refresh takes a SLICE of the known maps - all of them
        ///   would take twenty minutes and nobody wants that on every refresh.
        ///   The slice moves on each time, so consecutive refreshes cover
        ///   different maps and the index fills in over a few sessions. BUILD
        ///   INDEX takes the lot in one go for a player who wants it now.
        ///
        /// WHERE THE MAPS COME FROM
        ///   Servers already seen, remembered permanently in maps.txt. DayZ
        ///   gains community maps constantly and a hard-coded list would be
        ///   wrong within a month - and several names guessed by hand returned
        ///   nothing at all, which is a wasted round trip every single refresh.
        /// </summary>
        private List<SweepPass> DeepSweepPasses(uint app, bool everyMap)
        {
            var passes = new List<SweepPass>
            {
                new SweepPass(app, "first-person", "gametagsand", "no3rd"),
                new SweepPass(app, "populated",    "empty",       "1"),
                new SweepPass(app, "modded",       "gametagsand", "mod")
            };

            var maps = KnownMaps();

            // Maps Steam has twice told us nothing about. They are REAL maps
            // with real servers on them - Steam's map filter simply does not
            // match what those servers report - so asking again is a round trip
            // that reliably returns zero. They stay in the index; they just
            // stop costing time on every build.
            var barren = ServerStore.BarrenMaps();
            if (barren.Count > 0)
            {
                int was = maps.Count;
                maps = maps.Where(m => !barren.Contains(m)).ToList();
                if (was != maps.Count)
                    Log("  Skipping " + (was - maps.Count)
                        + " map(s) that have returned nothing twice running.");
            }

            if (maps.Count == 0) return passes;

            int take = everyMap ? maps.Count : Math.Min(MapsPerRefresh, maps.Count);

            for (int i = 0; i < take; i++)
            {
                string map = maps[(_mapCursor + i) % maps.Count];
                passes.Add(new SweepPass(app, "map " + map, "map", map));
            }

            // ASKING TWICE IS NOT WASTED.
            //
            // Steam does not return the same servers for the same query. Asked
            // for chernarusplus three times in a row it returned 3701, 3702 and
            // 3702 servers - but the second run added 289 the first had never
            // mentioned, and the third another 223. It hands back a slice, not
            // the list, and the slice moves.
            //
            // So a build repeats the busiest maps. Diminishing, but real, and
            // it is the only thing that actually works: combining a map with a
            // second filter - no3rd, full, mod - returns ZERO from Steam, so
            // there is no cleverer query to write. The index fills by asking
            // again, which is also why rebuilding it is worth doing.
            if (everyMap)
            {
                int busiest = Math.Min(MapsToRepeat, maps.Count);
                for (int pass = 2; pass <= RepeatBusiestMaps; pass++)
                    for (int i = 0; i < busiest; i++)
                        passes.Add(new SweepPass(app, "map " + maps[i] + " (again #" + pass + ")",
                                                 "map", maps[i]));
            }

            // Where the next ordinary refresh picks up.
            _mapCursor = maps.Count == 0 ? 0 : (_mapCursor + take) % maps.Count;
            return passes;
        }

        /// <summary>
        /// Every map ever seen, busiest first.
        ///
        /// Busiest first matters because a partial sweep is the normal case:
        /// the maps carrying thousands of servers are the ones the cap is
        /// hiding, and they should be asked about before a map with four.
        /// </summary>
        private List<string> KnownMaps()
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (string m in ServerStore.LoadKnownMaps())
                counts[m] = 0;

            foreach (var srv in _cache)
            {
                string m = (srv.Map ?? "").Trim();
                if (m.Length == 0) continue;
                int n;
                counts[m] = counts.TryGetValue(m, out n) ? n + 1 : 1;
            }

            return counts.OrderByDescending(k => k.Value)
                         .ThenBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
                         .Select(k => k.Key)
                         .ToList();
        }

        /// <summary>Maps asked about on an ordinary refresh, before the cursor moves on.</summary>
        private const int MapsPerRefresh = 12;

        /// <summary>How many of the busiest maps a full build asks about repeatedly.</summary>
        private const int MapsToRepeat = 6;

        /// <summary>Total times a build asks about those maps. Measured: run 2
        /// found 289 servers run 1 missed, run 3 another 223.</summary>
        private const int RepeatBusiestMaps = 3;

        private int _mapCursor;

        /// <summary>Set by BUILD INDEX: this sweep takes every known map.</summary>
        private bool _buildingIndex;

        /// <summary>What one pass of a sweep actually brought back.</summary>
        private sealed class PassResult
        {
            public string Label;
            public string Map;      // null for the tag passes
            public int Returned;
            public int New;
        }

        private readonly List<PassResult> _sweepResults = new List<PassResult>();

        /// <summary>
        /// Prints what every pass contributed, worst last.
        ///
        /// This is the only way to tell whether a map is worth asking about.
        /// Several real DayZ maps return NOTHING from Steam's map filter even
        /// though servers run them, and without this the sweep just felt slow
        /// for no visible reason.
        /// </summary>
        // The breakdown of WHY servers were screened out is deliberately not
        // logged any more.
        //
        // The log is visible in the launcher, and the launcher is public. Every
        // line describing a rule - what a slot count has to be, how many
        // servers on one address is too many - is a line telling a redirect
        // farm precisely what to change to get back in. The counts alone say
        // whether the screening is working; the reasons are written to the
        // local diagnostics folder instead, which never leaves the machine.

        private void ReportSweep()
        {
            if (_sweepResults.Count == 0) return;

            Log("");
            Log("  ---- sweep results: " + _sweepResults.Count + " passes ----");
            Log(string.Format("  {0,-28} {1,8} {2,8}", "pass", "returned", "new"));

            int empty = 0;
            foreach (var r in _sweepResults.OrderByDescending(r => r.New).ThenBy(r => r.Label))
            {
                if (r.Returned == 0) { empty++; continue; }
                Log(string.Format("  {0,-28} {1,8} {2,8}", r.Label, r.Returned, r.New));
            }

            // What the repeats were worth on their own, since that is the part
            // that looks like wasted time and is not.
            var repeats = _sweepResults.Where(r => r.Label.Contains("(again")).ToList();
            if (repeats.Count > 0)
                Log("  repeat passes alone found " + repeats.Sum(r => r.New)
                    + " server(s) the first pass missed.");

            if (empty > 0)
            {
                var names = _sweepResults.Where(r => r.Returned == 0)
                                         .Select(r => r.Label)
                                         .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
                Log("  " + empty + " pass(es) returned nothing at all: "
                    + string.Join(", ", names.ToArray()));
            }

            Log("  ---- total " + _mergedKeys.Count + " unique servers ----");
            Log("");

            // Remember what each map gave, so a map that has twice returned
            // nothing can stop costing a round trip on every build.
            try
            {
                // The BEST reading for each map, not the last. A map asked
                // about three times must not be judged on whichever run
                // happened to come back thin.
                var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in _sweepResults)
                {
                    if (r.Map == null) continue;
                    int had;
                    if (!counts.TryGetValue(r.Map, out had) || r.Returned > had)
                        counts[r.Map] = r.Returned;
                }
                ServerStore.RecordMapCounts(counts);
            }
            catch { }

            _sweepResults.Clear();
        }

        /// <summary>
        /// Files away every map in the current list, so the next sweep can ask
        /// about maps this one only just discovered.
        /// </summary>
        private void RememberMapsSeen()
        {
            try
            {
                int added = ServerStore.RememberMaps(_cache.Select(c => c.Map));
                if (added > 0) Log("  Map index: " + added + " new map(s) learnt, "
                                   + ServerStore.LoadKnownMaps().Count + " known.");
            }
            catch { }
        }

        /// <summary>
        /// Sweeps every known map in one run rather than a slice.
        ///
        /// This is the "build me the whole list now" button. It takes a few
        /// minutes and the list fills as it goes, so it is not modal and the
        /// player can keep searching while it runs.
        /// </summary>
        private void BuildIndexNow()
        {
            if (!IsSteamTab(_tab))
            {
                _status.Text = "Switch to the Community or Official tab to build the index.";
                return;
            }

            if (_pollTimer.Enabled)
            {
                Log("A server query is already running - not starting another.");
                return;
            }

            int maps = KnownMaps().Count;
            if (MessageBox.Show(
                    "Ask Steam for every one of the " + maps + " maps the launcher knows about?"
                    + "\r\n\r\nThis reaches past Steam's 10,000-server limit and builds the full "
                    + "list, but it takes several minutes. The list fills as it goes and you can "
                    + "keep using the launcher while it runs.",
                    "Build server index", MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;

            _buildingIndex = true;
            _mapCursor = 0;
            RefreshCurrent(true);
        }

        /// <summary>
        /// The player's own filters plus whatever narrowing this pass adds.
        ///
        /// A pass never REPLACES a filter the player set. If they have already
        /// asked for one map, the map passes are pointless and their choice
        /// wins - their filter is the reason the list is small enough not to
        /// need sweeping in the first place.
        /// </summary>
        private List<KeyValuePair<string, string>> FiltersFor(SweepPass pass, ListKind kind)
        {
            var f = kind == ListKind.Internet
                  ? _filters.ToSteamFilters()
                  : new List<KeyValuePair<string, string>>();

            if (pass.Extra != null)
                foreach (var kv in pass.Extra)
                    if (!f.Any(x => x.Key == kv.Key)) f.Add(kv);

            return f;
        }

        private static string BuildLabel(uint app)
        {
            return app == (uint)A2S.ExperimentalAppId ? "Experimental" : "stable";
        }

        private static readonly TimeSpan CacheFreshFor = TimeSpan.FromMinutes(1);

        /// <summary>How often the arriving list is actually redrawn.</summary>
        private static readonly TimeSpan RenderEvery = TimeSpan.FromMilliseconds(2500);
        private DateTime _lastRenderAt = DateTime.MinValue;

        private const int InternetPollMs = 700;
        private const int SmallListPollMs = 250;

        private bool PollingSmallList
        {
            get { return _pollKind != ListKind.Internet; }
        }

        private void OnPoll(object sender, EventArgs e)
        {
            bool done;
            int raw;
            _pollTicks++;
            _browser = SteamServerList.Poll(out done, out raw);

            int minTicks = PollingSmallList ? 2 : 5;
            if (_pollTicks < minTicks) done = false;

            if (raw > 0 && raw == _lastRawCount) _stableTicks++;
            else _stableTicks = 0;

            if (_stableTicks >= (PollingSmallList ? 4 : 12)) done = true;

            if (PollingSmallList && raw == 0 && _pollTicks >= 8) done = true;

            if (_pollTicks > (PollingSmallList ? 80 : 150)) done = true;

            var combined = _merged.Count == 0 ? _browser : Combine(_browser);
            _caches[CacheKey] = combined;
            _cacheTimes[CacheKey] = DateTime.Now;

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var srv in _browser) _lastSeen[srv.Endpoint] = now;

            // Steam's count creeps up continuously while a list is arriving.
            // Re-rendering on every change meant a full repaint several times a
            // second; once every couple of seconds keeps the list visibly
            // filling without the flicker.
            bool worthRendering = done
                || (raw != _lastRawCount
                    && (DateTime.UtcNow - _lastRenderAt) >= RenderEvery);

            if (worthRendering)
            {
                _lastRawCount = raw;
                _lastRenderAt = DateTime.UtcNow;
                _lastShownCount = RenderFromCache();
            }

            UpdateStatus(done ? null : "(loading from Steam...)");

            if (!done) return;

            int before = _mergedKeys.Count;
            foreach (var srv in _browser)
                if (_mergedKeys.Add(srv.Endpoint)) _merged.Add(srv);
            int gained = _mergedKeys.Count - before;

            Log("  " + _pollPass.Label + ": " + _browser.Count + " returned, " + gained + " new"
                + (raw >= NearlyCapped ? "  (TRUNCATED at Steam's cap)" : ""));

            _sweepResults.Add(new PassResult
            {
                Label = _pollPass.Label,
                Map = _pollPass.Map,
                Returned = _browser.Count,
                New = gained
            });

            // The list came back full, which means it was cut short. Ask again
            // in narrower slices - see SweepPass for what that is worth.
            // Maps discovered by THIS pass are available to the passes queued
            // below it, so a new community map starts being indexed the same
            // run it is first seen.
            RememberMapsSeen();

            if ((raw >= NearlyCapped || _buildingIndex) && !_deepSweepQueued
                && _pollKind == ListKind.Internet && _appQueue.Count == 0)
            {
                _deepSweepQueued = true;
                var extra = DeepSweepPasses(_pollApp, _buildingIndex);
                foreach (var ep in extra) _appQueue.Enqueue(ep);

                Log(_buildingIndex
                    ? "  BUILD INDEX: sweeping every known map - " + extra.Count + " passes."
                    : "  Steam capped that list at " + raw + ". Sweeping it in "
                      + extra.Count + " narrower passes to reach past the cap.");
            }

            if (_appQueue.Count > 0)
            {
                var next = _appQueue.Dequeue();
                var nextFilters = FiltersFor(next, _pollKind);
                _lastSteamFilterKey = SteamFilterKey(nextFilters);

                if (SteamServerList.Start(_pollKind, next.App, nextFilters))
                {
                    _pollPass = next;
                    _pollApp = next.App;
                    _pollTicks = 0;
                    _stableTicks = 0;
                    _lastRawCount = -1;

                    UpdateStatus((_buildingIndex ? "BUILDING SERVER INDEX - " : "sweeping - ")
                                 + next.Label + ", " + _appQueue.Count + " passes left...");
                    return;
                }

                Log("  Steam refused the " + next.Label + " query; showing what we have.");
            }

            _pollTimer.Stop();
            _appendWhileLoading = false;

            // The list was left in arrival order while it loaded so nothing
            // moved under the player. Now that it has stopped growing, put it
            // in order once.
            ResortNow(true);

            ReportSweep();

            // The evidence goes to a local file rather than the log. See
            // Diagnostics for why.
            Diagnostics.WriteServerList(combined, Flagged, _serversPerIp);
            Log("  Diagnostics written to " + Diagnostics.Folder);
            _buildingIndex = false;
            RememberMapsSeen();

            Log("Master list cached: " + combined.Count + " servers across "
                + ServerStore.LoadKnownMaps().Count
                + " known maps. Searching now filters this list instantly.");

            _indexByEndpoint.Clear();
            _indexDirty = true;
            ServerStore.SaveList(CacheKey, combined, _lastSeen);
            _indexDirty = false;
        }

        private List<BrowserServer> Combine(List<BrowserServer> current)
        {
            var all = new List<BrowserServer>(_merged);
            var seen = new HashSet<string>(_mergedKeys);
            foreach (var srv in current)
                if (seen.Add(srv.Endpoint)) all.Add(srv);
            return all;
        }

        private int RenderFromCache()
        {
            var cache = _cache;
            RecomputeFarms(cache);

            _hiddenFakes = 0;
            Flagged.Clear();

            var rows = new List<Row>(cache.Count);
            foreach (var srv in cache)
            {
                string reason = FakeReason(srv);
                if (reason != null)
                {
                    Flagged.Add(new FlaggedServer
                    {
                        Name = IpRegion.ReadableName(srv.Name),
                        Host = srv.Host,
                        Port = srv.Port,
                        Reason = reason
                    });

                    if (_filters.HideFakes) { _hiddenFakes++; continue; }
                }

                if (!_filters.Matches(srv)) continue;

                var row = new Row
                {
                    Name = IpRegion.ReadableName(srv.Name),
                    Map = srv.Map, Host = srv.Host, Port = srv.Port,
                    QueryPort = srv.QueryPort,
                    Players = srv.Players, MaxPlayers = srv.MaxPlayers, Ping = srv.Ping,
                    AppId = srv.AppId, Tags = srv.Tags, Password = srv.Password,
                    Favourite = _favourites.Contains(srv.Endpoint)
                };

                ServerInfo live;
                if (_live.TryGetValue(srv.Endpoint, out live)) Apply(row, live);
                rows.Add(row);
            }

            // WHILE A LIST IS LOADING, NOTHING ALREADY ON SCREEN MOVES.
            //
            // Sorting every render meant a server arriving with 40 players
            // shoved its way into the middle and pushed everything below it
            // down a row - under the pointer, mid-click. Arrivals are appended
            // to the end instead, so the rows being read stay exactly where
            // they are. Pressing a column header or RESORT LIST puts the list
            // in order on demand, and the finished list sorts itself once.
            if (_appendWhileLoading && _rows != null && _rows.Count > 0)
                rows = KeepOrderThenAppend(rows);
            else
                SortRows(rows);

            SetRows(rows, _selectedEndpoint);
            return rows.Count;
        }

        private static string ModSignature(ServerRules rules)
        {
            if (rules == null || rules.Mods == null || rules.Mods.Count == 0) return null;

            var ids = rules.Mods
                .Where(m => m != null && m.WorkshopId != 0)
                .Select(m => m.WorkshopId.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return ids.Length == 0 ? null : string.Join("|", ids);
        }

        private static bool LooksBranded(IEnumerable<string> names)
        {
            var entries = (names ?? Enumerable.Empty<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => Regex.Replace(n, @"[^A-Za-z0-9]+", " ").Trim())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => Regex.Replace(n, @"\s+", " ").Trim())
                .ToList();

            if (entries.Count < 2) return false;

            var exactCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
                exactCounts[entry] = exactCounts.TryGetValue(entry, out var count) ? count + 1 : 1;

            foreach (var kv in exactCounts)
            {
                if (kv.Value >= 2)
                    return true;
            }

            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                foreach (var part in entry.Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
                {
                    string token = part.Trim();
                    if (token.Length < 4) continue;
                    if (token.Equals("server", StringComparison.OrdinalIgnoreCase) ||
                        token.Equals("servers", StringComparison.OrdinalIgnoreCase) ||
                        token.Equals("chernarus", StringComparison.OrdinalIgnoreCase) ||
                        token.Equals("dayz", StringComparison.OrdinalIgnoreCase) ||
                        token.Equals("pvp", StringComparison.OrdinalIgnoreCase) ||
                        token.Equals("pve", StringComparison.OrdinalIgnoreCase) ||
                        token.Equals("vanilla", StringComparison.OrdinalIgnoreCase) ||
                        token.Equals("mod", StringComparison.OrdinalIgnoreCase) ||
                        token.Equals("mods", StringComparison.OrdinalIgnoreCase) ||
                        token.Equals("bases", StringComparison.OrdinalIgnoreCase))
                        continue;

                    counts[token] = counts.TryGetValue(token, out var count) ? count + 1 : 1;
                }
            }

            foreach (var kv in counts)
            {
                if (kv.Value >= Math.Max(2, entries.Count / 2)) return true;
            }
            return false;
        }

        /// <summary>
        /// The (address, name) pairs where one address runs the same name too
        /// many times over. Keyed host + NUL + name so the two cannot run
        /// together into a false match.
        /// </summary>
        private readonly HashSet<string> _farmGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static string GroupKey(string host, string name)
        {
            return (host ?? "") + "\u0000" + (name ?? "").Trim();
        }

        private void RecomputeFarms(List<BrowserServer> cache)
        {
            _farmIps.Clear();
            _farmGroups.Clear();

            // Plain count of servers per address. The farm rules below look for
            // patterns; this is just the crowd size, which is what a sentinel
            // slot count has to be weighed against.
            _serversPerIp.Clear();
            _sentinelOnlyIps.Clear();

            var sentinelCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var srv in cache)
            {
                if (srv == null || string.IsNullOrEmpty(srv.Host)) continue;

                int n;
                _serversPerIp[srv.Host] = _serversPerIp.TryGetValue(srv.Host, out n) ? n + 1 : 1;

                if (SlotsUnreported(srv.MaxPlayers))
                {
                    int k;
                    sentinelCount[srv.Host] = sentinelCount.TryGetValue(srv.Host, out k) ? k + 1 : 1;
                }
            }

            // THE SAME NAME ON MANY DIFFERENT ADDRESSES.
            //
            // A popular server group gets impersonated wholesale: one browse
            // found "KarmaKrew Chernarus #1 EU" on THIRTY separate addresses.
            // Only one of them is the real server.
            //
            // This cannot condemn on its own, because the genuine server is one
            // of the thirty - flagging every copy punishes the victim along
            // with the imposters. It is paired with a test for which copy looks
            // real, in ImpersonatedCopy below.
            _addressesPerName.Clear();
            var seen = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var srv in cache)
            {
                if (srv == null || string.IsNullOrWhiteSpace(srv.Name)
                    || string.IsNullOrEmpty(srv.Host)) continue;

                string name = srv.Name.Trim();
                HashSet<string> hosts;
                if (!seen.TryGetValue(name, out hosts))
                {
                    hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    seen[name] = hosts;
                }
                hosts.Add(srv.Host);
            }
            foreach (var kv in seen) _addressesPerName[kv.Key] = kv.Value.Count;

            // An address is "sentinel only" when every server on it reports a
            // sentinel capacity and there is more than one of them. One server
            // reporting 127 is a server with nothing to say; six of them on one
            // address, all saying nothing, is a generator.
            foreach (var kv in sentinelCount)
            {
                int total;
                if (!_serversPerIp.TryGetValue(kv.Key, out total)) continue;
                if (total >= 2 && kv.Value == total) _sentinelOnlyIps.Add(kv.Key);
            }

            // One address, the same name over and over. Counted FIRST, because
            // every rule below collapses names into a set and therefore cannot
            // see a repeat at all - twenty identical names look like one.
            var sameName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var srv in cache)
            {
                if (srv == null || string.IsNullOrEmpty(srv.Host) || string.IsNullOrWhiteSpace(srv.Name))
                    continue;
                string gk = GroupKey(srv.Host, srv.Name);
                int c;
                sameName.TryGetValue(gk, out c);
                sameName[gk] = c + 1;
            }
            foreach (var kv in sameName)
                if (kv.Value >= BrowserFilters.FarmSameNamePerIp) _farmGroups.Add(kv.Key);
            _farmSubnets.Clear();
            if (cache.Count == 0) return;

            var names = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var ipsNearFull = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var ipsModSets = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var s in cache)
            {
                if (string.IsNullOrEmpty(s.Host)) continue;

                int c;
                counts.TryGetValue(s.Host, out c);
                counts[s.Host] = c + 1;

                HashSet<string> set;
                if (!names.TryGetValue(s.Host, out set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    names[s.Host] = set;
                }
                if (!string.IsNullOrEmpty(s.Name)) set.Add(s.Name);

                ServerRules rules;
                lock (_modLock)
                {
                    _modCache.TryGetValue(s.Endpoint, out rules);
                }

                if (rules != null)
                {
                    string sig = ModSignature(rules);
                    if (!string.IsNullOrEmpty(sig))
                    {
                        HashSet<string> mods;
                        if (!ipsModSets.TryGetValue(s.Host, out mods))
                        {
                            mods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            ipsModSets[s.Host] = mods;
                        }
                        mods.Add(sig);
                    }
                }

                if (ImpossibleCapacity(s))
                {
                    int nearFull;
                    ipsNearFull.TryGetValue(s.Host, out nearFull);
                    ipsNearFull[s.Host] = nearFull + 1;
                }
            }

            foreach (var kv in counts)
            {
                bool branded = LooksBranded(names[kv.Key]);
                bool sameModSet = ipsModSets.TryGetValue(kv.Key, out var mods) && mods.Count >= 2 && names[kv.Key].Count >= 4 && kv.Value >= 4;

                if (!branded && sameModSet)
                {
                    _farmIps.Add(kv.Key);
                    continue;
                }

                if (!branded && kv.Value >= BrowserFilters.FarmServersPerIp && names[kv.Key].Count >= BrowserFilters.FarmNamesPerIp)
                {
                    _farmIps.Add(kv.Key);
                    continue;
                }

                if (!branded && ipsNearFull.TryGetValue(kv.Key, out var nearFull) && nearFull >= 4 && names[kv.Key].Count >= 4)
                    _farmIps.Add(kv.Key);
            }

            var netCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var netAddrs = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var netNames = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var netNearFull = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var netModSets = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var s in cache)
            {
                if (string.IsNullOrEmpty(s.Host)) continue;
                string net = BrowserFilters.Subnet24(s.Host);

                int c;
                netCount.TryGetValue(net, out c);
                netCount[net] = c + 1;

                HashSet<string> addrs;
                if (!netAddrs.TryGetValue(net, out addrs))
                {
                    addrs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    netAddrs[net] = addrs;
                }
                addrs.Add(s.Host);

                HashSet<string> nm;
                if (!netNames.TryGetValue(net, out nm))
                {
                    nm = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    netNames[net] = nm;
                }
                if (!string.IsNullOrEmpty(s.Name)) nm.Add(s.Name);

                ServerRules rules;
                lock (_modLock)
                {
                    _modCache.TryGetValue(s.Endpoint, out rules);
                }

                if (rules != null)
                {
                    string sig = ModSignature(rules);
                    if (!string.IsNullOrEmpty(sig))
                    {
                        HashSet<string> mods;
                        if (!netModSets.TryGetValue(net, out mods))
                        {
                            mods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            netModSets[net] = mods;
                        }
                        mods.Add(sig);
                    }
                }

                if (ImpossibleCapacity(s))
                {
                    int nearFull;
                    netNearFull.TryGetValue(net, out nearFull);
                    netNearFull[net] = nearFull + 1;
                }
            }

            foreach (var kv in netCount)
            {
                bool branded = LooksBranded(netNames[kv.Key]);
                bool sameModSet = netModSets.TryGetValue(kv.Key, out var mods) && mods.Count >= 2 && netNames[kv.Key].Count >= 6 && kv.Value >= 12;

                if (!branded && sameModSet)
                {
                    _farmSubnets.Add(kv.Key);
                    continue;
                }

                if (!branded && kv.Value >= BrowserFilters.FarmServersPerSubnet &&
                    netNames[kv.Key].Count >= BrowserFilters.FarmNamesPerSubnet)
                {
                    double perAddress = kv.Value / (double)Math.Max(1, netAddrs[kv.Key].Count);
                    if (perAddress >= BrowserFilters.FarmServersPerAddress)
                    {
                        _farmSubnets.Add(kv.Key);
                        continue;
                    }
                }

                if (!branded && netNearFull.TryGetValue(kv.Key, out var nearFull) && nearFull >= 5 &&
                    netNames[kv.Key].Count >= 6 && kv.Value >= 12)
                {
                    _farmSubnets.Add(kv.Key);
                }
            }

            foreach (string ok in _allowed)
            {
                _farmIps.Remove(ok);
                _farmSubnets.Remove(ok);
                _farmGroups.RemoveWhere(g => g.StartsWith(ok + "\u0000", StringComparison.OrdinalIgnoreCase));
            }

            // GUILT BY ADDRESS, worked out AFTER every other rule has run.
            //
            // A generator does not always finish the job: among a hundred
            // obvious fabrications it leaves two or three entries with a
            // plausible capacity and a plausible player count, and those slip
            // through every test that looks at one server at a time.
            //
            // But they are sitting on an address whose entire population has
            // just been condemned. An address running five or more servers of
            // which most are fabrications is not a host with a few bad
            // neighbours - it is the generator, and the survivors are its work
            // too. Requires five so that a small real host with one odd entry
            // is never swept up, and a clear majority so that a shared address
            // is not condemned by its worst tenant.
            _mostlyFakeIps.Clear();

            var perIpTotal = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var perIpFake = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var srv in cache)
            {
                if (srv == null || string.IsNullOrEmpty(srv.Host)) continue;

                int t;
                perIpTotal[srv.Host] = perIpTotal.TryGetValue(srv.Host, out t) ? t + 1 : 1;

                if (FakeReason(srv) == null) continue;
                int f;
                perIpFake[srv.Host] = perIpFake.TryGetValue(srv.Host, out f) ? f + 1 : 1;
            }

            foreach (var kv in perIpFake)
            {
                int total;
                if (!perIpTotal.TryGetValue(kv.Key, out total)) continue;
                if (total < MostlyFakeMinServers) continue;
                if (kv.Value * 100 >= total * MostlyFakePercent) _mostlyFakeIps.Add(kv.Key);
            }

        }

        private string FakeReason(BrowserServer s)
        {
            if (s == null) return null;

            if (_favourites.Contains(s.Endpoint)) return null;

            if (_allowed.Contains(s.Host) ||
                _allowed.Contains(BrowserFilters.Subnet24(s.Host))) return null;

            if (ImpossibleCapacity(s))
            {
                if (s.MaxPlayers == 255) return "reports 255 slots";
                if (!SlotsUnreported(s.MaxPlayers))
                    return "claims " + s.MaxPlayers + " slots (DayZ maximum realistic capacity is "
                         + MaxRealisticSlots + ")";
                if (s.Players == s.MaxPlayers)
                    return "claims to be exactly full at " + s.MaxPlayers + "/" + s.MaxPlayers;
                if (_sentinelOnlyIps.Contains(s.Host ?? ""))
                    return "every server on " + s.Host + " reports no real slot count";
                return "reports no real slot count (" + s.MaxPlayers + ") and shares "
                     + s.Host + " with " + _serversPerIp[s.Host] + " servers";
            }

            if (ImpersonatedCopy(s))
                return "\"" + s.Name.Trim() + "\" also appears on "
                     + (_addressesPerName[s.Name.Trim()] - 1) + " other address(es), and this "
                     + "copy does not look like the original";

            if (_farmGroups.Contains(GroupKey(s.Host, s.Name)))
                return "address " + s.Host + " runs " + BrowserFilters.FarmSameNamePerIp
                     + "+ servers under this exact name";

            if (_farmIps.Contains(s.Host))
                return "address " + s.Host + " runs many servers under many names or identical mod arrays";

            string net = BrowserFilters.Subnet24(s.Host);
            if (_farmSubnets.Contains(net))
                return "subnet " + net + ".x is a redirect farm";

            // Deliberately last, and deliberately reading a set built during the
            // PREVIOUS scan rather than anything computed here - the set is
            // populated by calling this method, and a rule that consulted its
            // own output would chase its tail.
            if (_mostlyFakeIps.Contains(s.Host ?? ""))
                return "most of what runs on " + s.Host + " is fabricated";

            return null;
        }

        // ---------------------------------------------------- query pool ----

        private void StartPingPool()
        {
            if (_pingPool != null) return;
            _pingPool = new Thread[PingWorkers];
            for (int i = 0; i < PingWorkers; i++)
            {
                var t = new Thread(PingLoop) { IsBackground = true, Name = "a2s-" + i };
                _pingPool[i] = t;
                t.Start();
            }
        }

        /// <summary>One A2S attempt, never throwing.</summary>
        private static ServerInfo Ask(Row row)
        {
            try { return A2S.GetInfoAt(row.Host, row.EffectiveQueryPort, 1200); }
            catch { return new ServerInfo { Error = "query failed" }; }
        }

        private void PingLoop()
        {
            while (!_closing)
            {
                Row row = null;

                // Rows the player can SEE are urgent; the rest are filler. The
                // difference decides whether this worker may also spend a
                // second query reading the server's mod list - see below.
                bool urgent = false;

                lock (_pingLock)
                {
                    foreach (var candidate in _pingWanted)
                    {
                        if (_pingBusy.Contains(candidate.Endpoint)) continue;
                        if (_asked.Contains(candidate.Endpoint)) continue;
                        row = candidate;
                        urgent = true;
                        break;
                    }

                    while (row == null && _sweep < _pingAll.Count)
                    {
                        var candidate = _pingAll[_sweep++];
                        if (_pingBusy.Contains(candidate.Endpoint)) continue;
                        if (_asked.Contains(candidate.Endpoint)) continue;
                        row = candidate;
                    }

                    if (row != null)
                    {
                        _pingBusy.Add(row.Endpoint);
                        _asked.Add(row.Endpoint);
                    }
                }

                if (row == null) { Thread.Sleep(200); continue; }

                // ASK TWICE BEFORE GIVING UP.
                //
                // A2S is UDP, and a query is two round trips - the challenge,
                // then the answer - so there are four packets to lose. Measured
                // against a live server 370ms away: it answered 15 times out of
                // 16 and dropped one, with the timeout making no difference.
                // That server was being shown as OFFLINE with 73 players on it.
                //
                // A single retry takes a ~6% miss down to well under 1%, and
                // costs nothing for the servers that answer first time.
                ServerInfo info = Ask(row);
                if (!info.Online && !_closing) info = Ask(row);

                // WHILE WE HAVE ITS ATTENTION, ASK WHAT IT RUNS.
                //
                // A mod list only comes from A2S_RULES, and it used to be asked
                // only when somebody clicked a server - so the launcher knew
                // the mods of FOURTEEN servers out of twelve thousand and
                // filtering by mod matched almost nothing.
                //
                // NEVER on an urgent row. A row the player is looking at must
                // get its ping and nothing else; the extra query would add
                // ~85 ms to something they are watching. Mod lists are only
                // collected on the background sweep, where nobody is waiting.
                //
                // Measured cost of a full pass: 228 ms per server blended
                // (83 ms when a server answers, 1.2 s for the 13% that never
                // do), about five minutes across the whole index on sixteen
                // workers, 16 MB down, 0.4 MB up.
                if (!urgent && info.Online && _sweepModLists && KnownModsFor(row) == null)
                {
                    try
                    {
                        var rules = A2S.GetRulesAt(row.Host, row.EffectiveQueryPort, 1800);
                        if (rules != null) RememberServerMods(row.Endpoint, rules);
                    }
                    catch { }
                }

                lock (_pingLock) { _pingBusy.Remove(row.Endpoint); }

                var captured = row;
                var result = info;
                try { BeginInvoke((Action)(() => ApplyLive(captured, result))); }
                catch (InvalidOperationException) { return; }
            }
        }

        /// <summary>
        /// True when this list is the same servers in the same order as the one
        /// already on screen. Compared by endpoint, which is what identifies a
        /// server - the row objects themselves are rebuilt each render.
        /// </summary>
        /// <summary>
        /// Inserts the notice above the block of unconfigured servers.
        ///
        /// Those servers sort to the bottom and stay there, and without a
        /// marker the list just appears to trail off into junk - people scroll
        /// into them and assume the launcher is broken. A blank gap and a line
        /// of large text says what they are looking at.
        ///
        /// Done here rather than in the sort so that every path gets it: the
        /// full sort, the append-while-loading path, and the favourites tab.
        /// </summary>
        private List<Row> WithHeadings(List<Row> rows)
        {
            if (rows == null || rows.Count == 0) return rows;

            // Strip any notice already in the list before adding one.
            //
            // The append-while-loading path hands back the list that is on
            // screen, which has already been decorated - so without this, every
            // render inserted another notice and another pair of blank rows
            // until the list was mostly gaps.
            //
            // Into a COPY, never in place: the caller owns that list and may
            // still be holding it, and quietly shortening someone else's list
            // is how a count comes out wrong three screens away.
            bool alreadyDecorated = false;
            foreach (var r in rows)
                if (r.IsHeading) { alreadyDecorated = true; break; }

            if (alreadyDecorated)
            {
                var plain = new List<Row>(rows.Count);
                foreach (var r in rows)
                    if (!r.IsHeading) plain.Add(r);
                rows = plain;
            }

            int first = -1;
            for (int i = 0; i < rows.Count; i++)
            {
                if (!Unconfigured(rows[i])) continue;
                first = i;
                break;
            }

            // Nothing unconfigured, or the whole list is - in which case a
            // heading at row zero helps nobody.
            if (first <= 0) return rows;

            var result = new List<Row>(rows.Count + HeadingGap + 1);
            for (int i = 0; i < first; i++) result.Add(rows[i]);

            // Blank rows first, so the notice is not crowded against the last
            // real server.
            for (int i = 0; i < HeadingGap; i++)
                result.Add(new Row { Heading = "", Host = "", Port = -(i + 1) });

            result.Add(new Row
            {
                Heading = "THESE SERVERS APPEAR TO NOT BE CONFIGURED YET",
                Host = "",
                Port = -99
            });

            for (int i = first; i < rows.Count; i++) result.Add(rows[i]);
            return result;
        }

        /// <summary>Blank rows above the notice.</summary>
        private const int HeadingGap = 2;

        /// <summary>
        /// Widens the Name column to fit the longest name on screen.
        ///
        /// MEASURES ONLY WHAT IS VISIBLE. Measuring twelve thousand strings on
        /// every render would cost more than the render; the rows in view are
        /// at most a few dozen and they are the only ones whose truncation
        /// anyone can see. Scrolling re-measures, so a longer name further down
        /// widens it when it arrives.
        ///
        /// Bounded at both ends: never narrower than the original width, and
        /// never so wide it pushes Players and Ping off the right edge.
        /// </summary>
        private void FitNameColumn()
        {
            if (_list == null || _list.Columns.Count <= ColName) return;
            if (_rows == null || _rows.Count == 0) return;

            try
            {
                int first = _list.TopItem != null ? _list.TopItem.Index : 0;
                if (first < 0) first = 0;

                int rowHeight = Math.Max(1, _list.TopItem != null ? _list.TopItem.Bounds.Height : 18);
                int visible = Math.Max(10, _list.ClientSize.Height / rowHeight + 4);
                int last = Math.Min(_rows.Count, first + visible);

                int widest = 0;
                using (var g = _list.CreateGraphics())
                {
                    for (int i = first; i < last; i++)
                    {
                        var r = _rows[i];
                        if (r.IsHeading || string.IsNullOrEmpty(r.Name)) continue;

                        int w = (int)Math.Ceiling(g.MeasureString(r.Name, _list.Font).Width);
                        if (w > widest) widest = w;
                    }
                }

                if (widest == 0) return;

                // A little air after the text, and a ceiling so the columns
                // that matter for choosing a server stay on screen.
                int want = widest + 18;
                int ceiling = Math.Max(NameColumnMin, _list.ClientSize.Width - 420);

                want = Math.Max(NameColumnMin, Math.Min(want, ceiling));

                // Only touch it when it actually moved - setting Width forces a
                // repaint of the whole list.
                if (Math.Abs(_list.Columns[ColName].Width - want) > 4)
                    _list.Columns[ColName].Width = want;
            }
            catch { }
        }

        /// <summary>The Name column never shrinks below the width it was designed at.</summary>
        private const int NameColumnMin = 244;

        private bool SameOrder(List<Row> rows)
        {
            if (_rows == null || rows == null) return false;
            if (_rows.Count != rows.Count || rows.Count == 0) return false;
            if (_list.VirtualListSize != rows.Count) return false;

            for (int i = 0; i < rows.Count; i++)
                if (!string.Equals(_rows[i].Endpoint, rows[i].Endpoint,
                                   StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        private void Enqueue(IEnumerable<Row> rows, bool force)
        {
            lock (_pingLock)
            {
                _pingWanted.Clear();
                foreach (var r in rows)
                {
                    if (force) _asked.Remove(r.Endpoint);
                    _pingWanted.Add(r);
                }
            }
            StartPingPool();
        }

        /// <summary>
        /// Worth spending a query on.
        ///
        /// A server still carrying its host's stock name has never been set up.
        /// Pinging thousands of them costs real time on every sweep and tells
        /// nobody anything, so they are listed but never queried - they sit at
        /// the bottom with a blank status, which is the honest answer.
        /// </summary>
        private static bool Pingable(Row r)
        {
            return r != null && !r.IsHeading && !Unconfigured(r);
        }

        private void SweepAll(List<Row> rows)
        {
            // The background sweep walks the whole list, so this is where
            // skipping the stock names saves the most.
            var worth = rows.Where(Pingable).ToList();

            lock (_pingLock)
            {
                _pingAll = worth;
                _sweep = 0;
            }
            StartPingPool();
        }

        private void OnVisTick(object sender, EventArgs e)
        {
            if (_rows.Count == 0) return;

            int top = _list.TopItem != null ? _list.TopItem.Index : 0;
            if (top == _lastTopIndex) return;
            _lastTopIndex = top;

            int rowHeight = Math.Max(1, _list.TopItem != null ? _list.TopItem.Bounds.Height : 18);
            int perPage = Math.Max(1, _list.ClientSize.Height / rowHeight);
            int end = Math.Min(_rows.Count, top + perPage + 4);

            var want = new List<Row>(end - top);
            for (int i = Math.Max(0, top); i < end; i++)
                if (Pingable(_rows[i])) want.Add(_rows[i]);
            Enqueue(want, false);

            // New rows are in view, so the longest visible name may have
            // changed.
            FitNameColumn();
        }

        private void ApplyLive(Row row, ServerInfo info)
        {
            _live[row.Endpoint] = info;
            _checked++;
            Apply(row, info);

            // The index is what the player searches, so what the server SAYS
            // about itself has to go back into it. Steam's browser copy of a
            // name can be hours stale; the server's own reply cannot. Without
            // this a rename showed up for a moment and was then overwritten by
            // the next render, and was never saved.
            UpdateIndexFrom(row.Endpoint, info);

            // Only this one row is repainted. NOT a re-render and NOT a re-sort:
            // a server renaming itself, filling up or going quiet must change
            // the line the player is reading, never move it out from under them.
            Redraw(row.Endpoint);

            if (IsSteamTab(_tab)) UpdateStatus(null);

            // Offline servers DO sort to the bottom - but only once the sweep
            // has gone quiet.
            //
            // Sorting the moment each one is found took every row below it up
            // one, mid-read and mid-click, which is exactly what must not
            // happen. Waiting until nothing is left to check lets the list
            // settle once, when the player has stopped watching rows arrive.
            if (!info.Online && !_idleSort.Enabled) _idleSort.Start();

            if (!IsSteamTab(_tab)) FinishMine();
        }

        /// <summary>
        /// Folds a server's own answer back into the cached index.
        ///
        /// The index exists so that searching is instant - it is answered from
        /// memory, never from the network. That only holds up if what is in it
        /// is what the server currently says, so every live reply is written
        /// back here and saved with the rest.
        /// </summary>
        private void UpdateIndexFrom(string endpoint, ServerInfo info)
        {
            if (info == null || !info.Online || string.IsNullOrEmpty(endpoint)) return;

            BrowserServer entry;
            if (!_indexByEndpoint.TryGetValue(endpoint, out entry))
            {
                entry = _cache.FirstOrDefault(
                    c => string.Equals(c.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase));
                if (entry == null) return;
                _indexByEndpoint[endpoint] = entry;
            }

            bool changed = false;

            // Deliberately not logged. A refresh re-checks thousands of
            // servers and any number of them will have been renamed since they
            // were last seen; a line each buries the sweep totals, which are
            // the part worth reading. The row updates on screen regardless.
            // ReadableName here rather than at draw time: the index is what is
            // searched, sorted and saved, so the spelling has to live in it or
            // typing "Miami" would find nothing.
            string liveName = IpRegion.ReadableName(info.Name);
            if (!string.IsNullOrEmpty(liveName) && entry.Name != liveName)
            {
                entry.Name = liveName;
                changed = true;
            }

            if (!string.IsNullOrEmpty(info.Map) && entry.Map != info.Map) { entry.Map = info.Map; changed = true; }
            if (entry.Players != info.Players) { entry.Players = info.Players; changed = true; }
            if (entry.MaxPlayers != info.MaxPlayers) { entry.MaxPlayers = info.MaxPlayers; changed = true; }
            if (info.Keywords != null && entry.Tags != info.Keywords) { entry.Tags = info.Keywords; changed = true; }
            if (entry.Password != info.Password) { entry.Password = info.Password; changed = true; }
            // A2S reports the app id as 64-bit, Steam's browser as 32-bit. Only
            // ever 221100 or 1024020 in practice, so the narrowing is safe, but
            // it is checked rather than assumed.
            if (info.AppId != 0 && info.AppId <= uint.MaxValue && entry.AppId != (uint)info.AppId)
            {
                entry.AppId = (uint)info.AppId;
                changed = true;
            }

            if (changed) _indexDirty = true;
        }

        /// <summary>Endpoint to its entry in the index, so a reply is not a linear scan.</summary>
        private readonly Dictionary<string, BrowserServer> _indexByEndpoint =
            new Dictionary<string, BrowserServer>(StringComparer.OrdinalIgnoreCase);

        private bool _indexDirty;

        /// <summary>
        /// Writes the index to disk if anything has changed since last time.
        /// Cheap when nothing has, which is why it can run on a timer.
        /// </summary>
        private void SaveIndex()
        {
            if (!_indexDirty) return;
            try
            {
                var cache = _cache;
                if (cache == null || cache.Count == 0) return;

                ServerStore.SaveList(CacheKey, cache, _lastSeen);
                _indexDirty = false;
            }
            catch { }
        }

        private void UpdateStatus(string suffix)
        {
            int online = 0, done = 0, servers = 0;
            foreach (var r in _rows)
            {
                if (r.IsHeading) continue;        // the notice is not a server
                servers++;
                if (r.Online.HasValue) done++;
                if (r.Online == true) online++;
            }

            // A build takes minutes, so it says so before anything else -
            // a status line that only counts servers looks like it has hung.
            string text = "";
            if (_buildingIndex) text = "BUILDING SERVER INDEX  -  ";
            else if (_deepSweepQueued && _pollTimer.Enabled) text = "Indexing  -  ";

            text += servers + " servers";
            if (_hiddenFakes > 0) text += "  (" + _hiddenFakes + " fake hidden)";

            // A mod filter can only match servers whose mods have been read, so
            // say how far along that is. Without it the filter looks broken: it
            // matches almost nothing at first and nothing says it is working.
            if (_filters.RequiredMods.Count > 0)
            {
                text += string.Format("  -  mod lists read for {0} of {1}{2}",
                                      ModListsKnown(), _cache.Count,
                                      _sweepModLists && PingsOutstanding ? ", still reading..." : "");
            }
            if (done > 0)
                text += string.Format("  -  {0} checked, {1} online{2}",
                                      done, online, done >= servers ? " (all checked)" : "");
            if (!string.IsNullOrEmpty(suffix)) text += "   " + suffix;
            _status.Text = text;
        }

        private void OnSettings(object sender, EventArgs e)
        {
            bool hide;
            bool changed = SettingsDialog.Show(this, Flagged, _allowed, _filters.HideFakes, out hide, FindSteam());
            if (!changed) return;

            _filters.HideFakes = hide;
            if (_chkHideFakes != null) _chkHideFakes.Checked = hide;

            if (IsSteamTab(_tab)) RenderFromCache();
            else RefreshMine();
        }

        private void ClearModCache(Row row)
        {
            if (row == null) return;
            lock (_modLock)
            {
                _modCache.Remove(row.Endpoint);
                _modLoading.Remove(row.Endpoint);
            }
        }

        private void RefreshOneRow(Row row)
        {
            if (row == null) return;
            ClearModCache(row);
            lock (_pingLock) { _asked.Remove(row.Endpoint); }
            Enqueue(new[] { row }, true);
            if (SelectedRow != null && SelectedRow.Endpoint == row.Endpoint)
                ShowMods(true);
            _status.Text = "Re-checking " + row.Endpoint + "...";
        }

        private void OnListClick(object sender, MouseEventArgs e)
        {
            var hit = _list.HitTest(e.Location);
            if (hit.Item == null || hit.SubItem == null) return;

            int col = hit.Item.SubItems.IndexOf(hit.SubItem);
            int index = hit.Item.Index;
            if (index < 0 || index >= _rows.Count) return;
            var row = _rows[index];

            if (col == ColRefresh) { RefreshOneRow(row); return; }
            if (col == ColStar) { ToggleFavourite(row); return; }
        }

        private void OnListMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            var hit = _list.HitTest(e.Location);
            if (hit.Item != null) hit.Item.Selected = true;
        }

        // ------------------------------------------------ filter/search UI ----
        private void OnSearchKey(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                ApplyFilters();
            }
        }

        private void ReadFilterUi()
        {
            string search = _search != null ? _search.Text.Trim() : "";

            if (BrowserFilters.LooksLikeAddressSearch(search))
            {
                _filters.Name = _fName != null ? _fName.Text.Trim() : "";
                _filters.Address = search;
            }
            else
            {
                _filters.Name = search.Length > 0
                    ? search
                    : (_fName != null ? _fName.Text.Trim() : "");
                _filters.Address = _fAddr?.Text ?? "";
            }

            string mapText = _fMap == null ? "" : (_fMap.Text ?? "").Trim();
            if (mapText.Equals("(any map)", StringComparison.OrdinalIgnoreCase)) mapText = "";
            _filters.Map = mapText;
            _filters.Search = _search?.Text ?? "";
            ReadCountryFilter();

            // THE TAB DECIDES. Nothing was setting this, so it sat on Any and
            // both tabs showed each other's servers: OFFICIAL was listing
            // community servers, and COMMUNITY was listing Bohemia's.
            //
            // Applied after the rest of the panel is read so it cannot be
            // overridden by a stale control, and applied to BOTH the Steam
            // query and the local match - the query narrows what arrives, the
            // local test keeps anything already cached from leaking through.
            switch (_tab)
            {
                case Tab.Official:
                    _filters.Official = TriState.Enabled;
                    break;

                case Tab.Community:
                    _filters.Official = TriState.Disabled;
                    break;

                default:
                    // Recent, Friends, LAN and Favourites are lists of servers
                    // the player has a relationship with. Which hive they are
                    // on is not the point, so neither is filtered out.
                    _filters.Official = TriState.Any;
                    break;
            }
            int ping;
            _filters.MaxPing = int.TryParse(_fPing?.Text, out ping) ? ping : 0;
            if (_playerRange != null)
            {
                _filters.MinPlayers = _playerRange.Low;
                _filters.MaxPlayersWanted = _playerRange.High;
            }
            if (_timeRange != null)
            {
                _filters.MinHour = _timeRange.Low;
                _filters.MaxHour = _timeRange.High;
            }
            _filters.ThirdPersonMode = (TriState)(_segThird?.SelectedIndex ?? 0);
            _filters.ModsMode = (TriState)(_segMods?.SelectedIndex ?? 0);
            _filters.NoPassword = _chkNoPass?.Checked ?? false;
            _filters.HideFull = _chkHideFull?.Checked ?? false;
            _filters.HideEmpty = _chkHideEmpty?.Checked ?? false;
            _filters.HideFakes = _chkHideFakes?.Checked ?? true;
        }

        private void ClearFilterUi()
        {
            if (_fName != null) _fName.Text = "";
            if (_fAddr != null) _fAddr.Text = "";
            if (_fMap != null) _fMap.Text = "";
            if (_fCountry != null) _fCountry.Text = "";

            // The region buttons keep their state in the filters, not in
            // themselves, so clearing means clearing both and repainting.
            _filters.Regions.Clear();
            _filters.HiddenRegions.Clear();
            _filters.Countries.Clear();
            _filters.HiddenCountries.Clear();
            PaintRegionButtons();

            _filters.RequiredMods.Clear();
            PublishRequiredMods();
            if (_modFilter != null) _modFilter.Clear();
            _modSweepTimer.Stop();

            if (_fPing != null) _fPing.Text = "";
            if (_playerRange != null)
                _playerRange.SetRange(0, BrowserFilters.PlayerCeiling);

            _filters.MinPlayers = 0;
            _filters.MaxPlayersWanted = BrowserFilters.PlayerCeiling;
            _filters.GameModes.Clear();
            PaintGameModeButtons();
            if (_timeRange != null) _timeRange.SetRange(0, 24);
            _filters.MinHour = 0;
            _filters.MaxHour = 24;
            if (_segThird != null) _segThird.SelectedIndex = 0;
            if (_segMods != null) _segMods.SelectedIndex = 0;
            if (_chkNoPass != null) _chkNoPass.Checked = false;
            if (_chkHideFull != null) _chkHideFull.Checked = false;
            if (_chkHideEmpty != null) _chkHideEmpty.Checked = false;
            if (_chkHideFakes != null) _chkHideFakes.Checked = true;
        }

        /// <summary>
        /// Puts every map currently in the list into the dropdown.
        ///
        /// Built from the cache rather than a hard-coded list: DayZ gains maps
        /// constantly and a fixed list would be wrong within a month.
        /// </summary>
        private void FillMapChoices()
        {
            if (_fMap == null || _fMap.DroppedDown) return;

            try
            {
                var maps = _cache
                    .Select(s2 => (s2.Map ?? "").Trim())
                    .Where(m => m.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                // Rebuilding while the list is open would close it, so only
                // touch it when the contents have actually changed.
                if (_fMap.Items.Count == maps.Length + 1) return;

                var all = new string[maps.Length + 1];
                all[0] = "(any map)";
                Array.Copy(maps, 0, all, 1, maps.Length);

                RefillCombo(_fMap, all, _fMap.Text);
            }
            catch { }
        }

        /// <summary>
        /// One small tri-state button per region.
        ///
        /// Off means the region is not mentioned at all, which is different
        /// from excluded: with nothing picked every region shows, and the
        /// moment one is picked the rest are implicitly out. Excluded is the
        /// third state, for "everywhere except here".
        /// </summary>
        private void BuildRegionButtons(Control parent, int x, int y)
        {
            _regionButtons.Clear();

            foreach (var region in IpRegion.All)
            {
                var b = new Button
                {
                    Text = ShortName(region),
                    Bounds = new Rectangle(x, y, 34, 23),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Panel2,
                    ForeColor = Color.Gainsboro,
                    Font = new Font("Segoe UI", 7.5f, FontStyle.Bold),
                    Tag = region,
                    TabStop = false
                };
                b.FlatAppearance.BorderColor = Color.FromArgb(75, 75, 82);

                var captured = region;
                b.Click += (s2, e2) =>
                {
                    CycleRegion(captured);
                    PaintRegionButtons();
                    QueueFilter();
                };

                new ToolTip().SetToolTip(b, IpRegion.Name(region)
                    + " - click to show only this, again to hide it, again for neither.");

                parent.Controls.Add(b);
                _regionButtons[region] = b;
                x += 36;
            }

            PaintRegionButtons();
        }

        /// <summary>Short enough to fit on a 34px button.</summary>
        private static string ShortName(WorldRegion r)
        {
            switch (r)
            {
                case WorldRegion.NorthAmerica: return "NA";
                case WorldRegion.SouthAmerica: return "SA";
                case WorldRegion.Europe:       return "EU";
                case WorldRegion.Asia:         return "AS";
                case WorldRegion.Oceania:      return "OC";
                case WorldRegion.MiddleEast:   return "ME";
                case WorldRegion.Africa:       return "AF";
                default:                       return "??";
            }
        }

        private readonly Dictionary<WorldRegion, Button> _regionButtons =
            new Dictionary<WorldRegion, Button>();

        private void CycleRegion(WorldRegion r)
        {
            if (_filters.Regions.Contains(r))
            {
                _filters.Regions.Remove(r);
                _filters.HiddenRegions.Add(r);
            }
            else if (_filters.HiddenRegions.Contains(r))
            {
                _filters.HiddenRegions.Remove(r);
            }
            else
            {
                _filters.Regions.Add(r);
            }
        }

        private void PaintRegionButtons()
        {
            foreach (var kv in _regionButtons)
            {
                bool shown = _filters.Regions.Contains(kv.Key);
                bool hidden = _filters.HiddenRegions.Contains(kv.Key);

                kv.Value.BackColor = shown ? Color.FromArgb(60, 95, 60)
                                   : hidden ? Color.FromArgb(95, 55, 55)
                                   : Panel2;
                kv.Value.ForeColor = shown || hidden ? Color.White : Color.Gainsboro;
                kv.Value.FlatAppearance.BorderColor = shown ? Color.FromArgb(90, 140, 90)
                                                    : hidden ? Color.FromArgb(150, 80, 80)
                                                    : Color.FromArgb(75, 75, 82);
            }
        }

        /// <summary>
        /// Puts the countries actually present into the dropdown, busiest
        /// first, so the list is the handful that matter rather than 239.
        /// </summary>
        private void FillCountryChoices()
        {
            if (_fCountry == null || _fCountry.DroppedDown) return;

            try
            {
                var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var srv in _cache)
                {
                    string cc = IpRegion.Country(srv.Host);
                    if (cc.Length == 0) continue;
                    int n;
                    counts[cc] = counts.TryGetValue(cc, out n) ? n + 1 : 1;
                }

                var ordered = counts.OrderByDescending(k => k.Value)
                                    .Select(k => k.Key + "   (" + k.Value + ")")
                                    .ToArray();

                if (_fCountry.Items.Count == ordered.Length + 1) return;

                var all = new string[ordered.Length + 1];
                all[0] = "(any country)";
                Array.Copy(ordered, 0, all, 1, ordered.Length);

                RefillCombo(_fCountry, all, _fCountry.Text);
            }
            catch { }
        }

        /// <summary>
        /// Reads the country box: codes separated by commas, a leading minus
        /// meaning "hide this one".
        /// </summary>
        private void ReadCountryFilter()
        {
            _filters.Countries.Clear();
            _filters.HiddenCountries.Clear();

            string text = _fCountry == null ? "" : (_fCountry.Text ?? "").Trim();
            if (text.Length == 0
                || text.StartsWith("(any", StringComparison.OrdinalIgnoreCase)) return;

            foreach (string piece in text.Split(',', ';', ' '))
            {
                string t = piece.Trim();
                if (t.Length == 0) continue;

                bool exclude = t[0] == '-' || t[0] == '!';
                if (exclude) t = t.Substring(1).Trim();

                // The dropdown shows "US   (2084)"; keep only the code.
                int space = t.IndexOf(' ');
                if (space > 0) t = t.Substring(0, space);

                if (t.Length != 2) continue;

                if (exclude) _filters.HiddenCountries.Add(t);
                else _filters.Countries.Add(t);
            }
        }

        /// <summary>
        /// Lights the FILTERS button while the panel is open.
        ///
        /// Without it the button looks the same either way, and the only clue
        /// that filters exist at all is a panel the player has to remember
        /// opening.
        /// </summary>
        private void PaintFilterToggle()
        {
            if (_filterToggle == null) return;

            bool open = _filterPanel != null && _filterPanel.Visible;

            // Whether anything is SET matters more than whether the panel is
            // open: a filter left on with the panel closed is invisible, and
            // "why can I not see any servers" usually ends there.
            bool narrowed = _filters != null && _filters.AnyPlayerChose;

            _filterToggle.BackColor = open ? Color.FromArgb(60, 95, 60) : Panel2;

            _filterToggle.ForeColor = narrowed
                ? Color.FromArgb(255, 200, 90)          // amber: filters are on
                : open ? Color.White : Color.Gainsboro;

            _filterToggle.FlatAppearance.BorderColor = narrowed
                ? Color.FromArgb(190, 150, 70)
                : open ? Color.FromArgb(100, 150, 100)
                : Color.FromArgb(75, 75, 82);

            _filterToggle.Text = (narrowed ? "FILTERS \u2022 " : "FILTERS ")
                               + (open ? "\u25B2" : "\u25BC");
        }

        /// <summary>
        /// The chip list changed, so rebuild the filter from it.
        ///
        /// Rebuilt wholesale rather than added to or removed from: the control
        /// owns the entries, and mirroring its edits one at a time is how the
        /// two drift apart.
        /// </summary>
        private void ModFilterChanged()
        {
            _filters.RequiredMods.Clear();
            foreach (string m in _modFilter.Entries) _filters.RequiredMods.Add(m);
            PublishRequiredMods();

            if (_filters.RequiredMods.Count > 0)
            {
                lock (_modListLock) _newModMatches = true;   // render once on the way in
                StartModSweep();
            }

            QueueFilter();
        }

        /// <summary>
        /// Sends the sweep round again for servers whose mods are still
        /// unknown, and re-renders every few seconds so matches appear as they
        /// are found.
        /// </summary>
        private void StartModSweep()
        {
            _sweepModLists = true;

            // Only servers with no mod list yet are re-asked. Clearing the
            // whole "already asked" set would re-ping thousands of servers that
            // were answered seconds ago - the point is to fill the gaps, not to
            // start over.
            lock (_pingLock)
            {
                var again = new List<string>();
                foreach (var r in _rows)
                    if (!r.IsHeading && KnownModsFor(r.Endpoint) == null) again.Add(r.Endpoint);
                foreach (string ep in again) _asked.Remove(ep);
            }

            SweepAll(_rows);
            _modSweepTimer.Start();
        }

        /// <summary>
        /// Warms the mod index and starts collecting, a little after the window
        /// has settled.
        ///
        /// DELIBERATELY LATE. The first thing a player does is look at the list
        /// and type in the search box, and both have to be instant. Reading a
        /// seven megabyte index and starting twelve thousand queries while that
        /// is happening would be felt. A few seconds later, nothing is.
        /// </summary>
        private void BeginBackgroundModIndex()
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    // Off the UI thread: this parses the whole file and builds
                    // the popularity tally once, so opening the dropdown never
                    // has to walk it.
                    int known;
                    lock (_modListLock)
                    {
                        known = ServerMods.Count;

                        _modPopularity.Clear();
                        foreach (var kv in ServerMods)
                            foreach (string m in kv.Value)
                            {
                                int n;
                                _modPopularity[m] = _modPopularity.TryGetValue(m, out n) ? n + 1 : 1;
                            }
                    }

                    BeginInvoke((Action)(() =>
                    {
                        if (_closing) return;
                        Log("Mod index: " + known + " server(s) already known. "
                            + "Reading the rest in the background.");

                        // The panel may already be open and showing empty
                        // lists - fill them now that there is something to put
                        // in them.
                        RefreshFilterChoices();

                        StartModSweep();
                    }));
                }
                catch { }
            });
        }

        /// <summary>
        /// Every mod the launcher has ever seen a server ask for, commonest
        /// first - which puts the mods worth filtering on at the top rather
        /// than burying them among one-server curiosities.
        /// </summary>
        private void FillModChoices()
        {
            // Never while it is open - see RefreshFilterChoices for why.
            if (_fMod == null || _fMod.DroppedDown) return;

            try
            {
                // WHY THIS USED TO TAKE SECONDS
                //
                //   1. It counted every mod of every server - 370,000 entries -
                //      WHILE HOLDING the lock that sixteen sweep workers are
                //      taking constantly. The click waited its turn behind
                //      them, over and over.
                //   2. It then handed a ComboBox three thousand items with
                //      AutoCompleteSource.ListItems set, and WinForms rebuilds
                //      its autocomplete index every time the collection
                //      changes. That alone is worth seconds.
                //
                // Now the tally is kept up to date as mod lists arrive, so this
                // copies a small dictionary under a brief lock and sorts it
                // outside. The list is capped, because nobody scrolls three
                // thousand entries - and the box is editable, so anything not
                // shown can still be typed.
                KeyValuePair<string, int>[] tally;
                lock (_modListLock)
                {
                    tally = _modPopularity.ToArray();
                }

                var ordered = tally
                    .OrderByDescending(k => k.Value)
                    .ThenBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
                    .Take(ModChoicesShown)
                    .Select(k => k.Key + "   (" + k.Value + " servers)")
                    .ToArray();

                // Rebuilding an unchanged list would pay the autocomplete cost
                // for nothing.
                if (_fMod.Items.Count == ordered.Length && _modChoiceCount == tally.Length) return;
                _modChoiceCount = tally.Length;

                // Belt and braces: nothing this method does should be able to
                // reach AddModFilter, but rebuilding a live ComboBox raises
                // several events and one more guard costs nothing.
                if (_fillingModChoices) return;
                _fillingModChoices = true;

                string typed = _fMod.Text;
                try { RefillCombo(_fMod, ordered, typed); }
                finally { _fillingModChoices = false; }
            }
            catch { _fillingModChoices = false; }
        }

        private bool _fillingModChoices;

        /// <summary>
        /// Replaces a combo's items without killing the process.
        ///
        /// WHY THIS IS NOT JUST Items.Clear() + AddRange()
        ///   A ComboBox with AutoCompleteMode set and AutoCompleteSource of
        ///   ListItems hands its list to the Windows shell autocomplete COM
        ///   object. Rebuilding Items while that is attached can fault inside
        ///   the unmanaged object - and an access violation there is not a .NET
        ///   exception. There is no stack trace, no handler runs, the log gets
        ///   no entry and the launcher simply vanishes. Which is exactly what
        ///   clicking the mod dropdown did, with nothing in launcher.log to
        ///   show for it.
        ///
        ///   Detaching autocomplete first makes it an ordinary list update, and
        ///   reattaching afterwards rebuilds the index once against the final
        ///   contents rather than against a list being mutated underneath it.
        /// </summary>
        private static void RefillCombo(ComboBox box, string[] items, string keepText)
        {
            if (box == null) return;

            var mode = box.AutoCompleteMode;
            var source = box.AutoCompleteSource;

            try
            {
                box.AutoCompleteMode = AutoCompleteMode.None;
                box.AutoCompleteSource = AutoCompleteSource.None;

                box.BeginUpdate();
                box.Items.Clear();
                if (items != null && items.Length > 0) box.Items.AddRange(items);
                box.EndUpdate();

                box.Text = keepText ?? "";
            }
            finally
            {
                box.AutoCompleteMode = mode;
                box.AutoCompleteSource = source;
            }
        }

        /// <summary>
        /// How many mods the dropdown offers. The rest are still filterable by
        /// typing - this is about what is worth scrolling, and about keeping
        /// WinForms' autocomplete index small enough to build instantly.
        /// </summary>
        private const int ModChoicesShown = 400;

        private int _modChoiceCount = -1;

        /// <summary>
        /// How many servers run each mod, kept current as lists arrive rather
        /// than counted from scratch when the dropdown opens.
        /// </summary>
        private readonly Dictionary<string, int> _modPopularity =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // ------------------------------------------------ known server mods --
        //
        // What each server was last seen asking for. Read from disk at startup
        // so the mod filter is useful immediately, and added to every time a
        // server answers an A2S_RULES query.

        private readonly object _modListLock = new object();

        private Dictionary<string, List<string>> _serverMods;

        private Dictionary<string, List<string>> ServerMods
        {
            get
            {
                lock (_modListLock)
                {
                    if (_serverMods == null) _serverMods = ServerStore.LoadServerMods();
                    return _serverMods;
                }
            }
        }

        private bool _serverModsDirty;

        /// <summary>The mods a server is known to run, or null if never asked.</summary>
        private List<string> KnownModsFor(BrowserServer s)
        {
            return s == null ? null : KnownModsFor(s.Endpoint);
        }

        private List<string> KnownModsFor(Row r)
        {
            return r == null ? null : KnownModsFor(r.Endpoint);
        }

        private List<string> KnownModsFor(string endpoint)
        {
            if (string.IsNullOrEmpty(endpoint)) return null;

            lock (_modListLock)
            {
                List<string> mods;
                return ServerMods.TryGetValue(endpoint, out mods) ? mods : null;
            }
        }

        /// <summary>
        /// Whether the background sweep also reads mod lists.
        ///
        /// On from startup, because a mod filter is useless if it starts empty
        /// and only begins learning once somebody uses it. It costs nothing the
        /// player can feel: it is confined to the background sweep, which was
        /// already walking the same servers.
        /// </summary>
        private bool _sweepModLists = true;

        /// <summary>
        /// Set when a server whose mods were just read satisfies the current
        /// filter - the only event that can add a row to a mod-filtered list.
        /// </summary>
        private bool _newModMatches;

        /// <summary>
        /// The mods being filtered on, as a snapshot the worker threads can
        /// read safely.
        ///
        /// THIS IS WHY IT EXISTS: the workers used to enumerate
        /// _filters.RequiredMods directly while the player was adding and
        /// removing entries on the UI thread. A HashSet being modified during
        /// enumeration throws InvalidOperationException, and an unhandled
        /// exception on a background thread TERMINATES THE PROCESS - no dialog,
        /// no catch, the launcher just vanishes. Reproduced in 59,000
        /// iterations.
        ///
        /// The reference is swapped whole, never mutated, so a worker either
        /// sees the old array or the new one and both are complete.
        /// </summary>
        private volatile string[] _requiredModsSnapshot = new string[0];

        /// <summary>Republishes the snapshot. UI thread only.</summary>
        private void PublishRequiredMods()
        {
            var copy = new string[_filters.RequiredMods.Count];
            _filters.RequiredMods.CopyTo(copy);
            _requiredModsSnapshot = copy;
        }

        /// <summary>Whether one mod list satisfies every mod the player asked for.</summary>
        private bool MatchesRequiredMods(List<string> mods)
        {
            if (mods == null) return false;

            var wanted = _requiredModsSnapshot;      // one atomic read
            if (wanted.Length == 0) return false;

            foreach (string want in wanted)
            {
                bool found = false;
                foreach (string has in mods)
                {
                    if (has.IndexOf(want, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    found = true;
                    break;
                }
                if (!found) return false;
            }
            return true;
        }

        /// <summary>How many servers in the current list have a known mod list.</summary>
        private int ModListsKnown()
        {
            int n = 0;
            lock (_modListLock)
            {
                foreach (var r in _rows)
                    if (!r.IsHeading && ServerMods.ContainsKey(r.Endpoint)) n++;
            }
            return n;
        }

        /// <summary>Files away what a server just told us it runs.</summary>
        private void RememberServerMods(string endpoint, ServerRules rules)
        {
            if (string.IsNullOrEmpty(endpoint) || rules == null) return;

            // The description arrives on the same reply as the mod list, so it
            // is filed here rather than costing a second query. Game mode
            // filtering reads it.
            if (!string.IsNullOrWhiteSpace(rules.Description))
            {
                lock (_modListLock)
                {
                    ServerDescriptions[endpoint] = rules.Description.Trim();
                    _serverModsDirty = true;
                }
            }

            if (rules.Mods == null) return;

            var names = new List<string>();
            foreach (var m in rules.Mods)
                if (m != null && !string.IsNullOrWhiteSpace(m.Name)) names.Add(m.Name.Trim());

            if (names.Count == 0) return;

            lock (_modListLock)
            {
                // Replacing a server's list means un-counting the old one, or
                // a server re-queried twice would count twice.
                List<string> before;
                if (ServerMods.TryGetValue(endpoint, out before))
                    foreach (string m in before)
                    {
                        int n;
                        if (_modPopularity.TryGetValue(m, out n))
                        {
                            if (n <= 1) _modPopularity.Remove(m);
                            else _modPopularity[m] = n - 1;
                        }
                    }

                ServerMods[endpoint] = names;

                foreach (string m in names)
                {
                    int n;
                    _modPopularity[m] = _modPopularity.TryGetValue(m, out n) ? n + 1 : 1;
                }

                _serverModsDirty = true;

                // Does THIS server satisfy the filter the player has set? The
                // list is right here, so the answer costs nothing, and it is
                // the only thing that can make the visible set change.
                if (!_newModMatches && _requiredModsSnapshot.Length > 0
                    && MatchesRequiredMods(names)) _newModMatches = true;
            }
        }

        /// <summary>
        /// Writes the mod index to disk, on a background thread.
        ///
        /// MEASURED: 60 ms and 8.5 MB at full size. That ran on the UI thread
        /// every three seconds while the sweep was active, holding the lock the
        /// mod dropdown needs - so opening that dropdown queued behind a file
        /// write. The snapshot is taken under the lock, which is quick; the
        /// writing happens outside it and off the thread that draws.
        /// </summary>
        private void SaveServerMods()
        {
            Dictionary<string, List<string>> snapshot;

            lock (_modListLock)
            {
                if (!_serverModsDirty || _serverMods == null) return;
                if (_savingServerMods) return;          // one writer is enough

                snapshot = new Dictionary<string, List<string>>(_serverMods, StringComparer.OrdinalIgnoreCase);
                _serverModsDirty = false;
                _savingServerMods = true;
            }

            Dictionary<string, string> descSnapshot;
            lock (_modListLock)
                descSnapshot = new Dictionary<string, string>(ServerDescriptions, StringComparer.OrdinalIgnoreCase);

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    ServerStore.SaveServerMods(snapshot);
                    ServerStore.SaveServerDescriptions(descSnapshot);
                }
                catch { }
                finally { lock (_modListLock) _savingServerMods = false; }
            });
        }

        private bool _savingServerMods;

        /// <summary>
        /// Two rows of toggles for the way a server says it plays.
        ///
        /// These match against the server NAME, because DayZ publishes nothing
        /// about game mode - a PvE server announces itself in its title and
        /// nowhere else. Several are selectable at once and they read as OR:
        /// "PvE or Trader" is a sensible ask, "PvE and Trader and Hardcore"
        /// usually is not, and would return nothing.
        /// </summary>
        private int BuildGameModeButtons(Control parent, int x, int y)
        {
            _modeButtons.Clear();

            string[] row1 = { "PVP", "PVE", "RP", "TRADER", "AI", "NO KOS", "KOS" };
            string[] row2 = { "HARDCORE", "DEATHMATCH", "PVP ZONES" };

            int top = y;
            foreach (string[] row in new[] { row1, row2 })
            {
                int cx = x;
                foreach (string mode in row)
                {
                    // Sized to the word so "DEATHMATCH" is not clipped and
                    // "AI" is not a slab of empty button.
                    int w = Math.Max(38, TextRenderer.MeasureText(mode,
                        new Font("Segoe UI", 7.5f, FontStyle.Bold)).Width + 14);

                    var b = new Button
                    {
                        Text = mode,
                        Bounds = new Rectangle(cx, top, w, 22),
                        FlatStyle = FlatStyle.Flat,
                        Font = new Font("Segoe UI", 7.5f, FontStyle.Bold),
                        Tag = mode,
                        TabStop = false
                    };

                    string captured = mode;
                    b.Click += (s2, e2) =>
                    {
                        if (_filters.GameModes.Contains(captured)) _filters.GameModes.Remove(captured);
                        else _filters.GameModes.Add(captured);

                        PaintGameModeButtons();
                        QueueFilter();
                    };

                    parent.Controls.Add(b);
                    _modeButtons[mode] = b;
                    cx += w + 4;
                }
                top += 25;
            }

            PaintGameModeButtons();
            return top + 4;
        }

        /// <summary>
        /// The description a server gave when it was last queried, or null.
        /// </summary>
        private string KnownDescriptionFor(BrowserServer s)
        {
            if (s == null) return null;

            lock (_modListLock)
            {
                string d;
                return ServerDescriptions.TryGetValue(s.Endpoint, out d) ? d : null;
            }
        }

        private Dictionary<string, string> _serverDescriptions;

        private Dictionary<string, string> ServerDescriptions
        {
            get
            {
                if (_serverDescriptions == null)
                    _serverDescriptions = ServerStore.LoadServerDescriptions();
                return _serverDescriptions;
            }
        }

        private readonly Dictionary<string, Button> _modeButtons =
            new Dictionary<string, Button>(StringComparer.OrdinalIgnoreCase);

        private void PaintGameModeButtons()
        {
            foreach (var kv in _modeButtons)
            {
                bool on = _filters.GameModes.Contains(kv.Key);

                kv.Value.BackColor = on ? Color.FromArgb(60, 120, 60) : Panel2;
                kv.Value.ForeColor = on ? Color.White : Color.FromArgb(150, 150, 158);
                kv.Value.FlatAppearance.BorderColor = on
                    ? Color.FromArgb(110, 180, 110)
                    : Color.FromArgb(70, 70, 78);
            }
        }

        /// <summary>
        /// Fills every dropdown in the filter panel from what is known now.
        ///
        /// WHY NOT ON DropDown, WHICH IS THE OBVIOUS PLACE
        ///   Rebuilding a ComboBox's Items from inside its own DropDown event
        ///   means the list is being replaced at the exact moment Windows is
        ///   opening it. Click twice quickly and the second open lands in the
        ///   middle of the first rebuild - and because the shell autocomplete
        ///   object is unmanaged, the failure is an access violation, not an
        ///   exception: no stack trace, nothing in the log, the launcher just
        ///   disappears.
        ///
        ///   So the lists are built BEFORE anyone can click them - when the
        ///   panel is opened, and again as new data arrives while it is open.
        ///   By the time a dropdown is clicked there is nothing left to do.
        ///
        /// Skips any list that is currently open, because replacing the items
        /// under an open dropdown is the thing being avoided.
        /// </summary>
        /// <summary>
        /// Starts the mod index load immediately if the startup delay has not
        /// elapsed yet. Harmless to call repeatedly - the load itself only
        /// happens once.
        /// </summary>
        private void WarmModIndexNow()
        {
            if (!_modIndexStart.Enabled) return;      // already started or done

            _modIndexStart.Stop();
            BeginBackgroundModIndex();
        }

        private void RefreshFilterChoices()
        {
            if (_filterPanel == null || !_filterPanel.Visible) return;

            try
            {
                if (_fMap != null && !_fMap.DroppedDown) FillMapChoices();
                if (_fCountry != null && !_fCountry.DroppedDown) FillCountryChoices();
                if (_fMod != null && !_fMod.DroppedDown) FillModChoices();
            }
            catch { }
        }

        /// <summary>
        /// A filter control changed. Debounces the actual work, and repaints
        /// the FILTERS button so the indicator keeps up with the controls.
        /// </summary>
        private void QueueFilter()
        {
            // Reading the panel is a handful of property reads and one small
            // string split - cheap enough to do per keystroke, and it is what
            // lets the button light up as soon as something is typed rather
            // than a second later when the timer fires.
            ReadFilterUi();
            PaintFilterToggle();

            _typeTimer.Stop();
            _typeTimer.Start();
        }

        /// <summary>How long after the last keystroke the one-shot search runs.</summary>
        private const int AutoSearchAfterMs = 5000;

        /// <summary>
        /// How long after the last keystroke the mod panel is allowed to
        /// refresh. Selecting a row queries the server for its mods, so doing it
        /// per keystroke is what made typing feel heavy.
        /// </summary>
        private const int ModsAfterTypingMs = 2000;

        private readonly System.Windows.Forms.Timer _modsDelay = new System.Windows.Forms.Timer();

        /// <summary>True while the player is still typing in the search box.</summary>
        private bool _typing;

        private readonly System.Windows.Forms.Timer _autoSearchTimer = new System.Windows.Forms.Timer();

        /// <summary>The last text actually sent to Steam, so it is never sent twice.</summary>
        private string _lastSteamSearch = "\u0000";

        /// <summary>
        /// Draws the current filters over the CACHED list. No network, ever.
        ///
        /// This used to compare the Steam filter key and force a full master
        /// list re-fetch whenever it changed. The search box feeds name_match
        /// into that key, so every single keystroke started another download of
        /// ten thousand servers - which is what made the app feel like it was
        /// dragging, and is a fine way to get rate limited. Searching now reads
        /// the cache and nothing else; RunSteamSearch is the only way to the
        /// network, and it runs once.
        /// </summary>
        private void ApplyFilters()
        {
            _typeTimer.Stop();
            ReadFilterUi();

            if (IsSteamTab(_tab)) RenderFromCache();
            else RefreshMine();
        }

        /// <summary>
        /// Runs ONE narrowed query against Steam for whatever is in the search
        /// box, and folds the result into the cached list.
        ///
        /// It exists because Steam caps a list at 10,000 servers and DayZ has
        /// more than that - measured: the raw count reaches exactly 10,000 in
        /// about a second and never moves again, however long it is polled. A
        /// server the player is hunting for may simply not be in the copy on
        /// hand. Sending the name as a filter asks Steam a NARROWER question,
        /// and that answer can contain servers the cap left out.
        ///
        /// <paramref name="fromButton"/> only changes what the log says.
        /// </summary>
        private void RunSteamSearch(bool fromButton)
        {
            _autoSearchTimer.Stop();
            ReadFilterUi();

            if (!IsSteamTab(_tab)) { RefreshMine(); return; }

            // Show what the cache already has, immediately.
            RenderFromCache();

            string text = _search.Text.Trim();
            if (text.Length == 0) return;

            if (string.Equals(text, _lastSteamSearch, StringComparison.OrdinalIgnoreCase))
            {
                if (fromButton) Log("Already searched Steam for \"" + text + "\".");
                return;
            }

            if (_pollTimer.Enabled)
            {
                Log("A server query is already running - not starting another.");
                return;
            }

            _lastSteamSearch = text;
            Log(fromButton
                ? "SEARCH pressed - asking Steam for servers matching \"" + text + "\"."
                : "No SEARCH pressed within " + (AutoSearchAfterMs / 1000)
                  + "s - asking Steam once for \"" + text + "\".");
            RefreshCurrent(true);
        }

        private static string SteamFilterKey(List<KeyValuePair<string, string>> filters)
        {
            if (filters == null || filters.Count == 0) return "";
            return string.Join("&", filters.Select(f => f.Key + "=" + f.Value).ToArray());
        }

        // -------------------------------------------------------- details ----
        /// <summary>
        /// Shows the mod list for the selected server.
        ///
        /// A server is asked ONCE, when it is first selected. After that the
        /// panel is left completely alone unless the player picks a different
        /// server or asks for a refresh - <paramref name="force"/> is the only
        /// way to make it query again.
        ///
        /// The early return below is the important part. Without it, every
        /// re-render of the server list rebuilt this panel, so the mods
        /// visibly cleared and reappeared several times a second while pings
        /// came in.
        /// </summary>
        /// <summary>
        /// Redraws the mod rows from the rules already held, recomputing each
        /// mod's installed / missing / outdated state. No network.
        /// </summary>
        private void RepopulateModsFromCache()
        {
            var row = SelectedRow;
            if (row == null || row.Endpoint != _modsShownFor) return;

            ServerRules rules;
            lock (_modLock) { _modCache.TryGetValue(row.Endpoint, out rules); }
            if (rules != null) PopulateMods(row, rules);
        }

        private void ShowMods(bool force = false)
        {
            var row = SelectedRow;
            if (row == null)
            {
                _modsShownFor = null;
                _modsHeader.Text = "No server selected";
                _mods.Items.Clear();
                _desc.Clear();
                return;
            }

            // Already showing this server, and not being asked to re-check it:
            // touch nothing at all.
            if (!force && row.Endpoint == _modsShownFor && _mods.Items.Count > 0)
                return;

            ServerRules rules;
            bool loading;
            lock (_modLock)
            {
                if (force) _modCache.Remove(row.Endpoint);
                _modCache.TryGetValue(row.Endpoint, out rules);
                loading = _modLoading.Contains(row.Endpoint);
            }

            if (rules != null)
            {
                PopulateMods(row, rules);
                return;
            }

            if (!loading)
            {
                lock (_modLock) { _modLoading.Add(row.Endpoint); }
                _modsShownFor = null;
                _modsHeader.Text = "Querying server rules (" + row.Endpoint + ")...";
                _mods.Items.Clear();
                _desc.Clear();

                ThreadPool.QueueUserWorkItem(_ => FetchRules(row));
            }
        }

        private void FetchRules(Row row)
        {
            ServerRules rules = null;
            try
            {
                rules = A2S.GetRulesAt(row.Host, row.EffectiveQueryPort, 1800);
            }
            catch { }

            lock (_modLock)
            {
                _modLoading.Remove(row.Endpoint);
                if (rules != null)
                {
                    _modCache[row.Endpoint] = rules;
                    RememberServerMods(row.Endpoint, rules);
                }
            }

            try
            {
                BeginInvoke((Action)(() =>
                {
                    if (SelectedRow != null && SelectedRow.Endpoint == row.Endpoint)
                        ShowMods();
                }));
            }
            catch { }
        }

        private void PopulateMods(Row row, ServerRules rules)
        {
            _mods.BeginUpdate();
            _mods.Items.Clear();
            try
            {
                int modCount = rules.Mods != null ? rules.Mods.Count : 0;
                _modsShownFor = row.Endpoint;
                _modsShownAt = DateTime.Now;

                // Say WHEN this was read. The panel no longer updates itself, so
                // without a time the player cannot tell whether they are looking
                // at something current or something from ten minutes ago.
                _modsHeader.Text = string.Format("Content required by {0} ({1} mods)  -  checked {2:HH:mm:ss}",
                                                 row.Name, modCount, _modsShownAt);

                if (rules.Mods != null)
                {
                    string steamPath = FindSteam();
                    int shown = 0;

                    foreach (var mod in rules.Mods)
                    {
                        if (!ModFindMatches(mod)) continue;
                        _mods.Items.Add(MakeModRow(mod, steamPath));
                        shown++;
                    }

                    // Say so when the list is being narrowed, otherwise a
                    // forgotten search looks like a server that lost its mods.
                    if (_modFind != null && _modFind.Count > 0)
                        _modsHeader.Text = string.Format(
                            "Content required by {0}  -  showing {1} of {2} mods matching your search",
                            row.Name, shown, modCount);
                }

                FillModFindChoices(rules);

                _desc.Clear();
                if (!string.IsNullOrEmpty(rules.Description))
                {
                    _desc.Text = rules.Description;
                }
            }
            finally
            {
                _mods.EndUpdate();
            }
        }

        /// <summary>
        /// Stocks the Find mod dropdown with every mod this server runs.
        ///
        /// ALWAYS the full list, never narrowed by what is already typed or
        /// added - the dropdown is how you find out what is on the server, so
        /// hiding entries from it defeats the point. Called whenever the panel
        /// is populated, which is the only time the answer changes.
        /// </summary>
        private void FillModFindChoices(ServerRules rules)
        {
            if (_modFind == null || _modFind.Box.DroppedDown) return;

            try
            {
                string[] names = rules == null || rules.Mods == null
                    ? new string[0]
                    : rules.Mods.Where(m => m != null && !string.IsNullOrWhiteSpace(m.Name))
                                .Select(m => m.Name.Trim())
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                                .ToArray();

                RefillCombo(_modFind.Box, names, _modFind.Box.Text);
            }
            catch { }
        }

        /// <summary>
        /// Whether a mod matches what is being searched for in the panel.
        ///
        /// ANY of the terms, not all - a row is one mod and cannot be two
        /// things at once, so requiring every term would always show nothing.
        /// That is the opposite of the Has Mods filter, where each term is a
        /// separate demand on one SERVER.
        /// </summary>
        private bool ModFindMatches(Mod mod)
        {
            if (_modFind == null || _modFind.Count == 0) return true;
            if (mod == null) return false;

            string name = (mod.Name ?? "").ToLowerInvariant();
            string id = mod.WorkshopId.ToString();

            foreach (string want in _modFind.Entries)
            {
                string w = want.ToLowerInvariant();
                if (name.IndexOf(w, StringComparison.Ordinal) >= 0) return true;
                if (id.IndexOf(w, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        /// <summary>A time span in the largest unit that still reads naturally.</summary>
        private static string Age(TimeSpan t)
        {
            if (t.TotalDays >= 365) return string.Format("{0:F1} years", t.TotalDays / 365.0);
            if (t.TotalDays >= 1)   return string.Format("{0:F0} days", t.TotalDays);
            if (t.TotalHours >= 1)  return string.Format("{0:F0} hours", t.TotalHours);
            return string.Format("{0:F0} minutes", t.TotalMinutes);
        }

        /// <summary>
        /// One mod row: what state it is actually in, plus the four actions.
        ///
        /// WHY EVERY SUB-ITEM IS COLOURED SEPARATELY
        ///   A ListView ignores per-sub-item colours unless
        ///   UseItemStyleForSubItems is false. Leave it at its default and every
        ///   ForeColor set below is silently discarded - the rows render in one
        ///   flat colour and the panel loses every state it was trying to show.
        ///   That is exactly what happened here: the row builder was replaced by
        ///   one that set no colours and never cleared the flag, so "installed",
        ///   "MISSING" and "OUT OF DATE" all looked identical.
        ///
        ///   The status is also computed, not taken from a fixed string. Whether
        ///   a mod is present, broken or behind the workshop is the entire point
        ///   of the panel.
        /// </summary>
        private ListViewItem MakeModRow(Mod m, string steam)
        {
            // A mod the server loaded from its own disk has no workshop id, so
            // it is matched against the player's library by name. There is
            // nothing to download and nothing to be out of date against.
            if (m.IsLocal)
            {
                var found = ModIndex.FindLocalByName(m.BareName, steam);
                var localRow = new ListViewItem(new[]
                {
                    m.Name,
                    "Local",
                    found != null ? "installed (local)" : "NOT FOUND in your mod folders",
                    // Blank, not greyed-out words: Steam has no item here to
                    // repair, subscribe to or remove, so offering the words at
                    // all only invites a click that can do nothing.
                    "", "", "", "Info"
                })
                {
                    Tag = m,
                    UseItemStyleForSubItems = false,
                    ToolTipText = found != null
                        ? "Loaded from " + found.Folder
                        : "This server loads a mod from its own disk. You need a folder called @"
                          + m.BareName + " in your mod folders."
                };

                Color lc = found != null ? Good : Color.FromArgb(230, 130, 130);
                localRow.SubItems[MColName].ForeColor = lc;
                localRow.SubItems[MColId].ForeColor = Color.FromArgb(150, 150, 158);
                localRow.SubItems[MColStatus].ForeColor = lc;

                localRow.SubItems[MColInfo].ForeColor = Color.FromArgb(120, 150, 190);
                return localRow;
            }

            bool have = steam != null && SteamWorkshop.IsInstalled(steam, m.WorkshopId);
            bool broken = !have && steam != null && SteamWorkshop.IsBrokenInstall(steam, m.WorkshopId);
            bool stale = have && SteamWorkshop.NeedsUpdate(steam, m.WorkshopId);

            string status;
            Color colour;
            if (broken) { status = "BROKEN - files present but no meta.cpp"; colour = Color.FromArgb(230, 130, 130); }
            else if (!have) { status = "NOT installed"; colour = Color.FromArgb(220, 190, 120); }
            else if (stale)
            {
                TimeSpan behind = steam == null ? TimeSpan.Zero : SteamWorkshop.StaleBy(steam, m.WorkshopId);
                status = behind > TimeSpan.Zero
                    ? "OUT OF DATE by " + Age(behind) + " - will update"
                    : "OUT OF DATE - will update";
                colour = Color.FromArgb(225, 175, 90);
            }
            else { status = "installed"; colour = Good; }

            var it = new ListViewItem(new[]
            {
                m.Name, m.WorkshopId.ToString(), status, "Repair", "Sub", "Remove", "Info"
            })
            {
                Tag = m,
                UseItemStyleForSubItems = false,
                ToolTipText = "Repair re-downloads, Sub subscribes, Remove unsubscribes, Info shows details"
            };

            it.SubItems[MColName].ForeColor = colour;
            it.SubItems[MColId].ForeColor = colour;
            it.SubItems[MColStatus].ForeColor = colour;

            var link = Color.FromArgb(120, 150, 190);
            it.SubItems[MColRepair].ForeColor = link;
            it.SubItems[MColSub].ForeColor = have ? Color.FromArgb(90, 90, 96) : link;
            it.SubItems[MColRemove].ForeColor = have ? Color.FromArgb(190, 130, 130)
                                                     : Color.FromArgb(90, 90, 96);
            it.SubItems[MColInfo].ForeColor = link;
            return it;
        }

        /// <summary>
        /// Drops every cached judgement about what is installed, and redraws
        /// the mod panel from disk.
        ///
        /// Called once a download has actually finished. Nothing here is
        /// expensive - the panel is at most a few dozen rows - but the order
        /// matters: forget the paths, invalidate the index, THEN repopulate, or
        /// the fresh scan is answered from the stale caches.
        /// </summary>
        private void RefreshInstalledState()
        {
            try
            {
                SteamWorkshop.ForgetItemPaths();
                ModIndex.Invalidate();

                if (_modsShownFor != null) ShowMods(true);
            }
            catch { }
        }

        /// <summary>
        /// Workshop items downloading because the player pressed Repair, and
        /// the timer that notices when they land.
        /// </summary>
        private readonly HashSet<ulong> _awaitingDownload = new HashSet<ulong>();
        private readonly System.Windows.Forms.Timer _downloadWatch = new System.Windows.Forms.Timer();

        /// <summary>
        /// Watches one item until Steam has it on disk, then refreshes the
        /// panel. Steam gives no completion callback we can rely on from here -
        /// a forged callback vtable was tried and proven not to deliver - so
        /// this asks, every couple of seconds, until the answer changes.
        /// </summary>
        private void WatchDownload(ulong id)
        {
            if (id == 0) return;

            _awaitingDownload.Add(id);

            if (!_downloadWatch.Enabled)
            {
                _downloadWatchTicks = 0;
                _downloadWatch.Start();
            }
        }

        private int _downloadWatchTicks;

        private void OnModClick(object sender, MouseEventArgs e)
        {
            var hit = _mods.HitTest(e.Location);
            if (hit.Item == null || hit.SubItem == null) return;

            // Mod, NOT ServerMod. ServerRules.Mods is a List<Mod>, so the tag
            // holds a base Mod - casting to the subclass returned null on every
            // single click and the whole panel silently did nothing.
            var mod = hit.Item.Tag as Mod;
            if (mod == null) return;
            int col = hit.Item.SubItems.IndexOf(hit.SubItem);

            if (col == MColInfo)
            {
                // Reads local files, so it works even with Steam shut.
                ModInfoDialog.Show(this, mod, FindSteam());
                return;
            }

            if (col != MColRepair && col != MColSub && col != MColRemove) return;

            // Every one of these asks Steam about a workshop item. A mod loaded
            // from the server's own disk has none, and passing 0 would act on
            // nothing at all.
            if (mod.IsLocal || mod.WorkshopId == 0) return;

            string steam = FindSteam();
            string gameDir = steam == null ? null
                : (FindGameDir(steam, A2S.ExperimentalAppId) ?? FindGameDir(steam, A2S.StableAppId));
            if (gameDir == null || !SteamWorkshop.TryInit(gameDir, Log))
            {
                MessageBox.Show("Steam is not available, so mods cannot be changed from here.",
                                "Steam not available", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (col == MColRepair)
            {
                // Subscribe first: repairing something unsubscribed would
                // otherwise ask Steam to download an item it does not own.
                SteamWorkshop.Subscribe(mod.WorkshopId);
                SteamWorkshop.ForceDownload(mod.WorkshopId);
                WatchDownload(mod.WorkshopId);
                Log("Repairing " + mod.Name + " (" + mod.WorkshopId + ") - Steam is re-downloading it.");
                hit.Item.SubItems[MColStatus].Text = "repairing...";
            }
            else if (col == MColSub)
            {
                SteamWorkshop.Subscribe(mod.WorkshopId);
                Log("Subscribed to " + mod.Name + " (" + mod.WorkshopId + ").");
                hit.Item.SubItems[MColStatus].Text = "subscribing...";
            }
            else
            {
                if (MessageBox.Show(
                        "Unsubscribe from " + mod.Name + "?\r\n\r\nSteam will delete it from disk. " +
                        "Any server that requires it will need it downloaded again.",
                        "Remove mod", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    return;

                SteamWorkshop.Unsubscribe(mod.WorkshopId);
                Log("Unsubscribed from " + mod.Name + " (" + mod.WorkshopId + ").");
                hit.Item.SubItems[MColStatus].Text = "removing...";
            }

            hit.Item.SubItems[MColStatus].ForeColor = Color.FromArgb(200, 190, 130);
            _modRecheck.Stop();
            _modRecheck.Start();
        }

        // ---------------------------------------------------- connections ----
        private void OnConnect(object sender, EventArgs e)
        {
            var row = SelectedRow;
            if (row == null) { MessageBox.Show("Pick a server first."); return; }

            _connect.Enabled = false;
            Cursor = Cursors.WaitCursor;
            try { Launch(row); }
            catch (Exception ex)
            {
                Log("ERROR: " + ex.Message);
                MessageBox.Show(ex.Message, "A Beautiful Potato Launcher",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _connect.Enabled = true;
                Cursor = Cursors.Default;
            }
        }

private void Launch(Row srv)
        {
            ServerStore.SaveName(_name.Text.Trim());

            if (IsRunning(GameExe))
            {
                MessageBox.Show(
                    "DayZ is already running.\r\n\r\nClose it completely - check Task Manager for " +
                    "DayZ_x64.exe and DayZ_BE.exe - then try again.\r\n\r\nLaunching on top of a " +
                    "running game is itself a cause of \"Game restart required\".",
                    "Close DayZ first", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string steam = FindSteam();
            if (steam == null) throw new Exception("Could not find Steam.");
            Log("Steam : " + steam);

            // Ask the server what it runs before choosing an exe. Getting this
            // wrong means launching Experimental against a stable server, which
            // simply will not connect.
            Log("");
            Log("Asking " + srv.Endpoint + " what it is running...");
            var live = A2S.GetInfoAt(srv.Host, srv.EffectiveQueryPort, 3000);
            if (!live.Online)
                throw new Exception("The server did not answer on query port " +
                                    A2S.QueryPort(srv.Port) + ".\r\n\r\n" + live.Error);
            Log("Server: " + live.Name);
            Log("Build : " + live.GameLabel + " (app " + live.AppId + ")  version " + live.Version);

            string gameDir = FindGameDir(steam, live.AppId);
            if (gameDir == null)
            {
                string[] wanted = live.AppId == A2S.StableAppId ? StableFolders : ExpFolders;
                throw new Exception("This server runs " + live.GameLabel +
                                    ", but that build is not installed.\r\n\r\nLooked for: " +
                                    string.Join(", ", wanted) + "\r\nunder " +
                                    Path.Combine(steam, "steamapps", "common"));
            }
            Log("Game  : " + gameDir);

            Log("");
            Log("Asking which mods it requires...");
            // Same re-check as the panel: joining with an unread mod list is a
            // kick, so a contradicted "no mods" is retried before trusting it.
            var rulesForLaunch = QueryModsChecked(srv);
            var mods = rulesForLaunch == null ? null : rulesForLaunch.Mods;

            if (mods != null && mods.Count == 0 && HasTag(srv.Tags, "mod"))
                throw new Exception(
                    "This server reports that it is modded, but its mod list could not be read."
                    + Environment.NewLine + Environment.NewLine
                    + "Joining now would very likely end in a \"missing mod\" kick. "
                    + "Press REFRESH on the server and try again.");
            if (mods == null)
                throw new Exception("The server did not answer the mod query on port " +
                                    A2S.QueryPort(srv.Port) + ".");
            Log("Server publishes " + mods.Count + " mod(s).");
            Log("Note: a server only advertises as many mods as its steamProtocolMaxDataSize");
            Log("      allows. If it kicks you for a mod not listed above, that setting is");
            Log("      too small on the server and no launcher can see the rest.");

            // Only warn when the reply could not be proven whole.
            if (!rulesForLaunch.Complete)
            {
                Log("WARNING: this server's mod list did not end cleanly, so it may be");
                Log("         incomplete. If it kicks you for a missing mod, that is why.");
            }

            // Ask Steam, once, when each of these items was last published, so
            // the per-mod check below can compare that against the timestamp in
            // each local meta.cpp. Without this the only staleness signal is
            // Steam's own flag, which is what let a player onto Emergence with
            // outdated mods and got them kicked.
            Log("Checking published versions with Steam...");
            SteamWorkshop.PrefetchWorkshopTimes(mods.Select(m => m.WorkshopId), Log);

            // Anything missing OR out of date gets fetched before launching -
            // joining with a stale mod is a kick waiting to happen.
            var missing = new List<Mod>();
            foreach (var m in mods)
            {
                // No point fetching something that has been turned off, or that
                // will be loaded from a folder of the player's choosing.
                // A mod the server loaded from its own disk cannot be fetched
                // from the workshop at all - there is nothing to fetch. It is
                // matched against the player's own library by name instead.
                if (m.IsLocal)
                {
                    var localHave = ModIndex.FindLocalByName(m.BareName, steam);
                    Log(string.Format("  {0} {1,-12} {2}",
                        localHave != null ? "[local]   " : "[NOT HERE]", "local", m.Name));
                    if (localHave == null)
                        Log("            no folder called @" + m.BareName + " in your mod folders");
                    continue;
                }

                var choice = ModOverrides.For(m.WorkshopId);
                if (choice != null && (!choice.Enabled || !string.IsNullOrEmpty(choice.Folder)))
                {
                    Log(string.Format("  {0} {1,-12} {2}",
                        choice.Enabled ? "[CUSTOM]  " : "[SKIPPED] ", m.WorkshopId, m.Name));
                    continue;
                }

                SteamWorkshop.RunCallbacks();           // read a fresh state, not a cached one

                bool have = SteamWorkshop.IsInstalled(steam, m.WorkshopId);
                bool broken = !have && SteamWorkshop.IsBrokenInstall(steam, m.WorkshopId);
                bool stale = have && SteamWorkshop.NeedsUpdate(steam, m.WorkshopId);

                // Steam may already be part-way through fetching this one.
                // Launching now would load a half-written mod, so it has to be
                // waited for just like a missing one.
                bool inFlight = SteamWorkshop.IsBusy(m.WorkshopId);

                Log(string.Format("  {0} {1,-12} {2}",
                    broken ? "[BROKEN]  " : !have ? "[MISSING] " : stale ? "[OUTDATED]"
                           : inFlight ? "[UPDATING]" : "[ok]      ",
                    m.WorkshopId, m.Name));

                if (broken)
                {
                    // Steam believes this is installed, so a plain download is a
                    // no-op; it has to be forced.
                    Log("            files are present but there is no meta.cpp - forcing a re-download");
                    SteamWorkshop.Subscribe(m.WorkshopId);
                    SteamWorkshop.ForceDownload(m.WorkshopId);
                    missing.Add(m);
                }
                else if (!have) missing.Add(m);
                else if (stale)
                {
                    TimeSpan behind = SteamWorkshop.StaleBy(steam, m.WorkshopId);
                    if (behind > TimeSpan.Zero)
                        Log("            local copy is " + Age(behind) + " behind the workshop");
                    SteamWorkshop.Subscribe(m.WorkshopId);
                    SteamWorkshop.ForceDownload(m.WorkshopId);
                    missing.Add(m);
                }
                else if (inFlight) missing.Add(m);       // wait, do not re-request
            }

            if (missing.Count > 0)
            {
                Log("");
                Log("Fetching " + missing.Count + " mod(s) that are missing, out of date, or still downloading...");
                using (var dl = new ModDownloadForm(steam, gameDir, missing.ToArray()))
                {
                    if (dl.ShowDialog(this) != DialogResult.OK)
                    {
                        Log("Download cancelled - not launching.");
                        return;
                    }
                }
                Log("All mods are present now.");

                // The panel still says "NOT installed" for everything that just
                // arrived: those rows were built from a scan taken before the
                // download. Both caches have to be dropped first - the path
                // cache because the folders did not exist when it was filled,
                // and the library index because it counted them as missing.
                RefreshInstalledState();
            }

            // Short mod paths. Absolute !Workshop paths run ~90 characters each;
            // junctions named by workshop id inside the game folder let the
            // argument be relative and tiny, and the working directory below is
            // the game folder so they resolve. Verified in a real launch.
            var modArgs = new List<string>();
            string shortRoot = Path.Combine(gameDir, "!m");
            foreach (var m in mods)
            {
                // The player may have turned this mod off, or pointed it at a
                // different copy on disk. See ModOverrides.
                var choice = ModOverrides.For(m);
                if (choice != null && !choice.Enabled)
                {
                    Log("  [SKIPPED] " + m.Name + " - you chose not to load this one.");
                    continue;
                }

                string source;
                if (choice != null && !string.IsNullOrEmpty(choice.Folder)
                    && Directory.Exists(choice.Folder))
                {
                    source = choice.Folder;
                    Log("  [CUSTOM]  " + m.Name + " -> " + source);
                }
                else if (m.IsLocal)
                {
                    // No workshop id to look up - find the folder by name.
                    var found = ModIndex.FindLocalByName(m.BareName, steam);
                    if (found == null)
                    {
                        Log("  [MISSING] " + m.Name + " - not in your mod folders, skipping.");
                        continue;
                    }
                    source = found.Folder;
                    Log("  [local]   " + m.Name + " -> " + source);
                }
                else
                {
                    source = SteamWorkshop.ItemPath(steam, m.WorkshopId);
                }

                // Local mods have no id to name a link after, so the mod's own
                // name is used - it is unique within a server's list.
                string linkName = m.IsLocal ? SafeLinkName(m.BareName) : m.WorkshopId.ToString();
                string link = Path.Combine(shortRoot, linkName);
                try { Directory.CreateDirectory(shortRoot); } catch { }

                if (Junction.TryCreate(link, source)) modArgs.Add("!m\\" + linkName);
                else modArgs.Add(source);            // long, but it still works
            }

            var args = new List<string>();
            if (modArgs.Count > 0)
            {
                string joined = string.Join(";", modArgs.ToArray());
                Log("Mod argument: " + joined.Length + " characters.");
                args.Add("\"-mod=" + joined + "\"");
            }

            args.Add("-connect=" + srv.Host);
            args.Add("-port=" + srv.Port);

            // A locked server needs the password on the command line, and there
            // is no way to supply it afterwards - DayZ simply bounces off. Ask
            // now, and take a cancel as "do not launch" rather than launching
            // into a refusal the player cannot read.
            if (live.Password || srv.Password)
            {
                string pw = PasswordDialog.Ask(this, srv.Name);
                if (pw == null)
                {
                    Log("Password required, and none was given - not launching.");
                    return;
                }
                args.Add("\"-password=" + pw + "\"");
                Log("Password supplied.");
            }

            string playerName = _name.Text.Trim();
            if (playerName.Length > 0) args.Add("\"-name=" + playerName + "\"");

            args.Add("-nolauncher");
            args.Add("-world=empty");

            string bePath = Path.Combine(gameDir, BeExe);
            if (!File.Exists(bePath))
                throw new Exception(BeExe + " not found in:\r\n" + gameDir +
                                    "\r\n\r\nWithout it BattlEye cannot attach and the server will kick you.");

            string full = BeArgs + " -exe " + GameExe + " " + string.Join(" ", args.ToArray());
            Log("");
            Log("Launching through " + BeExe + " so BattlEye attaches:");
            Log("  " + BeExe + " " + full);

            // THE GAME MUST START UNDER ITS OWN APP ID, NOT THE LAUNCHER'S.
            //
            // Stable DayZ and Experimental are separate Steam applications -
            // 221100 and 1024020 - and a server checks the auth ticket against
            // the one it runs. A ticket issued for the wrong app is refused
            // with "Steam authentication failed: Ticket is not for this game."
            //
            // This launcher has to claim 221100 for itself, because that is
            // where workshop content lives and there is no other way to
            // subscribe (see SteamWorkshop). Doing so puts SteamAppId and
            // SteamGameId into ITS OWN environment - and a child process
            // inherits its parent's environment, so the game was being started
            // as app 221100 whichever build was actually launched. Both game
            // folders ship a correct steam_appid.txt, but the environment
            // variables take precedence over that file, so it could not help.
            //
            // That is also why Steam showed the wrong game as running: the id
            // the process announces is the one Steam displays.
            //
            // So the child is given the id of the build being launched,
            // explicitly. That requires UseShellExecute = false, because the
            // environment block can only be set when .NET creates the process
            // itself rather than handing it to the shell.
            var psi = new ProcessStartInfo
            {
                FileName = bePath,
                Arguments = full,
                WorkingDirectory = gameDir,
                UseShellExecute = false
            };
            // Only ever set a value that is actually one of DayZ's two apps.
            // live.AppId is decoded from the server's reply, and a server that
            // answered oddly must not lead to inventing an app id - clearing the
            // variables instead lets the game fall back to the steam_appid.txt
            // sitting in its own folder, which is correct for both builds.
            if (live.AppId == A2S.StableAppId || live.AppId == A2S.ExperimentalAppId)
            {
                psi.EnvironmentVariables["SteamAppId"] = live.AppId.ToString();
                psi.EnvironmentVariables["SteamGameId"] = live.AppId.ToString();
                Log("  SteamAppId for the game process: " + live.AppId
                    + " (" + live.GameLabel + ")");
            }
            else
            {
                psi.EnvironmentVariables.Remove("SteamAppId");
                psi.EnvironmentVariables.Remove("SteamGameId");
                Log("  The server reported app " + live.AppId + ", which is neither DayZ");
                Log("  build - letting the game read its own steam_appid.txt instead.");
            }

            // The launcher's own Steam session is deliberately LEFT OPEN.
            //
            // An earlier version shut it down here, to stop Steam reporting the
            // wrong game as running. That was a bad trade: the same session
            // backs the server browser, so closing it would have killed live
            // player counts and refreshing the list for anyone who keeps the
            // launcher open beside the game - which is most of the point of
            // having one.
            Process.Start(psi);

            // Remember which mods this server actually used, so the Mod
            // Manager can show when each was last needed. Written now rather
            // than on exit: the launcher is often closed while the game runs.
            try { ModIndex.MarkLoaded(mods.Select(m => m.WorkshopId)); }
            catch { }

            Log("");
            Log("Started. DayZ takes a minute or two to appear - be patient.");
            _status.Text = "Launched " + srv.Endpoint;
        }

        /// <summary>
        /// A mod name reduced to something usable as a folder name.
        ///
        /// Local mods have no workshop id to name their junction after, so the
        /// mod's own name is used - and mod names carry brackets, slashes and
        /// exclamation marks that a directory cannot.
        /// </summary>
        private static string SafeLinkName(string name)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in name ?? "")
                sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
            string trimmed = sb.ToString().Trim('_');
            return trimmed.Length == 0 ? "local" : trimmed;
        }

private static bool IsRunning(string exeName)
        {
            try { return Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exeName)).Length > 0; }
            catch { return false; }
        }

private static ServerRules QueryModsChecked(Row row)
        {
            bool tagged = HasTag(row.Tags, "mod");

            var ports = new List<int>();
            if (row.QueryPort > 0 && row.QueryPort < 65535) ports.Add(row.QueryPort);
            int effective = row.EffectiveQueryPort;
            if (!ports.Contains(effective)) ports.Add(effective);
            int fallback = A2S.QueryPort(row.Port);
            if (!ports.Contains(fallback)) ports.Add(fallback);

            ServerRules rules = null;
            foreach (int port in ports)
            {
                var candidate = A2S.GetRulesAt(row.Host, port);
                if (candidate != null && candidate.Mods.Count > 0) return candidate;
                if (rules == null) rules = candidate;

                if (!tagged) continue;

                for (int attempt = 0; attempt < 2; attempt++)
                {
                    Thread.Sleep(220);
                    var retry = A2S.GetRulesAt(row.Host, port, 4000 + attempt * 2000);
                    if (retry != null && retry.Mods.Count > 0) return retry;
                    if (rules == null) rules = retry;
                }
            }

            return rules;
        }

        private void OnDirectConnect(object sender, EventArgs e)
        {
            Log("Direct connect requested.");
            SavePlayerName();
        }

        /// <summary>Opens the mod library window.</summary>
        private void OnModManager(object sender, EventArgs e)
        {
            string steam = FindSteam();
            if (steam == null)
            {
                MessageBox.Show("Could not find Steam, so the mod folder cannot be located.",
                                "Steam not found", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string gameDir = FindGameDir(steam, A2S.ExperimentalAppId)
                          ?? FindGameDir(steam, A2S.StableAppId);

            // MODELESS on purpose. ShowDialog would freeze the server list
            // behind it, and the mod library is exactly the thing a player
            // wants open BESIDE the browser - checking what a server needs
            // while looking at what they have. Show() keeps both alive.
            if (_modManager != null && !_modManager.IsDisposed)
            {
                // Already open - bring it forward rather than opening a second.
                if (_modManager.WindowState == FormWindowState.Minimized)
                    _modManager.WindowState = FormWindowState.Normal;
                _modManager.BringToFront();
                _modManager.Activate();
                return;
            }

            _modManager = new ModManagerForm(steam, gameDir);
            _modManager.FormClosed += (s2, e2) => _modManager = null;
            _modManager.Show(this);
        }

        /// <summary>The mod library window, while it is open.</summary>
        private ModManagerForm _modManager;

        private void OnToggleFavourite(object sender, EventArgs e)
        {
            var row = SelectedRow;
            if (row != null) ToggleFavourite(row);
        }

        private void ToggleFavourite(Row row)
        {
            if (row == null) return;
            if (_favourites.Contains(row.Endpoint))
            {
                _favourites.Remove(row.Endpoint);
                row.Favourite = false;
                Log("Removed " + row.Name + " from favourites.");
            }
            else
            {
                _favourites.Add(row.Endpoint);
                row.Favourite = true;
                ServerStore.SetName(row.Endpoint, row.Name);
                Log("Added " + row.Name + " to favourites.");
            }
            ServerStore.SaveFavourites(_favourites);
            Redraw(row.Endpoint);
        }

        /// <summary>
        /// The right-click menu.
        ///
        /// WHY THE COLOURS ARE SET IN TWO PLACES
        ///   DarkMenu is a ProfessionalColorTable, and a colour table only
        ///   describes BACKGROUNDS - it has no say over text. Setting the
        ///   renderer alone therefore produces a dark menu painted with the
        ///   default near-black text, which is unreadable on it. ForeColor has
        ///   to be set as well, on the strip AND on each item, because an item
        ///   does not inherit it once a custom renderer is in play.
        /// </summary>
        private ContextMenuStrip BuildServerMenu()
        {
            var menu = new ContextMenuStrip
            {
                BackColor = Panel2,
                ForeColor = Color.Gainsboro,
                ShowImageMargin = false,
                Renderer = new ToolStripProfessionalRenderer(new DarkMenu())
            };

            Action<string, EventHandler> add = (text, handler) =>
            {
                var item = new ToolStripMenuItem(text)
                {
                    ForeColor = Color.Gainsboro,
                    BackColor = Panel2
                };
                item.Click += handler;
                menu.Items.Add(item);
            };

            add("Copy server info", (s, e) => CopyServerInfo(false));
            add("Copy address only", (s, e) => CopyServerInfo(true));
            menu.Items.Add(new ToolStripSeparator());
            add("Add to / remove from Favorites", (s, e) => ToggleFavourite(SelectedRow));
            add("Refresh this server", (s, e) => RefreshOneRow(SelectedRow));
            menu.Items.Add(new ToolStripSeparator());
            add("Connect", OnConnect);

            // Nothing sensible to act on with no row selected.
            menu.Opening += (s, e) => { if (SelectedRow == null) e.Cancel = true; };
            return menu;
        }

        /// <summary>Puts the selected server on the clipboard.</summary>
        private void CopyServerInfo(bool addressOnly)
        {
            var row = SelectedRow;
            if (row == null) return;

            string text;
            if (addressOnly) text = row.Endpoint;
            else
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine(row.Name);
                sb.AppendLine("IP: " + row.Host);
                sb.AppendLine("Port: " + row.Port);
                text = sb.ToString().TrimEnd();
            }

            try
            {
                // An empty string throws, and the clipboard can be locked by
                // another application - neither deserves an error dialog.
                if (text.Length > 0) Clipboard.SetText(text);
                _status.Text = addressOnly
                    ? "Copied " + row.Endpoint + " to the clipboard."
                    : "Copied server info for " + row.Name + " to the clipboard.";
            }
            catch (Exception ex)
            {
                _status.Text = "Could not copy: " + ex.Message;
            }
        }

        private void SavePlayerName()
        {
            if (_name != null && !string.IsNullOrWhiteSpace(_name.Text))
                ServerStore.SaveName(_name.Text.Trim());
        }

        /// <summary>
        /// Where Steam is installed, ALWAYS in normal Windows form.
        ///
        /// The registry hands this back with forward slashes - "c:/program
        /// files (x86)/steam" - and everything built from it inherits them, so
        /// a mod folder ends up as
        ///
        ///     c:/program files (x86)/steam\steamapps\workshop\content\...
        ///
        /// .NET does not care: Directory.Exists happily returns true. But
        /// explorer.exe cannot parse a path like that, and when it fails it
        /// silently opens Documents instead - which is exactly what "Open
        /// folder" was doing. Normalising here fixes it for every caller
        /// rather than at each place a path is used.
        /// </summary>
        private static string FindSteam()
        {
            string found = FindSteamRaw();
            if (string.IsNullOrEmpty(found)) return found;
            try { return System.IO.Path.GetFullPath(found); }
            catch { return found; }
        }

        private static string FindSteamRaw()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    if (key != null) return key.GetValue("SteamPath") as string;
                }
            }
            catch { }
            return null;
        }

        private static string FindGameDir(string steamPath, ulong appId)
        {
            if (string.IsNullOrEmpty(steamPath)) return null;
            string apps = Path.Combine(steamPath, "steamapps", "common");
            string[] targets = appId == A2S.ExperimentalAppId ? ExpFolders : StableFolders;

            foreach (var t in targets)
            {
                string candidate = Path.Combine(apps, t);
                if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, GameExe)))
                    return candidate;
            }
            return null;
        }

        private static void OpenLink(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { }
        }

        private void Log(string msg)
        {
            if (_log == null) return;
            string time = DateTime.Now.ToString("HH:mm:ss");
            _log.AppendText("[" + time + "] " + msg + Environment.NewLine);

            try
            {
                File.AppendAllText(Program.LogFile, "[" + time + "] " + msg + Environment.NewLine);
            }
            catch { }
        }

        /// <summary>
        /// One of the launcher's own images.
        ///
        /// FROM INSIDE THE EXECUTABLE FIRST. These are embedded resources - the
        /// csproj puts them there with an explicit LogicalName - and nothing
        /// copies an assets folder next to the exe. This used to look only on
        /// disk, find nothing, and quietly return a 1x1 bitmap, which is why
        /// the logos were simply absent rather than broken-looking.
        ///
        /// The disk fallback stays for running out of a source tree, where the
        /// folder does exist beside the build.
        /// </summary>
        private static Image LoadImage(string file)
        {
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                using (var stream = asm.GetManifestResourceStream(file))
                {
                    // Copied out before the stream closes: Image.FromStream
                    // keeps reading from it lazily, and a disposed stream makes
                    // the image throw the first time it is drawn.
                    if (stream != null)
                    {
                        using (var copy = new MemoryStream())
                        {
                            stream.CopyTo(copy);
                            copy.Position = 0;
                            return Image.FromStream(copy);
                        }
                    }
                }
            }
            catch { }

            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", file);
                if (File.Exists(path)) return Image.FromFile(path);
            }
            catch { }

            return new Bitmap(1, 1);
        }

        private void RememberWindow()
        {
            if (WindowState == FormWindowState.Normal)
                ServerStore.SaveWindow(Location, Size);
        }

        private void RestoreWindow()
        {
            Point loc; Size sz;
            if (ServerStore.LoadWindow(out loc, out sz))
            {
                StartPosition = FormStartPosition.Manual;
                Location = loc;
                Size = sz;
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _closing = true;
            _pollTimer.Stop();
            _visTimer.Stop();
            _typeTimer.Stop();
            _modRecheck.Stop();
            _saveTimer.Stop();
            _idleSort.Stop();
            _modSweepTimer.Stop();
            _modIndexStart.Stop();
            _choicesTimer.Stop();
            _downloadWatch.Stop();

            // Whatever the index has learnt this session is kept, including
            // from a build that never finished.
            SaveIndex();
            SaveServerMods();

            RememberWindow();
            RememberAllSplits();

            SteamServerList.Stop();
            base.OnFormClosing(e);
        }
    }
}