r"""Tile objects from the Tiled working maps -> map_objects for the plugin (the inn clock placed as an
object with its own Tiled animation; "animations on a given tile need to be directly under the user's control").
Per map with at least one object layer (any depth, any name) writes Working\tilesets\Karnak\objects\<Map>.json and
<Map>_objects.png (every frame used, upright, packed); install = copy that folder's files to
<game>\Modules\00-Mods\SpriteMod HD\FF5\map_objects\.
  - position: the object's Tiled x, y (32 px per cell; y = the object's bottom edge, as Tiled stores tile objects)
  - frames: the tile's Tiled animation (Tileset > Tile Animation Editor; frame tiles + ms each) or the tile alone;
    flipped objects (Tiled H/V) are flipped; a resized object is drawn at its size
  - depth: from where the object layer sits among Bottom / Ground / Upper / Ceil: above Bottom 300, above Ground 200,
    above Upper 100, above Ceil 30 (the game's layers are at 350 / 250 / 150 / 50)
  - blend: the object layer's blend mode (Layer Properties > Blend Mode) and opacity (layer x object); the plugin draws
    it with SpriteMod/ObjectBlend (normal, add, multiply, screen, overlay, darken, lighten, color dodge/burn, hard/soft
    light, difference, exclusion)
  - objects above Ground are sorted with the characters by their bottom edge (plugin), like the game's furniture
  - hidden object layers and hidden objects are skipped
Usage: python tiled_objects.py <Map_....tmj> [...] [--dry]
"""
import json, sys
from pathlib import Path
from PIL import Image

PROJECT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(Path(__file__).resolve().parent))
import guard   # your saved files are never changed without consent
from project import P
OUT = P.objects
GID, FH, FV = 0x1FFFFFFF, 0x80000000, 0x40000000
Z_ABOVE = {None: 355, "Bottom": 300, "Ground": 200, "Upper": 100, "Ceil": 30}
# Tiled layer blend modes (layer "mode") -> SpriteMod/ObjectBlend _Mode
MODES = {m: k for k, m in enumerate(["normal", "add", "multiply", "screen", "overlay", "darken", "lighten", "color-dodge",
                                     "color-burn", "hard-light", "soft-light", "difference", "exclusion"])}


def tile_image(tmj, ts, k):
    """the 32 px (or the tileset's size) picture of tile k of tileset ts"""
    if "image" in ts:
        im = Image.open((tmj.parent / ts["image"]).resolve()).convert("RGBA")
        tw, th, cols = ts["tilewidth"], ts["tileheight"], ts["columns"]
        m, s = ts.get("margin", 0), ts.get("spacing", 0)
        x, y = m + (k % cols) * (tw + s), m + (k // cols) * (th + s)
        return im.crop((x, y, x + tw, y + th))
    t = next(t for t in ts.get("tiles", []) if t["id"] == k)          # image collection tileset
    return Image.open((tmj.parent / t["image"]).resolve()).convert("RGBA")


def export(tmj, dry=False):
    doc = json.loads(tmj.read_text(encoding="utf-8"))
    sets = sorted(doc["tilesets"], key=lambda s: s["firstgid"])
    m = next((p["value"] for p in doc.get("properties", []) if p["name"] == "map"), tmj.stem)
    found, below = [], None

    def go(ls, visible):
        nonlocal below
        for l in ls:
            vis = visible and l.get("visible", True)
            if l.get("type") == "group":
                go(l["layers"], vis)
            elif l.get("type") == "tilelayer" and l["name"] in Z_ABOVE:
                below = l["name"]
            elif l.get("type") == "objectgroup" and vis:
                for o in l["objects"]:
                    if o.get("gid") and o.get("visible", True):
                        found.append((o, Z_ABOVE[below], l["name"], l.get("mode", "normal"), l.get("opacity", 1)))
    go(doc["layers"], True)
    if not found:
        return None
    frames, objs, cache = [], [], {}
    for o, z, lname, mode, lop in found:
        g = o["gid"]
        ts = max((s for s in sets if s["firstgid"] <= (g & GID)), key=lambda s: s["firstgid"])
        k = (g & GID) - ts["firstgid"]
        anim = next((t.get("animation") for t in ts.get("tiles", []) if t["id"] == k and t.get("animation")), None)
        seq = [(a["tileid"], a["duration"]) for a in anim] if anim else [(k, 0)]
        w, h = int(round(o.get("width") or ts["tilewidth"])), int(round(o.get("height") or ts["tileheight"]))
        refs = []
        for tid, ms in seq:
            key = (ts["name"], tid, g & (FH | FV), w, h)
            if key not in cache:
                im = tile_image(tmj, ts, tid)
                if g & FH:
                    im = im.transpose(Image.FLIP_LEFT_RIGHT)
                if g & FV:
                    im = im.transpose(Image.FLIP_TOP_BOTTOM)
                if im.size != (w, h):
                    im = im.resize((w, h), Image.NEAREST)
                cache[key] = len(frames)
                frames.append(im)
            refs.append(cache[key])
        objs.append({"name": o.get("name", ""), "layer": lname, "x": o["x"], "y": o["y"], "w": w, "h": h, "z": z,
                     "mode": MODES.get(mode, 0), "opacity": round(lop * o.get("opacity", 1), 4),
                     "frames": refs, "ms": [ms for _, ms in seq] if anim else []})
    # pack: rows up to 1024 px wide
    pos, x, y, rowh, W = [], 0, 0, 0, 1024
    for im in frames:
        if x + im.width > W:
            x, y, rowh = 0, y + rowh, 0
        pos.append((x, y)); x += im.width; rowh = max(rowh, im.height)
    atlas = Image.new("RGBA", (W, y + rowh), (0, 0, 0, 0))
    for im, p in zip(frames, pos):
        atlas.paste(im, p)
    for o in objs:
        o["frames"] = [list(pos[i]) for i in o["frames"]]
    data = {"map": m, "width": doc["width"], "height": doc["height"], "atlas": f"{m}_objects.png", "objects": objs}
    if not dry:
        OUT.mkdir(parents=True, exist_ok=True)
        guard.need([OUT / f"{m}_objects.png", OUT / f"{m}.json"])
        guard.save_image(atlas, OUT / f"{m}_objects.png")
        guard.write_text(OUT / f"{m}.json", json.dumps(data, indent=1))
    return data


def main():
    a = sys.argv[1:]
    for f in [Path(x) for x in a if x.endswith(".tmj")]:
        d = export(f, "--dry" in a)
        if d is None:
            print(f"{f.stem}: no tile objects")
            continue
        for o in d["objects"]:
            print(f"{d['map']}: object '{o['name']}' on '{o['layer']}' at {o['x']},{o['y']} {o['w']}x{o['h']} z {o['z']}, "
                  f"{len(o['frames'])} frame(s)" + (f" {o['ms']} ms" if o["ms"] else ""))
        print(f"{d['map']}: {len(d['objects'])} object(s)" + (" (dry)" if "--dry" in a else f" -> {OUT}"))


if __name__ == "__main__":
    main()
