# FF1 Anniversary Edition (PSP): ordered reference

**Reference art only. Never ships in a release.** © SQUARE ENIX. Built by `tools\ff1psp_order.py` (in `FF5\SpriteMod`) from
`..\unpacked\`. Every picture is kept **once** (identical pictures merged); `index.csv` lists where each one came from.
Sorted by the game's container folder names; names are the game's file names.

## Folders

| Folder | What | From |
|---|---|---|
| `maps\<tile set>\<room>.png` | The world map (`MAP_00\MAP_00.png`, 255x255 tiles) and every room (inside view, no layers), rendered from its layout (32 px tiles, ground + overlay, first frame of animated water); `layout\` = the room files as stored | `tools\ff1psp_maps.py` |
| `maps\<tile set>\IN\` | What the game shows while you are **inside** (a building / room): inside view + IN layers (shadows, tree / cliff / wall tops; some bonus rooms also swap furniture). One file per layer set | same |
| `maps\<tile set>\OUT\` | What the game shows while you are **outside**: outside view (roofs closed, interiors hidden) + OUT layers | same |
| `maps\rooms.csv`, `maps\layer_sets.csv` | Rooms: tile set, place name (where known), size, animated tiles. Layer sets: which room layout and extra sheet each uses, and how that was matched | |
| `monsters\battle\MS_NN_<Name>` | Battle sprites with the game's English names | `MS_*` packs; name link below |
| `monsters\shadows\` | `_S` files: the shadow, shared by a species (all monsters using it are in `names.csv`) | same |
| `monsters\boss\BOSS_NN_<Name>` | Bonus-dungeon bosses (Echidna … Death Gaze) | `BOSS_*` |
| `monsters\names.csv` | Kept file, every game sprite merged into it, monster numbers, names | |
| `characters\party_field\` | **Party map sprites**: `PC10_Warrior` … `PC61_Black_Wizard` (class n, 0 = base, 1 = after class change); 32 px frames, 2 rows of 16 (walk in 4 directions, poses, KO, item, cheer) | `MEV_CMN` |
| **`frames\`** | **Every sprite assembled into its frames** (226 sprites, 1,953 frames): `<folder>\<sprite>\NNN.png` (anchor at the same point in every frame) and `<folder>\<sprite>.png` (all frames in a row). Party battle sprites: `frames\JOB_ALL\JOBnn_<Class>\NN_<pose>.png` (stand, ready, walk1-2, attack, chant1-2, arm_up, kneel, hit, ko; `JOBnn_S` = shadows). **`frames\battle_party\`** = all classes together, bodies only (the game and the plugin draw battle shadows), one folder per class, and `all_classes.png` (row = class, column = pose). Also NPCs, objects, vehicles, doors, party map sprites, story birds, battle icons. `frames.csv` = size and anchor per sprite | `tools\ff1psp_frames.py` |
| **`animations\`** | Every `.ANA` animation (spells, weapon swings, map and story effects) rendered frame by frame: `<folder>\<file>[_v<k>]\NNN.png` + `<file>.png` (up to 24 frames in a row); `_v<k>` = variants (e.g. colours). 538 animations, 65,126 frames, 3.2 GB; additive glows on black. `anim.csv` = tracks, frames, size. Not rebuilt by `ff1psp_order.py` (takes ~1.5 h): `python tools\ff1psp_anim.py <unpacked> <ordered>\animations --force` | `tools\ff1psp_anim.py` |
| `characters\battle\JOB_ALL\` | Party battle sprites, one sheet per class (`JOBnn`, `_S` = second sheet); `JOBMAGIC\` | `JOB_*`, `M_JOB_*`, `JOBMAGIC` (duplicates merged into `JOB_ALL`) |
| `characters\field\` | NPCs and objects as strips (`GUARD`, `FAIRY`, `DAIJIN` …), vehicles (airship, ship, canoe) | `MEV_*` |
| `world_map\objects\` | World-map towns and castles, clouds (`EXTRA_*`) | `MEV_*` |
| `tilesets\MAP_xx_yy\` | Map tile sheets (`MAP_*`, `0..3MAP_*` layers, `_DR` doors) | `MAP_*` |
| `backgrounds\battle\` | Battle backgrounds `BG_NN`, 512x312 | `BG_*` |
| `battle_weapons\` | Weapon pictures for attack animations | `WEAPON\*.ANA` (embedded) |
| `effects\battle\` | Spell and ability effects | `EFFECT01-03`, `MUEF_EFFECT`, `ICOMMON` (`.ANA`/`.EFA` embedded) |
| `effects\map\`, `effects\event\` | Field clouds, new-dungeon effects; airship, canal, Chaos, sea, observatory, crystal | `FIELD*`, `NEWDUNGEON`; `AIRSHIP`, `CANAL`, … |
| `art\gallery\` | Gallery art (`parts_*` = the pieces shown when zoomed) | `GARELLY*` |
| `art\screens\` | Map scenes as full 480x272 pictures `SSnnnn` | `SS*` |
| `art\story\`, `art\story_backgrounds\` | Opening, prologue, epilogue, ending, title logo; story backdrops | `OPDEMO`, … `STFL_BG*` |
| `text\event_fonts\J`, `J2`, `US` | The glyphs each event script uses (Japanese, Japanese 2, English) | `EVM*`, `J2EVM*`, `USEVM*` |
| `ui\menu_fonts\` | The glyphs each menu screen uses (`J1`/`J2`/`US`) | `FM_*`, `FONT_BATTLE*` |
| `ui\<screen>\` | Buttons, minimap, music player, mini game, icons | `COMMON`, `MINIMAP*`, … |

`problems.txt`: pictures that could not be read (0 in the last run).

## Reading `index.csv`

`kept file` | `md5` (pixels) | `copies` | `used in` (container folders) | `original paths` (`#n` = n-th picture inside an `.ANA`/`.EFA`).
A name followed by `__<folder>` means two different pictures had the same name; the folder is where the first copy came from.

## How the links are known

- **Monster names** [verified by eye on 29 sprites, and every species shares one shadow file]: names from the game text
  (`..\text_us\FONT_BATTLEUS\MONSTER_NAME.txt`, 203 names). `MS_00`-`7F` = monsters 0-127, `MS_80`-`B9` = monsters
  145-202, `BOSS_00`-`10` = monsters 128-144. The monster stat table is in the EBOOT at byte 1344640 (36 bytes each).
- **World map and rooms** [checked by eye: world map, 8 rooms across tile sets]: format in the header of
  `tools\ff1psp_maps.py`. Each tile = a ground sheet + an overlay sheet on top. Rooms: sheets 0+1 = inside view,
  2+3 = outside view. World map style sets: tiles 0-255 = sheets 0+1, 256-511 = sheets 2+3. `_ANM.ATI` places
  the animated frames. Black = nothing drawn there (dungeon void).
- **Inside / outside** [verified against 9 PPSSPP screenshots of Castle Cornelia and Cornelia, 2026-11-25]: inside
  rooms the game matches sheets 0+1 + IN layers, outside it matches sheets 2+3 + OUT layers (solid roofs). Doors are
  separate objects (`_DR`), and the moving cloud shadows in towns are a separate effect; neither is in these renders.
- **Forest on the world map** [seen in a PPSSPP shot]: standing on forest, the party's lower half is drawn under a
  translucent forest layer; the head is not, although the tile behind it is forest too, so it is a sprite effect, not
  layer order. Trigger [inferred]: the tile attribute in `MAP_00_ATT.BIN` (one u16 per tile); all forest tiles, edges
  included, have `0x6`.
- **IN / OUT layers** [decoded; checked by eye on 4 rooms and by pixel match]: format in the header of
  `tools\ff1psp_maps.py`. 327 of 372 layer sets match their room by name; the rest by size plus pixel match or
  name ending (`matched by` in `layer_sets.csv`; "[inferred]" = 7 sets with nothing to compare).
- `problems.txt` lists 3 layer sets whose tiles go past their extra sheet (`MAP_11_00` OUT, `MAP_18_00` IN,
  1 tile of `MAP_EX03_R22` IN); no sheet in the game fits them, so those tiles are left out.
- **Party map sprites** [checked by eye].
- Category of the other `MEV_*` pictures is by folder [inferred]; checked by eye on a few files per folder.
- **Sprite frames** [decoded; checked by eye: 2 battle classes in full, airship, ship, fairy, party map sprite,
  birds, door]: piece lists in `.OTI`, format in the header of `tools\ff1psp_frames.py`. Battle class order
  `JOB00`-`0B` = Warrior, Thief, Monk, Red Mage, White Mage, Black Mage, then the promoted six. Pose names are
  by look.

## Not done yet

- The sea of the bonus field `MAP_EX03_R54` is black: its tile is empty on every sheet and not animated; what the
  game draws there is not decoded.
- Room names: only Castle Cornelia (`MAP_0A_00_00`, `_01`) and Cornelia (`MAP_0A_01_00`) are named so far
  (`PLACES` in `tools\ff1psp_maps.py`); more come from matching screenshots.
- Where NPCs stand in each room (event data).
- `.EFA` (19 files) = effect textures, then particle settings [inferred: emitter data]. The textures are extracted (`<file>_efa_<n>.png` in `effects\`); the particle settings after them are not decoded.
- Animation renders are a reference approximation: blend modes, rotation direction and timing are inferred; not
  compared with the game.
- Per-frame anchor points (`.AOB`, `_AOB.BIN`) are not decoded; frames are drawn without them.
