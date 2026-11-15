# SpriteMod HD for FINAL FANTASY V Pixel Remaster

By **Raivshard**.

A BepInEx plugin (`FF5MenuSprites.dll`) and toolkit for HD art and UI mods in **FINAL FANTASY V Pixel Remaster** (Steam, Windows).

It does not contain any art. It is the system that makes HD art look right in the game, plus a live-editable layout file for the menus and the battle screen.

## What it does

| Area | Features |
|---|---|
| **Battle** | Full-resolution rendering (no more blocky HD sprites), HD character and enemy sizing, full-height backgrounds, party placement per slot, depth scaling, **3D ground shadows on the GPU** (light direction, height, blur, depth maps) |
| **Battle backgrounds** | HD backgrounds of any size, HD frames for animated parts (torches, water) with automatic placement |
| **Menus** | Size and place character sprites per screen; move, resize, recolour, re-font, hide or swap **any** text or image; shared styles; cursor/greyed-out looks; list spacing |
| **Other** | Custom loading animation, title-menu highlight, party name swap for old saves, shorter freezes after battles (optional) |
| **For modders** | F8 screen dump (every object, path, font, size), add-on rule files, JSON schema with autocomplete, typo warnings in the log |

Everything in `MenuLayout.json` is applied **live**: save the file and the game updates within a second.

## Requirements

| Needed | Version tested | Notes |
|---|---|---|
| FINAL FANTASY V Pixel Remaster (Steam) | Unity 2019.4.26f1 build | Windows, Direct3D 11 |
| [BepInEx 6](https://github.com/BepInEx/BepInEx) IL2CPP build | 6.0.0-be.697 | Run the game once after installing it, so BepInEx builds its `interop` folder |
| [KupoUI.PR](https://www.nexusmods.com/finalfantasy5pixelremaster/mods/27) by faospark (Nexus page "Darker UI") | FF5 build | Loads the HD image files. Without it the plugin still runs, but HD art is not swapped in |

Not required, but tested and compatible: Magicite, Memoria.FF5, Syldra, FFPR_Fix.

## Install

Use the ready-made pack zip (Nexus page, or Releases on GitHub). The GitHub source tree has no compiled files: no `FF5MenuSprites.dll`, no shader bundle.

1. Install BepInEx and KupoUI as their pages describe, and start the game once.
2. Copy everything inside this pack's `install\` folder into the game folder (the folder with `FINAL FANTASY V.exe`). It adds:
   - `BepInEx\plugins\FF5MenuSprites.dll`
   - `Modules\00-Mods\SpriteMod HD\FF5\` (settings file, guides, shader bundle; your art goes here too)
3. Start the game. `BepInEx\LogOutput.log` should contain a line with `FF5MenuSprites`.

**Uninstall:** delete `BepInEx\plugins\FF5MenuSprites.dll` and the `Modules\00-Mods\SpriteMod HD` folder.

## Where to read next

| File | For |
|---|---|
| `install\Modules\00-Mods\SpriteMod HD\FF5\MenuLayout_README.md` | Every setting in `MenuLayout.json`, with examples. Start with "Quick start" |
| `install\...\FF5\ART_GUIDE.md` | Making HD art: sizes, file names, folders, depth maps for shadows |
| `install\...\FF5\FONTS.md` | Using other fonts, and making extra font weights usable |
| `install\...\FF5\battle_backgrounds\animtex\README.md` | HD frames for animated battle backgrounds |
| `examples\author\MenuLayout.json` | The author's full setup: many real rules to copy from. To use it whole, replace the installed `MenuLayout.json` with it; it needs the fonts in `FONTS.md`. The pack's own `MenuLayout.json` is the same setup without fonts and images |
| `docs\TECHNICAL_NOTES.md` | How the game works inside, for plugin developers |
| `source\BUILDING.md` | Building the plugin, the shader bundle and the tools |

## Pack contents

```
install\    copy into the game folder
examples\   the author's full MenuLayout.json
docs\       technical notes for developers
source\     plugin source (C#), shader project (Unity), tools (Python, C#)
```

## Credits and licence

- **Built with Claude Code** (Anthropic's AI coding assistant): the code, shaders, tools and docs were written with it, directed and tested in-game by Raivshard.
- Made with [BepInEx](https://github.com/BepInEx/BepInEx), [Il2CppInterop](https://github.com/BepInEx/Il2CppInterop) and [Harmony](https://github.com/pardeike/Harmony). Works with KupoUI.PR by faospark.
- FINAL FANTASY is a registered trademark of Square Enix. This pack contains no game files or game art.
- Licence: free to use and change in free mods; no selling or paywalls (donations and Nexus Donation Points are fine). Your own art stays yours. Details: `LICENSE.md`.
