r"""What the tools last wrote into each Tiled atlas ("build (current)" and "frames (current)" tile sets), so a tile you
paint in an atlas is your edit: what you see in Tiled is what the game gets (user, 2026-11-24: "your tool is not allowed
to revert my changes. What I see in the editor must appear in the game").

Record: build\tool records\atlas\<atlas path>.png, the atlas as the tools last wrote it (tiled_export.py, tiled_frames.py,
tiled_refresh_atlas.py). A tile whose pixels differ from the record was painted by you:
  - tiled_import.py takes that art into the game for every cell showing the tile (tiled_import.py: painted tiles)
  - tiled_refresh_atlas.py never paints over it
An atlas with no record yet (exported before 2026-11-24) gets one equal to the atlas as it is: nothing counts as painted
until you paint after that.
"""
from pathlib import Path
from PIL import Image

import guard
import paths   # guard.py put the tools folder on the path

ATLAS_SETS = ("build (current)", "frames (current)")


def record_path(atlas):
    """build\\tool records\\atlas\\<atlas path below the project folder>: the tools' own memory, not beside your files"""
    p = Path(atlas).resolve()
    try:
        rel = p.relative_to(paths.ROOT)
    except ValueError:
        rel = Path(*[x.replace(":", "") for x in p.parts])
    return paths.TOOL_RECORDS / "atlas" / rel


def is_atlas_set(ts):
    return "image" in ts and ts.get("name", "").startswith(ATLAS_SETS)


def tile_prop(t):
    return next((p["value"] for p in t.get("properties", []) if p["name"] == "tile"), None)


def box(ts, k):
    T, cols = ts["tilewidth"], ts["columns"]
    return (k % cols * T, k // cols * T, k % cols * T + T, k // cols * T + T)


def load(atlas, keep=True):
    """the record of atlas; none yet: the atlas as it is, written at once unless keep=False (dry runs)"""
    rp = record_path(atlas)
    if rp.exists():
        return Image.open(rp).convert("RGBA")
    im = Image.open(atlas).convert("RGBA")
    if keep:
        save(atlas, im)
    return im


def save(atlas, im):
    rp = record_path(atlas)
    rp.parent.mkdir(parents=True, exist_ok=True)
    guard.save_image(im, rp)


def painted(atlas, ts, keep=True):
    """{tile id: painted 32 px art} of the tiles of ts (cells with a "tile" property) that differ from the record"""
    atlas = Path(atlas)
    if not atlas.exists():
        return {}
    a = Image.open(atlas).convert("RGBA")
    rec = load(atlas, keep)
    out = {}
    for t in ts.get("tiles", []):
        if not tile_prop(t):
            continue
        b = box(ts, t["id"])
        if rec.size != a.size or a.crop(b).tobytes() != rec.crop(b).tobytes():
            out[t["id"]] = a.crop(b)
    return out
