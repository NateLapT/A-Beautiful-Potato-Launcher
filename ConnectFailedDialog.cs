// ---------------------------------------------------------------------------
//  "Failed to connect" - shown when CONNECT finds the server not answering.
//
//  It replaced a Windows message box that listed the query ports tried and
//  the cfg setting behind them. That is for whoever reads the log (it is
//  still written there); a player needs to know the join failed and what
//  they can do about it:
//
//    AUTO REJOIN   asks the server again every two seconds and joins the
//                  moment it answers - for a server that is restarting,
//                  which is usually back within a minute or three.
//    REPORT        sends the server's details to our feedback tracker, for
//                  a server that IS up but never answers the launcher. No
//                  typing: what we need is known here already.
// ---------------------------------------------------------------------------

using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ABeautifulPotatoLauncher
{
    /// <summary>CONNECT found the server not answering on any query port tried.</summary>
    internal sealed class ServerNotAnsweringException : Exception
    {
        public readonly System.Collections.Generic.List<int> Tried;
        public readonly string Error;

        public ServerNotAnsweringException(string message, System.Collections.Generic.List<int> tried, string error)
            : base(message)
        {
            Tried = tried ?? new System.Collections.Generic.List<int>();
            Error = error;
        }
    }

    internal sealed class ConnectFailedDialog : Form
    {
        private static readonly Color Ink = Color.FromArgb(24, 24, 27);
        private static readonly Color Panel2 = Color.FromArgb(38, 38, 42);
        private static readonly Color Dim = Color.FromArgb(150, 150, 158);
        private static readonly Color Warn = Color.FromArgb(230, 130, 130);
        private static readonly Color Good = Color.FromArgb(120, 200, 120);

        /// <summary>How often Auto reJoin asks the server again.</summary>
        private const int RetryEveryMs = 2000;

        private readonly Func<bool> _answers;
        private readonly Func<string> _report;
        private readonly Label _status;
        private readonly Button _rejoin;
        private readonly Button _reportButton;
        private bool _retrying;
        private int _attempt;
        private DateTime _retryStarted;

        /// <param name="serverName">Shown as the server's name (already streamer-safe).</param>
        /// <param name="answers">Asks the server once; true when it answers. Runs off the UI thread.</param>
        /// <param name="report">Sends the report; null when sent, else what went wrong. Runs off the UI thread.</param>
        private ConnectFailedDialog(string serverName, Func<bool> answers, Func<string> report)
        {
            _answers = answers;
            _report = report;

            Text = "Failed to connect";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(500, 196);
            BackColor = Ink;
            ForeColor = Color.Gainsboro;
            Font = new Font("Segoe UI", 9f);

            Controls.Add(new Label
            {
                Text = "Failed to connect",
                Bounds = new Rectangle(16, 14, 468, 26),
                ForeColor = Warn,
                Font = new Font("Segoe UI", 13f, FontStyle.Bold)
            });
            Controls.Add(new Label
            {
                Text = serverName,
                Bounds = new Rectangle(16, 44, 468, 20),
                ForeColor = Color.White,
                AutoEllipsis = true,
                UseMnemonic = false,           // a "&" in a server name is not a shortcut
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
            });
            Controls.Add(new Label
            {
                Text = "Check Server is Online",
                Bounds = new Rectangle(16, 66, 468, 20),
                ForeColor = Dim
            });

            _status = new Label
            {
                Bounds = new Rectangle(16, 96, 468, 36),
                ForeColor = Dim,
                AutoEllipsis = true
            };
            Controls.Add(_status);

            _rejoin = MakeButton("Auto reJoin", new Rectangle(16, 146, 120, 32), Color.FromArgb(60, 95, 60),
                                 Color.FromArgb(80, 120, 80));
            _rejoin.Click += (s, e) => { if (_retrying) StopRetrying("Auto reJoin stopped."); else StartRetrying(); };
            new ToolTip().SetToolTip(_rejoin, "Ask the server again every 2 seconds and join as soon as it answers.");
            Controls.Add(_rejoin);

            _reportButton = MakeButton("Report Server Showing offline while Online", new Rectangle(144, 146, 262, 32),
                                       Panel2, Color.FromArgb(75, 75, 82));
            _reportButton.Click += OnReport;
            new ToolTip().SetToolTip(_reportButton,
                "The server is up, but the launcher can't reach it? This sends its details to us to check. Nothing to fill in.");
            Controls.Add(_reportButton);

            var close = MakeButton("Close", new Rectangle(414, 146, 70, 32), Color.FromArgb(70, 55, 55),
                                   Color.FromArgb(100, 70, 70));
            close.DialogResult = DialogResult.Cancel;
            Controls.Add(close);
            CancelButton = close;
            ActiveControl = close;

            FormClosing += (s, e) => _retrying = false;
            UiCursors.ApplyTo(this);
        }

        private static Button MakeButton(string text, Rectangle bounds, Color back, Color border)
        {
            var b = new Button
            {
                Text = text,
                Bounds = bounds,
                FlatStyle = FlatStyle.Flat,
                BackColor = back,
                ForeColor = Color.White
            };
            b.FlatAppearance.BorderColor = border;
            return b;
        }

        // ---------------------------------------------------- auto rejoin --

        private async void StartRetrying()
        {
            _retrying = true;
            _attempt = 0;
            _retryStarted = DateTime.UtcNow;
            _rejoin.Text = "Stop reJoin";
            _status.ForeColor = Dim;

            while (_retrying && !IsDisposed)
            {
                _attempt++;
                int secs = (int)(DateTime.UtcNow - _retryStarted).TotalSeconds;
                _status.Text = "Auto reJoin: asking the server every 2 seconds...  attempt " + _attempt
                             + "  (" + (secs / 60) + ":" + (secs % 60).ToString("00") + ")";

                bool up;
                try { up = await Task.Run(_answers); }
                catch { up = false; }
                if (!_retrying || IsDisposed) return;

                if (up)
                {
                    _status.ForeColor = Good;
                    _status.Text = "The server answered - joining...";
                    DialogResult = DialogResult.Retry;      // the caller joins again
                    Close();
                    return;
                }
                await Task.Delay(RetryEveryMs);
            }
        }

        private void StopRetrying(string message)
        {
            _retrying = false;
            _rejoin.Text = "Auto reJoin";
            _status.ForeColor = Dim;
            _status.Text = message;
        }

        // --------------------------------------------------------- report --

        private void OnReport(object sender, EventArgs e)
        {
            _reportButton.Enabled = false;
            _reportButton.Text = "Sending...";
            Task.Run(() =>
            {
                string problem;
                try { problem = _report(); }
                catch (Exception ex) { problem = ex.Message; }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        if (problem == null)
                        {
                            _reportButton.Text = "Reported - thanks!";
                            if (!_retrying) { _status.ForeColor = Good; _status.Text = "Sent. We'll check this server."; }
                            return;
                        }
                        _reportButton.Enabled = true;
                        _reportButton.Text = "Report Server Showing offline while Online";
                        _status.ForeColor = Warn;
                        _status.Text = "Couldn't send the report (" + problem + "). Try again in a moment.";
                    }));
                }
                catch (InvalidOperationException) { }        // closed meanwhile
            });
        }

        /// <summary>
        /// Shows the window. True when Auto reJoin found the server answering
        /// and the caller should join again; false when it was closed.
        /// </summary>
        internal static bool Show(IWin32Window owner, string serverName, Func<bool> answers, Func<string> report)
        {
            using (var dlg = new ConnectFailedDialog(serverName, answers, report))
                return dlg.ShowDialog(owner) == DialogResult.Retry;
        }
    }
}
