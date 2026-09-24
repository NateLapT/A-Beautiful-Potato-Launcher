// ---------------------------------------------------------------------------
//  The launcher installs itself. No MSI, no setup.exe.
//
//  WHERE: %LocalAppData%\Programs\A Beautiful Potato Launcher - the per-user
//  equivalent of Program Files, as VS Code and Discord use. Program Files needs
//  administrator to write, which would put a UAC prompt in front of every
//  update and stop the self-update swap (see Updater) from working at all.
//
//  HOW: run the downloaded exe from anywhere and it offers to install. Yes
//  copies it into the folder above, adds Start menu and desktop shortcuts, and
//  an entry in Settings > Apps (HKCU, so no administrator), then starts the
//  installed copy. From then on the installed copy updates itself in place.
//
//  Settings, favourites and the server cache live in %AppData%, not in the
//  program folder, so reinstalling or updating never touches them.
//
//  A developer build (anything under a bin\ folder) never asks.
// ---------------------------------------------------------------------------

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ABeautifulPotatoLauncher
{
    internal static class Installer
    {
        public const string AppName = "A Beautiful Potato Launcher";
        private const string ExeName = "ABeautifulPotatoLauncher.exe";
        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ABeautifulPotatoLauncher";
        private const string PrefsKey = @"Software\ABeautifulPotatoLauncher";

        public static string InstallDir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs", AppName);
            }
        }

        public static string InstalledExe { get { return Path.Combine(InstallDir, ExeName); } }

        private static string RunningExe
        {
            get { return Path.GetFullPath(Assembly.GetExecutingAssembly().Location); }
        }

        public static bool RunningInstalled
        {
            get { return string.Equals(RunningExe, Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase); }
        }

        private static bool IsDevBuild
        {
            get
            {
                string dir = Path.GetDirectoryName(RunningExe) ?? "";
                return dir.Split(Path.DirectorySeparatorChar)
                          .Any(p => p.Equals("bin", StringComparison.OrdinalIgnoreCase));
            }
        }

        /// <summary>
        /// Everything that happens before the main window. Returns true when
        /// this process should exit instead of opening the launcher - it has
        /// uninstalled, or it has handed over to the installed copy.
        /// </summary>
        public static bool BeforeStart(string[] args)
        {
            if (args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)))
            {
                Uninstall();
                return true;
            }

            if (RunningInstalled)
            {
                // Keeps Settings > Apps showing the right version after a
                // self-update, which only swaps the exe.
                Register();
                return false;
            }

            if (IsDevBuild || args.Any(a => a.Equals("--no-install", StringComparison.OrdinalIgnoreCase)))
                return false;

            string installed = InstalledVersion();
            if (installed == null && DontAsk()) return false;

            return Offer(installed);
        }

        // ------------------------------------------------------------ offer --

        /// <returns>true when the installed copy has been started.</returns>
        private static bool Offer(string installedVersion)
        {
            string mine = Program.Version;
            bool have = installedVersion != null;
            bool mineIsNewer = !have || Updater.IsNewer(mine, installedVersion);

            using (var f = new Form())
            {
                f.Text = have ? AppName + " is installed" : "Install " + AppName;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterScreen;
                f.MinimizeBox = f.MaximizeBox = false;
                f.ClientSize = new Size(520, 250);
                f.BackColor = Color.FromArgb(24, 24, 27);
                f.ForeColor = Color.Gainsboro;
                f.Font = new Font("Segoe UI", 9f);
                try { f.Icon = Icon.ExtractAssociatedIcon(RunningExe); } catch { }

                string headline = !have ? "Install " + AppName + " v" + mine + "?"
                                : mineIsNewer ? "Update the installed launcher to v" + mine + "?"
                                : "v" + installedVersion + " is already installed.";
                f.Controls.Add(new Label
                {
                    Text = headline,
                    Bounds = new Rectangle(16, 14, 488, 26),
                    Font = new Font("Segoe UI", 12f, FontStyle.Bold),
                    ForeColor = Color.White
                });
                f.Controls.Add(new Label
                {
                    Text = (have ? "Installed" : "Installs") + " to:\r\n" + InstallDir + "\r\n\r\n"
                         + "No administrator needed. Updates install themselves from there, and "
                         + "your settings and favourites are kept.",
                    Bounds = new Rectangle(16, 46, 488, 80),
                    ForeColor = Color.FromArgb(150, 150, 158)
                });

                var desktop = new CheckBox
                {
                    Text = "Desktop shortcut",
                    Checked = true,
                    Bounds = new Rectangle(16, 130, 200, 22),
                    Visible = mineIsNewer,
                    FlatStyle = FlatStyle.Flat
                };
                f.Controls.Add(desktop);

                var dontAsk = new CheckBox
                {
                    Text = "Don't ask again",
                    Bounds = new Rectangle(16, 154, 200, 22),
                    Visible = !have,
                    FlatStyle = FlatStyle.Flat,
                    ForeColor = Color.FromArgb(150, 150, 158)
                };
                f.Controls.Add(dontAsk);

                var go = new Button
                {
                    Text = !have ? "INSTALL" : mineIsNewer ? "UPDATE" : "OPEN INSTALLED",
                    Bounds = new Rectangle(250, 200, 140, 34),
                    DialogResult = DialogResult.OK,
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(60, 95, 60),
                    ForeColor = Color.White,
                    Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
                };
                var skip = new Button
                {
                    Text = "Run this copy",
                    Bounds = new Rectangle(398, 200, 106, 34),
                    DialogResult = DialogResult.Cancel,
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(55, 55, 60),
                    ForeColor = Color.White
                };
                f.Controls.Add(go);
                f.Controls.Add(skip);
                f.AcceptButton = go;
                f.CancelButton = skip;      // the X does the same

                if (f.ShowDialog() != DialogResult.OK)
                {
                    if (dontAsk.Checked) SetDontAsk();
                    return false;
                }

                try
                {
                    if (mineIsNewer) Install(desktop.Checked);
                    Process.Start(new ProcessStartInfo(InstalledExe)
                    {
                        UseShellExecute = false,
                        WorkingDirectory = InstallDir
                    });
                    return true;
                }
                catch (Exception ex)
                {
                    MessageBox.Show("The install failed:\r\n\r\n" + ex.Message
                                    + "\r\n\r\nThis copy will run instead.",
                                    AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
            }
        }

        // ---------------------------------------------------------- install --

        private static void Install(bool desktopShortcut)
        {
            Directory.CreateDirectory(InstallDir);
            string target = InstalledExe;

            // The installed launcher may be open right now. A running exe
            // cannot be overwritten but can be renamed - the same trick the
            // updater uses; the leftover .old is removed on its next start.
            if (File.Exists(target))
            {
                string old = target + ".old";
                try { File.Delete(old); } catch { }
                File.Move(target, old);
            }
            File.Copy(RunningExe, target);

            string config = RunningExe + ".config";
            if (File.Exists(config)) File.Copy(config, target + ".config", true);

            Shortcut(StartMenuLink, target);
            if (desktopShortcut) Shortcut(DesktopLink, target);
            Register();
        }

        /// <summary>The Settings > Apps entry. Per-user, so no administrator.</summary>
        private static void Register()
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
                {
                    k.SetValue("DisplayName", AppName);
                    k.SetValue("DisplayVersion", Program.Version);
                    k.SetValue("Publisher", "A Beautiful Potato");
                    k.SetValue("DisplayIcon", InstalledExe + ",0");
                    k.SetValue("InstallLocation", InstallDir);
                    k.SetValue("UninstallString", "\"" + InstalledExe + "\" --uninstall");
                    k.SetValue("URLInfoAbout", Updater.ReleasesPage);
                    k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    try
                    {
                        k.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024),
                                   RegistryValueKind.DWord);
                    }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>The version in Settings > Apps, or null if not installed.</summary>
        private static string InstalledVersion()
        {
            if (!File.Exists(InstalledExe)) return null;
            try
            {
                string v = FileVersionInfo.GetVersionInfo(InstalledExe).ProductVersion ?? "";
                int plus = v.IndexOf('+');
                return plus > 0 ? v.Substring(0, plus) : v;
            }
            catch { return "0"; }
        }

        // -------------------------------------------------------- uninstall --

        private static void Uninstall()
        {
            using (var f = new Form())
            {
                f.Text = "Uninstall " + AppName;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterScreen;
                f.MinimizeBox = f.MaximizeBox = false;
                f.ClientSize = new Size(460, 170);
                f.BackColor = Color.FromArgb(24, 24, 27);
                f.ForeColor = Color.Gainsboro;
                f.Font = new Font("Segoe UI", 9f);

                f.Controls.Add(new Label
                {
                    Text = "Remove " + AppName + " from this PC?",
                    Bounds = new Rectangle(16, 14, 428, 24),
                    Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                    ForeColor = Color.White
                });
                var data = new CheckBox
                {
                    Text = "Also delete my settings, favourites and server cache",
                    Bounds = new Rectangle(16, 56, 428, 22),
                    FlatStyle = FlatStyle.Flat
                };
                f.Controls.Add(data);

                var go = new Button
                {
                    Text = "UNINSTALL", Bounds = new Rectangle(226, 120, 120, 32),
                    DialogResult = DialogResult.OK, FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(120, 40, 40), ForeColor = Color.White,
                    Font = new Font("Segoe UI", 9f, FontStyle.Bold)
                };
                var cancel = new Button
                {
                    Text = "Cancel", Bounds = new Rectangle(354, 120, 90, 32),
                    DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(55, 55, 60), ForeColor = Color.White
                };
                f.Controls.Add(go);
                f.Controls.Add(cancel);
                f.AcceptButton = cancel;     // the safe one on Enter
                f.CancelButton = cancel;

                if (f.ShowDialog() != DialogResult.OK) return;

                try { File.Delete(StartMenuLink); } catch { }
                try { File.Delete(DesktopLink); } catch { }
                try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
                try { Registry.CurrentUser.DeleteSubKeyTree(PrefsKey, false); } catch { }

                string dirs = "\"" + InstallDir + "\"";
                if (data.Checked)
                {
                    string roaming = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "ABeautifulPotatoLauncher");
                    dirs += " \"" + roaming + "\"";
                }

                // This exe is inside the folder being removed, so the removal
                // runs after it exits: a hidden cmd waits two seconds first.
                Process.Start(new ProcessStartInfo("cmd.exe",
                    "/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q " + dirs)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
            }
        }

        // -------------------------------------------------------- shortcuts --

        private static string StartMenuLink
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                                    AppName + ".lnk");
            }
        }

        private static string DesktopLink
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                                    AppName + ".lnk");
            }
        }

        /// <summary>A .lnk through Windows' own WScript.Shell - no extra library.</summary>
        private static void Shortcut(string link, string target)
        {
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                object shell = Activator.CreateInstance(shellType);
                object lnk = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod,
                                                    null, shell, new object[] { link });
                var t = lnk.GetType();
                t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { target });
                t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk,
                               new object[] { Path.GetDirectoryName(target) });
                t.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk, new object[] { target + ",0" });
                t.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { AppName });
                t.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(lnk);
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
            catch { }
        }

        // ------------------------------------------------------ preferences --

        private static bool DontAsk()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(PrefsKey))
                    return k != null && Convert.ToInt32(k.GetValue("NoInstallPrompt", 0)) == 1;
            }
            catch { return false; }
        }

        private static void SetDontAsk()
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(PrefsKey))
                    k.SetValue("NoInstallPrompt", 1, RegistryValueKind.DWord);
            }
            catch { }
        }
    }
}
