r"""Sort the FF1 Anniversary Edition (PSP) extract into an ordered reference folder (reference art, never for release).

Input: the `unpacked\` folder written by ff1psp_dpk.py. Every picture is kept once (identical pictures merged by
pixel content); `index.csv` lists every original copy. Textures embedded in `.ANA` / `.EFA` files (effects, weapons)
are cut out and converted too (`<file>_<n>.png`). Categories come from the container folder names (see RULES).

Usage: python ff1psp_order.py <unpacked folder> <out folder> [--force]
  refuses to write into an existing <out> unless --force (then it must be a folder this tool made: has index.csv).
  --force rebuilds the whole folder, so it first checks `.built_files.csv` (every file the last build wrote, with
  size and time): any file added or changed since stops the run (exit 3) with the list. Those are your own files.
  ff1psp_maps.py run alone on <out>\maps: run `python ff1psp_order.py --record <out>` afterwards to update the list.
Reads only; never runs anything from the game files.
"""
import csv
import hashlib
import io
import re
import shutil
import struct
import sys
from collections import defaultdict
from pathlib import Path

from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parent))
import ff1psp_dpk  # noqa: E402

# (folder regex, file regex or None, target folder; {f} = source folder, {g} = gallery part)
RULES = [
    (r"MS2?_", r"_S$", "monsters/shadows"),
    (r"MS2?_", None, "monsters/battle"),
    (r"BOSS", None, "monsters/boss"),
    (r"MON_BOOK$", None, "ui/bestiary"),
    (r"M?_?JOB_", None, "characters/battle/{f}"),
    (r"JOBMAGI", None, "characters/battle/{f}"),
    (r"MEV", r"^(EXTRA_|WORLD)", "world_map/objects"),
    (r"MEV", r"^PC\d\d$", "characters/party_field"),
    (r"MEV", None, "characters/field"),
    (r"MAP_", None, "tilesets/{f}"),
    (r"STFL_BG", None, "art/story_backgrounds"),
    (r"BG_", None, "backgrounds/battle"),
    (r"SS\d", None, "art/screens"),
    (r"GARELLY_(L|R|LT|RT|LB|RB)\d", None, "art/gallery/parts_{g}"),
    (r"GARELLY(\d|$)", None, "art/gallery"),
    (r"(OPDEMO|PROLOGU|EPILOGU|THEEN|CLEAR|STAFF_ROLL|TITLE_LOGO)", None, "art/story/{f}"),
    (r"USEVM", None, "text/event_fonts/US"),
    (r"J2EVM", None, "text/event_fonts/J2"),
    (r"EVM", None, "text/event_fonts/J"),
    (r"(FM_|FONT_)", None, "ui/menu_fonts"),
    (r"WEAPON$", None, "battle_weapons"),
    (r"(FIEL|NEWDUNGEON)", None, "effects/map/{f}"),
    (r"(EFFECT\d*|MUEF_EFFECT|ICOMMON)$", None, "effects/battle/{f}"),
    (r"(AIRSHIP|CANAL|CHAOS|MARIN|OBSERV|MU_CLYSTAL)", None, "effects/event/{f}"),
    (r"(MINIMAP|MENU_CAMP|BATTLEICON|COMMON|MUSIC_PLAYER|MINI_GAM|MSICON|MSICON_US|EXTRA_MENU)", None, "ui/{f}"),
]


def target(folder, stem):
    for fr, nr, t in RULES:
        if re.match(fr, folder) and (nr is None or re.search(nr, stem)):
            m = re.match(r"GARELLY_([A-Z]+)", folder)
            return t.format(f=folder, g=m.group(1) if m else "")
    return f"misc/{folder}"


def embedded_gims(b):
    at = 0
    while True:
        at = b.find(b"MIG.00.1PSP", at)
        if at < 0:
            return
        bid, _u, size = struct.unpack_from("<HHI", b, at + 16)
        yield b[at:at + 16 + size] if bid == 2 else b[at:]
        at += 16


def pictures(unpacked, problems):
    """(source folder, stem, PIL image, original path text) for every picture."""
    for d in sorted(p for p in unpacked.iterdir() if p.is_dir()):
        for f in sorted(d.rglob("*")):
            if not f.is_file():
                continue
            rel = f.relative_to(unpacked).as_posix()
            folder = d.name
            if f.name.endswith(".GIM.png"):
                yield folder, f.name[:-8], Image.open(f).convert("RGBA"), rel
            elif f.suffix.upper() == ".PNG" and not f.name.endswith(".png"):
                yield folder, f.stem, Image.open(f).convert("RGBA"), rel
            elif f.suffix.upper() in (".ANA", ".EFA"):
                for k, g in enumerate(embedded_gims(f.read_bytes())):
                    buf = io.BytesIO()
                    try:
                        ff1psp_dpk.gim_to_png(g, buf)
                    except Exception as e:
                        problems.append(f"{rel} #{k}: {e}")
                        continue
                    buf.seek(0)
                    tag = "" if f.suffix.upper() == ".ANA" else "_efa"
                    yield folder, f"{f.stem}{tag}_{k}",Image.open(buf).convert("RGBA"), f"{rel}#{k}"


# Party field sprites (MEV_CMN\PCnm: n = class, m = 0 base / 1 after class change) [verified by eye 2026-11-25]
PARTY = {"PC10": "Warrior", "PC11": "Knight", "PC20": "Thief", "PC21": "Ninja", "PC30": "Monk", "PC31": "Master",
         "PC40": "Red_Mage", "PC41": "Red_Wizard", "PC50": "White_Mage", "PC51": "White_Wizard",
         "PC60": "Black_Mage", "PC61": "Black_Wizard"}

MON_RE = re.compile(r"^(MS|BOSS)_([0-9A-F]{2})(_S)?$")


def monster_id(stem):
    """Game monster number for a sprite name [verified by eye on 29 sprites, 2026-11-25]:
    MS_00-7F = monsters 0-127, MS_80-B9 = monsters 145-202, BOSS_00-10 = monsters 128-144 (bonus bosses)."""
    m = MON_RE.match(stem)
    if not m:
        return None
    v = int(m.group(2), 16)
    if m.group(1) == "BOSS":
        return 128 + v
    return v if v < 0x80 else v - 0x80 + 145


def monster_names(unpacked, problems):
    import ff1psp_text
    d = unpacked / "FONT_BATTLEUS"
    try:
        return ff1psp_text.strings(d / "MONSTER_NAME.MSG", d / "FONT_BATTLEUS.FIF")
    except Exception as e:
        problems.append(f"monster names: {e}")
        return []


MANIFEST = ".built_files.csv"


def record(out):
    """Write the list of every file in <out> (path, size, time) as this tool's output."""
    rows = [[f.relative_to(out).as_posix(), f.stat().st_size, f.stat().st_mtime_ns]
            for f in sorted(out.rglob("*")) if f.is_file() and f.name != MANIFEST]
    with (out / MANIFEST).open("w", newline="", encoding="utf-8") as fh:
        csv.writer(fh).writerows(rows)
    return len(rows)


def changed_since_build(out):
    m = out / MANIFEST
    if not m.exists():
        return [f"(no {MANIFEST}: cannot tell which files are the tool's)"]
    known = {r[0]: (int(r[1]), int(r[2])) for r in csv.reader(m.open(encoding="utf-8"))}
    found = []
    for f in sorted(out.rglob("*")):
        if f.is_file() and f.name != MANIFEST:
            rel = f.relative_to(out).as_posix()
            if known.get(rel) != (f.stat().st_size, f.stat().st_mtime_ns):
                found.append(rel)
    return found


def main():
    a = sys.argv[1:]
    if len(a) < 2 or a[0] in ("-h", "--help"):
        raise SystemExit(__doc__)
    if a and a[0] == "--record":
        print(f"recorded {record(Path(a[1]))} files in {Path(a[1]) / MANIFEST}")
        return
    force = "--force" in a
    unpacked, out = Path(a[0]), Path(a[1])
    if out.exists():
        if not force:
            raise SystemExit(f"{out} exists; pass --force to rebuild it")
        if not (out / "index.csv").exists():
            raise SystemExit(f"{out} has no index.csv; not a folder this tool made, refusing")
        changed = changed_since_build(out)
        if changed:
            print("STOPPED: NEEDS YOUR CONSENT. These files were added or changed after the last build and a rebuild "
                  "would delete them:")
            for c in changed:
                print("  ", c)
            raise SystemExit(3)
        shutil.rmtree(out)
    problems = []
    groups = {}  # md5 -> [image, first stem, folder, [(folder, original)]]
    order = []
    for folder, stem, img, rel in pictures(unpacked, problems):
        h = hashlib.md5(img.tobytes() + struct.pack("<II", *img.size)).hexdigest()
        if h not in groups:
            groups[h] = [img, stem, folder, []]
            order.append(h)
        groups[h][3].append((folder, rel, stem))
    mon_names = monster_names(unpacked, problems)
    mon_rows = []
    for h in order:  # monsters: the lowest sprite number among identical copies, plus the name
        g = groups[h]
        st = sorted({c[2] for c in g[3] if MON_RE.match(c[2])})
        if st:
            g[1] = st[0]
            ids = [i for i in (monster_id(s) for s in st) if i is not None]
            nm = [mon_names[i] for i in ids if i < len(mon_names)]
            g.append((st, ids, nm))
    # place: name = stem; same name with different pixels in one target folder -> add the source folder
    names = defaultdict(set)
    for h in order:
        img, stem, folder, _ = groups[h][:4]
        names[(target(folder, stem), stem)].add(h)
    rows = []
    counts = defaultdict(int)
    for h in order:
        img, stem, folder, copies = groups[h][:4]
        t = target(folder, stem)
        src = Path(copies[0][1].split("#")[0]).parent.name  # the (nested) folder of the first copy
        name = stem if len(names[(t, stem)]) == 1 else f"{stem}__{src}"
        if t == "characters/party_field" and stem in PARTY:
            name = f"{stem}_{PARTY[stem]}"
        if len(groups[h]) > 4 and groups[h][4][2]:
            st, ids, nm = groups[h][4]
            name = f"{stem}_{re.sub(r'[^A-Za-z0-9-]+', '_', nm[0]).strip('_')}"
            mon_rows.append([f"{t}/{name}.png", " ".join(st), " ".join(str(i) for i in ids), " / ".join(dict.fromkeys(nm))])
        p = out / t / f"{name}.png"
        p.parent.mkdir(parents=True, exist_ok=True)
        if p.exists():
            problems.append(f"name clash {p}")
            p = p.with_name(f"{name}__{h[:6]}.png")
        img.save(p)
        counts[t.split("/")[0]] += 1
        used = sorted({c[0] for c in copies})
        rows.append([p.relative_to(out).as_posix(), h, len(copies), " ".join(used),
                     " | ".join(c[1] for c in copies)])
    with (out / "index.csv").open("w", newline="", encoding="utf-8") as fh:
        w = csv.writer(fh)
        w.writerow(["kept file", "md5", "copies", "used in", "original paths"])
        w.writerows(sorted(rows))
    import ff1psp_maps
    mrows, mprob = ff1psp_maps.render_all(unpacked, out / "maps")
    problems += mprob
    counts["maps (rooms)"] = len(mrows)
    import ff1psp_frames
    frows, fprob = ff1psp_frames.render_all(unpacked, out / "frames")
    problems += fprob
    counts["frames (sprites)"] = len(frows)
    (out / "monsters").mkdir(parents=True, exist_ok=True)   # partial unpacks (--only) may hold no monsters
    with (out / "monsters" / "names.csv").open("w", newline="", encoding="utf-8") as fh:
        w = csv.writer(fh)
        w.writerow(["kept file", "game sprites", "monster numbers", "names"])
        w.writerows(sorted(mon_rows))
    (out / "problems.txt").write_text("\n".join(problems) + "\n", encoding="utf-8")
    readme = Path(__file__).resolve().parent / "ff1psp_ordered_README.md"
    if readme.exists():
        shutil.copyfile(readme, out / "README.md")
    record(out)
    total = sum(len(g[3]) for g in groups.values())
    print(f"pictures read: {total}; kept (unique): {len(order)}; problems: {len(problems)}")
    for k in sorted(counts):
        print(f"  {k:16} {counts[k]}")


if __name__ == "__main__":
    main()
