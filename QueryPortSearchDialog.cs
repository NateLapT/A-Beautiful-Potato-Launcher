using System;
using System.Drawing;
using System.Windows.Forms;

namespace ABeautifulPotatoLauncher
{
    internal static class QueryPortSearchDialog
    {
        private static readonly Color Ink = Color.FromArgb(24, 24, 27);
        private static readonly Color Panel2 = Color.FromArgb(45, 45, 50);

        public static bool Show(IWin32Window owner, int preferredPort, out int firstPort, out int lastPort)
        {
            firstPort = preferredPort > 0
                ? Math.Max(1, Math.Min(65534 - 15, preferredPort - 3))
                : 27015;
            lastPort = firstPort + 15;

            using (var form = new Form())
            {
                form.Text = "Search query ports";
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.StartPosition = FormStartPosition.CenterParent;
                form.MinimizeBox = form.MaximizeBox = false;
                form.ClientSize = new Size(390, 150);
                form.BackColor = Ink;
                form.ForeColor = Color.Gainsboro;
                form.Font = new Font("Segoe UI", 9f);

                form.Controls.Add(new Label
                {
                    Text = "Search query ports for this server. Replies must report its game port,\r\n"
                         + "so servers sharing an IP are kept separate.",
                    Bounds = new Rectangle(12, 12, 366, 34)
                });
                form.Controls.Add(new Label
                {
                    Text = "From",
                    Bounds = new Rectangle(20, 54, 42, 22),
                    TextAlign = ContentAlignment.MiddleRight,
                    ForeColor = Color.Gainsboro
                });
                var from = new NumericUpDown
                {
                    Bounds = new Rectangle(68, 54, 90, 23),
                    Minimum = 1,
                    Maximum = 65534,
                    Value = firstPort,
                    BackColor = Panel2,
                    ForeColor = Color.Gainsboro
                };
                form.Controls.Add(from);
                form.Controls.Add(new Label
                {
                    Text = "To",
                    Bounds = new Rectangle(174, 54, 32, 22),
                    TextAlign = ContentAlignment.MiddleRight,
                    ForeColor = Color.Gainsboro
                });
                var to = new NumericUpDown
                {
                    Bounds = new Rectangle(210, 54, 90, 23),
                    Minimum = 1,
                    Maximum = 65534,
                    Value = lastPort,
                    BackColor = Panel2,
                    ForeColor = Color.Gainsboro
                };
                form.Controls.Add(to);
                form.Controls.Add(new Label
                {
                    Text = "At most 32 ports are queried per search.",
                    Bounds = new Rectangle(68, 82, 280, 20),
                    ForeColor = Color.FromArgb(150, 150, 158)
                });

                var search = new Button
                {
                    Text = "SEARCH",
                    Bounds = new Rectangle(188, 112, 90, 28),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(178, 34, 34),
                    ForeColor = Color.White,
                    DialogResult = DialogResult.OK
                };
                var cancel = new Button
                {
                    Text = "Cancel",
                    Bounds = new Rectangle(288, 112, 90, 28),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(60, 60, 64),
                    ForeColor = Color.White,
                    DialogResult = DialogResult.Cancel
                };
                form.Controls.Add(search);
                form.Controls.Add(cancel);
                form.AcceptButton = search;
                form.CancelButton = cancel;

                while (true)
                {
                    if (form.ShowDialog(owner) != DialogResult.OK) return false;

                    int start = Decimal.ToInt32(from.Value);
                    int end = Decimal.ToInt32(to.Value);
                    if (end < start)
                    {
                        MessageBox.Show(form, "The last port must be at least the first port.", "Search query ports",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                        continue;
                    }
                    if (end - start + 1 > 32)
                    {
                        MessageBox.Show(form, "Choose a range of 32 ports or fewer.", "Search query ports",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                        continue;
                    }

                    firstPort = start;
                    lastPort = end;
                    return true;
                }
            }
        }
    }
}
