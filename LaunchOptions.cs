// ---------------------------------------------------------------------------
//  The player's own extra game arguments - CPU cores, extra threads, memory,
//  a few switches, and anything else typed by hand - added to every launch.
//
//  Opened from LAUNCH OPTIONS on the filter panel. Kept per game (see
//  ServerStore.LoadLaunchOptions).
//
//  THE LAUNCHER'S OWN ARGUMENTS ARE NOT OVERRIDABLE. -connect, -port, -mod,
//  -name, -password, -nolauncher and -world are what make a join work at all;
//  a second copy typed into "other" would either be ignored or win, and
//  either way the join breaks in a way the player cannot see. They are
//  stripped from the free text, and the window says so.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace ABeautifulPotatoLauncher
{
    internal static class LaunchOptions
    {
        private static readonly Color Ink    = Color.FromArgb(24, 24, 27);
        private static readonly Color Panel2 = Color.FromArgb(38, 38, 42);
        private static readonly Color Dim    = Color.FromArgb(150, 150, 158);

        /// <summary>Set by the launcher itself; see the header.</summary>
        private static readonly string[] Reserved =
            { "connect", "port", "mod", "servermod", "name", "password", "nolauncher", "world" };

        /// <summary>The on/off switches offered as checkboxes: argument, label, tooltip.</summary>
        private static readonly string[][] Switches =
        {
            new[] { "noPause",   "Keep running in the background (-noPause)",
                    "The game keeps running and rendering when you alt-tab away." },
            new[] { "noSplash",  "Skip the splash screens (-noSplash)",
                    "Starts without the intro logos." },
            new[] { "skipIntro", "Skip the intro scene (-skipIntro)",
                    "Starts without the main-menu intro scene." },
            new[] { "window",    "Start in a window (-window)",
                    "Starts windowed instead of full screen." },
        };

        private static readonly string[] ThreadChoices = { "", "0", "1", "3", "5", "7" };

        // -------------------------------------------------------- arguments --

        /// <summary>
        /// The extra arguments to add to a launch, from what was saved. Empty
        /// when nothing has been set - a launch is then exactly as before.
        /// </summary>
        public static List<string> Arguments()
        {
            return Build(ServerStore.LoadLaunchOptions());
        }

        private static List<string> Build(Dictionary<string, string> o)
        {
            var args = new List<string>();
            int n;

            if (o.TryGetValue("cpuCount", out var cpu) && int.TryParse(cpu, out n) && n > 0)
                args.Add("-cpuCount=" + n);
            if (o.TryGetValue("exThreads", out var ex) && int.TryParse(ex, out n) && n >= 0)
                args.Add("-exThreads=" + n);
            if (o.TryGetValue("maxMem", out var mem) && int.TryParse(mem, out n) && n > 0)
                args.Add("-maxMem=" + n);

            foreach (var sw in Switches)
                if (o.TryGetValue(sw[0], out var on) && on == "1") args.Add("-" + sw[0]);

            if (o.TryGetValue("other", out var other))
                foreach (string a in SplitArgs(other))
                    if (!IsReserved(a) && !args.Any(x => SameArg(x, a))) args.Add(a);

            return args;
        }

        /// <summary>Splits typed text into arguments, keeping "quoted values" together.</summary>
        private static IEnumerable<string> SplitArgs(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) yield break;
            var cur = new System.Text.StringBuilder();
            bool quoted = false;
            foreach (char c in text)
            {
                if (c == '"') { quoted = !quoted; cur.Append(c); continue; }
                if (char.IsWhiteSpace(c) && !quoted)
                {
                    if (cur.Length > 0) { yield return cur.ToString(); cur.Clear(); }
                    continue;
                }
                cur.Append(c);
            }
            if (cur.Length > 0) yield return cur.ToString();
        }

        private static string ArgName(string arg)
        {
            string a = arg.Trim().Trim('"').TrimStart('-');
            int eq = a.IndexOf('=');
            return eq >= 0 ? a.Substring(0, eq) : a;
        }

        private static bool IsReserved(string arg)
        {
            return Reserved.Contains(ArgName(arg), StringComparer.OrdinalIgnoreCase);
        }

        private static bool SameArg(string a, string b)
        {
            return string.Equals(ArgName(a), ArgName(b), StringComparison.OrdinalIgnoreCase);
        }

        // ----------------------------------------------------------- window --

        /// <summary>Returns true when the options were saved.</summary>
        public static bool Show(IWin32Window owner)
        {
            var saved = ServerStore.LoadLaunchOptions();
            Func<string, string> get = k => saved.TryGetValue(k, out var v) ? v : "";

            using (var f = new Form())
            {
                f.Text = "Launch options";
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.MinimizeBox = f.MaximizeBox = false;
                f.ShowInTaskbar = false;
                f.ClientSize = new Size(560, 430);
                f.BackColor = Ink;
                f.ForeColor = Color.Gainsboro;
                f.Font = new Font("Segoe UI", 9f);

                f.Controls.Add(new Label
                {
                    Text = "Extra arguments added every time DayZ is started",
                    Bounds = new Rectangle(16, 12, 528, 24),
                    Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                    ForeColor = Color.White
                });
                f.Controls.Add(new Label
                {
                    Text = "Leave a box empty to use the game's own default.",
                    Bounds = new Rectangle(16, 38, 528, 18),
                    ForeColor = Dim
                });

                var tips = new ToolTip { AutoPopDelay = 20000 };
                int y = 68;

                Func<string, string, TextBox> numberRow = (label, hint) =>
                {
                    f.Controls.Add(new Label
                    {
                        Text = label, Bounds = new Rectangle(16, y + 3, 170, 20),
                        TextAlign = ContentAlignment.MiddleRight, ForeColor = Color.Gainsboro
                    });
                    var b = new TextBox
                    {
                        Bounds = new Rectangle(194, y, 90, 23), BackColor = Panel2,
                        ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle
                    };
                    b.KeyPress += (s, e) => { if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true; };
                    f.Controls.Add(b);
                    f.Controls.Add(new Label
                    {
                        Text = hint, Bounds = new Rectangle(292, y + 3, 252, 20), ForeColor = Dim
                    });
                    y += 32;
                    return b;
                };

                var cpu = numberRow("CPU cores (-cpuCount)", "this PC has " + Environment.ProcessorCount + " logical cores");
                cpu.Text = get("cpuCount");
                tips.SetToolTip(cpu, "How many CPU cores the game may use.");

                f.Controls.Add(new Label
                {
                    Text = "Extra threads (-exThreads)", Bounds = new Rectangle(16, y + 3, 170, 20),
                    TextAlign = ContentAlignment.MiddleRight, ForeColor = Color.Gainsboro
                });
                var threads = new ComboBox
                {
                    Bounds = new Rectangle(194, y, 90, 23), DropDownStyle = ComboBoxStyle.DropDownList,
                    BackColor = Panel2, ForeColor = Color.Gainsboro, FlatStyle = FlatStyle.Flat
                };
                threads.Items.AddRange(ThreadChoices.Select(c => (object)(c.Length == 0 ? "default" : c)).ToArray());
                int ti = Array.IndexOf(ThreadChoices, get("exThreads"));
                threads.SelectedIndex = ti < 0 ? 0 : ti;
                f.Controls.Add(threads);
                f.Controls.Add(new Label
                {
                    Text = "7 = all extra threads (file, texture, geometry)",
                    Bounds = new Rectangle(292, y + 3, 252, 20), ForeColor = Dim
                });
                tips.SetToolTip(threads, "Extra worker threads for loading files, textures and geometry.");
                y += 32;

                var mem = numberRow("Max memory MB (-maxMem)", "e.g. 8192 for 8 GB");
                mem.Text = get("maxMem");
                tips.SetToolTip(mem, "The most memory, in megabytes, the game may use.");

                y += 4;
                var checks = new List<CheckBox>();
                foreach (var sw in Switches)
                {
                    var c = new CheckBox
                    {
                        Text = sw[1], Bounds = new Rectangle(194, y, 340, 22),
                        Checked = get(sw[0]) == "1", FlatStyle = FlatStyle.Flat, Tag = sw[0]
                    };
                    tips.SetToolTip(c, sw[2]);
                    f.Controls.Add(c);
                    checks.Add(c);
                    y += 24;
                }

                y += 8;
                f.Controls.Add(new Label
                {
                    Text = "Other arguments", Bounds = new Rectangle(16, y + 3, 170, 20),
                    TextAlign = ContentAlignment.MiddleRight, ForeColor = Color.Gainsboro
                });
                var other = new TextBox
                {
                    Bounds = new Rectangle(194, y, 350, 23), BackColor = Panel2,
                    ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle,
                    Text = get("other")
                };
                f.Controls.Add(other);
                tips.SetToolTip(other, "Anything else, separated by spaces, e.g.  -high -noBenchmark");
                y += 30;

                var warn = new Label
                {
                    Bounds = new Rectangle(194, y, 350, 18), ForeColor = Color.FromArgb(225, 175, 90),
                    Font = new Font("Segoe UI", 8f)
                };
                f.Controls.Add(warn);
                y += 24;

                f.Controls.Add(new Label
                {
                    Text = "Added to each launch:", Bounds = new Rectangle(16, y, 528, 18), ForeColor = Dim
                });
                y += 20;
                var preview = new TextBox
                {
                    Bounds = new Rectangle(16, y, 528, 23), ReadOnly = true, BackColor = Panel2,
                    ForeColor = Color.FromArgb(140, 200, 140), BorderStyle = BorderStyle.FixedSingle,
                    TabStop = false
                };
                f.Controls.Add(preview);

                Func<Dictionary<string, string>> collect = () =>
                {
                    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        { "cpuCount", cpu.Text.Trim() },
                        { "exThreads", ThreadChoices[Math.Max(0, threads.SelectedIndex)] },
                        { "maxMem", mem.Text.Trim() },
                        { "other", other.Text.Trim() }
                    };
                    foreach (var c in checks) d[(string)c.Tag] = c.Checked ? "1" : "0";
                    return d;
                };

                Action refresh = () =>
                {
                    var args = Build(collect());
                    preview.Text = args.Count == 0 ? "(nothing - the game starts with its defaults)" : string.Join(" ", args);

                    var dropped = SplitArgs(other.Text).Where(IsReserved).ToList();
                    warn.Text = dropped.Count == 0 ? ""
                        : string.Join(" ", dropped) + (dropped.Count == 1 ? " is" : " are")
                          + " set by the launcher and will be ignored.";
                };
                cpu.TextChanged += (s, e) => refresh();
                mem.TextChanged += (s, e) => refresh();
                other.TextChanged += (s, e) => refresh();
                threads.SelectedIndexChanged += (s, e) => refresh();
                foreach (var c in checks) c.CheckedChanged += (s, e) => refresh();
                refresh();

                int by = f.ClientSize.Height - 46;
                var reset = new Button
                {
                    Text = "Reset", Bounds = new Rectangle(16, by, 90, 32), FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(55, 55, 60), ForeColor = Color.White
                };
                reset.Click += (s, e) =>
                {
                    cpu.Text = mem.Text = other.Text = "";
                    threads.SelectedIndex = 0;
                    foreach (var c in checks) c.Checked = false;
                };
                var save = new Button
                {
                    Text = "SAVE", Bounds = new Rectangle(f.ClientSize.Width - 206, by, 100, 32),
                    DialogResult = DialogResult.OK, FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(60, 95, 60), ForeColor = Color.White,
                    Font = new Font("Segoe UI", 9f, FontStyle.Bold)
                };
                var cancel = new Button
                {
                    Text = "Cancel", Bounds = new Rectangle(f.ClientSize.Width - 98, by, 82, 32),
                    DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(55, 55, 60), ForeColor = Color.White
                };
                f.Controls.Add(reset);
                f.Controls.Add(save);
                f.Controls.Add(cancel);
                f.AcceptButton = save;
                f.CancelButton = cancel;

                UiCursors.ApplyTo(f);
                if (f.ShowDialog(owner) != DialogResult.OK) return false;

                ServerStore.SaveLaunchOptions(collect());
                return true;
            }
        }
    }
}
