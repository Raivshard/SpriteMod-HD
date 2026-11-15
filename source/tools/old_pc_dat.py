"""Convert image .dat files from the old (2015) FF5 PC release (and its mods) to PNG.

Format [verified 2026-11-14 on the 479 files of one sprite mod: sprites, NPCs, portraits]: 4-byte header "INP\\0", then a complete
PNG file (ends with IEND, nothing after). The PNG bytes are copied unchanged (no re-encode), then opened with
PIL to check they decode.

Usage: python old_pc_dat.py <folder with .dat files> <output folder> [--force]
Refuses to overwrite existing PNGs unless --force. Prints counts, sizes per name prefix, and any skipped file.
"""
import sys
from collections import Counter
from pathlib import Path

from PIL import Image

HEAD = b"INP\x00"
PNG = b"\x89PNG\r\n\x1a\n"


def main(argv):
    args = [a for a in argv if not a.startswith("--")]
    force = "--force" in argv
    if len(args) != 2:
        print(__doc__)
        return 2
    src, out = Path(args[0]), Path(args[1])
    files = sorted(src.rglob("*.dat"))
    if not files:
        print(f"no .dat files in {src}")
        return 2
    out.mkdir(parents=True, exist_ok=True)
    done, skipped, existed = 0, [], 0
    sizes = Counter()
    for f in files:
        b = f.read_bytes()
        if not (b.startswith(HEAD) and b[4:12] == PNG):
            skipped.append(f"{f.name}: header {b[:12].hex(' ')}")
            continue
        dst = out / f.relative_to(src).with_suffix(".png")
        if dst.exists() and not force:
            existed += 1
            continue
        dst.parent.mkdir(parents=True, exist_ok=True)
        dst.write_bytes(b[4:])
        try:
            with Image.open(dst) as im:
                im.load()
                prefix = "".join(c for c in f.stem.split("_")[0] if not c.isdigit())
                sizes[(prefix, im.size, im.mode)] += 1
        except Exception as e:  # keep the file for inspection, report it
            skipped.append(f"{f.name}: PIL could not decode ({e})")
            continue
        done += 1
    print(f"source: {src}\noutput: {out}")
    print(f".dat files: {len(files)}; PNGs written: {done}; already there (skipped, use --force): {existed}; problems: {len(skipped)}")
    for (prefix, size, mode), n in sorted(sizes.items()):
        print(f"  {prefix:8} {size[0]}x{size[1]} {mode:5} x{n}")
    for s in skipped:
        print("  PROBLEM", s)
    return 1 if skipped else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
