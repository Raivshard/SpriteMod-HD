# Changelog

## v0.1.35.36 (2026-11-25)

### Changed

- **Map tools find your games:** the tools look in every Steam library, so `games.json` usually needs no edit; `steam` may also be the game folder itself, and a path typed with single backslashes works. The export folder follows Magicite's `Export Directory` setting. A missing game or export stops the tools with a message that says what is missing and how to fix it (including how to make the Magicite export, now also in the map guide). `python tools\games.py` prints what was found.
- **Requirements:** Magicite is listed as required for the map tools (they read its export); it is still not needed to play with the mod.

## v0.1.35.35 (2026-11-24)

### New

- **Other Pixel Remaster games:** should run in all PR games. Originally built on FF5 PR, so some bugs may reflect that. The plugin reads its files from `Modules\00-Mods\SpriteMod HD\<game>\` (e.g. `FF6`). HD map tiles and map layouts are tested in-game in FF1, FF5 and FF6.
- **Map tiles for modders** (experimental; `source\tools\map_tiles\README.md`): make your own map tiles for any PR game in Tiled. `new_project.py` sets up a project for the maps you name; Tiled commands check, import, build and install it. You can replace a tile everywhere, give a few cells their own tile, move, copy, erase, flip or rotate cells, restyle animation frames and doors, and place tile objects anywhere. The import warns when you replace an animated tile, which would not show. Keep backups of your work. If the documentation is unclear or a tool fails, please tell the author (GitHub Issues or the Nexus page).
- **Edited map layouts:** `GameAssets\Serial\Res\Map\...\tilemap.json` beside MenuLayout.json replaces a map's tile cells. Walking, events and colours stay the game's.
- **Map objects:** `map_objects\<Map>.json` draws tiles placed freely in Tiled, with their Tiled animation, blend mode and opacity, sorted with the characters.
- **Map shadows** (`fieldMapShadows`, on by default): a field map can have a shadow mask `map_shadows\<Map>.png` beside MenuLayout.json (black, alpha = shadow, 32 px per map cell, top row = map row 0). It is drawn over the floor and under characters, aligned to the map's cells. No masks ship with the mod. You paint the mask yourself (map guide, section "Shadows, lava and fire").
- **Lava** (tested in FF5 Karnak): `fieldLavaSpeed` and `fieldLavaWaterBlend` tune the game's lava surface; a lava layer (`map_shadows\<Map>.lava.json`) draws animated lava frames under it, with `fieldLavaOpacity`, `fieldLavaBlend` (Photoshop-style blend modes and Blend If ranges), `fieldLavaEdge` (a lighter top edge) and `fieldLavaAnimSpeed`.
- **Flame frames** (`fieldFlames`, deprecated): `effects\<effect>\flame_*.png` replaces the fire of one map fire effect (torches, fireplaces), keeping the game's sparks and glow. Each effect needs its own files, set up case by case; it does not replace fire effects automatically. Use map objects with a Tiled animation and a blend mode instead. `flame.json` sets speed, offset, scale, hides the game's fire, or sets the glow's blend mode. `fieldEffectResolution` keeps the frames sharp.
- **Tile animations:** `map_tiles\anim\anim.json` animates single tiles of an HD sheet.
- `MenuLayout_README.md`: every setting is now documented, including the diagnostic ones, with a new "Map files" section.

### Changed

- README requirements: the BepInEx version is corrected to 6.0.0-pre.2 IL2CPP (Unity IL2CPP, Windows x64, linked). The UnityPlayer CRC check Bypass is listed as a prerequisite.
- Shorter log: the per-map detail lines (map tiles, layouts, shadows, lava, flames) are written only when `LogLevels` includes `Info`.

### Fixed

- **`fieldCeilingSigns`**: the listed signs showed inside buildings without a ceiling layer (e.g. Karnak's pub and inn) and moved with the camera. The game switches the town's ceiling off there but keeps it; the signs now follow it.
- **Map tile catalogue** (`map_tile_catalog.py`): map tile numbers were read top-down, but the game's map files count sheet rows from the bottom. Usage (cells placed, used or unused, layers, animation frames, collision) went to the wrong tiles. Rebuild your catalogue with `--force` (`labels.csv` is kept). Replacements and `map_tiles_pack.py build` were not affected.
- **Map tile catalogue:** now finds every sheet name (FF1 `Map_NNNNN`, FF6 `mapNNN`, FF5 `map_chip_NNN`) and skips sheets that are not 512x512 px (4 FF1 sheets) with a message.

### Known issues

- An NPC can be drawn over a `fieldCeilingSigns` sign for a moment while walking past it (low priority).

## v0.1.29.33 (2026-11-16)

### New

- **`fieldScale`**: one size factor for every custom field (map) character, party and NPCs (`1.4` recommended; the starter settings use it). Live; the size files stay at the game-matched value.
- **`fieldEffectsUnderCharacters`** (default on): map effects such as fireplace fire no longer cover a taller character standing in front of them. Characters behind the effect are unchanged.
- **`fieldCeilingSigns`**: lists ceiling-layer tiles that should be drawn like wall tiles. The game put two shop signs in Karnak in the ceiling layer, so they covered taller heads. Listed tiles now cover only characters in their own cell or behind it; overhangs and eaves stay on top. Per tile sheet: top-left pixel of each 16 px tile in the exported sheet PNG. Works with HD tile sheets. Format: `MenuLayout_README.md`.
- **`fastSaveList`** (default on): the Load Game / Save list opens at once. The game decodes every slot's screenshot (full screen size) before showing the list; the plugin decodes them in the background and they appear a moment later.

### Changed

- F8 dump: field sprites show `on=yes/no`, material, shader, render queue and depth settings; cameras show their target and which layers they draw.
- `profileGame`: slow PNG decodes name the code that called them.

### Known issues

- An NPC can be drawn over a `fieldCeilingSigns` sign for a moment while walking past it (low priority).

## v0.1.26.30 (2026-11-15)

### Fixed

- `battle.depthScale`: a party member no longer goes back to full size after an ability that moves them (Kick and similar).

## v0.1.26.29 (2026-11-15)

### New

- **HD map tiles.** Put `map_tiles\<sheet>.hd.png` (e.g. `map_chip_147.hd.png`) beside `MenuLayout.json`. Any size with the game sheet's layout (2048x2048 = 4x tested). Shows on the main map, minimap, bird view and water. Saves apply live. Guide: `ART_GUIDE.md`, "Map tile sheets".
- **Per-map tile sheets.** 23 sheet names have different art in different maps. `map_tiles\map_NNNNN\<sheet>.hd.png` is used for that map only and wins over the shared file.
- **Map tile tools** (`source\tools\map_tiles\`): a catalogue of every tile image in the game (about 12,900 in use); replace a tile once and rebuild every sheet that uses it; import a whole HD sheet; compare tile by tile. Plus `map_tiles_hd.py` for whole-pixel enlargements and grid test sheets.
- **`battle.customScale`**: shows replaced party sprites drawn at the game's pixel density (e.g. 64 px sets) larger on screen, feet kept. Keep the files at their real size.
- **`battle.miniScale`** *(unverified, not yet tested in-game)*: under Mini, a party member keeps their normal sprites, shown smaller (default half size). No Mini art needed.
- **`fieldSprites`**: HD frames for field (map) characters, which KupoUI cannot swap: walking, standing, a second stride per direction. On in the starter settings; without field frames the game's own stay.
- **`fieldStrideDelay`**: longer walk frames per direction, `[first, middle, last]` = stride, passing pose, other stride. Each frame keeps the game's time plus its own; each tap starts on the other stride.
- `il2cpp_dump --grep <regex>`: find any game method or field by part of its name.

### Changed

- Weapons in battle follow the HD body's size.
- The KupoUI texture cache (`kupoTextureCache`) leaves battle character sprites to KupoUI.

### Tools added since the last pack

- `old_pc_dat.py`: convert image `.dat` files from the 2015 PC release and its mods to PNG.
- `cut_sheet_frames.py`: cut sprite sheets into single frames, pixels unchanged.

## v0.1.23.27 (2026-11-14)

First release pack.
