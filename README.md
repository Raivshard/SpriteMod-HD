# SpriteMod HD for FINAL FANTASY V Pixel Remaster

By **Raivshard**.

A BepInEx plugin (`FF5MenuSprites.dll`) and toolkit for HD art and UI mods in **FINAL FANTASY V Pixel Remaster** (Steam, Windows).

**Other Pixel Remaster games:** should run in all PR games. Originally built on FF5 PR, so some bugs may reflect that. Use the game's folder name (`FF1` … `FF6`) in place of `FF5` in the paths below, e.g. `Modules\00-Mods\SpriteMod HD\FF6\`.

It does not contain any art. It is the system that makes HD art look right in the game, plus a live-editable layout file for the menus and the battle screen.

## What it does

| Area | Features |
|---|---|
| **Battle** | Full-resolution rendering (no more blocky HD sprites), HD character and enemy sizing, size factor for replaced sprites drawn at the game's pixel density (`customScale`), full-height backgrounds, party placement per slot, depth scaling, **3D ground shadows on the GPU** (light direction, height, blur, depth maps). *Unverified (not yet tested in-game):* Mini status shown with the normal sprites at a smaller size (`miniScale`) |
| **Maps** | HD map tile sheets of any size (towns, dungeons, world map), swapped in on every map view without breaking the game's tile layout; per-map files where a sheet name has different art in different maps. Tile tools: one catalogue of every tile, replace a tile once and rebuild every sheet that uses it. HD field (map) character frames (`fieldSprites`) with one size setting (`fieldScale`), effects and mis-layered signs drawn correctly around taller characters, with per-frame walk timing (`fieldStrideDelay`) |
| **Battle backgrounds** | HD backgrounds of any size, HD frames for animated parts (torches, water) with automatic placement |
| **Menus** | Size and place character sprites per screen; move, resize, recolour, re-font, hide or swap **any** text or image; shared styles; cursor/greyed-out looks; list spacing |
| **Other** | Faster Load Game list (`fastSaveList`), custom loading animation, title-menu highlight, party name swap for old saves, shorter freezes after battles (optional) |
| **For modders** | F8 screen dump (every object, path, font, size), add-on rule files, JSON schema with autocomplete, typo warnings in the log |

Everything in `MenuLayout.json` is applied **live**: save the file and the game updates within a second.

## Requirements

| Needed | Version tested | Notes |
|---|---|---|
| FINAL FANTASY V Pixel Remaster (Steam) | Unity 2019.4.26f1 build | Windows, Direct3D 11 |
| [UnityPlayer CRC check Bypass](https://www.nexusmods.com/finalfantasypixelremaster/mods/90) | | Prerequisite for UnityPlayer mods |
| [BepInEx 6.0.0-pre.2 IL2CPP](https://github.com/BepInEx/BepInEx/releases/download/v6.0.0-pre.2/BepInEx-Unity.IL2CPP-win-x64-6.0.0-pre.2.zip) | 6.0.0-pre.2 (Unity IL2CPP, Windows x64) | Run the game once after installing it, so BepInEx builds its `interop` folder |
| [KupoUI.PR](https://www.nexusmods.com/finalfantasy5pixelremaster/mods/27) by faospark (Nexus page "Darker UI") | FF5 build | Loads the HD battle, enemy and menu images. Without it the plugin still runs, but that art is not swapped in (HD map tiles and field characters are loaded by the plugin itself) |
| [Magicite](https://github.com/Silvris/Magicite) by Silvris | | **Required for the map tools** (`source\tools\map_tiles\`): they read the game's maps from Magicite's one-time export (steps in the map guide). Not needed to play with the mod |

Not required, but tested and compatible: Memoria.FF5, Syldra, FFPR_Fix.

## Install

Use the ready-made pack zip (Nexus page, or Releases on GitHub). The GitHub source tree has no compiled files: no `FF5MenuSprites.dll`, no shader bundle.

1. Install BepInEx and KupoUI as their pages describe, and start the game once.
2. Copy everything inside this pack's `install\` folder into the game folder (the folder with `FINAL FANTASY V.exe`). It adds:
   - `BepInEx\plugins\FF5MenuSprites.dll`
   - `Modules\00-Mods\SpriteMod HD\FF5\` (settings file, guides, shader bundle; your art goes here too)
   - Other PR games: copy only the DLL and make `Modules\00-Mods\SpriteMod HD\FF6\MenuLayout.json` (your game's name in place of `FF6`) containing `{ }`. The pack's `MenuLayout.json` holds FF5 rules.
3. Start the game. `BepInEx\LogOutput.log` should contain a line with `FF5MenuSprites`.

**Uninstall:** delete `BepInEx\plugins\FF5MenuSprites.dll` and the `Modules\00-Mods\SpriteMod HD` folder.

## Where to read next

| File | For |
|---|---|
| `install\Modules\00-Mods\SpriteMod HD\FF5\MenuLayout_README.md` | Every setting in `MenuLayout.json`, with examples. Start with "Quick start" |
| `install\...\FF5\ART_GUIDE.md` | Making HD art: sizes, file names, folders, depth maps for shadows |
| `install\...\FF5\FONTS.md` | Using other fonts, and making extra font weights usable |
| `install\...\FF5\battle_backgrounds\animtex\README.md` | HD frames for animated battle backgrounds |
| `source\tools\map_tiles\README.md` | Making your own map tiles in Tiled, for any PR game; one command builds and installs them |
| `examples\author\MenuLayout.json` | The author's full setup: many real rules to copy from. To use it whole, replace the installed `MenuLayout.json` with it; it needs the fonts in `FONTS.md`. The pack's own `MenuLayout.json` is the same setup without fonts and images |
| `CHANGELOG.md` | What changed in each version |
| `docs\TECHNICAL_NOTES.md` | How the game works inside, for plugin developers |
| `source\BUILDING.md` | Building the plugin, the shader bundle and the tools |

## Pack contents

```
install\    copy into the game folder
examples\   the author's full MenuLayout.json
docs\       technical notes for developers
source\     plugin source (C#), shader project (Unity), tools (Python, C#)
CHANGELOG.md
```

## Credits and licence

- **Built with Claude Code** (Anthropic's AI coding assistant): the code, shaders, tools and docs were written with it, directed and tested in-game by Raivshard.
- Made with [BepInEx](https://github.com/BepInEx/BepInEx), [Il2CppInterop](https://github.com/BepInEx/Il2CppInterop) and [Harmony](https://github.com/pardeike/Harmony). Works with KupoUI.PR by faospark.
- FINAL FANTASY is a registered trademark of Square Enix. This pack contains no game files or game art.
- Licence: free to use and change in free mods; no selling or paywalls (donations and Nexus Donation Points are fine). Your own art stays yours. Details: `LICENSE.md`.
