// ---------------------------------------------------------------------------
//  The window that says "it is starting" while DayZ gets on its feet.
//
//  WHY: from the moment the launcher hands over to BattlEye, DayZ can take a
//  minute or two to put anything on screen. Nothing happens, the launcher
//  looks idle, and the only word about it was a line in the log panel that
//  nobody reads. Players conclude it did not work and press CONNECT again,
//  which starts a second copy and makes things worse.
//
//  The meter is HONEST ABOUT BEING A GUESS. There is no progress to report -
//  DayZ tells nobody how far along it is - so the bar fills against the clock
//  and stops short of the end rather than sitting at 100% while nothing
//  happens. What is real is the finish: the game's own process appearing is
//  watched for, and that is what fills the bar and closes the window.
// ---------------------------------------------------------------------------

using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace ABeautifulPotatoLauncher
{
    internal sealed class LaunchingForm : Form
    {
        private static readonly Color Ink = Color.FromArgb(24, 24, 27);
        private static readonly Color Panel2 = Color.FromArgb(38, 38, 42);
        private static readonly Color Dim = Color.FromArgb(150, 150, 158);
        private static readonly Color Go = Color.FromArgb(130, 200, 130);

        /// <summary>How wide the meter is, in characters.</summary>
        private const int Cells = 34;

        /// <summary>
        /// How long a cold start is assumed to take. The bar is paced to reach
        /// its last few cells at about this point and then wait there - better
        /// a bar that is still moving when the game appears than one that hit
        /// the end thirty seconds ago.
        /// </summary>
        private static readonly TimeSpan Expected = TimeSpan.FromSeconds(75);

        private readonly Label _head;
        private readonly Label _meter;
        private readonly Label _note;
        private readonly Button _close;
        private readonly Button _cancel;
        private readonly Process _started;
        private readonly Action<string> _log;
        private readonly Action _cancelled_cb;
        private readonly Timer _tick = new Timer { Interval = 250 };
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly string _exeName;

        private bool _seen;              // the game's process has appeared
        private DateTime _seenAt;

        private bool _cancelled;         // Cancel was pressed; the watch is armed
        private DateTime _cancelledAt;
        private DateTime _killedAt = DateTime.MinValue;

        /// <param name="started">
        /// The process the launcher actually started - BattlEye's launcher,
        /// which goes on to start the game. Cancel needs it: killing what we
        /// started is the only honest way to stop a launch that is already
        /// under way. Null is allowed; Cancel then falls back to the names.
        /// </param>
        /// <param name="onCancelled">
        /// Told when the player cancels, so the launcher can let them start
        /// something else at once rather than sitting out the rest of the
        /// quiet period meant for a launch that is no longer happening.
        /// </param>
        internal LaunchingForm(string serverName, string gameExe, Process started,
                               Action<string> log, Action onCancelled)
        {
            _started = started;
            _log = log ?? (m => { });
            _cancelled_cb = onCancelled ?? (() => { });

            _exeName = System.IO.Path.GetFileNameWithoutExtension(gameExe ?? "DayZ_x64");

            Text = "Starting DayZ";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            bool ours = IsOneOfOurs(serverName);
            ClientSize = new Size(460, ours ? 248 : 190);
            BackColor = Ink;
            ForeColor = Color.Gainsboro;
            Font = new Font("Segoe UI", 9f);

            _head = new Label
            {
                Text = "Starting DayZ...",
                Bounds = new Rectangle(18, 16, 424, 34),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 15f, FontStyle.Bold),
                UseMnemonic = false
            };
            Controls.Add(_head);

            Controls.Add(new Label
            {
                Text = serverName ?? "",
                Bounds = new Rectangle(18, 52, 424, 20),
                ForeColor = Dim,
                UseMnemonic = false,        // "&" in a server name is not a shortcut
                AutoEllipsis = true
            });

            // Consolas so every cell is the same width - in a proportional font
            // the bar would jiggle as it fills.
            _meter = new Label
            {
                Text = Bar(0),
                Bounds = new Rectangle(18, 82, 424, 30),
                ForeColor = Go,
                Font = new Font("Consolas", 14f, FontStyle.Bold),
                UseMnemonic = false
            };
            Controls.Add(_meter);

            _note = new Label
            {
                Text = "DayZ takes a minute or two to appear. This window closes by itself.",
                // 26 high, not 34: the extra reached down over the top of
                // the button below it, and a label added first sits on top in
                // z-order, so it painted over the button's top edge.
                Bounds = new Rectangle(18, 116, 424, 26),
                ForeColor = Dim,
                UseMnemonic = false
            };
            Controls.Add(_note);

            // THE EGG. Only on our own servers, and only here - it is a
            // thank-you to someone who chose to play on them, not a banner.
            if (ours)
            {
                // AutoSize, and the potato placed from where the text actually
                // ends. A fixed width guessed at the string's length overlapped
                // the picture, and because the label is added first it sits on
                // top and painted its own background over the spud, leaving a
                // sliver.
                var line = new Label
                {
                    Text = "You Are A Beautiful",
                    AutoSize = true,
                    Location = new Point(18, 166),
                    ForeColor = Color.White,
                    Font = new Font("Segoe UI", 13f, FontStyle.Bold),
                    UseMnemonic = false
                };
                Controls.Add(line);

                var spud = new PictureBox
                {
                    Image = MainForm.LoadImage("logo_potato.png"),
                    SizeMode = PictureBoxSizeMode.Zoom,
                    Bounds = new Rectangle(line.Location.X + line.PreferredWidth + 10, 152, 54, 54),
                    BackColor = Color.Transparent
                };
                Controls.Add(spud);
            }

            _close = new Button
            {
                Text = "Hide",
                Bounds = new Rectangle(356, ours ? 210 : 152, 86, 28),
                FlatStyle = FlatStyle.Flat,
                BackColor = Panel2,
                ForeColor = Color.White
            };
            _close.FlatAppearance.BorderColor = Color.FromArgb(75, 75, 82);
            _close.Click += (s, e) => Close();
            Controls.Add(_close);

            // STOPPING A LAUNCH THAT IS ALREADY UNDER WAY.
            //
            // Separate from Hide, which only gets the window out of the way.
            // This kills what the launcher started - and the game if BattlEye
            // has got that far - so a wrong server, or a change of mind during
            // the minute DayZ takes to appear, does not mean waiting for the
            // game to finish loading just to quit it.
            _cancel = new Button
            {
                Text = "Cancel",
                Bounds = new Rectangle(262, ours ? 210 : 152, 86, 28),
                FlatStyle = FlatStyle.Flat,

                // Properly red, not the muted red the other dialogs use for
                // "no thanks". This one stops something that is already
                // happening, and it should not be mistaken for Hide.
                BackColor = Color.FromArgb(160, 48, 48),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            _cancel.FlatAppearance.BorderColor = Color.FromArgb(200, 80, 80);
            _cancel.Click += OnCancel;
            Controls.Add(_cancel);

            CancelButton = _close;          // Esc hides, it does not stop the game

            _tick.Tick += OnTick;
            _tick.Start();
        }

        /// <summary>
        /// One of ours. Matched on the name rather than on an address list,
        /// because the addresses move and the name does not - and a community
        /// server that happens to be called something similar getting the
        /// kind word too is no loss at all.
        /// </summary>
        private static bool IsOneOfOurs(string serverName)
        {
            return serverName != null
                && serverName.IndexOf("beautiful potato", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>"||||||||..........", filled to the given fraction.</summary>
        private static string Bar(double fraction)
        {
            if (fraction < 0) fraction = 0;
            if (fraction > 1) fraction = 1;

            int filled = (int)Math.Round(fraction * Cells);
            return new string('|', filled) + new string('.', Cells - filled);
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (_cancelled)
            {
                if (KillNow())
                {
                    _log("DayZ was stopped.");
                    _head.Text = "Stopped.";
                    _note.Text = "DayZ was stopped before it finished loading.";
                    _meter.Text = Bar(0);
                    _killedAt = DateTime.UtcNow;
                }

                // Close once the game has been caught and killed, or once the
                // watch has run its course with nothing to catch.
                bool caught = _killedAt != DateTime.MinValue
                              && (DateTime.UtcNow - _killedAt).TotalSeconds >= 1.5;
                if (caught || DateTime.UtcNow - _cancelledAt > CancelWatch) Close();
                return;
            }

            // THE ONE REAL SIGNAL: the game's own process.
            if (!_seen && Running(_exeName))
            {
                _seen = true;
                _seenAt = DateTime.UtcNow;
                _head.Text = "DayZ is running.";

                // Cancel meant "stop this launch". The game is up now, and a
                // button that kills a running game is not what anyone expects
                // to find on a window that is about to close itself.
                _cancel.Visible = false;
                _meter.Text = Bar(1);
                _note.Text = "Good luck out there. This window is closing.";
                _close.Text = "Close";
            }

            if (_seen)
            {
                // Leave the finished bar up for a moment so it is seen, then go.
                if ((DateTime.UtcNow - _seenAt).TotalSeconds >= 2.5) Close();
                return;
            }

            double share = _clock.Elapsed.TotalSeconds / Expected.TotalSeconds;

            // Stops at nine tenths. The last cells belong to the game actually
            // arriving, and a bar sitting full while nothing happens is worse
            // than no bar at all.
            _meter.Text = Bar(Math.Min(share, 0.9));

            if (_clock.Elapsed > Expected)
                _note.Text = "Still starting. A first launch, or one after an update, is slower.";
        }

        /// <summary>
        /// Stops the launch - and KEEPS stopping it.
        ///
        /// One pass of killing was not enough, and the reason is a race: the
        /// launcher starts BattlEye's launcher, BattlEye starts the game and
        /// exits, and the gap between those is seconds long. Press Cancel in
        /// that gap and there is nothing running under either name yet, so
        /// nothing is killed - and DayZ carries on loading, which is exactly
        /// what happened.
        ///
        /// So Cancel arms a watch instead of firing once. Whatever is running
        /// now is killed now, and anything that appears afterwards is killed
        /// as it appears, until the game has been stopped or the watch times
        /// out. The window stays up and says so, because a window that closed
        /// while DayZ was still coming up would be lying.
        /// </summary>
        private void OnCancel(object sender, EventArgs e)
        {
            _log("Launch cancelled - stopping DayZ, and anything it starts from here.");

            _cancelled = true;
            _cancelledAt = DateTime.UtcNow;
            _cancelled_cb();

            _head.Text = "Cancelling...";
            _note.Text = "Stopping DayZ. If it is still starting, it will be stopped as it appears.";
            _meter.ForeColor = Color.FromArgb(220, 140, 120);
            _cancel.Visible = false;
            _close.Text = "Close";

            KillNow();
        }

        /// <summary>
        /// Kills the launch, as much of it as exists at this moment. Returns
        /// true if it actually killed a GAME process - which is what tells the
        /// watch it is finished, rather than merely early.
        /// </summary>
        private bool KillNow()
        {
            bool killedGame = false;

            try
            {
                if (_started != null && !_started.HasExited) _started.Kill();
            }
            catch { }

            // BattlEye starts the game as a separate process, so killing what
            // we started is never enough on its own. Both names are taken:
            // nothing else can be running under them, because a join refuses
            // to start at all while DayZ is already open.
            foreach (string name in new[] { "DayZ_BE", "DayZ_x64", _exeName })
            {
                try
                {
                    foreach (var p in Process.GetProcessesByName(name))
                    {
                        using (p)
                        {
                            try
                            {
                                p.Kill();
                                if (!string.Equals(name, "DayZ_BE", StringComparison.OrdinalIgnoreCase))
                                    killedGame = true;
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }

            return killedGame;
        }

        /// <summary>
        /// How long to keep watching after Cancel. Long enough to cover a slow
        /// start - the game can take a minute and a half to appear on a cold
        /// disk - and short enough that the watch cannot outlive the player's
        /// interest in it.
        /// </summary>
        private static readonly TimeSpan CancelWatch = TimeSpan.FromSeconds(120);

        private static bool Running(string exeName)
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(exeName))
                {
                    p.Dispose();
                    return true;
                }
            }
            catch { }
            return false;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _tick.Stop();
            _tick.Dispose();
            base.OnFormClosed(e);
        }
    }
}
