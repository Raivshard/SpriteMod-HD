# Animated battle backgrounds (animtex)

The game animates parts of some battle backgrounds (torches, water, lights) by swapping small pictures ("frames") on top of the background. The game still decides **which** frame shows and **when**. This folder only supplies the HD pictures and **where** they go on your HD background.

## Files

| File | What it is |
|---|---|
| `BG_FF5_12_A_0.png` … | One HD frame per game frame, named exactly like the game's frame. Drawn at your HD background's own pixel scale. |
| `BG_FF5_12_place.json` | Where the frames go on your HD background. Written by `tools/animtex_fit.py`. |
| No frame file | The game's own frame is shown. |

Not `BG_FF5_12.json`: KupoUI reads that name as its own settings for the background texture.

## `_place.json` keys

| Key | Meaning | Example |
|---|---|---|
| `source` | Part of the original 512x256 background that your HD image shows (px, from top-left) | `[23.87, 12, 463.86, 231.93]` |
| `pieces` | Top-left of each piece's frames in your HD background (px). Key = the game's piece name. | `"BG_FF5_12_A_0": [1157, 0]` |
| `feather` | Fade the frame edges over this many HD px (only transparency; colours unchanged). Edges on the background's border stay sharp. | `16` |
| `featherPieces` | Feather for one piece | `"BG_FF5_12_A_0": 40` |
| `effects` | Centre of a spark/particle effect in HD px (default: moved with `source`) | `"Sparkle1": [550, 137]` |
| `frameScale` | Frame px per HD-background px (default 1) | `1` |

- Saving a file here applies within 2 seconds while the game runs.
- Pieces without a `pieces` entry are centred where the game's piece is, mapped through `source`.

## New background

1. Put the HD background in `battle_backgrounds\` and the frames (or your upscaled sheet) anywhere.
2. Run `animtex_fit.py` (pack: `source\tools\`; needs Python with Pillow and UnityPy): it cuts frames from an upscaled sheet (crop only, pixels unchanged), finds each piece on the background, writes `_place.json`, and prints a match score per piece (under ~5 = clean match).
   `python animtex_fit.py <game background bundle> <HD background.png> <this folder> [--sheet <upscaled sheet.png>] [--feather 16]`. The bundle is the background's file in `FINAL FANTASY V_Data\StreamingAssets\aa\StandaloneWindows64\` (`bg_ff5_12_assets_all_*.bundle`).
3. Test in-game; adjust `feather` / `featherPieces` by eye.

## Backgrounds with animation

| Background | Setup |
|---|---|
| 05, 08, 12, 20, 26 | 1–3 pieces swapping frames |
| 18 | 32 small pieces (frames named `BG_FF5_18_A_n`) |
| 22 | 12 pieces, frame swaps + colour fades; one frame used by 3 pieces |
| 23 | the whole sheet as one fading overlay |
| 31 | scrolling texture on a mesh: **not supported yet** |
