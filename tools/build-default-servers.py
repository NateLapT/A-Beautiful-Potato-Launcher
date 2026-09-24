# ---------------------------------------------------------------------------
#  Builds assets/default-servers.tsv.gz - the server list a fresh install
#  starts with, so the browser is full on first run instead of empty.
#
#  Source: https://steam.dayzed.gg/servers - one JSON document of every DayZ
#  server that 21 query nodes around the world saw in Steam's master list.
#
#  Output: the launcher's own list format (see ServerStore.SaveList), one
#  server per line, tab separated, gzipped:
#
#    name  map  gamedir  tags  host  gameport  queryport  players  maxplayers
#    ping  appid  password  secure  lastseen
#
#  Fakes are kept on purpose. The launcher's farm detection works by counting
#  how many servers share an address, so a list with the farms removed would
#  teach it nothing - it hides them itself at render time.
#
#  Usage:  python tools/build-default-servers.py            (downloads)
#          python tools/build-default-servers.py file.json  (uses a saved copy)
# ---------------------------------------------------------------------------

import gzip
import json
import os
import sys
import urllib.request

URL = "https://steam.dayzed.gg/servers"
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "assets", "default-servers.tsv.gz")


def clean(v):
    # Same as ServerStore.Clean: a stray tab or newline would shift every
    # later column on the line.
    return (v or "").replace("\t", " ").replace("\r", " ").replace("\n", " ")


def main():
    if len(sys.argv) > 1:
        with open(sys.argv[1], encoding="utf-8") as f:
            doc = json.load(f)
    else:
        req = urllib.request.Request(URL, headers={"User-Agent": "Mozilla/5.0"})
        with urllib.request.urlopen(req, timeout=300) as r:
            doc = json.load(r)

    lines = []
    for s in doc["servers"]:
        host, _, qport = s["addr"].rpartition(":")
        if not host or not s.get("gameport"):
            continue
        lines.append("\t".join([
            clean(s.get("name")),
            clean(s.get("map")),
            clean(s.get("gamedir")),
            clean(s.get("gametype")),
            host,
            str(s["gameport"]),
            qport,
            str(s.get("players", 0)),
            str(s.get("max_players", 0)),
            "0",                                  # ping: unknown until asked
            str(s.get("appid", 0)),
            "0",                                  # password: not in the source
            "1" if s.get("secure") else "0",
            "0",                                  # last seen: stamped on first load
        ]))

    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with gzip.open(OUT, "wt", encoding="utf-8", newline="\n", compresslevel=9) as f:
        f.write("\n".join(lines) + "\n")

    print("%d servers (generated %s) -> %s, %.1f MB"
          % (len(lines), doc.get("generated_at"), os.path.normpath(OUT), os.path.getsize(OUT) / 1048576.0))


if __name__ == "__main__":
    main()
