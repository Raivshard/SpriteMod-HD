# Changelog

## v0.1.25.29 (2026-11-15)

### New

- **HD map tiles.** Put `map_tiles\<sheet>.hd.png` (e.g. `map_chip_147.hd.png`) beside `MenuLayout.json`. Any size with the game sheet's layout (2048x2048 = 4x tested). Shows on the main map, minimap, bird view and water. Saves apply live. Guide: `ART_GUIDE.md`, "Map tile sheets".
- **Per-map tile sheets.** 23 sheet names have different art in different maps. `map_tiles\map_NNNNN\<sheet>.hd.png` is used for that map only and wins over the shared file.
- **Map tile tools** (`source\tools\map_tiles\`): a catalogue of every tile image in the game (about 12,900 in use); replace a tile once and rebuild every sheet that uses it; import a whole HD sheet; compare tile by tile. Plus `map_tiles_hd.py` for whole-pixel enlargements and grid test sheets.
- **`battle.customScale`**: shows replaced party sprites drawn at the game's pixel density (e.g. 64 px sets) larger on screen, feet kept. Keep the files at their real size.
- **`battle.miniScale`** *(unverified, not yet tested in-game)*: under Mini, a party member keeps their normal sprites, shown smaller (default half size). No Mini art needed.
- **`fieldSprites`** *(experimental)*: HD frames for field (map) characters, which KupoUI cannot swap. Walk timing still follows the game. Off in the starter settings.
- `il2cpp_dump --grep <regex>`: find any game method or field by part of its name.

### Changed

- Weapons in battle follow the HD body's size.
- The KupoUI texture cache (`kupoTextureCache`) leaves battle character sprites to KupoUI.

### Tools added since the last pack

- `old_pc_dat.py`: convert image `.dat` files from the 2015 PC release and its mods to PNG.
- `cut_sheet_frames.py`: cut sprite sheets into single frames, pixels unchanged.

## v0.1.23.27 (2026-11-14)

First release pack.
