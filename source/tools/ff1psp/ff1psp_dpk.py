r"""Unpack FINAL FANTASY (Anniversary Edition, PSP, ULUS10251) data/ff1psp.dpk and convert GIM textures to PNG
(reference art, never for release).

Formats [read from a retail copy (ULUS10251), 2026-11-25; checked by eye on a few files, see the README the output names]:
  DPK / PCK (same layout): u32 count, u32 total size, 8 bytes 0; then <count> rows of 36 bytes:
    22-byte NUL-padded name, u16 ?, u32 offset (from the start of the archive), u32 stored size, u32 unpacked size.
    Stored size != unpacked size -> the entry is Wp16-compressed.
  Wp16: "Wp16", u32 unpacked size, then 32-bit flag words (LSB first) each covering 32 items:
    1 = copy one literal 16-bit word; 0 = u16 v: copy (v & 31) + 2 words from (v >> 5) words back
    (positions before the start read as 0).
  .PCK files are archives again; .NPK (sound) and other entries are written as stored.
  GIM ("MIG.00.1PSP"): blocks u16 id, u16 ?, u32 size, u32 next, u32 data offset; 2 root, 3 picture, 4 image,
    5 palette. Image/palette data: u16 header size, u16 ?, u16 format (0 5650, 1 5551, 2 4444, 3 8888, 4 idx4,
    5 idx8), u16 order (1 = PSP-swizzled 16-byte x 8-row blocks), u16 width, u16 height, u16 bpp align,
    u16 pitch align, ..., u32 at 0x1C = pixel start (from the data start). Only the first picture/frame is drawn.

Usage: python ff1psp_dpk.py <ff1psp.dpk> <out folder> [--only <name prefix>] [--force]
  writes <out>\files\<name> (unpacked bytes of every top-level entry),
         <out>\unpacked\<PCK name>\<entry> (nested PCKs as sub-folders) and <entry>.png beside every GIM.
Reads only; never runs anything from the game files.
"""
import struct
import sys
from collections import Counter
from pathlib import Path

from PIL import Image

stats = Counter()
problems = []


def wp16(b):
    if b[:4] != b"Wp16":
        raise ValueError("not Wp16")
    size = struct.unpack_from("<I", b, 4)[0]
    out = bytearray()
    i = 8
    while len(out) < size:
        flags = struct.unpack_from("<I", b, i)[0]
        i += 4
        for bit in range(32):
            if len(out) >= size:
                break
            if flags >> bit & 1:
                out += b[i:i + 2]
                i += 2
            else:
                v = struct.unpack_from("<H", b, i)[0]
                i += 2
                back, n = (v >> 5) * 2, ((v & 31) + 2) * 2
                for k in range(n):
                    p = len(out) - back
                    out.append(out[p] if p >= 0 else 0)
    return bytes(out[:size])


def table(b):
    """Rows of a DPK/PCK archive, or None if b is not one."""
    if len(b) < 16:
        return None
    n, total = struct.unpack_from("<II", b, 0)
    if n == 0 or n > 20000 or 16 + n * 36 > len(b) or b[8:16] != bytes(8):
        return None
    rows = []
    for k in range(n):
        o = 16 + k * 36
        raw = b[o:o + 22]
        name = raw.split(b"\0")[0]
        if not name or any(c < 32 or c > 126 for c in name):
            return None
        _h, off, stored, size = struct.unpack_from("<HIII", b, o + 22)
        if off + stored > len(b):
            return None
        rows.append((name.decode("ascii"), off, stored, size))
    return rows


def unswizzle(data, pitch, h):
    out = bytearray(pitch * h)
    bw = pitch // 16
    i = 0
    for by in range(0, h, 8):
        for bx in range(bw):
            for r in range(8):
                y = by + r
                if y < h:
                    d = y * pitch + bx * 16
                    out[d:d + 16] = data[i:i + 16]
                i += 16
    return bytes(out)


def colours(fmt, data, count):
    if fmt == 3:
        return [tuple(data[k * 4:k * 4 + 4]) for k in range(count)]
    res = []
    for k in range(count):
        v = struct.unpack_from("<H", data, k * 2)[0]
        if fmt == 0:
            res.append(((v & 31) * 255 // 31, (v >> 5 & 63) * 255 // 63, (v >> 11 & 31) * 255 // 31, 255))
        elif fmt == 1:
            res.append(((v & 31) * 255 // 31, (v >> 5 & 31) * 255 // 31, (v >> 10 & 31) * 255 // 31,
                        255 if v >> 15 else 0))
        else:
            res.append(((v & 15) * 17, (v >> 4 & 15) * 17, (v >> 8 & 15) * 17, (v >> 12 & 15) * 17))
    return res


def gim_blocks(b, at, end, found):
    while at + 16 <= end:
        bid, _u, size, nxt, dat = struct.unpack_from("<HHIII", b, at)
        if size < 16 or at + size > end:
            break
        if bid in (2, 3):
            gim_blocks(b, at + dat, at + size, found)
        elif bid in (4, 5):
            found.setdefault(bid, (at + dat, at + size))
        at += size


def gim_to_png(b, dst):
    found = {}
    gim_blocks(b, 16, len(b), found)
    if 4 not in found:
        raise ValueError("no image block")

    def head(start):
        fmt, order, w, h, bpp_al, pitch_al = struct.unpack_from("<HHHHHH", b, start + 4)
        pix = struct.unpack_from("<I", b, start + 0x1C)[0]
        return fmt, order, w, h, pitch_al, start + pix

    fmt, order, w, h, pitch_al, pix = head(found[4][0])
    bpp = {0: 16, 1: 16, 2: 16, 3: 32, 4: 4, 5: 8}.get(fmt)
    if bpp is None:
        raise ValueError(f"image format {fmt} not supported")
    pitch = (w * bpp // 8 + pitch_al - 1) // pitch_al * pitch_al if pitch_al else w * bpp // 8
    pitch = max(pitch, 16) if order == 1 else pitch
    hh = (h + 7) // 8 * 8 if order == 1 else h
    data = b[pix:pix + pitch * hh]
    if len(data) < pitch * hh:
        data = data + bytes(pitch * hh - len(data))
    if order == 1:
        data = unswizzle(data, pitch, hh)
    if fmt in (4, 5):
        if 5 not in found:
            raise ValueError("indexed image without palette")
        pf, _po, pw, ph, _pa, ppix = head(found[5][0])
        pal = colours(pf, b[ppix:], pw * ph)
        pal += [(0, 0, 0, 0)] * (256 - len(pal))
        px = bytearray()
        for y in range(h):
            row = data[y * pitch:y * pitch + pitch]
            if fmt == 5:
                idx = row[:w]
            else:
                idx = bytearray()
                for c in row[:(w + 1) // 2]:
                    idx += bytes((c & 15, c >> 4))
                idx = idx[:w]
            for i in idx:
                px += bytes(pal[i])
        stats[f"gim idx{bpp}"] += 1
    else:
        px = bytearray()
        bp = bpp // 8
        for y in range(h):
            row = data[y * pitch:y * pitch + w * bp]
            for c in (colours(fmt, row, w)):
                px += bytes(c)
        stats[f"gim fmt{fmt}"] += 1
    Image.frombytes("RGBA", (w, h), bytes(px)).save(dst, format="PNG")
    stats["png"] += 1


def write(p, data, force):
    if p.exists() and not force:
        raise SystemExit(f"refusing to overwrite {p} (use --force)")
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_bytes(data)
    stats["files"] += 1


def entry_bytes(b, off, stored, size, name):
    d = b[off:off + stored]
    if stored != size:
        d = wp16(d)
        stats["wp16"] += 1
        if len(d) != size:
            problems.append(f"{name}: unpacked {len(d)} != {size}")
    return d


def unpack(b, folder, force, label):
    rows = table(b)
    for name, off, stored, size in rows:
        try:
            d = entry_bytes(b, off, stored, size, f"{label}/{name}")
        except Exception as e:
            problems.append(f"{label}/{name}: {e}")
            continue
        p = folder / name
        if table(d) is not None and name.upper().endswith(".PCK"):
            unpack(d, folder / Path(name).stem, force, f"{label}/{name}")
            continue
        write(p, d, force)
        if d[:11] == b"MIG.00.1PSP":
            try:
                gim_to_png(d, p.with_name(p.name + ".png"))
            except Exception as e:
                problems.append(f"{label}/{name}: GIM {e}")


def main():
    a = sys.argv[1:]
    if len(a) < 2 or a[0] in ("-h", "--help"):
        raise SystemExit(__doc__)
    force = "--force" in a
    only = a[a.index("--only") + 1] if "--only" in a else ""
    src, out = Path(a[0]), Path(a[1])
    b = src.read_bytes()
    rows = table(b)
    print(f"{src.name}: {len(rows)} entries")
    for name, off, stored, size in rows:
        if not name.startswith(only):
            continue
        try:
            d = entry_bytes(b, off, stored, size, name)
        except Exception as e:
            problems.append(f"{name}: {e}")
            continue
        write(out / "files" / name, d, force)
        if name.upper().endswith(".PCK") and table(d) is not None:
            unpack(d, out / "unpacked" / Path(name).stem, force, name)
    print("counts:", dict(stats))
    print(f"problems: {len(problems)}")
    for p in problems[:60]:
        print("  ", p)


if __name__ == "__main__":
    main()
