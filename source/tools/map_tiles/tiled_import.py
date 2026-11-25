r"""Read the Tiled working maps (tiled_export.py) back: every cell as drawn ("applies as drawn";
the user edits cell by cell, moving, adding, erasing and restyling single cells).
Per cell of the layers Bottom / Ground / Upper / Ceil:
  - unchanged                        -> stays
  - a build tile put there           -> that game tile (moved or copied; it keeps its art and animation)
  - erased                           -> empty
  - a mobile tile stamped there      -> that art in this cell only (choice "slot <sheet> <n>", "obj <file> <x> <y>",
                                        "file <name>"; Tiled flips: "flipx" (mirrored slot) or "xf <H|V|D>")
  - a flipped/rotated build tile    -> "copy <tid> xf <H|V|D>" in a slot of its own
  - a "game (reference)" tile        -> ignored, listed
Tile numbers for stamped art, per sheet (all maps that use the sheet count, edited or not):
  - every copy of a game tile restamped with the same art, and no cell left showing it -> a normal pick for that tile
    (manual_choices.csv; as with every pick it also applies to the same image on the other reviewed sheets)
  - otherwise the art gets its own tile number in an empty slot of the sheet (a manual_choices row noted "layout
    slot"; the same art reuses the same slot on later imports)
Writes (not with --dry): the project's choices (Karnak: manual_choices.csv; other projects: choices.csv), and Working\tilesets\Karnak\layouts\<Map_NNNNN>\<SubMap>\tilemap.json for each
map whose cells differ from the game's (base: the existing layout, else Color Corrected Tiles' file, else the export;
only the tile layers' cells change). Install: copy layouts\ to the game (see Working\tilesets\Karnak\README.md).
Usage: python tiled_import.py <Map_....tmj> [...] [--dry] [--date YYYY-MM-DD]
"""
import csv, json, sys
from collections import Counter, defaultdict
from datetime import date
from pathlib import Path
from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parent))
import guard   # your saved files are never changed without consent
import atlas_record as ar
from project import P
from layouts import CC, EXPORT, LAYOUTS, cell_layer_tiles, flip, layout_file, walk
sys.path.insert(0, str(__import__('pathlib').Path(__file__).resolve().parents[1]))  # tools folder
import paths   # project folder layout (tools\paths.py)

PROJECT = Path(__file__).resolve().parents[2]
M = paths.MOBILE_MATCHING
CAT = P.catalog
ADDED = P.added
GID, FLAGS, FLIPX = 0x1FFFFFFF, 0xE0000000, 0x80000000
LAYERS = ("Bottom", "Ground", "Upper", "Ceil")          # Tiled layer names; the game's are <name>Layer


def tileset_of(sets, g):
    t = max((s for s in sets if s["firstgid"] <= g), key=lambda s: s["firstgid"])
    return t, g - t["firstgid"]


def choice(ts, k, flags):
    props = {p["name"]: p["value"] for p in ts.get("properties", [])}
    kind = props.get("kind")
    if kind == "slot":
        c = f"slot {ts['name']} {k}"
    elif kind == "obj":
        c = f"obj {ts['name']} {(k % ts['columns']) * 32} {(k // ts['columns']) * 32}"
    elif kind == "file":
        c = f"file {props['file']}"
    else:
        return None
    return c + xf(flags, kind == "slot")


def xf(flags, slot=False):
    """Tiled flip flags as a choice suffix: plain mirror of a slot = " flipx" (older token), else " xf <H|V|D>"""
    code = "".join(x for x, m in (("H", 0x80000000), ("V", 0x40000000), ("D", 0x20000000)) if flags & m)
    if not code:
        return ""
    return " flipx" if code == "H" and slot else " xf " + code


def door_cells(m):
    """cell index -> (door tid, [frame tids]) for every cell of a TileAnimRegion (the game's doors: an OpenTrigger
    plays the region, every animated tile in it steps to its next frame; "all_layers": true [verified, map files
    2026-11-20]), with the animated tile the game has in that cell (any layer)"""
    ef = export_tilemap(m)
    gj = json.load(open(ef, encoding="utf-8"))
    w = gj["width"]
    ganim = {t["id"]: [a["tileid"] for a in t["animation"]] for t in gj["tilesets"][0].get("tiles", []) if "animation" in t}
    first = gj["tilesets"][0]["firstgid"]
    out = {}
    for ent in ef.parent.glob("*.json"):
        try:
            d = json.load(open(ent, encoding="utf-8"))
        except Exception:
            continue
        def objs(ls):
            for l in ls:
                if "layers" in l:
                    yield from objs(l["layers"])
                for o in l.get("objects", []):
                    yield o
        for o in objs(d.get("layers", []) if isinstance(d, dict) else []):
            if o.get("name") != "TileAnimRegion":
                continue
            x0, y0 = int(o["x"]) // 16, int(o["y"]) // 16
            for y in range(y0, y0 + max(1, int(o.get("height", 16)) // 16)):
                for x in range(x0, x0 + max(1, int(o.get("width", 16)) // 16)):
                    i = y * w + x
                    for l in walk(gj["layers"]):
                        g = l.get("data", [0] * (i + 1))[i] if "data" in l else 0
                        if g and (g - first) in ganim:
                            gi = g - first
                            fr = []
                            for a in ganim[gi]:
                                if a != gi and a not in fr:
                                    fr.append(a)
                            out[i] = (flip(gi), [flip(a) for a in fr])
    return out


def frame_layers(doc):
    """every tile layer named "<Layer> frame <k>" inside any group named "animation frames" (any depth)"""
    def go(ls, inside):
        for l in ls:
            if l.get("type") == "group":
                yield from go(l["layers"], inside or l["name"] == "animation frames")
            elif inside and l.get("type") == "tilelayer":
                parts = l["name"].split(" ")
                if len(parts) == 3 and parts[1] == "frame" and parts[0] in LAYERS and parts[2].isdigit():
                    yield l, parts[0], int(parts[2])
    return list(go(doc["layers"], False))


def read_frames(f, m, sheet, out, notes, final):
    """stamps in the "animation frames" layers (tiled_frames.py; the user may split and move the groups): layer
    "<Layer> frame <k>" at an animated cell = art for that frame's tile (exact, same tile number: the animation needs it).
    Doors: in a door region, the layer with a frame stamp is the door's layer; that layer's cell gets
    the game's door tile (closed art = the user's stamp there, open = the frame stamp); other layers keep plain tiles."""
    from tiled_frames import frames_of
    doc = json.loads(f.read_text(encoding="utf-8"))
    fls = frame_layers(doc)
    if not fls:
        return
    fr, *_ = frames_of(m)
    # cells whose current layout lost the game's animated tile: the frames of the tile the game has there
    orig = json.loads(f.with_suffix(".orig.json").read_text(encoding="utf-8"))
    gj = json.load(open(export_tilemap(m), encoding="utf-8"))
    ganim = {t["id"]: [a["tileid"] for a in t["animation"]] for t in gj["tilesets"][0].get("tiles", []) if "animation" in t}
    for L, od in orig["layers"].items():
        for i, o in enumerate(od):
            if (L, i) in fr or not o & GID:
                continue
            gi = flip(orig["keys"][(o & GID) - 1][1])
            if gi in ganim:
                seen = [x for n2, x in enumerate(ganim[gi]) if x != gi and x not in ganim[gi][:n2]]
                if seen:
                    fr[(L, i)] = [flip(x) for x in seen]
    doors = door_cells(m)
    sets = sorted(doc["tilesets"], key=lambda s: s["firstgid"])
    paint = painted_tiles(f, sets)
    w = doc["width"]
    door_layer = {}
    for l, L, k in fls:
        k -= 2
        for i, g in enumerate(l["data"]):
            where = f"{m} {l['name']} {i % w},{i // w}"
            frames = doors[i][1] if i in doors else fr.get((L, i))
            if not frames or k >= len(frames):
                if g & GID:
                    notes["frame layer: tile placed where that cell has no such frame (ignored)"].append(where)
                continue
            if not g & GID:
                continue                           # nothing stamped: frame unchanged
            ts, j = tileset_of(sets, g & GID)
            c = paint_choice(paint, sets, g)
            if c:
                notes["frame tile painted in its atlas (your art in the game)"].append(f"{where} {c}")
            elif ts["name"] == "frames (current)":
                continue
            else:
                c = choice(ts, j, g)
            if c is None:
                notes[f"frame layer: '{ts['name']}' tile placed (ignored)"].append(where)
                continue
            if i in doors:
                if door_layer.setdefault(i, L) != L:
                    notes["door: frame stamps on two layers (first one used)"].append(where)
                    continue
            out[(sheet, frames[k])].setdefault(c, []).append(where)
    # door cells: the door layer's cell becomes the game's door tile, its stamp the closed art
    for i, L in door_layer.items():
        tid = doors[i][0]
        fc = final[L][i]
        where = f"{m} {L} {i % w},{i // w}"
        if fc is None:
            notes["door: open frame stamped but no closed door on that layer (closed door = current art)"].append(where)
        elif fc[0] == "a":
            out[(sheet, tid)].setdefault(fc[1], []).append(where)
        elif fc[1] != tid:
            notes["door: build tile on the door layer (it becomes the door tile, art unchanged)"].append(where)
        final[L][i] = ("t", tid)
        for L2 in LAYERS:
            if L2 != L and L2 in final and final[L2][i] == ("t", tid):
                notes["door: the door tile is also on another layer there (it opens too)"].append(f"{m} {L2} {i % w},{i // w}")
        notes["door on layer " + L].append(where)


def painted_tiles(f, sets):
    """firstgid -> {tile id: art} of the atlas tile sets (build, frames) whose tiles you painted (atlas_record.py)"""
    return {ts["firstgid"]: ar.painted((f.parent / ts["image"]).resolve(), ts, "--dry" not in sys.argv)
            for ts in sets if ar.is_atlas_set(ts)}


def paint_choice(paint, sets, g):
    """choice "file <atlas>_<tile id>.png" (+ flips) for a painted atlas tile, its art saved in the added-tiles folder;
    None if the tile is not a painted one"""
    ts, k = tileset_of(sets, g & GID)
    art = paint.get(ts["firstgid"], {}).get(k)
    if art is None:
        return None
    fname = f"{Path(ts['image']).stem}_{k}.png"
    if "--dry" not in sys.argv and not ((ADDED / fname).exists()
                                        and Image.open(ADDED / fname).convert("RGBA").tobytes() == art.tobytes()):
        ADDED.mkdir(parents=True, exist_ok=True)
        guard.save_image(art, ADDED / fname)
    return f"file {fname}" + xf(g & FLAGS)


def export_tilemap(mapname):
    return next(Path(EXPORT).glob(f"map_*/Assets/GameAssets/Serial/Res/Map/**/{mapname}/tilemap.json"))


def build_key(f, ts, k, keys):
    """(sheet, tid) of tile k of a "build (current)" tileset: this map's own (atlas/<this map>_build.png) or another
    map's, read from that map's .orig.json"""
    other = Path(ts["image"]).stem.replace("_build", "")
    if other == f.stem or other == json.loads(f.with_suffix(".orig.json").read_text(encoding="utf-8"))["map"]:
        return tuple(keys[k])
    ok = json.loads((f.parent / f"{other}.orig.json").read_text(encoding="utf-8"))["keys"]
    return tuple(ok[k])


def read_tiled(f, notes):
    """map name, sheet, {layer: [final cell]}, {layer: [origin tid or None]}; final = None | ("t", tid) | ("a", choice)"""
    doc = json.loads(f.read_text(encoding="utf-8"))
    orig = json.loads(f.with_suffix(".orig.json").read_text(encoding="utf-8"))
    keys = [tuple(k) for k in orig["keys"]]
    sheets = {s for s, _ in keys}
    if len(sheets) != 1:
        sys.exit(f"{f.stem}: {len(sheets)} sheets; layouts support one sheet per map")
    sheet = sheets.pop()
    sets = sorted(doc["tilesets"], key=lambda s: s["firstgid"])
    layers = {l["name"]: l for l in doc["layers"] if l.get("type") == "tilelayer"}
    w = doc["width"]
    final, origin = {}, {}
    paint = painted_tiles(f, sets)
    for name in LAYERS:
        od, nd = orig["layers"][name], layers[name]["data"]
        fl, ol = [], []
        for i, (o, n) in enumerate(zip(od, nd)):
            ot = keys[(o & GID) - 1][1] if o & GID else None
            ol.append(ot)
            where = f"{f.stem} {name} {i % w},{i // w}"
            if n & GID:
                pc = paint_choice(paint, sets, n)
                if pc:
                    # a tile you painted in its atlas: every cell showing it shows your art (what Tiled shows)
                    fl.append(("a", pc))
                    notes["tile painted in its atlas (your art in the game)"].append(f"{where} {pc}")
                    continue
            if o == n:
                fl.append(None if ot is None else ("t", ot))
                continue
            if not n & GID:
                fl.append(None)
                notes["erased"].append(where)
                continue
            ts, k = tileset_of(sets, n & GID)
            if "source" in ts:
                # a tileset made in Tiled (external .tsx): not read; tiles go in via the added-tiles folder
                notes[f"tile from tileset file {ts['source']} (ignored, cell unchanged; put the PNG in the added-tiles folder and run tiled_add_tiles.py)"].append(where)
                fl.append(None if ot is None else ("t", ot))
                continue
            own =Path(ts.get("image", "")).stem.replace("_build", "") in (f.stem, orig["map"])
            if ts["name"] == "build (current)" and own and k >= len(keys):
                # user-drawn tile in an empty cell of the build atlas (2026-11-21, town Ceil): saved as an added tile
                # (upright, as in the atlas) and placed like one: file <Map>_build_<k>.png
                fname = f"{orig['map']}_build_{k}.png"
                if "--dry" not in sys.argv:
                    T, cols = ts["tilewidth"], ts["columns"]
                    im = Image.open((f.parent / ts["image"]).resolve()).convert("RGBA")
                    guard.save_image(im.crop((k % cols * T, k // cols * T, k % cols * T + T, k // cols * T + T)), ADDED / fname)
                fl.append(("a", f"file {fname}" + xf(n & FLAGS)))
                notes["tile drawn into the build atlas (added tile)"].append(f"{where} {fname}")
                continue
            if ts["name"] == "build (current)":
                # which map's build tiles: tiles copied over from another Tiled map bring that map's tileset along
                # (2026-11-20: Map_20061_4 had Map_20061_3's, sheet 149 in a sheet-150 map)
                bs, bt = build_key(f, ts, k, keys)
                if bs != sheet:
                    fl.append(("a", f"copy {bs} {bt}" + xf(n & FLAGS)))
                    notes["build tile from another sheet (own slot, its built art)"].append(f"{where} {bs}:{bt}")
                elif n & FLAGS:
                    # flipped/rotated build tile: its built art transformed, in a slot of its own (no flips in game data)
                    fl.append(("a", f"copy {bt}" + xf(n & FLAGS)))
                    notes["flipped/rotated build tile (own slot)"].append(where)
                else:
                    fl.append(("t", bt))
                    notes["build tile placed"].append(where)
                continue
            c = choice(ts, k, n)
            if c is None:
                notes[f"'{ts['name']}' tile placed (ignored, cell unchanged)"].append(where)
                fl.append(None if ot is None else ("t", ot))
                continue
            if n & FLAGS:
                notes["flipped/rotated stamp"].append(where)
            fl.append(("a", c))
        final[name], origin[name] = fl, ol
    return orig["map"], sheet, final, origin, w


def sheet_maps(sheet):
    """(package, sub-map) of every map that uses the sheet"""
    row = next(r for r in csv.DictReader(open(CAT / "sheets.csv", encoding="utf-8")) if r["sheet"] == sheet)
    out = []
    for pkg in row["packages"].split():
        for tm in Path(EXPORT).glob(f"{pkg}/Assets/GameAssets/Serial/Res/Map/*/*/tilemap.json"):
            j = json.load(open(tm, encoding="utf-8"))
            if any(Path(t["image"]).stem == row["name"] for t in j["tilesets"]):
                out.append((pkg, tm.parent.parent.name, tm.parent.name))
    return row, out


_ANIM = {}


def animated_tiles(maps):
    """project tile ids that the game animates (tileset "tiles" with frames other than the tile itself) on any of
    these maps ((package, map, sub-map) from sheet_maps)"""
    out = set()
    for pkg, mp, sub in maps:
        if sub not in _ANIM:
            j = json.load(open(export_tilemap(sub), encoding="utf-8"))
            _ANIM[sub] = {flip(t["id"]) for ts in j["tilesets"] for t in ts.get("tiles", [])
                          if any(a["tileid"] != t["id"] for a in t.get("animation", []))}
        out |= _ANIM[sub]
    return out


def free_slots(row, pkg, used):
    png = next(Path(EXPORT).glob(f"{pkg}/Assets/GameAssets/Serial/Res/Map/*/{row['name']}.png"))
    im = Image.open(png).convert("RGBA")
    cols = im.width // 16
    return [t for t in range(cols * (im.height // 16))
            if t not in used and im.crop(((t % cols) * 16, (t // cols) * 16, (t % cols) * 16 + 16, (t // cols) * 16 + 16)).getextrema()[3][1] == 0]


def see_through(c, sheets):
    """clearly see-through pixels (alpha < 128); the mobile sheets' alpha-254 pixels are invisible and everywhere"""
    if c.startswith("copy"):
        return False
    if not P.mobile:
        from write_choices import file_art, split_xf
        return c.startswith("file") and file_art(split_xf(c)[0][5:].strip(), 32).getextrema()[3][0] < 128
    import write_mobile_replacements as wmr
    cell = wmr.choice_cell(c.split(), None, sheets)
    return cell.getchannel("A").getextrema()[0] < 128   # any pixel with alpha < 128


def main():
    a = sys.argv[1:]
    a_args = a
    dry = "--dry" in a
    day = a[a.index("--date") + 1] if "--date" in a else date.today().isoformat()
    notes = defaultdict(list)
    edited = {}
    frame_stamps = defaultdict(dict)             # (sheet, frame tid) -> {choice: [where]}
    for f in [Path(x) for x in a if x.endswith(".tmj")]:
        m, sheet, final, origin, w = read_tiled(f, notes)
        edited[m] = (sheet, final, origin, w)
        read_frames(f, m, sheet, frame_stamps, notes, final)
    path = P.choices   # Karnak: _Mobile Files\manual_choices.csv; other projects: <project>\choices.csv
    rows = list(csv.DictReader(open(path, encoding="utf-8"))) if path.exists() else []
    have = {}
    for i, r in enumerate(rows):                 # a Tiled row wins over an older row for the same tile
        k = (r["sheet"], int(r["tid"]))
        if k not in have or "(Tiled:" in r["note"]:
            have[k] = i
    picks, new_rows, tids = [], [], {}          # tids: (map, layer) -> final tid list
    summary = Counter()
    art_cache = {}
    for sheet in sorted({v[0] for v in edited.values()}):
        row, maps = sheet_maps(sheet)
        cells = []                               # (map, layer, i, final, origin)
        for pkg, mp, sub in maps:
            if sub in edited:
                _, final, origin, _ = edited[sub]
                for L in LAYERS:
                    for i, (fc, oc) in enumerate(zip(final[L], origin[L])):
                        cells.append((sub, L, i, fc, oc))
            else:
                for i, d in cell_layer_tiles(sub).items():
                    for gl, (s, t) in d.items():
                        cells.append((sub, gl.replace("Layer", ""), i, ("t", t), t))
        refs = Counter(fc[1] for *_, fc, _ in cells if fc and fc[0] == "t")
        by_origin = defaultdict(list)
        for *_, fc, oc in cells:
            if oc is not None:
                by_origin[oc].append(fc)
        want = defaultdict(list)
        for mp, L, i, fc, oc in cells:
            if fc and fc[0] == "a":
                want[fc[1]].append((mp, L, i, oc))
        assign = {}                              # choice -> tid
        # stamped art that this tile already shows (its current pick): keep the tile
        cur = {(int(r["tid"])): r["choice"] for r in rows if r["sheet"] == sheet}
        tiled_rows = {int(r["tid"]) for r in rows if r["sheet"] == sheet and "(Tiled:" in r["note"]}
        # whole-tile picks
        for c, where in sorted(want.items()):
            if c.startswith("copy"):
                continue                         # always its own slot (it reads another tile's built art)
            for oc in sorted({o for *_, o in where if o is not None}):
                if refs[oc] == 0 and all(x == ("a", c) for x in by_origin[oc]) and oc not in assign.values():
                    assign[c] = oc               # exactly as stamped (no changes without an okay)
                    picks.append((sheet, oc, c, where))
                    if oc in animated_tiles(maps):
                        # the game plays the tile's frames there, not its own art (FF6 test 2026-11-24: tile 824
                        # looked empty on the sheet, the game showed its gear frames)
                        notes["pick on an animated tile (the game shows its frames: stamp them in the 'animation frames' layers, tiled_frames.py)"].append(f"{sheet}:{oc}")
                    break
        # layout slots: existing ones first, then empty slots
        # registry keyed by the art without its blend token (" blend <tid>" is added below)
        slot_rows = {r["choice"].split(" blend ")[0]: i for i, r in enumerate(rows) if r["sheet"] == sheet and r["note"].startswith("layout slot")}
        used = set(refs) | {int(r["tid"]) for r in rows if r["sheet"] == sheet}
        free = None
        for c, where in sorted(want.items()):
            if c in assign:
                continue
            # the tile already shows exactly this art: only if its row is a Tiled row too (built exactly as stamped);
            # an older pick with the same choice is built with SHADOW_FIX etc. (2026-11-20: 150:907 = mc09_0 71
            # looked different from the user's stamp of mc09_0 71)
            same = [o for *_, o in where if o is not None and cur.get(o) == c and refs[o] > 0 and o in tiled_rows]
            if same and len({o for *_, o in where}) == 1:
                assign[c] = same[0]
                continue
            # edge blending ("keep edge blending on for all tiles"): against the game tile the art
            # replaces; one only if every cell of this art replaced the same game tile
            origins = {o for *_, o in where}
            blend = f" blend {origins.pop()}" if c.startswith("slot") and len(origins) == 1 and None not in origins else ""
            if c.startswith("slot") and not blend:
                notes["layout slot not edge-blended (its cells replaced different game tiles, or none)"].append(f"{sheet} {c}")
            if c in slot_rows:
                ri = slot_rows[c]
                assign[c] = int(rows[ri]["tid"])
                if rows[ri]["choice"] != c + blend:
                    rows[ri]["choice"] = c + blend
                    summary["layout slots given edge blending"] += 1
                continue
            if free is None:
                free = free_slots(row, maps[0][0], used)
            if not free:
                sys.exit(f"{sheet}: no empty slot left for {c}")
            t = free.pop(0)
            assign[c] = t
            mp, L, i, _ = where[0]
            new_rows.append({"sheet": sheet, "tid": str(t), "choice": c + blend,
                             "note": f"layout slot {day} (Tiled: {mp} {L} cell {i}" + (f" +{len(where) - 1} cells" if len(where) > 1 else "") + ")"})
            if L == "Bottom" and see_through(c, art_cache):
                notes["see-through art on the floor layer (nothing under it: shows the backdrop)"].append(f"{c} at {mp} cell {i}")
        for mp, (sh, final, origin, w) in edited.items():
            if sh != sheet:
                continue
            for L in LAYERS:
                out = []
                for fc in final[L]:
                    out.append(None if fc is None else fc[1] if fc[0] == "t" else assign[fc[1]])
                tids[(mp, L)] = out
                summary["cells now showing stamped art"] += sum(1 for fc in final[L] if fc and fc[0] == "a")
        summary["new layout slots"] += sum(1 for r in new_rows if r["sheet"] == sheet)
    # animation frames: a pick for the frame tile itself
    for (sheet, t), cs in sorted(frame_stamps.items()):
        if len(cs) > 1:
            print(f"CONFLICT frame {sheet}:{t}: " + "; ".join(f"{c} at {', '.join(v)}" for c, v in cs.items()) + " (nothing written)")
            continue
        (c, where), = cs.items()
        if any(p[0] == sheet and p[1] == t for p in picks):
            print(f"CONFLICT frame {sheet}:{t}: also picked on the map (nothing written for the frame)")
            continue
        mp = where[0].split(" ")[0]
        picks.append((sheet, t, c, [(mp, "frame", where[0], None)] + [(mp, "frame", x, None) for x in where[1:]]))
        summary["animation frame picks"] += 1
    summary["whole-tile picks"] = len(picks)
    # report
    for sheet, t, c, where in picks:
        old = rows[have[(sheet, t)]]["choice"] if (sheet, t) in have else None
        print(f"pick {sheet}:{t} -> {c}" + (f"   (was: {old})" if old else "") + f"   [{len(where)} cell(s)]")
    for r in new_rows:
        print(f"new slot {r['sheet']}:{r['tid']} = {r['choice']}   {r['note']}")
    for k, v in notes.items():
        print(f"note: {k}: {len(v)}" + (": " + "; ".join(v[:6]) + (" ..." if len(v) > 6 else "") if v else ""))
    # layouts
    written = []
    for mp, (sheet, final, origin, w) in edited.items():
        ef = export_tilemap(mp)
        pkg, sub = ef.parent.parent.name, ef.parent.name
        lf = layout_file(sub, pkg)
        ccf = CC / ef.parts[ef.parts.index("StreamingAssets") + 2] / Path(*ef.parts[ef.parts.index("Assets"):])
        base = lf if lf.exists() else ccf if ccf.exists() else ef
        j = json.loads(base.read_text(encoding="utf-8-sig"))
        if len(j["tilesets"]) != 1:
            sys.exit(f"{mp}: {len(j['tilesets'])} tilesets in the game file")
        first = j["tilesets"][0]["firstgid"]
        changed = 0
        for l in walk(j["layers"]):
            L = l["name"].replace("Layer", "")
            if L not in LAYERS or (mp, L) not in tids:
                continue
            new = [0 if t is None else flip(t) + first for t in tids[(mp, L)]]
            changed += sum(1 for x, y in zip(l["data"], new) if x != y)
            l["data"] = new
        ex = json.load(open(ef, encoding="utf-8"))
        differs = any(l["data"] != m["data"] for l, m in zip(walk(j["layers"]), walk(ex["layers"])) if "data" in l)
        print(f"layout {mp}: {changed} cell(s) differ from {base.name if base == lf else ('Color Corrected Tiles' if base == ccf else 'the export')}"
              + ("" if differs or lf.exists() else " (same as the game: no layout file)"))
        if differs or lf.exists():
            written.append((lf, j))
    print({k: v for k, v in summary.items()})
    # every file the import would write, checked before the first one is written (guard.py: yours need consent)
    obj_files = []
    for f in [Path(x) for x in a_args if x.endswith(".tmj")]:
        m = json.loads(f.read_text(encoding="utf-8"))
        mname = next((q["value"] for q in m.get("properties", []) if q["name"] == "map"), f.stem)
        obj_files += [tiled_objects_out() / f"{mname}.json", tiled_objects_out() / f"{mname}_objects.png"]
    outs = [path] + [lf for lf, _ in written] + obj_files
    if dry:
        bad = [p for p in outs if not guard.mine(p)]
        if bad:
            print("NEEDS YOUR CONSENT before an import: these files were saved or edited outside the tools:")
            for p in bad:
                print(f"   {p}")
        print("dry run, nothing written")
        return
    guard.need(outs)
    for sheet, t, c, where in picks:
        mp, L, i, _ = where[0]
        r = {"sheet": sheet, "tid": str(t), "choice": c, "note": f"user pick {day} (Tiled: {mp} {L} cell {i}" + (f" +{len(where) - 1} cells" if len(where) > 1 else "") + ")"}
        k = (sheet, t)
        if k in have and "(Tiled:" in rows[have[k]]["note"]:
            rows[have[k]] = r                    # an earlier Tiled pick for this tile: replaced
        else:
            # an older (non-Tiled) row stays: it still gives the same image on the other sheets its art; the writer
            # uses the Tiled row for this tile only
            rows.append(r); have[k] = len(rows) - 1
    rows += new_rows
    import io
    buf = io.StringIO(newline="")
    wr = csv.DictWriter(buf, ["sheet", "tid", "choice", "note"], lineterminator="\r\n"); wr.writeheader(); wr.writerows(rows)
    guard.write_bytes(path, buf.getvalue().encode("utf-8"))
    for lf, j in written:
        lf.parent.mkdir(parents=True, exist_ok=True)
        guard.write_text(lf, json.dumps(j, ensure_ascii=False))
    print(f"written: {path.name} ({len(picks)} pick(s), {len(new_rows)} new slot(s)), {len(written)} layout file(s) in {LAYOUTS}")
    # tile objects (tiled_objects.py): one file per map; a map whose objects were all removed gets an empty list
    import tiled_objects
    for f in [Path(x) for x in a_args if x.endswith(".tmj")]:
        d = tiled_objects.export(f)
        m = json.loads(f.read_text(encoding="utf-8"))
        mname = next((q["value"] for q in m.get("properties", []) if q["name"] == "map"), f.stem)
        if d is None and (tiled_objects.OUT / f"{mname}.json").exists():
            guard.write_text(tiled_objects.OUT / f"{mname}.json", json.dumps({"map": mname, "width": m["width"], "height": m["height"],
                                                                         "atlas": f"{mname}_objects.png", "objects": []}))
        if d:
            print(f"objects: {mname}: {len(d['objects'])} -> {tiled_objects.OUT}")


def tiled_objects_out():
    import tiled_objects
    return tiled_objects.OUT


if __name__ == "__main__":
    main()
