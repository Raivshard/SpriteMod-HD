r"""Render every room and the world map of FF1 Anniversary Edition (PSP) from their layouts (reference only).

Format [read from a retail copy (ULUS10251), 2026-11-25; world map and rooms checked by eye]:
  Each `unpacked\MAP_*` folder is one tile set: `0<pack>.GIM` .. `3<pack>.GIM` (512x512 sheets, 32 px tiles,
  16 per row). A tile number t is drawn from two sheets at the same position k = t & 0xFF: a ground sheet and an
  overlay sheet on top (forest, coast, walls, furniture; mostly transparent).
    Rooms (layouts use tiles 0-255 only) [verified against 7 PPSSPP shots of Castle Cornelia, 2026-11-25]:
      inside view  = sheet 0 + sheet 1 (the player is inside a building / room)
      outside view = sheet 2 + sheet 3 (same tiles seen from outside: roofs closed, interiors hidden)
    World-map style sets (`MAP_00` incl. the `MAP_EX01_R13-15` fields, `MAP_EX03_R54`; layouts use tiles 0-511):
      t < 256: sheet 0 + sheet 1; t >= 256: sheet 2 + sheet 3
  `<room>_AMD.BIN` = the room: u32 0, u16 width, u16 height, then width*height u16 cells; tile = value & 0x1FF
  (tile 0 is a real tile).
  `<pack>_ANM.ATI` = animated tiles (water, whirlpools, lava): u32 n, n u32 offsets; each entry = u16 count,
  u16 0, u16 frame type (2 = 16 frames, else 4), u16 0, 2x i16 -1, u16 flag, u16 0, u32 ANM sheet (0 =
  `0<pack>_ANM.GIM` or `<pack>_ANM.GIM`, 1 = `1<pack>_ANM.GIM`), u32 first frame (tile index in that sheet); then
  count records of u32 layer (0 = ground, 1 = overlay), u32 tile number. The first frame is drawn on top of
  that layer's still tile (animated frames have cut-outs where the still tile shows through).
  `<pack>_ASC.BIN` (u32 2, u32, u8 64, u8 64, u16, then 4096 u16) = 8 entries per tile [inferred: the 16 px
  pieces of each tile, 4 per layer]; not needed for rendering.
  Cells with nothing on any sheet stay black (dungeon void; the sea of `MAP_EX03_R54`, whose fill is not decoded).

Shadow / foreground layers [decoded 2026-11-25; checked by eye and by pixel match against the room]:
  `<set>_IN.ACD`, `<set>_IN.ACT`, `<set>_OUT.ACD`, `<set>_OUT.ACT`: same header and size as a room layout
  (2 bytes shorter: the layout is padded to 4). Each cell u16: 0 = empty, tile = value & 0xFF in the set's extra
  sheet (`<set>.GIM`, else `<pack>.GIM`; for IN, `<set>_IN.GIM` / `<pack>_IN.GIM` first when present; tile sets
  with one extra sheet per room, `<pack>_nn.GIM`, are matched by pixel content), flags = value >> 8:
  0x200 = mirror left-right, 0x100 = flip top-bottom (0x300 = both). The extra sheet holds soft shadows (~30 %
  alpha) and opaque cliff / wall / tree tops that repeat the room art. Drawn over the room: ACD, then ACT.
  IN layers go with the inside view, OUT layers with the outside view [verified on Castle Cornelia: inside rooms
  0/1 + IN matches the game best, outside 2/3 + OUT; cloud shadows in towns are a separate moving effect]. A layer
  set belongs to the layout with the same name (underscores ignored) and size; otherwise to the same-size layout in
  the tile set whose tiles match best (or whose name ends the same).

Usage: python ff1psp_maps.py <unpacked folder> <out maps folder> [--force]
  writes <out>\<pack>\<room>.png (inside view, no layers), <out>\<pack>\IN\<set>.png (inside view + IN layers),
  \OUT\<set>.png (outside view + OUT layers; rooms with no OUT layers but a different outside view get
  OUT\<room>.png), <out>\<pack>\layout\ (the room files as stored), <out>\rooms.csv and <out>\layer_sets.csv.
Import: render_all(unpacked, out) -> (rows, problems).
"""
import csv
import shutil
import struct
import sys
from pathlib import Path

from PIL import Image, ImageChops, ImageOps, ImageStat

T = 32

# Place names, found by matching PPSSPP screenshots against the renders [verified 2026-11-25]
PLACES = {"MAP_0A_00_00": "Castle Cornelia, ground floor", "MAP_0A_00_01": "Castle Cornelia, upper floor (throne room)",
          "MAP_0A_01_00": "Cornelia (town)"}


def read_room(p):
    b = p.read_bytes()
    _z, w, h = struct.unpack_from("<IHH", b, 0)
    if 8 + w * h * 2 > len(b) or w == 0 or h == 0:
        raise ValueError(f"size {w}x{h} does not fit {len(b)} bytes")
    return w, h, struct.unpack_from(f"<{w*h}H", b, 8)


def read_ati(p):
    """{(layer, tile): (anm sheet, first frame)}"""
    amap = {}
    if not p.exists():
        return amap
    b = p.read_bytes()
    n = struct.unpack_from("<I", b, 0)[0]
    for o in struct.unpack_from(f"<{n}I", b, 4):
        cnt = struct.unpack_from("<H", b, o)[0]
        sheet, first = struct.unpack_from("<II", b, o + 16)
        for j in range(cnt):
            layer, t = struct.unpack_from("<II", b, o + 24 + 8 * j)
            amap[(layer, t)] = (sheet, first)
    return amap


def crop(img, k):
    if img is None:
        return None
    cols = img.width // T
    x, y = (k % cols) * T, (k // cols) * T
    if y + T > img.height:
        return None
    return img.crop((x, y, x + T, y + T))


class TileSet:
    def __init__(self, d, outside=False):
        def load(p):
            return Image.open(p).convert("RGBA") if p.exists() else None
        pk = d.name
        self.sheets = [load(d / f"{k}{pk}.GIM.png") for k in range(4)]
        if outside:  # rooms only: outside view = sheets 2 + 3
            self.sheets = self.sheets[2:] + self.sheets[:2]
        self.anm = [load(d / f"0{pk}_ANM.GIM.png") or load(d / f"{pk}_ANM.GIM.png"), load(d / f"1{pk}_ANM.GIM.png")]
        self.amap = read_ati(d / f"{pk}_ANM.ATI")
        self.cache = {}

    def tile(self, t):
        if t not in self.cache:
            img = Image.new("RGBA", (T, T), (0, 0, 0, 0))
            for layer in (0, 1):
                src = [crop(self.sheets[2 * (t >> 8) + layer], t & 0xFF)]
                a = self.amap.get((layer, t))
                if a is not None and a[0] < len(self.anm):
                    src.append(crop(self.anm[a[0]], a[1]))
                for s in src:
                    if s is not None:
                        img.alpha_composite(s)
            self.cache[t] = img
        return self.cache[t]


def render(w, h, cells, ts):
    img = Image.new("RGBA", (w * T, h * T), (0, 0, 0, 255))
    for i, v in enumerate(cells):
        img.alpha_composite(ts.tile(v & 0x1FF), ((i % w) * T, (i // w) * T))
    return img


def norm(s):
    return s.replace("_", "").upper()


def common_suffix(a, b):
    n = 0
    while n < min(len(a), len(b)) and a[-1 - n] == b[-1 - n]:
        n += 1
    return n


def layer_tile(plain, v):
    c = crop(plain, v & 0xFF)
    if c is not None:
        if v & 0x200:
            c = ImageOps.mirror(c)
        if v & 0x100:
            c = ImageOps.flip(c)
    return c


def match_score(ts, base, layers, plain):
    """Mean pixel difference between the opaque layer tiles and the room tiles under them; None if none to compare."""
    s = n = 0
    for cells in layers:
        for i, v in enumerate(cells):
            if not v & 0xFF:
                continue
            c = layer_tile(plain, v)
            if c is None:
                s, n = s + 255, n + 1
                continue
            if ImageStat.Stat(c.getchannel("A")).mean[0] < 200:
                continue
            diff = ImageChops.difference(c.convert("RGB"), ts.tile(base[i] & 0x1FF).convert("RGB"))
            s, n = s + ImageStat.Stat(diff).mean[0], n + 1
    return s / n if n >= 5 else None


def layer_sets(d, ts, layouts, problems):
    """[(set, state, layout room, plain sheet path, how matched, (acd, act))]"""
    def load(p):
        return Image.open(p).convert("RGBA")
    out = []
    room_sheets = sorted(p for p in d.glob(f"{d.name}_[0-9]*.GIM.png"))
    for acd in sorted(d.glob("*.ACD")):
        name, state = acd.stem.rsplit("_", 1)
        act_p = acd.with_suffix(".ACT")
        try:
            w, h, c1 = read_room(acd)
            c2 = read_room(act_p)[2] if act_p.exists() else ()
        except Exception as e:
            problems.append(f"{d.name}/{acd.name}: {e}")
            continue
        layers = (c1, c2)
        same = [r for r, (rw, rh, _) in layouts.items() if (rw, rh) == (w, h)]
        if not same:
            problems.append(f"{d.name}/{name}_{state}: no room layout {w}x{h} in this tile set; not rendered")
            continue
        sheet_names = ([f"{name}_IN", f"{d.name}_IN"] if state == "IN" else []) + [name, d.name]
        sheets = [d / f"{n}.GIM.png" for n in sheet_names if (d / f"{n}.GIM.png").exists()][:1] or room_sheets
        if not sheets:
            problems.append(f"{d.name}/{name}_{state}: no extra sheet; not rendered")
            continue
        exact = [r for r in same if norm(r) == norm(name)]
        rooms = exact or same
        how = "name" if exact else "size"
        if len(rooms) * len(sheets) == 1:
            out.append((name, state, rooms[0], sheets[0], how, layers))
            continue
        scored = []
        for r in rooms:
            for p in sheets:
                sc = match_score(ts, layouts[r][2], layers, load(p))
                scored.append((sc if sc is not None else 1e9, -common_suffix(norm(r), norm(name)), r, p))
        scored.sort(key=lambda x: (x[0], x[1]))
        best = scored[0]
        how += ", pixel match" if best[0] < 1e9 else ", name ending [inferred]"
        if best[0] >= 1e9:
            best = sorted(scored, key=lambda x: (x[1], x[2]))[0]
        out.append((name, state, best[2], best[3], how, layers))
    return out


def render_all(unpacked, out):
    rows, problems, lrows = [], [], []
    for d in sorted(p for p in Path(unpacked).iterdir() if p.is_dir() and p.name.startswith("MAP")):
        rooms = sorted(d.glob("*_AMD.BIN"))
        if not rooms:
            continue
        if not (d / f"0{d.name}.GIM.png").exists():
            problems.append(f"{d.name}: no 0{d.name}.GIM sheet; {len(rooms)} room(s) not rendered")
            continue
        ts = TileSet(d)
        layouts, images = {}, {}
        for r in rooms:
            room = r.name[:-len("_AMD.BIN")]
            try:
                w, h, cells = read_room(r)
            except Exception as e:
                problems.append(f"{d.name}/{r.name}: {e}")
                continue
            layouts[room] = (w, h, cells)
            dst = Path(out) / d.name / f"{room}.png"
            dst.parent.mkdir(parents=True, exist_ok=True)
            images[room] = render(w, h, cells, ts)
            images[room].save(dst)
            lay = dst.parent / "layout"
            lay.mkdir(exist_ok=True)
            for f in d.glob(f"{room}_*"):
                shutil.copyfile(f, lay / f.name)
            rows.append([d.name, room, PLACES.get(room, ""), w, h, f"{d.name}/{room}.png", len(ts.amap)])
        wide = any(v & 0x100 for _w, _h, c in layouts.values() for v in c)  # world-map style: no outside view
        ts_out = TileSet(d, outside=True) if not wide and ts.sheets[2] is not None else None
        outside = {}

        def outside_view(room):
            if room not in outside:
                outside[room] = render(*layouts[room], ts_out) if ts_out else images[room]
            return outside[room]
        sheets, has_out = {}, set()
        for name, state, room, sheet, how, layers in layer_sets(d, ts, layouts, problems):
            if sheet not in sheets:
                sheets[sheet] = Image.open(sheet).convert("RGBA")
            w = layouts[room][0]
            img = (outside_view(room) if state == "OUT" else images[room]).copy()
            if state == "OUT":
                has_out.add(room)
            missing = 0
            for cells in layers:
                for i, v in enumerate(cells):
                    if v & 0xFF:
                        c = layer_tile(sheets[sheet], v)
                        if c is None:
                            missing += 1
                            continue
                        img.alpha_composite(c, ((i % w) * T, (i // w) * T))
            if missing:
                how += f"; {missing} tiles outside the sheet, not drawn"
                problems.append(f"{d.name}/{name}_{state}: {missing} tiles outside {sheet.name} (no sheet fits; not drawn)")
            dst = Path(out) / d.name / state / f"{name}.png"
            dst.parent.mkdir(exist_ok=True)
            img.save(dst)
            for f in d.glob(f"{name}_{state}.AC?"):
                shutil.copyfile(f, Path(out) / d.name / "layout" / f.name)
            lrows.append([d.name, name, state, room, sheet.name[:-len(".GIM.png")], how, f"{d.name}/{state}/{name}.png"])
        if ts_out:  # rooms without OUT layers still get their outside view when it differs
            for room in layouts:
                if room in has_out:
                    continue
                img = outside_view(room)
                if ImageChops.difference(img, images[room]).getbbox():
                    dst = Path(out) / d.name / "OUT" / f"{room}.png"
                    dst.parent.mkdir(exist_ok=True)
                    img.save(dst)
                    lrows.append([d.name, room, "OUT", room, "", "outside view only (no OUT layers)",
                                  f"{d.name}/OUT/{room}.png"])
    Path(out).mkdir(parents=True, exist_ok=True)
    with (Path(out) / "rooms.csv").open("w", newline="", encoding="utf-8") as fh:
        wr = csv.writer(fh)
        wr.writerow(["tile set", "room", "place", "width", "height", "rendered file", "animated tiles in set"])
        wr.writerows(rows)
    with (Path(out) / "layer_sets.csv").open("w", newline="", encoding="utf-8") as fh:
        wr = csv.writer(fh)
        wr.writerow(["tile set", "layer set", "state", "room layout", "extra sheet", "matched by", "rendered file"])
        wr.writerows(lrows)
    print(f"layer sets rendered: {len(lrows)}")
    return rows, problems


def main():
    a = sys.argv[1:]
    if len(a) < 2 or a[0] in ("-h", "--help"):
        raise SystemExit(__doc__)
    out = Path(a[1])
    if out.exists() and "--force" not in a:
        raise SystemExit(f"{out} exists; pass --force")
    rows, problems = render_all(Path(a[0]), out)
    print(f"rooms rendered: {len(rows)}; problems: {len(problems)}")
    for p in problems:
        print("  ", p)


if __name__ == "__main__":
    main()
