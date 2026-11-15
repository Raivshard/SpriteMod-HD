# ObjectConfig.json (KupoUI): how to use it

KupoUI reads every `ObjectConfig.json` under `Modules\`. Each rule changes one UI object: its position, size, text settings, colour, or visibility.

> **Sources:** key names come from KupoUI's own code (`KupoUI.PR.dll`) and from the configs installed here (Amano Style TitleScreen, Darker UI).
> KupoUI ships no documentation for this file, so anything marked *(inferred)* has not been tested.

---

## Before you start

| Thing to know | Details |
|---|---|
| **When changes apply** | When a scene loads (the log says `[ObjectConfig] Scene loaded: ...`). Restart the game, or leave and re-enter the screen, to be safe. |
| **Which files load** | Files in a mod's root apply to **all** games. Files in an `FF1`–`FF6` folder apply to **that game only**. FF5 currently loads 3 files: Amano, Darker UI, `Darker UI\FF5`. |
| **Units** | `Position`, `Size` and `FontSize` are the game's UI units, **not** screen pixels. At 1440p, 1 unit ≈ 1.33 px *(inferred from the 1080p UI reference size)*. |
| **Log** | `BepInEx\LogOutput.log`, lines start with `[ObjectConfig]`. A rule without `TargetObjectName` is skipped with a warning. |
| **Finding paths** | Press **F8** (SpriteMod HD screen dump). The `path` column is the `TargetPath`. |

---

## File layout

```json
{
  "objects": [
    {
      "TargetObjectName": "last_text",
      "TargetPath": "RootObject/menu_canvas/.../main_menu/title_command_content(Clone)/last_text",
      "FontSize": 70
    }
  ]
}
```

---

## Picking the object

| Key | Meaning |
|---|---|
| `TargetObjectName` | The object's own name. **Required.** |
| `TargetPath` | Its full path, including `(Clone)` where the game adds it |
| `SceneName` | Limit the rule to one scene, e.g. `"Title"` *(inferred)* |

---

## Position and size

| Key | Value | Example |
|---|---|---|
| `Position` | Local position, units | `{ "x": -730, "y": 580, "z": 0 }` |
| `Scale` | Multiplier (scaling text this way can blur it) | `{ "x": 0.9, "y": 0.9, "z": 1 }` |
| `Rotation` | Degrees *(inferred)* | `{ "x": 0, "y": 0, "z": 0 }` |
| `Size` | Width and height, units | `{ "x": 1920, "y": 745 }` |
| `SiblingIndex` | Drawing order among siblings (higher = drawn later, on top) | `2` |

## Layout

Lists and rows use layout groups, which put their items back in place. These keys stop that.

| Key | Value | What it does |
|---|---|---|
| `IgnoreLayout` | `true` | The object is no longer placed by its parent's layout. Needed before `Position` works on a list item. |
| `DisableLayoutGroup` | `true` | Switches off the object's own layout group |
| `DisableLayoutElement` | `true` | Switches off its layout element |
| `DisableContentSizeFitter` | `true` | Stops it resizing itself to its content |
| `ChildAlignment` | `"MiddleLeft"` ... | Alignment of a layout group's items |

## Text

| Key | Value | What it does |
|---|---|---|
| `FontSize` | Number | Text size, units. **Overridden** for any text whose font type is set in KupoUI's `fontconfig.json` (the font swap writes its own size). Use `Scale` for those. |
| `ResizeTextForBestFit` | `true` / `false` | The game's shrink-to-fit. With it on, the text is never larger than `ResizeTextMaxSize`. |
| `ResizeTextMaxSize` | Number | Largest size shrink-to-fit may use |
| `ResizeTextMinSize` | Number | Smallest size shrink-to-fit may use |
| `TextAlignment` | `"MiddleLeft"`, `"UpperCenter"`, ... | Text alignment: Upper / Middle / Lower + Left / Center / Right |
| `TextColorWhite` | `true` | Forces white text |
| `DisableShadow` | `true` | Removes the text's shadow |

## Colour and visibility

| Key | Value | Example |
|---|---|---|
| `Color` | A name, `#hex`, or r/g/b/a from 0 to 1 | `"Yellow"`, `"#FFD966"`, `{ "r": 1, "g": 1, "b": 1, "a": 0.4 }` |
| `SetActive` | `false` hides the object (KupoUI blocks the game from showing it again) | `false` |
| `DisableMask` | `true` stops the object cutting off its children | `true` |

Colour names KupoUI knows:
- **Plain:** `red`, `green`, `blue`, `black`, `white`, `yellow`, `cyan`, `magenta`, `gray`, `orange`, `clear`, `lightblue`, `navy`, `crimson`, `violet`
- **The game's own UI colours:** `gamewhite`, `gameblack`, `gameyellow`, `gameblue`, `gamegray`, `gamered`, `gamegreen`, `resuscitationyellow`, `keyhelpblack`, `timestampblue`

## Extra images

`NewImages` adds images inside the object, e.g. a window frame and background:

```json
"NewImages": [
  { "Name": "frame", "ImagePath": "UI_Common_WindowFrame01.png", "ImageType": "Sliced" },
  { "Name": "bg",    "ImagePath": "UI_Common_WindowFrame03.png" }
]
```

| Key | Meaning |
|---|---|
| `Name` | Name of the new image object |
| `ImagePath` | PNG file, looked up like KupoUI's other textures |
| `ImageType` | `Simple`, `Sliced` (keeps borders when stretched), `Tiled` or `Filled` |

---

## Changes made here

| File | Change | Why |
|---|---|---|
| `Amano Style TitleScreen\ObjectConfig.json`, the `main_menu` text rule | `FontSize` 40 → **70** | Title commands 75% larger (user, 2026-10-31). SpriteMod HD's own size rule was removed so the two mods don't compete. |
| same file, MAIN MENU TEXT | Added `"ResizeTextForBestFit": false` | These texts use shrink-to-fit, capped by fontconfig's Font01 `FontSizeMax` (38), so `FontSize` had no effect (user test). Off = `FontSize` is used as written. Untested. |
| same file, MAIN MENU TEXT | Found (F8 dump + KupoUI log, verified): the font swap sets these texts to Font01's `FontSize` from fontconfig.json, which overrides ObjectConfig `FontSize`. The user switched to `Scale` 1.25 and raised Font01 instead. | Per-text size can't win against the font swap; `Scale` is applied after it. |
| same file, new MAIN MENU CURSOR entry | `cursor_parent` `Scale` 0.8 (= 1 / 1.25); also added a missing comma after `DisableShadow` in the text entry | The cursor sits inside the text object, so it grew with the text scale. |
| same file, every entry | Added a `_comment` saying what the entry changes and where it is on screen; values unchanged | `TargetObjectName` alone (`last_text`, `sub_menu`) doesn't say what it is. Extras/Options list contents are from the game's command list *(inferred)*. |

---

## ObjectConfig or MenuLayout.json?

Both can move and restyle UI. Give each property **one** owner. If both set the same thing (like the title text size), you can't tell which one wins.

| Use | For |
|---|---|
| **ObjectConfig** | Fixed positions and sizes, hiding things, extra frame images, shadows |
| **MenuLayout.json** | Placing things relative to other objects (`below`, `centerOver`), list spacing, fonts by name, character sprites, battle sprites, render resolution |
