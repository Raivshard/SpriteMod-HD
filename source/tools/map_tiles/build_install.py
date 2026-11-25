r"""Build and install a map project's work in one run (usable without an AI assistant; one command).
The project (project.py): --project <folder>, or the project of a .tmj argument (the Tiled command passes the map),
else Karnak (Working\tilesets\Karnak). Its project.json gives the game, maps, sheets and folders.
Steps (each printed with its result; stops at the first failure):
  1. tile art        mobile projects (Karnak): write_mobile_replacements.py (votes, decisions, manual_choices incl.
                     Tiled picks, edge blending); other projects: write_choices.py (choices.csv -> overrides)
  2. sheets          map_tiles_pack.py build --scale <project scale> (the project's sheets)
  3. animations      mobile projects only: tile_anim.py (lava walls, inn clock) -> pack\anim
  4. shadows         mobile projects only: map_shadow_sim.py --export (the project's shadow_maps)
  5. install         sheets -> <mod>\map_tiles, anim -> <mod>\map_tiles\anim, layouts -> <mod>\GameAssets\Serial\Res\Map,
                     objects -> <mod>\map_objects, shadow masks -> <mod>\map_shadows; project copies refreshed
                     (build\Karnak\installed, build\Karnak\shadow_masks)
  6. Tiled atlases   tiled_refresh_atlas.py: the "build (current)" tiles in Tiled = the installed art (.tmj untouched)
Refuses to install while the game runs. Does not import Tiled maps: run tiled_import.py first (or the Tiled command).
Usage: python build_install.py [--project <folder> | <map>.tmj] [--no-install]
"""
import shutil, subprocess, sys, time
from pathlib import Path

HERE = Path(__file__).resolve().parent
PROJECT = HERE.parents[1]
sys.path.insert(0, str(HERE))
import guard   # your saved files are never changed without consent (takes --overwrite-saved off the line)
from project import P   # first: it picks the game
from layouts import LAYOUTS
import games   # --game was taken by layouts' import of games.py
sys.path.insert(0, str(__import__('pathlib').Path(__file__).resolve().parents[1]))  # tools folder
import paths   # project folder layout (tools\paths.py)

M = paths.MOBILE_MATCHING
ROOT = P.build
MOD = games.GAME.mod
VOTES = ["votes.csv", "votes_stacked.csv", "votes_sheet.csv", "votes_sheet_stacked.csv", "votes_world.csv", "votes_world_sheet.csv"]


def run(title, args, consent=True):
    t = time.time()
    p = subprocess.run([sys.executable, "-W", "ignore"] + [str(x) for x in args] + (["--overwrite-saved"] if guard.CONSENT and consent else []),
                       cwd=HERE, capture_output=True, text=True)
    if p.returncode == 3:   # guard.py: a file needs the user's consent; show the whole message and stop at once
        print(f"== {title}: STOPPED")
        print((p.stdout + p.stderr).strip())
        sys.exit(3)
    last = [l for l in (p.stdout + p.stderr).strip().splitlines() if l.strip()][-3:]
    print(f"== {title}: {'ok' if p.returncode == 0 else 'FAILED'} ({time.time() - t:.0f} s)")
    for l in last:
        print("   " + l[:200])
    if p.returncode != 0:
        sys.exit(f"stopped at: {title}")


def game_running():
    try:
        out = subprocess.run(["tasklist"], capture_output=True, text=True).stdout
        return (games.GAME.exe + ".exe").lower() in out.lower()
    except Exception:
        return False


def tree_copies(src, dst):
    """(source, destination) of every file of src that differs from its copy under dst"""
    if not src.exists():
        return []
    return [(f, dst / f.relative_to(src)) for f in src.rglob("*") if f.is_file()
            and (not (dst / f.relative_to(src)).exists() or (dst / f.relative_to(src)).read_bytes() != f.read_bytes())]


def do_copies(pairs):
    for f, d in pairs:
        d.parent.mkdir(parents=True, exist_ok=True)
        guard.copy(f, d)
    return len(pairs)


def main():
    no_install = "--no-install" in sys.argv
    if not no_install and game_running():
        sys.exit("the game is running: close it, then run again (or use --no-install)")
    print(f"project {P}")
    sheets = P.sheets
    if P.mobile:
        run("1 tile art", ["write_mobile_replacements.py"] + [M / v for v in VOTES] + ["--variants", P.folder / "location_variants.csv", "--root", ROOT, "--force"])
    else:
        run("1 tile art", ["write_choices.py"])
    run("2 sheets", [HERE / "map_tiles_pack.py", "build", "--root", ROOT, "--scale", P.scale, "--sheets", ",".join(sheets), "--force"])
    tmp = P.shadow_masks
    if P.mobile:
        run("3 animations", ["tile_anim.py", "--pack", ROOT / "pack"])
        run("4 shadows", ["map_shadow_sim.py", tmp / "_page.html", ROOT, "--maps"] + P.shadow_maps +
            ["--lengths", "0.5", "--opacity", "0.5", "--blur", "2", "--export", tmp])
    if no_install:
        print("not installed (--no-install)")
        return
    pack = ROOT / "pack"
    sheet_copies = []
    for s in sheets:
        f = pack / f"{s}.hd.png"
        if not f.exists():
            continue                                   # nothing changed on this sheet (no override, no replacement)
        for d in (MOD / "map_tiles", P.installed):
            if not (d / f.name).exists() or (d / f.name).read_bytes() != f.read_bytes():
                sheet_copies.append((f, d / f.name))
    anim = tree_copies(pack / "anim", MOD / "map_tiles" / "anim")
    lay = tree_copies(LAYOUTS, MOD / "GameAssets" / "Serial" / "Res" / "Map")
    obj = tree_copies(P.objects, MOD / "map_objects")
    shd = []
    for m in P.shadow_maps if P.mobile else []:
        for f in [tmp / f"{m}.png"] + sorted(tmp.glob(f"{m}.*.png")):   # <map>.png and <map>.decor.png
            d = MOD / "map_shadows" / f.name
            if f.exists() and (not d.exists() or d.read_bytes() != f.read_bytes()):
                shd.append((f, d))
    # every installed file, checked before the first copy (a file you saved in the game folder needs your consent)
    guard.need([d for _, d in sheet_copies + anim + lay + obj + shd], "replace")
    n = sum(1 for _, d in sheet_copies if d.parent == MOD / "map_tiles")
    do_copies(sheet_copies)
    print(f"== 5 install ({MOD}): sheets changed {n}, animations {do_copies(anim)}, layouts {do_copies(lay)}, "
          f"objects {do_copies(obj)}, shadow masks {do_copies(shd)}")
    # Tiled shows the installed art (reopen the map in Tiled). Never with the build's consent: atlases you painted
    # need their own yes ("Refresh Tiled tiles" command or tiled_refresh_atlas.py --overwrite-saved)
    run("6 Tiled atlases", ["tiled_refresh_atlas.py"], consent=False)
    print("done: restart the game (or change maps) to see it; reopen maps in Tiled to see the refreshed tiles")


if __name__ == "__main__":
    main()
