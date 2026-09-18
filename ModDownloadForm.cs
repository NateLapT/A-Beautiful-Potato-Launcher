// ---------------------------------------------------------------------------
//  Subscribes to any missing mods and waits for Steam to finish downloading.
//
//  Two routes, in order:
//    1. Steam API - SubscribeItem + DownloadItem, no player interaction.
//    2. Workshop pages - opened in Steam so the player clicks Subscribe.
//
//  Either way the WAIT is the same, and it watches the filesystem rather than
//  trusting the API. That keeps the progress display honest even when the
//  subscription came from a manual click, and means a half-finished download
//  is never mistaken for a ready mod.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace BeautifulPotatoLauncher
{
    internal sealed class ModDownloadForm : Form
    {
        private readonly string _steam;
        private readonly string _gameDir;
        private readonly Mod[] _missing;
        private readonly Dictionary<ulong, Label> _rows = new Dictionary<ulong, Label>();

        private readonly Timer _timer = new Timer();
        private readonly Label _status;
        private readonly Button _cancel;
        private readonly Button _openPages;
        private bool _usedApi;
        private int _ticks;

        public ModDownloadForm(string steamPath, string gameDir, Mod[] missing)
        {
            _steam = steamPath;
            _gameDir = gameDir;
            _missing = missing;

            Text = "Downloading mods";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(28, 28, 30);
            ForeColor = Color.Gainsboro;
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(520, 150 + missing.Length * 26);

            int y = 14;
            Controls.Add(new Label
            {
                Text = missing.Length == 1
                       ? "One mod is missing. Getting it from the Steam Workshop:"
                       : missing.Length + " mods are missing. Getting them from the Steam Workshop:",
                Bounds = new Rectangle(16, y, 490, 20),
                ForeColor = Color.White
            });
            y += 28;

            foreach (var m in _missing)
            {
                var row = new Label
                {
                    Text = "  " + m.Name + "  -  waiting",
                    Bounds = new Rectangle(16, y, 490, 22),
                    ForeColor = Color.FromArgb(150, 150, 155),
                    Font = new Font("Consolas", 8.5f)
                };
                Controls.Add(row);
                _rows[m.WorkshopId] = row;
                y += 26;
            }

            y += 6;
            _status = new Label
            {
                Text = "Starting...",
                Bounds = new Rectangle(16, y, 490, 36),
                ForeColor = Color.FromArgb(170, 200, 170)
            };
            Controls.Add(_status);
            y += 42;

            _openPages = new Button
            {
                Text = "Open Workshop pages",
                Bounds = new Rectangle(16, y, 170, 28),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(55, 55, 60),
                ForeColor = Color.White
            };
            _openPages.Click += (s, e) => OpenAllPages();
            Controls.Add(_openPages);

            _cancel = new Button
            {
                Text = "Cancel",
                Bounds = new Rectangle(406, y, 100, 28),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(70, 55, 55),
                ForeColor = Color.White,
                DialogResult = DialogResult.Cancel
            };
            Controls.Add(_cancel);
            CancelButton = _cancel;

            Shown += OnShown;
        }

        private void OnShown(object sender, EventArgs e)
        {
            var log = new List<string>();
            _usedApi = SteamWorkshop.TryInit(_gameDir, log.Add);

            if (_usedApi)
            {
                int ok = _missing.Count(m => SteamWorkshop.Subscribe(m.WorkshopId));
                if (ok > 0)
                {
                    _status.Text = "Subscribed automatically through Steam. Downloading...";
                }
                else
                {
                    _usedApi = false;
                }
            }

            if (!_usedApi)
            {
                _status.Text = "Opening the Workshop in Steam - click Subscribe on each page.\r\n"
                             + "This window will continue by itself once the files arrive.";
                OpenAllPages();
            }

            _timer.Interval = 1000;
            _timer.Tick += OnTick;
            _timer.Start();
        }

        private void OpenAllPages()
        {
            foreach (var m in _missing)
                SteamWorkshop.OpenWorkshopPage(m.WorkshopId);
        }

        private void OnTick(object sender, EventArgs e)
        {
            _ticks++;
            SteamWorkshop.RunCallbacks();

            int done = 0;
            foreach (var m in _missing)
            {
                var row = _rows[m.WorkshopId];
                long got, total;

                if (SteamWorkshop.TryGetProgress(m.WorkshopId, out got, out total))
                {
                    // Steam is actively transferring, so report its own numbers.
                    int pct = (int)(100.0 * got / total);
                    row.Text = string.Format("  {0}  -  {1}%  ({2} of {3})",
                                             m.Name, pct, Mb(got), Mb(total));
                    row.ForeColor = Color.FromArgb(200, 190, 130);
                }
                else if (SteamWorkshop.IsInstalled(_steam, m.WorkshopId)
                         && !SteamWorkshop.IsBusy(m.WorkshopId))
                {
                    // On disk, and Steam has nothing left queued for it.
                    row.Text = "  " + m.Name + "  -  ready ("
                             + Mb(SteamWorkshop.SizeOnDisk(_steam, m.WorkshopId)) + ")";
                    row.ForeColor = Color.FromArgb(140, 200, 140);
                    done++;
                }
                else if (SteamWorkshop.IsBusy(m.WorkshopId))
                {
                    row.Text = "  " + m.Name + "  -  queued by Steam";
                    row.ForeColor = Color.FromArgb(200, 190, 130);
                }
                else
                {
                    row.Text = "  " + m.Name + "  -  waiting for subscription";
                }
            }

            if (done == _missing.Length)
            {
                _timer.Stop();
                _status.Text = "All mods ready.";
                DialogResult = DialogResult.OK;
                Close();
                return;
            }

            _status.Text = string.Format("{0} of {1} ready.{2}",
                done, _missing.Length,
                _usedApi ? "" : "  Subscribe on the Workshop pages if you have not yet.");

            // If nothing at all has appeared after a while on the manual route,
            // say so plainly rather than spinning silently.
            if (!_usedApi && _ticks == 45 && done == 0)
            {
                _status.Text = "Nothing has downloaded yet.\r\n"
                             + "Check Steam - the Workshop pages need a Subscribe click.";
            }
        }

        private static string Mb(long bytes)
        {
            if (bytes >= 1048576) return (bytes / 1048576.0).ToString("N1") + " MB";
            return Math.Max(1, bytes / 1024) + " KB";
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _timer.Stop();
            base.OnFormClosed(e);
        }
    }
}
