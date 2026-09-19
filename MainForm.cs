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
using System.Text;
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
        private readonly System.Windows.Forms.Timer _resortTimer = new System.Windows.Forms.Timer();
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

        private Row SelectedRow
        {
            get
            {
                if (_list.SelectedIndices.Count == 0) return null;
                int idx = _list.SelectedIndices[0];
                if (idx >= 0 && idx < _rows.Count) return _rows[idx];
                return null;
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

            _pollTimer.Interval = InternetPollMs;
            _pollTimer.Tick += OnPoll;

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
                Text = "Ã¢â„¢Â¥  Donate",
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

            var top = new Panel { Dock = DockStyle.Top, Height = 70, BackColor = Ink };
            main.Controls.Add(top);

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

            _searchClear = new Label
            {
                Text = "Ã¢Å“â€¢",
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
                var r = SelectedRow;
                _selectedEndpoint = r != null ? r.Endpoint : null;
                ShowMods();
            };
            _list.Columns.Add("", 26, HorizontalAlignment.Center);
            _list.Columns.Add("", 26, HorizontalAlignment.Center);
            _list.Columns.Add("Name", 244);
            _list.Columns.Add("Game", 84);
            _list.Columns.Add("Status", 62);
            _list.Columns.Add("Map", 106);
            _list.Columns.Add("Players", 70, HorizontalAlignment.Center);
            _list.Columns.Add("Time", 54, HorizontalAlignment.Center);
            _list.Columns.Add("Ping", 56, HorizontalAlignment.Center);
            _list.Columns.Add("Mods", 48, HorizontalAlignment.Center);
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
                case Tab.Favourites: return "FAVOURITES";
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

        private void OnColumnClick(object sender, ColumnClickEventArgs e)
        {
            if (e.Column == ColRefresh || e.Column == ColStar) return;

            if (_sortColumn == e.Column) _sortAscending = !_sortAscending;
            else { _sortColumn = e.Column; _sortAscending = true; }

            MarkSortedColumn();
            if (IsSteamTab(_tab)) RenderFromCache();
            else RefreshMine();
        }

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
            it.SubItems[ColGame].ForeColor =
                row.AppId == A2S.ExperimentalAppId ? Color.FromArgb(255, 170, 80)
                : row.AppId == A2S.StableAppId ? Color.FromArgb(110, 220, 140)
                : body;
            it.SubItems[ColStatus].ForeColor =
                !row.Online.HasValue ? Color.FromArgb(120, 120, 128)
                : row.Online.Value ? StatusOn : StatusOff;

            it.BackColor = band;
            for (int i = 0; i < it.SubItems.Count; i++) it.SubItems[i].BackColor = band;
            return it;
        }

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
            catch { }
            finally { _list.EndUpdate(); }

            _lastTopIndex = -1;
            _visTimer.Start();
            SweepAll(rows);
            _list.Invalidate();
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

            _appQueue = new Queue<uint>(AppsForQuery);
            _merged.Clear();
            _mergedKeys.Clear();

            foreach (var srv in _cache)
                if (_mergedKeys.Add(srv.Endpoint)) _merged.Add(srv);

            uint app = _appQueue.Dequeue();

            var steamFilters = kind == ListKind.Internet
                             ? _filters.ToSteamFilters()
                             : new List<KeyValuePair<string, string>>();
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

        private Queue<uint> _appQueue = new Queue<uint>();
        private readonly List<BrowserServer> _merged = new List<BrowserServer>();
        private readonly HashSet<string> _mergedKeys = new HashSet<string>();
        private uint _pollApp;

        private static string BuildLabel(uint app)
        {
            return app == (uint)A2S.ExperimentalAppId ? "Experimental" : "stable";
        }

        private static readonly TimeSpan CacheFreshFor = TimeSpan.FromMinutes(1);

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

            if (raw != _lastRawCount || done)
            {
                _lastRawCount = raw;
                _lastShownCount = RenderFromCache();
            }

            UpdateStatus(done ? null : "(loading from Steam...)");

            if (!done) return;

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
                _lastSteamFilterKey = SteamFilterKey(nextFilters);

                if (SteamServerList.Start(_pollKind, next, nextFilters))
                {
                    _pollApp = next;
                    _pollTicks = 0;
                    _stableTicks = 0;
                    _lastRawCount = -1;
                    return;
                }

                Log("  Steam refused the " + BuildLabel(next) + " query; showing what we have.");
            }

            _pollTimer.Stop();
            Log("Master list cached: " + combined.Count
                + " servers. Searching now filters this list instantly.");

            ServerStore.SaveList(CacheKey, combined, _lastSeen);
            if (raw >= 10000)
                UpdateStatus("Steam caps this at 10,000 - narrow it with FILTERS then REFRESH.");
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
                        Name = srv.Name,
                        Host = srv.Host,
                        Port = srv.Port,
                        Reason = reason
                    });

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

                ServerInfo live;
                if (_live.TryGetValue(srv.Endpoint, out live)) Apply(row, live);
                rows.Add(row);
            }

            SortRows(rows);
            SetRows(rows, _selectedEndpoint);
            return rows.Count;
        }

        private void RecomputeFarms(List<BrowserServer> cache)
        {
            _farmIps.Clear();
            _farmSubnets.Clear();
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

            foreach (string ok in _allowed)
            {
                _farmIps.Remove(ok);
                _farmSubnets.Remove(ok);
            }
        }

        private string FakeReason(BrowserServer s)
        {
            if (s == null) return null;

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
                    foreach (var candidate in _pingWanted)
                    {
                        if (_pingBusy.Contains(candidate.Endpoint)) continue;
                        if (_asked.Contains(candidate.Endpoint)) continue;
                        row = candidate;
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

                ServerInfo info;
                try { info = A2S.GetInfoAt(row.Host, row.EffectiveQueryPort, 1200); }
                catch { info = new ServerInfo { Error = "query failed" }; }

                lock (_pingLock) { _pingBusy.Remove(row.Endpoint); }

                var captured = row;
                var result = info;
                try { BeginInvoke((Action)(() => ApplyLive(captured, result))); }
                catch (InvalidOperationException) { return; }
            }
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

        private void SweepAll(List<Row> rows)
        {
            lock (_pingLock)
            {
                _pingAll = rows;
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
                _resortTimer.Stop();
                _resortTimer.Start();
            }

            if (!IsSteamTab(_tab)) FinishMine();
        }

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

            // The top search box normally searches server names, but pasted
            // endpoints are address filters so Steam can be asked for that exact
            // game address instead of searching a capped local cache.
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

            _filters.Map = _fMap?.Text ?? "";
            _filters.Search = _search?.Text ?? "";
            int ping;
            _filters.MaxPing = int.TryParse(_fPing?.Text, out ping) ? ping : 0;
            _filters.PlayersMode = (PlayersMode)(_cbPlayers?.SelectedIndex ?? 0);
            _filters.TimeMode = (TimeMode)(_cbTime?.SelectedIndex ?? 0);
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
            if (_fPing != null) _fPing.Text = "";
            if (_cbPlayers != null) _cbPlayers.SelectedIndex = 0;
            if (_cbTime != null) _cbTime.SelectedIndex = 0;
            if (_segThird != null) _segThird.SelectedIndex = 0;
            if (_segMods != null) _segMods.SelectedIndex = 0;
            if (_chkNoPass != null) _chkNoPass.Checked = false;
            if (_chkHideFull != null) _chkHideFull.Checked = false;
            if (_chkHideEmpty != null) _chkHideEmpty.Checked = false;
            if (_chkHideFakes != null) _chkHideFakes.Checked = true;
        }

        private void QueueFilter()
        {
            _typeTimer.Stop();
            _typeTimer.Start();
        }

        private void ApplyFilters()
        {
            ReadFilterUi();
            if (IsSteamTab(_tab))
            {
                var nextFilters = KindFor(_tab) == ListKind.Internet
                                ? _filters.ToSteamFilters()
                                : new List<KeyValuePair<string, string>>();
                string nextKey = SteamFilterKey(nextFilters);
                if (nextKey != _lastSteamFilterKey)
                {
                    RefreshCurrent(true);
                    return;
                }

                RenderFromCache();
            }
            else RefreshMine();
        }

        private static string SteamFilterKey(IEnumerable<KeyValuePair<string, string>> filters)
        {
            return string.Join("\u001F", (filters ?? new KeyValuePair<string, string>[0])
                .Select(f => f.Key + "=" + f.Value).ToArray());
        }

        // ---------------------------------------------- connection & launch ----
        private void OnConnect(object sender, EventArgs e)
        {
            var r = SelectedRow;
            if (r == null) return;
            LaunchGame(r);
        }

        private void OnDirectConnect(object sender, EventArgs e)
        {
            string input = PromptInput("Enter IP:Port or Hostname:", "Direct Connect");
            if (string.IsNullOrWhiteSpace(input)) return;
            input = input.Trim();
            int c = input.LastIndexOf(':');
            string host = c > 0 ? input.Substring(0, c) : input;
            int port = 2302;
            if (c > 0) int.TryParse(input.Substring(c + 1), out port);

            var r = new Row { Host = host, Port = port, Name = host + ":" + port };
            LaunchGame(r);
        }

        private static string PromptInput(string prompt, string title)
        {
            using (var input = new TextBox
            {
                BorderStyle = BorderStyle.FixedSingle,
                Width = 220,
                Text = ""
            })
            {
                var dlg = new Form
                {
                    Text = title,
                    Width = 300,
                    Height = 160,
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    StartPosition = FormStartPosition.CenterParent,
                    ShowInTaskbar = false,
                    MinimizeBox = false,
                    MaximizeBox = false
                };
                var lbl = new Label
                {
                    Text = prompt,
                    AutoSize = true,
                    Location = new Point(12, 16)
                };
                input.Location = new Point(12, 40);
                var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(120, 76), Width = 70 };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(200, 76), Width = 70 };
                dlg.Controls.Add(lbl);
                dlg.Controls.Add(input);
                dlg.Controls.Add(ok);
                dlg.Controls.Add(cancel);
                dlg.AcceptButton = ok;
                dlg.CancelButton = cancel;
                if (dlg.ShowDialog() == DialogResult.OK) return input.Text;
                return "";
            }
        }

        private void LaunchGame(Row row)
        {
            if (row == null) return;
            string playerName = _name.Text.Trim();
            if (!string.IsNullOrEmpty(playerName)) ServerStore.SaveName(playerName);

            string steam = FindSteam();
            if (string.IsNullOrEmpty(steam))
            {
                MessageBox.Show("Steam installation directory could not be located.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            uint appId = row.AppId != 0 ? (uint)row.AppId : (uint)A2S.StableAppId;
            string gameDir = FindGameDir(steam, appId);
            if (string.IsNullOrEmpty(gameDir))
            {
                MessageBox.Show("DayZ directory not found for AppID " + appId + ".", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            string bePath = Path.Combine(gameDir, BeExe);
            if (!File.Exists(bePath))
            {
                MessageBox.Show("BattlEye launcher (" + BeExe + ") not found in:\n" + gameDir, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            string args = string.Format("{0} -connect={1} -port={2}", BeArgs, row.Host, row.Port);
            if (!string.IsNullOrEmpty(playerName)) args += " -name=\"" + playerName + "\"";

            Log("Launching: " + bePath + " " + args);
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = bePath,
                    Arguments = args,
                    WorkingDirectory = gameDir,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to start game:\n" + ex.Message, "Launch Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ------------------------------------------------ favourites & mods ----
        private void OnToggleFavourite(object sender, EventArgs e)
        {
            ToggleFavourite(SelectedRow);
        }

        private void ToggleFavourite(Row row)
        {
            if (row == null) return;

            if (_favourites.Contains(row.Endpoint))
            {
                _favourites.Remove(row.Endpoint);
                ServerStore.SaveFavourites(_favourites);
                UpdateFavButton();

                if (_tab == Tab.Favourites) { RefreshMine(); return; }
            }
            else
            {
                _favourites.Add(row.Endpoint);
                ServerStore.RememberName(row.Endpoint, row.Name);
                ServerStore.SaveFavourites(_favourites);
                UpdateFavButton();
            }

            if (IsSteamTab(_tab)) { RenderFromCache(); return; }
            Redraw(row.Endpoint);
        }

        private void UpdateFavButton()
        {
            var row = SelectedRow;
            _favBtn.Text = row != null && _favourites.Contains(row.Endpoint)
                         ? "REMOVE FAVOURITE" : "ADD TO FAVOURITES";
        }

        private void ShowMods(bool force = false)
        {
            UpdateFavButton();
            var row = SelectedRow;
            if (row == null) return;

            var endpoint = row.Endpoint;
            ServerRules cached;
            lock (_modLock)
            {
                if (!force && _modCache.TryGetValue(endpoint, out cached))
                {
                    FillMods(row, cached, FindSteam());
                    return;
                }
                if (_modLoading.Contains(endpoint)) return;
                _modLoading.Add(endpoint);
            }

            _mods.Items.Clear();
            _modsHeader.Text = "Content required by server  -  " + endpoint;
            _mods.Items.Add(new ListViewItem(new[] { "querying server...", "", "" }));

            _desc.Text = "";
            _descHeader.Text = "  Description";

            var captured = row;
            new Thread(() =>
            {
                ServerRules rules = null;
                try { rules = QueryModsChecked(captured); }
                catch (Exception ex)
                {
                    Log("Mod query failed for " + captured.Endpoint + ": " + ex.Message);
                    rules = null;
                }

                string steam = FindSteam();
                if (rules != null && rules.Mods.Count > 0)
                {
                    try { SteamWorkshop.CorrectInstalledIds(steam, rules.Mods); }
                    catch { }
                    try { SteamWorkshop.PrefetchWorkshopTimes(rules.Mods.Select(m => m.WorkshopId), null); }
                    catch { }
                }

                lock (_modLock)
                {
                    _modLoading.Remove(captured.Endpoint);
                    if (rules != null)
                        _modCache[captured.Endpoint] = rules;
                    else
                        _modCache.Remove(captured.Endpoint);
                }

                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        if (SelectedRow == null || SelectedRow.Endpoint != captured.Endpoint)
                            return;
                        FillMods(captured, rules, steam);
                    }));
                }
                catch (InvalidOperationException) { }
            })
            { IsBackground = true }.Start();
        }

        private void FillMods(Row row, ServerRules rules, string steam)
        {
            var sel = SelectedRow;
            if (sel == null || sel.Endpoint != row.Endpoint) return;
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

        private static string Age(TimeSpan t)
        {
            if (t.TotalDays >= 365) return string.Format("{0:F1} years", t.TotalDays / 365.0);
            if (t.TotalDays >= 1) return string.Format("{0:F0} days", t.TotalDays);
            if (t.TotalHours >= 1) return string.Format("{0:F0} hours", t.TotalHours);
            return string.Format("{0:F0} minutes", t.TotalMinutes);
        }

        private ListViewItem MakeModRow(Mod m, string steam)
        {
            bool have = steam != null && SteamWorkshop.IsInstalled(steam, m.WorkshopId);
            bool broken = !have && steam != null && SteamWorkshop.IsBrokenInstall(steam, m.WorkshopId);
            bool stale = have && SteamWorkshop.NeedsUpdate(steam, m.WorkshopId);

            string status;
            Color colour;
            if (broken) { status = "BROKEN - files present but no meta.cpp"; colour = Color.FromArgb(230, 130, 130); }
            else if (!have) { status = "MISSING - will be downloaded"; colour = Color.FromArgb(220, 190, 120); }
            else if (stale)
            {
                TimeSpan behind = steam == null ? TimeSpan.Zero : SteamWorkshop.StaleBy(steam, m.WorkshopId);
                status = behind > TimeSpan.Zero ? "OUT OF DATE by " + Age(behind) + " - will update" : "OUT OF DATE - will update";
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
            it.SubItems[MColRemove].ForeColor = have ? Color.FromArgb(190, 130, 130) : Color.FromArgb(90, 90, 96);
            it.SubItems[MColInfo].ForeColor = link;
            return it;
        }

        private void OnModClick(object sender, MouseEventArgs e)
        {
            var hit = _mods.HitTest(e.Location);
            if (hit.Item == null || hit.SubItem == null) return;

            var mod = hit.Item.Tag as Mod;
            if (mod == null) return;
            int col = hit.Item.SubItems.IndexOf(hit.SubItem);

            if (col == MColInfo)
            {
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

            string text = (rules.Description ?? "").Replace("\r\n", "\n")
                                                   .Replace("\r", "\n")
                                                   .Replace("\n", Environment.NewLine)
                                                   .Trim();
            _desc.Text = text.Length > 0 ? text : "(this server publishes no description)";
        }

        private static string Accel(string tags, string prefix)
        {
            if (string.IsNullOrEmpty(tags)) return "";
            foreach (var raw in tags.Split(','))
            {
                string t = raw.Trim();
                if (!t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

                string rest = t.Substring(prefix.Length);
                double v;
                if (!double.TryParse(rest, System.Globalization.NumberStyles.Float,
                                     System.Globalization.CultureInfo.InvariantCulture, out v))
                    continue;
                return v.ToString("0.##") + "x";
            }
            return "";
        }

        // ---------------------------------------------------- system helpers ----
        private static void OpenLink(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch { }
        }

        private static Image LoadImage(string name)
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", name);
                if (File.Exists(path)) return Image.FromFile(path);
            }
            catch { }
            return null;
        }

        private void Log(string msg)
        {
            if (InvokeRequired)
            {
                BeginInvoke((Action)(() => Log(msg)));
                return;
            }
            _log.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg + Environment.NewLine);
        }

        private void RestoreWindow()
        {
            var bounds = ServerStore.LoadWindowBounds();
            if (bounds.HasValue && bounds.Value.Width > 200 && bounds.Value.Height > 200)
            {
                StartPosition = FormStartPosition.Manual;
                Bounds = bounds.Value;
            }
        }

        private void RememberWindow()
        {
            if (WindowState == FormWindowState.Normal)
            {
                ServerStore.SaveWindowBounds(Bounds);
            }
        }

        private static string FindSteam()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    return key?.GetValue("SteamPath") as string;
                }
            }
            catch { return null; }
        }

        private static string FindGameDir(string steamPath, ulong appId)
        {
            if (string.IsNullOrEmpty(steamPath)) return null;

            string[] folders = appId == A2S.ExperimentalAppId ? ExpFolders : StableFolders;
            string defaultCommon = Path.Combine(steamPath, "steamapps", "common");

            foreach (var folder in folders)
            {
                string candidate = Path.Combine(defaultCommon, folder);
                if (Directory.Exists(candidate)) return candidate;
            }
            return null;
        }

        private ContextMenuStrip BuildServerMenu()
        {
            var menu = new ContextMenuStrip
            {
                BackColor = Panel2,
                ForeColor = Color.Gainsboro,
                ShowImageMargin = false
            };

            menu.Renderer = new ToolStripProfessionalRenderer(new DarkMenu());

            var copyAll = new ToolStripMenuItem("Copy server info");
            copyAll.Click += (s, e) =>
            {
                var r = SelectedRow;
                if (r != null) Clipboard.SetText(r.Name + " - " + r.Endpoint);
            };
            menu.Items.Add(copyAll);

            var copyIp = new ToolStripMenuItem("Copy IP:Port");
            copyIp.Click += (s, e) =>
            {
                var r = SelectedRow;
                if (r != null) Clipboard.SetText(r.Endpoint);
            };
            menu.Items.Add(copyIp);

            menu.Items.Add(new ToolStripSeparator());

            var fav = new ToolStripMenuItem("Toggle Favourite");
            fav.Click += OnToggleFavourite;
            menu.Items.Add(fav);

            var connect = new ToolStripMenuItem("Connect");
            connect.Click += OnConnect;
            menu.Items.Add(connect);

            return menu;
        }
    }
}