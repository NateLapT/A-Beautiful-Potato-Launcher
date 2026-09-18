// ---------------------------------------------------------------------------
//  Beautiful Potato Experimental Launcher
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
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BeautifulPotatoExpLauncher
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
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

    internal sealed class MainForm : Form
    {
        // Column indices. The first column is a clickable refresh glyph - a
        // ListView cannot host real buttons, so the cell is hit-tested instead.
        private const int ColRefresh = 0, ColStar = 1, ColName = 2, ColGame = 3,
                          ColStatus = 4, ColMap = 5, ColPlayers = 6, ColTime = 7,
                          ColPing = 8, ColMods = 9, ColAddress = 10;

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

        // ---- state ----
        private Tab _tab = Tab.Community;
        private readonly HashSet<string> _favourites;
        private readonly HashSet<string> _allowed;
        private List<BrowserServer> _browser = new List<BrowserServer>();
        private readonly BrowserFilters _filters = new BrowserFilters();

        // The master list is slow to fetch - tens of seconds - so it is pulled
        // once per REFRESH and then kept. Searching and filtering run over this
        // cache and are instant; only REFRESH goes back to Steam.
        // One cache per list, because Recent, Friends and LAN are different
        // lists rather than filtered views of the internet one.
        private readonly Dictionary<string, List<BrowserServer>> _caches =
            new Dictionary<string, List<BrowserServer>>();
        private readonly Dictionary<string, DateTime> _cacheTimes =
            new Dictionary<string, DateTime>();

        /// <summary>
        /// One cache per list. Official is its own tab and therefore its own
        /// key, which matters more than it looks: asking for official servers
        /// is a different REQUEST to Steam, not a different view of the same
        /// answer. It sends a tag filter so the 10,000 cap is not spent on
        /// community servers, and its result must never be confused with the
        /// unfiltered list.
        /// </summary>
        private string CacheKey
        {
            get
            {
                return _tab + "/" + EffectiveBrowseMode;
            }
        }

        /// <summary>
        /// When each server was last seen in a list, so the saved cache can drop
        /// ones that have genuinely gone away instead of keeping them for ever.
        /// </summary>
        private readonly Dictionary<string, long> _lastSeen = new Dictionary<string, long>();

        /// <summary>
        /// The current tab's list. Falls through to the copy saved on disk the
        /// first time a tab is opened in a session, which is what makes a cold
        /// start show servers immediately instead of an empty table.
        /// </summary>
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

        // Live A2S detail is fetched only for the rows actually on screen -
        // pinging ten thousand servers to show twenty would be absurd.
        private readonly System.Windows.Forms.Timer _visTimer = new System.Windows.Forms.Timer();
        private int _lastTopIndex = -1;
        private int _lastRawCount = -1;
        private int _stableTicks;
        private int _lastShownCount;
        // What the list is currently showing. The ListView holds no items of
        // its own in virtual mode - it asks for these by index.
        private List<Row> _rows = new List<Row>();
        private string _selectedEndpoint;
        private readonly Dictionary<string, int> _rowIndex =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Addresses judged to be redirect farms, recomputed whenever the cached
        // list changes. See BrowserFilters.HideFakes for why this goes by address.
        private readonly HashSet<string> _farmIps =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _farmSubnets =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int _hiddenFakes;

        // -1 means the default order: most populated first. Clicking a header
        // picks a column; clicking the same one again reverses it.
        private int _sortColumn = -1;
        private bool _sortAscending = true;

        /// <summary>Everything hidden as fake, with the reason, for Settings.</summary>
        internal readonly List<FlaggedServer> Flagged = new List<FlaggedServer>();

        private readonly HashSet<string> _asked = new HashSet<string>();
        private readonly Dictionary<string, ServerInfo> _live =
            new Dictionary<string, ServerInfo>(StringComparer.OrdinalIgnoreCase);

        // A fixed pool of query workers, always working on whatever is on
        // screen. The old code started a Thread per visible row; scrolling a
        // long list spawned hundreds of them, each with its own stack and its
        // own blocking socket, which is what made scrolling stutter.
        // More workers than before: each spends nearly all its time blocked on
        // a UDP socket, and the background sweep has thousands of servers to get
        // through. Sixteen keeps the sweep moving without any real CPU cost.
        private const int PingWorkers = 16;
        private readonly object _pingLock = new object();
        // Two tiers. _pingWanted is what is on screen and always goes first;
        // _pingAll is the whole list, walked by _sweep whenever nothing visible
        // is outstanding. Scrolling therefore jumps the queue rather than
        // cancelling the work already done.
        private readonly List<Row> _pingWanted = new List<Row>();
        private List<Row> _pingAll = new List<Row>();
        private int _sweep;
        private int _checked;
        private readonly HashSet<string> _pingBusy =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private Thread[] _pingPool;
        private volatile bool _closing;
        // Fully qualified: System.Threading is also in scope and has its own Timer.
        private readonly System.Windows.Forms.Timer _pollTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer _typeTimer = new System.Windows.Forms.Timer();
        // Steam applies subscribe/unsubscribe asynchronously, so the mod panel
        // is re-read a moment after any action rather than guessing the result.
        private readonly System.Windows.Forms.Timer _modRecheck = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer _resortTimer = new System.Windows.Forms.Timer();
        /// <summary>"all", "exp" or "stable" - which builds the browser asks for.</summary>
        private string _browseMode = ServerStore.LoadBrowseMode();

        /// <summary>
        /// The AppIds a query should cover. "All" means both, and both really
        /// are needed: Steam's scoped lists are filtered by the app you ask
        /// for, and asking only for stable was measured to miss an Experimental
        /// server sitting in the player's own Steam favourites.
        /// </summary>
        private uint[] AppsForQuery
        {
            get
            {
                // EffectiveBrowseMode, NOT _browseMode. On Recent, Friends and
                // LAN the tab forces "all" and the cache is keyed that way, so
                // reading the raw setting here would fetch a single build and
                // then store it under the key that claims to hold both.
                string mode = EffectiveBrowseMode;
                if (mode == "all")
                    return new[] { (uint)A2S.StableAppId, (uint)A2S.ExperimentalAppId };
                return new[] { mode == "exp"
                             ? (uint)A2S.ExperimentalAppId : (uint)A2S.StableAppId };
            }
        }

        /// <summary>
        /// Recent, Friends and LAN are the player's OWN servers, and splitting
        /// them by build only hides things - the Game column already says which
        /// build each one is. So those tabs always browse everything.
        /// </summary>
        private static bool TabForcesAllBuilds(Tab t)
        {
            return t == Tab.Recent || t == Tab.Friends || t == Tab.Lan;
        }

        /// <summary>The build selector applies to the tabs that query by AppId.</summary>
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
        private RichTextBox _desc;
        private Label _descHeader;
        private TextBox _name, _log, _search;
        private Label _status, _modsHeader, _searchHint, _searchClear;
        private Button _connect, _refresh, _filterToggle, _favBtn;
        private Panel _filterPanel;
        private readonly Dictionary<Tab, Button> _tabs = new Dictionary<Tab, Button>();
        private ComboBox _cbPlayers, _cbTime, _cbBuild;
        private TextBox _fName, _fAddr, _fMap, _fPing;
        private CheckBox _chkNoPass, _chkHideFull, _chkHideEmpty, _chkHideFakes;
        private Segmented _segThird, _segMods;

        public MainForm()
        {
            Text = "A Beautiful Potato Launcher";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1180, 800);
            MinimumSize = new Size(1020, 680);
            RestoreWindow();

            // Save whenever a drag-resize or a move finishes, not only on exit.
            // Closing is not the only way a program ends - if it is killed, or
            // the machine goes down, a size saved only at shutdown is a size
            // that was never saved at all. ResizeEnd fires once when the mouse
            // is released, so this costs one tiny file write per deliberate
            // resize rather than one per pixel.
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

            _pollTimer.Interval = InternetPollMs;
            _pollTimer.Tick += OnPoll;

            // Watches for scrolling so newly visible rows get their live detail.
            _visTimer.Interval = 500;
            _visTimer.Tick += OnVisTick;

            _typeTimer.Interval = 220;
            _typeTimer.Tick += (s, e) => { _typeTimer.Stop(); ApplyFilters(); };

            _modRecheck.Interval = 2500;
            _modRecheck.Tick += (s, e) => { _modRecheck.Stop(); ShowMods(); };

            _resortTimer.Interval = 1800;
            _resortTimer.Tick += (s, e) =>
            {
                _resortTimer.Stop();
                if (IsSteamTab(_tab)) RenderFromCache();
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

            // Dock.Right places the LAST added control furthest right, so
            // Settings goes in first and Donate takes the corner.
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
                Text = "♥  Donate",
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

            // ---- top bar: tabs, search, filters ----
            var top = new Panel { Dock = DockStyle.Top, Height = 70, BackColor = Ink };
            main.Controls.Add(top);

            // Two rows: the tabs on top, then search / filters / SEARCH beneath.
            // Six tabs and a search box no longer share one line comfortably,
            // and the search box was the thing that lost - it is what the top
            // bar is mostly FOR, so it gets a row of its own.
            //
            // Docking runs from the HIGHEST child index down, so every Fill in
            // here is forced to index 0: a Fill that docks first eats the whole
            // panel and leaves the others stacked on top of it. That is exactly
            // how the search box ended up hidden behind the FAVOURITES button
            // once before, so the indices are set explicitly rather than relying
            // on the order things were added.
            var searchRow = new Panel { Dock = DockStyle.Fill, BackColor = Ink };
            top.Controls.Add(searchRow);

            var tabBar = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = Ink };
            top.Controls.Add(tabBar);

            top.Controls.SetChildIndex(searchRow, 0);
            top.Controls.SetChildIndex(tabBar, 1);

            var topRight = new Panel { Dock = DockStyle.Right, Width = 200, BackColor = Ink };
            searchRow.Controls.Add(topRight);

            var searchArea = new Panel { Dock = DockStyle.Fill, BackColor = Ink };
            searchRow.Controls.Add(searchArea);
            searchRow.Controls.SetChildIndex(searchArea, 0);
            searchRow.Controls.SetChildIndex(topRight, 1);

            // OFFICIAL leads, because a player looking for Bohemia's servers
            // should not have to discover that the answer lives in a filter
            // panel they never open.
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

            _search = new TextBox
            {
                Bounds = new Rectangle(10, 4, 200, 23),
                BackColor = Panel2,
                ForeColor = Color.Gainsboro,
                BorderStyle = BorderStyle.FixedSingle
            };
            _search.KeyDown += OnSearchKey;
            searchArea.Controls.Add(_search);

            // Clear button, sitting just inside the right edge of the box.
            _searchClear = new Label
            {
                Text = "✕",
                Bounds = new Rectangle(0, 6, 18, 19),
                ForeColor = Color.FromArgb(150, 150, 158),
                BackColor = Panel2,
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand,
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
                // Must match the text box, not the bar behind it - a transparent
                // label here paints the panel colour and leaves a visible seam.
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

                // Filter as the player types. The list is already cached in
                // memory, so this costs nothing - but the redraw is debounced so
                // a fast typist is not re-sorting ten thousand rows per letter.
                _typeTimer.Stop();
                _typeTimer.Start();
            };
            searchArea.Controls.Add(_searchHint);
            _searchHint.BringToFront();

            // One place decides the geometry, at the size the panel actually is.
            EventHandler layoutSearch = (s, e) =>
            {
                int w = Math.Max(60, searchArea.ClientSize.Width - 20);
                _search.Bounds = new Rectangle(10, 4, w, 23);
                _searchHint.Bounds = new Rectangle(11, 5, w - 2, 21);
                _searchClear.Bounds = new Rectangle(10 + w - 22, 6, 18, 19);
                _searchClear.BringToFront();
            };
            searchArea.Resize += layoutSearch;
            searchArea.HandleCreated += layoutSearch;

            _filterToggle = MakeBtn("FILTERS", new Rectangle(4, 4, 92, 25), Panel2);
            _filterToggle.Click += (s, e) =>
            {
                _filterPanel.Visible = !_filterPanel.Visible;
                Log("Filter panel " + (_filterPanel.Visible ? "shown." : "hidden."));
            };
            topRight.Controls.Add(_filterToggle);

            var apply = MakeBtn("SEARCH", new Rectangle(102, 4, 92, 25), Color.FromArgb(60, 95, 60));
            apply.Click += (s, e) => ApplyFilters();
            topRight.Controls.Add(apply);

            // ---- filter panel (hidden until asked for) ----
            _filterPanel = BuildFilterPanel();
            main.Controls.Add(_filterPanel);

            // ---- server list ----
            // VIRTUAL MODE, and this is the whole reason the list is usable.
            // Building ten thousand ListViewItems took seconds and allocated a
            // ListViewItem plus eleven sub-items per server; in virtual mode the
            // control asks for only the rows it is about to paint, so a 10,000
            // server list costs the same as a 20 server one.
            _list = new ListView
            {
                Dock = DockStyle.Fill,          // was Top+fixed height, so it never grew
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
                var r = SelectedRow;
                _selectedEndpoint = r != null ? r.Endpoint : null;
            };
            _list.Columns.Add("", 26, HorizontalAlignment.Center);   // refresh
            _list.Columns.Add("", 26, HorizontalAlignment.Center);   // favourite
            _list.Columns.Add("Name", 244);
            _list.Columns.Add("Game", 84);
            _list.Columns.Add("Status", 62);
            _list.Columns.Add("Map", 106);
            _list.Columns.Add("Players", 70, HorizontalAlignment.Center);
            _list.Columns.Add("Time", 54, HorizontalAlignment.Center);
            _list.Columns.Add("Ping", 56, HorizontalAlignment.Center);
            _list.Columns.Add("Mods", 48, HorizontalAlignment.Center);
            _list.Columns.Add("Address", 132);
            _list.SelectedIndexChanged += (s, e) => ShowMods();
            _list.DoubleClick += OnConnect;
            _list.MouseClick += OnListClick;
            _list.MouseDown += OnListMouseDown;
            _list.ContextMenuStrip = BuildServerMenu();

            // list  /  (details  /  log)
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
                UseMnemonic = false,        // or "&" in a server name vanishes
                Text = "Content required by server",
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 0, 0),
                BackColor = Panel2,
                ForeColor = Color.White
            };
            detailArea.Controls.Add(_modsHeader);

            // mods  |  description, side by side and resizable
            var detailSplit = MakeSplit(Orientation.Vertical, 600, "mods");
            detailArea.Controls.Add(detailSplit);
            detailArea.Controls.SetChildIndex(detailSplit, 0);   // Fill first
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

            // A RichTextBox rather than a TextBox purely for DetectUrls, which
            // turns the Discord links servers put in their description into
            // something clickable.
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
            _mods.Columns.Add("", 58, HorizontalAlignment.Center);   // Repair
            _mods.Columns.Add("", 42, HorizontalAlignment.Center);   // Sub
            _mods.Columns.Add("", 60, HorizontalAlignment.Center);   // Remove
            _mods.Columns.Add("", 44, HorizontalAlignment.Center);   // Info
            _mods.MouseClick += OnModClick;
            _mods.FullRowSelect = true;
            detailSplit.Panel1.Controls.Add(_mods);

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

            // Docking runs from the highest child index down, and a Fill control
            // must sit at index 0 or it claims the space before the rest are placed.
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
            _cbBuild.Items.AddRange(new object[] { "All servers", "Experimental servers", "Stable servers" });
            _cbBuild.SelectedIndex = IndexForMode(_browseMode);
            _cbBuild.SelectedIndexChanged += (s, e) =>
            {
                // HighlightTab drives this control when the tab changes, and a
                // programmatic SelectedIndex raises this event exactly like a
                // click would. Without the guard, switching to Recent would set
                // the box to "All", which would fire this, which would refresh
                // the tab that was already mid-refresh.
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

            _favBtn = MakeBtn("ADD TO FAVOURITES", new Rectangle(20, ry, 170, 26), Panel2);
            _favBtn.Font = new Font("Segoe UI", 8f, FontStyle.Bold);
            _favBtn.Click += OnToggleFavourite;
            rail.Controls.Add(_favBtn);
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

        /// <summary>The filter panel, laid out after the official launcher's.</summary>
        private Panel BuildFilterPanel()
        {
            var p = new Panel
            {
                Dock = DockStyle.Top,
                Height = 214,
                BackColor = Panel,
                Visible = false
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

                // Clear button, inside the right edge, shown only when there is
                // something to clear.
                var clear = new Label
                {
                    Text = "\u2715",
                    Bounds = new Rectangle(bx + 250 - 20, by + 2, 18, 19),
                    BackColor = Panel2,
                    ForeColor = Color.FromArgb(150, 150, 158),
                    TextAlign = ContentAlignment.MiddleCenter,
                    Cursor = Cursors.Hand,
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
            lab("Server Name", 4, y); _fName = box(118, y); y += 28;
            lab("IP Address", 4, y);  _fAddr = box(118, y); y += 28;
            lab("Map Name", 4, y);    _fMap  = box(118, y); y += 28;
            lab("Max Ping", 4, y);    _fPing = box(118, y); y += 28;

            lab("Players", 4, y);
            _cbPlayers = MakeCombo(new Rectangle(118, y, 250, 23),
                                   new object[] { "(Any)", "Not empty", "Not full", "Empty only" });
            _cbPlayers.SelectedIndexChanged += (s2, e2) => QueueFilter();
            p.Controls.Add(_cbPlayers);
            y += 28;

            lab("Game Time", 4, y);
            _cbTime = MakeCombo(new Rectangle(118, y, 250, 23), new object[] { "(Any)", "Day", "Night" });
            _cbTime.SelectedIndexChanged += (s2, e2) => QueueFilter();
            p.Controls.Add(_cbTime);

            // ---- right hand side ----
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
                "Hides servers claiming more than " + BrowserFilters.MaxRealSlots +
                " slots (DayZ cannot do that), and addresses running " +
                BrowserFilters.FarmServersPerIp + "+ servers under " +
                BrowserFilters.FarmNamesPerIp + "+ different names - redirect farms.");

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

            p.Controls.Add(new Label
            {
                // Along the bottom, full width: at rx + 240 this ran past the
                // right edge of the panel and was clipped.
                Text = "Filters apply as you change them, against the list already downloaded.  "
                     + "Name, map, players and password are also sent to Steam on the next REFRESH, "
                     + "which is how a narrowed refresh reaches past the 10,000 servers it returns at once.",
                Bounds = new Rectangle(8, 190, 920, 18),
                ForeColor = Color.FromArgb(110, 110, 118),
                Font = new Font("Segoe UI", 7.5f)
            });

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

        /// <summary>
        /// A splitter pane styled to match. FixedPanel is left alone so both
        /// sides share the extra space when the window is resized.
        ///
        /// <paramref name="key"/> names this divider in the saved layout, so the
        /// player's arrangement comes back next time.
        /// </summary>
        private SplitContainer MakeSplit(Orientation orientation, int distance, string key)
        {
            var sc = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = orientation,
                BackColor = Color.FromArgb(58, 58, 64),   // the grab bar itself
                SplitterWidth = 6,
                Panel1MinSize = 80,
                Panel2MinSize = 80
            };
            sc.Panel1.BackColor = Ink;
            sc.Panel2.BackColor = Ink;

            _splitters[key] = sc;

            // SplitterDistance throws if it is set before the control has a size,
            // so it waits for the handle.
            sc.HandleCreated += (s, e) => ApplySplit(sc, key, distance);

            // SplitterMoved fires for a programmatic change too - including the
            // one just above, and every proportional nudge the control makes
            // when the window is resized. Only a real drag should overwrite what
            // the player chose, and SplitterMoving is what distinguishes one:
            // it is raised for user drags and not for code setting the value.
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

        /// <summary>The space a divider can actually move within.</summary>
        private static int SplitSpan(SplitContainer sc)
        {
            return (sc.Orientation == Orientation.Vertical ? sc.Width : sc.Height)
                 - sc.SplitterWidth;
        }

        /// <summary>
        /// Puts one divider back, clamped to what currently fits. The saved
        /// fraction can easily ask for a position this window has no room for -
        /// a narrow window, or a panel with a minimum size - and SplitterDistance
        /// throws rather than clamping, which would take the whole layout down.
        /// </summary>
        private void ApplySplit(SplitContainer sc, string key, int fallback)
        {
            int span = SplitSpan(sc);
            int want = fallback;

            double fraction;
            if (span > 0 && _panelFractions.TryGetValue(key, out fraction))
                want = (int)Math.Round(fraction * span);

            int lo = sc.Panel1MinSize;
            int hi = span - sc.Panel2MinSize;
            if (hi < lo) return;                 // no room for either rule; leave it
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

        /// <summary>Records every divider where it currently sits.</summary>
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
                case Tab.Favourites: return "FAVOURITES";
                case Tab.Official: return "OFFICIAL";
                default: return "COMMUNITY";
            }
        }

        /// <summary>
        /// Favourites is this launcher's own saved list, queried server by
        /// server. Every other tab is one of Steam's lists.
        /// </summary>
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

            // Community and Official both query Steam's internet list by AppId,
            // so both can choose a build - Official was previously locked out of
            // this for no reason, leaving no way to see Experimental officials.
            //
            // Recent, Friends and LAN show "All" and are locked there: they are
            // the player's own servers and hiding half of them behind a build
            // switch helps nobody, when the Game column already labels each row.
            bool picks = TabPicksBuild(_tab);
            _cbBuild.Enabled = picks;

            _syncingBuild = true;
            try
            {
                _cbBuild.SelectedIndex = IndexForMode(EffectiveBrowseMode);
            }
            finally { _syncingBuild = false; }
        }

        /// <param name="force">
        /// true only for the REFRESH button. Everything else reuses the cached
        /// master list, because fetching it again takes tens of seconds.
        /// </param>
        private void RefreshCurrent(bool force = false)
        {
            _pollTimer.Stop();
            SteamServerList.Stop();

            if (!IsSteamTab(_tab)) { _visTimer.Stop(); RefreshMine(); return; }

            // SHOW FIRST, REFRESH SECOND - always, in that order.
            //
            // Switching to a tab used to mean staring at an empty table until
            // Steam answered. There is no reason for that: the list from last
            // time is right here, and the overwhelming majority of it is still
            // true. So it goes on screen immediately and the player can search
            // it straight away, while a fresh query runs underneath and folds
            // its results in as they arrive.
            // BEFORE rendering, not after. The filters carry state that belongs
            // to the tab - Official in particular - and they are otherwise only
            // refreshed inside StartCommunityQuery, which runs after this point.
            // Leaving it until then meant switching away from the Official tab
            // painted the new tab's cached list through the previous tab's
            // "official servers only" rule, which matches nothing: the list
            // appeared completely empty until Steam answered.
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

            // Then refresh behind it - but not on every single tab click. The
            // internet list is ten thousand servers and takes the better part of
            // a minute; flicking between tabs to compare them would start that
            // download again each time and leave every list permanently
            // half-finished. So a list fetched within the last minute is left
            // alone, and REFRESH always forces regardless.
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

            // Carry over anything already known so switching tabs does not throw
            // away results and query everything again.
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

        /// <summary>Folds a query result into a row. No UI work - see Redraw.</summary>
        private static void Apply(Row row, ServerInfo info)
        {
            row.Online = info.Online;
            if (info.Online)
            {
                if (!string.IsNullOrEmpty(info.Name)) row.Name = info.Name;
                row.Map = info.Map;
                row.Players = info.Players;
                row.MaxPlayers = info.MaxPlayers;
                row.Ping = info.PingMs;
                row.AppId = info.AppId;
                row.Tags = info.Keywords;
            }
            else
            {
                row.Error = info.Error;
            }
        }

        /// <summary>
        /// A virtual ListView never sorts itself - it only knows how many rows
        /// there are - so the backing list is sorted and the control redrawn.
        /// </summary>
        private void OnColumnClick(object sender, ColumnClickEventArgs e)
        {
            // The glyph and star columns are buttons, not data.
            if (e.Column == ColRefresh || e.Column == ColStar) return;

            if (_sortColumn == e.Column) _sortAscending = !_sortAscending;
            else { _sortColumn = e.Column; _sortAscending = true; }

            MarkSortedColumn();
            if (IsSteamTab(_tab)) RenderFromCache();
            else RefreshMine();
        }

        /// <summary>Puts an arrow on the sorted column so the order is visible.</summary>
        private void MarkSortedColumn()
        {
            string[] titles = { "", "", "Name", "Game", "Status", "Map",
                                "Players", "Time", "Ping", "Mods", "Address" };
            for (int i = 0; i < _list.Columns.Count && i < titles.Length; i++)
            {
                string label = titles[i];
                if (i == _sortColumn && label.Length > 0)
                    label += _sortAscending ? "  \u25B2" : "  \u25BC";
                _list.Columns[i].Text = label;
            }
        }

        /// <summary>
        /// Orders rows by the chosen column. Servers that failed their query
        /// always sink to the bottom whatever the sort - you cannot join them,
        /// so they should never outrank one you can.
        /// </summary>
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
                case ColPlayers:
                    byColumn = (a, b) => a.Players.CompareTo(b.Players);
                    break;
                case ColTime:
                    byColumn = (a, b) => string.Compare(TagTime(a.Tags), TagTime(b.Tags), StringComparison.Ordinal);
                    break;
                case ColPing:
                    // An unmeasured ping is not "0 ms" - it sorts last either way.
                    byColumn = (a, b) => PingKey(a).CompareTo(PingKey(b));
                    break;
                case ColMods:
                    byColumn = (a, b) => HasTag(a.Tags, "mod").CompareTo(HasTag(b.Tags, "mod"));
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
                // Saved servers come first, ahead of every other rule including
                // whichever column the player clicked. That is the point of
                // saving one: in a list of thousands it should never have to be
                // hunted for.
                int fav = (y.Favourite ? 1 : 0) - (x.Favourite ? 1 : 0);
                if (fav != 0) return fav;

                // Offline still sinks, but only WITHIN each group - so a saved
                // server that happens to be down sits at the bottom of the
                // saved ones rather than vanishing into the tail of the list.
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
            var rows = _rows;                       // local: the field can be swapped
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
                row.MaxPlayers > 0 ? row.Players + " / " + row.MaxPlayers : "",
                TagTime(row.Tags),
                row.Ping > 0 ? row.Ping + " ms" : (row.Offline ? "-" : ""),
                HasTag(row.Tags, "mod") ? "yes" : "",
                row.Endpoint
            })
            {
                UseItemStyleForSubItems = false,
                ToolTipText = "Arrow re-checks this server; star saves it to Favourites"
            };

            Color body = row.Offline ? RowOffline : RowNormal;
            for (int i = ColName; i < it.SubItems.Count; i++) it.SubItems[i].ForeColor = body;

            it.SubItems[ColRefresh].ForeColor = Color.FromArgb(120, 150, 190);
            it.SubItems[ColStar].ForeColor = fav
                ? Color.FromArgb(235, 200, 90) : Color.FromArgb(95, 95, 102);
            it.SubItems[ColStatus].ForeColor =
                !row.Online.HasValue ? Color.FromArgb(120, 120, 128)
                : row.Online.Value ? StatusOn : StatusOff;

            // With UseItemStyleForSubItems off, the row's own BackColor is
            // ignored - each sub-item has to be shaded itself.
            it.BackColor = band;
            for (int i = 0; i < it.SubItems.Count; i++) it.SubItems[i].BackColor = band;
            return it;
        }

        /// <summary>
        /// Swaps in a new set of rows. Selection is kept by address rather than
        /// by index, because sorting moves rows around underneath it.
        /// </summary>
        private void SetRows(List<Row> rows, string keepSelected)
        {
            _rows = rows;
            _rowIndex.Clear();
            for (int i = 0; i < rows.Count; i++) _rowIndex[rows[i].Endpoint] = i;

            _list.BeginUpdate();
            try
            {
                _list.SelectedIndices.Clear();
                _list.VirtualListSize = rows.Count;

                int sel;
                if (keepSelected != null && _rowIndex.TryGetValue(keepSelected, out sel))
                {
                    _list.SelectedIndices.Add(sel);
                    if (sel < rows.Count) _list.EnsureVisible(sel);
                }
                else if (rows.Count > 0)
                {
                    _list.SelectedIndices.Add(0);
                }
            }
            catch { /* a resize mid-swap can throw; the next paint fixes it */ }
            finally { _list.EndUpdate(); }

            _lastTopIndex = -1;                     // re-examine what is on screen
            _visTimer.Start();                      // and start querying them
            SweepAll(rows);                         // then work down the rest
            _list.Invalidate();
        }

        /// <summary>Repaints one row in place, by address.</summary>
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

            // "All" is two queries, not one. Steam scopes a list to the AppId it
            // is asked for, so the only way to cover both builds is to ask twice
            // and merge - which is what _appQueue is for.
            _appQueue = new Queue<uint>(AppsForQuery);
            _merged.Clear();
            _mergedKeys.Clear();

            // Seed the merge with what is already known. Steam's answer arrives
            // over tens of seconds and is capped at 10,000, so treating it as a
            // REPLACEMENT would empty the table and then refill it - and would
            // throw away servers this cap happened to omit this time. Folding
            // the new answer into the old list instead means the view only ever
            // improves, and coverage builds up across refreshes.
            foreach (var srv in _cache)
                if (_mergedKeys.Add(srv.Endpoint)) _merged.Add(srv);

            uint app = _appQueue.Dequeue();

            // Steam only filters the internet list; the scoped lists are small,
            // so those are filtered locally after they arrive.
            var steamFilters = kind == ListKind.Internet
                             ? _filters.ToSteamFilters()
                             : new List<KeyValuePair<string, string>>();

            // Deliberately NOT clearing the table here - see RefreshCurrent.
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
            _pollApp = app;
            _pollTicks = 0;
            _stableTicks = 0;
            _lastRawCount = -1;
            _pollKind = kind;

            // Poll the small lists faster. Measured against Steam: the Recent
            // and Favourites lists arrive COMPLETE in the first poll - 88 and 48
            // servers respectively, landing inside 704ms and never changing
            // after - so at the internet list's 700ms cadence almost all the
            // remaining wait is just the gaps between polls.
            _pollTimer.Interval = PollingSmallList ? SmallListPollMs : InternetPollMs;
            _pollTimer.Start();
        }

        private int _pollTicks;
        private ListKind _pollKind = ListKind.Internet;

        // The AppIds still to query for this refresh, and what has come back so
        // far. Both are empty for an ordinary single-build browse.
        private Queue<uint> _appQueue = new Queue<uint>();
        private readonly List<BrowserServer> _merged = new List<BrowserServer>();
        private readonly HashSet<string> _mergedKeys = new HashSet<string>();
        private uint _pollApp;

        private static string BuildLabel(uint app)
        {
            return app == (uint)A2S.ExperimentalAppId ? "Experimental" : "stable";
        }

        /// <summary>
        /// How long a fetched list is trusted before a tab switch re-fetches it.
        /// Short enough that a list is never really stale, long enough that
        /// moving between tabs costs nothing.
        /// </summary>
        private static readonly TimeSpan CacheFreshFor = TimeSpan.FromMinutes(1);

        private const int InternetPollMs = 700;
        private const int SmallListPollMs = 250;

        /// <summary>
        /// Whether this list is one of the small, locally-sourced ones.
        ///
        /// Recent, Friends, LAN and Steam's own favourites come from a handful
        /// of addresses Steam already knows about. They arrive in a second or
        /// two. The internet list is five thousand servers dribbling in over a
        /// minute, and the two need completely different patience - which is the
        /// whole reason the thresholds below are not constants.
        /// </summary>
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

            // IsRefreshing is false for the first moment after a request is
            // made, before Steam has actually started. Taking that at face value
            // stops the timer instantly with an empty list, so the query is only
            // considered finished once it has had a few ticks to get going.
            int minTicks = PollingSmallList ? 2 : 5;
            if (_pollTicks < minTicks) done = false;

            // Steam keeps IsRefreshing true long after the list has stopped
            // growing, while it pings stragglers. Once the count has held still
            // for a while the list is done as far as the player cares.
            //
            // HOW LONG "A WHILE" IS depends entirely on which list this is.
            // Twelve ticks is 8.4 seconds, which is right for the internet list
            // - servers trickle in with real gaps between them and stopping
            // early would truncate it. Applying the same wait to the Recent tab
            // was the bug: those few servers all land inside the first second,
            // and then the tab sat there apparently loading for another eight
            // with the final list already on screen.
            if (raw > 0 && raw == _lastRawCount) _stableTicks++;
            else _stableTicks = 0;
            // 4 ticks at 250ms is a second of stillness, against a list that
            // measured as complete after the very first poll.
            if (_stableTicks >= (PollingSmallList ? 4 : 12)) done = true;

            // A small list that comes back genuinely empty must still end - with
            // no servers the count never "holds still" at a non-zero value, so
            // the rule above can never fire and only the long timeout would stop
            // it. Steam saying it has finished is enough here.
            if (PollingSmallList && raw == 0 && _pollTicks >= 8) done = true;

            // And never spin forever if Steam goes quiet.
            if (_pollTicks > (PollingSmallList ? 80 : 150)) done = true;

            // With two builds in flight, what is cached has to be everything
            // seen SO FAR plus the query still running - otherwise the list
            // visibly empties out when the second query starts.
            var combined = _merged.Count == 0 ? _browser : Combine(_browser);
            _caches[CacheKey] = combined;
            _cacheTimes[CacheKey] = DateTime.Now;

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var srv in _browser) _lastSeen[srv.Endpoint] = now;

            // Only redraw when the list actually grew. Rebuilding every tick
            // would throw away the selection and yank the scroll position back
            // to the top while the player is trying to read it.
            if (raw != _lastRawCount || done)
            {
                _lastRawCount = raw;
                _lastShownCount = RenderFromCache();
            }

            UpdateStatus(done ? null : "(loading from Steam...)");

            if (!done) return;

            // One build finished. Keep its servers and move to the next.
            if (_appQueue.Count > 0)
            {
                foreach (var srv in _browser)
                    if (_mergedKeys.Add(srv.Endpoint)) _merged.Add(srv);

                uint next = _appQueue.Dequeue();
                Log("  " + BuildLabel(_pollApp) + ": " + _browser.Count
                    + " servers. Now asking for " + BuildLabel(next) + "...");

                var nextFilters = _pollKind == ListKind.Internet
                                ? _filters.ToSteamFilters()
                                : new List<KeyValuePair<string, string>>();

                if (SteamServerList.Start(_pollKind, next, nextFilters))
                {
                    _pollApp = next;
                    _pollTicks = 0;
                    _stableTicks = 0;
                    _lastRawCount = -1;
                    return;                      // the timer keeps running
                }

                Log("  Steam refused the " + BuildLabel(next) + " query; showing what we have.");
            }

            _pollTimer.Stop();
            Log("Master list cached: " + combined.Count
                + " servers. Searching now filters this list instantly.");

            // Keep it for next time, so the next launch of this tab is instant.
            ServerStore.SaveList(CacheKey, combined, _lastSeen);
            if (raw >= 10000)
                UpdateStatus("Steam caps this at 10,000 - narrow it with FILTERS then REFRESH.");
        }

        /// <summary>
        /// Everything already collected from earlier AppIds, plus what the
        /// running query has so far, with duplicates dropped. A server can
        /// legitimately appear in both answers, and counting it twice would put
        /// two identical rows in the list.
        /// </summary>
        private List<BrowserServer> Combine(List<BrowserServer> current)
        {
            var all = new List<BrowserServer>(_merged);
            var seen = new HashSet<string>(_mergedKeys);
            foreach (var srv in current)
                if (seen.Add(srv.Endpoint)) all.Add(srv);
            return all;
        }

        /// <summary>
        /// Filters and draws the cached master list. No network at all - this is
        /// what makes searching instant.
        /// </summary>
        /// <summary>
        /// Filters and sorts the cached list into the rows the ListView will
        /// ask for. Nothing is instantiated per server here - that is what
        /// makes ten thousand rows cheap.
        /// </summary>
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
                        Name = srv.Name,
                        Host = srv.Host,
                        Port = srv.Port,
                        Reason = reason
                    });

                    // Recorded either way, but only hidden when asked.
                    if (_filters.HideFakes) { _hiddenFakes++; continue; }
                }

                if (!_filters.Matches(srv)) continue;

                var row = new Row
                {
                    Name = srv.Name, Map = srv.Map, Host = srv.Host, Port = srv.Port,
                    QueryPort = srv.QueryPort,
                    Players = srv.Players, MaxPlayers = srv.MaxPlayers, Ping = srv.Ping,
                    AppId = srv.AppId, Tags = srv.Tags,
                    Favourite = _favourites.Contains(srv.Endpoint)
                };

                // A live result always beats the master list's stale copy.
                ServerInfo live;
                if (_live.TryGetValue(srv.Endpoint, out live)) Apply(row, live);
                rows.Add(row);
            }

            SortRows(rows);

            SetRows(rows, _selectedEndpoint);
            return rows.Count;
        }

        /// <summary>
        /// Works out which addresses are running a redirect farm: many servers
        /// AND many different community names on one address. Both conditions
        /// matter - a legitimate host with twenty servers wears one name, while
        /// a farm wears a different one on nearly every port.
        /// </summary>
        private void RecomputeFarms(List<BrowserServer> cache)
        {
            _farmIps.Clear();
            if (cache.Count == 0) return;

            var names = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

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
            }

            foreach (var kv in counts)
            {
                if (kv.Value < BrowserFilters.FarmServersPerIp) continue;
                if (names[kv.Key].Count < BrowserFilters.FarmNamesPerIp) continue;
                _farmIps.Add(kv.Key);
            }

            // ---- and again per /24, on density ----
            // A farm spread thinly over a subnet dodges the per-address rule.
            // Density is what separates it from a hosting provider, which also
            // has a busy /24 but only one or two servers per address.
            _farmSubnets.Clear();
            var netCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var netAddrs = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var netNames = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

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
            }

            foreach (var kv in netCount)
            {
                if (kv.Value < BrowserFilters.FarmServersPerSubnet) continue;
                if (netNames[kv.Key].Count < BrowserFilters.FarmNamesPerSubnet) continue;

                double perAddress = kv.Value / (double)Math.Max(1, netAddrs[kv.Key].Count);
                if (perAddress < BrowserFilters.FarmServersPerAddress) continue;

                _farmSubnets.Add(kv.Key);
            }

            // Anything the player has explicitly allowed is never flagged.
            foreach (string ok in _allowed)
            {
                _farmIps.Remove(ok);
                _farmSubnets.Remove(ok);
            }
        }

        /// <summary>Why a server was hidden, or null when it was not.</summary>
        private string FakeReason(BrowserServer s)
        {
            if (s == null) return null;

            // A server the player saved is trusted outright. Saving one is an
            // explicit statement that they know what it is, and the farm rules
            // are heuristics about STRANGERS - they key on an address running
            // many servers under many names, which is exactly what a person
            // hosting a dozen of their own looks like from the outside. Without
            // this, a player could star their own server and watch it vanish
            // from the list, which is also the one case where being told
            // "pinned to the top" would be a lie.
            if (_favourites.Contains(s.Endpoint)) return null;

            if (_allowed.Contains(s.Host) ||
                _allowed.Contains(BrowserFilters.Subnet24(s.Host))) return null;

            if (s.MaxPlayers > BrowserFilters.MaxRealSlots)
                return "claims " + s.MaxPlayers + " slots (DayZ maximum is "
                     + BrowserFilters.MaxRealSlots + ")";

            if (_farmIps.Contains(s.Host))
                return "address " + s.Host + " runs many servers under many names";

            string net = BrowserFilters.Subnet24(s.Host);
            if (_farmSubnets.Contains(net))
                return "subnet " + net + ".x is a redirect farm";

            return null;
        }

        // ---------------------------------------------------- query pool ----

        /// <summary>
        /// Starts the query workers. They live for the life of the window and
        /// always work on whatever is currently on screen, so scrolling changes
        /// what gets queried instead of piling up more work.
        /// </summary>
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

        private void PingLoop()
        {
            while (!_closing)
            {
                Row row = null;
                lock (_pingLock)
                {
                    // 1. anything visible that has not been asked yet
                    foreach (var candidate in _pingWanted)
                    {
                        if (_pingBusy.Contains(candidate.Endpoint)) continue;
                        if (_asked.Contains(candidate.Endpoint)) continue;
                        row = candidate;
                        break;
                    }

                    // 2. otherwise carry on down the rest of the list
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

                ServerInfo info;
                try { info = A2S.GetInfoAt(row.Host, row.EffectiveQueryPort, 1200); }
                catch { info = new ServerInfo { Error = "query failed" }; }

                lock (_pingLock) { _pingBusy.Remove(row.Endpoint); }

                var captured = row;
                var result = info;
                // The window can close between the query and the marshal back.
                // ObjectDisposedException derives from InvalidOperationException,
                // so this one catch covers the window closing mid-query.
                try { BeginInvoke((Action)(() => ApplyLive(captured, result))); }
                catch (InvalidOperationException) { return; }
            }
        }

        /// <summary>Sets the high-priority set: the rows currently on screen.</summary>
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
        /// Hands the workers the full list to sweep through once the visible
        /// rows are done. Called whenever the rows change, so the sweep always
        /// follows what is actually being shown.
        /// </summary>
        private void SweepAll(List<Row> rows)
        {
            lock (_pingLock)
            {
                _pingAll = rows;
                _sweep = 0;
            }
            StartPingPool();
        }

        /// <summary>
        /// Queries only the rows on screen. Pinging every server in a ten
        /// thousand row list to fill in twenty visible ones would never finish,
        /// and was what made scrolling lock up.
        /// </summary>
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
            for (int i = Math.Max(0, top); i < end; i++) want.Add(_rows[i]);
            Enqueue(want, false);
        }

        private void ApplyLive(Row row, ServerInfo info)
        {
            _live[row.Endpoint] = info;
            _checked++;
            Apply(row, info);
            Redraw(row.Endpoint);

            if (IsSteamTab(_tab)) UpdateStatus(null);

            if (!info.Online)
            {
                // Re-sort shortly so offline servers drop to the bottom, but
                // debounced or rows would shuffle while being read.
                _resortTimer.Stop();
                _resortTimer.Start();
            }

            if (!IsSteamTab(_tab)) FinishMine();
        }

        /// <summary>
        /// The one place the status line is composed, so the master-list poll
        /// and the background sweep cannot overwrite each other - which they did,
        /// and the poll usually won because it finishes later.
        /// </summary>
        private void UpdateStatus(string suffix)
        {
            int online = 0, done = 0;
            foreach (var r in _rows)
            {
                if (r.Online.HasValue) done++;
                if (r.Online == true) online++;
            }

            string text = _rows.Count + " servers";
            if (_hiddenFakes > 0) text += "  (" + _hiddenFakes + " fake hidden)";
            if (done > 0)
                text += string.Format("  -  {0} checked, {1} online{2}",
                                      done, online, done >= _rows.Count ? " (all checked)" : "");
            if (!string.IsNullOrEmpty(suffix)) text += "   " + suffix;
            _status.Text = text;
        }

        /// <summary>
        /// Settings, including the record of every server the fake rules threw
        /// out. Anything allowed back there takes effect on the next redraw.
        /// </summary>
        private void OnSettings(object sender, EventArgs e)
        {
            bool hide;
            bool changed = SettingsDialog.Show(this, Flagged, _allowed, _filters.HideFakes, out hide);
            if (!changed) return;

            _filters.HideFakes = hide;
            if (_chkHideFakes != null) _chkHideFakes.Checked = hide;

            if (IsSteamTab(_tab)) RenderFromCache();
            else RefreshMine();
        }

        /// <summary>Re-query one row, ignoring the "already asked" set.</summary>
        private void RefreshOneRow(Row row)
        {
            if (row == null) return;
            lock (_pingLock) { _asked.Remove(row.Endpoint); }
            Enqueue(new[] { row }, true);
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

        /// <summary>
        /// Right-clicking must select the row under the pointer first, or the
        /// menu would act on whatever happened to be selected before.
        /// </summary>
        private void OnListMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            var hit = _list.HitTest(e.Location);
            if (hit.Item != null) hit.Item.Selected = true;
        }

        private ContextMenuStrip BuildServerMenu()
        {
            var menu = new ContextMenuStrip
            {
                BackColor = Panel2,
                ForeColor = Color.Gainsboro,
                ShowImageMargin = false
            };

            // BackColor alone is not enough: the default renderer paints its own
            // light background and would leave a pale menu on a dark window.
            menu.Renderer = new ToolStripProfessionalRenderer(new DarkMenu());

            var copyAll = new ToolStripMenuItem("Copy server info");
            copyAll.Click += (s, e) => CopyServerInfo(false);
            menu.Items.Add(copyAll);

            var copyAddr = new ToolStripMenuItem("Copy address only");
            copyAddr.Click += (s, e) => CopyServerInfo(true);
            menu.Items.Add(copyAddr);

            menu.Items.Add(new ToolStripSeparator());

            var fav = new ToolStripMenuItem("Add to / remove from Favourites");
            fav.Click += (s, e) =>
            {
                ToggleFavourite(SelectedRow);
            };
            menu.Items.Add(fav);

            var recheck = new ToolStripMenuItem("Refresh this server");
            recheck.Click += (s, e) =>
            {
                RefreshOneRow(SelectedRow);
            };
            menu.Items.Add(recheck);

            menu.Items.Add(new ToolStripSeparator());

            var connect = new ToolStripMenuItem("Connect");
            connect.Click += OnConnect;
            menu.Items.Add(connect);

            // Nothing sensible to act on with an empty list.
            menu.Opening += (s, e) => { if (SelectedRow == null) e.Cancel = true; };
            return menu;
        }

        /// <summary>
        /// Puts the server on the clipboard in a form that can be pasted into
        /// Discord and still make sense to whoever reads it.
        /// </summary>
        private void CopyServerInfo(bool addressOnly)
        {
            var row = SelectedRow;
            if (row == null) return;

            string text;
            if (addressOnly)
            {
                text = row.Endpoint;
            }
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
                // another app - neither is worth an error dialog.
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

        /// <summary>Star clicked - save or unsave this server.</summary>
        private void ToggleFavourite(Row row)
        {
            if (row == null) return;

            if (_favourites.Contains(row.Endpoint))
            {
                _favourites.Remove(row.Endpoint);
                ServerStore.SaveFavourites(_favourites);
                UpdateFavButton();

                // Favourites is the saved list, so unstarring there drops the row.
                if (_tab == Tab.Favourites) { RefreshMine(); return; }
            }
            else
            {
                _favourites.Add(row.Endpoint);
                ServerStore.RememberName(row.Endpoint, row.Name);
                ServerStore.SaveFavourites(_favourites);
                UpdateFavButton();
            }

            // The browsable lists pin saved servers to the top, so starring one
            // changes where it belongs. Re-render rather than repainting the
            // single row, or the star would light up while the row stayed put
            // and the list would only right itself on the next refresh.
            //
            // Nothing is lost by doing this: SetRows is handed the selected
            // endpoint and calls EnsureVisible on it, so the row the player
            // just clicked stays selected and stays on screen - it simply
            // arrives at its new position with the cursor still on it.
            if (IsSteamTab(_tab)) { RenderFromCache(); return; }

            Redraw(row.Endpoint);
        }

        // ------------------------------------------------------- filters ----
        private void ReadFilterUi()
        {
            // The top search box and the panel's Server Name field mean the same
            // thing; whichever the player typed in wins.
            _filters.Name = _search.Text.Trim().Length > 0 ? _search.Text.Trim() : _fName.Text;
            _filters.Address = _fAddr.Text;
            _filters.Map = _fMap.Text;
            int ping;
            _filters.MaxPing = int.TryParse(_fPing.Text.Trim(), out ping) ? ping : 0;
            _filters.Players = (PlayersMode)_cbPlayers.SelectedIndex;
            _filters.GameTime = (TimeMode)_cbTime.SelectedIndex;
            _filters.ThirdPerson = (TriState)_segThird.Index;
            _filters.Mods = (TriState)_segMods.Index;

            // Driven by the TAB rather than by a control in the filter panel.
            // A player hunting for official servers looks along the tabs; they
            // do not open Filters and hunt for a "Server Type" row, and a
            // launcher that hides it there reads as one that cannot find them.
            _filters.Official = _tab == Tab.Official ? TriState.Enabled : TriState.Any;
            _filters.NoPassword = _chkNoPass.Checked;
            _filters.HideFull = _chkHideFull.Checked;
            _filters.HideEmpty = _chkHideEmpty.Checked;
            _filters.HideFakes = _chkHideFakes.Checked;
        }

        /// <summary>
        /// Coalesces filter changes. Typing or dragging through a combo can fire
        /// this many times a second, and each one re-sorts the whole list.
        /// </summary>
        private void QueueFilter()
        {
            _typeTimer.Stop();
            _typeTimer.Start();
        }

        private void ClearFilterUi()
        {
            _fName.Text = _fAddr.Text = _fMap.Text = _fPing.Text = "";
            _search.Text = "";
            _cbPlayers.SelectedIndex = 0;
            _cbTime.SelectedIndex = 0;
            _segThird.Index = 0;
            _segMods.Index = 0;
            _chkNoPass.Checked = _chkHideFull.Checked = _chkHideEmpty.Checked = false;
            _chkHideFakes.Checked = true;      // clearing filters should not unleash the fakes
            _filters.Clear();
        }

        private void ApplyFilters()
        {
            ReadFilterUi();
            if (IsSteamTab(_tab))
            {
                // Filter the cached list. The master list takes tens of seconds
                // to fetch, so searching must never trigger another fetch - only
                // REFRESH does that, and it sends these same filters to Steam so
                // a narrowed refresh can also reach past the 10,000 cap.
                if (_cache.Count == 0) { StartCommunityQuery(); return; }
                int n = RenderFromCache();
                _status.Text = string.Format("{0} of {1} cached servers shown{2}",
                    n, _cache.Count, _filters.AnyActive ? "  (filtered)" : "");
            }
            else
            {
                RefreshMine();
            }
        }

        private void OnSearchKey(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            ApplyFilters();
        }

        // ---------------------------------------------------- favourites ----
        private void OnToggleFavourite(object sender, EventArgs e)
        {
            ToggleFavourite(SelectedRow);
        }

        private void UpdateFavButton()
        {
            var row = SelectedRow;
            _favBtn.Text = row != null && _favourites.Contains(row.Endpoint)
                         ? "REMOVE FAVOURITE" : "ADD TO FAVOURITES";
        }

        private Row SelectedRow
        {
            get
            {
                // Virtual items are transient and carry no Tag, so the index is
                // the only reliable handle back to the row.
                if (_list.SelectedIndices.Count == 0) return null;
                int i = _list.SelectedIndices[0];
                return i >= 0 && i < _rows.Count ? _rows[i] : null;
            }
        }

        // ------------------------------------------------------ mod panel ----
        private void ShowMods()
        {
            UpdateFavButton();
            _mods.Items.Clear();
            var row = SelectedRow;
            if (row == null) return;

            _modsHeader.Text = "Content required by server  -  " + row.Endpoint;
            _mods.Items.Add(new ListViewItem(new[] { "querying server...", "", "" }));

            _desc.Text = "";
            _descHeader.Text = "  Description";

            var captured = row;
            new Thread(() =>
            {
                var rules = QueryModsChecked(captured);
                string steam = FindSteam();

                // Ask Steam for the published version of each mod while still
                // OFF the UI thread. The rows built below compare that against
                // each meta.cpp to decide what is out of date, and the query is
                // a network round trip - doing it here means the list is right
                // the first time it is drawn, with nothing blocking.
                if (rules != null && rules.Mods.Count > 0)
                {
                    try { SteamWorkshop.PrefetchWorkshopTimes(rules.Mods.Select(m => m.WorkshopId), null); }
                    catch { }
                }

                try { BeginInvoke((Action)(() => FillMods(captured, rules, steam))); }
                catch (InvalidOperationException) { }
            })
            { IsBackground = true }.Start();
        }

        private void FillMods(Row row, ServerRules rules, string steam)
        {
            var sel = SelectedRow;
            if (sel == null || sel.Endpoint != row.Endpoint) return;   // selection moved on
            _mods.Items.Clear();

            ShowDescription(row, rules);
            var mods = rules == null ? null : rules.Mods;

            if (mods == null)
            {
                _mods.Items.Add(new ListViewItem(new[]
                { "Could not read the mod list.", "", "server offline or not answering queries" })
                { ForeColor = Color.FromArgb(200, 150, 150) });
                return;
            }
            if (mods.Count == 0)
            {
                bool tagged = HasTag(row.Tags, "mod");
                _mods.Items.Add(tagged
                    ? new ListViewItem(new[]
                      {
                          "Could not read this server's mod list",
                          "",
                          "the server reports that it IS modded - try Refresh"
                      })
                      { ForeColor = Color.FromArgb(220, 150, 150) }
                    : new ListViewItem(new[] { "No mods required", "", "ready to connect" })
                      { ForeColor = Good });
                return;
            }

            // Only say the list is short when the parse could not prove it ended
            // cleanly - the header's own count byte is not trustworthy enough to
            // raise an alarm on.
            if (!rules.Complete)
                _mods.Items.Add(new ListViewItem(new[]
                {
                    "(the mod list may be incomplete)", "",
                    "the server's reply did not end cleanly"
                })
                { ForeColor = Color.FromArgb(220, 190, 120) });

            foreach (var m in mods)
                _mods.Items.Add(MakeModRow(m, steam));
        }

        /// <summary>
        /// One mod row: what state it is in, plus the three actions.
        ///
        /// "Installed" means the mod has a meta.cpp - the manifest Steam writes
        /// last, once the install has completed. "Out of date" is judged both by
        /// Steam's own flag and by the timestamp inside that meta.cpp against
        /// the publication time Steam reports for the workshop item; see
        /// SteamWorkshop.NeedsUpdate for why one signal is not enough.
        /// </summary>
        /// <summary>A time span in the largest unit that still reads naturally.</summary>
        private static string Age(TimeSpan t)
        {
            if (t.TotalDays >= 365) return string.Format("{0:F1} years", t.TotalDays / 365.0);
            if (t.TotalDays >= 1)   return string.Format("{0:F0} days", t.TotalDays);
            if (t.TotalHours >= 1)  return string.Format("{0:F0} hours", t.TotalHours);
            return string.Format("{0:F0} minutes", t.TotalMinutes);
        }

        private ListViewItem MakeModRow(Mod m, string steam)
        {
            bool have = steam != null && SteamWorkshop.IsInstalled(steam, m.WorkshopId);
            bool broken = !have && steam != null && SteamWorkshop.IsBrokenInstall(steam, m.WorkshopId);
            bool stale = have && SteamWorkshop.NeedsUpdate(steam, m.WorkshopId);

            string status;
            Color colour;
            if (broken)       { status = "BROKEN - files present but no meta.cpp"; colour = Color.FromArgb(230, 130, 130); }
            else if (!have)   { status = "MISSING - will be downloaded";           colour = Color.FromArgb(220, 190, 120); }
            else if (stale)
            {
                // Say HOW far behind when the timestamps could tell us, because
                // "out of date" alone leaves a player guessing whether the
                // launcher is being fussy.
                TimeSpan behind = steam == null ? TimeSpan.Zero
                                                : SteamWorkshop.StaleBy(steam, m.WorkshopId);
                status = behind > TimeSpan.Zero
                    ? "OUT OF DATE by " + Age(behind) + " - will update"
                    : "OUT OF DATE - will update";
                colour = Color.FromArgb(225, 175, 90);
            }
            else              { status = "installed";                              colour = Good; }

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

        /// <summary>Repair / Sub / Remove, hit-tested like the server list.</summary>
        private void OnModClick(object sender, MouseEventArgs e)
        {
            var hit = _mods.HitTest(e.Location);
            if (hit.Item == null || hit.SubItem == null) return;

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

        /// <summary>
        /// Asks a server for its mods, and does not take "none" for an answer
        /// when the server's own Steam tags say otherwise.
        ///
        /// The tags are an independent signal: DayZ sets "mod" on a modded
        /// server regardless of the rules reply. So an empty mod list from a
        /// server tagged "mod" is a contradiction - a dropped UDP reply or a
        /// truncated one - and is worth asking again before telling the player
        /// there is nothing to install.
        /// </summary>
        private static ServerRules QueryModsChecked(Row row)
        {
            bool tagged = HasTag(row.Tags, "mod");

            ServerRules rules = A2S.GetRulesAt(row.Host, row.EffectiveQueryPort);
            if (rules != null && rules.Mods.Count > 0) return rules;
            if (!tagged) return rules;              // "no mods" agrees with the tags

            // Contradiction: ask again, giving the server longer each time.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                Thread.Sleep(220);
                var retry = A2S.GetRulesAt(row.Host, row.EffectiveQueryPort, 4000 + attempt * 2000);
                if (retry != null && retry.Mods.Count > 0) return retry;
                if (rules == null) rules = retry;
            }
            return rules;
        }

        /// <summary>
        /// The server's own description, straight from A2S_RULES - the same text
        /// the official launcher shows beside a server. Servers commonly put
        /// their Discord link here, so the header carries a few facts too.
        /// </summary>
        private void ShowDescription(Row row, ServerRules rules)
        {
            if (rules == null)
            {
                _descHeader.Text = "  Description";
                _desc.Text = "The server did not answer the query.";
                return;
            }

            var bits = new List<string>();
            string version = rules.RequiredVersion;
            if (version.Length > 0) bits.Add("Requires " + version);
            if (HasTag(row.Tags, "privHive")) bits.Add("Private hive");
            bits.Add(HasTag(row.Tags, "no3rd") ? "1st person only" : "3rd and 1st person");

            string day = Accel(row.Tags, "etm");
            string night = Accel(row.Tags, "entm");
            if (day.Length > 0) bits.Add("Day " + day);
            if (night.Length > 0) bits.Add("Night " + night);

            _modsHeader.Text = "  " + row.Endpoint + "   |   " + string.Join("   |   ", bits.ToArray());

            // Servers write CRLF into this field; a multiline TextBox needs it
            // exactly that way or the line breaks render as boxes.
            string text = (rules.Description ?? "").Replace("\r\n", "\n")
                                                   .Replace("\r", "\n")
                                                   .Replace("\n", Environment.NewLine)
                                                   .Trim();
            _desc.Text = text.Length > 0 ? text : "(this server publishes no description)";
        }

        /// <summary>
        /// Opens a link from a server description.
        ///
        /// That text comes from whoever runs the server, so it is not trusted:
        /// only http and https are ever handed to the shell. Without that check
        /// a server could publish a "link" pointing at a local file or some
        /// other protocol handler and have the launcher open it.
        /// </summary>
        private void OpenLink(string link)
        {
            if (string.IsNullOrEmpty(link)) return;

            string candidate = link.Trim();
            if (candidate.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                candidate = "http://" + candidate;

            Uri uri;
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                _status.Text = "Ignored a link that was not http or https: " + link;
                return;
            }

            try { Process.Start(uri.AbsoluteUri); }
            catch (Exception ex) { _status.Text = "Could not open the link: " + ex.Message; }
        }

        /// <summary>
        /// Day/night acceleration, published in the Steam tags as etm/entm -
        /// "etm4.000000" means the in-game day runs at 4x.
        /// </summary>
        private static string Accel(string tags, string prefix)
        {
            if (string.IsNullOrEmpty(tags)) return "";
            foreach (var raw in tags.Split(','))
            {
                string t = raw.Trim();
                if (!t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

                // "entm" also starts with "etm"? No - but "etm" is a prefix of
                // nothing else here, so an exact-length check keeps it honest.
                string rest = t.Substring(prefix.Length);
                double v;
                if (!double.TryParse(rest, System.Globalization.NumberStyles.Float,
                                     System.Globalization.CultureInfo.InvariantCulture, out v))
                    continue;
                return v.ToString("0.##") + "x";
            }
            return "";
        }

        // --------------------------------------------------------- servers ----
        /// <summary>
        /// Join by address, for a server that is not in the master list at all -
        /// which is common for brand new or unlisted servers. Optionally saves it
        /// to Favourites on the way.
        /// </summary>
        private void OnDirectConnect(object sender, EventArgs e)
        {
            string host;
            int port;
            bool save;
            if (!DirectConnectDialog.Show(this, out host, out port, out save)) return;

            var row = new Row { Name = host + ":" + port, Host = host, Port = port };

            if (save)
            {
                _favourites.Add(row.Endpoint);
                ServerStore.RememberName(row.Endpoint, row.Name);
                ServerStore.SaveFavourites(_favourites);
                Log("Saved " + row.Endpoint + " to Favourites.");
            }

            _connect.Enabled = false;
            Cursor = Cursors.WaitCursor;
            _log.Clear();
            try { Launch(row); }
            catch (Exception ex)
            {
                Log("ERROR: " + ex.Message);
                MessageBox.Show(ex.Message, "Direct connect",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _connect.Enabled = true;
                Cursor = Cursors.Default;
            }
        }

        // --------------------------------------------------------- connect ----
        private void OnConnect(object sender, EventArgs e)
        {
            var row = SelectedRow;
            if (row == null) { MessageBox.Show("Pick a server first."); return; }

            _connect.Enabled = false;
            Cursor = Cursors.WaitCursor;
            _log.Clear();
            try { Launch(row); }
            catch (Exception ex)
            {
                Log("ERROR: " + ex.Message);
                MessageBox.Show(ex.Message, "Beautiful Potato Experimental Launcher",
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
            }

            // Short mod paths. Absolute !Workshop paths run ~90 characters each;
            // junctions named by workshop id inside the game folder let the
            // argument be relative and tiny, and the working directory below is
            // the game folder so they resolve. Verified in a real launch.
            var modArgs = new List<string>();
            string shortRoot = Path.Combine(gameDir, "!m");
            foreach (var m in mods)
            {
                string numeric = SteamWorkshop.ItemPath(steam, m.WorkshopId);
                string link = Path.Combine(shortRoot, m.WorkshopId.ToString());
                try { Directory.CreateDirectory(shortRoot); } catch { }

                if (Junction.TryCreate(link, numeric)) modArgs.Add("!m\\" + m.WorkshopId);
                else modArgs.Add(numeric);           // long, but it still works
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

            Log("");
            Log("Started. DayZ takes a minute or two to appear - be patient.");
            _status.Text = "Launched " + srv.Endpoint;
        }

        // ----------------------------------------------------------- utils --
        private static string FindSteam()
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view))
                    using (var key = baseKey.OpenSubKey(@"Software\Valve\Steam"))
                    {
                        var path = key?.GetValue("SteamPath") as string;
                        if (!string.IsNullOrEmpty(path))
                        {
                            path = path.Replace('/', '\\');
                            if (Directory.Exists(Path.Combine(path, "steamapps"))) return path;
                        }
                    }
                }
                catch { }
            }
            string fallback = @"C:\Program Files (x86)\Steam";
            return Directory.Exists(Path.Combine(fallback, "steamapps")) ? fallback : null;
        }

        private static string FindGameDir(string steam, ulong appId)
        {
            string common = Path.Combine(steam, "steamapps", "common");
            string[] folders = appId == A2S.StableAppId ? StableFolders : ExpFolders;
            return folders.Select(f => Path.Combine(common, f))
                          .FirstOrDefault(d => File.Exists(Path.Combine(d, GameExe)));
        }

        private static bool IsRunning(string exeName)
        {
            try { return Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exeName)).Length > 0; }
            catch { return false; }
        }

        private static Image LoadImage(string resourceName)
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
            {
                if (s == null) return null;
                var ms = new MemoryStream();
                s.CopyTo(ms);
                ms.Position = 0;
                return Image.FromStream(ms);
            }
        }

        private void Log(string line)
        {
            if (_log == null) return;
            _log.AppendText(line + Environment.NewLine);
            _log.SelectionStart = _log.TextLength;
            _log.ScrollToCaret();
            Application.DoEvents();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _closing = true;
            RememberWindow();
            RememberAllSplits();
            _pollTimer.Stop();
            _visTimer.Stop();
            _typeTimer.Stop();
            _modRecheck.Stop();
            _resortTimer.Stop();
            SteamServerList.Stop();
            SteamWorkshop.Shutdown();
            base.OnFormClosed(e);
        }

        // ------------------------------------------------ window geometry --

        /// <summary>
        /// Puts the window back where it was last time.
        ///
        /// WHY THE SAVED POSITION IS CHECKED AGAINST THE SCREENS
        ///   Monitors come and go - a laptop leaves its dock, a second screen is
        ///   unplugged, a resolution changes. A window restored onto a monitor
        ///   that is no longer there opens completely off-screen, with no
        ///   visible title bar to drag it back by, and looks for all the world
        ///   like the program failed to start. So the saved rectangle is only
        ///   used if it still overlaps a screen that exists; otherwise the size
        ///   is kept and the window is re-centred, because the size is the part
        ///   worth remembering and the position is the part that goes stale.
        /// </summary>
        private void RestoreWindow()
        {
            int x, y, w, h;
            bool maximised;
            if (!ServerStore.LoadWindow(out x, out y, out w, out h, out maximised)) return;

            // Never smaller than the form can actually work at.
            w = Math.Max(w, MinimumSize.Width);
            h = Math.Max(h, MinimumSize.Height);

            var saved = new Rectangle(x, y, w, h);
            bool onScreen = false;
            foreach (var screen in Screen.AllScreens)
            {
                var overlap = Rectangle.Intersect(screen.WorkingArea, saved);
                // A sliver peeking onto a screen is not good enough - there has
                // to be enough of the title bar showing to grab hold of.
                if (overlap.Width >= 200 && overlap.Height >= 60) { onScreen = true; break; }
            }

            Size = new Size(w, h);
            if (onScreen)
            {
                StartPosition = FormStartPosition.Manual;
                Location = new Point(x, y);
            }
            // else: keep CenterScreen, so it lands somewhere the player can see.

            if (maximised) WindowState = FormWindowState.Maximized;
        }

        /// <summary>
        /// Saves the size on the way out.
        ///
        /// RestoreBounds, not Bounds: while a window is maximised or minimised,
        /// Bounds describes the maximised frame, and saving that would restore a
        /// window that un-maximises to full screen and can never be made smaller
        /// again by dragging. RestoreBounds is the size it would return to, which
        /// is the one the player actually chose.
        /// </summary>
        private void RememberWindow()
        {
            try
            {
                Rectangle r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                if (r.Width <= 0 || r.Height <= 0) return;
                ServerStore.SaveWindow(r.X, r.Y, r.Width, r.Height,
                                       WindowState == FormWindowState.Maximized);
            }
            catch { }
        }
    }

    /// <summary>Dark colours for the context menu; see BuildServerMenu.</summary>
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

    /// <summary>A three-way segmented button, as the official filter panel uses.</summary>
    internal sealed class Segmented
    {
        private readonly Button[] _buttons;
        private int _index;

        public Segmented(string[] labels, Point at)
        {
            _buttons = new Button[labels.Length];
            int x = at.X;
            for (int i = 0; i < labels.Length; i++)
            {
                int captured = i;
                var b = new Button
                {
                    Text = labels[i],
                    Bounds = new Rectangle(x, at.Y, 74, 24),
                    FlatStyle = FlatStyle.Flat,
                    ForeColor = Color.White,
                    Font = new Font("Segoe UI", 8f)
                };
                b.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 76);
                b.Click += (s, e) => Index = captured;
                _buttons[i] = b;
                x += 76;
            }
            Index = 0;
        }

        /// <summary>Raised when the selection changes, so filters can re-apply.</summary>
        public event Action Changed;

        public int Index
        {
            get { return _index; }
            set
            {
                bool changed = _index != value;
                _index = value;
                for (int i = 0; i < _buttons.Length; i++)
                    _buttons[i].BackColor = i == _index
                        ? Color.FromArgb(92, 92, 98)
                        : Color.FromArgb(45, 45, 50);

                if (changed && Changed != null) Changed();
            }
        }

        public void AddTo(Control parent)
        {
            foreach (var b in _buttons) parent.Controls.Add(b);
        }

        /// <summary>The same tip on every segment, so it shows wherever the
        /// pointer lands on the control rather than only on one button.</summary>
        public void SetToolTip(ToolTip tip, string text)
        {
            foreach (var b in _buttons) tip.SetToolTip(b, text);
        }
    }

    /// <summary>A one-line text prompt; WinForms has no built-in equivalent.</summary>
    internal static class Prompt
    {
        public static string Show(IWin32Window owner, string title, string label)
        {
            using (var f = new Form())
            {
                f.Text = title;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.MinimizeBox = f.MaximizeBox = false;
                f.ClientSize = new Size(420, 120);
                f.BackColor = Color.FromArgb(28, 28, 30);
                f.ForeColor = Color.Gainsboro;
                f.Font = new Font("Segoe UI", 9f);

                f.Controls.Add(new Label { Text = label, Bounds = new Rectangle(14, 14, 392, 20) });
                var box = new TextBox
                {
                    Bounds = new Rectangle(14, 40, 392, 24),
                    BackColor = Color.FromArgb(45, 45, 48),
                    ForeColor = Color.Gainsboro,
                    BorderStyle = BorderStyle.FixedSingle
                };
                f.Controls.Add(box);

                var ok = new Button
                {
                    Text = "Add",
                    Bounds = new Rectangle(226, 78, 84, 28),
                    DialogResult = DialogResult.OK,
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(70, 120, 70),
                    ForeColor = Color.White
                };
                var cancel = new Button
                {
                    Text = "Cancel",
                    Bounds = new Rectangle(320, 78, 86, 28),
                    DialogResult = DialogResult.Cancel,
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(60, 60, 64),
                    ForeColor = Color.White
                };
                f.Controls.Add(ok);
                f.Controls.Add(cancel);
                f.AcceptButton = ok;
                f.CancelButton = cancel;

                return f.ShowDialog(owner) == DialogResult.OK ? box.Text.Trim() : null;
            }
        }
    }
}
