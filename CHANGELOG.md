# Changelog

All notable changes to A Beautiful Potato Launcher.

**Every change goes in here.** Add an entry under the top (current version)
section as part of the work, not afterwards — an undocumented change is the one nobody can explain in
six months.

Format: newest first. Each entry says what changed and, where it matters, *why*
— particularly for the screening rules, which are full of thresholds that look
arbitrary until you know what they were measured against.

> Commits before 2026-09-20 have messages like `20260919` and `Cleanup`, so the
> history below starts where the work became traceable. Earlier entries are
> reconstructed from the code and are necessarily thin.

---

## [0.3] — current build (pre-release)

### Fixed

- **A server could be launched twice by clicking too fast.** Double-clicking a
  row launches, and so does CONNECT, and for the first few seconds nothing on
  screen has changed - so an impatient second click started a second copy of
  DayZ. The damage outlives the mistake: BattlEye's launcher is still waiting
  on the second one, so closing the first game makes it start the game AGAIN.
  A launch now buys twenty seconds of quiet. Another launch in that window is
  refused **silently** - a dialog would be one more thing to click away from
  someone already clicking too fast, and the launch they asked for is happening
  anyway - with the reason written to the log. CONNECT reads "STARTING..." and
  the server rows are drawn dark grey to say "not now". Only the text: the row
  banding and the panel behind it are untouched, and the list stays live so it
  can still be scrolled - the double-click it would otherwise have swallowed is
  already refused at the door. Twenty seconds because DayZ's own window is on
  its way up by then. Pressing CANCEL ends the quiet period immediately:
  somebody who has just cancelled is entitled to pick another server at once.
  Tested on the real members: quiet for a measured 20.0 s, CONNECT disabled
  throughout and restored by itself, row text 88,88,94 during and 220,220,220
  after with the background unchanged at 30,30,34, and cancel clearing it
  early.
- **Cancel could not catch the game in time, and DayZ loaded anyway.** It
  killed once, at the instant it was pressed - but the launcher starts
  BattlEye's launcher, BattlEye starts the game and exits, and the gap between
  those is seconds long. Press Cancel in that gap and there is nothing running
  under either name to kill, so nothing was, and DayZ carried on loading.
  Cancel now arms a watch rather than firing once: whatever is running is
  killed immediately, and anything that appears afterwards is killed as it
  appears, for up to two minutes. The window stays up saying "Cancelling..."
  while it waits - closing it while DayZ was still coming up would have been a
  lie - and changes to "Stopped." once it has caught the game, then closes.
  Tested against the exact race, with a stand-in process: Cancel pressed with
  nothing running, the process started three seconds later, killed by the
  watch, "DayZ was stopped." logged and the window closed itself.
- **The additional mods folder is searched properly, not one level deep.** It
  only ever listed the folders directly inside it, so a mod kept the way a
  working tree keeps them - `P:\Published\COT\@COT_dzpmtest` - was invisible,
  and every server asking for it said "NOT FOUND in your mod folders" while the
  mod sat on disk. It now walks up to four levels down, taking any folder
  named `@Something` or holding an `addons` folder or a manifest, and does not
  descend into a mod it has already found. Steam's own workshop folder is still
  read flat, since it is one level of numbered folders and walking into them
  would be wasted work. Measured on a real tree: 37 mods under `P:\Published`,
  including the three nested under `COT\` and `Testing\`, and nothing from
  inside any mod's `addons`.
- **The launcher vanished when joining a server** - no window, no error, the
  log stopping mid-sequence at "closing the session as app 221100" with no
  crash entry after it. The app-id switch tore the Steam session down and only
  then told the browser to let go of its query, so cancelling and releasing
  that query went through an interface whose session no longer existed. That is
  an access violation, and .NET Framework does not turn those into exceptions -
  the process is simply gone, which is why nothing was written to the log or
  to the crash handler. It survived every test until a REFRESH happened to be
  in flight, because with no query active there was nothing to release. The
  browser now lets go BEFORE the session closes, and `Stop` checks the
  interface pointer as well as the query handle. Proved both ways against a
  live Steam client with a query genuinely in flight (10,000 servers, not yet
  done): the old order dies with `AccessViolationException` in
  `SteamServerList.Stop`, the new one completes the whole cycle - switch,
  rebind, new query returning 187 Experimental servers, and back to 221100.
- **The Hide button on the starting window was clipped along its top edge.**
  The note above it was 34 px high where the text needed 26, and the extra
  reached down over the button - a label added before a control sits on top of
  it in WinForms z-order, so it painted over the button's top edge. The note is
  now sized to its text, and on the Beautiful Potato version the button sits
  level with the potato instead of stranded below it.
- **Steam no longer counts play time for both DayZ builds at once.** The
  launcher has to claim DayZ (221100) for its own Steam session, because that
  is the only way to reach the workshop, and it keeps that session open on
  purpose - the browser needs it. Join an Experimental server and the game
  announces itself as 1024020 while the launcher is still announcing 221100,
  so Steam sees DayZ *and* DayZ Experimental running and adds time to both for
  as long as the launcher is open. Once the game is started, the launcher's
  session now moves onto the build actually being played, leaving one game
  running. A session's app id cannot be changed, so it is closed and re-opened
  (`SteamWorkshop.SwitchApp`): every delegate bound to the old session is
  dropped and the server browser is told to forget its interface pointer, which
  belonged to the session that just ended - and a new one is bound in the same
  breath, so the player sees nothing: the launcher never closes, the window
  does not flicker, and live player counts keep working without anyone
  pressing REFRESH. Anything that touches the workshop - verifying,
  downloading, the Mod Manager - calls `EnsureWorkshopApp` first, which moves
  the session back to 221100, because mods belong to 221100 whichever build is
  played. Verified in one process against a live Steam client: browser
  connected, session re-opened as 1024020, browser rebound, session back to
  221100, browser still connected.
- **The launcher no longer re-downloads perfectly good mods when it cannot
  reach Steam.** Seen in a real log: a player restarted the launcher as
  administrator, pressed CONNECT without pressing REFRESH first, and all seven
  of the server's mods - every one of them already `[ok]` on the timestamp
  check - came back `[WAITING]` and were sent to the download window, which
  then failed. The Steam API is only initialised when something asks for it,
  and pressing CONNECT straight after starting the launcher asks for nothing;
  the verification step then read its own silence as "still updating" for every
  mod. It now initialises Steam itself, and when Steam cannot be reached it
  says so and lets the timestamps stand - they had already passed every mod in
  the list - so the join goes ahead instead of stalling behind a download that
  was never needed.
- **The game is found on any Steam library drive, so the launcher no longer
  needs to be "run as administrator".** It looked for DayZ in exactly one
  place, `<SteamPath>\steamapps\common`, so anyone whose DayZ sits on a second
  drive was told the build was not installed - and since the workshop folder
  was derived from the same root, their mods read as missing too. Steam's
  `steamapps\libraryfolders.vdf` is now read (see `SteamLibraries.cs`) and every
  library is searched; the Steam path the rest of the launcher works from is
  the library that actually holds the game. The vdf is world-readable, so no
  elevation is involved. Running elevated never fixed this - it only appeared
  to, because an elevated run usually coincided with other things working.
  When the build really is absent, the error now lists every folder searched.
- **Steam's location is also read from the machine-wide registry key** when the
  per-user one is missing - Steam installed but never started - or when it
  belongs to a different account, which is exactly what happens when someone
  right-clicks "Run as administrator" and enters another user's credentials.
  Both keys are readable without elevation.
- **The logs and `serverdata` folders fall back to
  `%LOCALAPPDATA%\A Beautiful Potato Launcher`** when the folder holding the
  executable cannot be written to - the launcher dropped into `Program Files`,
  typically. Both used to be created beside the exe unconditionally: the log
  silently vanished (so "send me your log" produced nothing) and the
  diagnostics dump threw. Beside the exe is still preferred and still used
  wherever it works, tested by writing a file rather than by trusting
  `CreateDirectory`. See `AppPaths.cs`.

### Added

- **A red Cancel button on the starting window**, beside Hide. DayZ takes a
  minute or two to appear, and until now a wrong server or a change of mind
  during that minute meant waiting for the game to finish loading just to quit
  it. Cancel kills what the launcher started - BattlEye's launcher - and the
  game itself if BattlEye has got that far. It is deliberately a stronger red
  than the muted one the other dialogs use for "no thanks", because it stops
  something already under way and must not be mistaken for Hide, which only
  gets the window out of the way. Esc still hides rather than cancels. Once the
  game is up the button disappears: the window is about to close itself, and a
  button that kills a running game is not what anyone expects to find there.
- **"NOT FOUND - click to say where it is".** A local mod the launcher cannot
  place used to be a dead end: a red line in the mod panel and nothing to do
  about it. Clicking that row now asks where the mod is, with the path and a
  Browse button, and says what it makes of the folder as it is typed - whether
  it has an `addons` folder in it, or whether the `@Thing` you meant is one
  level further in, which is the usual mistake. The answer is kept as an
  ordinary mod override, the same mechanism that points a workshop mod at a
  local build, so the command line already knew how to use it and it is asked
  once rather than every join. A join that hits a missing local mod asks the
  same question instead of quietly dropping the mod and letting the server do
  the explaining with a kick.
- **An egg for our own servers.** Join anything with "A Beautiful Potato" in
  its name and the starting window grows a line along the bottom - **You Are A
  Beautiful** followed by the potato himself. Matched on the name rather than
  on a list of addresses, because addresses move and the name does not, and a
  community server that happens to be called something similar getting the
  kind word too is no loss. Everywhere else the window is exactly as it was.
- **A "Starting DayZ..." window while the game gets on its feet**, with the
  server's name, a `||||||||..........` meter and large text. From the moment
  the launcher hands over to BattlEye, DayZ can take a minute or two to put
  anything on screen; all the launcher said about it was one line in the log
  panel, which is small and goes unread, so players read the silence as failure
  and pressed CONNECT again - starting a second copy and making it worse. It
  is modeless, so the launcher stays usable behind it, and Esc or Hide dismiss
  it. **The meter is honest about being a guess:** DayZ reports no progress, so
  it fills against the clock over about 75 seconds and deliberately stops at
  nine tenths rather than sitting full while nothing happens; past that it says
  a first launch or one after an update is slower. What is real is the finish -
  the game's own process is watched for, and that is what fills the bar, changes
  the headline to "DayZ is running." and closes the window 2.5 seconds later.
  Verified on screen in both states, and the close measured at 1.9 s after the
  process was seen.
- **A name is settled before the first join, and it is never "Survivor".** DayZ
  takes the in-game name from `-name=` on the command line and nowhere else,
  so a player who never set one is called Survivor - as is everyone else who
  never set one, which makes a server unreadable to its admins and a player
  unable to find their own body. On the first join with no name, a dialog asks
  for one. Pressing OK with the box empty is a real answer meaning "you pick",
  and produces `Spud` plus four digits - `Spud7575`. "Survivor" in any casing
  is refused outright, with the reason shown, rather than quietly swapped, so
  nobody walks away thinking they are wearing a name they are not. Whatever is
  settled on is saved and used from then on until they change it.
- **The title bar says when the launcher is elevated**: *A Beautiful Potato
  Launcher   (elevated User: Administrator)*. Elevation decides who owns every
  file the launcher and the game write from that point on, and there was
  otherwise nothing on screen to show it.
- **"Unable to locate a running instance of Steam" is caught before the game
  shows it, and explained.** That box is DayZ's own - `DayZ_x64.exe` calls
  `SteamAPI_IsSteamRunning()` on startup and puts up its own message when the
  answer is no - so its wording cannot be changed from here. The launcher now
  asks the same export, through the same `steam_api64.dll` the game will load,
  before starting anything. **Reproduced and measured:** with Steam started as
  administrator and the launcher not, `SteamAPI_IsSteamRunning()` returns
  false - even though `SteamAPI_Init` in the launcher itself still succeeds,
  which is why the browser works while the game refuses to start.
- **A choice when that happens, instead of a dead end.** The launcher explains
  the cause and offers both ways out: *No* - close Steam and start it normally,
  the fix that lasts, which is what the status bar then says; or *Yes* -
  restart the launcher as administrator so it matches Steam, with the game
  inheriting that. The second is quicker and the dialog says what it costs
  (everything downloaded keeps belonging to the administrator account). "Close
  Steam and start it normally" is the default button. A dismissed UAC prompt
  changes nothing and says so.
- **The launcher tells the player when Steam is running as administrator, and
  to close it and start it normally.** Reported from the wild: with Steam
  elevated, launching fails with a Windows box titled with the path to
  `DayZ_x64.exe` - *"Windows cannot access the specified device, path, or
  file"* - and mod downloads fail too. Everything an elevated Steam writes, the
  game and every mod, belongs to the administrator account, and the player's
  own account can then be refused when it tries to run or update those files.
  `Elevation.cs` reads both processes' tokens, the advice names the fix (close
  Steam, start it without "Run as administrator", and Verify Integrity if the
  files are already owned wrongly), and every log records
  `Elevation: launcher normal, Steam ADMINISTRATOR`. The reverse - elevated
  launcher, normal Steam - is called out too, since it leaves
  administrator-owned files in the player's own mod folders. Detection needs no
  elevation itself.
- **A join checks the game can actually be run before starting it.** The
  Windows box above comes from BattlEye, which starts `DayZ_x64.exe` after the
  launcher starts `DayZ_BE.exe`, so the launcher never saw the failure and
  could not explain it. Both executables are now opened for reading first; if
  either is refused, the launcher says so itself, with the elevation advice
  when that fits and the ordinary causes (anti-virus, files owned by an
  elevated install, a drive that has gone away) when it does not.

### Measured

- **A launcher running normally CAN still reach an elevated Steam.** Tested
  with Steam started as administrator: `SteamAPI_Init` succeeded, ISteamUGC
  bound and the server browser connected. So an elevation mismatch is recorded
  in the log and explained when something else fails - it does not block a
  join, which was the first shape this took and would have stopped players who
  were working fine.

### Changed

- **Unofficial-launcher disclaimer.** "A Beautiful Potato Launcher is an
  unofficial third-party launcher made by community members for the DayZ
  community. It is not endorsed by, affiliated with, or sponsored by Bohemia
  Interactive a.s. All trademarks are the property of their respective
  owners." Shown to the right of SEARCH, and as the hover text (and
  screen-reader description) of the DayZ logo. One `Disclaimer` constant
  feeds both so the wording cannot drift. Beside SEARCH it shows in full on
  windows about 1,500 px wide or more; narrower, it ends in "..." and the
  full text is in its tooltip.

---

## [0.25] — published

### Added
- **Version number in the footer**, just left of Settings: `v0.25 · 2026-09-21 05:09`.
  Every build is stamped with its build time (the informational version reads
  `0.25+20260921.0909`, UTC), because test builds happen many times a day at
  the same version number and a bug report needs to say which one it came
  from. The tooltip gives the UTC build time, and the log's "Launcher started"
  line now records the version too. Version set to **0.25**. To move to the next
  version, bump `<Version>` in the project file and start a new section above
  this one.
- **Changelog.** This file.
- **Mod Manager: INSTALL BY ID.** Enter a workshop id and the mod is
  subscribed and downloaded. Accepts a pasted workshop URL as well as a bare
  number, since the id is usually arrived at by copying the link. Subscribes
  *before* downloading — asking Steam to download an item the account does not
  own is a no-op that reports success, which looks exactly like an instant
  download of nothing. Reuses the existing download window for progress, drops
  the path and index caches afterwards, and selects the new mod in the list.

### Changed
- **The Browse dropdown carries the server list's colours** — Stable green,
  Experimental orange, "All servers" plain. Owner-drawn, using the same values
  as the Game column so the two agree at a glance.
- **The FILTERS button turns amber with a bullet when any filter is set.** A
  filter left on with the panel closed is invisible, and that is where "why can
  I not see any servers" usually ends. Driven by a new `AnyPlayerChose` rather
  than the existing `AnyActive`, which includes the tab-driven Official setting
  and would therefore be true the moment you left the Recent tab. It also covers
  the newer controls — regions, countries, required mods, game modes, the two
  sliders — that `AnyActive` predates.
- **Official servers named `- MI` are shown as `- Miami`.** Bohemia's datacentre
  codes are cities, and that one reads as Michigan to everyone who sees it while
  the hardware is in Florida. Applied where the name enters the index rather
  than at draw time, so searching "Miami" finds them. Only `MI` is expanded —
  the rest are unambiguous, and every expansion changes what a player sees.
  12 servers affected, country resolution unaffected.
- **A mod more than 5 minutes out of step with the workshop is updated before
  joining.** `StaleTolerance` went from a day to five minutes. The day existed
  to absorb workshop *page* edits, which move the "updated" time without
  changing any files; that job is now done by remembering Steam's own answer.
  Once Steam confirms our copy is current for a given publication time it is
  recorded in `steam-confirmed.tsv`, so a page edit does not bring the mod back
  as "out of date" on every Connect — and the record lapses the moment the
  author publishes again. Out-of-step mods go through Steam's check rather than
  straight to the download window, because an already-current mod transfers
  nothing and a window waiting for bytes would never finish. Measured: 3 of 880
  installed mods sent to Steam on the first Connect (3.7 s, none needed
  fetching), 0 on the second.
- **Steam's confirmation allows time per mod.** Steam answers one at a time,
  ~1.4 s each, so the fixed 15 s deadline could expire before later mods were
  reached and send current ones to be downloaded. It is now 4 s + 2.5 s per
  mod, capped at 90 s.

### Fixed



- **Official servers showed the wrong country.** Bohemia's entire fleet sits in
  RIPE blocks registered to Germany and Luxembourg, so the registry table filed
  New York, Miami, Sao Paulo and Sydney servers under DE/LU. Their names carry
  the datacentre — `3208 | NORTH AMERICA - MI | Temp` — so that is used instead
  for these servers only; a community server's name is whatever its owner typed.
  **99 of 181 officials were being mislabelled.** Note the codes are cities:
  **MI is Miami**, not Michigan.
- **Connect could join with a mod republished minutes earlier.** The timestamp
  check allows a day of slack, because the workshop's "updated" time also moves
  when only a description or image changes. A mod author republishing at 04:47
  and joining at 04:50 sat inside that slack and was waved through. Connect now
  has **Steam** confirm any mod in doubt: Steam compares the content manifest,
  which moves only when the files do. Measured: asking about an already-current
  mod settles in ~1.4 s and transfers **0 bytes, rewrites nothing**; anything
  Steam decides to fetch joins the download window and is waited on before
  launching. Steam answers these one at a time, so only doubtful mods are
  asked — a copy downloaded *after* the workshop last published cannot be
  behind, and that is 862 of 880 installed mods. Result: 1 of 5 mods asked on
  the MotoX server, 1.5 s added to Connect. The same check confirmed two mods
  the timestamp rule was flagging were metadata-edit false positives.
- **Workshop ids ending in `0x02` were silently corrupted.**
  `NormalizeWorkshopId` subtracted 2 from any id whose low byte was `0x02`, on
  the theory that DayZ emits a stray tag shifting ids by +2. That broke one
  workshop id in every 256 — `3805561602` ("Fill Direct From Pumps") was
  reported as `3805561600`, which does not exist. Checked against Steam's
  catalogue:

  | id | exists | title |
  |---|---|---|
  | 3805561602 | yes | Fill Direct From Pumps |
  | 3805561600 | no  | — |
  | 1797720066 | no  | — |
  | 1797720064 | yes | WindstridesClothingPack |

  So the drift is real for *some* records and not others, and the number alone
  cannot tell them apart — guessing corrupted good ids at the same rate it
  fixed bad ones. The id is now taken exactly as the server sends it (the wire
  bytes were verified by hand against a live packet), and the ambiguity is
  resolved where it can actually be checked: `ResolveDownloadId` asks Steam's
  catalogue before a download and only falls back to `id - 2` when the original
  genuinely does not exist.
- **The mod panel refreshed forever after installing a mod by id.** The
  publication-time prefetch selected ids whose time was still *unknown*, which
  reads correctly and is a live lock: the fetch finishes, the panel repopulates,
  and any id Steam did not answer for is still unknown - so it asks again,
  forever. The panel flashed continuously and the window could not be clicked.
  A newly installed mod is exactly the case Steam has no time for, which is why
  INSTALL BY ID set it off every time. Ids are now remembered as **asked**
  rather than answered, so each is requested once per session; installing by
  hand clears that list so the new mod still gets checked. Verified: 12
  populates, 3 requests.
- **The Mod Info window disagreed with the panel and with the launch check.**
  Its "Difference" row did its own subtraction of meta.cpp against the workshop
  date, while the panel and `Launch` use `StaleBy` — so Info could say OUT OF
  DATE, the panel show green, and joining repair nothing. All three now call
  `StaleBy`. **The panel was right:** in all 7 disagreements the copy had been
  *downloaded after* the workshop last published; only the author's own
  meta.cpp label was stale. Judging by meta.cpp alone would have re-downloaded
  7 mods that were already correct. The window now shows all three dates —
  meta.cpp, downloaded, workshop — and says so explicitly when the label is
  behind but the copy is current.
- **The mod panel never checked for updates.** `PrefetchWorkshopTimes` was only
  called from the launch path, so outside a launch the workshop publication
  dates were unknown — and `StaleBy` compares those against the local meta.cpp,
  so with nothing to compare it returned "up to date" for everything. Measured:
  0 of 60 mods had a known workshop date before the fetch, 60 of 60 after. The
  panel now fetches them in the background when a server is selected and
  repaints when they arrive.
- **The Mod Info window hid the timestamps it could not fill.** "Workshop
  version" was omitted entirely when Steam had not been asked — which looked
  like the mod had no such date rather than like nobody had checked. Both rows
  now always appear, with an explicit "(not checked yet)", plus a **Difference**
  row spelling out the gap.
- **The Region filter disagreed with the Country column.** `PassesRegion` read
  the address directly instead of the name-first rule, so US official servers
  showed `US` in the column while the Europe filter claimed them. There is now
  one place that answers this — `IpRegion.CountryOf(name, host)` — and the row,
  the column and the filter all call it. Verified: 181 officials, 0
  disagreements; 48 now under North America rather than all 130 under Europe.
- **The "not configured yet" notice was drawn in the refresh column.** That
  column is 26px wide, so the text was invisible. It goes in the Name column.
- **Unknown countries show `??` rather than a blank cell.** A blank reads like
  nobody looked; two question marks say we looked and could not tell. Sorts as
  unknown, not as a country.
- **OFFICIAL and COMMUNITY tabs were showing each other's servers.** Nothing set
  `_filters.Official` from the tab, so it sat on `Any` and both lists were
  unfiltered — the Official cache held 5,649 servers of which 176 were actually
  official. The tab now drives it, for the Steam query *and* the local match.
  Recent, Friends, LAN and Favourites are deliberately left alone: which hive a
  server is on is not what those lists are about. Verified at 0 leakage in both
  directions.
- **Chip entries overflowed and covered their neighbours.** The Has Mods control
  was 660px wide starting at x=118, running clean across the panel and over the
  3rd Person, Mods and checkbox controls on the right. It is 250px now, matching
  the rest of its column, and the chip area **scrolls** — previously it simply
  stopped drawing once the rows ran out, making a filter the player had set
  invisible. Mouse wheel, drawn scrollbar, and it jumps to the newest chip as
  one is added.
- **The logos were missing from the main window.** `LoadImage` looked for an
  `assets` folder beside the executable, but the images are embedded resources
  and nothing deploys such a folder — so it silently returned a 1×1 bitmap and
  the logos were absent rather than broken-looking. It now reads the embedded
  resource first and falls back to disk for source-tree runs.
- **Chip entries are below the input, not beside it.** Beside the box there was
  room for two or three mods before the rest ran off the edge. They now sit
  underneath across the full width and wrap onto two lines — measured at six
  long mod names in 660-720px, where the previous arrangement held three.

### Investigated and rejected
- **Flagging country mismatches by ping.** Measured and abandoned: A2S ping is
  dominated by server-side processing, not distance. Pinging one server five
  times gives a median spread of **50 ms** and a worst case of **194 ms**, while
  the gap between country medians is only **20-30 ms** (SG 108, DE 118, US 127).
  The noise on a single server is larger than the difference between continents,
  so any threshold would flag legitimate servers constantly. Within-country
  spread is also enormous (DE: 79-479 ms). A bundled GeoLite2 database is the
  route to better accuracy, not latency inference.

---

## 2026-09-20

### Renamed
- `BeautifulPotatoExpLauncher` → `ABeautifulPotatoLauncher` (namespace, assembly,
  executable, project file, `%APPDATA%` folder, installer, tooling). The launcher
  covers every DayZ server, not just Experimental.
- `ServerStore.BringForwardOldData` copies the old AppData folder forward once,
  gated by a marker file rather than by whether the new folder exists — it can
  exist and still be empty. Reads from both previous names. The old folders are
  left in place.

### Added — server index
- **Past Steam's 10,000-server cap.** The cap is per *request*, so the list is
  swept in narrower slices and merged. Measured: one plain query returns 4,997
  servers with details; adding per-map, populated-only, modded and first-person
  passes reaches 18,502.
- **Persistent map index** (`maps.txt`). Every map name ever seen, kept forever —
  there is no endpoint that lists DayZ maps, so they are learnt from servers.
- **BUILD INDEX button.** Sweeps every known map in one run.
- **Per-map results** logged after each sweep, and recorded in `map-counts.tsv`.
  Maps that return nothing twice running are skipped; one non-zero reading brings
  them back.
- **Repeat passes** for the busiest maps during a full build. Steam returns a
  *slice*, not the list: `map=chernarusplus` three times gave 3701/3702/3702
  servers, but run 2 added 289 the first never mentioned and run 3 another 223.
- **The index is saved while it builds**, every 30s and on close, not only when a
  sweep completes.
- **Live A2S replies are written back into the index** — names, player counts,
  tags — so a rename survives re-renders and restarts.
- Retention raised 7 → 30 days: a refresh only asks about 12 maps out of 100+, so
  a live server on a quiet map could go a fortnight without being seen.

### Added — filters
- **Region and country filtering**, from a 1.36 MB IP→country table built from
  the five regional internet registries (`tools/build-ip-table.ps1`). 27 MB of
  registry text in, 142,930 merged ranges out, **100% of 3,548 real server
  addresses resolved**. Country → continent is mapped in code, because the
  registries' regions are not continents (RIPE covers Europe *and* the Middle
  East).
- **Country column** in the server list, sortable, blanks last.
- **Game mode toggles** (PVP, PVE, RP, TRADER, AI, NO KOS, KOS, HARDCORE,
  DEATHMATCH, PVP ZONES), level with the tabs. Matched against the server name
  *and* description. **All selected modes must match** — each toggle narrows.
  Word-boundary matching, so "Warp Zone Gaming" is no longer a roleplay server.
- **Player count range slider**, two grips, 0–127. The top means "and above".
- **Game time range slider**, six-hour steps with ticks. Verified: the four bands
  partition the 12,248 clock-publishing servers exactly.
- **Has Mods filter** — several mods at once, servers must run all of them.
  Backed by a persistent mod index (`server-mods.tsv`).
- **Find mod** search in the Mods panel, narrowing one server's list. Matches
  *any* term, unlike Has Mods — a row is one mod and cannot be two things.
- Map filter became a searchable dropdown; **X to clear** on every input.
- The FILTERS button lights up while the panel is open.

### Added — mods
- **Mod lists are collected in the background from startup** and persisted, so
  mod filtering answers instantly instead of starting empty. Measured cost of a
  full pass: 228 ms per server blended, ~5 minutes across the index on 16
  workers, 16 MB down, 0.4 MB up, ~6.8 MB on disk.
- **Mod panel refreshes when downloads finish** — after the launch-time download
  and after a Repair, with both the path cache and the library index dropped
  first.
- `Unpacked` status replaces `Corrupt` for workshop folders with content but no
  `meta.cpp` — extracted mods, not broken ones. 12 of 898 on the dev machine.
  Row tooltips explain every status, and the explanation wraps.
- Mod Manager: resizable detail panel, ESC to close, hand cursors on Repair and
  Remove, local mods excluded from Steam actions.
- Cached workshop previews are named `.jpg`/`.png` by magic number rather than a
  generic `.img`.

### Added — diagnostics
- **`serverdata/`** beside the executable: the full browsed list and everything
  screened out, with reasons. Gitignored — the screened list is a description of
  what the screening looks for.
- Unhandled exceptions on the UI *and* background threads are written to
  `logs\launcher.log` before the process goes down.

### Changed — fake server screening
Rules are deliberately **not** described in the interface any more; the "Fake
server rules" tab was a checklist for the people being screened.

- **255 slots is fake outright.** 272 servers report it; 242 of those also claim
  zero players.
- **127 slots needs a second signal** — it is `0x7F`, "not reported", and 5,604
  servers use it. Any one of: claims to be exactly full at the sentinel
  (`127/127`); every server on that address reports a sentinel; 20+ servers on
  the address. A real host runs a crowd — the dev machine's own host runs 18,
  every one stating a true capacity.
- **Impersonated copies.** A name on 3+ addresses where *this* copy also looks
  fabricated. The tiebreaker matters: the genuine server is one of the copies.
- **Mostly-fake addresses.** 60%+ of 5 or more servers already condemned.
- Result: of 2,363 servers carrying one impersonated network's name, 2,350 are
  hidden and the 13 survivors are all on genuine addresses.
- Stock host names (nitrado, GTXGaming, HostHavoc, gportal, NFOservers) sort to
  the bottom under a notice, and are never queried.

### Fixed
- **Crash: `InvalidOperationException` on a worker thread.** `MatchesRequiredMods`
  enumerated a `HashSet` the UI thread was mutating. An unhandled exception on a
  background thread terminates the process — no dialog, no log. Workers now read
  an immutable snapshot.
- **Crash: virtual `ListView` sub-items.** The "not configured" notice rows filled
  one column of thirteen; a virtual list throws from inside `WndProc` when such a
  row scrolls into view.
- **Crash: ComboBox autocomplete.** Rebuilding `Items` while the shell
  autocomplete COM object is attached faults unmanaged — no exception, nothing
  logged. Dropdowns are now stocked *before* they are clicked, never from their
  own `DropDown` event, and never while open.
- **Servers with players shown as offline.** A2S is UDP and a query is two round
  trips; a single lost packet marked a live server dead. One server 370 ms away
  answered 15 of 16 times. Queries are now retried once.
- **The list jumped while loading.** Arrivals are appended rather than sorted in;
  offline servers sink once the sweep goes quiet, not mid-click.
- **Favourites always sort to the top**, offline or not.
- **Flicker.** Lists are double-buffered, unchanged renders are skipped, and
  renders are throttled while a list is arriving.
- **~400 ms of UI-thread work every 3 seconds** during a mod sweep: an 8.5 MB
  save moved off-thread, the dropdown tally kept incrementally, and re-renders
  gated on an O(1) check.
- Filter panel sizes itself to its contents; `AutoScroll` removed, which was
  eating the first click on every dropdown.
- Settings: Mods tab first, Explorer buttons on both paths, the DayZ `!Workshop`
  junction path shown rather than the numeric Steam one, and the flagged-servers
  search no longer covers the list header.
- Name column widens to the longest visible name.
- Steam AppID passed correctly when launching, so Experimental servers no longer
  reject the ticket.

---

## Before 2026-09-20

Reconstructed from the code; the commit history does not describe it.

- Server browser over `ISteamMatchmakingServers`, with A2S queries for live
  detail (the legacy UDP master protocol is dead).
- Mod list parsing from `A2S_RULES`, including DayZ's chunked, byte-escaped mod
  blob.
- Mod Manager, mod downloading and repair through the Steam Workshop API.
- Favourites, recent servers, LAN and Friends tabs; Official server detection via
  the absence of the `privHive` tag.
- Window and panel sizes remembered between runs.
