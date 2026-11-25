r"""Map tile items <-> HD sheets, using the catalogue from map_tile_catalog.py.

Layout under <root> (default Working/tilesets/Karnak):
  catalog\                     from map_tile_catalog.py (sheets.csv, sheet_tiles.csv, items.csv, labels.csv)
  replacements\<item>.png      HD art for one item, used in every sheet that has that item (shared)
  overrides\<sheet>\<id>.png   HD art for one tile of one sheet only (wins over the shared item)
  pack\                        built sheets: <name>.hd.png, or <package>\<name>.hd.png for the 23 sheet names whose
                               art differs between map packages; copy pack\* into the game's map_tiles\ folder

Every HD tile is (16 x scale) px square; one scale per build (default 4 = 64 px tiles, 2048 px sheets).

  import <hd sheet.png> <sheet>   cut an HD sheet (same layout as the game's, any whole multiple) into items: a new item
                                  goes to replacements\; an item that already has different art there goes to
                                  overrides\<sheet>\ (so the sheet rebuilds exactly). Never overwrites a differing file
                                  without --force.
  build [--sheets a,b] [--all]    build HD sheets: the game's tile enlarged whole-pixel, then the item's replacement or
                                  the tile's override pasted over it. Default: every sheet with a replacement or override.
  compare <hd sheet.png> <sheet>  per-tile check of a built sheet against another HD sheet (tiles used by the game only)
Options: --root <dir>, --scale N, --export <MagiciteExport folder> (the game sheets; same as for the catalogue), --force. <sheet> = the catalogue's sheet key (sheets.csv), e.g. map_chip_147.
"""
import csv
import sys
from collections import defaultdict
from pathlib import Path

from PIL import Image
sys.path.insert(0, str(Path(__file__).resolve().parent))
import guard   # your saved files are never changed without consent
from project import P   # map project (project.json): paths, lists, game
sys.path.insert(0, str(__import__('pathlib').Path(__file__).resolve().parents[1]))  # tools folder: games.py
from games import GAME  # --game FF6 picks the game (tools\games.json)
sys.path.insert(0, str(__import__('pathlib').Path(__file__).resolve().parents[1]))  # tools folder
import paths   # project folder layout (tools\paths.py)

PROJECT = Path(__file__).resolve().parents[2]
EXPORT = GAME.export   # --game FF6 picks the game; --export <folder> overrides
T, COLS = 16, 32


def read_catalog(root):
    cat = root / "catalog" if (root / "catalog").exists() else P.catalog   # the map project's catalogue
    sheets = {}
    with open(cat / "sheets.csv", encoding="utf-8") as fh:
        for r in csv.DictReader(fh):
            sheets[r["sheet"]] = r
    tiles = defaultdict(dict)   # sheet -> {id: item}
    with open(cat / "sheet_tiles.csv", encoding="utf-8") as fh:
        for r in csv.DictReader(fh):
            tiles[r["sheet"]][int(r["tile_id"])] = r["item"]
    multi = defaultdict(int)
    for r in sheets.values():
        multi[r["name"]] += 1
    return sheets, tiles, multi


def game_sheet(row):
    pkg = row["packages"].split()[0]
    return EXPORT / pkg / "Assets" / "GameAssets" / "Serial" / "Res" / "Map" / ("Map_" + pkg[4:]) / f"{row['name']}.png"


def cell(im, tid, size):
    c, r = tid % COLS, tid // COLS
    return im.crop((c * size, r * size, c * size + size, r * size + size))


def same(a, b):
    return a.size == b.size and a.convert("RGBA").tobytes() == b.convert("RGBA").tobytes()


def do_import(root, hd_path, key, force):
    sheets, tiles, _ = read_catalog(root)
    if key not in sheets:
        print(f"unknown sheet '{key}' (see catalog\\sheets.csv)")
        return 1
    hd = Image.open(hd_path).convert("RGBA")
    if hd.width != hd.height or hd.width % (COLS * T):
        print(f"{hd_path}: {hd.size} is not a whole multiple of the game's 512x512 sheet")
        return 1
    size = hd.width // COLS
    rep, ovr = root / "replacements", root / "overrides" / key
    rep.mkdir(parents=True, exist_ok=True)
    n = defaultdict(int)
    for tid, item in sorted(tiles[key].items()):
        t = cell(hd, tid, size)
        rp, op = rep / f"{item}.png", ovr / f"{tid}.png"
        if not rp.exists():
            guard.save_image(t, rp); n["new item"] += 1
        elif same(Image.open(rp), t):
            n["same as item"] += 1
        elif op.exists() and same(Image.open(op), t):
            n["same as override"] += 1
        elif op.exists() and not force:
            n["override differs, kept (use --force)"] += 1
        else:
            ovr.mkdir(parents=True, exist_ok=True)
            guard.save_image(t, op); n["override (item already had other art)"] += 1
    empty_drawn = sum(1 for tid in range(COLS * COLS) if tid not in tiles[key]
                      and cell(hd, tid, size).getchannel("A").getbbox() is not None)
    print(f"import {Path(hd_path).name} -> {key} ({size} px tiles): " + ", ".join(f"{k} {v}" for k, v in n.items())
          + f"; cells empty in the game sheet but drawn here (ignored): {empty_drawn}")
    return 0


def do_build(root, scale, only, build_all, force):
    sheets, tiles, multi = read_catalog(root)
    size = T * scale
    rep, ovr_root, out = root / "replacements", root / "overrides", root / "pack"
    have_rep = {p.stem for p in rep.glob("*.png")} if rep.exists() else set()
    have_ovr = {p.name for p in ovr_root.iterdir() if p.is_dir()} if ovr_root.exists() else set()
    if only:
        keys = only
    elif build_all:
        keys = list(sheets)
    else:
        keys = [k for k in sheets if k in have_ovr or any(i in have_rep for i in tiles[k].values())]
    bad_size = set()
    for key in keys:
        row = sheets[key]
        base = Image.open(game_sheet(row)).convert("RGBA")
        sheet = base.resize((base.width * scale, base.height * scale), Image.Resampling.NEAREST)
        n_rep = n_ovr = 0
        for tid, item in tiles[key].items():
            op, rp = ovr_root / key / f"{tid}.png", rep / f"{item}.png"
            src = op if op.exists() else rp if item in have_rep else None
            if src is None:
                continue
            t = Image.open(src).convert("RGBA")
            if t.size != (size, size):
                bad_size.add(f"{src} {t.size}")
                continue
            sheet.paste(t, ((tid % COLS) * size, (tid // COLS) * size))   # replaces the cell, alpha included
            n_ovr += src == op
            n_rep += src == rp
        # overrides for slots the game sheet leaves empty: new tiles of edited map layouts (tiled_import.py, user
        # 2026-11-19), used only by the layout tilemap.json files
        n_new = 0
        if (ovr_root / key).is_dir():
            for op in (ovr_root / key).glob("*.png"):
                if not op.stem.isdigit() or int(op.stem) in tiles[key]:
                    continue
                tid = int(op.stem)
                t = Image.open(op).convert("RGBA")
                if t.size != (size, size):
                    bad_size.add(f"{op} {t.size}")
                    continue
                sheet.paste(t, ((tid % COLS) * size, (tid // COLS) * size))
                n_new += 1
        targets = [out / f"{row['name']}.hd.png"] if multi[row["name"]] == 1 else \
            [out / pk / f"{row['name']}.hd.png" for pk in row["packages"].split()]
        for dst in targets:
            if dst.exists() and not force:
                print(f"{dst} exists: skipped (use --force)")
                continue
            dst.parent.mkdir(parents=True, exist_ok=True)
            guard.save_image(sheet, dst)
        used = len(tiles[key])
        print(f"{key}: {sheet.width}x{sheet.height}, tiles {used}: shared items {n_rep}, overrides {n_ovr}, "
              f"game tile enlarged {used - n_rep - n_ovr}, new layout slots {n_new} ->{', '.join(str(t.relative_to(out)) for t in targets)}")
    for b in sorted(bad_size):
        print(f"WRONG SIZE (need {size}x{size}), skipped: {b}")
    return 0


def do_compare(root, a_path, key):
    sheets, tiles, _ = read_catalog(root)
    row = sheets[key]
    built = root / "pack" / (f"{row['name']}.hd.png" if Path(root / "pack" / f"{row['name']}.hd.png").exists()
                             else Path(row["packages"].split()[0]) / f"{row['name']}.hd.png")
    a, b = Image.open(a_path).convert("RGBA"), Image.open(built).convert("RGBA")
    if a.size != b.size:
        print(f"sizes differ: {a.size} vs {b.size}")
        return 1
    size = a.width // COLS
    diff = [tid for tid in tiles[key] if not same(cell(a, tid, size), cell(b, tid, size))]
    print(f"compare {Path(a_path).name} vs {built.relative_to(root)}: {len(tiles[key])} game tiles, "
          f"{len(diff)} differ{': ' + ' '.join(map(str, diff[:30])) if diff else ''}")
    return 0 if not diff else 1


def main(argv):
    root = Path(argv[argv.index("--root") + 1]) if "--root" in argv else paths.KARNAK_BUILD / "mobile_v5"
    scale = int(argv[argv.index("--scale") + 1]) if "--scale" in argv else 4
    only = argv[argv.index("--sheets") + 1].split(",") if "--sheets" in argv else None
    global EXPORT
    if "--export" in argv:
        EXPORT = Path(argv[argv.index("--export") + 1])
    else:
        GAME.require_export()   # stops with what is missing and how to fix it
    skip = {argv[i + 1] for i, a in enumerate(argv) if a in ("--root", "--scale", "--sheets", "--export") and i + 1 < len(argv)}
    args = [a for a in argv if not a.startswith("--") and a not in skip]
    force = "--force" in argv
    if args[:1] == ["import"] and len(args) == 3:
        return do_import(root, args[1], args[2], force)
    if args[:1] == ["build"]:
        return do_build(root, scale, only, "--all" in argv, force)
    if args[:1] == ["compare"] and len(args) == 3:
        return do_compare(root, args[1], args[2])
    print(__doc__)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
