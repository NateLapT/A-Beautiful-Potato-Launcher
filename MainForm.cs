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
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BeautifulPotatoExpLauncher
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
                          ColPing = 8, ColMods = 9, ColPassword = 10, ColAddress = 11;

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
        private const int MaxRealisticSlots = 120;

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

            // Every clickable thing gets the hand cursor; see UiCursors.
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

            var top = new Panel { Dock = DockStyle.Top, Height = 70, BackColor = Ink };
            main.Controls.Add(top);

            var searchRow = new Panel { Dock = DockStyle.Fill, BackColor = Ink };
            top.Controls.Add(searchRow);

            var tabBar = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = Ink };
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

            var resort = MakeBtn("RESORT LIST", new Rectangle(20, ry, 170, 26), Panel2);
            resort.Font = new Font("Segoe UI", 8f, FontStyle.Bold);
            resort.Click += (s, e) => ResortNow();
            rail.Controls.Add(resort);
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
                "Hides servers claiming more than " + MaxRealisticSlots +
                " slots (DayZ limits effective capacity to ~120), and addresses running " +
                BrowserFilters.FarmServersPerIp + "+ servers under " +
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
        private static bool Unconfigured(Row r)
        {
            if (r == null || string.IsNullOrWhiteSpace(r.Name)) return true;
            string n = r.Name.Trim();
            return n.Equals("EXAMPLE NAME", StringComparison.OrdinalIgnoreCase)
                || n.Equals("DayZ", StringComparison.OrdinalIgnoreCase)
                || n.Equals("nitrado.net gameserver", StringComparison.OrdinalIgnoreCase)
                || n.Equals("Server Name", StringComparison.OrdinalIgnoreCase);
        }

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
            foreach (var r in rows)
                if (!used.Contains(r.Endpoint)) ordered.Add(r);

            return ordered;
        }

        /// <summary>Re-sorts what is on screen using the last chosen column.</summary>
        private void ResortNow()
        {
            if (_rows == null || _rows.Count == 0) return;
            var rows = new List<Row>(_rows);
            SortRows(rows);
            SetRows(rows, _selectedEndpoint);
            _status.Text = string.Format("Re-sorted {0} servers.", rows.Count);
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
                    if (sel < rows.Count) _list.EnsureVisible(sel);
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
            _appendWhileLoading = true;
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
            _appendWhileLoading = false;
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
                    AppId = srv.AppId, Tags = srv.Tags, Password = srv.Password,
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

                if (s.MaxPlayers > MaxRealisticSlots)
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

                if (s.MaxPlayers > MaxRealisticSlots)
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
        }

        private string FakeReason(BrowserServer s)
        {
            if (s == null) return null;

            if (_favourites.Contains(s.Endpoint)) return null;

            if (_allowed.Contains(s.Host) ||
                _allowed.Contains(BrowserFilters.Subnet24(s.Host))) return null;

            if (s.MaxPlayers > MaxRealisticSlots)
                return "claims " + s.MaxPlayers + " slots (DayZ maximum realistic capacity is "
                     + MaxRealisticSlots + ")";

            if (_farmGroups.Contains(GroupKey(s.Host, s.Name)))
                return "address " + s.Host + " runs " + BrowserFilters.FarmSameNamePerIp
                     + "+ servers under this exact name";

            if (_farmIps.Contains(s.Host))
                return "address " + s.Host + " runs many servers under many names or identical mod arrays";

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
                if (rules != null) _modCache[row.Endpoint] = rules;
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
                    foreach (var mod in rules.Mods)
                        _mods.Items.Add(MakeModRow(mod, steamPath));
                }

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
                    "Repair", "Sub", "Remove", "Info"
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

                // Repair and Sub mean nothing without a workshop item.
                var off = Color.FromArgb(90, 90, 96);
                localRow.SubItems[MColRepair].ForeColor = off;
                localRow.SubItems[MColSub].ForeColor = off;
                localRow.SubItems[MColRemove].ForeColor = off;
                localRow.SubItems[MColInfo].ForeColor = Color.FromArgb(120, 150, 190);
                return localRow;
            }

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

        private static Image LoadImage(string file)
        {
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
            _resortTimer.Stop();

            RememberWindow();
            RememberAllSplits();

            SteamServerList.Stop();
            base.OnFormClosing(e);
        }
    }
}