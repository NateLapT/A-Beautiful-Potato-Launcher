// ---------------------------------------------------------------------------
//  "An update is available" - the change log, and one button to install it.
//
//  Opened from the [UPDATE] button in the footer, never by itself: a launcher
//  that throws a window at someone on the way into a game is in the way. The
//  X in the corner is "not now"; the footer stays yellow as the reminder.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace ABeautifulPotatoLauncher
{
    internal static class UpdateDialog
    {
        private static readonly Color Ink    = Color.FromArgb(24, 24, 27);
        private static readonly Color Panel2 = Color.FromArgb(38, 38, 42);
        private static readonly Color Dim    = Color.FromArgb(150, 150, 158);
        private static readonly Color Gold   = Color.FromArgb(240, 200, 80);

        /// <param name="releases">Everything newer than this build, newest first.</param>
        public static void Show(IWin32Window owner, List<ReleaseInfo> releases)
        {
            if (releases == null || releases.Count == 0) return;
            var newest = releases[0];

            using (var f = new Form())
            {
                f.Text = "Update available";
                f.FormBorderStyle = FormBorderStyle.Sizable;
                f.StartPosition = FormStartPosition.CenterParent;
                f.MinimizeBox = false;
                f.MaximizeBox = false;          // the X is the only way out, as asked
                f.ShowInTaskbar = false;
                f.ClientSize = new Size(620, 480);
                f.MinimumSize = new Size(460, 340);
                f.BackColor = Ink;
                f.ForeColor = Color.Gainsboro;
                f.Font = new Font("Segoe UI", 9f);

                f.Controls.Add(new Label
                {
                    Text = "Version " + newest.Version + " is available",
                    Bounds = new Rectangle(16, 14, 588, 26),
                    Font = new Font("Segoe UI", 13f, FontStyle.Bold),
                    ForeColor = Gold,
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
                });
                f.Controls.Add(new Label
                {
                    Text = "You have " + Program.Version + ".  What changed:",
                    Bounds = new Rectangle(16, 42, 588, 20),
                    ForeColor = Dim,
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
                });

                var log = new RichTextBox
                {
                    Bounds = new Rectangle(16, 68, 588, 346),
                    ReadOnly = true,
                    BackColor = Panel2,
                    ForeColor = Color.Gainsboro,
                    BorderStyle = BorderStyle.None,
                    DetectUrls = true,
                    TabStop = false,
                    Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
                };
                log.LinkClicked += (s, e) => { try { Process.Start(e.LinkText); } catch { } };
                FillChangeLog(log, releases);
                f.Controls.Add(log);

                var status = new Label
                {
                    Bounds = new Rectangle(16, 432, 400, 30),
                    TextAlign = ContentAlignment.MiddleLeft,
                    ForeColor = Dim,
                    Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
                };
                f.Controls.Add(status);

                bool canSwap = Updater.CanSelfUpdate();
                var go = new Button
                {
                    Text = canSwap ? "UPDATE NOW" : "OPEN DOWNLOAD PAGE",
                    Bounds = new Rectangle(f.ClientSize.Width - 176, 432, 160, 32),
                    Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(60, 95, 60),
                    ForeColor = Color.White,
                    Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
                };
                go.FlatAppearance.BorderColor = Color.FromArgb(80, 120, 80);
                f.Controls.Add(go);

                if (!canSwap)
                    status.Text = "The launcher's folder is read-only, so it cannot update itself here.";

                go.Click += async (s, e) =>
                {
                    if (!canSwap)
                    {
                        try { Process.Start(newest.PageUrl.Length > 0 ? newest.PageUrl : Updater.ReleasesPage); }
                        catch { }
                        return;
                    }

                    go.Enabled = false;
                    f.ControlBox = false;       // no closing half way through a swap
                    status.ForeColor = Dim;
                    try
                    {
                        string file = await Updater.DownloadAsync(newest, pct =>
                        {
                            try { f.BeginInvoke((Action)(() => go.Text = "DOWNLOADING " + pct + "%")); }
                            catch { }
                        });
                        go.Text = "INSTALLING...";
                        Updater.ApplyAndRestart(file);
                        Application.Exit();
                    }
                    catch (Exception ex)
                    {
                        status.ForeColor = Color.FromArgb(230, 130, 130);
                        status.Text = "Update failed: " + ex.Message;
                        go.Text = "TRY AGAIN";
                        go.Enabled = true;
                        f.ControlBox = true;
                    }
                };

                UiCursors.ApplyTo(f);
                f.ShowDialog(owner);
            }
        }

        /// <summary>
        /// Every release between this build and the newest, newest first, so
        /// someone who skipped a version still sees what it changed. The notes
        /// are GitHub markdown; headings, bullets and bold are turned into
        /// something readable rather than shown as raw ## and **.
        /// </summary>
        private static void FillChangeLog(RichTextBox box, List<ReleaseInfo> releases)
        {
            var body = box.Font;
            var head = new Font("Segoe UI", 11f, FontStyle.Bold);
            var sub = new Font("Segoe UI", 9.5f, FontStyle.Bold);

            foreach (var r in releases)
            {
                Append(box, "v" + r.Version
                            + (r.Published > DateTime.MinValue ? "   " + r.Published.ToLocalTime().ToString("d MMMM yyyy") : "")
                            + (r.PreRelease ? "   (pre-release)" : "") + "\n", head, Gold);

                string notes = (r.Notes ?? "").Replace("\r\n", "\n").Trim();
                if (notes.Length == 0) notes = "(no notes for this release)";

                foreach (string raw in notes.Split('\n'))
                {
                    string line = raw.TrimEnd();
                    if (line.StartsWith("#"))
                        Append(box, line.TrimStart('#').Trim().Replace("**", "") + "\n", sub, Color.White);
                    else if (line.TrimStart().StartsWith("- ") || line.TrimStart().StartsWith("* "))
                    {
                        int indent = line.Length - line.TrimStart().Length;
                        Append(box, new string(' ', indent) + "  •  " + line.TrimStart().Substring(2).Replace("**", "") + "\n",
                               body, Color.Gainsboro);
                    }
                    else
                        Append(box, line.Replace("**", "") + "\n", body, Color.Gainsboro);
                }
                Append(box, "\n", body, Color.Gainsboro);
            }
            box.SelectionStart = 0;
            box.ScrollToCaret();
        }

        private static void Append(RichTextBox box, string text, Font font, Color colour)
        {
            box.SelectionStart = box.TextLength;
            box.SelectionLength = 0;
            box.SelectionFont = font;
            box.SelectionColor = colour;
            box.AppendText(text);
        }
    }
}
