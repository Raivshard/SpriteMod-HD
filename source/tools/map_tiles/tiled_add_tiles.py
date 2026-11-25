r"""Add tiles you made after the export to Tiled working maps: every PNG in the project's added-tiles folder
("my tiles"; Karnak: Tilesets\added, modified) that a map does not list yet is appended as tileset "added: <name>".
Only tilesets are appended; layers, cells and existing tilesets stay as they are. In Tiled: save the map first; after
the command Tiled asks to reload the file (yes).
The map file is yours: a map you saved needs your consent (guard.py); the Tiled command "Add my new tiles to this map"
is that consent for the open map (it passes --overwrite-saved).
Usage: python tiled_add_tiles.py <map>.tmj [...] [--dry] [--overwrite-saved]
"""
import json, sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import guard   # your saved files are never changed without consent
from project import P
from tiled_export import mobile_tilesets


def add(tmj, dry):
    doc = json.loads(tmj.read_text(encoding="utf-8"))
    have = {t.get("name") for t in doc["tilesets"]}
    first = max(t["firstgid"] + t.get("tilecount", 1) for t in doc["tilesets"])
    new = []
    for ts in mobile_tilesets(tmj.parent, 0):
        if ts["name"].startswith("added: ") and ts["name"] not in have:
            ts["firstgid"] = first
            first += ts["tilecount"]
            new.append(ts)
    print(f"{tmj.name}: {len(new)} new tile set(s)" + (": " + ", ".join(t["name"] for t in new) if new else "")
          + (" (dry run)" if dry else ""))
    if new and not dry:
        doc["tilesets"] += new
        guard.write_text(tmj, json.dumps(doc))


if __name__ == "__main__":
    for f in [Path(x) for x in sys.argv[1:] if x.endswith(".tmj")]:
        add(f, "--dry" in sys.argv)
