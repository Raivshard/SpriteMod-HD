r"""Tiled working maps for picking mobile art by area ("yes, use tiled"). Local only: mobile art is
personal use, never upload.
Per PR map, <out>\<Map>.tmj (Tiled JSON, 32 px tiles) with:
  - layers Bottom / Ground / Upper / Ceil drawn with the current build (tileset "build": each distinct tile of the map,
    upright, with the property "tile" = sheet:tid and "cells" = how many cells of this map use it)
  - group "game (reference)", hidden: the same layers with the game's own tiles (tileset "game")
  - every mobile tileset as a Tiled tileset: Tilesets\*.dat.png (slots), Field Objects\*.dat.png (32 px cells),
    Tilesets\added, modified\*.png (your own tiles)
Pick by stamping a mobile tile INTO the layer over the wrong tile; tiled_import.py reads the changes.
<out>\<Map>.orig.json keeps what was exported (for the import diff). Re-export only after importing (it overwrites).
Usage: python tiled_export.py <Map_...> [...] [--project <folder>] [--root <build folder>] [--out <tiled folder>] [--force]
(defaults from the map project, project.py; Karnak: build\Karnak\mobile_v5, Working\tilesets\Karnak\tiled)
"""
import json, os, sys
from pathlib import Path
from PIL import Image

sys.path.insert(0, str(Path(__file__).parent))
import guard   # your saved files are never changed without consent
from project import P
from layouts import EXPORT, GID, walk, load_tilemap, cell_layer_tiles
sys.path.insert(0, str(__import__('pathlib').Path(__file__).resolve().parents[1]))  # tools folder
import paths   # project folder layout (tools\paths.py)

PROJECT = Path(__file__).resolve().parents[2]
LAYERS = ("BottomLayer", "GroundLayer", "UpperLayer", "CeilLayer")
T = 32
FLAGS = 0xE0000000


def tile_art(pack, sheet, tid, game_sheet):
    """upright 32 px art of a PR tile: the built HD sheet (current build) or the game's 16 px sheet enlarged"""
    r, c = tid // 32, tid % 32
    if pack is not None and (pack / f"{sheet}.hd.png").exists():
        im = Image.open(pack / f"{sheet}.hd.png").convert("RGBA")
        s = im.width // 32
        t = im.crop((c * s, r * s, c * s + s, r * s + s)).resize((T, T), Image.NEAREST)
    else:
        t = game_sheet.crop((c * 16, r * 16, c * 16 + 16, r * 16 + 16)).resize((T, T), Image.NEAREST)
    return t.transpose(Image.FLIP_TOP_BOTTOM)


def atlas(tiles, path):
    cols = 16
    rows = (len(tiles) + cols - 1) // cols
    im = Image.new("RGBA", (cols * T, max(1, rows) * T), (0, 0, 0, 0))
    for i, t in enumerate(tiles):
        im.paste(t, ((i % cols) * T, (i // cols) * T))
    guard.save_image(im, path)
    if Path(path).stem.endswith(("_build", "_frames")):
        import atlas_record
        atlas_record.save(path, im)   # what the tools wrote: a tile you paint over it later is your edit
    return im.size, cols


def rel(p, base):
    return os.path.relpath(p, base).replace("\\", "/")


def mobile_tilesets(out, first):
    sets = []
    # mobile sheets (project.json "mobile", Karnak only): from the project's paths "mobile_tilesets" / "mobile_objects"
    files = ([("slot", p) for p in sorted(P.mobile_tilesets.glob("*.dat.png"))]
             + [("obj", p) for p in sorted(P.mobile_objects.glob("*.dat.png"))]) if P.mobile else []
    files += [("file", p) for p in sorted(P.added.glob("*.png"))]
    for kind, p in files:
        w, h = Image.open(p).size
        cols, rows = w // T, h // T
        if cols == 0 or rows == 0:
            continue
        name = p.name.replace(".dat.png", "") if kind != "file" else "added: " + p.stem
        sets.append({"firstgid": first, "name": name, "image": rel(p, out), "imagewidth": w, "imageheight": h,
                     "tilewidth": T, "tileheight": T, "columns": cols, "tilecount": cols * rows, "margin": 0, "spacing": 0,
                     "properties": [{"name": "kind", "type": "string", "value": kind}, {"name": "file", "type": "string", "value": p.name}]})
        first += cols * rows
    return sets


def export(mapname, root, out, force):
    tmj = out / f"{mapname}.tmj"
    if tmj.exists() and not force:
        sys.exit(f"{tmj} exists (import it first, then --force)")
    # every file this export writes, checked before the first one is written
    guard.need([tmj, out / f"{mapname}.orig.json", out / "atlas" / f"{mapname}_build.png", out / "atlas" / f"{mapname}_game.png"])
    f = next(Path(EXPORT).glob(f"map_*/Assets/GameAssets/Serial/Res/Map/**/{mapname}/tilemap.json"))
    j = load_tilemap(f)   # edited layout (Working\tilesets\Karnak\layouts) if there is one
    w, h = j["width"], j["height"]
    game_sheets = {Path(t["image"]).stem: Image.open(f.parent.parent / Path(t["image"]).name).convert("RGBA") for t in j["tilesets"]}
    lt = cell_layer_tiles(mapname)
    raw = {l["name"]: l["data"] for l in walk(j["layers"]) if l["name"] in LAYERS}
    keys, count = [], {}
    for i, d in sorted(lt.items()):
        for L in LAYERS:
            if L in d:
                k = d[L]
                if k not in count:
                    keys.append(k)
                count[k] = count.get(k, 0) + 1
    idx = {k: n for n, k in enumerate(keys)}
    (out / "atlas").mkdir(parents=True, exist_ok=True)
    pack = root / "pack"
    bsize, bcols = atlas([tile_art(pack, s, t, game_sheets[s]) for s, t in keys], out / "atlas" / f"{mapname}_build.png")
    gsize, gcols = atlas([tile_art(None, s, t, game_sheets[s]) for s, t in keys], out / "atlas" / f"{mapname}_game.png")
    n = len(keys)
    tsb = {"firstgid": 1, "name": "build (current)", "image": f"atlas/{mapname}_build.png", "imagewidth": bsize[0], "imageheight": bsize[1],
           "tilewidth": T, "tileheight": T, "columns": bcols, "tilecount": n, "margin": 0, "spacing": 0,
           "tiles": [{"id": k, "properties": [{"name": "tile", "type": "string", "value": f"{s.replace('map_chip_', '')}:{t}"},
                                              {"name": "cells", "type": "int", "value": count[(s, t)]}]} for k, (s, t) in enumerate(keys)]}
    tsg = dict(tsb, firstgid=1 + n, name="game (reference)", image=f"atlas/{mapname}_game.png", imagewidth=gsize[0], imageheight=gsize[1])
    first = 1 + 2 * n
    sets = [tsb, tsg] + mobile_tilesets(out, first)

    def layer(L, base, lid):
        data = []
        for i in range(w * h):
            g = raw.get(L, [0] * (w * h))[i]
            k = lt.get(i, {}).get(L)
            data.append(0 if (g & GID) == 0 or k is None else (base + idx[k]) | (g & FLAGS))
        return {"id": lid, "name": L.replace("Layer", ""), "type": "tilelayer", "width": w, "height": h, "x": 0, "y": 0,
                "opacity": 1, "visible": True, "data": data}

    build_layers = [layer(L, 1, k + 1) for k, L in enumerate(LAYERS)]
    game_layers = [dict(layer(L, 1 + n, k + 11), name="game " + L.replace("Layer", "")) for k, L in enumerate(LAYERS)]
    doc = {"type": "map", "version": "1.10", "tiledversion": "1.12.2", "orientation": "orthogonal", "renderorder": "right-down",
           "width": w, "height": h, "tilewidth": T, "tileheight": T, "infinite": False, "nextlayerid": 100, "nextobjectid": 1,
           "properties": [{"name": "map", "type": "string", "value": mapname}, {"name": "root", "type": "string", "value": str(root)}],
           "layers": [{"id": 20, "name": "game (reference)", "type": "group", "visible": False, "opacity": 1, "x": 0, "y": 0,
                       "layers": game_layers}] + build_layers,
           "tilesets": sets}
    guard.write_text(tmj, json.dumps(doc))
    guard.write_text(out / f"{mapname}.orig.json", json.dumps({"map": mapname, "keys": [[s, t] for s, t in keys],
                                                          "layers": {l["name"]: l["data"] for l in build_layers}}))
    print(f"{mapname}: {w}x{h} cells, {n} distinct tiles, {len(sets) - 2} mobile tilesets -> {tmj}")


def main():
    a = sys.argv[1:]
    opt = lambda k, d: Path(a[a.index(k) + 1]) if k in a else d
    root = opt("--root", P.build)
    out = opt("--out", P.tiled)
    out.mkdir(parents=True, exist_ok=True)
    maps = [x for i, x in enumerate(a) if x.startswith("Map_") and (i == 0 or a[i - 1] not in ("--root", "--out"))]
    for m in maps:
        export(m, root, out, "--force" in a)


if __name__ == "__main__":
    main()
