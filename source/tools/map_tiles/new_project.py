r"""Start a map project for any Pixel Remaster game: a folder with project.json, a folder for your own tiles, the
maps as Tiled working maps and a Tiled project with the commands (check, import, build and install).
  1. the game's tile catalogue, if missing (map_tile_catalog.py: data\catalogs\<game>, ~1 min once per game)
  2. <folder>\project.json   game, name, maps, scale 2 (32 px tiles)
  3. <folder>\my tiles\      your tiles (32 px PNG, drawn upright); each appears in Tiled as tileset "added: <name>"
  4. <folder>\tiled\<Map>.tmj per map (tiled_export.py: the game's tiles, tileset "build (current)"; tiled_frames.py:
     hidden "animation frames" layers where the map has animated tiles)
  5. <folder>\tiled\<name>.tiled-project with the commands; open it in Tiled
Refuses if <folder>\project.json exists (add maps later: tiled_export.py --project <folder> <Map> and add the map to
"maps" in project.json). Map names: press F8 in the game at that place; the dump in SpriteMod HD\<game>\_dumps lists
Map_NNNNN or Map_NNNNN_n (an interior: the sub-map), as does the game's MagiciteExport (map_NNNNN folders).
Usage: python new_project.py --game FF6 <folder> <Map_...> [...] [--name <name>]
"""
import json, subprocess, sys
from pathlib import Path
sys.path.insert(0, str(__import__('pathlib').Path(__file__).resolve().parents[1]))  # tools folder
import paths   # project folder layout (tools\paths.py)

HERE = Path(__file__).resolve().parent
PROJECT = HERE.parents[1]


def main():
    a = sys.argv[1:]
    game = a[a.index("--game") + 1].upper() if "--game" in a else None
    name = a[a.index("--name") + 1] if "--name" in a else None
    skip = {a[i + 1] for i, x in enumerate(a) if x in ("--game", "--name") and i + 1 < len(a)}
    rest = [x for x in a if not x.startswith("--") and x not in skip]
    if not game or len(rest) < 2:
        sys.exit(__doc__)
    folder, maps = Path(rest[0]).resolve(), rest[1:]
    if (folder / "project.json").exists():
        sys.exit(f"{folder} already has a project.json")
    sys.path.insert(0, str(HERE.parent))
    import games
    g = games.Game(game)
    if not g.export.exists():
        sys.exit(f"{game}: no MagiciteExport at {g.export} (export the map packages with Magicite first)")
    from PIL import Image
    for m in maps:
        tm = next(g.export.glob(f"map_*/Assets/GameAssets/Serial/Res/Map/**/{m}/tilemap.json"), None)
        if not tm:
            sys.exit(f"{m}: no such map in the {game} export")
        for ts in json.loads(tm.read_text(encoding="utf-8"))["tilesets"]:
            png = tm.parent.parent / Path(ts["image"]).name
            if png.exists() and Image.open(png).size != (512, 512):
                sys.exit(f"{m}: its tile sheet {png.name} is {Image.open(png).size[0]}x{Image.open(png).size[1]} px; "
                         "only 512x512 sheets are supported yet")
    py = [sys.executable, "-W", "ignore"]
    cat = paths.catalog(game)
    if not (cat / "sheets.csv").exists():
        print(f"1. tile catalogue for {game} (once per game) -> {cat}", flush=True)
        subprocess.run(py + [HERE / "map_tile_catalog.py", "--game", game, "--out", cat], check=True)
    folder.mkdir(parents=True, exist_ok=True)
    (folder / "my tiles").mkdir(exist_ok=True)
    name = name or folder.name
    (folder / "project.json").write_text(json.dumps({"game": game, "name": name, "scale": 2, "maps": maps}, indent=2) + "\n",
                                         encoding="utf-8")
    print(f"2. {folder / 'project.json'}")
    print(f"3. {folder / 'my tiles'}  (your 32 px tiles)")
    print("4. Tiled working maps:", flush=True)
    r = subprocess.run(py + [HERE / "tiled_export.py", "--project", folder] + maps)
    if r.returncode:
        sys.exit("export failed (see above)")
    # animated tiles (doors, torches, water): one hidden "<Layer> frame N" layer per extra frame, to restyle frames
    r = subprocess.run(py + [HERE / "tiled_frames.py", "--project", folder] + maps)
    if r.returncode:
        sys.exit("animation frame layers failed (see above)")
    tool = lambda args: {"command": sys.executable, "workingDirectory": str(PROJECT), "enabled": True,
                         "saveBeforeExecute": True, "showOutput": True, "arguments": args}
    cmds = [dict(tool("tools\\map_tiles\\tiled_import.py %mapfile --dry"), name="Check my edits (dry run, writes nothing)", shortcut="Ctrl+Shift+P"),
            dict(tool("tools\\map_tiles\\tiled_import.py %mapfile"), name="Import this map (writes choices, layout, objects)", shortcut="Ctrl+Shift+I"),
            dict(tool("tools\\map_tiles\\build_install.py %mapfile"), name="Build and install (close the game first)", shortcut="Ctrl+Shift+B"),
            dict(tool("tools\\map_tiles\\tiled_add_tiles.py %mapfile --overwrite-saved"), name="Add my new tiles to this map (changes this map file; then reload)", shortcut="Ctrl+Shift+T"),
            dict(tool(f"tools\\map_tiles\\tiled_refresh_atlas.py --project \"{folder}\""), name="Refresh Tiled tiles from the installed art", shortcut=None)]
    tp = folder / "tiled" / f"{name}.tiled-project"
    sys.path.insert(0, str(HERE))
    import guard   # your saved files are never changed without consent
    guard.write_text(tp, json.dumps({"automappingRulesFile": "", "commands": cmds, "compatibilityVersion": 1100,
                              "extensionsPath": "extensions", "folders": ["."], "properties": [], "propertyTypes": []}, indent=4))
    print(f"5. {tp}: open it in Tiled (File > Open File or Project)")


if __name__ == "__main__":
    main()
