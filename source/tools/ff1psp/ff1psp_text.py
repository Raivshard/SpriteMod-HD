r"""FF1 Anniversary Edition (PSP) game text to readable text (reference only).

Format [read from a retail copy (ULUS10251), 2026-11-25; English names checked against the known FF1 monster list]:
  .FIF (font info, one per container): u16 glyph height?, u16 glyph count, then a u16 table indexed by character
    code (code 0 at byte 4): glyph number or 0xFFFF. Several codes can point at one glyph; the lowest code wins.
  .MSG: u32 0, "TEXT", u8 ?, u8 string count, u16 0, u32 size, then <count> u32 offsets; each string = one byte
    per glyph number of the container's own font (the .GIM beside the .FIF). Unmapped bytes are written <xx>.
  Japanese containers use codes above 0x7F (not decoded to Japanese here; written <xx>).

Usage: python ff1psp_text.py <unpacked folder> <out folder> [--lang US] [--force]
  writes <out>\<container>\<msg name>.txt, one numbered string per line, for every container whose
  name contains <lang> (default US) and holds a .FIF.
Import: strings(msg_path, fif_path) -> list of str.
"""
import struct
import sys
from pathlib import Path


def glyph_map(fif):
    b = Path(fif).read_bytes()
    _h, n = struct.unpack_from("<HH", b, 0)
    inv = {}
    for code in range(0, 128):
        o = 4 + code * 2
        if o + 2 > len(b):
            break
        g = struct.unpack_from("<H", b, o)[0]
        if g != 0xFFFF and g < n:
            inv.setdefault(g, chr(code) if code >= 32 else "")
    return inv


def strings(msg, fif):
    inv = glyph_map(fif)
    m = Path(msg).read_bytes()
    if m[4:8] != b"TEXT":
        raise ValueError("not a TEXT file")
    cnt = m[9]
    offs = [struct.unpack_from("<I", m, 16 + 4 * k)[0] for k in range(cnt)]
    out = []
    for k, o in enumerate(offs):
        e = offs[k + 1] if k + 1 < cnt else len(m)
        out.append("".join(inv.get(c, f"<{c:02x}>") for c in m[o:e]).rstrip("\0"))
    return out


def main():
    a = sys.argv[1:]
    if len(a) < 2 or a[0] in ("-h", "--help"):
        raise SystemExit(__doc__)
    force = "--force" in a
    lang = a[a.index("--lang") + 1] if "--lang" in a else "US"
    src, out = Path(a[0]), Path(a[1])
    n = 0
    for d in sorted(p for p in src.iterdir() if p.is_dir() and lang in p.name):
        fifs = list(d.glob("*.FIF"))
        if not fifs:
            continue
        for msg in sorted(d.glob("*.MSG")):
            dst = out / d.name / (msg.stem + ".txt")
            if dst.exists() and not force:
                raise SystemExit(f"refusing to overwrite {dst} (use --force)")
            dst.parent.mkdir(parents=True, exist_ok=True)
            try:
                lines = strings(msg, fifs[0])
            except ValueError as e:
                print(f"skipped {msg}: {e}")
                continue
            dst.write_text("\n".join(f"{k}\t{s}" for k, s in enumerate(lines)) + "\n", encoding="utf-8")
            n += 1
    print(f"text files written: {n}")


if __name__ == "__main__":
    main()
