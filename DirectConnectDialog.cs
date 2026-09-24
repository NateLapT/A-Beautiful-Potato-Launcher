// ---------------------------------------------------------------------------
//  Join a server by address.
//
//  Needed because the master list does not hold everything: a brand new server,
//  one that has not reported in yet, or one deliberately unlisted will never
//  appear in the browser no matter how you filter it. Typing the address in is
//  the only way to reach those.
// ---------------------------------------------------------------------------

using System;
using System.Drawing;
using System.Windows.Forms;

namespace ABeautifulPotatoLauncher
{
    internal static class DirectConnectDialog
    {
        private static readonly Color Ink    = Color.FromArgb(24, 24, 27);
        private static readonly Color Panel2 = Color.FromArgb(45, 45, 50);

        public static bool Show(IWin32Window owner, out string host, out int port, out bool save)
        {
            host = "";
            port = 2302;
            save = false;

            using (var f = new Form())
            {
                f.Text = "Direct connect";
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.MinimizeBox = f.MaximizeBox = false;
                f.ClientSize = new Size(430, 196);
                f.BackColor = Ink;
                f.ForeColor = Color.Gainsboro;
                f.Font = new Font("Segoe UI", 9f);

                f.Controls.Add(new Label
                {
                    Text = "Join a server by address. Use the GAME port - the launcher works\r\n" +
                           "out the query port itself.",
                    Bounds = new Rectangle(16, 12, 400, 34),
                    ForeColor = Color.FromArgb(150, 150, 158)
                });

                f.Controls.Add(new Label
                {
                    Text = "IP address",
                    Bounds = new Rectangle(16, 56, 80, 22),
                    TextAlign = ContentAlignment.MiddleRight,
                    ForeColor = Color.Gainsboro
                });
                var ip = new TextBox
                {
                    Bounds = new Rectangle(104, 55, 200, 23),
                    BackColor = Panel2,
                    ForeColor = Color.Gainsboro,
                    BorderStyle = BorderStyle.FixedSingle
                };
                f.Controls.Add(ip);

                f.Controls.Add(new Label
                {
                    Text = "Port",
                    Bounds = new Rectangle(16, 88, 80, 22),
                    TextAlign = ContentAlignment.MiddleRight,
                    ForeColor = Color.Gainsboro
                });
                var pt = new TextBox
                {
                    Text = "2302",
                    Bounds = new Rectangle(104, 87, 90, 23),
                    BackColor = Panel2,
                    ForeColor = Color.Gainsboro,
                    BorderStyle = BorderStyle.FixedSingle
                };
                f.Controls.Add(pt);

                var chkSave = new CheckBox
                {
                    Text = "Save to Favourites",
                    Bounds = new Rectangle(104, 118, 200, 22),
                    ForeColor = Color.Gainsboro,
                    FlatStyle = FlatStyle.Flat,
                    Checked = true
                };
                f.Controls.Add(chkSave);

                var ok = new Button
                {
                    Text = "CONNECT",
                    Bounds = new Rectangle(216, 154, 100, 30),
                    DialogResult = DialogResult.OK,
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(178, 34, 34),
                    ForeColor = Color.White,
                    Font = new Font("Segoe UI", 9f, FontStyle.Bold)
                };
                var cancel = new Button
                {
                    Text = "Cancel",
                    Bounds = new Rectangle(324, 154, 90, 30),
                    DialogResult = DialogResult.Cancel,
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(60, 60, 64),
                    ForeColor = Color.White
                };
                f.Controls.Add(ok);
                f.Controls.Add(cancel);
                f.AcceptButton = ok;
                f.CancelButton = cancel;

                // "1.2.3.4:2402" in the address box should just work - pasted
                // or typed. Split when the box is left or CONNECT is pressed,
                // NOT on every keystroke: typing "127.0.0.1:2402" key by key
                // split at ":2", leaving port 2 and the rest in the wrong box.
                Action splitPort = () =>
                {
                    int c = ip.Text.LastIndexOf(':');
                    if (c <= 0) return;
                    string tail = ip.Text.Substring(c + 1).Trim();
                    int parsed;
                    if (!int.TryParse(tail, out parsed) || parsed <= 0 || parsed > 65535) return;
                    ip.Text = ip.Text.Substring(0, c).Trim();
                    pt.Text = parsed.ToString();
                };
                ip.Leave += (s, e) => splitPort();

                while (true)
                {
                    if (f.ShowDialog(owner) != DialogResult.OK) return false;

                    splitPort();
                    string h = ip.Text.Trim();
                    int p;
                    if (h.Length == 0)
                    {
                        MessageBox.Show(f, "Enter the server's IP address.", "Direct connect");
                        continue;
                    }
                    if (!int.TryParse(pt.Text.Trim(), out p) || p <= 0 || p > 65535)
                    {
                        MessageBox.Show(f, "That port is not valid.", "Direct connect");
                        continue;
                    }

                    host = h;
                    port = p;
                    save = chkSave.Checked;
                    return true;
                }
            }
        }
    }
}
