# A Beautiful Potato Launcher

A server browser and launcher for **DayZ** (stable and Experimental) and
**Project Zomboid**. One small Windows exe - no installer needed, no account,
nothing to configure. Pick the game at the top left; the window switches over
in place.

Download the latest `ABeautifulPotatoLauncher.exe` from
[Releases](../../releases/latest). The launcher tells you when a newer version
is out and can update itself.

## What it does

**Both games**
- Browses Steam's full master list - past Steam's 10,000-server limit - and
  searches and filters it instantly: name, map, region, country, players, ping,
  password, game modes (PvP, PvE, RP...), and the mods a server runs.
- The list, with every server's mods and description, is downloaded in a
  second or two from `serverlist.beautifulpotato.com`, which keeps a copy of
  Steam's list up to date. Nothing about you is sent - just a request for the
  file. If it can't be reached, the launcher asks Steam itself as before.
- Live player counts and pings for the servers on screen; favourites, recent,
  friends and LAN tabs.
- Shows each server's required mods and whether you have them, with Sub,
  Repair (Verify for Project Zomboid) and Remove right in the list.
- Steam shows the game you are browsing, and the game is started under its own
  app id.

**DayZ**
- Stable and Experimental servers in one list; the right build is launched for
  each server.
- Subscribes to missing workshop mods and checks installed ones are current
  before joining - no "manual setup may be required".
- Launches through BattlEye with the full mod list, your name and the server
  password.
- Hides fake and redirect-farm servers.

**Project Zomboid**
- Version column and version filter - your installed version is shown in green.
- Matches each server's mod ids to their Steam Workshop items so they can be
  subscribed or verified ahead of joining. (The game also downloads anything
  missing itself when it connects.)
- Join window: server password, which saved account to use, and Steam Relay.

## Project Zomboid: the join helper mod

Project Zomboid only accepts the server address and server password on its
command line. Its connect window is meant to fill in your saved account for
the server, but it never manages to when the game is started that way, so the
launcher installs a tiny helper mod to do it:

- `%USERPROFILE%\Zomboid\mods\ABeautifulPotatoJoinHelper` - the mod itself.
- One line added to `%USERPROFILE%\Zomboid\mods\default.txt` so it is enabled
  **for the main menu only** (a backup is kept as `default.txt.launcher-backup`).
- `%USERPROFILE%\Zomboid\Lua\ABPL_join.txt` - which account to fill in for the
  next join; cleared as soon as it is used.

Joining a server reloads the game with that server's own mods, so the helper
is never active on a server and cannot affect its checks. To remove it, delete
that folder and its line in `default.txt`.

The launcher **reads** the game's saved server list to show your accounts. It
never reads, shows or stores your account passwords - the game keeps those
itself.

## Where it keeps its data

- `%APPDATA%\ABeautifulPotatoLauncher` - favourites, cached server lists and
  settings (Project Zomboid's in a `zomboid` folder inside it).
- `logs` beside the exe (or `%LOCALAPPDATA%\A Beautiful Potato Launcher` when
  that folder is not writable).

## Disclaimer

A Beautiful Potato Launcher is an unofficial third-party launcher made by
community members. It is not endorsed by, affiliated with, or sponsored by
Bohemia Interactive a.s. or The Indie Stone. All trademarks are the property of
their respective owners.

## License

MIT - see [LICENSE](LICENSE). Changes are listed in [CHANGELOG.md](CHANGELOG.md).
