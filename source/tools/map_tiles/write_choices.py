r"""Tile art for a map project without mobile art (project.json without "mobile"): the project's choices.csv
(written by tiled_import.py) -> <project>\overrides\<sheet>\<tid>.png, which map_tiles_pack.py pastes over the game
sheet. Tiles are 16 x scale px (project.json "scale", default 2 = 32 px), stored upside down like the game's sheets.
Choices (one row per tile: sheet, tid, choice, note):
  file <name> [xf <HVD>]          your tile <added folder>\<name> (drawn upright; scaled to the tile size if needed),
                                  flipped/rotated as stamped in Tiled
  copy [<sheet>] <tid> [xf <HVD>] another tile as built (its override, else the game tile enlarged), flipped/rotated
  keep                            the game tile (no override)
Overrides no longer in choices.csv are removed (the folder is this script's output only). Prints a summary.
Usage: python write_choices.py [--project <folder>] [--dry]
"""
import csv, sys
from pathlib import Path
from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parent))
import guard   # your saved files are never changed without consent
from project import P
from layouts import EXPORT


def apply_xf(stored, code):
    """Tiled flip flags on a tile stored upside down: diagonal first, then horizontal, then vertical, as Tiled draws
    them; done on the upright image (same as write_mobile_replacements.apply_xf)"""
    vis = stored.transpose(Image.FLIP_TOP_BOTTOM)
    if "D" in code:
        vis = vis.transpose(Image.TRANSPOSE)
    if "H" in code:
        vis = vis.transpose(Image.FLIP_LEFT_RIGHT)
    if "V" in code:
        vis = vis.transpose(Image.FLIP_TOP_BOTTOM)
    return vis.transpose(Image.FLIP_TOP_BOTTOM)


def split_xf(choice):
    if " xf " in choice:
        a, code = choice.rsplit(" xf ", 1)
        return a, code.strip()
    return choice, ""


class Sheets:
    """the game's 16 px sheets by sheet key (catalogue sheets.csv: name and packages)"""
    def __init__(self):
        self.rows = {r["sheet"]: r for r in csv.DictReader(open(P.catalog / "sheets.csv", encoding="utf-8"))}
        self.ims = {}

    def tile(self, sheet, tid):
        """project tile id (rows from the top of the stored sheet) -> 16 px tile as stored"""
        if sheet not in self.ims:
            r = self.rows[sheet]
            png = next(Path(EXPORT).glob(f"{r['packages'].split()[0]}/Assets/GameAssets/Serial/Res/Map/*/{r['name']}.png"))
            self.ims[sheet] = Image.open(png).convert("RGBA")
        im = self.ims[sheet]
        r, c = tid // 32, tid % 32
        return im.crop((c * 16, r * 16, c * 16 + 16, r * 16 + 16))


def file_art(name, size):
    src = Image.open(P.added / name).convert("RGBA")
    if src.size != (size, size):
        nearest = src.width % size == 0 and src.height % size == 0 or size % src.width == 0
        src = src.resize((size, size), Image.NEAREST if nearest else Image.BOX)
    return src.transpose(Image.FLIP_TOP_BOTTOM)


def main():
    dry = "--dry" in sys.argv
    size = 16 * P.scale
    rows = list(csv.DictReader(open(P.choices, encoding="utf-8"))) if P.choices.exists() else []
    last = {}
    for r in rows:                                  # the last row for a tile wins
        last[(r["sheet"], int(r["tid"]))] = r["choice"].strip()
    sheets = Sheets()
    out = P.build / "overrides"
    made, art = {}, {}
    # plain art first, copies after (a copy reads the copied tile's override)
    for (sh, tid), c in sorted(last.items(), key=lambda kv: kv[1].startswith("copy")):
        base, code = split_xf(c)
        tok = base.split()
        if tok[0] == "keep":
            continue
        if tok[0] == "file":
            im = file_art(base[5:].strip(), size)
        elif tok[0] == "copy":
            sh2, t2 = (sh, int(tok[1])) if len(tok) == 2 else (tok[1], int(tok[2]))
            im = art.get((sh2, t2)) or sheets.tile(sh2, t2).resize((size, size), Image.NEAREST)
        else:
            sys.exit(f"{P.choices.name}: {sh}:{tid}: '{c}' needs the mobile pipeline (project.json \"mobile\")")
        if code:
            im = apply_xf(im, code)
        art[(sh, tid)] = im
        made[out / sh / f"{tid}.png"] = im
    changed = removed = 0
    todo = [f for f, im in made.items() if not (f.exists() and Image.open(f).convert("RGBA").tobytes() == im.tobytes())]
    stale = [f for f in out.glob("*/*.png") if f not in made] if out.exists() else []
    if not dry:
        guard.need(todo + stale)   # before anything is written or deleted
    for f in todo:
        changed += 1
        if not dry:
            f.parent.mkdir(parents=True, exist_ok=True)
            guard.save_image(made[f], f)
    for f in stale:
        removed += 1
        if not dry:
            guard.remove(f)
    print(f"{P.name}: {len(made)} tile(s) from {P.choices.name}, {changed} written, {removed} old override(s) removed"
          + (" (dry run)" if dry else "") + f" -> {out}")


if __name__ == "__main__":
    main()
