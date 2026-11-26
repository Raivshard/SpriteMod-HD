# FF1 PSP extraction tools

Python tools that unpack **FINAL FANTASY Anniversary Edition for PSP** (US, ULUS10251) and turn its files into
pictures you can use:
- every texture as PNG
- the world map and every room
- every sprite as frames
- every effect animation frame by frame
- the game text as plain text

They are meant as reference art for modding **FINAL FANTASY Pixel Remaster**. One tool builds a KupoUI pack that
puts the PSP party battle sprites into FF1 PR.

## Legal: your own copy only

- The tools contain **no game data**. They read a game image you own.
- Everything they write is Square Enix art and text. Keep it for **personal use**: do not upload, share or ship it,
  including inside a mod.

## What you need

| Item | Notes |
|---|---|
| Your own ISO of the PSP game | US release, ULUS10251. Other regions are untested |
| Python 3 | Tested with Python 3.15 |
| Pillow | `pip install pillow` |
| A tool that opens ISO files | For example 7-Zip, to copy one file out of the ISO |
| Free disk space | About 4 GB for everything; the animations alone take 3.2 GB |

Keep all `ff1psp_*.py` files and `ff1psp_ordered_README.md` together in one folder: some tools import each other.

## Step by step

Paths below are examples; use any folders you like. `<work>` is one empty folder for the output.

**1. Copy the data file out of the ISO**

Open the ISO and copy `PSP_GAME\USRDIR\data\ff1psp.dpk` (all of the game's data in one file) into `<work>`.

**2. Unpack it**

```
python ff1psp_dpk.py "<work>\ff1psp.dpk" "<work>"
```

- Writes `<work>\files\` (the 2,033 top-level entries) and `<work>\unpacked\` (one folder per archive).
- Every texture gets a `.png` beside it (3,831).
- `convert_log.txt` lists counts and problems; a good run reports `problems: 0`.
- `--only <name prefix>` unpacks only matching entries, e.g. `--only MAP_0A` for a quick test.

**3. Sort the pictures (start here when browsing)**

```
python ff1psp_order.py "<work>\unpacked" "<work>\ordered"
```

- Keeps every picture once (2,230), in categories: monsters, characters, backgrounds, tilesets, effects, UI and more.
- Also renders the maps (`maps\`) and every sprite as frames (`frames\`) by calling the two tools below.
- Writes its own `README.md` into `ordered\` explaining each folder and CSV.

**4. Render the animations (optional, slow)**

```
python ff1psp_anim.py "<work>\unpacked" "<work>\ordered\animations"
python ff1psp_order.py --record "<work>\ordered"
```

- 538 spell, weapon and map animations, 65,126 frames; takes about 1.5 hours.
- The `--record` line tells the rebuild guard (below) that these files came from the tools.

**5. Extract the text (optional)**

```
python ff1psp_text.py "<work>\unpacked" "<work>\text_us"
```

One `.txt` per message file, one numbered string per line (`<31>` = line break). `--lang` picks another language
folder; Japanese characters are written as `<xx>` codes.

**6. PSP party battle sprites in FF1 PR (optional)**

```
python ff1psp_battle_pack.py "<work>\ordered" "<work>\battle_pack"
```

- Needs KupoUI.PR by faospark (with BepInEx) installed in FF1 PR; it loads the replacement pictures.
- Writes `GameAssets\Serial\Res\Chara\Battle\BC_FF1_P001`-`P012\` (one PNG + `.json` per pose).
  Copy the `GameAssets` folder into a KupoUI mod folder for FF1, e.g.
  `<FF1 PR>\Modules\00-Mods\<your mod name>\FF1\GameAssets\`.
- 64x64 frames, feet at the bottom centre, `pixelsPerUnit` 2: the same size on screen as the game's own sprites.
- Pose mapping: `POSES` at the top of the tool. Left-hand attack poses are empty in FF1 PR and stay unchanged.
- `--vanilla <MagiciteExport folder>` adds `preview.png`: the game's sprites above, the PSP ones below. It needs a
  [Magicite](https://github.com/Silvris/Magicite) export of FF1 PR.

## The tools

| Tool | Reads | Writes |
|---|---|---|
| `ff1psp_dpk.py` | `ff1psp.dpk` | unpacked archives, PNG beside every texture |
| `ff1psp_order.py` | `unpacked\` | sorted `ordered\` folder (runs the next two tools), `index.csv` of every original copy |
| `ff1psp_maps.py` | `unpacked\` | world map and rooms: inside and outside views with their shadow and foreground layers, `rooms.csv`, `layer_sets.csv` |
| `ff1psp_frames.py` | `unpacked\` | every sprite (party, NPCs, objects, vehicles) as frames, `frames.csv` |
| `ff1psp_anim.py` | `unpacked\` | every `.ANA` animation as frames plus a contact strip, `anim.csv` |
| `ff1psp_text.py` | `unpacked\` | game text as `.txt` |
| `ff1psp_battle_pack.py` | `ordered\` | KupoUI pack for FF1 PR's party battle sprites |

Common rules:
- Each tool prints its usage when started without arguments or with `--help`.
- They refuse to write into an existing output folder unless you add `--force`.
- They only read the game files and never run anything from them.
- **Rebuild guard:** `ff1psp_order.py --force` rebuilds `ordered\` from scratch. Before it does, it checks
  `.built_files.csv`. If you added or edited any file in `ordered\` since the last build, it stops (exit code 3)
  and lists those files, so your own work is never overwritten. Move them out first, then run it again.

## File formats

Each tool's header describes the formats it reads, byte by byte, with every point marked as checked or inferred:

| Format | What it is | Described in |
|---|---|---|
| `.dpk`, `.PCK`, Wp16 compression | Archives | `ff1psp_dpk.py` |
| `.GIM` | Textures (indexed and direct colour, PSP-swizzled) | `ff1psp_dpk.py` |
| `_AMD.BIN`, `_ANM.ATI` | Room layouts, animated tiles | `ff1psp_maps.py` |
| `.ACD`, `.ACT` | Shadow and foreground layers of a room (inside / outside view) | `ff1psp_maps.py` |
| `.OTI` | Sprite frames as lists of pieces (two variants: sheet areas and 16 px cells) | `ff1psp_frames.py` |
| `.ANA` | Keyframed sprite animations with embedded textures | `ff1psp_anim.py` |
| `.FIF`, `.MSG` | Font tables and message text | `ff1psp_text.py` |

## Known gaps

- **`.EFA`** (19 effect files): the textures inside are extracted; the particle settings after them are not
  decoded.
- **`.AOB` / `_AOB.BIN`:** one entry per sprite frame, probably anchor points (hands, effects); not decoded. Frames
  draw correctly without them.
- **Bonus field `MAP_EX03_R54`:** its sea renders black. That tile is empty on every sheet and not in the
  animation list, so the game draws it some other way.
- **Room names:** maps keep their file names. Only Castle Cornelia and the town of Cornelia are named (`PLACES` in
  `ff1psp_maps.py`, filled in by comparing screenshots).
- **NPC positions** per room (event data): not read.
- **Animations** are a close approximation: blend modes, rotation direction and timing are inferred and were not
  compared frame by frame with the game.
- **Not graphics, not decoded:** `.DAT` (save/load messages), `.EMT` (menu table), `.NPK` / `.AT3` (sound).
