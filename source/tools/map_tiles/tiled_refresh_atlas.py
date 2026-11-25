r"""Refresh the Tiled atlases ("build (current)" and "frames (current)" tile sets) from the installed HD sheets, so
Tiled shows what the game shows after a build. Only the atlas images (<project>\tiled\atlas\*.png) and their records
(atlas\.tools\, atlas_record.py) are written; the .tmj files are not touched. Tile order and size come from each tile
set (tile property "tile" = sheet:tid). The HD sheets store tiles upside down; the atlases hold them upright.
Never reverts your edits: a tile you painted in an atlas (it differs from what the tools last wrote there) is kept and
listed; tiled_import.py takes it into the game, and once the game shows it the record follows. Cells without a "tile"
property (empty cells you painted) are never touched.
When Claude Code runs it, an atlas you saved stops it before anything is written (guard.py).
Usage: python tiled_refresh_atlas.py [Map_... ...] [--dry]   (default: every .tmj in the tiled folder)
"""
import json, sys
from pathlib import Path
from PIL import Image

PROJECT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(Path(__file__).resolve().parent))
import guard   # first: takes --overwrite-saved off the command line
import atlas_record as ar
from project import P
TD = P.tiled
SHEETS = P.installed


def plan(tmjs, dry=False):
    """atlas path -> (new atlas, new record, [refreshed tiles], [kept painted tiles]); each atlas once"""
    out, sheets = {}, {}
    for tmj in tmjs:
        doc = json.loads(tmj.read_text(encoding="utf-8"))
        for ts in doc["tilesets"]:
            if not ar.is_atlas_set(ts):
                continue
            path = (tmj.parent / ts["image"]).resolve()
            if path in out or not path.exists():
                continue
            atlas = Image.open(path).convert("RGBA")
            rec = ar.load(path, not dry)
            new, new_rec, done, kept, T = atlas.copy(), rec.copy(), [], [], ts["tilewidth"]
            for t in ts.get("tiles", []):
                v = ar.tile_prop(t)
                if not v:
                    continue
                s, tid = v.split(":")
                tid = int(tid)
                if s not in sheets:
                    f = SHEETS / f"{'map_chip_' + s if s.isdigit() else s}.hd.png"
                    sheets[s] = Image.open(f).convert("RGBA") if f.exists() else None   # not installed: left as is
                sh = sheets[s]
                if sh is None:
                    continue
                z = sh.width // 32
                tile = sh.crop((tid % 32 * z, tid // 32 * z, tid % 32 * z + z, tid // 32 * z + z)).transpose(Image.FLIP_TOP_BOTTOM)
                if T != z:
                    tile = tile.resize((T, T), Image.NEAREST)
                b = ar.box(ts, t["id"])
                cur = atlas.crop(b).tobytes()
                if cur == tile.tobytes():
                    new_rec.paste(tile, b[:2])                 # the game shows what Tiled shows
                elif rec.size != atlas.size or cur != rec.crop(b).tobytes():
                    kept.append(f"{t['id']} ({v})")            # painted by you: never painted over
                else:
                    new.paste(tile, b[:2]); new_rec.paste(tile, b[:2])
                    done.append(f"{t['id']} ({v})")
            out[path] = (new, new_rec, done, kept, rec)
    return out


def main():
    a = sys.argv[1:]
    maps = [m for m in a if not m.startswith("--")]
    tmjs = [TD / f"{m}.tmj" for m in maps] if maps else sorted(TD.glob("Map_*.tmj"))
    todo = plan(tmjs, "--dry" in a)
    for p, (_, _, done, kept, _) in todo.items():
        if done:
            print(f"{p.name}: {len(done)} tile(s) refreshed from the installed art: {', '.join(done)}")
        if kept:
            print(f"{p.name}: {len(kept)} tile(s) you painted kept (import the map to put them in the game): {', '.join(kept)}")
    if not any(v[2] for v in todo.values()):
        print("Tiled atlases: nothing to refresh")
    if "--dry" in a:
        print("(dry run, nothing written)")
        return
    guard.need([p for p, v in todo.items() if v[2]], "replace build tiles in")   # runs from Claude Code: stops on your saved atlases
    for p, (new, new_rec, done, _, rec) in todo.items():
        if done:
            guard.save_image(new, p)
        if new_rec.tobytes() != rec.tobytes():
            ar.save(p, new_rec)


if __name__ == "__main__":
    main()
