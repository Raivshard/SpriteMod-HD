# HD art: sizes, names and folders

How to add HD art that the plugin sizes and places correctly. All paths below are inside
`Modules\00-Mods\SpriteMod HD\FF5\` (the folder with `MenuLayout.json`).

> **How art gets in:** KupoUI loads every PNG under `Modules\` and swaps it for the game's image **of the same name**. The plugin then fixes size, sharpness and placement.
> KupoUI reads its folders **at game start**: restart after adding new files. Edits to files the plugin reads (`_place.json`, `.anchors.json`, `.depth.png`, loading frames, `image` swaps) apply live.

## The one size rule

**Draw art at the size you want it on a 2560x1440 screen.** With the default `battle.hdPixelsPerUnit` of `6.667`, a 248 px tall image shows 248 px tall at 1440p (186 px at 1080p).
To make all HD battle art larger or smaller at once, change `hdPixelsPerUnit` (lower = larger).

Only images much larger than the game's (more than 2x) are resized, so poses you haven't made yet keep showing the original art.

## Battle characters (also used in the menus)

| Item | Details |
|---|---|
| Folder | `GameAssets\Serial\Res\Chara\Battle\BC_FF5_P001_J00\` — `P001` = character, `J00` = job, as in the game's own asset paths |
| File | The game's frame name: `Default_00.png` (standing), other poses by their game names (e.g. `Win_00`, `RightAttack_01`). The F8 dump and KupoUI's log show the names in use |
| Size file | `Default_00.json` beside it: `{ "pixelsPerUnit": 6.667 }` |
| Canvas | Square, transparent. Feet on the bottom edge, figure centred. 248x248 is a good default |
| Menus | The menu shows the same `Default_00`. Size per screen: `screens` in `MenuLayout.json` |

- **At least two characters must have a file of the same name** (e.g. two `Default_00.png`). With only one, KupoUI matches by name alone and puts it on every character.
- A pose without an HD file shows the small original art. Most visible: `RightAttack_01` flickers in for about a second at battle start.

## Enemies

| Item | Details |
|---|---|
| Folder | `enemies\MN_FF5_033\` (the enemy's id) |
| File | The game's texture name, e.g. `MN_FF5_033_C00.png` (`_C00`, `_C01`… = colour variants) |
| Size | Same rule: drawn at its 1440p size. Width and height are scaled separately; feet and centre stay where the game puts them |
| Draw order | `battle.enemyOnTop` lists enemies drawn in front where they overlap |

**Ground points (`.anchors.json`).** The first time an enemy appears, the plugin writes `enemies\MN_FF5_033\MN_FF5_033_C00.anchors.json` with guessed ground-contact points. It is never overwritten. Edit it if an enemy's shadow starts at the wrong height:

```json
{ "texture": "MN_FF5_033_C00", "size": [32, 46],
  "points": [ { "on": true, "x": 9, "y": 41 }, { "on": true, "x": 17, "y": 45 }, { "on": false, "x": 0, "y": 0 } ] }
```

`x`, `y` = pixel from the top-left as in an image editor; the bottom of each `on` pixel touches the ground. Up to 8 points. `size` = the image size the points were taken on (they are scaled to the HD image).

## Depth maps (better ground shadows)

Without a depth map, a sprite's shadow is built from its outline (a rounded shape). With one, arms, legs and capes cast their own shadows in the right place.

| Item | Details |
|---|---|
| File | `<sprite>.depth.png` beside the sprite: `Default_00.depth.png`, `MN_FF5_033_C00.depth.png` |
| Size | Exactly the sprite's size |
| Content | Greyscale, **bright = near** the camera, **black background** outside the figure |

**Recipe (ComfyUI, Depth Anything V2):**

1. **Load Image**: the sprite PNG (with transparency).
2. **Depth Anything V2 Relative**: model `vitl`, resolution `512`, input = the image.
3. **InvertMask** on Load Image's `MASK` output (ComfyUI's mask is the inverse of the alpha), then **GrowMask** by `-1` (trims the edge).
4. **ImageCompositeMasked**: destination = an **EmptyImage** of the sprite's size, black; source = the depth image; mask = step 3; `resize_source` = **true** (the depth comes out at 512 px and must be scaled to the sprite).
5. Save, rename to `<sprite>.depth.png`, put it beside the sprite.

One sprite size per batch (the EmptyImage size must match). Any other depth tool works if the result follows the table above.

Check the result in battle with `battle.groundShadow.debug: true` (bodies show grey by depth). The log line `[GpuShadow] ground fit` says per sprite whether both soles were found.

## Battle backgrounds

| Item | Details |
|---|---|
| Folder / file | `battle_backgrounds\BG_FF5_07.png` (the game's name) |
| Size | Any size above 512x256 with the same 2:1 shape; 2048x1024 recommended |
| Framing | The game's art sits in a black frame (painted area 472x232 of 512x256). Full-bleed HD art is fine; keep important content in the middle 75 % (what a 16:9 screen shows) |
| Sharpness | Needs `renderHeights` `CameraBattlebackGround` 1707 (pack default) and `textureLimit` 0 if the game draws it at half size |

**Animated parts** (torches, water, lights in backgrounds 05, 08, 12, 18, 20, 22, 23, 26): HD frames go in `battle_backgrounds\animtex\`, placed by a `_place.json`. Guide: `battle_backgrounds\animtex\README.md`. Tool: `source\tools\animtex_fit.py` finds where each frame sits on your HD background and writes the `_place.json`. Background 31 (scrolling texture) is not supported yet.

## Map tile sheets (towns, dungeons, world map)

| Item | Details |
|---|---|
| Folder / file | `map_tiles\map_chip_147.hd.png` (the game's sheet name + `.hd.png`) |
| Size | Same layout as the game's 512x512 sheet (32 x 32 tiles of 16 px), any size with the same shape; whole multiples keep tile edges on pixels (1024 = 2x, 2048 = 4x) |
| Why `.hd.png` | KupoUI loads any `<sheet>.png` under `Modules` by name into the game's sheet, which breaks larger sheets. The plugin keeps the game's sheet and swaps the HD file in on every map view (main, minimap, bird view, water) |
| Which sheet | Each map's `tilemap.json` names its sheet (`"image": "map_chip_147.png"`); the game's export has them under `map_NNNNN\...\Res\Map\Map_NNNNN\` |
| Same name, other art | 23 sheet names have different art in different maps (e.g. `map_chip_063`). Put those in `map_tiles\map_NNNNN\<sheet>.hd.png` (the map package); it wins over the shared file |
| Live | Saving the file updates the map within a second. Log lines start with `[MapTiles]` |

Keep each tile inside its own 16 px cell (scaled). Upscaling a whole sheet at once can blend neighbouring tiles, which sit next to unrelated tiles on the sheet.

**Tile by tile:** `source\tools\map_tiles\map_tile_catalog.py` lists every tile image of the game once (about 21,000 are used on maps) with where it is used. Replace a tile once and `map_tiles_pack.py build` puts it into every sheet that has it; `import` cuts a whole HD sheet into tiles; overrides cover one tile of one sheet. Tool: `source\tools\map_tiles_hd.py` (whole-pixel enlargement; `--grid` draws test lines on tile edges).

## Loading animation

PNG frames in `loading_icon\`, named in play order (`00.png`, `01.png`…), square, any size. Speed and size: `loadingIcon` in `MenuLayout.json`. Applies within 2 s.

## Replacing a menu image

Any UI image can show your PNG instead: an `elements` rule with `"image": "Images/my_cursor.png"` (path from `MenuLayout.json`'s folder). The PNG is stretched to the object's box; size it with `width` / `height` / `scale`. Reloads when the file changes.

## Finding names

Press **F8** in-game: `_dumps\screen_<time>.tsv` lists every object on screen with its path, sprite and texture names. Battle sprites show their texture name (`MN_FF5_033_C00`, `Default_00`).
