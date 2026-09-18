// ---------------------------------------------------------------------------
//  Subscribes to whatever the server needs and waits for Steam to deliver it.
//
//  Two routes, in order:
//    1. Steam API - SubscribeItem + DownloadItem, no player interaction.
//    2. Workshop pages - opened in Steam so the player clicks Subscribe.
//
//  Either way the WAIT is the same, and readiness is judged from Steam's own
//  item state plus the filesystem rather than from having asked. That keeps the
//  display honest when the subscription came from a manual click, and stops a
//  half-finished download being mistaken for a ready mod.
//
//  WHY SO MUCH DETAIL ON SCREEN
//    This is the step where a player sits and waits, sometimes for gigabytes.
//    Nothing here is decoration: per-mod progress shows WHICH mod is holding
//    things up, the speed and estimate say whether it is worth waiting, and the
//    workshop ids let a stuck item be looked up by hand.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace BeautifulPotatoExpLauncher
{
    internal sealed class ModDownloadForm : Form
    {
        private const int ColMod = 0, ColId = 1, ColState = 2, ColSize = 3,
                          ColPercent = 4, ColSpeed = 5;

        private static readonly Color Ink    = Color.FromArgb(24, 24, 27);
        private static readonly Color Panel  = Color.FromArgb(28, 28, 30);
        private static readonly Color Panel2 = Color.FromArgb(38, 38, 42);
        private static readonly Color Dim    = Color.FromArgb(150, 150, 158);
        private static readonly Color Good   = Color.FromArgb(140, 200, 140);
        private static readonly Color Busy   = Color.FromArgb(215, 195, 130);
        private static readonly Color Bad    = Color.FromArgb(230, 130, 130);

        private sealed class Item
        {
            public Mod Mod;
            public ListViewItem Row;
            public long Done, Total;
            public long LastDone;
            public double Speed;             // bytes per second, smoothed
            public bool Ready;

            /// <summary>
            /// The meta.cpp timestamp this mod had when it was queued, or 0 if
            /// it had no meta.cpp at all. A mod queued because it was OUT OF
            /// DATE already has a meta.cpp, so "the file exists" cannot mean it
            /// finished - the timestamp has to have MOVED. See WasStale.
            /// </summary>
            public ulong StampAtQueue;

            /// <summary>
            /// True when this mod was already installed when it was queued, so
            /// it is here to be updated rather than fetched. Those are the ones
            /// whose completion has to be judged by the timestamp.
            /// </summary>
            public bool WasStale;

            /// <summary>
            /// Steam has had this item in a Downloading or DownloadPending state
            /// at some point. Once that has been true and then stops being true
            /// without the mod appearing, the download is over and it did not
            /// work - which is the only notice Steam gives.
            /// </summary>
            public bool EverBusy;

            /// <summary>
            /// The last moment anything happened for this mod: a byte arrived or
            /// Steam changed its state. Silence past a threshold is what
            /// distinguishes a slow download from a dead one.
            /// </summary>
            public DateTime LastMovement = DateTime.Now;

            public uint LastState = 0xffffffff;

            /// <summary>Set once the download is judged to have failed.</summary>
            public bool Failed;
            public string FailReason;
        }

        // ---- how long silence has to last before it counts as failure ----
        //
        // These are deliberately generous. A download that is merely slow must
        // never be declared dead - the cost of being wrong is telling a player
        // their mod failed when it was about to finish.

        /// <summary>
        /// Steam dropped the item from its queue without installing it. Measured
        /// against a download Steam abandoned: the state went DownloadPending,
        /// then cleared, then to nothing, all inside 1.3 seconds and with no
        /// callback of any kind. A few seconds of grace covers the gap between
        /// one state and the next.
        /// </summary>
        private static readonly TimeSpan AbandonGrace = TimeSpan.FromSeconds(10);

        /// <summary>Steam accepted the request but never started anything.</summary>
        private static readonly TimeSpan NeverStartedAfter = TimeSpan.FromSeconds(90);

        /// <summary>Transferring, but no bytes have arrived for this long.</summary>
        private static readonly TimeSpan StallAfter = TimeSpan.FromMinutes(3);

        private readonly string _steam;
        private readonly string _gameDir;
        private readonly Mod[] _wanted;
        private readonly List<Item> _items = new List<Item>();

        private readonly Timer _timer = new Timer();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private ListView _list;
        private ProgressBar _bar;
        private Label _headline, _detail, _hint;
        private Button _openPages, _retry;

        /// <summary>
        /// When the waiting started. Used to spot a download Steam accepted and
        /// then never began, which has no other symptom at all.
        /// </summary>
        private readonly DateTime _started = DateTime.Now;
        private bool _usedApi;
        private int _ticks;

        public ModDownloadForm(string steamPath, string gameDir, Mod[] wanted)
        {
            _steam = steamPath;
            _gameDir = gameDir;
            _wanted = wanted;

            Text = "Downloading mods";
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(760, 420);
            MinimumSize = new Size(620, 320);
            BackColor = Ink;
            ForeColor = Color.Gainsboro;
            Font = new Font("Segoe UI", 9f);

            BuildUi();
            Shown += OnShown;
        }

        private void BuildUi()
        {
            _headline = new Label
            {
                Dock = DockStyle.Top,
                Height = 26,
                Padding = new Padding(12, 4, 0, 0),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                Text = _wanted.Length == 1 ? "1 mod needed" : _wanted.Length + " mods needed"
            };
            Controls.Add(_headline);

            _hint = new Label
            {
                Dock = DockStyle.Top,
                Height = 22,
                Padding = new Padding(12, 2, 0, 0),
                ForeColor = Dim,
                Text = "Starting..."
            };
            Controls.Add(_hint);

            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                BackColor = Panel,
                ForeColor = Color.Gainsboro,
                BorderStyle = BorderStyle.FixedSingle
            };
            _list.Columns.Add("Mod", 250);
            _list.Columns.Add("Workshop ID", 100);
            _list.Columns.Add("State", 150);
            _list.Columns.Add("Size", 110, HorizontalAlignment.Right);
            _list.Columns.Add("Progress", 70, HorizontalAlignment.Right);
            _list.Columns.Add("Speed", 80, HorizontalAlignment.Right);
            Controls.Add(_list);

            foreach (var m in _wanted)
            {
                var row = new ListViewItem(new[] { m.DisplayName, m.WorkshopId.ToString(), "waiting", "", "", "" })
                { UseItemStyleForSubItems = false, ForeColor = Dim };
                row.SubItems[ColId].ForeColor = Color.FromArgb(120, 150, 190);
                _list.Items.Add(row);
                ulong stamp = _steam == null ? 0 : SteamWorkshop.MetaTimestamp(_steam, m.WorkshopId);
                _items.Add(new Item
                {
                    Mod = m,
                    Row = row,
                    StampAtQueue = stamp,
                    WasStale = stamp != 0
                });
            }

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 108, BackColor = Ink };
            Controls.Add(bottom);

            _bar = new ProgressBar
            {
                Bounds = new Rectangle(12, 8, 736, 20),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Style = ProgressBarStyle.Continuous,
                Maximum = 1000                    // per-mille, so the bar moves smoothly
            };
            bottom.Controls.Add(_bar);

            _detail = new Label
            {
                Bounds = new Rectangle(12, 34, 736, 34),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                ForeColor = Color.Gainsboro,
                Text = ""
            };
            bottom.Controls.Add(_detail);

            _openPages = new Button
            {
                Text = "Open Workshop pages",
                Bounds = new Rectangle(12, 72, 170, 28),
                Anchor = AnchorStyles.Top | AnchorStyles.Left,
                FlatStyle = FlatStyle.Flat,
                BackColor = Panel2,
                ForeColor = Color.White
            };
            _openPages.FlatAppearance.BorderColor = Color.FromArgb(75, 75, 82);
            _openPages.Click += (s, e) => OpenAllPages();
            bottom.Controls.Add(_openPages);

            _retry = new Button
            {
                Text = "Retry failed",
                Bounds = new Rectangle(190, 72, 130, 28),
                Anchor = AnchorStyles.Top | AnchorStyles.Left,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(60, 95, 60),
                ForeColor = Color.White,
                Visible = false            // only once something has failed
            };
            _retry.FlatAppearance.BorderColor = Color.FromArgb(80, 120, 80);
            _retry.Click += (s, e) => RetryFailed();
            bottom.Controls.Add(_retry);

            var cancel = new Button
            {
                Text = "Cancel",
                Bounds = new Rectangle(648, 72, 100, 28),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(70, 55, 55),
                ForeColor = Color.White,
                DialogResult = DialogResult.Cancel
            };
            cancel.FlatAppearance.BorderColor = Color.FromArgb(100, 70, 70);
            bottom.Controls.Add(cancel);
            CancelButton = cancel;

            // Docked controls fill from the highest child index down.
            Controls.SetChildIndex(_list, 0);
            Controls.SetChildIndex(bottom, 1);
            Controls.SetChildIndex(_hint, 2);
            Controls.SetChildIndex(_headline, 3);
        }

        private void OnShown(object sender, EventArgs e)
        {
            var log = new List<string>();
            _usedApi = SteamWorkshop.TryInit(_gameDir, log.Add);

            if (_usedApi)
            {
                int ok = _items.Count(i => SteamWorkshop.Subscribe(i.Mod.WorkshopId));
                if (ok > 0) _hint.Text = "Subscribed through Steam. Steam is fetching them now.";
                else _usedApi = false;
            }

            if (!_usedApi)
            {
                _hint.Text = "Steam is not reachable - opening the Workshop pages. "
                           + "Click Subscribe on each; this window continues by itself.";
                OpenAllPages();
            }

            _timer.Interval = 500;
            _timer.Tick += OnTick;
            _timer.Start();
        }

        private void OpenAllPages()
        {
            foreach (var i in _items) SteamWorkshop.OpenWorkshopPage(i.Mod.WorkshopId);
        }

        private void OnTick(object sender, EventArgs e)
        {
            _ticks++;
            SteamWorkshop.RunCallbacks();

            long grandDone = 0, grandTotal = 0;
            double grandSpeed = 0;
            int ready = 0, active = 0, failed = 0;

            // Overall progress is the AVERAGE of each mod's own fraction, not
            // bytes-done over bytes-known. A mod Steam has not sized yet counts
            // zero on both sides of a byte ratio, which made the bar sit at
            // 100% while four of six mods had not started.
            double fractionSum = 0;

            foreach (var it in _items)
            {
                long done, total;
                bool transferring = SteamWorkshop.TryGetProgress(it.Mod.WorkshopId, out done, out total);
                var state = SteamWorkshop.GetState(it.Mod.WorkshopId);
                bool busy = SteamWorkshop.IsBusy(it.Mod.WorkshopId);

                // "Finished" is not the same question for the two kinds of mod
                // in this queue. A MISSING one is finished when its meta.cpp
                // appears. An OUT OF DATE one already had a meta.cpp when it was
                // queued, so that file appearing proves nothing - it is finished
                // only once the timestamp inside it has actually moved on.
                //
                // Without this, an update raced its own progress bar: Steam
                // takes a moment to raise the downloading flag after
                // ForceDownload, and in that gap the old meta.cpp was still
                // sitting there, so the mod was called ready and the game
                // launched against the stale copy - the very kick this is all
                // meant to prevent.
                bool onDisk;
                if (it.WasStale)
                {
                    ulong now = SteamWorkshop.MetaTimestamp(_steam, it.Mod.WorkshopId);
                    onDisk = now != 0 && now != it.StampAtQueue;
                }
                else
                {
                    onDisk = SteamWorkshop.IsInstalled(_steam, it.Mod.WorkshopId);
                }

                // Any change of state counts as Steam still being alive on this
                // item, even if no bytes have moved yet.
                if ((uint)state != it.LastState)
                {
                    it.LastState = (uint)state;
                    it.LastMovement = DateTime.Now;
                }
                if (busy) { it.EverBusy = true; it.LastMovement = DateTime.Now; }

                // A mod already judged failed keeps its row and is not chased
                // again; only Retry clears it.
                if (it.Failed)
                {
                    failed++;
                    Set(it, "FAILED - " + it.FailReason, "", "", "", Bad);
                    continue;
                }

                if (transferring)
                {
                    // Speed from the delta since the last tick, smoothed so the
                    // number is readable instead of flickering.
                    long delta = done - it.LastDone;
                    if (delta > 0)
                    {
                        double instant = delta / (_timer.Interval / 1000.0);
                        it.Speed = it.Speed <= 0 ? instant : (it.Speed * 0.7 + instant * 0.3);
                        it.LastMovement = DateTime.Now;
                    }
                    else if (DateTime.Now - it.LastMovement > StallAfter)
                    {
                        Fail(it, "no data for " + (int)StallAfter.TotalMinutes + " minutes");
                        failed++;
                        continue;
                    }
                    it.LastDone = done;
                    it.Done = done;
                    it.Total = total;
                    it.Ready = false;
                    active++;

                    fractionSum += total > 0 ? Math.Min(1.0, done / (double)total) : 0;
                    Set(it, "downloading", Bytes(done) + " / " + Bytes(total),
                        Percent(done, total), Rate(it.Speed), Busy);
                }
                else if (onDisk && !busy)
                {
                    if (!it.Ready)
                    {
                        it.Ready = true;
                        it.Done = it.Total = SteamWorkshop.SizeOnDisk(_steam, it.Mod.WorkshopId);
                    }
                    ready++;
                    fractionSum += 1.0;
                    Set(it, "ready", Bytes(it.Total), "100%", "", Good);
                }
                else if (busy)
                {
                    it.Speed = 0;
                    Set(it, state.HasFlag(ItemState.Downloading) ? "starting..." : "queued by Steam",
                        it.Total > 0 ? Bytes(it.Total) : "", Percent(it.Done, it.Total), "", Busy);
                }
                else
                {
                    it.Speed = 0;

                    // NOT BUSY, NOT INSTALLED - and this is where the launcher
                    // used to wait for ever.
                    //
                    // Steam does not report a failed download. There IS a
                    // DownloadItemResult_t callback carrying an EResult, but it
                    // never arrived in testing - not for a download Steam
                    // abandoned, and not even for an API call known to complete,
                    // so the callback route is not available here at all.
                    //
                    // What Steam does do is drop the item: the Downloading and
                    // DownloadPending flags clear and the mod simply never
                    // appears. Having seen those flags set and then cleared,
                    // with nothing installed, is the failure - there is nothing
                    // else coming.
                    if (it.EverBusy && DateTime.Now - it.LastMovement > AbandonGrace)
                    {
                        Fail(it, "Steam stopped without installing it");
                        failed++;
                        continue;
                    }

                    // Or it never started at all - a request Steam accepted and
                    // then did nothing about.
                    if (!it.EverBusy && _usedApi
                        && DateTime.Now - _started > NeverStartedAfter)
                    {
                        Fail(it, "Steam never started the download");
                        failed++;
                        continue;
                    }

                    Set(it, _usedApi ? "waiting for Steam" : "waiting for subscription", "", "", "", Dim);
                }

                grandDone += it.Done;
                grandTotal += it.Total;
                grandSpeed += it.Speed;
            }

            // ---- overall ----
            if (ready > 0 || active > 0)
            {
                _bar.Style = ProgressBarStyle.Continuous;
                _bar.Value = (int)Math.Max(0, Math.Min(1000, 1000.0 * fractionSum / _items.Count));
            }
            else
            {
                // Nothing has started, so show motion rather than a dead bar.
                _bar.Style = ProgressBarStyle.Marquee;
            }

            _headline.Text = string.Format("{0} of {1} mods ready", ready, _items.Count);

            var parts = new List<string>();
            parts.Add(((int)(100.0 * fractionSum / _items.Count)) + "% overall");
            if (grandTotal > 0)
                parts.Add(Bytes(grandDone) + " of " + Bytes(grandTotal) + " accounted for");
            if (grandSpeed > 1024) parts.Add(Rate(grandSpeed));
            if (grandSpeed > 1024 && grandTotal > grandDone)
                parts.Add("about " + Eta((grandTotal - grandDone) / grandSpeed) + " left");
            parts.Add("elapsed " + Eta(_clock.Elapsed.TotalSeconds));
            if (active > 0) parts.Add(active + " downloading");

            _detail.Text = string.Join("    ", parts.ToArray());

            if (ready == _items.Count)
            {
                _timer.Stop();
                _bar.Style = ProgressBarStyle.Continuous;
                _bar.Value = _bar.Maximum;
                _headline.Text = "All mods ready";
                _detail.Text = Bytes(grandTotal) + " ready  -  finished in "
                             + Eta(_clock.Elapsed.TotalSeconds);
                DialogResult = DialogResult.OK;
                Close();
                return;
            }

            // Everything has either arrived or failed, so there is nothing left
            // to wait for. Stopping here is the whole point: waiting on a
            // download Steam has already given up on is waiting for ever.
            if (ready + failed == _items.Count)
            {
                _timer.Stop();
                _bar.Style = ProgressBarStyle.Continuous;
                _headline.Text = failed + (failed == 1 ? " mod failed to download"
                                                       : " mods failed to download");
                _detail.Text = "Steam gave up on " + failed + " of " + _items.Count
                             + ".  Joining now would end in a missing-mod kick.";
                _hint.Text = "RETRY asks Steam again. If it keeps failing, "
                           + "restarting Steam usually clears it.";
                if (_retry != null) _retry.Visible = true;
                return;
            }

            // If the manual route has produced nothing at all, say so plainly
            // rather than spinning in silence.
            if (!_usedApi && _ticks == 60 && ready == 0 && active == 0)
                _hint.Text = "Nothing has started yet - the Workshop pages need a Subscribe click.";
        }

        /// <summary>Marks one mod as failed and says so on its row.</summary>
        private void Fail(Item it, string why)
        {
            it.Failed = true;
            it.FailReason = why;
            it.Speed = 0;
            Set(it, "FAILED - " + why, "", "", "", Bad);
        }

        /// <summary>
        /// Asks Steam again for everything that failed. Subscribe first, then a
        /// forced download: Steam frequently still believes it holds the item,
        /// in which case a plain download request is a no-op.
        /// </summary>
        private void RetryFailed()
        {
            int n = 0;
            foreach (var it in _items)
            {
                if (!it.Failed) continue;
                it.Failed = false;
                it.FailReason = null;
                it.EverBusy = false;
                it.LastState = 0xffffffff;
                it.LastMovement = DateTime.Now;
                it.Speed = 0;
                it.LastDone = 0;
                Set(it, "retrying...", "", "", "", Busy);

                SteamWorkshop.Subscribe(it.Mod.WorkshopId);
                SteamWorkshop.ForceDownload(it.Mod.WorkshopId);
                n++;
            }
            if (n == 0) return;

            _retry.Visible = false;
            _hint.Text = "Asked Steam again for " + n + (n == 1 ? " mod." : " mods.");
            _bar.Style = ProgressBarStyle.Marquee;
            _timer.Start();
        }

        private static void Set(Item it, string state, string size, string pct, string speed, Color colour)
        {
            it.Row.SubItems[ColState].Text = state;
            it.Row.SubItems[ColSize].Text = size;
            it.Row.SubItems[ColPercent].Text = pct;
            it.Row.SubItems[ColSpeed].Text = speed;

            it.Row.SubItems[ColMod].ForeColor = colour;
            it.Row.SubItems[ColState].ForeColor = colour;
            it.Row.SubItems[ColSize].ForeColor = colour;
            it.Row.SubItems[ColPercent].ForeColor = colour;
            it.Row.SubItems[ColSpeed].ForeColor = colour;
        }

        private static string Percent(long done, long total)
        {
            if (total <= 0) return "";
            return ((int)(100.0 * done / total)) + "%";
        }

        private static string Bytes(long n)
        {
            if (n <= 0) return "";
            if (n >= 1073741824) return (n / 1073741824.0).ToString("N2") + " GB";
            if (n >= 1048576) return (n / 1048576.0).ToString("N1") + " MB";
            if (n >= 1024) return (n / 1024.0).ToString("N0") + " KB";
            return n + " B";
        }

        private static string Rate(double bytesPerSecond)
        {
            if (bytesPerSecond < 1024) return "";
            if (bytesPerSecond >= 1048576) return (bytesPerSecond / 1048576.0).ToString("N1") + " MB/s";
            return (bytesPerSecond / 1024.0).ToString("N0") + " KB/s";
        }

        private static string Eta(double seconds)
        {
            if (seconds < 0 || double.IsInfinity(seconds) || double.IsNaN(seconds)) return "?";
            var t = TimeSpan.FromSeconds(seconds);
            if (t.TotalHours >= 1) return string.Format("{0}h {1}m", (int)t.TotalHours, t.Minutes);
            if (t.TotalMinutes >= 1) return string.Format("{0}m {1}s", (int)t.TotalMinutes, t.Seconds);
            return (int)t.TotalSeconds + "s";
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _timer.Stop();
            base.OnFormClosed(e);
        }
    }
}
