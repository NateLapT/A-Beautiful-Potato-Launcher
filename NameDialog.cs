// ---------------------------------------------------------------------------
//  Asks for the in-game name, on the first join that does not have one.
//
//  WHY THIS EXISTS: DayZ is passed the name as "-name=" on the command line
//  and nothing else. With no name the game falls back to "Survivor", which is
//  what everybody else without a name is called too - servers full of them,
//  admins unable to tell one player from another, and the player themself
//  unable to find their own body.
//
//  So the name is asked for once, and pressing OK with the box empty is a
//  valid answer rather than an error: it means "you pick", and it produces
//  Spud1234. "Survivor" is the one answer that is refused, because it is the
//  default this dialog exists to get away from.
// ---------------------------------------------------------------------------

using System;
using System.Drawing;
using System.Windows.Forms;

namespace ABeautifulPotatoLauncher
{
    internal sealed class NameDialog : Form
    {
        private static readonly Color Ink = Color.FromArgb(24, 24, 27);
        private static readonly Color Panel2 = Color.FromArgb(38, 38, 42);
        private static readonly Color Dim = Color.FromArgb(150, 150, 158);

        /// <summary>The name DayZ uses when it is given none.</summary>
        internal const string DefaultName = "Survivor";

        private static readonly Random Dice = new Random();

        private readonly TextBox _box;
        private readonly Label _warn;
        private readonly string _suggestion;

        private NameDialog(string suggestion)
        {
            _suggestion = suggestion;

            Text = "What should DayZ call you?";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(430, 168);
            BackColor = Ink;
            ForeColor = Color.Gainsboro;
            Font = new Font("Segoe UI", 9f);

            Controls.Add(new Label
            {
                Text = "This is the name other players and admins see in game.",
                Bounds = new Rectangle(14, 14, 400, 18),
                ForeColor = Color.White
            });

            Controls.Add(new Label
            {
                Text = "Leave it empty and press OK to be called " + suggestion + ".",
                Bounds = new Rectangle(14, 34, 400, 18),
                ForeColor = Dim
            });

            Controls.Add(new Label
            {
                Text = "Name:",
                Bounds = new Rectangle(14, 66, 44, 20),
                ForeColor = Color.Gainsboro
            });

            _box = new TextBox
            {
                Bounds = new Rectangle(60, 63, 354, 24),
                BackColor = Panel2,
                ForeColor = Color.Gainsboro,
                BorderStyle = BorderStyle.FixedSingle,
                MaxLength = 32
            };
            Controls.Add(_box);

            _warn = new Label
            {
                Text = "",
                Bounds = new Rectangle(60, 90, 354, 18),
                ForeColor = Color.FromArgb(220, 140, 120),
                UseMnemonic = false
            };
            Controls.Add(_warn);

            var ok = new Button
            {
                Text = "OK",
                Bounds = new Rectangle(322, 122, 92, 30),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(60, 95, 60),
                ForeColor = Color.White
            };
            ok.FlatAppearance.BorderColor = Color.FromArgb(80, 120, 80);
            ok.Click += OnOk;
            Controls.Add(ok);

            AcceptButton = ok;

            // No cancel: the game is about to be launched and it needs a name
            // either way. Closing the window with the X is the same as pressing
            // OK - see Ask, which takes whatever the box ended up holding.
            ControlBox = true;
        }

        private void OnOk(object sender, EventArgs e)
        {
            string typed = (_box.Text ?? "").Trim();

            if (IsDefault(typed))
            {
                // Refused rather than quietly replaced, so the player knows
                // the name they chose is not the one they will be wearing.
                _warn.Text = "\"" + DefaultName + "\" is DayZ's default - please pick another.";
                _box.SelectAll();
                _box.Focus();
                return;
            }

            DialogResult = DialogResult.OK;
            Close();
        }

        /// <summary>The name that was settled on. Never empty, never the default.</summary>
        private string Chosen
        {
            get
            {
                string typed = (_box.Text ?? "").Trim();
                return typed.Length == 0 || IsDefault(typed) ? _suggestion : typed;
            }
        }

        /// <summary>"Survivor", in any casing, with or without stray spaces.</summary>
        internal static bool IsDefault(string name)
        {
            return name != null
                && string.Equals(name.Trim(), DefaultName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Spud plus four digits - Spud0417, Spud9930.</summary>
        internal static string Suggest()
        {
            lock (Dice) return "Spud" + Dice.Next(1000, 10000);
        }

        /// <summary>
        /// Asks, and always comes back with a usable name: what they typed,
        /// or Spud1234 when they left it empty, closed the window, or tried to
        /// keep the default.
        /// </summary>
        internal static string Ask(IWin32Window owner)
        {
            using (var dlg = new NameDialog(Suggest()))
            {
                dlg.ShowDialog(owner);
                return dlg.Chosen;
            }
        }
    }
}
