// ---------------------------------------------------------------------------
//  The Feedback window, from the button beside Settings.
//
//  Sent to the same service the server list comes from (see Relay), so a
//  player can report a problem without an account anywhere or leaving the
//  launcher. What goes: what they wrote, the server it is about if they name
//  one (the selected server is filled in), how to reach them if they say, the
//  launcher version, the game and the Windows version.
//
//  Two things go only when ticked:
//    - the server's mods list: the mods the selected server lists. Never the
//      mods installed on this PC;
//    - the launcher log, which names servers, mods and folders.
// ---------------------------------------------------------------------------

using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ABeautifulPotatoLauncher
{
    internal sealed class FeedbackDialog : Form
    {
        private static readonly Color Ink = Color.FromArgb(24, 24, 27);
        private static readonly Color Panel2 = Color.FromArgb(38, 38, 42);
        private static readonly Color Dim = Color.FromArgb(150, 150, 158);

        /// <summary>The longest message the service keeps, in characters.</summary>
        private const int MaxMessage = 8000;

        /// <summary>How much of the end of the log goes with it.</summary>
        private const int LogTailBytes = 200 * 1024;

        private readonly TextBox _message, _server, _contact;
        private readonly CheckBox _withMods, _withLog;
        private readonly Label _result;
        private readonly Button _send;
        private readonly Func<string, string> _modsFor;

        private FeedbackDialog(string server, Func<string, string> modsFor)
        {
            _modsFor = modsFor;

            Text = "Feedback";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(520, 470);
            BackColor = Ink;
            ForeColor = Color.Gainsboro;
            Font = new Font("Segoe UI", 9f);

            Controls.Add(new Label
            {
                Text = "Something not working, or an idea? Tell us here.",
                Bounds = new Rectangle(14, 14, 490, 18),
                ForeColor = Color.White
            });

            _message = new TextBox
            {
                Bounds = new Rectangle(14, 40, 492, 186),
                Multiline = true,
                AcceptsReturn = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Panel2,
                ForeColor = Color.Gainsboro,
                BorderStyle = BorderStyle.FixedSingle,
                MaxLength = MaxMessage
            };
            Controls.Add(_message);

            Controls.Add(new Label
            {
                Text = "Server name, if it's about a server (optional):",
                Bounds = new Rectangle(14, 236, 492, 18),
                ForeColor = Dim
            });

            _server = new TextBox
            {
                Bounds = new Rectangle(14, 256, 492, 24),
                BackColor = Panel2,
                ForeColor = Color.Gainsboro,
                BorderStyle = BorderStyle.FixedSingle,
                MaxLength = 200,
                Text = server ?? ""
            };
            Controls.Add(_server);

            Controls.Add(new Label
            {
                Text = "How to reach you, if you'd like a reply (optional) - a Discord name or an email:",
                Bounds = new Rectangle(14, 290, 492, 18),
                ForeColor = Dim
            });

            _contact = new TextBox
            {
                Bounds = new Rectangle(14, 310, 492, 24),
                BackColor = Panel2,
                ForeColor = Color.Gainsboro,
                BorderStyle = BorderStyle.FixedSingle,
                MaxLength = 200
            };
            Controls.Add(_contact);

            _withMods = new CheckBox
            {
                Text = "Send the server's mods list - the mods that server lists",
                Bounds = new Rectangle(14, 342, 492, 22),
                ForeColor = Color.Gainsboro,
                // Only the selected server's list can be sent.
                Enabled = !string.IsNullOrEmpty(server)
            };
            Controls.Add(_withMods);

            _withLog = new CheckBox
            {
                Text = "Include the launcher log - it helps with problems, and names the servers, "
                     + "mods and folders on this PC",
                Bounds = new Rectangle(14, 366, 492, 36),
                ForeColor = Color.Gainsboro
            };
            Controls.Add(_withLog);

            _result = new Label
            {
                Bounds = new Rectangle(14, 410, 300, 50),
                ForeColor = Dim,
                UseMnemonic = false
            };
            Controls.Add(_result);

            _send = new Button
            {
                Text = "SEND",
                Bounds = new Rectangle(322, 424, 92, 30),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(60, 95, 60),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            _send.FlatAppearance.BorderColor = Color.FromArgb(80, 120, 80);
            _send.Click += OnSend;
            Controls.Add(_send);

            var cancel = new Button
            {
                Text = "Cancel",
                Bounds = new Rectangle(420, 424, 86, 30),
                FlatStyle = FlatStyle.Flat,
                BackColor = Panel2,
                ForeColor = Color.White,
                DialogResult = DialogResult.Cancel
            };
            cancel.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 76);
            Controls.Add(cancel);
            CancelButton = cancel;
        }

        private void OnSend(object sender, EventArgs e)
        {
            string message = (_message.Text ?? "").Trim();
            if (message.Length == 0)
            {
                _result.ForeColor = Color.FromArgb(220, 140, 120);
                _result.Text = "Write something first.";
                _message.Focus();
                return;
            }

            string server = (_server.Text ?? "").Trim();
            string contact = (_contact.Text ?? "").Trim();
            string mods = null;
            if (_withMods.Checked && _modsFor != null)
                try { mods = _modsFor(server); } catch { }
            string log = _withLog.Checked ? LogTail() : null;

            _send.Enabled = false;
            _result.ForeColor = Dim;
            _result.Text = "Sending...";

            Task.Run(() =>
            {
                string problem;
                bool sent = Relay.SendFeedback(message, contact, server, mods, log, out problem);
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        if (sent)
                        {
                            DialogResult = DialogResult.OK;
                            Close();
                            return;
                        }
                        _send.Enabled = true;
                        _result.ForeColor = Color.FromArgb(220, 140, 120);
                        _result.Text = "Couldn't send it (" + problem + "). Your message is still here - "
                                     + "try again in a moment.";
                    }));
                }
                catch (InvalidOperationException) { }        // closed meanwhile
            });
        }

        /// <summary>The last LogTailBytes of the launcher log, or null if there is none.</summary>
        private static string LogTail()
        {
            try
            {
                if (!File.Exists(Program.LogFile)) return null;
                using (var fs = new FileStream(Program.LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long start = Math.Max(0, fs.Length - LogTailBytes);
                    fs.Seek(start, SeekOrigin.Begin);
                    var buf = new byte[fs.Length - start];
                    int got = 0;
                    while (got < buf.Length)
                    {
                        int n = fs.Read(buf, got, buf.Length - got);
                        if (n <= 0) break;
                        got += n;
                    }
                    string text = Encoding.UTF8.GetString(buf, 0, got);
                    // Started mid-line: drop the partial first line.
                    if (start > 0)
                    {
                        int nl = text.IndexOf('\n');
                        if (nl >= 0) text = text.Substring(nl + 1);
                    }
                    return text;
                }
            }
            catch { return null; }
        }

        /// <param name="server">The selected server, to start the Server box with; "" for none.</param>
        /// <param name="modsFor">Builds the mods list, given what the Server box says when SEND is pressed.</param>
        internal static void Ask(IWin32Window owner, string server, Func<string, string> modsFor)
        {
            using (var dlg = new FeedbackDialog(server, modsFor))
            {
                if (dlg.ShowDialog(owner) == DialogResult.OK)
                    MessageBox.Show(owner, "Thanks - your feedback has been sent.", "Feedback",
                                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
    }
}
