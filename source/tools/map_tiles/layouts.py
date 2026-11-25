r"""Edited map layouts (Tiled edits that move, add, erase or restyle single cells).
The project copy of each edited map's tilemap.json lives in <map project>\layouts\ (Karnak: Working\tilesets\Karnak\layouts)\<Map_NNNNN>\<SubMap>\tilemap.json
(written by tiled_import.py); the plugin reads the installed copy from the game's own path under the mod folder,
<game>\Modules\00-Mods\SpriteMod HD\FF5\GameAssets\Serial\Res\Map\<Map_NNNNN>\<SubMap>\tilemap.json, and replaces the
tile layers' cells (FieldLayouts.cs). Every tool that reads a map's tiles (Tiled export, shadow simulation, lava cells)
reads the edited layout where one exists, the game's export otherwise.
Tile numbers: the game's tilemap counts sheet rows from the bottom (gid - firstgid = (31 - row) * 32 + col), the
project's tile ids (catalogue, overrides, manual_choices) from the top; flip() converts either way.
"""
import json
from pathlib import Path
import sys
sys.path.insert(0, str(Path(__file__).resolve().parent))
from project import P   # map project (project.json): paths, lists, game
sys.path.insert(0, str(__import__('pathlib').Path(__file__).resolve().parents[1]))  # tools folder: games.py
from games import GAME  # --game FF6 picks the game (tools\games.json)

PROJECT = Path(__file__).resolve().parents[2]
LAYOUTS = P.layouts
INSTALLED = GAME.mod / "GameAssets" / "Serial" / "Res" / "Map"
CC = GAME.magicite / "Color Corrected Tiles"
EXPORT = str(GAME.export)   # the game's MagiciteExport (read-only)
GAME = GAME.root   # game folder (other map tools import it from here)
GID = 0x1FFFFFFF   # Tiled gid without the flip flags
TILE_LAYERS = ("BottomLayer", "GroundLayer", "UpperLayer", "CeilLayer")


def walk(layers):
    """every tile layer of a Tiled layer list, groups opened (any depth); same as pair_maps.walk"""
    for l in layers:
        if l.get("type") == "tilelayer":
            yield l
        yield from walk(l.get("layers", []))


def cell_layer_tiles(mapname):
    """cell index -> {layer name: (sheet key, project tile id)} of a map (its edited layout if there is one)"""
    import csv
    f = next(Path(EXPORT).glob(f"map_*/Assets/GameAssets/Serial/Res/Map/**/{mapname}/tilemap.json"))
    pkg = next(p for p in f.parts if p.startswith("map_") and p == p.lower())
    by_pkg = {}
    for r in csv.DictReader(open(P.catalog / "sheets.csv", encoding="utf-8")):
        for p in r["packages"].split():
            by_pkg[(p, r["name"])] = r["sheet"]
    j = load_tilemap(f)
    ts = sorted(((t["firstgid"], by_pkg.get((pkg, Path(t["image"]).stem))) for t in j["tilesets"]), reverse=True)
    out = {}
    for l in walk(j["layers"]):
        if l.get("name") not in TILE_LAYERS or "data" not in l:
            continue
        for i, g in enumerate(l["data"]):
            g &= GID
            if g:
                first, key = next((fi, k) for fi, k in ts if g >= fi)
                out.setdefault(i, {})[l["name"]] = (key, flip(g - first))
    return out


def flip(t):
    """game tile index <-> project tile id (512 px sheet, 32 x 32 tiles of 16 px)"""
    return (31 - t // 32) * 32 + t % 32


def layout_file(mapname, package):
    return LAYOUTS / package / mapname / "tilemap.json"


def load_tilemap(f):
    """the map's tilemap.json: the edited layout if there is one, else the export file f"""
    f = Path(f)
    p = layout_file(f.parent.name, f.parent.parent.name)
    return json.loads(p.read_text(encoding="utf-8")) if p.exists() else json.load(open(f, encoding="utf-8"))
