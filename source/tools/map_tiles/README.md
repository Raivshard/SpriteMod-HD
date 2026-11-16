# Map tiles: catalogue, replacements, pack

HD map tiles as **items**: one item = one 16 px tile image of the game. The same image in 40 sheets is one item, so you replace it once (user, 2026-11-15: shared items + per-sheet overrides).

## Folders

| Folder | What | Who writes it |
|---|---|---|
| `catalog\` | `items.csv`, `sheets.csv`, `sheet_tiles.csv`, `items_16\` (each item at 16 px), `labels.csv` | `map_tile_catalog.py`; `labels.csv` is yours (never overwritten) |
| `replacements\<item>.png` | HD art for one item, used in every sheet that has it | you, or `map_tiles_pack.py import` |
| `overrides\<sheet>\<tile id>.png` | HD art for one tile of one sheet only; wins over the item | you, or `import` when an item already has other art |
| `pack\` | built sheets to copy into the game's `map_tiles\` | `map_tiles_pack.py build` |
| `source\` | HD sheets to import (e.g. `map_chip_147_user_2048.png`) | you |
| `test_grid\` | grid test sheets (`tools\map_tiles_hd.py --grid`) | tests only |

## Steps

Needs Python 3 with Pillow and the game's Magicite export (`MagiciteExport` in the game's `StreamingAssets`). Folders: `--root <folder>` (the folder holding `catalog\`, `replacements\`, `overrides\`, `pack\`; default `data\map_tiles` two levels above the tools), `--export <MagiciteExport folder>` if the game is not in the default Steam folder (catalogue: `--out <root>\catalog`).

| Step | Command |
|---|---|
| 1. Catalogue (once; again after a game update) | `python tools\map_tiles\map_tile_catalog.py [--force]` |
| 2a. Import a whole HD sheet | `python tools\map_tiles\map_tiles_pack.py import <hd sheet.png> <sheet>` |
| 2b. Or drop single tiles | `replacements\<item>.png`, 64x64 at the default 4x |
| 3. Build | `python tools\map_tiles\map_tiles_pack.py build [--scale 4] [--sheets a,b] [--all] [--force]` |
| 4. Check (optional) | `python tools\map_tiles\map_tiles_pack.py compare <hd sheet.png> <sheet>` |
| 5. Install | copy `pack\*` into `<game>\Modules\00-Mods\SpriteMod HD\FF5\map_tiles\` |

## Names

- **Item:** `t_` + 12 hex digits of the md5 of the tile's 16x16 RGBA pixels, e.g. `t_352856fcab93`. `items.csv` lists every sheet and tile id that has it, how often maps place it, on which layers (B/G/U/C), whether it is an animation frame, and the collision values seen under it.
- **Sheet:** the game's name (`map_chip_147`). 23 names have different art in different map packages: those are `<name>@<package>` (e.g. `map_chip_063@map_30090`) and build to `pack\<package>\<name>.hd.png`. The plugin reads `map_tiles\<package>\<name>.hd.png` before `map_tiles\<name>.hd.png`.
- **Tile id:** row x 32 + column on the 512x512 sheet (32 x 32 tiles of 16 px).

## Numbers (2026-11-15)

439 sheet versions, 211,320 tile cells, 31,648 items, **12,875 used** on maps or as animation frames, 363,359 placements.

## Good to know

- Shared means shared: importing Karnak's sheet (`map_chip_147`) also changed tiles in about 160 other sheets that use the same images (e.g. 464 of 682 tiles in `map_chip_051`). Art made in one place's context shows up everywhere; use overrides where that looks wrong.
- Upscaling a whole sheet at once can blend neighbouring tiles, which sit next to unrelated tiles on the sheet.
- Tiles keep the game's layout and transparency: a replacement fills the whole cell, alpha included.
- Walkability is per map cell (`collision.json`), not per tile: new art never changes where you can walk.
