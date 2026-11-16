"""Make an HD map tile sheet with the game sheet's exact layout, for the plugin's map_tiles swap.

The plugin (FieldTiles.cs) lets the game load its own sheet (512x512, 32 x 32 tiles of 16 px [verified, export]) and then
draws map_tiles/<sheet>.png instead; any size with the same layout fits, a whole multiple keeps tile edges on pixels.

Usage: python map_tiles_hd.py <game sheet.png> <out.png> [--scale N] [--grid] [--force]
  --scale N  nearest-neighbour enlargement (default 2): every pixel becomes an N x N block, no new colours
  --grid     test only: draws a 1 px magenta line on every tile's top and left edge, so the screen shows that the
             HD sheet is used and that tiles line up (the line is thinner than one game pixel)
"""
import sys
from pathlib import Path

from PIL import Image

TILE = 16


def main(argv):
    args = [a for a in argv if not a.startswith("--")]
    scale = int(argv[argv.index("--scale") + 1]) if "--scale" in argv else 2
    if "--scale" in argv:
        args.remove(str(scale))
    if len(args) != 2:
        print(__doc__)
        return 2
    src, out = Path(args[0]), Path(args[1])
    if out.exists() and "--force" not in argv:
        print(f"{out} exists: use --force")
        return 1
    im = Image.open(src).convert("RGBA")
    big = im.resize((im.width * scale, im.height * scale), Image.Resampling.NEAREST)
    lines = 0
    if "--grid" in argv:
        px = big.load()
        step = TILE * scale
        for y in range(big.height):
            for x in range(big.width):
                if (x % step == 0 or y % step == 0) and px[x, y][3] > 0:
                    px[x, y] = (255, 0, 255, 255)
                    lines += 1
    out.parent.mkdir(parents=True, exist_ok=True)
    big.save(out)
    # check: every HD block equals its source pixel (grid pixels excepted)
    bad = 0
    sp, bp = im.load(), big.load()
    for y in range(big.height):
        for x in range(big.width):
            if bp[x, y] != sp[x // scale, y // scale] and not ("--grid" in argv and bp[x, y] == (255, 0, 255, 255)):
                bad += 1
    print(f"{src.name} {im.width}x{im.height} -> {out} {big.width}x{big.height} (x{scale}), grid pixels {lines}, "
          f"pixels differing from source {bad}")
    return 0 if bad == 0 else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
