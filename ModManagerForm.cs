// ---------------------------------------------------------------------------
//  The mod library window.
//
//  SPEED IS THE WHOLE POINT
//    The official launcher walks every byte of every mod before it draws
//    anything, which on a real library - 868 mods, 357 GB - takes minutes and
//    pounds the disk. This window shows the complete list in about a tenth of
//    a second, because it only reads what it needs to DRAW a row: the folder
//    names and one small meta.cpp each. See ModIndex for the timings.
//
//    The expensive fact - size on disk - is gathered afterwards on a background
//    thread and filled into rows that are already on screen. Nothing blocks.
//
//    The list is VIRTUAL for the same reason the server list is: building 868
//    ListViewItems up front is slow and pointless when twenty are visible.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace ABeautifulPotatoLauncher
{
    internal sealed class ModManagerForm : Form
    {
        private static readonly Color Ink = Color.FromArgb(24, 24, 27);
        private static readonly Color Panel = Color.FromArgb(28, 28, 30);
        private static readonly Color Panel2 = Color.FromArgb(38, 38, 42);
        private static readonly Color Dim = Color.FromArgb(150, 150, 158);
        private static readonly Color Good = Color.FromArgb(140, 200, 140);
        private static readonly Color Warn = Color.FromArgb(225, 175, 90);
        private static readonly Color Bad = Color.FromArgb(230, 130, 130);
        private static readonly Color Link = Color.FromArgb(120, 150, 190);

        private const int ColName = 0, ColId = 1, ColStatus = 2, ColUpdated = 3,
                          ColLoaded = 4, ColSize = 5, ColRepair = 6, ColRemove = 7;

        private readonly string _steam;
        private readonly string _gameDir;

        private Button _repairBad;
        private ListView _list;
        private TextBox _search;
        private Label _status, _detailTitle;

        /// <summary>Why the selected mod is in the state it is, wrapped.</summary>
        private Label _statusDetail;
        private Panel _detail;
        private Splitter _grip;
        private PictureBox _image;
        private ListView _facts;
        private System.Windows.Forms.Timer _typeTimer = new System.Windows.Forms.Timer();

        private List<ModEntry> _all = new List<ModEntry>();
        private List<ModEntry> _shown = new List<ModEntry>();
        private ModEntry _selected;

        private volatile bool _closing;
        private int _deepDone;

        public ModManagerForm(string steamPath, string gameDir)
        {
            _steam = steamPath;
            _gameDir = gameDir;

            Text = "Mod Manager";
            StartPosition = FormStartPosition.CenterParent;
            // Wide enough for every column PLUS the detail panel. The columns
            // below total 948px and the panel takes 330, so anything narrower
            // clips the Repair and Remove cells off the right-hand edge - which
            // is exactly what they are there to be clicked on.
            ClientSize = new Size(1330, 760);
            MinimumSize = new Size(1150, 600);
            BackColor = Ink;
            ForeColor = Color.Gainsboro;
            Font = new Font("Segoe UI", 9f);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            BuildUi();
            UiCursors.ApplyTo(this);

            _typeTimer.Interval = 200;
            _typeTimer.Tick += (s, e) => { _typeTimer.Stop(); ApplyFilter(); };

            // Escape closes the window. KeyPreview is what makes it work from
            // anywhere inside - without it the key only arrives when the form
            // itself has focus, which it never does once a list or search box
            // has taken it.
            KeyPreview = true;
            KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Escape) return;
                e.Handled = true;
                Close();
            };

            Shown += (s, e) => Start();
            FormClosing += (s, e) => { _closing = true; ModIndex.Save(); };
        }

        // ----------------------------------------------------------- layout --

        private void BuildUi()
        {
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 30, BackColor = Panel };
            Controls.Add(bottom);
            _status = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(10, 0, 0, 0),
                ForeColor = Good,
                Text = "Reading the mod library..."
            };
            bottom.Controls.Add(_status);

            var top = new Panel { Dock = DockStyle.Top, Height = 38, BackColor = Ink };
            Controls.Add(top);

            var searchLabel = new Label
            {
                Text = "Search",
                Bounds = new Rectangle(10, 10, 48, 20),
                ForeColor = Dim
            };
            top.Controls.Add(searchLabel);

            _search = new TextBox
            {
                Bounds = new Rectangle(62, 7, 320, 23),
                BackColor = Panel2,
                ForeColor = Color.Gainsboro,
                BorderStyle = BorderStyle.FixedSingle
            };
            _search.TextChanged += (s, e) => { _typeTimer.Stop(); _typeTimer.Start(); };
            top.Controls.Add(_search);
            ClearBox.AddTo(_search);

            var rescan = MakeButton("RESCAN", new Rectangle(394, 6, 92, 25), Panel2);
            rescan.Click += (s, e) => Rescan();
            top.Controls.Add(rescan);

            _repairBad = MakeButton("REPAIR CORRUPTED", new Rectangle(496, 6, 142, 25), Panel2);
            _repairBad.Click += (s, e) => RepairCorrupted();
            top.Controls.Add(_repairBad);

            var validate = MakeButton("VALIDATE ALL", new Rectangle(646, 6, 110, 25), Panel2);
            validate.Click += (s, e) => ValidateAll();
            top.Controls.Add(validate);

            var unsub = MakeButton("UNSUB ALL", new Rectangle(764, 6, 100, 25), Color.FromArgb(70, 55, 55));
            unsub.Click += (s, e) => UnsubAll();
            top.Controls.Add(unsub);

            var byId = MakeButton("INSTALL BY ID", new Rectangle(872, 6, 124, 25),
                                  Color.FromArgb(55, 70, 55));
            byId.Click += (s, e) => InstallById();
            top.Controls.Add(byId);

            _bulkButtons.Add(_repairBad);
            _bulkButtons.Add(validate);
            _bulkButtons.Add(unsub);
            _bulkButtons.Add(byId);

            var tips = new ToolTip { AutoPopDelay = 15000 };
            tips.SetToolTip(_repairBad, "Re-downloads every mod whose install never finished.");
            tips.SetToolTip(validate, "Asks Steam to re-check every installed mod. This takes a long time.");
            tips.SetToolTip(unsub, "Unsubscribes from EVERY DayZ mod and deletes them from disk.");
            tips.SetToolTip(byId, "Installs a workshop item by its id, without hunting for it in Steam.");

            // ---- detail panel on the right ----
            _detail = new Panel { Dock = DockStyle.Right, Width = 330, BackColor = Panel, Padding = new Padding(10) };
            Controls.Add(_detail);

            // A drag handle down the LEFT edge of the detail panel - the join
            // between the mod list and the panel.
            //
            // The two facts worth reading here are paths, the mod's folder and
            // the workshop folder it links to, and at a fixed 330px both were
            // cut off with no way to see the rest.
            //
            // DOCK ORDER MATTERS, and getting it wrong is silent. WinForms docks
            // in reverse z-order: the HIGHEST child index is placed first and
            // ends up outermost. Added normally, the splitter took a higher
            // index than the panel and docked outside it - a dead strip against
            // the window edge with nothing to drag. It is pushed to a lower
            // index than the panel below, which puts it inside, between the
            // panel and the list, where it belongs.
            _grip = new Splitter
            {
                Dock = DockStyle.Right,
                Width = 7,
                // Visibly a handle rather than a gap, so it can be found.
                BackColor = Color.FromArgb(70, 70, 78),
                MinExtra = 360,       // the list never shrinks below this
                MinSize = 260,        // nor the detail panel below this
                Cursor = Cursors.VSplit
            };
            Controls.Add(_grip);

            _image = new PictureBox
            {
                Bounds = new Rectangle(10, 10, 310, 150),
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Panel2,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _detail.Controls.Add(_image);

            _detailTitle = new Label
            {
                Bounds = new Rectangle(10, 168, 310, 40),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                UseMnemonic = false,          // a "&" in a mod name is not a shortcut
                Text = "Select a mod",
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _detail.Controls.Add(_detailTitle);

            _facts = new ListView
            {
                Bounds = new Rectangle(10, 212, 310, 330),
                View = View.Details,
                HeaderStyle = ColumnHeaderStyle.None,
                FullRowSelect = true,
                BackColor = Panel,
                ForeColor = Color.Gainsboro,
                BorderStyle = BorderStyle.FixedSingle,
                Anchor = AnchorStyles.Top | AnchorStyles.Left
                       | AnchorStyles.Right | AnchorStyles.Bottom
            };
            _facts.Columns.Add("", 96);
            _facts.Columns.Add("", 208);
            ListViewTweaks.Smooth(_facts);
            _detail.Controls.Add(_facts);

            // The value column takes whatever width the panel is given, which
            // is the entire point of being able to drag it wider.
            _detail.Resize += (s, e) => FitFactColumns();
            _facts.Resize += (s, e) => FitFactColumns();

            // The explanation of the status, under the facts and above the
            // buttons. AutoSize with a maximum width is what makes it wrap:
            // without the cap it grows sideways forever instead of downwards.
            _statusDetail = new Label
            {
                Bounds = new Rectangle(10, 548, 310, 60),
                MaximumSize = new Size(310, 0),
                AutoSize = true,
                ForeColor = Color.FromArgb(150, 150, 158),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };
            _detail.Controls.Add(_statusDetail);

            _detail.Resize += (s, e) =>
            {
                int room = Math.Max(120, _detail.ClientSize.Width - 20);
                _statusDetail.MaximumSize = new Size(room, 0);
            };

            int by = 556;
            var repair = MakeButton("Repair", new Rectangle(10, by, 74, 27), Panel2);
            repair.Click += (s, e) => RepairSelected();
            _detail.Controls.Add(repair);

            var remove = MakeButton("Remove", new Rectangle(90, by, 74, 27), Color.FromArgb(70, 55, 55));
            remove.Click += (s, e) => RemoveSelected();
            _detail.Controls.Add(remove);

            var workshop = MakeButton("Workshop", new Rectangle(170, by, 80, 27), Panel2);
            workshop.Click += (s, e) => OpenWorkshop();
            _detail.Controls.Add(workshop);

            var explorer = MakeButton("Explorer", new Rectangle(256, by, 64, 27), Panel2);
            explorer.Click += (s, e) => OpenFolder();
            _detail.Controls.Add(explorer);

            foreach (Control c in new Control[] { repair, remove, workshop, explorer })
                c.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;

            // ---- the list ----
            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                VirtualMode = true,
                BackColor = Panel,
                ForeColor = Color.Gainsboro,
                BorderStyle = BorderStyle.FixedSingle,
                HideSelection = false,
                ShowItemToolTips = true
            };
            _list.Columns.Add("Mod Name", 300);
            _list.Columns.Add("Workshop ID", 100);
            _list.Columns.Add("Status", 100);
            _list.Columns.Add("Last Update", 120);
            _list.Columns.Add("Last Loaded", 120);
            _list.Columns.Add("Size", 84);
            _list.Columns.Add("", 62, HorizontalAlignment.Center);
            _list.Columns.Add("", 62, HorizontalAlignment.Center);
            _list.RetrieveVirtualItem += OnRetrieveItem;
            _list.SelectedIndexChanged += (s, e) => ShowDetail();
            _list.MouseClick += OnListClick;
            _list.ColumnClick += OnColumnClick;
            ListViewTweaks.Smooth(_list);

            // Repair and Remove are cells the player clicks, so say so. A local
            // mod has neither - Steam did not install it and cannot touch it -
            // and those cells are left blank, which HandOverColumns skips.
            UiCursors.HandOverColumns(_list, (item, col) => RowAt(item.Index) != null,
                                      ColRepair, ColRemove);
            Controls.Add(_list);

            // Innermost first: the list fills what is left, the grip sits just
            // inside the detail panel, and the panel holds the right edge.
            Controls.SetChildIndex(_list, 0);
            Controls.SetChildIndex(_grip, 1);

            FitFactColumns();
        }

        private Button MakeButton(string text, Rectangle bounds, Color back)
        {
            var b = new Button
            {
                Text = text,
                Bounds = bounds,
                FlatStyle = FlatStyle.Flat,
                BackColor = back,
                ForeColor = Color.White
            };
            b.FlatAppearance.BorderColor = Color.FromArgb(75, 75, 82);
            return b;
        }

        // ------------------------------------------------------- the scans --

        private void Start()
        {
            var sw = Stopwatch.StartNew();
            ModIndex.Load();
            _all = ModIndex.FastScan(_steam);
            SortAll();
            ApplyFilter();

            _status.Text = string.Format("{0} mods listed in {1} ms - measuring sizes in the background...",
                                         _all.Count, sw.ElapsedMilliseconds);

            // Anything never deep-scanned, or whose folder changed, needs the
            // expensive pass. Everything else keeps what the index already has.
            var todo = _all.Where(m => m.SizeBytes < 0).ToList();
            if (todo.Count == 0)
            {
                _status.Text = string.Format("{0} mods - all details already indexed.", _all.Count);
                return;
            }

            _deepDone = 0;
            new Thread(() => DeepPass(todo)) { IsBackground = true }.Start();
        }

        /// <summary>
        /// The expensive pass, off the UI thread. Rows already on screen simply
        /// gain their size as it is measured; nothing is rebuilt.
        /// </summary>
        private void DeepPass(List<ModEntry> todo)
        {
            foreach (var m in todo)
            {
                if (_closing) return;
                ModIndex.DeepScan(m);
                int n = Interlocked.Increment(ref _deepDone);

                // Repainting on every single mod would cost more than the scan.
                if (n % 25 != 0 && n != todo.Count) continue;
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        _status.Text = string.Format("Measuring sizes... {0} of {1}", n, todo.Count);
                        _list.Invalidate();
                    }));
                }
                catch { return; }
            }

            try
            {
                BeginInvoke((Action)(() =>
                {
                    ModIndex.Save();
                    long total = _all.Sum(m => Math.Max(0, m.SizeBytes));
                    _status.Text = string.Format("{0} mods, {1:N1} GB on disk.",
                                                 _all.Count, total / 1073741824.0);
                    _list.Invalidate();
                    ShowDetail();
                }));
            }
            catch { }
        }

        private void Rescan()
        {
            foreach (var m in _all) m.SizeBytes = -1;
            ModIndex.Save();
            Start();
        }

        // ------------------------------------------------------ list plumbing --

        private int _sortColumn = ColName;
        private bool _sortAsc = true;

        private void SortAll()
        {
            Comparison<ModEntry> cmp;
            switch (_sortColumn)
            {
                case ColId: cmp = (a, b) => a.WorkshopId.CompareTo(b.WorkshopId); break;
                case ColStatus: cmp = (a, b) => string.Compare(a.Status(_steam), b.Status(_steam), StringComparison.OrdinalIgnoreCase); break;
                case ColUpdated: cmp = (a, b) => a.InstalledAt.CompareTo(b.InstalledAt); break;
                case ColLoaded: cmp = (a, b) => a.LastLoaded.CompareTo(b.LastLoaded); break;
                case ColSize: cmp = (a, b) => a.SizeBytes.CompareTo(b.SizeBytes); break;
                default: cmp = (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); break;
            }
            _all.Sort((x, y) =>
            {
                int c = cmp(x, y);
                if (!_sortAsc) c = -c;
                return c != 0 ? c : string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
            });
        }

        private void OnColumnClick(object sender, ColumnClickEventArgs e)
        {
            if (e.Column == ColRepair || e.Column == ColRemove) return;
            if (e.Column == _sortColumn) _sortAsc = !_sortAsc;
            else { _sortColumn = e.Column; _sortAsc = true; }
            SortAll();
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            string q = _search.Text.Trim();
            _shown = q.Length == 0
                ? new List<ModEntry>(_all)
                : _all.Where(m =>
                        m.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                     || m.WorkshopId.ToString().IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                     || (m.Author ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();

            _list.BeginUpdate();
            try
            {
                _list.SelectedIndices.Clear();
                _list.VirtualListSize = _shown.Count;
                if (_shown.Count > 0) _list.SelectedIndices.Add(0);
            }
            catch { }
            finally { _list.EndUpdate(); }
            _list.Invalidate();

            if (q.Length > 0)
                _status.Text = string.Format("{0} of {1} mods match \"{2}\".", _shown.Count, _all.Count, q);
        }

        private void OnRetrieveItem(object sender, RetrieveVirtualItemEventArgs e)
        {
            if (e.ItemIndex < 0 || e.ItemIndex >= _shown.Count)
            {
                e.Item = new ListViewItem("");
                return;
            }
            var m = _shown[e.ItemIndex];
            string status = m.Status(_steam);

            var it = new ListViewItem(new[]
            {
                m.Name,
                m.IdText,
                status,
                m.InstalledAt == DateTime.MinValue ? "" : m.InstalledAt.ToLocalTime().ToString("yyyy-MM-dd"),
                m.LastLoaded == DateTime.MinValue ? "never" : m.LastLoaded.ToLocalTime().ToString("yyyy-MM-dd"),
                m.SizeText,
                // Steam owns neither of these actions for a hand-installed mod:
                // there is nothing to re-download and no subscription to drop.
                m.IsLocal ? "" : "Repair",
                m.IsLocal ? "" : "Remove"
            })
            { UseItemStyleForSubItems = false };

            // Unpacked is its own colour: it is not an error, but it is not the
            // ordinary case either, and the owner should be able to pick those
            // folders out at a glance.
            Color c = status == "Corrupt" ? Bad
                    : status == "Needs update" ? Warn
                    : status == "Unpacked" ? Color.FromArgb(120, 170, 220)
                    : Good;
            it.SubItems[ColName].ForeColor = c;
            it.SubItems[ColStatus].ForeColor = c;
            it.SubItems[ColId].ForeColor = Dim;
            it.SubItems[ColUpdated].ForeColor = Dim;
            it.SubItems[ColLoaded].ForeColor = m.LastLoaded == DateTime.MinValue ? Dim : Color.Gainsboro;
            it.SubItems[ColSize].ForeColor = Dim;
            it.SubItems[ColRepair].ForeColor = Link;
            it.SubItems[ColRemove].ForeColor = Bad;

            // Hovering a row says why it is in that state. The list already has
            // ShowItemToolTips on for this.
            it.ToolTipText = m.StatusDetail(_steam);

            e.Item = it;
        }

        /// <summary>
        /// The mod drawn at a row, or null when that row offers no Steam
        /// action. Used by the cursor tracking, which has only the item.
        /// </summary>
        private ModEntry RowAt(int index)
        {
            if (index < 0 || index >= _shown.Count) return null;
            var m = _shown[index];
            return m.IsLocal ? null : m;
        }

        private void OnListClick(object sender, MouseEventArgs e)
        {
            var hit = _list.HitTest(e.Location);
            if (hit.Item == null || hit.SubItem == null) return;
            int col = hit.Item.SubItems.IndexOf(hit.SubItem);
            if (hit.Item.Index < 0 || hit.Item.Index >= _shown.Count) return;

            _selected = _shown[hit.Item.Index];

            // A local mod has no workshop id, so asking Steam to repair or
            // remove it would act on item 0 - which is nothing at all.
            if (_selected.IsLocal) return;

            if (col == ColRepair) RepairSelected();
            else if (col == ColRemove) RemoveSelected();
        }

        // ---------------------------------------------------- the detail pane --

        private void ShowDetail()
        {
            var sel = _list.SelectedIndices.Count > 0 ? _list.SelectedIndices[0] : -1;
            _selected = sel >= 0 && sel < _shown.Count ? _shown[sel] : null;

            _facts.Items.Clear();
            if (_statusDetail != null) _statusDetail.Text = "";

            if (_selected == null)
            {
                _detailTitle.Text = "Select a mod";
                _image.Image = null;
                return;
            }

            var m = _selected;
            _detailTitle.Text = m.Name;

            // Author and version come from mod.cpp, which only the deep pass
            // reads - so fill them in now if this mod has not had one yet.
            if (m.DeepScannedAt == DateTime.MinValue) ModIndex.DeepScan(m);

            Fact("Mod Name", m.Name);
            Fact("Workshop ID", m.IsLocal ? "Local install (not from the workshop)" : m.WorkshopId.ToString());
            Fact("Author", string.IsNullOrEmpty(m.Author) ? "(not stated)" : m.Author);
            if (!string.IsNullOrEmpty(m.Version)) Fact("Version", m.Version);
            Fact("Last Update", m.InstalledAt == DateTime.MinValue
                ? "(unknown)" : m.InstalledAt.ToLocalTime().ToString("dddd d MMMM yyyy, HH:mm"));
            Fact("Last Loaded", m.LastLoaded == DateTime.MinValue
                ? "never" : m.LastLoaded.ToLocalTime().ToString("dddd d MMMM yyyy, HH:mm"));
            Fact("File Size", m.SizeBytes < 0 ? "measuring..." : m.SizeText);
            Fact("Status", m.Status(_steam));

            // NOT a Fact row. A ListView cell is one line, and this explanation
            // is a paragraph - as a cell it ran off the side of the window with
            // no way to read the rest of it. It goes in the wrapping label
            // below the table instead.
            _statusDetail.Text = m.StatusDetail(_steam);
            Fact("Content", m.PboCount + (m.PboCount == 1 ? " pbo" : " pbos"));
            Fact("Key (signed)", m.Signed
                ? "Signed (" + m.SignatureCount + (m.SignatureCount == 1 ? " signature)" : " signatures)")
                : "NOT SIGNED - some servers reject it");
            Fact("Key Files", m.HasKeysFolder
                ? "Yes (" + m.KeyFileCount + (m.KeyFileCount == 1 ? " bikey)" : " bikeys)")
                : "No");
            if (!string.IsNullOrEmpty(m.LinkFolder)) Fact("Folder", m.LinkFolder);
            Fact("Workshop folder", m.Folder);

            ShowImage(m);
        }

        /// <summary>
        /// Sizes the facts table to the panel: a fixed label column, and the
        /// value column taking the rest. Called whenever the panel is dragged.
        /// </summary>
        private void FitFactColumns()
        {
            if (_facts == null || _facts.Columns.Count < 2) return;
            try
            {
                int room = _facts.ClientSize.Width - _facts.Columns[0].Width - 4;
                if (room > 60) _facts.Columns[1].Width = room;
            }
            catch { }
        }

        private void Fact(string key, string value)
        {
            var it = new ListViewItem(new[] { key, value }) { UseItemStyleForSubItems = false };
            it.SubItems[0].ForeColor = Dim;
            it.SubItems[1].ForeColor = Color.Gainsboro;
            it.ToolTipText = value;
            _facts.Items.Add(it);
        }

        /// <summary>
        /// A mod's own image when it ships one.
        ///
        /// Steam does NOT keep the workshop preview on disk, and the details
        /// query returns an empty URL for it, so there is nothing to show for
        /// most mods - measured, only 5 of the first 300 carry a png or jpg at
        /// all. Rather than an empty grey box, the rest get a tile drawn from
        /// the mod's own initials.
        /// </summary>
        private void ShowImage(ModEntry m)
        {
            ulong id = m.WorkshopId;

            // 1. The workshop preview, if it has already been fetched.
            var cached = ModImages.Cached(id);
            if (cached != null) { _image.Image = cached; return; }

            // 2. An image the mod itself ships. Rare - measured at 5 of the
            //    first 300 mods - but free when it is there.
            try
            {
                if (Directory.Exists(m.Folder))
                {
                    var file = Directory.EnumerateFiles(m.Folder, "*.*")
                        .FirstOrDefault(f =>
                        {
                            string ext = Path.GetExtension(f).ToLowerInvariant();
                            return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp";
                        });
                    if (file != null)
                    {
                        byte[] bytes = File.ReadAllBytes(file);
                        using (var ms = new MemoryStream(bytes)) _image.Image = Image.FromStream(ms);
                        return;
                    }
                }
            }
            catch { }

            // 3. Something to look at immediately, then ask Steam in the
            //    background. The callback only fires if a preview really
            //    arrives, and only replaces the picture if this mod is still
            //    the one selected.
            _image.Image = Placeholder(m.Name);
            ModImages.Fetch(id, img =>
            {
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        if (_selected != null && _selected.WorkshopId == id) _image.Image = img;
                    }));
                }
                catch { }
            });
        }

        private static Image Placeholder(string name)
        {
            var bmp = new Bitmap(310, 150);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(52, 52, 60));
                string initials = Initials(name);
                using (var font = new Font("Segoe UI", 44f, FontStyle.Bold))
                using (var brush = new SolidBrush(Color.FromArgb(170, 170, 185)))
                {
                    var size = g.MeasureString(initials, font);
                    g.DrawString(initials, font, brush,
                        (310 - size.Width) / 2f, (150 - size.Height) / 2f);
                }
            }
            return bmp;
        }

        private static string Initials(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "?";
            var words = name.Split(new[] { ' ', '_', '-', '.' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return "?";
            if (words.Length == 1) return words[0].Substring(0, Math.Min(2, words[0].Length)).ToUpperInvariant();
            return (words[0].Substring(0, 1) + words[1].Substring(0, 1)).ToUpperInvariant();
        }

        // ------------------------------------------------------------ actions --

        private void RepairSelected()
        {
            var m = _selected;
            if (m == null) return;
            if (!SteamWorkshop.EnsureWorkshopApp(_gameDir, s => { }))
            {
                MessageBox.Show("Steam is not available, so mods cannot be changed from here.",
                                "Steam not available", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Subscribe first: forcing a download of something unsubscribed
            // asks Steam for an item it does not believe it owns.
            SteamWorkshop.Subscribe(m.WorkshopId);
            SteamWorkshop.ForceDownload(m.WorkshopId);
            _status.Text = "Repairing " + m.Name + " - Steam is re-downloading it.";
        }

        private void RemoveSelected()
        {
            var m = _selected;
            if (m == null) return;
            if (MessageBox.Show(
                    "Unsubscribe from " + m.Name + "?\r\n\r\nSteam will delete it from disk. "
                    + "Any server that requires it will have to download it again.",
                    "Remove mod", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            if (!SteamWorkshop.EnsureWorkshopApp(_gameDir, s => { }))
            {
                MessageBox.Show("Steam is not available, so mods cannot be changed from here.",
                                "Steam not available", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            SteamWorkshop.Unsubscribe(m.WorkshopId);
            _status.Text = "Unsubscribed from " + m.Name + ".";
        }

        // ------------------------------------------------- bulk actions --

        /// <summary>
        /// Re-downloads every mod whose install never completed.
        ///
        /// No confirmation: these are already broken, the player can see that
        /// in the list, and the fix is the only sensible thing to do with them.
        /// </summary>
        /// <summary>
        /// Only genuinely broken installs. An "Unpacked" mod is somebody's
        /// working copy, and repairing it would replace it with the workshop
        /// version - destroying local edits.
        /// </summary>
        /// <summary>
        /// Subscribes to a workshop item by id and downloads it.
        ///
        /// Subscribe FIRST. Asking Steam to download an item the account does
        /// not own is a no-op that reports success, which looks exactly like a
        /// download that finished instantly - and then the mod is not there.
        /// </summary>
        private void InstallById()
        {
            if (!EnsureSteam()) return;

            ulong id = InstallByIdDialog.Ask(this);
            if (id == 0) return;

            // Confirm with Steam that this id is real before subscribing to it.
            // Subscribing to a nonexistent item succeeds and downloads nothing,
            // which is indistinguishable from a fast download of a small mod.
            ulong resolved = SteamWorkshop.ResolveDownloadId(id, null);
            if (resolved != id)
            {
                if (MessageBox.Show(this,
                        "Steam has no item " + id + ", but " + resolved + " does exist."
                        + "\r\n\r\nInstall " + resolved + " instead?",
                        "Check the id", MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question) != DialogResult.Yes) return;

                id = resolved;
            }

            // Already here? Say so rather than re-downloading it silently.
            if (SteamWorkshop.IsInstalled(_steam, id))
            {
                var existing = _all.FirstOrDefault(m => m.WorkshopId == id);
                string known = existing != null ? " (" + existing.Name + ")" : "";

                if (MessageBox.Show(this,
                        "Workshop item " + id + " is already installed" + known + "."
                        + "\r\n\r\nDownload it again?",
                        "Already installed", MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                {
                    Select(id);
                    return;
                }
            }

            if (!SteamWorkshop.Subscribe(id))
            {
                MessageBox.Show(this,
                    "Steam would not accept the subscription for " + id + "."
                    + "\r\n\r\nCheck the id is right and that Steam is signed in.",
                    "Could not subscribe", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            SteamWorkshop.ForceDownload(id);

            // The download window already knows how to wait on an item and show
            // progress, so this reuses it rather than growing a second one.
            using (var dl = new ModDownloadForm(_steam, _gameDir, new[] { new Mod("", id) }))
            {
                dl.ShowDialog(this);
            }

            // The folder is new, so every cached answer about it is wrong.
            SteamWorkshop.ForgetItemPaths();
            ModIndex.Invalidate();

            // The main window remembers which workshop ids it has asked Steam
            // about, and it will have asked about this one BEFORE the item
            // existed - getting no answer. Clearing that lets the mod panel
            // pick up the real publication date instead of treating the miss
            // as final.
            var main = Owner as MainForm;
            if (main != null) main.ForgetModTimeChecks();

            Rescan();

            if (SteamWorkshop.IsInstalled(_steam, id))
            {
                Select(id);
                _status.Text = "Installed workshop item " + id + ".";
            }
            else
            {
                _status.Text = "Workshop item " + id + " did not finish downloading.";
            }
        }

        /// <summary>Highlights a mod in the list, if it is there.</summary>
        private void Select(ulong id)
        {
            for (int i = 0; i < _shown.Count; i++)
            {
                if (_shown[i].WorkshopId != id) continue;

                _list.SelectedIndices.Clear();
                _list.SelectedIndices.Add(i);
                try { _list.EnsureVisible(i); } catch { }
                return;
            }
        }

        private void RepairCorrupted()
        {
            var bad = _all.Where(m => m.Status(_steam) == "Corrupt").ToList();
            if (bad.Count == 0)
            {
                MessageBox.Show(this, "Nothing to repair - no mod is missing its meta.cpp.",
                                "Repair corrupted", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!EnsureSteam()) return;

            foreach (var m in bad)
            {
                // Subscribe first: Steam will not download an item it does not
                // believe the player owns.
                SteamWorkshop.Subscribe(m.WorkshopId);
                SteamWorkshop.ForceDownload(m.WorkshopId);
            }
            _status.Text = string.Format(
                "Asked Steam to re-download {0} broken mod{1}. Watch progress in Steam's Downloads.",
                bad.Count, bad.Count == 1 ? "" : "s");
        }

        /// <summary>
        /// Asks Steam to fetch every installed mod again.
        ///
        /// WHAT "VALIDATE" CAN ACTUALLY MEAN HERE
        ///   ISteamUGC has no verify-integrity call - that exists for games, not
        ///   for workshop items. The nearest thing is to request each item again
        ///   and let Steam work out what differs. On a library this size that is
        ///   a very long job and a lot of traffic, which is why it asks first
        ///   and says how much it is about to touch.
        /// </summary>
        private void ValidateAll()
        {
            var all = _all.Where(m => m.HasMeta || Directory.Exists(m.Folder)).ToList();
            if (all.Count == 0) return;

            long bytes = all.Sum(m => Math.Max(0, m.SizeBytes));
            string size = bytes > 0 ? string.Format(" ({0:N1} GB on disk)", bytes / 1073741824.0) : "";

            if (MessageBox.Show(this,
                    "Ask Steam to re-check all " + all.Count + " installed mods" + size + "?"
                    + "\r\n\r\nSteam has no quick verify for workshop items, so each one is "
                    + "requested again and re-downloaded wherever it differs."
                    + "\r\n\r\nThis takes a long time and a lot of bandwidth.",
                    "Validate all mods", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;

            if (!EnsureSteam()) return;
            RunBulk("Validating", all, m =>
            {
                SteamWorkshop.Subscribe(m.WorkshopId);
                SteamWorkshop.ForceDownload(m.WorkshopId);
            });
        }

        /// <summary>
        /// Unsubscribes from every DayZ workshop mod.
        ///
        /// Steam deletes the files afterwards, so this is not something to walk
        /// into by accident. The confirmation names the count and the size, and
        /// defaults to No.
        /// </summary>
        private void UnsubAll()
        {
            var all = _all.ToList();
            if (all.Count == 0) return;

            long bytes = all.Sum(m => Math.Max(0, m.SizeBytes));
            string size = bytes > 0 ? string.Format(" and free {0:N1} GB", bytes / 1073741824.0) : "";

            if (MessageBox.Show(this,
                    "Unsubscribe from ALL " + all.Count + " DayZ mods" + size + "?"
                    + "\r\n\r\nSteam will delete every one of them from disk. Any server that "
                    + "needs them will have to download them all again."
                    + "\r\n\r\nThis cannot be undone from here.",
                    "Unsubscribe from everything", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;

            // Asked twice on purpose: the first dialog is easy to click through,
            // and this one deletes a library that took hours to gather.
            if (MessageBox.Show(this,
                    "Last check - this removes every DayZ mod you have.\r\n\r\nContinue?",
                    "Unsubscribe from everything", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;

            if (!EnsureSteam()) return;
            RunBulk("Unsubscribing", all, m => SteamWorkshop.Unsubscribe(m.WorkshopId));
        }

        /// <summary>
        /// Walks a list of mods off the UI thread, reporting progress.
        ///
        /// Eight hundred Steam calls in a row would otherwise lock the window
        /// solid, and this one is deliberately usable while it works.
        /// </summary>
        private void RunBulk(string what, List<ModEntry> mods, Action<ModEntry> step)
        {
            SetBulkEnabled(false);
            new Thread(() =>
            {
                int done = 0;
                foreach (var m in mods)
                {
                    if (_closing) return;
                    try { step(m); } catch { }
                    done++;

                    if (done % 20 != 0 && done != mods.Count) continue;
                    int shown = done;
                    try
                    {
                        BeginInvoke((Action)(() =>
                            _status.Text = string.Format("{0}... {1} of {2}", what, shown, mods.Count)));
                    }
                    catch { return; }
                }

                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        SetBulkEnabled(true);
                        _status.Text = string.Format("{0} finished - {1} mods. Steam is doing the rest.",
                                                     what, mods.Count);
                    }));
                }
                catch { }
            })
            { IsBackground = true }.Start();
        }

        /// <summary>
        /// Greys the bulk buttons out while one of them is running, so a second
        /// sweep cannot be started on top of the first.
        ///
        /// The buttons are held in a list rather than hunted for by their text:
        /// searching by caption breaks the moment a label is reworded, and
        /// silently - the button simply stops being disabled.
        /// </summary>
        private void SetBulkEnabled(bool on)
        {
            foreach (var b in _bulkButtons)
            {
                if (b != null && !b.IsDisposed) b.Enabled = on;
            }
        }

        private readonly List<Button> _bulkButtons = new List<Button>();

        /// <summary>Steam has to be reachable before any of this can work.</summary>
        private bool EnsureSteam()
        {
            if (SteamWorkshop.EnsureWorkshopApp(_gameDir, s => { })) return true;
            MessageBox.Show(this, "Steam is not available, so mods cannot be changed from here.",
                            "Steam not available", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        private void OpenWorkshop()
        {
            if (_selected == null) return;
            SteamWorkshop.OpenWorkshopPage(_selected.WorkshopId);
        }

        private void OpenFolder()
        {
            if (_selected == null) return;
            try
            {
                // The "@Name" folder DayZ keeps beside the game, not the
                // numeric workshop one - that is where a player expects to land
                // and what they can recognise once Explorer opens.
                string target = _selected.DisplayFolder;
                if (Directory.Exists(target))
                {
                    // GetFullPath, always - see ModInfoDialog. A mixed-separator
                    // path sends explorer to Documents rather than the mod.
                    Process.Start("explorer.exe", "\"" + Path.GetFullPath(target) + "\"");
                }
                else
                    _status.Text = "That folder is no longer on disk.";
            }
            catch (Exception ex) { _status.Text = "Could not open the folder: " + ex.Message; }
        }
    }
}
