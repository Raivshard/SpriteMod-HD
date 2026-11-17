# Changelog

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
