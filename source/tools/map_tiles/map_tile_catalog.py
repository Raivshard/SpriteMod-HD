r"""Build the map tile catalogue: every 16 px tile image of every map sheet as one shared item.

Reads the game's export (read-only): map_NNNNN/.../Res/Map/Map_NNNNN/*chip_*.png (512x512 = 32 x 32 tiles of 16 px),
every tilemap.json (Tiled; 4 tile layers; tileset `tiles` = animations) and collision.json [verified, docs/verified_facts.md
"Field maps and tiles"].

Item = one tile image, named t_<first 12 hex of the md5 of its 16x16 RGBA pixels>. The same image in 40 sheets is one
item (user, 2026-11-15: shared items + per-sheet overrides). Fully transparent cells are not items.
Sheet version: 23 sheet names have different pixels in different map packages [verified]; each pixel version is its own
sheet key: the plain name if a name has one version, else <name>@<first package> (e.g. map_chip_063@map_30090).

Output (<out>, default data/map_tiles/catalog; refuses to overwrite without --force, never touches labels.csv):
  sheets.csv       sheet key, name, packages, pixel md5, tiles (non-empty), tiles used on maps
  sheet_tiles.csv  sheet key, tile id (row * 32 + column), item
  items.csv        item, sheets, cells placed, maps, layers (B/G/U/C counts), animation frame, collision values seen,
                   first sheet:id, all sheet:id
  labels.csv       item, label, group, note (created once with every used item; yours to edit)
  items_16\<item>.png   the 16 px tile (reference / template)
Usage: python map_tile_catalog.py [--export <MagiciteExport>] [--out <dir>] [--force]
"""
import csv
import glob
import hashlib
import json
import os
import sys
from collections import Counter, defaultdict
from pathlib import Path

from PIL import Image

PROJECT = Path(__file__).resolve().parents[2]
EXPORT = r"C:\Program Files (x86)\Steam\steamapps\common\FINAL FANTASY V PR\FINAL FANTASY V_Data\StreamingAssets\MagiciteExport"
T, COLS = 16, 32
GID_MASK = 0x1FFFFFFF   # Tiled stores flip flags in the top bits
LAYER_LETTER = {"BottomLayer": "B", "GroundLayer": "G", "UpperLayer": "U", "CeilLayer": "C"}
COLLISION_FOR = {"BottomLayer": "BottomCollision", "GroundLayer": "GroundCollision", "UpperLayer": "UpperCollision"}


def package_of(path):
    """map_NNNNN export folder of a file."""
    p = Path(path)
    for part in p.parts:
        if part.lower().startswith("map_") and part == part.lower():
            return part
    return None


def item_id(tile):
    return "t_" + hashlib.md5(tile.tobytes()).hexdigest()[:12]


def load_sheets(export):
    """-> versions: {key: dict(name, packages, md5, path)}, by_pkg: {(package, name): key}"""
    files = defaultdict(list)
    for p in glob.glob(os.path.join(export, "map_*", "Assets", "GameAssets", "Serial", "Res", "Map", "*", "*chip_*.png")):
        if Path(p).stem.endswith("_pal_anim"):
            continue   # 128x128 palette-animation lookups listed beside some sheets (package.json), not tile art
        files[Path(p).stem].append(p)
    versions, by_pkg = {}, {}
    for name, paths in sorted(files.items()):
        groups = defaultdict(list)
        for p in sorted(paths):
            md5 = hashlib.md5(Image.open(p).convert("RGBA").tobytes()).hexdigest()
            groups[md5].append(p)
        for md5, ps in groups.items():
            pkgs = sorted({package_of(p) for p in ps})
            key = name if len(groups) == 1 else f"{name}@{pkgs[0]}"
            versions[key] = {"name": name, "packages": pkgs, "md5": md5, "path": ps[0]}
            for pk in pkgs:
                by_pkg[(pk, name)] = key
    return versions, by_pkg


def walk_layers(layers):
    for l in layers:
        if l.get("type") == "tilelayer":
            yield l
        yield from walk_layers(l.get("layers", []))


def main(argv):
    export = argv[argv.index("--export") + 1] if "--export" in argv else EXPORT
    out = Path(argv[argv.index("--out") + 1]) if "--out" in argv else PROJECT / "data" / "map_tiles" / "catalog"
    force = "--force" in argv
    if (out / "items.csv").exists() and not force:
        print(f"{out} has a catalogue: use --force to rebuild (labels.csv is never touched)")
        return 1
    versions, by_pkg = load_sheets(export)

    # cut every sheet version into items
    sheet_tiles = {}                     # (key, id) -> item
    items = {}                           # item -> tile image
    for key, v in versions.items():
        im = Image.open(v["path"]).convert("RGBA")
        if im.size != (COLS * T, COLS * T):
            print(f"WARNING {key}: {im.size}, expected 512x512; cut by 16 px anyway")
        for r in range(im.height // T):
            for c in range(im.width // T):
                tile = im.crop((c * T, r * T, c * T + T, r * T + T))
                if tile.getchannel("A").getbbox() is None:
                    continue
                it = item_id(tile)
                sheet_tiles[(key, r * COLS + c)] = it
                items.setdefault(it, tile)

    # usage from every tilemap
    placed, maps, layers, anim, coll = Counter(), defaultdict(set), defaultdict(Counter), set(), defaultdict(Counter)
    used_cells = Counter()   # sheet key -> placed cells
    problems = []
    placeholders = 0
    for f in glob.glob(os.path.join(export, "map_*", "Assets", "GameAssets", "Serial", "Res", "Map", "**", "tilemap.json"), recursive=True):
        pkg = package_of(f)
        j = json.load(open(f, encoding="utf-8"))
        sub = Path(f).parent.name
        tsets = []
        for ts in sorted(j.get("tilesets", []), key=lambda t: t["firstgid"]):
            name = Path(ts["image"]).stem
            key = by_pkg.get((pkg, name))
            if key is None and "chip_" not in name:
                placeholders += 1   # event maps: one-tile 'Map_event.png' / 'tileset.png', no tiles placed [verified, Map_40037]
            elif key is None:
                problems.append(f"{sub}: sheet {name} not in package {pkg}")
            tsets.append((ts["firstgid"], key))
            for e in ts.get("tiles", []):
                for fr in e.get("animation", []):
                    it = sheet_tiles.get((key, fr["tileid"]))
                    if it:
                        anim.add(it)
        cgrid = {}
        cf = Path(f).parent / "collision.json"
        if cf.exists():
            cj = json.load(open(cf, encoding="utf-8"))
            for l in walk_layers(cj.get("layers", [])):
                cgrid[l["name"]] = (l["data"], l.get("width", 0))
        for l in walk_layers(j["layers"]):
            letter = LAYER_LETTER.get(l["name"], "?")
            cg = cgrid.get(COLLISION_FOR.get(l["name"], ""))
            w = l.get("width", j["width"])
            for i, g in enumerate(l["data"]):
                g &= GID_MASK
                if not g:
                    continue
                key = None
                for first, k in tsets:
                    if g >= first:
                        key, tid = k, g - first
                it = sheet_tiles.get((key, tid)) if key else None
                if it is None:
                    continue
                placed[it] += 1
                used_cells[key] += 1
                maps[it].add(sub)
                layers[it][letter] += 1
                if cg and cg[1] == w and i < len(cg[0]):
                    coll[it][cg[0][i]] += 1

    out.mkdir(parents=True, exist_ok=True)
    (out / "items_16").mkdir(exist_ok=True)
    where = defaultdict(list)
    for (key, tid), it in sorted(sheet_tiles.items()):
        where[it].append(f"{key}:{tid}")
    with open(out / "sheets.csv", "w", newline="", encoding="utf-8") as fh:
        w = csv.writer(fh)
        w.writerow(["sheet", "name", "packages", "pixel_md5", "tiles", "tiles_used_cells"])
        for key, v in versions.items():
            n = sum(1 for (k, _t) in sheet_tiles if k == key)
            w.writerow([key, v["name"], " ".join(v["packages"]), v["md5"], n, used_cells[key]])
    with open(out / "sheet_tiles.csv", "w", newline="", encoding="utf-8") as fh:
        w = csv.writer(fh)
        w.writerow(["sheet", "tile_id", "item"])
        for (key, tid), it in sorted(sheet_tiles.items()):
            w.writerow([key, tid, it])
    with open(out / "items.csv", "w", newline="", encoding="utf-8") as fh:
        w = csv.writer(fh)
        w.writerow(["item", "sheets", "cells_placed", "maps", "layers", "animation_frame", "collision_values",
                    "first", "all"])
        for it in sorted(items, key=lambda i: (-placed[i], i)):
            ws = where[it]
            w.writerow([it, len({x.split(":")[0] for x in ws}), placed[it], len(maps[it]),
                        " ".join(f"{k}{n}" for k, n in sorted(layers[it].items())),
                        "yes" if it in anim else "",
                        " ".join(f"{k}:{n}" for k, n in sorted(coll[it].items())),
                        ws[0], " ".join(ws)])
    for it, tile in items.items():
        p = out / "items_16" / f"{it}.png"
        if not p.exists():
            tile.save(p)
    lab = out / "labels.csv"
    if not lab.exists():
        with open(lab, "w", newline="", encoding="utf-8") as fh:
            w = csv.writer(fh)
            w.writerow(["item", "label", "group", "note"])
            for it in sorted(items, key=lambda i: (-placed[i], i)):
                if placed[it] or it in anim:
                    w.writerow([it, "", "", ""])
    (out / "problems.txt").write_text("\n".join(problems) + ("\n" if problems else ""), encoding="utf-8")
    used = sum(1 for it in items if placed[it] or it in anim)
    multi = sum(1 for v in versions.values() if len([k for k in versions if versions[k]["name"] == v["name"]]) > 1)
    print(f"sheet versions {len(versions)} ({multi} belong to names with several versions); tile cells {len(sheet_tiles)}; "
          f"items {len(items)}, used (placed or animation frame) {used}; placements {sum(placed.values())}; "
          f"event-map placeholder tile sets skipped {placeholders}; problems {len(problems)} -> {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
