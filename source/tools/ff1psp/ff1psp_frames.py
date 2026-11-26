r"""Assemble the sprite frames of FF1 Anniversary Edition (PSP) from their piece lists (reference only).

Format [decoded 2026-11-25 from a retail copy (ULUS10251); frames checked by eye]:
  `<name>.OTI` = frame list for the texture(s) `<name>*.GIM` in the same folder:
    u32 n, n u32 offsets; each frame: s16 x, s16 y, u16 u, u16 v, u16 width, u16 height (bounds), u16 count,
    then count pieces of s16 x, s16 y, u16 u, u16 v, u16 w, u16 h, u16 f (14 bytes each).
    A piece is drawn at (x, y) relative to the sprite's anchor (0, 0 = feet centre for characters).
  Two variants:
    sheet coordinates (field NPCs, objects, vehicles, party map sprites): the piece is the area (u, v, w, h) of
      the texture; f = flags [inferred: 0xC on the lower half of 32x32 characters = drawn behind forest / grass].
    cell index (party battle sprites `JOB.OTI`): u = 16 * f, v = 0; the texture is a stream of 16x16 cells
      (row by row) and the piece takes its cells from f on, row by row inside the piece. The OTI holds 4 shapes;
      the sheet holds 11 frames: frames 0-9 use shape 0 (48x48, 9 cells) at cell 9 * k, frame 10 (KO) uses shape
      0 or, when the sheet has cells past 98 (only White Wizard `JOB0A`), the wide shape 3 (64x48, 12 cells).
      `JOBnn_S` = the shadow, same layout. Poses: see BATTLE_POSES.
  `<name>_AOB.BIN` / `JOB.AOB` = one entry per OTI frame [inferred: anchor points, e.g. hand / effect positions];
    not needed to draw the frames, not decoded.

Usage: python ff1psp_frames.py <unpacked folder> <out folder> [--force]
  writes <out>\<texture folder>\<texture>\NNN.png (one file per frame, anchor at the centre of a fixed canvas),
  <out>\<texture folder>\<texture>.png (all frames in a row) and <out>\frames.csv. Battle frames are named
  NN_<pose> and their folders <JOBnn>_<class>. Identical texture + frame lists are written once.
"""
import csv
import hashlib
import shutil
import struct
import sys
from pathlib import Path

from PIL import Image

BATTLE_CLASSES = ["Warrior", "Thief", "Monk", "Red_Mage", "White_Mage", "Black_Mage",
                  "Knight", "Ninja", "Master", "Red_Wizard", "White_Wizard", "Black_Wizard"]
BATTLE_POSES = ["stand", "ready", "walk1", "walk2", "attack", "chant1", "chant2", "arm_up", "kneel", "hit", "ko"]


def parse_oti(b):
    n = struct.unpack_from("<I", b, 0)[0]
    offs = struct.unpack_from(f"<{n}I", b, 4)
    frames = []
    for o in offs:
        hdr = struct.unpack_from("<hhHHHHH", b, o)
        frames.append((hdr, [struct.unpack_from("<hhHHHHH", b, o + 14 + 14 * j) for j in range(hdr[6])]))
    return frames


def is_cell_style(frames):
    pcs = [p for _, ps in frames for p in ps]
    return (all(p[2] == 16 * p[6] and p[3] == 0 and p[4] % 16 == 0 and p[5] % 16 == 0 for p in pcs)
            and any(p[6] for p in pcs))


def cell(tex, i):
    cols = tex.width // 16
    x, y = (i % cols) * 16, (i // cols) * 16
    return tex.crop((x, y, x + 16, y + 16))


def draw(tex, pieces, cell_base=None):
    """-> (image, left, top): pieces composed; (left, top) = position of the image's corner relative to the anchor."""
    x0 = min(p[0] for p in pieces); y0 = min(p[1] for p in pieces)
    x1 = max(p[0] + p[4] for p in pieces); y1 = max(p[1] + p[5] for p in pieces)
    img = Image.new("RGBA", (x1 - x0, y1 - y0), (0, 0, 0, 0))
    for x, y, u, v, w, h, f in pieces:
        if cell_base is None:
            img.alpha_composite(tex.crop((u, v, u + w, v + h)), (x - x0, y - y0))
        else:
            cw = w // 16
            for j in range(cw * (h // 16)):
                img.alpha_composite(cell(tex, cell_base + f + j), (x - x0 + (j % cw) * 16, y - y0 + (j // cw) * 16))
    return img, x0, y0


def used_cells(tex):
    cols = tex.width // 16
    return [i for i in range(cols * (tex.height // 16)) if cell(tex, i).getchannel("A").getextrema()[1] > 0]


def battle_frames(tex, shapes):
    last = max(used_cells(tex) or [0])
    out = []
    for k in range(11):
        shape = 3 if k == 10 and last > 98 else 0
        out.append(draw(tex, shapes[shape][1], cell_base=9 * k))
    return out


def canvas(items):
    """Place frames on one canvas size with the anchor at the same point; -> list of images."""
    left = min(x0 for _, x0, _ in items); top = min(y0 for _, _, y0 in items)
    right = max(x0 + im.width for im, x0, _ in items); bottom = max(y0 + im.height for im, _, y0 in items)
    res = []
    for im, x0, y0 in items:
        c = Image.new("RGBA", (right - left, bottom - top), (0, 0, 0, 0))
        c.alpha_composite(im, (x0 - left, y0 - top))
        res.append(c)
    return res, (-left, -top)


def main():
    a = sys.argv[1:]
    if len(a) < 2 or a[0] in ("-h", "--help"):
        raise SystemExit(__doc__)
    unpacked, out = Path(a[0]), Path(a[1])
    if out.exists():
        if "--force" not in a:
            raise SystemExit(f"{out} exists; pass --force")
        if not (out / "frames.csv").exists():
            raise SystemExit(f"{out} has no frames.csv; not a folder this tool made, refusing")
        shutil.rmtree(out)
    rows, problems = render_all(unpacked, out)
    print(f"sprites: {len(rows)}; frames: {sum(r[4] for r in rows)}; problems: {len(problems)}")
    for p in problems[:20]:
        print("  ", p)


def battle_party(unpacked, out, shadows=False):
    """Party battle sprites of all classes together: <out>\\<Class>\\NN_<pose>.png and all_classes.png (one row
    per class, one column per pose). Bodies only by default (the game and the plugin draw battle shadows);
    shadows=True merges the PSP shadow under each body. -> number of classes written."""
    d = Path(unpacked) / "JOB_ALL"
    shapes = parse_oti((d / "JOB.OTI").read_bytes())
    rows = []
    for nn, cls in enumerate(BATTLE_CLASSES):
        body_p, shadow_p = d / f"JOB{nn:02X}.GIM.png", d / f"JOB{nn:02X}_S.GIM.png"
        if not body_p.exists():
            continue
        body = battle_frames(Image.open(body_p).convert("RGBA"), shapes)
        shadow = battle_frames(Image.open(shadow_p).convert("RGBA"), shapes) if shadows and shadow_p.exists() else []
        items = []
        for k, (im, x0, y0) in enumerate(body):
            if k < len(shadow):
                s, sx, sy = shadow[k]
                l, t = min(x0, sx), min(y0, sy)
                c = Image.new("RGBA", (max(x0 + im.width, sx + s.width) - l, max(y0 + im.height, sy + s.height) - t))
                c.alpha_composite(s, (sx - l, sy - t))
                c.alpha_composite(im, (x0 - l, y0 - t))
                items.append((c, l, t))
            else:
                items.append((im, x0, y0))
        rows.append((cls, items))
    every = [it for _, items in rows for it in items]
    if not every:
        return 0
    imgs_all, _ = canvas(every)
    w, h = imgs_all[0].size
    sheet = Image.new("RGBA", (w * len(BATTLE_POSES), h * len(rows)), (0, 0, 0, 0))
    k = 0
    for r, (cls, items) in enumerate(rows):
        dd = Path(out) / f"JOB{r:02X}_{cls}"
        dd.mkdir(parents=True, exist_ok=True)
        for j in range(len(items)):
            imgs_all[k].save(dd / f"{j:02d}_{BATTLE_POSES[j]}.png")
            sheet.alpha_composite(imgs_all[k], (j * w, r * h))
            k += 1
    sheet.save(Path(out) / "all_classes.png")
    return len(rows)


def render_all(unpacked, out):
    """Write every sprite's frames into <out>; -> (rows, problems). Import: used by ff1psp_order.py."""
    unpacked, out = Path(unpacked), Path(out)
    rows, problems, seen = [], [], set()
    if (unpacked / "JOB_ALL" / "JOB.OTI").exists():
        battle_party(unpacked, out / "battle_party")
    for oti in sorted(unpacked.rglob("*.OTI")):
        try:
            frames = parse_oti(oti.read_bytes())
        except Exception as e:
            problems.append(f"{oti.relative_to(unpacked)}: {e}")
            continue
        cands = sorted(oti.parent.glob(f"{oti.stem}*.GIM.png"))
        texs = ([p for p in cands if p.name[:-8] == oti.stem]  # same name, else <name>_xx / JOBnn
                or [p for p in cands if p.name[len(oti.stem)] in "_0123456789ABCDEF"])
        if not texs:
            problems.append(f"{oti.relative_to(unpacked)}: no texture")
            continue
        cellstyle = is_cell_style(frames)
        for tp in texs:
            key = hashlib.md5(oti.read_bytes() + tp.read_bytes()).hexdigest()
            if key in seen:
                continue
            seen.add(key)
            tex = Image.open(tp).convert("RGBA")
            stem = tp.name[:-8]
            names = None
            if cellstyle and oti.stem == "JOB":
                items = battle_frames(tex, frames)
                nn = int(stem[3:5], 16)
                stem = f"{stem}_{BATTLE_CLASSES[nn]}" if nn < len(BATTLE_CLASSES) else stem
                names = [f"{k:02d}_{p}" for k, p in enumerate(BATTLE_POSES)]
            elif cellstyle:
                items = [draw(tex, ps, cell_base=0) for _, ps in frames]
            else:
                bad = [p for _, ps in frames for p in ps if p[2] + p[4] > tex.width or p[3] + p[5] > tex.height]
                if bad:
                    problems.append(f"{tp.relative_to(unpacked)}: {len(bad)} pieces outside the texture")
                items = [draw(tex, ps) if ps else (Image.new("RGBA", (1, 1)), 0, 0) for _, ps in frames]
            imgs, anchor = canvas(items)
            rel = tp.parent.relative_to(unpacked)
            d = out / rel / stem
            d.mkdir(parents=True, exist_ok=True)
            for k, im in enumerate(imgs):
                im.save(d / f"{names[k] if names else f'{k:03d}'}.png")
            w, h = imgs[0].size
            strip = Image.new("RGBA", (w * len(imgs), h), (0, 0, 0, 0))
            for k, im in enumerate(imgs):
                strip.alpha_composite(im, (k * w, 0))
            strip.save(out / rel / f"{stem}.png")
            rows.append([rel.as_posix(), stem, oti.name, "cell index" if cellstyle else "sheet coordinates",
                         len(imgs), w, h, anchor[0], anchor[1]])
    out.mkdir(parents=True, exist_ok=True)
    with (out / "frames.csv").open("w", newline="", encoding="utf-8") as fh:
        wr = csv.writer(fh)
        wr.writerow(["folder", "sprite", "frame list", "style", "frames", "frame width", "frame height",
                     "anchor x", "anchor y"])
        wr.writerows(rows)
    return rows, problems


if __name__ == "__main__":
    main()
