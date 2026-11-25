# Making your own map tiles (any Pixel Remaster game)

Replace or add map tiles in FF1–FF6 Pixel Remaster with your own art: you paint the tiles, place them in
[Tiled](https://www.mapeditor.org), and one command builds them and installs them into the game.

**Experimental:** keep backups of your work. If the documentation is unclear or a tool fails, please tell the author ([GitHub Issues](https://github.com/Raivshard/SpriteMod-HD/issues) or the Nexus page).

| Status | |
|---|---|
| FF5 | Works in-game (the Karnak town and interiors are built this way). |
| FF6 | Works in-game (2026-11-24, Narshe Map_20010: a replaced tile and a changed layout). |
| FF1 | Works in-game (2026-11-24, Cornelia town Map_20020: a replaced tile, every pixel of it shown, windowed and fullscreen). |
| FF1 | The maps on 4 sheets are not supported yet: Map_10010, Map_30081 and Map_30111 (1024x512 px), and Map_20130 (512x1024 px). `new_project.py` refuses them with a message. |

## What you need

| Need | Where |
|---|---|
| Tiled 1.10 or newer | mapeditor.org |
| Python 3 with Pillow | `python -m pip install pillow` |
| The game with BepInEx, Magicite and this mod's plugin | the mod's install notes |
| The game's exported map files | Magicite export of the map packages: `MagiciteExport` in the game's `StreamingAssets` |
| Where your games are | `tools\games.json`, `steam` = the folder that holds the game folders |

Run every command from the folder that holds `tools\` (in the mod's download: `source\`). Inside it, your
projects go in `Working\tilesets\<name>\`, the game's tile catalogue in `data\catalogs\<game>\`, and the tools' own
records in `build\tool records\`.

## 1. Find the map

- In the game, stand on the map and press **F8**.
- Open the newest file in `Modules\00-Mods\SpriteMod HD\<game>\_dumps\`. Its lines name the map, e.g. `Map_20010`.
- An interior has its own sub-map, e.g. `Map_20061_2`. Use that name.

## 2. Start a project

```
python tools\map_tiles\new_project.py --game FF6 Working\tilesets\narshe Map_20010
```

- This takes about a minute the first time for a game, because it builds the game's tile catalogue first.
- You can list several maps. Maps that share a sheet, like a town and its houses, belong in one project.

| Made | What it is |
|---|---|
| `project.json` | game, name, maps, tile size (`scale` 2 = 32 px tiles) |
| `my tiles\` | your tiles |
| `tiled\<Map>.tmj` | the maps for Tiled, drawn with the game's tiles |
| `tiled\<name>.tiled-project` | open this in Tiled (File > Open File or Project). It has the commands below. |

Tiled commands: before the first use, check them in File > Commands > Edit Commands:
- `Executable` must be your `python.exe`.
- `Working Directory` must be the project folder.

## 3. Paint a tile

| Rule | Why |
|---|---|
| 32 x 32 px PNG (16 x `scale`). Larger files are scaled down. | the tile size of the build and of Tiled |
| Draw it **upright**, as it looks in the game | the tools turn it the way the game stores it |
| Transparent where the tile shows what is below | tiles on `Upper` and `Ceil` are drawn over the characters |
| Start from the game's tile: `tiled\atlas\<Map>_game.png` | every tile of the map, upright, 32 px |
| Or paint straight into `tiled\atlas\<Map>_build.png` (or `_frames.png`) | what Tiled shows goes into the game: every cell of the map showing that tile gets your art; builds never paint over it |

- Save the tile into `my tiles\`.
- Then, in Tiled: **Add my new tiles to this map** (Ctrl+Shift+T), and reload the map when Tiled asks.
- The tile appears as tile set `added: <file name>`.

## 4. Place it

1. Pick a layer: `Bottom`, `Ground`, `Upper` or `Ceil`. The hidden group `game (reference)` shows the original map and is never read.
2. Stamp Brush (B): pick a tile, click cells. X / Y / Z flip and rotate the tile, and the flips are kept.
3. Save (Ctrl+S).

| What you do | What the game shows |
|---|---|
| Stamp your tile on **every** cell that shows one game tile | that game tile is replaced, on every map that uses the sheet |
| Stamp it on only some cells | your tile gets its own empty slot on the sheet; only those cells show it |
| Move, copy, erase, flip or rotate the map's own tiles | exactly that, cell by cell |

- Which game tile is under a cell: right-click the cell with the Stamp Brush, then look in the Properties panel. It shows `tile` (sheet:number) and `cells` (how many cells of this map use it).
- Select Same Tile (S) shows them all.

## 5. Check, import, build

| Tiled command | What it does |
|---|---|
| **Check my edits** (Ctrl+Shift+P) | Lists what the import would do. It writes nothing. |
| **Import this map** (Ctrl+Shift+I) | Saves your edits to the project: `choices.csv` (tile art) and `layouts\` (cells). |
| **Build and install** (Ctrl+Shift+B) | **Close the game first.** Builds the sheets and installs them. |
| **Refresh Tiled tiles** | Shows the installed art in Tiled. |

- Then start the game or change maps to see your tiles.
- Command output: View > Views and Toolbars > Console.

**AI assistants can't change your files behind your back.** Commands you run (Tiled, your own terminal) always run. When an AI coding assistant (Claude Code) runs these tools, they stop before changing any file you saved or edited (a map, a painted atlas, a tile, an installed file) and print `STOPPED: NEEDS YOUR CONSENT` with the files, so the assistant has to ask you. The tools remember what they wrote in `build\tool records\written_files.json`.

## What gets installed (to share your work)

Everything goes into `Modules\00-Mods\SpriteMod HD\<game>\` in the game folder:

| Folder | Content |
|---|---|
| `map_tiles\<sheet>.hd.png` | the built sheets, 32 px tiles |
| `GameAssets\Serial\Res\Map\...\tilemap.json` | maps whose cells you changed |
| `map_objects\` | tile objects (Tiled object layers), if any |

Players need the mod's plugin and these files.

## Limits

- **Walking:** walking is not changed. A wall drawn on floor can be walked through.
- **Animation:** animated game tiles (water, torches, gears) keep animating where they stay. A flipped or restamped copy is a still image.
- **Animated tiles:** replacing an animated tile on every cell does nothing visible, because the game shows its frames. A tile can look empty on the sheet and still animate. The import warns about this. Change the frames instead (Animation frames, below).
- **Sheet size:** one tile sheet per map, 512x512 px (all FF5 and FF6 maps; FF1 except the 4 sheets named above).
- **Several projects:** projects of one game install into the same folder. Two projects that both change one sheet overwrite each other.

## Animation frames, doors, objects

| What | How |
|---|---|
| **Animation frames** | Maps with animated tiles (doors, torches, water) get a hidden group `animation frames`: one layer per extra frame, e.g. `Ground frame 2`. Stamp there to change that frame. It keeps the game's tile number, because the animation needs it. Tested in FF5. |
| **Doors** | A game door is an animated tile: frame 1 closed, frame 2 open. Draw the closed door on a layer and the open door in that layer's `frame 2` layer, same cells. The import keeps the game's door tile, so it still opens. |
| **Objects** | Any object layer (Insert Tile, T) is drawn by the plugin at the exact position, also off the grid. Tiled tile animations (Tile Animation Editor) play in the game. The layer's blend mode and opacity are used. An object layer above `Ground` is drawn over the ground and behind `Upper`, sorted with the characters by its bottom edge. Hidden layers and objects are left out. Tested in FF5. |

## Shadows, lava and fire (made by hand)

The tools don't make these files; you paint or write them yourself. Formats and settings: `MenuLayout_README.md`, section "Map files".

| What | How |
|---|---|
| **Map shadow** | `map_shadows\<Map>.png`: black, alpha = shadow, 32 px per map cell (a 18x20-cell map = 576x640 px), top row = map row 0. Leave it empty wherever an object, `Upper` or `Ceil` tile covers the floor: the shadow is drawn over the whole floor, under the characters only. For a base image of the same size, open the map in Tiled and use **File > Export As Image**. Optional `<Map>.decor.png`: the same shadow on see-through floor tiles (stools, rug edges), which the game draws after the main shadow. Tested in FF5. |
| **Lava layer** | `map_shadows\<Map>.lava.json` lists the lava cells and an animated frame strip. Tested in FF5 (Karnak). |
| **Fire** | Use an object layer: a tile object with a Tiled animation and a blend mode (Animation frames, doors, objects, above). The older `effects\<effect>\flame_*.png` files still work but are deprecated: each effect is set up case by case. |

## Scripts

All in `tools\map_tiles\`. The Tiled commands run them for you; the terminal works too.

| Script | What it does |
|---|---|
| `new_project.py --game <game> <folder> <Map> [...]` | starts a project (section 2) |
| `tiled_import.py <map>.tmj [--dry]` | reads your edits; `--dry` only lists them. Import maps that share a sheet in one run, so new tiles never take the same empty slot twice. |
| `build_install.py <map>.tmj [--no-install]` | builds the sheets and installs them; refuses while the game runs |
| `tiled_add_tiles.py <map>.tmj` | adds new files in `my tiles\` to a map |
| `tiled_export.py --project <folder> <Map>` | adds a map to a project (also add it to `maps` in `project.json`); `tiled_frames.py --project <folder> <Map>` then adds its frame layers |
| `tiled_refresh_atlas.py --project <folder>` | Tiled's `build (current)` tiles = the installed art |
| `map_tile_catalog.py --game <game>` | the game's tile catalogue (`new_project.py` runs it once per game; again after a game update with `--force`) |
| `map_tiles_pack.py`, `write_choices.py`, `layouts.py`, `project.py`, `tiled_objects.py`, `atlas_record.py`, `guard.py` | used by the scripts above |
