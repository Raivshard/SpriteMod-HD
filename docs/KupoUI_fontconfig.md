# fontconfig.json (KupoUI): how to use it

KupoUI's font swap. It replaces the game's fonts **per font type** (`Font01`–`Font10`) and sets their size and spacing.

- File: `Modules\System\fontconfig.json`
- KupoUI's own guide, with the full default list for every language: `font-help.txt` (same folder)
- Switch: `faospark.kupoui.pr.cfg` → `[FontSwap]` → `Enabled`

---

## Before you start

| Thing to know | Details |
|---|---|
| **When changes apply** | **Restart the game.** Saving alone does nothing. |
| **Scope** | One font type changes **every** text of that type in the whole game. For a single text, see the last section. |
| **Language block** | Settings go in the block for the language you play, e.g. `"En"`. Other blocks are ignored. |
| **Only list what you change** | Font types you leave out keep the game's default. |
| **Font names** | The font's family name as Windows lists it, e.g. `"Segoe UI"`. The font must be installed. |

---

## Settings per font type

```json
"Font01": { "FontName": "Roboto Serif", "LineSpace": 1.1, "YOffset": 4, "FontSize": 28, "FontSizeMin": 20, "FontSizeMax": 38 }
```

| Key | Meaning | Example |
|---|---|---|
| `FontName` | Font to use | `"Roboto Serif"` |
| `LineSpace` | Line spacing, × normal | `1.1` |
| `YOffset` | Moves the text vertically, px. **Positive = up**, negative = down | `4` |
| `FontSize` | Size for texts with a **fixed** size | `28` |
| `FontSizeMin` | Smallest size for **shrink-to-fit** texts | `20` |
| `FontSizeMax` | Largest size for **shrink-to-fit** texts | `38` |

### Fixed size or shrink-to-fit?

Each text in the game uses one of two modes, chosen by the game:

| Mode | Size it shows at | Which key counts |
|---|---|---|
| **Fixed** | Always `FontSize` | `FontSize` |
| **Shrink-to-fit** ("best fit") | The largest size between Min and Max that fits the text's box | `FontSizeMin` / `FontSizeMax` |

> **Why a text won't get bigger:** a shrink-to-fit text is never larger than `FontSizeMax`, however much empty screen is around it. Raising `FontSize` does nothing for it.
> Example: the title screen commands are Font01 with shrink-to-fit, so here they stop at 38.

---

## Which font type is which (English)

From the game's menu files. *Inferred* = only set by the game's code, so it can't be read from the files.

| Type | Default font | Used for |
|---|---|---|
| **Font01** | SE-ALPSTN__ (modern) | **Main UI text:** menus, item/ability/command names, help text, battle windows, title screen commands |
| **Font02** | Arial | Touch-mode buttons only ("Back", "Remove all") |
| **Font03** | Arial | **Names and headers:** character names, save-slot info, item names in lists, pause menu, pop-up messages |
| **Font04** | Arial | Not used by any menu |
| **Font05** | FOT-NewRodinPro-DB | **Numbers and symbols** (modern): HP/MP, stats, gil, `/`, `%` |
| **Font06** | FOT-NewCezannePro-B | Letters printed **on button icons** |
| **Font07** | SE-ALPSTN__ | Dialogue window, modern font *(inferred)* |
| **Font08** | sqex-MonoSix (classic) | Main UI text with the Classic font setting *(inferred)* |
| **Font09** | PIXELREMASTERFONT | Numbers, classic *(inferred)* |
| **Font10** | sqex-MonoSix | Dialogue, classic *(inferred)* |

**Pairs that belong together** (from KupoUI's guide):

| To change | Edit |
|---|---|
| Modern font | `Font01` **and** `Font07` |
| Classic font | `Font08` **and** `Font10` |
| Menu numbers | `Font05` (modern) / `Font09` (classic) |

> Arial is listed as a default but isn't included with the game. KupoUI suggests replacing every Arial type.

---

## Current settings (`"En"`)

| Type | Font | Size | Min–Max | LineSpace | YOffset |
|---|---|---|---|---|---|
| Font01 | Roboto Serif | 32 | 24–44 | 1.1 | 4 |
| Font02, 04, 05, 06 | Roboto Serif | 28 | 20–38 | 1.1 | 4 |
| Font03 | Roboto Serif | 32 | 24–44 | 1.1 | 0 |
| Font07 | Roboto Serif | 32 | 20–42 | 1.1 | 4 |
| Font08, 09, 10 | Assistant | 30 | 20–40 | 1.1 | 4 |

*(Snapshot from 2026-10-31. The file itself is the up-to-date version.)*

---

## Changing just one text

`fontconfig.json` changes a whole font type. For one text, use one of these:

| Tool | How | Notes |
|---|---|---|
| **KupoUI ObjectConfig `Scale`** | `"Scale": { "x": 1.25, "y": 1.25, "z": 1 }` on the text | **Works.** Applied after the font swap. Slightly softer at large scales; anything inside the text (e.g. a cursor) grows too. |
| **ObjectConfig `FontSize`**, MenuLayout `fontSize` / `fontScale` | A size number on the text | **Doesn't work for swapped font types.** The font swap writes its own `FontSize` over it (verified on the title screen, 2026-10-31). |
| **SpriteMod HD MenuLayout.json** | `color`, `font` | See `MenuLayout_README.md`. |

> Give each text **one** owner. If two tools set the same thing, you can't tell which one wins.
