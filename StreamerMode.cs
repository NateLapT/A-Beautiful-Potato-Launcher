// Streamer mode (Settings): for players who stream the launcher. A viewer who
// can read the server name and address on screen can follow a streamer onto
// their server (stream sniping), and the search box and log say the same.
//
//   - a server's address and port are hidden everywhere they are drawn
//   - a server's name is replaced by the nickname the player gave it, or by
//     its playstyle words and a fixed tag ("PvE · x3 Loot  #KQ7")
//   - the search box and the log show ***** instead of their text
//
// Separately, "Hide the launcher from screen capture" asks Windows to leave
// the launcher's windows out of every screen capture - OBS, Discord, the
// Snipping Tool alike (SetWindowDisplayAffinity, WDA_EXCLUDEFROMCAPTURE,
// Windows 10 2004 and later). The windows still show normally on this PC's
// own screen; captures see them as missing (or black, on older Windows). A
// hardware capture card records the real screen, so it still sees them.
//
// Both are kept in streamer.txt beside the other launcher-wide settings
// (not per game).

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace ABeautifulPotatoLauncher
{
    internal static class StreamerMode
    {
        /// <summary>Hide addresses, shorten server names, mask the search box and the log.</summary>
        public static bool On { get; private set; }

        /// <summary>Leave the launcher's windows out of screen captures.</summary>
        public static bool HideFromCapture { get; private set; }

        public const string Mask = "*****";

        private static string FilePath { get { return Path.Combine(ServerStore.BaseDir, "streamer.txt"); } }

        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    bool val = line.Substring(eq + 1).Trim() == "1";
                    if (key.Equals("mode", StringComparison.OrdinalIgnoreCase)) On = val;
                    else if (key.Equals("capture", StringComparison.OrdinalIgnoreCase)) HideFromCapture = val;
                }
            }
            catch { }
        }

        public static void Save(bool on, bool hideFromCapture)
        {
            On = on;
            HideFromCapture = hideFromCapture;
            try { File.WriteAllText(FilePath, "mode=" + (on ? "1" : "0") + "\r\ncapture=" + (hideFromCapture ? "1" : "0") + "\r\n"); }
            catch { }
            ApplyCaptureToOpenWindows();
        }

        // ----------------------------------------------------------- text --

        /// <summary>
        /// The name as shown. In streamer mode: the nickname the player gave
        /// this server, or else its playstyle words and a fixed tag - see
        /// Anonymous. A capture card records the real screen, so hiding the
        /// window from capture can't stand in for this.
        /// </summary>
        public static string Name(string name, string endpoint)
        {
            if (!On) return name;
            string nick = Nickname(endpoint);
            return nick ?? Anonymous(name, endpoint);
        }

        // ------------------------------------------------------ nicknames --
        //
        // Per game (servers are), one "address<TAB>nickname" line each, in
        // nicknames.txt. Only shown in streamer mode.

        private static Dictionary<string, string> _nicks;
        private static string _nicksFor;

        private static string NickFile { get { return Path.Combine(ServerStore.Dir, "nicknames.txt"); } }

        private static Dictionary<string, string> Nicks()
        {
            string file = NickFile;
            if (_nicks != null && _nicksFor == file) return _nicks;
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(file))
                    foreach (string line in File.ReadAllLines(file))
                    {
                        int tab = line.IndexOf('\t');
                        if (tab <= 0) continue;
                        string nick = line.Substring(tab + 1).Trim();
                        if (nick.Length > 0) d[line.Substring(0, tab).Trim()] = nick;
                    }
            }
            catch { }
            _nicks = d;
            _nicksFor = file;
            return d;
        }

        /// <summary>The nickname given to this server, or null.</summary>
        public static string Nickname(string endpoint)
        {
            if (string.IsNullOrEmpty(endpoint)) return null;
            string n;
            return Nicks().TryGetValue(endpoint, out n) ? n : null;
        }

        /// <summary>Sets (or, with an empty one, removes) a server's nickname.</summary>
        public static void SetNickname(string endpoint, string nickname)
        {
            if (string.IsNullOrEmpty(endpoint)) return;
            var d = Nicks();
            nickname = (nickname ?? "").Replace("\t", " ").Replace("\r", " ").Replace("\n", " ").Trim();
            if (nickname.Length == 0) d.Remove(endpoint); else d[endpoint] = nickname;
            try
            {
                var lines = new List<string>();
                foreach (var kv in d) lines.Add(kv.Key + "\t" + kv.Value);
                File.WriteAllLines(NickFile, lines);
            }
            catch { }
        }

        // ------------------------------------------------------ anonymous --
        //
        // A server without a nickname shows what it is for, not who it is:
        // only the common playstyle words its name holds (PvE, x3 Loot,
        // Traders, 1PP ...), never the brand words that identify it, plus a
        // short tag worked out from its address. The tag is always the same
        // for the same server, so rows can be told apart and found again,
        // but it can't be turned back into a name or an address.

        private static readonly Regex Playstyle = new Regex(
            @"\b(PvPvE|PvP|PvE|RP|Role ?play|Hardcore|Vanilla\+?|Deathmatch|1PP|3PP|FPP|TPP|No ?KOS|KOS"
            + @"|Traders?|Bases?|Base ?Building|Raid(?:ing|s)?|Cars|Helis?|Events|Missions|Fresh ?Wipe|Wiped?"
            + @"|x\d+(?:\.\d+)?(?: ?Loot)?|Loot)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string Anonymous(string name, string endpoint)
        {
            var words = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(name))
                foreach (Match m in Playstyle.Matches(name))
                {
                    string w = Regex.Replace(m.Value, @"\s+", " ");
                    if (seen.Add(w)) words.Add(w);
                    if (words.Count == 5) break;
                }
            return (words.Count > 0 ? string.Join(" · ", words) : "Server") + "  #" + Tag(endpoint);
        }

        /// <summary>Three characters from the address (FNV-1a - the same on every PC and every run).</summary>
        public static string Tag(string endpoint)
        {
            uint h = 2166136261;
            foreach (char c in (endpoint ?? "").ToLowerInvariant()) { h ^= c; h *= 16777619; }
            const string letters = "ABCDEFGHJKLMNPQRSTUVWXYZ";      // no I or O, which read as 1 and 0
            return "" + letters[(int)(h % 24)] + letters[(int)(h / 24 % 24)] + (char)('2' + h / 576 % 8);
        }

        /// <summary>Asks for a server's nickname. Null when cancelled; "" removes it.</summary>
        public static string AskNickname(IWin32Window owner, string shownAs, string current)
        {
            using (var f = new Form())
            {
                f.Text = "Nickname this server";
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.MinimizeBox = f.MaximizeBox = false;
                f.StartPosition = FormStartPosition.CenterParent;
                f.ClientSize = new System.Drawing.Size(420, 150);
                f.BackColor = System.Drawing.Color.FromArgb(20, 20, 22);
                f.ForeColor = System.Drawing.Color.Gainsboro;
                f.Font = new System.Drawing.Font("Segoe UI", 9f);

                // Never the real name: the dialog is on stream too.
                f.Controls.Add(new Label
                {
                    Text = "Shown in streamer mode as:  " + shownAs + "\r\nGive it a name only you will recognize. Leave it empty to remove it.",
                    Bounds = new System.Drawing.Rectangle(14, 12, 400, 36),
                    ForeColor = System.Drawing.Color.FromArgb(150, 150, 158)
                });
                var box = new TextBox
                {
                    Text = current ?? "",
                    Bounds = new System.Drawing.Rectangle(14, 58, 392, 24),
                    BackColor = System.Drawing.Color.FromArgb(38, 38, 42),
                    ForeColor = System.Drawing.Color.Gainsboro,
                    BorderStyle = BorderStyle.FixedSingle,
                    MaxLength = 40
                };
                f.Controls.Add(box);

                Func<string, int, DialogResult, Button> btn = (text, x, result) =>
                {
                    var b = new Button
                    {
                        Text = text,
                        Bounds = new System.Drawing.Rectangle(x, 104, 90, 30),
                        FlatStyle = FlatStyle.Flat,
                        BackColor = System.Drawing.Color.FromArgb(60, 60, 64),
                        ForeColor = System.Drawing.Color.White,
                        DialogResult = result
                    };
                    b.FlatAppearance.BorderColor = System.Drawing.Color.FromArgb(75, 75, 82);
                    f.Controls.Add(b);
                    return b;
                };
                f.AcceptButton = btn("Save", 220, DialogResult.OK);
                f.CancelButton = btn("Cancel", 316, DialogResult.Cancel);

                return f.ShowDialog(owner) == DialogResult.OK ? box.Text.Trim() : null;
            }
        }

        /// <summary>An address as shown: hidden altogether.</summary>
        public static string Address(string endpoint)
        {
            if (!On || string.IsNullOrEmpty(endpoint)) return endpoint;
            return Mask;
        }

        // An IPv4 address with or without a port, and anything in quotes (what
        // the status bar says was searched for).
        private static readonly Regex Ip = new Regex(@"\b\d{1,3}(\.\d{1,3}){3}(:\d{1,5})?\b", RegexOptions.Compiled);
        private static readonly Regex Quoted = new Regex("\"[^\"\\r\\n]*\"", RegexOptions.Compiled);

        /// <summary>Free text (the status bar): addresses and quoted text masked.</summary>
        public static string Scrub(string text)
        {
            return Scrub(text, null, null);
        }

        /// <summary>Free text (a server's description): addresses and quoted text masked, and the server's own name replaced by what streamer mode shows for it.</summary>
        public static string Scrub(string text, string name, string endpoint)
        {
            if (!On || string.IsNullOrEmpty(text)) return text;
            if (!string.IsNullOrWhiteSpace(name) && name.Trim().Length > 2)
                text = Regex.Replace(text, Regex.Escape(name.Trim()), Name(name, endpoint).Replace("$", "$$"), RegexOptions.IgnoreCase);
            text = Ip.Replace(text, Mask);
            text = Quoted.Replace(text, "\"" + Mask + "\"");
            return text;
        }

        // ------------------------------------------------- screen capture --

        [DllImport("user32.dll")]
        private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

        private const uint WDA_NONE = 0x0, WDA_MONITOR = 0x1, WDA_EXCLUDEFROMCAPTURE = 0x11;

        private static readonly HashSet<IntPtr> Applied = new HashSet<IntPtr>();
        private static bool _hooked;

        /// <summary>
        /// Keeps every launcher window - the main one and each dialog as it
        /// opens - in or out of screen captures, as set. Called once at start-up.
        /// </summary>
        public static void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            Application.Idle += (s, e) =>
            {
                if (!HideFromCapture) return;
                foreach (Form f in Application.OpenForms)
                {
                    if (!f.IsHandleCreated || Applied.Contains(f.Handle)) continue;
                    SetCapture(f, true);
                }
            };
        }

        private static void ApplyCaptureToOpenWindows()
        {
            Applied.Clear();
            foreach (Form f in Application.OpenForms)
                if (f.IsHandleCreated) SetCapture(f, HideFromCapture);
        }

        private static void SetCapture(Form f, bool hide)
        {
            try
            {
                IntPtr h = f.Handle;
                // Windows before 10 2004 refuses EXCLUDEFROMCAPTURE; MONITOR
                // (captured as black) is the best it can do there.
                if (hide && !SetWindowDisplayAffinity(h, WDA_EXCLUDEFROMCAPTURE))
                    SetWindowDisplayAffinity(h, WDA_MONITOR);
                else if (!hide)
                    SetWindowDisplayAffinity(h, WDA_NONE);
                if (hide) Applied.Add(h);
            }
            catch { }
        }
    }
}
