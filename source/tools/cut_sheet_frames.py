"""Cut sprite sheets (PNG with transparency) into single frames. Pixels and palette are copied unchanged.

Layout rule (from the old FF5 PC sheets, 2026-11-14; a fixed 56 px grid failed: one pose crosses it by 1 px):
  - Recursive cut: split the sheet at empty rows into bands, each band at empty columns, each piece at empty
    rows again, and so on until a piece splits neither way: that piece is one pose. Gaps up to ROW_GAP/COL_GAP
    px do not split; a run thinner than TINY px (a sparkle dot, a sliver) joins its nearest pose.
  - Each frame is cut halfway through the empty gaps around its pose, at most MARGIN px beyond it, so frames of
    one row share the same top and bottom: feet stay lined up along a row.
  - Sheets of 64x64 or smaller are one frame: copied whole.
Checks printed: every opaque pixel lands in exactly one frame; poses smaller than TINY px or bigger than
BIG px (a fragment, or two poses merged) are listed for a look.

Usage: python cut_sheet_frames.py <png folder> <out folder> [--map map.json] [--dry-run] [--force]
  Output: <out>/<folder>/<sheet>_NN.png, NN in reading order (rows top to bottom, left to right).
  <folder> = the map's entry for the sheet's path below <png folder> without ".png" ({"fldchr15_0": "Knight/Bartz",
  "NPC/fldchr49_1": ...}), else <sub folder>/<sheet>.
  Refuses to overwrite unless --force.
"""
import json
import sys
from pathlib import Path

from PIL import Image

ROW_GAP = 2   # px; empty rows this thin do not separate poses (rows of poses are 4 px apart on some NPC sheets)
COL_GAP = 3   # px; sparkles sit 2 px beside a head; poses are 4 px or more apart (the drake sheet)
TINY = 10     # px; a run thinner than this (a dot, a sliver) joins its nearest pose if within STRAY px
STRAY = 12    # px
BIG = 90      # px; poses bigger than this are listed for a look (two poses merged?)
MARGIN = 8    # px; most empty space kept beyond a pose (halfway to the next pose if that is closer)


def runs(flags):
    out, start = [], None
    for i, f in enumerate(list(flags) + [False]):
        if f and start is None:
            start = i
        elif not f and start is not None:
            out.append((start, i - 1))
            start = None
    return out


def merge(rs, gap):
    """Join runs closer than gap px; then join a run thinner than TINY to its nearest neighbour within STRAY px."""
    out = []
    for r in rs:
        if out and r[0] - out[-1][1] - 1 <= gap:
            out[-1] = (out[-1][0], r[1])
        else:
            out.append(r)
    i = 0
    while len(out) > 1 and i < len(out):
        r = out[i]
        if r[1] - r[0] + 1 < TINY:
            gl = r[0] - out[i - 1][1] - 1 if i > 0 else 10**9
            gr = out[i + 1][0] - r[1] - 1 if i + 1 < len(out) else 10**9
            if min(gl, gr) <= STRAY:
                j = i - 1 if gl <= gr else i + 1
                lo, hi = min(i, j), max(i, j)
                out[lo:hi + 1] = [(out[lo][0], out[hi][1])]
                i = 0
                continue
        i += 1
    return out


def cuts(rs, lo, hi):
    """[(start, end)] per run: halfway through each gap, but at most MARGIN px of empty space beyond the pose
    (end-of-row and last-row poses would otherwise reach the far sheet edge)."""
    mids = [lo] + [(rs[i][1] + rs[i + 1][0] + 1) // 2 for i in range(len(rs) - 1)] + [hi]
    return [(max(mids[i], r0 - MARGIN), min(mids[i + 1], r1 + 1 + MARGIN)) for i, (r0, r1) in enumerate(rs)]


def split(a, box, rows_first=True, tried_other=False):
    """Recursive cut: split the box at empty rows, else at empty columns, until neither splits.
    Returns [(crop box, pose size)]."""
    x0, y0, x1, y1 = box
    if rows_first:
        rs = merge(runs(any(a[x, y] for x in range(x0, x1)) for y in range(y0, y1)), ROW_GAP)
        rs = [(r0 + y0, r1 + y0) for r0, r1 in rs]
    else:
        rs = merge(runs(any(a[x, y] for y in range(y0, y1)) for x in range(x0, x1)), COL_GAP)
        rs = [(r0 + x0, r1 + x0) for r0, r1 in rs]
    if not rs:
        return []
    if len(rs) == 1:
        (e0, e1), = cuts(rs, y0, y1) if rows_first else cuts(rs, x0, x1)
        box = (x0, e0, x1, e1) if rows_first else (e0, y0, e1, y1)  # trim empty space to MARGIN
        if tried_other:  # neither axis splits: this is one pose
            x0, y0, x1, y1 = box
            cy = runs(any(a[x, y] for x in range(x0, x1)) for y in range(y0, y1))
            cx = runs(any(a[x, y] for y in range(y0, y1)) for x in range(x0, x1))
            return [(box, (cx[-1][1] - cx[0][0] + 1, cy[-1][1] - cy[0][0] + 1))]
        return split(a, box, not rows_first, True)
    out = []
    for e0, e1 in (cuts(rs, y0, y1) if rows_first else cuts(rs, x0, x1)):
        sub = (x0, e0, x1, e1) if rows_first else (e0, y0, e1, y1)
        out += split(a, sub, not rows_first, False)
    return out


def layout(a, w, h):
    """[(name suffix, crop box, pose size)]"""
    if w <= 64 and h <= 64:
        return [("", (0, 0, w, h), None)]
    return [(f"_{i:02}", box, size) for i, (box, size) in enumerate(split(a, (0, 0, w, h)))]


def main(argv):
    mapping = {}
    if "--map" in argv:
        i = argv.index("--map")
        mapping = json.loads(Path(argv[i + 1]).read_text(encoding="utf-8"))
        argv = argv[:i] + argv[i + 2:]
    args = [x for x in argv if not x.startswith("--")]
    dry, force = "--dry-run" in argv, "--force" in argv
    if len(args) != 2:
        print(__doc__)
        return 2
    src, out = Path(args[0]), Path(args[1])
    sheets = sorted(src.rglob("*.png"))
    total = written = existed = 0
    fails, odd = [], []
    for p in sheets:
        im = Image.open(p)
        w, h = im.size
        a = im.convert("RGBA").getchannel("A").load()
        frames = layout(a, w, h)
        opaque = sum(1 for y in range(h) for x in range(w) if a[x, y])
        covered = sum(1 for _, (x0, y0, x1, y1), _s in frames for y in range(y0, y1) for x in range(x0, x1) if a[x, y])
        if opaque != covered:
            fails.append(f"{p.relative_to(src)}: {opaque - covered} opaque pixels not in exactly one frame")
        for suf, box, size in frames:
            if size and (min(size) < TINY or max(size) > BIG):
                odd.append(f"{p.relative_to(src).with_suffix('')}{suf}: pose {size[0]}x{size[1]}")
        total += len(frames)
        if dry:
            continue
        key = p.relative_to(src).with_suffix("").as_posix()  # "fldchr15_0", "NPC/fldchr73_0"
        sub = out / mapping[key] if key in mapping else out / p.relative_to(src).parent / p.stem
        sub.mkdir(parents=True, exist_ok=True)
        for suf, box, _ in frames:
            dst = sub / f"{p.stem}{suf}.png"
            if dst.exists() and not force:
                existed += 1
                continue
            im.crop(box).save(dst)  # crop keeps the sheet's mode and palette
            written += 1
    print(f"source: {src}\noutput: {out}{'  (dry run, nothing written)' if dry else ''}")
    print(f"sheets: {len(sheets)}; frames: {total}; written: {written}; already there: {existed}")
    print(f"pixel check: {'all opaque pixels in exactly one frame' if not fails else f'{len(fails)} sheet(s) FAILED'}")
    for f in fails:
        print("  FAIL", f)
    print(f"poses to look at (smaller than {TINY} px or bigger than {BIG} px): {len(odd)}")
    for o in odd:
        print("  ", o)
    return 1 if fails else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
