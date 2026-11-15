# Fonts

The pack contains no font files. This page says how to use other fonts in `MenuLayout.json`, and how to make extra weights of a font usable.

## Use an installed font

1. Install the font in Windows (right-click the `.ttf` → **Install for all users**).
2. Restart the game.
3. Use its name in a rule: `"font": "Georgia"`.

The log line `[Fonts] loaded fonts` lists every name the game can use. Details: `MenuLayout_README.md`, "Font faces".

## Why some weights need a renamed copy

The game can reach only **Regular, Bold and Italic** of each font family. A Light or SemiBold file of the same family is installed but unreachable.
Variable fonts (one file for all weights, `…-VariableFont_wght.ttf`) don't work either.

The fix: a copy of the static weight file whose **family name is its own**, e.g. "My Sans Light". Then `"font": "My Sans Light"` reaches it.

## Make a renamed copy

Tool: `source\tools\rename_font.py` (Python 3, no extra packages).

```
python rename_font.py JosefinSans-Light.ttf MySans-Light.ttf --family "My Sans Light" --style Regular
python rename_font.py JosefinSans-LightItalic.ttf MySans-LightItalic.ttf --family "My Sans Light" --style Italic
```

| Step | Details |
|---|---|
| 1. Get the **static** files | Download the font, open the `static` folder, pick the weights you need (`-Regular`, `-Light`, `-LightItalic`…) |
| 2. Rename | Run the tool once per file. `--family` = the new name; `--style` = `Regular`, `Italic`, `Bold` or `Bold Italic` (required); `--force` overwrites an existing output |
| 3. Install | Install the new `.ttf` files in Windows, restart the game |
| 4. Use | `"font": "My Sans Light"` in a rule or style |

The tool replaces every name the font is found by, removes the "typographic family" names that would file it under the old family again, and keeps the copyright and licence entries.

### Licence rule for renamed fonts

Many free fonts use the **SIL Open Font License**. If the font has a *Reserved Font Name* (Josefin Sans does), a modified copy **must not** use that name. Pick a new one ("My Sans", not "Josefin Sans Light"). You may share the renamed files only under the same licence, and never sell them on their own.

## The fonts in the author's setup

`examples\author\MenuLayout.json` uses these. Download them free from Google Fonts:

| Name in the config | Original font | Download | Made how |
|---|---|---|---|
| `Amarante` | Amarante | https://fonts.google.com/specimen/Amarante | Installed as is |
| `SpriteMod Sans` | Josefin Sans (static Regular and Italic) | https://fonts.google.com/specimen/Josefin+Sans | Renamed copies, family "SpriteMod Sans" |
| `SpriteMod Sans Light` | Josefin Sans (static Light and Light Italic) | same | Renamed copies, family "SpriteMod Sans Light" |
| `fontStyle: "Small"` | Segoe UI Variable | ships with Windows 11 | — |

Without them, rules that name these fonts keep the game's font and the log says which name was not found.
