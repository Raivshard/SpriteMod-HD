"""
Makes a renamed copy of a TrueType font: every name the font is looked up by is replaced, so the copy is its own
font family (Unity's font list files faces under the typographic family and keeps only Regular/Bold/Italic, so a
Light weight is only reachable as a family of its own; see docs/MenuLayout_README.md "Font faces").

    python rename_font.py <in.ttf> <out.ttf> --family "SpriteMod Sans Light" --style Italic [--force]

Name IDs written (Windows, English): 1 family, 2 style (Regular/Italic/Bold/Bold Italic), 3 unique ID, 4 full name,
6 PostScript name. Removed: 16/17 (typographic family/style), 21/22 (WWS), 25 (variations prefix): with them Unity
would file the copy under the old family again. Copyright (0), licence (13/14) and the rest are kept, with a note in
10 (description) that this is a renamed copy. Table checksums and head.checkSumAdjustment are recomputed.

Fonts under the SIL Open Font License with a Reserved Font Name must not keep that name in a modified copy.
"""
import argparse
import pathlib
import re
import struct
import sys

DROP = {1, 2, 3, 4, 6, 16, 17, 21, 22, 25}


def checksum(data):
    data = data + b"\0" * (-len(data) % 4)
    return sum(struct.unpack(f">{len(data) // 4}I", data)) & 0xFFFFFFFF


def read_tables(d):
    ver, n = struct.unpack(">IH", d[:6])
    tables = []
    for i in range(n):
        tag, cs, off, ln = struct.unpack(">4sIII", d[12 + 16 * i:28 + 16 * i])
        tables.append([tag, d[off:off + ln]])
    return ver, d[4:12], tables


def read_names(name):
    fmt, cnt, so = struct.unpack(">HHH", name[:6])
    recs = []
    for i in range(cnt):
        pid, eid, lid, nid, ln, off = struct.unpack(">HHHHHH", name[6 + 12 * i:18 + 12 * i])
        recs.append((pid, eid, lid, nid, name[so + off:so + off + ln]))
    return recs


def build_names(recs):
    recs = sorted(recs, key=lambda r: r[:4])
    head = struct.pack(">HHH", 0, len(recs), 6 + 12 * len(recs))
    body, store, offs = b"", b"", {}
    for pid, eid, lid, nid, s in recs:
        if s not in offs:
            offs[s] = len(store)
            store += s
        body += struct.pack(">HHHHHH", pid, eid, lid, nid, len(s), offs[s])
    return head + body + store


def text(recs, nid):
    for pid, eid, lid, n, s in recs:
        if pid == 3 and lid == 0x409 and n == nid:
            return s.decode("utf-16-be")
    return ""


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("src")
    ap.add_argument("out")
    ap.add_argument("--family", required=True)
    ap.add_argument("--style", required=True, choices=["Regular", "Italic", "Bold", "Bold Italic"])
    ap.add_argument("--force", action="store_true")
    a = ap.parse_args()
    out = pathlib.Path(a.out)
    if out.exists() and not a.force:
        sys.exit(f"{out} exists; use --force")
    d = pathlib.Path(a.src).read_bytes()
    ver, hdr_rest, tables = read_tables(d)
    if ver not in (0x00010000, 0x74727565):
        sys.exit("not a TrueType (glyf) font")
    ti = {t[0]: i for i, t in enumerate(tables)}
    old = read_names(tables[ti[b"name"]][1])
    old_family = text(old, 16) or text(old, 1)
    ps_fam = re.sub(r"[^A-Za-z0-9]", "", a.family)
    ps_style = a.style.replace(" ", "")
    full = a.family if a.style == "Regular" else f"{a.family} {a.style}"
    version = text(old, 5).replace("Version ", "") or "1.0"
    new = {1: a.family, 2: a.style, 3: f"{version};{ps_fam}-{ps_style}", 4: full, 6: f"{ps_fam}-{ps_style}",
           10: f"Renamed copy of {old_family} ({pathlib.Path(a.src).name}) for private use; glyphs unchanged."}
    recs = [r for r in old if r[3] not in DROP and r[3] != 10]
    recs += [(3, 1, 0x409, nid, s.encode("utf-16-be")) for nid, s in new.items()]
    tables[ti[b"name"]][1] = build_names(recs)
    # head.checkSumAdjustment (offset 8) = 0 while summing
    head = bytearray(tables[ti[b"head"]][1])
    head[8:12] = b"\0\0\0\0"
    tables[ti[b"head"]][1] = bytes(head)
    n = len(tables)
    off = 12 + 16 * n
    dirs, blobs = b"", b""
    for tag, data in tables:
        dirs += struct.pack(">4sIII", tag, checksum(data), off, len(data))
        pad = data + b"\0" * (-len(data) % 4)
        blobs += pad
        off += len(pad)
    font = bytearray(struct.pack(">I", ver) + hdr_rest + dirs + blobs)
    adj = (0xB1B0AFBA - checksum(bytes(font))) & 0xFFFFFFFF
    hpos = 12 + 16 * n + sum(len(t[1]) + (-len(t[1]) % 4) for t in tables[:ti[b"head"]])
    font[hpos + 8:hpos + 12] = struct.pack(">I", adj)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_bytes(bytes(font))
    chk = read_names(read_tables(bytes(font))[2][ti[b"name"]][1])
    left = sorted({r[3] for r in chk} & {16, 17, 21, 22, 25})
    print(f"{pathlib.Path(a.src).name} -> {out.name}: family '{text(chk, 1)}', style '{text(chk, 2)}', full '{text(chk, 4)}', "
          f"PostScript '{text(chk, 6)}'; typographic names left: {left or 'none'}; {len(font)} bytes")


if __name__ == "__main__":
    main()
