r"""Render the effect / weapon / scene animations (`.ANA`) of FF1 Anniversary Edition (PSP) as frames (reference).

Format [decoded 2026-11-25 from a retail copy (ULUS10251); renders checked by eye]:
  `@ANA` header: u32 16, u32 animation data offset, u32 0x100, u32 n textures; at 0x20 n entries of
  u32 offset, u32 size, char[8] name: the embedded GIM textures (converted by tools\ff1psp_dpk.py).
  Animation data: one or more `@ANT` blocks (variants, e.g. colours of one spell). Block (offsets from its start):
    u32 1, u32 n parts, u32 n groups, u32 n tracks, u32 parts offset, u32 groups offset, u32 tracks offset.
    part (96 bytes) = one sprite in one state: u32 texture index, u32 0, u32 blend (2 = additive [inferred],
      1 = normal), u32 flag; float u0, v0, u1, v1 (pixels in the texture); float x, y, rotation (radians
      [inferred]), 0; float pivot x, pivot y, x, y (repeat); float scale x, scale y, 1, 1; float r, g, b, a.
    group = u32 offsets to lists of part numbers ending in -1: one keyframe state (part k of every state of a
      track is the same sprite).
    track (16 bytes) = one layer: u32 keys offset, u32 n keys, float length (frames), u32 0.
      key (16 bytes) = u32 type, float value, float start, float end:
        type 0: show group <value> from start to end (start = end: an instant state between tweens);
        type 1: blend from the previous state to the next one over <value> frames (start to end).
    All tracks of a block play at the same time; frame = 1 time unit [inferred: 1/60 s].
Rendering (reference approximation): parts drawn in order, anchor at the canvas centre, tint = multiply,
  blend 2 added onto the frame. Blocks with additive parts are drawn on black, the others on transparent.

Usage: python ff1psp_anim.py <unpacked folder> <out folder> [--force]
  writes <out>\<folder>\<file>[_v<k>]\NNN.png (frames), <out>\<folder>\<file>[_v<k>].png (contact strip, max 24
  frames), <out>\anim.csv and <out>\problems.txt.
"""
import csv
import io
import math
import shutil
import struct
import sys
from pathlib import Path

from PIL import Image, ImageChops

sys.path.insert(0, str(Path(__file__).resolve().parent))
import ff1psp_dpk  # noqa: E402

MAX_SIDE = 1024


def parse(b):
    anim = struct.unpack_from("<I", b, 8)[0]
    ntex = struct.unpack_from("<I", b, 16)[0]
    texs = []
    for k in range(ntex):
        o, size, name = struct.unpack_from("<II8s", b, 0x20 + 16 * k)
        texs.append((name.rstrip(b"\0").decode("ascii", "replace"), b[o:o + size]))
    blocks = []
    i = b.find(b"@ANT", anim)
    while i >= 0:
        A = i
        _v, np_, ng, nt, po, go, to = struct.unpack_from("<7I", b, A + 4)
        parts = [(struct.unpack_from("<4I", b, A + po + 0x60 * k), struct.unpack_from("<20f", b, A + po + 0x60 * k + 16))
                 for k in range(np_)]
        groups = []
        for k in range(ng):
            o = A + struct.unpack_from("<I", b, A + go + 4 * k)[0]
            g = []
            while (v := struct.unpack_from("<i", b, o)[0]) != -1:
                g.append(v)
                o += 4
            groups.append(g)
        tracks = []
        for k in range(nt):
            ko, nk, length, _z = struct.unpack_from("<IIfI", b, A + to + 16 * k)
            tracks.append((length, [struct.unpack_from("<Ifff", b, A + ko + 16 * j) for j in range(nk)]))
        blocks.append((parts, groups, tracks))
        i = b.find(b"@ANT", A + 4)
    return texs, blocks


def state_at(keys, t):
    """-> (group a, group b, mix) for time t; mix 0 = a only."""
    last = None
    for j, (typ, val, start, end) in enumerate(keys):
        if typ == 0:
            if start <= t < end or (start == end == t):
                return int(val), int(val), 0.0
            if end <= t:
                last = int(val)
        elif start <= t < end:
            nxt = next((int(k[1]) for k in keys[j + 1:] if k[0] == 0), last)
            prev = last if last is not None else nxt
            return prev, nxt, (t - start) / max(val, 1e-6)
    return (last, last, 0.0) if last is not None else (None, None, 0.0)


def lerp(p, q, m):
    if q is None or m <= 0:
        return p
    ints, fa = p
    fb = q[1]
    return ints, tuple(a + (b - a) * m for a, b in zip(fa, fb))


def sprite(tex, part):
    ints, f = part
    u0, v0, u1, v1 = f[0:4]
    if u1 <= u0 or v1 <= v0 or tex is None:
        return None
    img = tex.crop((int(u0), int(v0), int(u1), int(v1)))
    r, g, b_, a = (max(0.0, min(1.0, x)) for x in f[16:20])
    if a <= 0:
        return None
    sx, sy = f[12], f[13]
    w, h = max(1, round(img.width * abs(sx))), max(1, round(img.height * abs(sy)))
    if (w, h) != img.size:
        img = img.resize((w, h), Image.BILINEAR)
    if sx < 0:
        img = img.transpose(Image.FLIP_LEFT_RIGHT)
    if sy < 0:
        img = img.transpose(Image.FLIP_TOP_BOTTOM)
    if (r, g, b_, a) != (1, 1, 1, 1):
        rr, gg, bb, aa = img.split()
        img = Image.merge("RGBA", (rr.point(lambda x: x * r), gg.point(lambda x: x * g), bb.point(lambda x: x * b_),
                                   aa.point(lambda x: x * a)))
    px, py = f[8] * abs(sx), f[9] * abs(sy)  # pivot in scaled pixels
    rot = f[6]
    if abs(rot) > 1e-4:
        big = Image.new("RGBA", (w * 2 + 4, h * 2 + 4), (0, 0, 0, 0))
        cx, cy = big.width // 2, big.height // 2
        big.alpha_composite(img, (int(cx - px), int(cy - py)))
        img = big.rotate(-math.degrees(rot), resample=Image.BILINEAR, center=(cx, cy))
        px, py = cx, cy
    return img, f[4] - px, f[5] - py, ints[2] == 2


def frames_of(texs, block):
    parts, groups, tracks = block
    n = int(max((t[0] for t in tracks), default=0))
    out = []
    for t in range(max(n, 1)):
        draws = []
        for length, keys in tracks:
            a, b_, m = state_at(keys, t)
            if a is None or a >= len(groups):
                continue
            ga = groups[a]
            gb = groups[b_] if b_ is not None and b_ < len(groups) else ga
            for k, pi in enumerate(ga):
                if pi >= len(parts):
                    continue
                p = parts[pi]
                q = parts[gb[k]] if m > 0 and k < len(gb) and gb[k] < len(parts) else None
                pp = lerp(p, q, m)
                ti = pp[0][0]
                s = sprite(texs[ti] if ti < len(texs) else None, pp)
                if s:
                    draws.append(s)
        out.append(draws)
    return out


def render(frames, additive_bg):
    xs = [x for d in frames for im, x, y, _ in d for x in (x, x + im.width)]
    ys = [y for d in frames for im, x, y, _ in d for y in (y, y + im.height)]
    if not xs:
        return []
    half_w = min(MAX_SIDE // 2, int(max(abs(v) for v in xs)) + 2)
    half_h = min(MAX_SIDE // 2, int(max(abs(v) for v in ys)) + 2)
    res = []
    for d in frames:
        bg = (0, 0, 0, 255) if additive_bg else (0, 0, 0, 0)
        img = Image.new("RGBA", (half_w * 2, half_h * 2), bg)
        for im, x, y, add in d:
            pos = (int(round(x)) + half_w, int(round(y)) + half_h)
            if add:
                layer = Image.new("RGBA", img.size, (0, 0, 0, 0))
                layer.paste(im, pos)
                rgb = ImageChops.multiply(layer.convert("RGB"), layer.getchannel("A").convert("RGB"))
                img = Image.merge("RGBA", (*ImageChops.add(img.convert("RGB"), rgb).split(), img.getchannel("A")))
            else:
                tmp = Image.new("RGBA", img.size, (0, 0, 0, 0))
                tmp.paste(im, pos)
                img.alpha_composite(tmp)
        res.append(img)
    return res


def main():
    a = sys.argv[1:]
    if len(a) < 2 or a[0] in ("-h", "--help"):
        raise SystemExit(__doc__)
    unpacked, out = Path(a[0]), Path(a[1])
    if out.exists():
        if "--force" not in a:
            raise SystemExit(f"{out} exists; pass --force")
        if not (out / "anim.csv").exists():
            raise SystemExit(f"{out} has no anim.csv; not a folder this tool made, refusing")
        shutil.rmtree(out)
    rows, problems = render_all(unpacked, out)
    print(f"animations: {len(rows)}; frames: {sum(r[4] for r in rows)}; problems: {len(problems)}")
    for p in problems[:20]:
        print("  ", p)


def render_all(unpacked, out, only=None):
    unpacked, out = Path(unpacked), Path(out)
    rows, problems = [], []
    files = sorted(unpacked.rglob("*.ANA"))
    if only:
        files = [f for f in files if any(o in f.as_posix() for o in only)]
    for f in files:
        rel = f.relative_to(unpacked)
        try:
            texnames, blocks = parse(f.read_bytes())
            texs = []
            for name, g in texnames:
                buf = io.BytesIO()
                ff1psp_dpk.gim_to_png(g, buf)
                buf.seek(0)
                texs.append(Image.open(buf).convert("RGBA"))
        except Exception as e:
            problems.append(f"{rel}: {e}")
            continue
        for k, block in enumerate(blocks):
            try:
                fr = frames_of(texs, block)
                additive = any(p[0][2] == 2 for p in block[0])
                imgs = render(fr, additive)
            except Exception as e:
                problems.append(f"{rel} block {k}: {e}")
                continue
            if not imgs:
                problems.append(f"{rel} block {k}: nothing drawn")
                continue
            name = f.stem + (f"_v{k}" if len(blocks) > 1 else "")
            d = out / rel.parent / name
            d.mkdir(parents=True, exist_ok=True)
            for j, im in enumerate(imgs):
                im.save(d / f"{j:03d}.png")
            step = max(1, math.ceil(len(imgs) / 24))
            pick = imgs[::step]
            w, h = imgs[0].size
            strip = Image.new("RGBA", (w * len(pick), h), (0, 0, 0, 0))
            for j, im in enumerate(pick):
                strip.alpha_composite(im, (j * w, 0))
            strip.save(out / rel.parent / f"{name}.png")
            rows.append([rel.parent.as_posix(), name, len(block[2]), "black" if additive else "transparent",
                         len(imgs), w, h])
    out.mkdir(parents=True, exist_ok=True)
    with (out / "anim.csv").open("w", newline="", encoding="utf-8") as fh:
        wr = csv.writer(fh)
        wr.writerow(["folder", "animation", "tracks", "background", "frames", "width", "height"])
        wr.writerows(rows)
    (out / "problems.txt").write_text("\n".join(problems) + "\n", encoding="utf-8")
    return rows, problems


if __name__ == "__main__":
    main()
