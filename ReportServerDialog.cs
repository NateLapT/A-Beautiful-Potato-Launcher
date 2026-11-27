// ---------------------------------------------------------------------------
//  Report a server - the flag at the end of each server row.
//
//  The report goes to the same service the server list comes from, where
//  every report is read by a person before anything is done about it, and
//  reports of the same server are read together. What goes: the server
//  (address, name, map, and the player count it was claiming), the reason
//  picked, what the player wrote, and how to reach them if they say. Nothing
//  about any player on the server.
//
//  REVIEW. The same window is how a server's owner asks for it to be
//  reviewed - to be listed as Trusted, or to have a flag looked at again.
//  Ticking Review swaps the reasons for a website (one showing the server's
//  IP and name, so ownership can be checked), and a way to reach them
//  becomes required. It is filed with the server's reports, as reason
//  ReviewReason, so the reviewer sees both sides together.
// ---------------------------------------------------------------------------

using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ABeautifulPotatoLauncher
{
    internal sealed class ReportServerDialog : Form
    {
        private static readonly Color Ink = Color.FromArgb(24, 24, 27);
        private static readonly Color Panel2 = Color.FromArgb(38, 38, 42);
        private static readonly Color Dim = Color.FromArgb(150, 150, 158);
        private static readonly Color Warn = Color.FromArgb(220, 140, 120);

        /// <summary>The reasons offered. The relay groups reports by these words - keep them stable.</summary>
        internal static readonly string[] Reasons =
        {
            "Fake player count",
            "Misleading name or description",
            "Cheating or admin abuse",
            "Offensive name or content",
            "Scam or pay-to-win",
            "Something else"
        };

        /// <summary>The reason a review request is filed under. The relay checks for it - keep it stable.</summary>
        internal const string ReviewReason = "Review request";

        private readonly Row _row;
        private readonly CheckBox _review;
        private readonly Label _reasonsLabel, _websiteLabel, _websiteHint, _detailsLabel, _contactLabel, _note;
        private readonly RadioButton[] _reasons;
        private readonly TextBox _website, _details, _contact;
        private readonly Label _result;
        private readonly Button _send, _cancel;

        private ReportServerDialog(Row row)
        {
            _row = row;

            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            BackColor = Ink;
            ForeColor = Color.Gainsboro;
            Font = new Font("Segoe UI", 9f);

            Controls.Add(new Label
            {
                Text = StreamerMode.Name(row.Name, row.Endpoint),
                Bounds = new Rectangle(14, 12, 472, 20),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                UseMnemonic = false,
                AutoEllipsis = true
            });
            Controls.Add(new Label
            {
                Text = StreamerMode.Address(row.Endpoint) + (string.IsNullOrEmpty(row.Map) ? "" : "   -   " + row.Map)
                     // Streamer mode hides the player count, as the list does.
                     + (StreamerMode.On ? "" : "   -   " + row.Players + " / " + row.MaxPlayers + " players"),
                Bounds = new Rectangle(14, 34, 472, 18),
                ForeColor = Dim,
                UseMnemonic = false
            });

            _review = new CheckBox
            {
                Text = "Review - I run this server and would like it reviewed",
                Bounds = new Rectangle(14, 60, 472, 22),
                ForeColor = Color.Gainsboro
            };
            _review.CheckedChanged += (s, e) => Arrange();
            Controls.Add(_review);

            // ---- report: what's wrong ----
            _reasonsLabel = new Label { Text = "What's wrong with it?", ForeColor = Color.Gainsboro };
            Controls.Add(_reasonsLabel);
            _reasons = new RadioButton[Reasons.Length];
            for (int i = 0; i < Reasons.Length; i++)
            {
                _reasons[i] = new RadioButton { Text = Reasons[i], ForeColor = Color.Gainsboro };
                Controls.Add(_reasons[i]);
            }

            // ---- review: the owner's website ----
            _websiteLabel = new Label { Text = "Website that shows this server's IP and name:", ForeColor = Dim };
            Controls.Add(_websiteLabel);
            _website = Box(300);
            Controls.Add(_website);
            _websiteHint = new Label
            {
                Text = "Your server's page, store or Discord page - so we can see the server is yours.",
                ForeColor = Dim
            };
            Controls.Add(_websiteHint);

            // ---- both ----
            _detailsLabel = new Label { ForeColor = Dim };
            Controls.Add(_detailsLabel);
            _details = Box(4000);
            _details.Multiline = true;
            _details.AcceptsReturn = true;
            _details.ScrollBars = ScrollBars.Vertical;
            Controls.Add(_details);

            _contactLabel = new Label { ForeColor = Dim };
            Controls.Add(_contactLabel);
            _contact = Box(200);
            Controls.Add(_contact);

            _note = new Label { ForeColor = Dim };
            Controls.Add(_note);

            _result = new Label { ForeColor = Dim, UseMnemonic = false };
            Controls.Add(_result);

            _send = new Button
            {
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            _send.Click += OnSend;
            Controls.Add(_send);

            _cancel = new Button
            {
                Text = "Cancel",
                FlatStyle = FlatStyle.Flat,
                BackColor = Panel2,
                ForeColor = Color.White,
                DialogResult = DialogResult.Cancel
            };
            _cancel.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 76);
            Controls.Add(_cancel);
            CancelButton = _cancel;

            Arrange();
        }

        private static TextBox Box(int max)
        {
            return new TextBox
            {
                BackColor = Panel2,
                ForeColor = Color.Gainsboro,
                BorderStyle = BorderStyle.FixedSingle,
                MaxLength = max
            };
        }

        /// <summary>Lays the window out for a report, or - with Review ticked - for a review request.</summary>
        private void Arrange()
        {
            bool review = _review.Checked;
            SuspendLayout();
            int y = 90;

            _reasonsLabel.Visible = !review;
            foreach (var r in _reasons) r.Visible = !review;
            _websiteLabel.Visible = _website.Visible = _websiteHint.Visible = review;

            if (review)
            {
                _websiteLabel.SetBounds(14, y, 472, 18); y += 20;
                _website.SetBounds(14, y, 472, 24); y += 28;
                _websiteHint.SetBounds(14, y, 472, 18); y += 28;
            }
            else
            {
                _reasonsLabel.SetBounds(14, y, 472, 18); y += 22;
                for (int i = 0; i < _reasons.Length; i++)
                    _reasons[i].SetBounds(24 + (i % 2) * 236, y + (i / 2) * 24, 230, 22);
                y += ((_reasons.Length + 1) / 2) * 24 + 8;
            }

            _detailsLabel.Text = review
                ? "About the server - what it is, who runs it, and what you'd like reviewed:"
                : "Details - what you saw, and when (optional, but it helps):";
            _detailsLabel.SetBounds(14, y, 472, 18); y += 20;
            _details.SetBounds(14, y, 472, review ? 120 : 140); y += _details.Height + 8;

            _contactLabel.Text = review
                ? "Email or Discord name (required - so we can reply):"
                : "How to reach you, if we need to ask (optional) - a Discord name or an email:";
            _contactLabel.ForeColor = review ? Color.Gainsboro : Dim;
            _contactLabel.SetBounds(14, y, 472, 18); y += 20;
            _contact.SetBounds(14, y, 472, 24); y += 32;

            _note.Text = review
                ? "Requests are read by a person, who will get in touch."
                : "Reports are read by a person before anything is done.";
            _note.SetBounds(14, y, 472, 18); y += 24;

            _result.SetBounds(14, y, 270, 50);
            _send.SetBounds(review ? 276 : 296, y + 14, review ? 124 : 104, 30);
            _cancel.SetBounds(406, y + 14, 80, 30);

            Text = review ? "Request a review" : "Report server";
            _send.Text = review ? "REQUEST REVIEW" : "SEND REPORT";
            _send.BackColor = review ? Color.FromArgb(55, 100, 65) : Color.FromArgb(110, 55, 55);
            _send.FlatAppearance.BorderColor = review ? Color.FromArgb(80, 135, 90) : Color.FromArgb(150, 80, 80);
            _result.Text = "";

            ClientSize = new Size(500, y + 60);
            ResumeLayout();
        }

        private void OnSend(object sender, EventArgs e)
        {
            bool review = _review.Checked;
            string details = (_details.Text ?? "").Trim();
            string contact = (_contact.Text ?? "").Trim();
            string website = review ? (_website.Text ?? "").Trim() : "";
            string reason = null;

            if (review)
            {
                reason = ReviewReason;
                if (website.Length > 0 && (website.IndexOf('.') <= 0 || website.IndexOf(' ') >= 0))
                {
                    Problem("That website doesn't look right - paste its address.");
                    _website.Focus();
                    return;
                }
                if (contact.Length < 2)
                {
                    Problem("Add an email or Discord name, so we can reply.");
                    _contact.Focus();
                    return;
                }
            }
            else
            {
                foreach (var r in _reasons) if (r.Checked) reason = r.Text;
                if (reason == null) { Problem("Pick what's wrong with it first."); return; }
                if (reason == Reasons[Reasons.Length - 1] && details.Length == 0)
                {
                    Problem("Say what's wrong in Details.");
                    _details.Focus();
                    return;
                }
            }

            var row = _row;
            _send.Enabled = false;
            _result.ForeColor = Dim;
            _result.Text = "Sending...";

            Task.Run(() =>
            {
                string problem;
                bool sent = Relay.SendReport(row.Host, row.Port, row.QueryPort, row.Name, row.Map,
                                             row.Players, row.MaxPlayers, reason, details, contact, website,
                                             out problem);
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        if (sent) { DialogResult = DialogResult.OK; Close(); return; }
                        _send.Enabled = true;
                        Problem("Couldn't send it (" + problem + "). Try again in a moment.");
                    }));
                }
                catch (InvalidOperationException) { }        // closed meanwhile
            });
        }

        private void Problem(string text)
        {
            _result.ForeColor = Warn;
            _result.Text = text;
        }

        /// <summary>True when a report or review request was sent; <paramref name="review"/> says which.</summary>
        internal static bool Ask(IWin32Window owner, Row row, out bool review)
        {
            using (var dlg = new ReportServerDialog(row))
            {
                bool sent = dlg.ShowDialog(owner) == DialogResult.OK;
                review = dlg._review.Checked;
                return sent;
            }
        }
    }
}
