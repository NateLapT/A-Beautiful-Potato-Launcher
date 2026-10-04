// ---------------------------------------------------------------------------
//  Project Zomboid: the helper mod that fills in the account when the game is
//  started to join a server.
//
//  WHY A MOD AT ALL
//    The game's command line takes only +connect and +password. Its connect
//    window is MEANT to fill in the rest from the accounts it has saved for
//    the server - ServerConnectPopup:setServer walks the saved servers looking
//    for this one - but the match can never succeed:
//
//        if server:getIp() == self.ip and server:getPort() == self.port then
//
//    getPort() is a number (Server.getPort()I in the game's Java) and
//    self.port is text (the caller passes tostring(port)), and in Lua a
//    number never equals a string. So the window always opens empty, for any
//    +connect start including Steam invites. Measured: the launcher had the
//    chosen account first for the server, and the window was still blank.
//
//    The game only runs Lua from mods, so the fix is a small one: matched
//    properly, the account the player picked in the launcher is filled in,
//    with Steam Relay as chosen, and - when the game holds that account's
//    password - Connect sends the saved password the same way the game's
//    own server list does (it is a hash already, so it is not hashed again).
//
//  WHY IT CANNOT UPSET A SERVER
//    It is enabled for the MAIN MENU only (Zomboid\mods\default.txt). Joining
//    a server reloads Lua with that server's own mods (ConnectToServerState
//    calls ResetLua), so the helper is gone before a server could see it or
//    checksum it, and it touches nothing but the connect window anyway.
//
//  HOW THE LAUNCHER TELLS IT WHICH ACCOUNT
//    A one-line request in Zomboid\Lua\ - the one folder the game lets Lua
//    read - naming the address, the account and the relay choice. The mod
//    acts only when the address matches the window it is filling, and clears
//    the request once used, so an old one cannot leak into a later join.
//    Nothing here writes to the game's own database.
// ---------------------------------------------------------------------------

using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ABeautifulPotatoLauncher
{
    internal static class ZomboidJoinHelper
    {
        public const string ModId = "ABeautifulPotatoJoinHelper";
        private const string RequestFile = "ABPL_join.txt";

        private static string ModRoot { get { return Path.Combine(Zomboid.UserFolder, "mods", ModId); } }
        private static string DefaultMods { get { return Path.Combine(Zomboid.UserFolder, "mods", "default.txt"); } }

        private const string ModInfo =
            "name=A Beautiful Potato Launcher - Join Helper\n" +
            "id=" + ModId + "\n" +
            "description=Fills in the account chosen in A Beautiful Potato Launcher when the game is started " +
            "to join a server. Main menu only - joining a server switches to that server's own mods.\n";

        // Kept deliberately small, and in the game's own idiom: every call it
        // makes is one the game's MultiplayerUI:connectToServer makes.
        private const string Lua =
@"-- A Beautiful Potato Launcher - Join Helper. Main menu only.
--
-- The game's connect window (ServerConnectPopup) is meant to fill in a saved
-- account when the game is started with +connect, but it compares the saved
-- port (a number) with the port it was given (text), which never matches - so
-- it always opens empty. This matches them properly and fills in the account
-- the launcher asked for. Written by the launcher; rewritten when it changes.

require ""OptionScreens/ServerConnectPopup""

local REQUEST = """ + RequestFile + @"""

-- The launcher's request: ip, port, account name, relay (1/0), tab separated.
local function readRequest()
    local reader = getFileReader(REQUEST, false)
    if not reader then return nil end
    local line = reader:readLine()
    reader:close()
    if not line or line == """" then return nil end
    local ip, port, user, relay = string.match(line, ""^([^\t]*)\t([^\t]*)\t([^\t]*)\t([01])"")
    if not ip then return nil end
    return { ip = ip, port = port, user = user, relay = relay == ""1"" }
end

local function clearRequest()
    local writer = getFileWriter(REQUEST, true, false)
    if writer then writer:write(""""); writer:close() end
end

local function savedServer(ip, port)
    local found = nil
    for _, server in ipairs(getServerList()) do
        if server:getIp() == ip and tostring(server:getPort()) == tostring(port) then found = server end
    end
    return found
end

local function savedAccount(server, user)
    if not server then return nil end
    local accounts = server:getAccounts()
    for i = 0, accounts:size() - 1 do
        local account = accounts:get(i)
        if account:getUserName() == user then return account end
    end
    return nil
end

local function hasSavedPassword(account)
    return account and account:isSavePwd() and account:getPwd() and account:getPwd() ~= """"
end

local originalSetServer = ServerConnectPopup.setServer
function ServerConnectPopup:setServer(ip, port, passwordStr)
    originalSetServer(self, ip, port, passwordStr)
    self.abplServer, self.abplAccount = nil, nil

    local request = readRequest()
    if not request or request.ip ~= tostring(ip) or request.port ~= tostring(port) then return end
    clearRequest()

    local server = savedServer(ip, port)
    local account = savedAccount(server, request.user)
    self.abplServer, self.abplAccount = server, account

    if server and (not passwordStr or passwordStr == """") then
        self.serverPasswordEntry:setText(server:getServerPassword() or """")
    end
    self.usernameEntry:setText(request.user)
    self.passwordEntry:setText(hasSavedPassword(account) and account:getPwd() or """")
    if self.connectTypeEntry and getSteamModeActive() then
        self.connectTypeEntry.selected[1] = request.relay
    end
end

local originalClick = ServerConnectPopup.onOptionMouseDown
function ServerConnectPopup:onOptionMouseDown(button, x, y)
    local account, server = self.abplAccount, self.abplServer
    if button.internal == ""CONNECT"" and hasSavedPassword(account)
       and self.usernameEntry:getText() == account:getUserName()
       and self.passwordEntry:getInternalText() == account:getPwd() then
        -- The saved password is already a hash: sent as-is, exactly as the
        -- game's own server list does (MultiplayerUI:connectToServer).
        getCore():setAccountUsed(account)
        account:setLastLogonNow()
        updateAccountToAccountList(account)
        if getSteamModeActive() then steamReleaseInternetServersRequest() end
        stopSendSecretKey()
        getCore():setNoSave(false)
        local relay = getSteamModeActive() and self.connectTypeEntry and self.connectTypeEntry.selected[1] or false
        local localIP = getSteamModeActive() and server:getLocalIP() or """"
        ConnectToServer.instance:connect(self, server:getName(), account:getUserName(), account:getPwd(),
            self.ip, localIP, tostring(self.port), self.serverPasswordEntry:getInternalText(),
            relay, false, account:getAuthType())
        return
    end
    return originalClick(self, button, x, y)
end
";

        /// <summary>
        /// Installs or refreshes the helper mod, enables it for the main menu,
        /// and leaves the request for this join. Returns false, changing as
        /// little as possible, if any step fails - the game then simply asks.
        /// </summary>
        public static bool Prepare(string host, int port, string username, bool steamRelay, Action<string> log)
        {
            try
            {
                if (!Directory.Exists(Zomboid.UserFolder))
                {
                    log("  The game has not been run yet, so it will ask for the account itself.");
                    return false;
                }

                bool installed = Install();
                bool enabled = Enable();
                if (installed) log("  Installed the launcher's join helper mod in Zomboid\\mods (main menu only).");
                if (enabled) log("  Enabled the join helper for the main menu (Zomboid\\mods\\default.txt).");

                string line = Clean(host) + "\t" + port + "\t" + Clean(username) + "\t" + (steamRelay ? "1" : "0");
                string luaDir = Path.Combine(Zomboid.UserFolder, "Lua");
                Directory.CreateDirectory(luaDir);
                File.WriteAllText(Path.Combine(luaDir, RequestFile), line, new UTF8Encoding(false));

                log("  The game's connect window will open with account \"" + username + "\", Steam Relay "
                    + (steamRelay ? "on" : "off") + ".");
                return true;
            }
            catch (Exception ex)
            {
                log("  Could not set up the join helper (" + ex.Message + ") - the game will ask for the account.");
                return false;
            }
        }

        private static string Clean(string v)
        {
            return (v ?? "").Replace("\t", " ").Replace("\r", " ").Replace("\n", " ").Trim();
        }

        /// <summary>Writes the mod's files when missing or out of date. True when anything was written.</summary>
        private static bool Install()
        {
            // B42 layout: <mod>\42\mod.info and <mod>\42\media\..., plus an
            // (empty) common folder, which B42 expects to find.
            string v42 = Path.Combine(ModRoot, "42");
            string luaPath = Path.Combine(v42, "media", "lua", "client", "ABPL_JoinHelper.lua");
            Directory.CreateDirectory(Path.Combine(ModRoot, "common"));
            Directory.CreateDirectory(Path.GetDirectoryName(luaPath));

            bool wrote = WriteIfChanged(Path.Combine(v42, "mod.info"), ModInfo);
            wrote |= WriteIfChanged(luaPath, Lua.Replace("\r\n", "\n"));
            return wrote;
        }

        private static bool WriteIfChanged(string path, string content)
        {
            if (File.Exists(path) && File.ReadAllText(path) == content) return false;
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return true;
        }

        /// <summary>
        /// Adds the helper to the main menu's mod list, leaving every other
        /// line as it was. True when the file was changed.
        /// </summary>
        private static bool Enable()
        {
            string path = DefaultMods;
            string text = File.Exists(path)
                ? File.ReadAllText(path)
                : "VERSION = 1,\n\nmods\n{\n}\n\nmaps\n{\n}\n";

            if (Regex.IsMatch(text, @"mod\s*=\s*\\?" + ModId + @"\s*,", RegexOptions.IgnoreCase)) return false;

            var block = Regex.Match(text, @"(^|\n)\s*mods\s*\{");
            if (!block.Success)
                throw new InvalidDataException("default.txt has no mods block");

            if (File.Exists(path)) File.Copy(path, path + ".launcher-backup", true);

            int at = block.Index + block.Length;
            string nl = text.Contains("\r\n") ? "\r\n" : "\n";
            text = text.Insert(at, nl + "    mod = " + ModId + ",");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return true;
        }
    }
}
