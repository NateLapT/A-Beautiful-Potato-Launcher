# Changelog

All notable changes to A Beautiful Potato Launcher.

**Every change goes in here.** Add an entry under `[Unreleased]` as part of the
work, not afterwards — an undocumented change is the one nobody can explain in
six months.

Format: newest first. Each entry says what changed and, where it matters, *why*
— particularly for the screening rules, which are full of thresholds that look
arbitrary until you know what they were measured against.

> Commits before 2026-09-20 have messages like `20260919` and `Cleanup`, so the
> history below starts where the work became traceable. Earlier entries are
> reconstructed from the code and are necessarily thin.

---

## [Unreleased]

### Added
- **Changelog.** This file.

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

### Fixed
- **Official servers showed the wrong country.** Bohemia's entire fleet sits in
  RIPE blocks registered to Germany and Luxembourg, so the registry table filed
  New York, Miami, Sao Paulo and Sydney servers under DE/LU. Their names carry
  the datacentre — `3208 | NORTH AMERICA - MI | Temp` — so that is used instead
  for these servers only; a community server's name is whatever its owner typed.
  **99 of 181 officials were being mislabelled.** Note the codes are cities:
  **MI is Miami**, not Michigan.
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
