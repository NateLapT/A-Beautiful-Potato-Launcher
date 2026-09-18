// ---------------------------------------------------------------------------
//  Beautiful Potato Launcher
//
//  Finds Steam and the DayZ Experimental install, resolves the workshop mod
//  folders, and starts the game.
//
//  THE ONE THING THAT MATTERS, AND IT IS NOT OBVIOUS
//    The game MUST be started by DayZ_BE.exe. There is no "-BattlEye" launch
//    parameter - DayZ_x64.exe contains no such flag. Start the game any other
//    way (DayZ_x64.exe directly, or steam.exe -applaunch) and it runs fine with
//    the right mods, but BattlEye never attaches and the server drops you with
//        Warning (0x000400F0) - You were kicked off the game.
//        BattlEye (Game restart required)
//    DayZ_BE.exe carries a "-noBE" switch to turn BattlEye OFF, which is the
//    proof it is ON by default there. The argument form
//        DayZ_BE.exe 2 1 0 -exe DayZ_x64.exe <game args>
//    is taken from DayZLauncher.exe itself, which builds "{0} 1 0 -exe {1}".
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BeautifulPotatoLauncher
{
    internal sealed class Server
    {
        public string Name;
        public string Address;
        public int Port;
        public Mod[] Mods = new Mod[0];
        public string BannerResource;   // may be null
        public string Note;
    }

    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    internal sealed class MainForm : Form
    {
        // -------------------------------------------------------- servers --
        // MotoX first, as asked.
        private static readonly Server[] Servers =
        {
            new Server
            {
                Name    = "A Beautiful Potato Motocross [MotoX]",
                Address = "104.218.48.62",
                Port    = 4902,
                // Workshop ids, not just names - the launcher subscribes and
                // downloads by id when a mod is missing, and the numeric folder
                // under steamapps\workshop\content\221100 is the only thing that
                // reliably exists straight after a download.
                Mods    = new[]
                {
                    new Mod("@CF-Test",                     1625463737UL),
                    new Mod("@Community-Online-Tools-Test", 1618340505UL),
                    new Mod("@MotoX",                       3800710051UL),
                    new Mod("@BikeWheelies",                 3803129582UL),
                },
                BannerResource = "banner_motox.png",
                Note    = "4 mods"
            },
            new Server
            {
                Name    = "A Beautiful Potato Experimental [Chernarus]",
                Address = "104.218.48.62",
                Port    = 2402,
                Mods    = new Mod[0],
                BannerResource = null,
                Note    = "no mods"
            },
        };

        private const string GameExe = "DayZ_x64.exe";
        private const string BeExe   = "DayZ_BE.exe";
        private const string BeArgs  = "2 1 0";

        // Folder names Steam has used for the Experimental build.
        private static readonly string[] ExpFolders =
        {
            "DayZ Exp", "DayZ Exp129", "DayZExp", "DayZ Experimental"
        };

        private TextBox _name;
        private TextBox _log;
        private readonly List<Button> _buttons = new List<Button>();

        public MainForm()
        {
            Text = "Beautiful Potato Launcher";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(540, 880);
            BackColor = Color.FromArgb(28, 28, 30);
            ForeColor = Color.Gainsboro;
            Font = new Font("Segoe UI", 9f);
            try { Icon = LoadIcon(); } catch { /* icon is cosmetic */ }

            BuildUi();
        }

        // ------------------------------------------------------------- ui --
        private void BuildUi()
        {
            int y = 12;

            // Header reads left to right: potato, wordmark, DayZ.
            var potato = new PictureBox
            {
                Image = LoadImage("logo_potato.png"),
                SizeMode = PictureBoxSizeMode.Zoom,
                Bounds = new Rectangle(14, y + 6, 92, 92),
                BackColor = Color.Transparent
            };
            Controls.Add(potato);

            var title = new PictureBox
            {
                Image = LoadImage("logo_title.png"),
                SizeMode = PictureBoxSizeMode.Zoom,
                Bounds = new Rectangle(110, y + 18, 300, 64),
                BackColor = Color.Transparent
            };
            Controls.Add(title);

            // DayZ mark, with EXPERIMENTAL captioned underneath. The artwork is
            // transparent at the edges so it needs no backing panel.
            var dayz = new PictureBox
            {
                Image = LoadImage("dayz_logo.png"),
                SizeMode = PictureBoxSizeMode.Zoom,
                Bounds = new Rectangle(420, y, 108, 108),
                BackColor = Color.Transparent
            };
            Controls.Add(dayz);

            Controls.Add(new Label
            {
                Text = "E X P E R I M E N T A L",
                Bounds = new Rectangle(408, y + 104, 132, 16),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 7f, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = Color.Transparent
            });

            y += 132;
            Controls.Add(new Label
            {
                Text = "Choose a server",
                Bounds = new Rectangle(16, y, 300, 20),
                ForeColor = Color.FromArgb(150, 150, 155)
            });
            y += 24;

            foreach (var srv in Servers)
                y = AddServerCard(srv, y);

            // ---- player name ----
            y += 4;
            Controls.Add(new Label
            {
                Text = "In-game name (optional - blank keeps whatever DayZ has saved)",
                Bounds = new Rectangle(16, y, 460, 18),
                ForeColor = Color.FromArgb(150, 150, 155)
            });
            y += 20;
            _name = new TextBox
            {
                Bounds = new Rectangle(16, y, 300, 24),
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.Gainsboro,
                BorderStyle = BorderStyle.FixedSingle,
                Text = Settings.LoadName()
            };
            Controls.Add(_name);
            y += 34;

            _log = new TextBox
            {
                Bounds = new Rectangle(16, y, 508, ClientSize.Height - y - 16),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(18, 18, 20),
                ForeColor = Color.FromArgb(170, 200, 170),
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 8.5f)
            };
            Controls.Add(_log);

            Log("Ready.");
        }

        private int AddServerCard(Server srv, int y)
        {
            var card = new Panel
            {
                Bounds = new Rectangle(16, y, 508, srv.BannerResource != null ? 382 : 106),
                BackColor = Color.FromArgb(38, 38, 42),
                BorderStyle = BorderStyle.FixedSingle
            };

            int inner = 6;
            if (srv.BannerResource != null)
            {
                var banner = new PictureBox
                {
                    Image = LoadImage(srv.BannerResource),
                    SizeMode = PictureBoxSizeMode.Zoom,
                    // 498x272 keeps the source 1.83:1 aspect, so the MOTO-X
                    // title at the top of the artwork is never cropped away.
                    Bounds = new Rectangle(4, 4, 498, 272)
                };
                card.Controls.Add(banner);
                inner = 282;
            }

            card.Controls.Add(new Label
            {
                Text = srv.Name,
                Bounds = new Rectangle(10, inner + 4, 360, 20),
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                ForeColor = Color.White
            });
            card.Controls.Add(new Label
            {
                Text = "Port " + srv.Port + "  -  " + srv.Note,
                Bounds = new Rectangle(10, inner + 26, 360, 18),
                ForeColor = Color.FromArgb(150, 150, 155)
            });

            // Mods spelled out, so a player can see exactly what they need to
            // be subscribed to before they click.
            card.Controls.Add(new Label
            {
                Text = srv.Mods.Length == 0
                       ? "Mods: none required"
                       : "Mods: " + string.Join("   ", srv.Mods.Select(m => m.Name).ToArray()),
                Bounds = new Rectangle(10, inner + 48, 486, 32),
                ForeColor = Color.FromArgb(125, 155, 125),
                Font = new Font("Consolas", 8f)
            });

            var btn = new Button
            {
                Text = "CONNECT",
                Bounds = new Rectangle(378, inner + 4, 118, 40),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(70, 120, 70),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                Tag = srv
            };
            btn.FlatAppearance.BorderColor = Color.FromArgb(100, 160, 100);
            btn.Click += OnConnect;
            card.Controls.Add(btn);
            _buttons.Add(btn);

            Controls.Add(card);
            return y + card.Height + 10;
        }

        // ---------------------------------------------------------- launch --
        private void OnConnect(object sender, EventArgs e)
        {
            var srv = (Server)((Button)sender).Tag;
            SetBusy(true);
            _log.Clear();

            try
            {
                Launch(srv);
            }
            catch (Exception ex)
            {
                Log("ERROR: " + ex.Message);
                MessageBox.Show(ex.Message, "Beautiful Potato Launcher",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void Launch(Server srv)
        {
            Settings.SaveName(_name.Text.Trim());

            // A DayZ already running is itself a cause of "Game restart required":
            // the new launch just focuses the old process, which BattlEye never
            // attached to.
            if (IsRunning(GameExe))
            {
                Log("DayZ is already running.");
                MessageBox.Show(
                    "DayZ is already running.\r\n\r\n" +
                    "Close it completely - check Task Manager for DayZ_x64.exe and " +
                    "DayZ_BE.exe - then try again.\r\n\r\n" +
                    "Launching on top of a running game is itself a cause of " +
                    "\"Game restart required\".",
                    "Close DayZ first", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string steam = FindSteam();
            if (steam == null) throw new Exception("Could not find Steam.");
            Log("Steam : " + steam);

            string gameDir = FindGameDir(steam);
            if (gameDir == null)
                throw new Exception(
                    "Could not find DayZ Experimental.\r\n\r\nLooked for: " +
                    string.Join(", ", ExpFolders) + "\r\nunder " +
                    Path.Combine(steam, "steamapps", "common"));
            Log("Game  : " + gameDir);

            // Workshop content lives under the STABLE DayZ folder even for the
            // Experimental build - Steam ties workshop files to the stable app.
            string workshop = Path.Combine(steam, "steamapps", "common", "DayZ", "!Workshop");

            // Presence is judged by workshop id, not by folder name. A player who
            // subscribed but has never run the stable DayZ launcher has the files
            // but no !Workshop\@Name link, and the old name-only check called that
            // "missing" and sent them off for a download they already had.
            var missing = new List<Mod>();
            foreach (var mod in srv.Mods)
            {
                if (SteamWorkshop.IsInstalled(steam, mod.WorkshopId))
                    Log("  [ok]      " + mod.Name);
                else
                {
                    missing.Add(mod);
                    Log("  [MISSING] " + mod.Name);
                }
            }

            if (missing.Count > 0)
            {
                Log("");
                Log(missing.Count + " mod(s) missing - getting them from the Workshop.");
                using (var dl = new ModDownloadForm(steam, gameDir, missing.ToArray()))
                {
                    if (dl.ShowDialog(this) != DialogResult.OK)
                    {
                        Log("Download cancelled - not launching.");
                        return;
                    }
                }
                Log("All mods are present now.");
            }

            var modPaths = new List<string>();
            foreach (var mod in srv.Mods)
            {
                string path = ResolveModPath(steam, workshop, mod);
                if (path == null)
                    throw new Exception("Mod folder disappeared after download: " + mod.Name);
                modPaths.Add(path);
            }

            // -mod= is one argument, semicolon separated, no trailing separator.
            var args = new List<string>();
            if (modPaths.Count > 0)
                args.Add("\"-mod=" + string.Join(";", modPaths.ToArray()) + "\"");

            args.Add("-connect=" + srv.Address);
            args.Add("-port=" + srv.Port);

            string playerName = _name.Text.Trim();
            if (playerName.Length > 0) args.Add("\"-name=" + playerName + "\"");

            args.Add("-nolauncher");
            args.Add("-world=empty");

            string bePath = Path.Combine(gameDir, BeExe);
            if (!File.Exists(bePath))
                throw new Exception(BeExe + " not found in:\r\n" + gameDir +
                                    "\r\n\r\nWithout it BattlEye cannot attach and the " +
                                    "server will kick you.");

            // "-exe DayZ_x64.exe" is a RELATIVE name, so the working directory has
            // to be the game folder.
            string full2 = BeArgs + " -exe " + GameExe + " " + string.Join(" ", args.ToArray());
            Log("");
            Log("Launching via " + BeExe + " so BattlEye attaches:");
            Log("  " + BeExe + " " + full2);

            var psi = new ProcessStartInfo
            {
                FileName = bePath,
                Arguments = full2,
                WorkingDirectory = gameDir,
                UseShellExecute = true
            };
            Process.Start(psi);

            Log("");
            Log("Started. DayZ takes a minute or two to appear - be patient.");
            Log("If the server kicks you for BattlEye, tell Nate the BE_ARGS value");
            Log("(" + BeArgs + ") may need to be 0 or 1 instead.");
        }

        /// <summary>
        /// Turns a mod into the path handed to DayZ.
        ///
        /// Prefers !Workshop\@Name, because that is the name the player sees in
        /// the in-game mod list. Steam only ever writes the numeric folder, so a
        /// mod this launcher just downloaded has no such link yet - one is made
        /// here. Junctions need no administrator rights. If even that fails the
        /// numeric path loads the mod perfectly well, just under an ugly name -
        /// so a failure here is never worth stopping a launch over.
        /// </summary>
        private string ResolveModPath(string steam, string workshop, Mod mod)
        {
            string numeric = SteamWorkshop.ItemPath(steam, mod.WorkshopId);
            string linked  = Path.Combine(workshop, mod.Name);

            if (Directory.Exists(linked)) return linked;
            if (!Directory.Exists(numeric)) return null;

            try { Directory.CreateDirectory(workshop); } catch { }

            if (Junction.TryCreate(linked, numeric))
            {
                Log("  linked    " + mod.Name);
                return linked;
            }

            return numeric;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            SteamWorkshop.Shutdown();
            base.OnFormClosed(e);
        }

        // ----------------------------------------------------------- utils --
        private static string FindSteam()
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view))
                    using (var key = baseKey.OpenSubKey(@"Software\Valve\Steam"))
                    {
                        var path = key?.GetValue("SteamPath") as string;
                        if (!string.IsNullOrEmpty(path))
                        {
                            path = path.Replace('/', '\\');
                            if (Directory.Exists(Path.Combine(path, "steamapps"))) return path;
                        }
                    }
                }
                catch { /* try the next view */ }
            }

            string fallback = @"C:\Program Files (x86)\Steam";
            return Directory.Exists(Path.Combine(fallback, "steamapps")) ? fallback : null;
        }

        private static string FindGameDir(string steam)
        {
            string common = Path.Combine(steam, "steamapps", "common");
            return ExpFolders
                .Select(f => Path.Combine(common, f))
                .FirstOrDefault(d => File.Exists(Path.Combine(d, GameExe)));
        }

        private static bool IsRunning(string exeName)
        {
            string bare = Path.GetFileNameWithoutExtension(exeName);
            try { return Process.GetProcessesByName(bare).Length > 0; }
            catch { return false; }
        }

        private static Image LoadImage(string resourceName)
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
            {
                if (s == null) return null;
                // Copy out - Image keeps the stream alive otherwise.
                var ms = new MemoryStream();
                s.CopyTo(ms);
                ms.Position = 0;
                return Image.FromStream(ms);
            }
        }

        private static Icon LoadIcon()
        {
            return Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }

        private void SetBusy(bool busy)
        {
            foreach (var b in _buttons) b.Enabled = !busy;
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
            Application.DoEvents();
        }

        private void Log(string line)
        {
            _log.AppendText(line + Environment.NewLine);
            _log.SelectionStart = _log.TextLength;
            _log.ScrollToCaret();
            Application.DoEvents();
        }
    }

    // Remembers the player name between runs, in the user's own AppData.
    internal static class Settings
    {
        private static string File_
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "BeautifulPotatoLauncher");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "settings.txt");
            }
        }

        public static string LoadName()
        {
            try { return File.Exists(File_) ? File.ReadAllText(File_).Trim() : ""; }
            catch { return ""; }
        }

        public static void SaveName(string name)
        {
            try { File.WriteAllText(File_, name ?? ""); }
            catch { /* not worth interrupting a launch over */ }
        }
    }
}
