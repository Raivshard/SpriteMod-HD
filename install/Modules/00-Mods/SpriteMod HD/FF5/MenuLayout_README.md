# MenuLayout.json: how to use it

The settings file for the **FF5MenuSprites** plugin. It lives in `Modules\00-Mods\SpriteMod HD\FF5\MenuLayout.json`.

> **Other PR games:** should run in all PR games. Originally built on FF5 PR, so some bugs may reflect that. Use the game's folder name (`FF1` … `FF6`) in place of `FF5` in every path. Menu paths, battle tuning and the examples here come from FF5; settings that need certain maps or files say so.

> **Where the plugin looks:** `Modules\System\FF5\` first, then any `FF5` folder under `Modules`. If it finds more than one, the log names them all and the first by path wins.

It can:
- size and place the **character sprites** on each menu screen
- move, resize, recolour or change the font of **any text or image**
- raise the **render resolution** of battle, so high-res sprites stay sharp
- write a **screen dump** (F8) that lists everything on screen

---

## Before you start

| Thing to know | Details |
|---|---|
| **Changes are live** | Save the file and the game picks it up within a second. No restart. |
| **Units** | All sizes and distances are **pixels on a 1440p 16:9 screen**. Other resolutions are scaled automatically. Other screen shapes (ultrawide, 16:10 / Steam Deck): px follow the game's menu, which keeps its 16:9 shape (untested; the log says `[Aspect]` when this applies). |
| **Direction** | `moveDown` / `moveRight`: positive = down / right, negative = up / left. |
| **Broken file** | If the file can't be read (a missing comma, for example), the plugin keeps the original layout and the log says which line is wrong. |
| **Log** | `BepInEx\LogOutput.log`. Plugin lines start with `[MenuLayout]`, `[Elements]`, `[Render]`, `[Fonts]`, `[Loading]`, `[Title]`, `[Dump]`, `[AddOns]`, `[Keys]`, `[Aspect]`. |
| **Typos** | Keys are case-sensitive. An unknown or misspelt key logs a `[Keys]` warning with the closest real key. In VS Code the file also gets autocomplete and red underlines. See "Typos: key warnings and the schema" below. |
| **Add-on files** | More rules can go in `MenuLayout.<name>.json` files beside this one. See "Add-on files" below. |
| **Keys starting with `_`** | Ignored by the plugin, so use them for your own notes. |

---

## Quick start: change one text

1. Go to the screen in-game and press **F8**.
2. Open the newest file in the `_dumps` folder next to `MenuLayout.json` (Excel or any text editor).
3. Find the text (search for what it says, e.g. `Strength`) and copy the end of its **path**.
4. Add a rule to `"elements"`:

```json
{ "name": "my rule", "path": "names/name", "within": "status_details", "color": "#6FFFAF" }
```

5. Save. The change shows straight away.

---

## Top-level keys

| Key | What it does | Example |
|---|---|---|
| `$schema` | Points editors (VS Code) at `MenuLayout_schema.json` for autocomplete and checks. The plugin ignores it | `"./MenuLayout_schema.json"` |
| `log` | `true` = write details of what each rule matched to the log | `true` |
| `dumpKey` | Screen dump key, `F1`–`F12`; `""` = off. Default `F8` | `"F8"` |
| `perf` | `true` = every 5 s a `[Perf]` line in the log: frame times (average, 95%, worst, frames over 33 ms; loading screens separately), .NET garbage collections, and milliseconds per plugin system. For finding slowdowns; leave `false` otherwise | `false` |
| `profileGame` | `true` = time the **game's own** map loading, asset loading, object creation, PNG decoding and clean-up. Logs `[GameProf]` lines: every frame over 100 ms with what ran in it, and one summary per area change (from the first map-loading call until 30 smooth frames; works in every PR game). Read **at game start** (restart to switch). Diagnostic only: leave `false` | `false` |
| `characterNames` | Party names are stored in the **save file**, so a text pack (e.g. *Lenna to Reina*) only renames them in a new game. This swaps a saved name for another wherever the game shows it. Only exact matches change; a name you typed yourself stays | `{ "Lenna": "Reina" }` |
| `cleanup` | The game forces a full memory clean-up after battles, menus and map changes, about half a second of freeze each time. `skipGcCollect`: `true` = skip the forced collection (the game's background collector still runs). `unloadEveryMinutes`: the unused-asset sweep runs at most this often; skipped calls cost nothing. `0` = game's behaviour. Memory grows a little between sweeps; with `perf` on, the `[Perf]` line shows memory and skipped calls | `{ "skipGcCollect": true, "unloadEveryMinutes": 10 }` |
| `kupoTextureCache` | KupoUI swaps in its replacement images again every time the game loads a menu. `true` = keep a graphics-card copy of the images KupoUI replaces as a whole (title background, menu images) and reuse it. Anything that can't be copied falls back to KupoUI's normal way (log line `[KupoCache]`). Battle character sprites are always left to KupoUI's own swap: cached, a party member with a full set of replaced poses fell back to the game's own sprite after taking damage. Live | `true` |
| `fieldSprites` | Field (map) characters are cut from one atlas image per character, and KupoUI looks their frames up by name only (`Default_00`), a name every battle set also uses, so it cannot swap them. `true` = the plugin does it: `GameAssets\Serial\Res\Chara\Map\MO_FF5_P001_C00\Default_00.png` (folder = the character's map sprite set, file = the frame name) replaces that frame, standing on the game's feet line; `Default_00.json` `{ "pixelsPerUnit": 3 }` = 3 image pixels per game pixel; `"shiftY": -1` draws that frame 1 image pixel lower. The game lifts some frames itself (the side passing frame `WalkL_01` by 1 game pixel); for replaced frames the plugin cancels that, so the size file alone sets the height. `Default_00.2.png` (optional) = a second version of the frame, used every other time the frame comes round (two strides in a 2-frame walk; with `fieldStrideDelay` each walk starts on the other stride than last time). `Default_00.idle.png` (optional) = the frame shown once the character stops (the game keeps `_00` at rest). Missing files keep the game's frame. Needs a raised field render size (`renderHeights`) to show the detail. Live | `false` |
| `fieldSpritesDebug` | With `fieldSprites`: log every frame the game sets on the player's field character (`[FieldDbg]`, max 400 lines). Diagnostic: leave `false`. Live | `false` |
| `fieldScale` | With `fieldSprites`: size factor for every custom field (map) character frame, party and NPCs, around the feet. `1` = the size files' own size (`pixelsPerUnit`, e.g. 3 = the game's size for art drawn at 3x). `1.4` works best with taller HD characters (author's tests; starter settings use it). Live | `1` (starter: `1.4`) |
| `fieldStrideDelay` | With `fieldSprites`: seconds **added** to the walk frames of a direction: `"Default"` (front), `"WalkB"` (back), `"WalkL"` (side). `[first, middle, last]` = stride A, the passing frame (shown after each stride), stride B; a single number = middle only. Each frame keeps the game's 0.10 s plus its own value, so the walk cycle gets longer. A listed walk runs on the plugin's own clock while the game says the character walks; it starts on a stride, the other one than last time (tap, tap = stride A, stride B), and the game's own frame lift is cancelled so the frames' size files alone set their height. Max `0.5` each. Live: tune while walking | `{ "WalkL": [0, 0.1, 0] }` |
| `fastSaveList` | Save/Load list opens without the long pause: each slot's saved screenshot is decoded in the background and appears a moment later, instead of the game decoding all of them before the list shows. Each screenshot is also turned into a picture with a plain rectangle shape instead of Unity's traced outline, which took up to 1 s per slot (measured in FF1). Pictures already decoded are kept in memory (up to 160 MB) and set at once when the list refreshes them, so they don't flicker. `false` = the game's way. Live | `true` (default) |
| `fieldCeilingSigns` | With `fieldSprites`: ceiling-layer tiles that should be drawn like wall tiles, e.g. shop signs the game put in the ceiling layer (Karnak). Ceiling tiles cover every character, so they hide taller heads; listed tiles are drawn at ground-layer depth instead (behind characters in front of them). Per tile sheet: `{ "map_chip_147": [[0, 320], [16, 320]] }` = top-left pixel (x, y counted from the top) of each 16 px tile in the exported sheet PNG (`MagiciteExport\map_*\...\map_chip_NNN.png`). Live | none |
| `fieldEffectsUnderCharacters` | With `fieldSprites`: map effects drawn as their own square (for example the fire in a fireplace) follow the game's rule that whatever is lower on screen is in front. The game draws them after the characters, so larger sprites standing in front of one had their heads covered; while such a character overlaps the effect, it is now drawn under the character. A character behind the effect stays covered. `false` = the game's order. Live | `true` (default) |
| `fieldMapShadows` | Map shadows: `map_shadows\<Map>.png` beside this file is drawn over the floor and under the characters of that map (see "Map files" below). **Needs map files**; none ship with the mod. `false` = off. Live | `true` (default) |
| `fieldFlames` | **Deprecated** (use map objects with a Tiled animation and blend mode). Flame frames for map fire effects (torches, fireplaces) from `effects\<effect>\` (see "Map files" below). **Needs effect files**; tested in FF5 (Karnak). `false` = the game's flames. Live | `true` (default) |
| `fieldEffectResolution` | Map fire effects are filmed into a small 40x40 px picture that every torch shows. This makes that picture N times larger, so flame frames and sparks stay sharp. `1`–`8`. Live | `2` (default) |
| `fieldLavaSpeed` | Lava drawn by the game's water shader (orange "water", e.g. FF5 Karnak): speed of its scrolling, `1` = the game's. Water that is not lava-coloured is left alone. `0.05`–`4`. Tested in FF5 (Karnak). Live | `0.5` (default) |
| `fieldLavaWaterBlend` | Same lava: multiplies the shader's blend values, so the lava tile shows through the moving surface more (`<1`) or less. `0`–`1`. Tested in FF5 (Karnak). Live | `1` (default) |
| `fieldLavaAnimSpeed` | Speed of the lava layer's frames and of the tile animations in `map_tiles\anim\` (see "Map files" below). `0` = still. `0`–`8`. Live | `1` (default) |
| `fieldLavaOpacity` | Lava layer (`map_shadows\<Map>.lava.json`, see "Map files"): how much of the animated lava shows over the game's lava surface. `0`–`1`. **Needs map files.** Tested in FF5 (Karnak). Live | `0.5` (default) |
| `fieldLavaBlend` | Lava layer: how the game's moving surface (top) is blended onto the lava frames (bottom). `mode`: `normal`, `multiply`, `screen`, `overlay`, `linearBurn`, `colorBurn`, `linearDodge`, `softLight`, `vividLight`, `linearLight`, `color`. `opacity` `0`–`1`. `water` / `lava`: `[b0, b1, w0, w1]` (`0`–`255`) = Photoshop "Blend If" sliders for the top / bottom layer (`[0, 0, 255, 255]` = all). **Needs map files.** Live | `{ "mode": "linearBurn", "opacity": 1, "water": [0, 0, 92, 255], "lava": [0, 192, 240, 255] }` (default mode `linearBurn`) |
| `fieldLavaEdge` | Lava layer: a lighter edge along lava cells with no lava north of them, from your `map_shadows\lava_edge.png` (32 px, its own colours, alpha = coverage). Same `mode`, `opacity`, `water`, `lava` as `fieldLavaBlend`; `color` `"#RRGGBB"` tints the PNG (hue and saturation, like a Photoshop Color layer). **Needs map files.** Live | `{ "mode": "vividLight", "opacity": 1, "lava": [0, 92, 255, 255] }` (default mode `normal`) |
| `kupoFastUnreadable` | Most menu art goes into textures the game locks. KupoUI reads the file from disk, loads it, then hits a step that always fails on them (the art still shows). `true` = we do the same swap with KupoUI's own settings, keep the files in memory and skip the failing step: about 0.3 s faster per return to the title and 0.6 s on the bestiary (measured). After the first time an image is loaded, a graphics-card copy is kept and copied straight into later uses of the same image (when the size matches), so screens opened again (bestiary, menus) skip the image loading. Same look. Live | `true` |
| `kupoSkipUnreadable` | Like `kupoFastUnreadable`, but skips KupoUI's swap on locked textures entirely. Faster, but **that menu art is not replaced**. Diagnostic: leave `false`. Live | `false` |
| `textureLimit` | Texture quality. The game draws **every texture at half size** (Unity `masterTextureLimit` = 1), so a 2048x1024 battle background shows like 1024x512. `0` = full size, `1` = the game's half, `2` = quarter. No key = the game's own. Live | `0` |
| `screens` | Character sprite size and position per menu screen | see below |
| `styles` | Named sets of element keys that several rules share | see below |
| `elements` | Rules for single texts, images and sprites | see below |
| `textStyles` | Bold/italic, shadow, outline, spacing for every text of a font type | see below |
| `renderHeights` | Render resolution of the game's cameras | see below |
| `battle` | Battle screen: background fit, HD sprite sizing, party placement, depth look, 3D ground shadows | see "`battle`: the battle screen" below |
| `loadingIcon` | Loading animation speed, size and loading priority. See "Loading animation" below | `{ "frameTime": 0.125, "size": 0, "priority": "BelowNormal" }` |
| `titleHighlight` | Title menu highlight colour, growth and fade. See "Title menu highlight" below | `{ "color": "#FFE08A", "scale": 1.1, "fadeTime": 0.15 }` |

---

## `screens`: character sprites in menus

One entry per menu screen. The sprite keeps its shape; the feet are the anchor.

| Key | Meaning |
|---|---|
| `name` | Your label (shown in the log) |
| `match` | Part of the screen's object path, e.g. `status_details` |
| `height` | Sprite height on screen, px |
| `moveDown` / `moveRight` | Move the feet from where the game puts them, px |
| `top` | *Instead of moveDown:* put the **top** of the sprite at this y (px from the top of the screen) |
| `hide` | Images to switch off on that screen: `[{ "path": "...", "sprite": "..." }]` |

```json
{ "name": "equip", "match": "equipment_info", "height": 368, "top": 330 }
```

---

## `elements`: texts, images and sprites

Each rule has two parts: **which object** it changes, and **what** it changes.

### 1. Picking the object

| Key | Meaning |
|---|---|
| `name` | Your label (shown in the log) |
| `path` | The object's path **ends with** this. Copy it from a screen dump. |
| `within` | The path must **also contain** this. Use it to limit a rule to one screen. |
| `sprite` | *Instead of path:* the image's sprite name **starts with** this |
| `text` | Only texts **showing these words**: the whole text, capitals ignored, `*` = any letters (`"M*"` = Magic, Map...). Needs `path` or `within` too. Re-checked when the words change (list rows are reused as you scroll), about 10x a second. **`text` rules are checked before all other rules**, also from add-on files, and replace the general rule for that object: add the general rule's `style` to keep its look. |
| `fontType` | Only texts of this font type (`Font01`-`Font10`, `Default`; the F8 dump shows `fontType=`). Use with `path`, `within` or `text`; for every text of one type use `textStyles`. |
| `battleOnly` | `true` = only in battle |
| `nth` | Only the Nth object with this name under the same parent, e.g. `2` = the second `parame_text` in a row. The **first** matching rule wins, so put an `nth` rule above a general one for the same path |

> `(Clone)` in paths is ignored, so `status_details/...` and `status_details(Clone)/...` both match.
> The first rule that fits an object wins.
> **Section headings:** the boxes made of `"____": ""` lines (at the top level, and as small objects inside `elements`) are only headings for finding your way in the file. The plugin reads only the keys it knows and skips an `elements` entry without `path` or `sprite`, so headings can be added, renamed or removed freely. The same key twice (top and bottom border) is fine. Rules are grouped by screen under them; keep a new rule in its screen's group.
> `"font": ""` and `"fontScale": 0` mean "unchanged": new rules carry them as placeholders to fill in.

### 2. What to change

**Position**

| Key | Meaning |
|---|---|
| `moveDown` / `moveRight` | Move by this many px |
| `addSpritesDown` | `true` = in battle, add `battle.spritesDown` to `moveDown`, so a cursor follows the battle sprites. `moveDown` is then only the fine-tuning |
| `scale` | Size of any object (image, text, container) × the game's own size, e.g. `1.5`. `1` = unchanged. Scales around the object's pivot |
| `alpha` | Opacity of any object **and everything inside it**, `0`-`1`, e.g. `0.5` = half see-through. Multiplies a fade the game does itself |
| `hide` | `true` = invisible. **Looks only:** the object keeps its space (a list doesn't close the gap) and can still be selected with cursor, gamepad and mouse |

> Works on **any** object, including containers that draw nothing themselves (a row's number block, for example). Moving a container moves everything inside it.
> Objects inside a list or row keep their offset when the game re-lays the list out.

**Text**

| Key | Meaning |
|---|---|
| `color` | `"#RRGGBB"`, or `"#RRGGBBAA"` with transparency |
| `fontSize` | Text size in px |
| `fontScale` | Multiply the text's own size, e.g. `1.75` = 75% larger. Turns off the game's shrink-to-fit. `1` = same size, but the text may draw past its box: use it when a text **vanishes** after a font or size change (Unity drops a line that is taller than its box; e.g. the equip screen's `%`). Texts in KupoUI's fonts: see "KupoUI fonts and size" below the Images table. |
| `fontSizeMax` | Largest size, px, for a **shrink-to-fit** text (F8 dump shows `bestFit=True fit=min-max shown=size`). The text grows only as far as its box allows, so pair it with `widen`. |
| `fontSizeMin` | Smallest size, px, for a **shrink-to-fit** text: a long text stops shrinking here (and may then overflow its box). |
| `font` | Font name: a font the game or another mod has loaded (the log line `[Fonts] loaded fonts` lists them), or an installed Windows font, e.g. `"Georgia"` |
| `fontStyle` | `"Normal"` (or `"Regular"`), `"Bold"`, `"Italic"`, `"BoldAndItalic"`: Unity's own styles. **Any other words pick a face of the same font** that Windows has installed: `"SemiBold"` → *Josefin Sans SemiBold*, `"Small"` → *Segoe UI Variable Small*, `"Small Semibold"` → *Segoe UI Variable Small Semibold*. Add `Italic` to slant it (`"Light Italic"`). See "Font faces" below. |
| `lineSpacing` | Line spacing, × normal, e.g. `1.2` |
| `alignment` | `"UpperLeft"`, `"UpperCenter"`, `"UpperRight"`, `"MiddleLeft"`, `"MiddleCenter"`, `"MiddleRight"`, `"LowerLeft"`, `"LowerCenter"`, `"LowerRight"` |
| `shadow` | `false` = switch the text's shadow off. `{ "color": "#000000AA", "distance": [3, 3] }` = add or change it (`distance`: `[right, down]` in px; `3` or `[3]` = 3 right and 3 down). `"blur": 2` = soft edge, px: Unity shadows have no real blur, so 8 fainter copies are drawn in a ring around the shadow (1–3 px looks soft; more can show as rings; each copy draws the text once more) |
| `outline` | Same as `shadow`, for an outline around the letters, e.g. `{ "color": "#000000", "distance": 2 }` |
| `onTop` | `true` = draw this text above neighbouring windows and texts (e.g. numbers that overlap other text) |
| `backdrop` | Box behind the text: `{ "color": "#00000080", "padding": [6, 2] }` (padding px left/right, top/bottom). Sized to the letters, drawn just under the text and above everything else; hidden when the text is empty. `"merge": 3` = **one box** around all matching texts that share the object 3 levels up (e.g. a whole column; count from the text: its parent = 1). `"radius": 10` = rounded corners, px (`0` = square; a radius larger than half the box is reduced to fit) |
| `selected` | Settings used only while the **cursor is on this row**: `{ "color": "#FFD800", "scale": 1.1, "fontScale": 0.8, "font": "Georgia" }`. `color` replaces the rule's colour (the game's own is yellow `#FFD800`); `scale` multiplies on top of the rule's `scale`; `fontScale` and `font` replace the rule's. Item lists, command menus, character select, abilities, names and title use the game's own "has cursor" flag; other lists (magic list, battle lists) go by the game's cursor colour: a text the game paints yellow `#FFD800` counts as selected |
| `greyed` | Settings used only while the game shows the text **greyed out** (unusable: Tent away from a save point, a spell without MP). Same keys as `selected`. The game's grey is `#999999`; while greyed, the rule's own `color` is not used (the grey stays) unless `greyed` sets one. `selected` wins when both apply |

**Images**

| Key | Meaning |
|---|---|
| `image` | Show a PNG of yours instead of the game's picture: path from `MenuLayout.json`'s folder, e.g. `"Images/cursor.png"`. Stretched to the object's box (size unchanged: use `width`/`height`/`scale`). Images only (F8 dump: `Image` with `sprite=`). Saving the PNG again reloads it within 2 s. Missing file: one warning in the log, the game's picture stays |
| `radius` | Rounded corners, px, for a **plain box** (an image without a sprite; the F8 dump shows `sprite=` empty), e.g. the battle list's highlight bar. Images with a game sprite are left alone |
| `padding` | Grow the box by this many px **on each side**: `4`, or `[left/right, top/bottom]` e.g. `[10, 2]`. Negative = smaller. `0` = game's size. Don't combine with `moveDown`/`moveRight` in the same rule |

> **KupoUI fonts and size:** KupoUI keeps its own size on texts drawn in the fonts it loaded (`fontconfig.json`). So a rule with `fontScale` (or `selected`/`greyed` `fontScale`) draws the text in the plugin's **own copy** of the same installed Windows font, which KupoUI leaves alone. If the font is not an installed Windows font, the log says so and the size may not change. KupoUI's `YOffset` may not apply to the copy: if the text sits a few px off, fix it with `moveDown`. `fontSize` alone still uses KupoUI's copy: use `fontScale` instead.
> `fontStyle` `"Bold"` / `"Italic"`: with a real bold / italic file (e.g. `georgiab.ttf`, `Roboto-Bold.ttf`) that file is used. Without one, the game can thicken or slant the letters itself (Josefin Slab, variable files only, showed Bold and Italic; a Josefin Sans Bold test showed no change, cause unknown). Other weights (Light, SemiBold…): see "Font faces".
> KupoUI ObjectConfig `DisableShadow` may also switch off an `outline` on the same text (an outline is a kind of shadow to Unity). *(inferred)*

**Lists (rows of items)**

| Key | Meaning |
|---|---|
| `spacing` | Gap between **every** item of a list, px, in the list's own direction (rows: left-right, columns: top-bottom). `path` must point at the list itself. To shift one part of a row, use `moveRight` on that part instead. |
| `spacing` on a **scrolling list** | Gap between rows, px; the rows keep their size. `path` = the list's `Scroll View` (e.g. `"list_window/Scroll View"` with `"within": "item_info"`). Shows the **next time the list opens**. The log line `list row gap` gives the game's own gap and the row stride (row pitch) before and after. *(Untested: the first version changed only the gap and had no visible effect; this one sets the stride too.)* |
| `listPadding` | Space **inside** a row or column layout, between its edge and its items, px: `8`, `[left/right, top/bottom]` or `[left, right, top, bottom]`. `path` must point at the layout itself (F8 dump: `HorizontalLayoutGroup`/`VerticalLayoutGroup`). Not for scrolling lists |
| `listAlign` | Where a row or column layout puts its items: `"MiddleLeft"`, `"UpperCenter"`, ... (same names as `alignment`). Same `path` rule as `listPadding` |

> Row **height** in a scrolling list: `height` on a row only resizes that row's box, it does not move the rows below (the game places them itself). Use `spacing` on the `Scroll View` for the distance between rows.

**Size and placement relative to other objects**

| Key | Meaning |
|---|---|
| `width` | New width, px, for **any** object, including containers (stays centred). In a layout group it sets the layout width, like `height`. `0` = unchanged |
| `height` | New height, px, for **any** object, including containers. In a layout group (F8 dump: `HorizontalLayoutGroup`/`VerticalLayoutGroup` on the parent) it sets the row's layout height, so the rows below move with it. Scrolling lists: see `spacing`. `0` = unchanged |
| `widen` | Add this many px on the **right**; the left edge stays (containers too). Survives the game re-laying out the row. For shrink-to-fit text, a wider box lets it show larger. |
| `below` | List of paths: the top edge goes under the lowest of these |
| `gap` | Distance under the `below` objects, px |
| `centerOver` | List of paths: centred over these objects together |

> ⚠ **Unreliable.** The first real use (a short line on the status screen) put every line in that window in the wrong place. Avoid until it's reworked.
> `below` and `centerOver` use the objects' **boxes**, not the visible letters. A text box usually has some empty space under the letters, so tune `gap` by eye.
> With `below` / `centerOver`, `moveDown` / `moveRight` add a nudge on top.

### Examples

Recolour the character name on the main menu:
```json
{ "name": "main menu name", "path": "status/GameObject/name_text", "within": "status_info_content", "color": "#6FFFAF" }
```

More space between the title screen commands:
```json
{ "name": "title menu spacing", "path": "content_root/menu/main_menu", "spacing": 80 }
```

> **Other mods can set the same thing.** The title text size is set by Amano Style TitleScreen's KupoUI `ObjectConfig.json` (`FontSize`). If a rule here sets it too, you can't tell which one wins. Give each property one owner. See `ObjectConfig_README.md` in that mod's folder.

Move the equip screen's stat numbers right (the words stay put):
```json
{ "name": "equip stat columns", "path": "equip_status_content/root", "within": "equipment_status_window", "moveRight": 165 }
```

...and give the stat names that space:
```json
{ "name": "equip stat names", "path": "equip_status_content/category_text", "within": "equipment_status_window", "widen": 165 }
```

### Font faces (`fontStyle` beyond Bold/Italic)

The plugin adds your words to the text's font name and looks that face up **in the game's own font list**, which has one entry per installed font **file**.

- **Works:** fonts installed as separate files per weight (*static* fonts). Segoe UI (Light, Semilight, Semibold, Black), Georgia, Roboto Bold, … `"Semibold"` → *Segoe UI Semibold*.
- **Doesn't work: variable fonts** (one file for all weights, named like `…-VariableFont_wght.ttf`). The game sees only the file itself: with Josefin Sans' variable file the game lists only `Josefin Sans` and `Josefin Sans Italic`, so `"Light"`, `"SemiBold"` find nothing. Windows' font list shows those weights, but the game can't use them.
- **Only Regular, Bold and Italic are reachable per family.** The game files fonts under their family and lists only those styles: with `JosefinSans-Regular`, `-Bold`, `-Light` installed it shows "Josefin Sans", "Josefin Sans Bold", "Josefin Sans Italic", no Light. Asking for "Josefin Sans Light" by name doesn't reach it either. An unlisted face leaves the text's font alone.
- **Other weights (Light, SemiBold…)** need a font file whose family is its own: a renamed copy. How: `FONTS.md`.
- **Variable and static files of the same font installed together:** the game draws plain "Josefin Sans" in the static **Regular** style.
- Not found → the text keeps its font, and the log lists the faces the game can see: `[Fonts] no installed face … Faces of '…': …` (Warning, always in the log).
- Capitals and spaces don't matter (`"semibold"` = `"SemiBold"`).

---

## `styles`: shared named styles

Change a look in one place for every rule that uses it.

```json
"styles": {
  "screen name": { "color": "#6FFFAF", "font": "Amarante", "fontScale": 1.25 },
  "list row": { "font": "SpriteMod Sans Light", "fontScale": 1.0,
                "selected": { "color": "#33FF66", "font": "SpriteMod Sans" } }
},
"elements": [
  { "name": "magic name", "path": "...", "style": "screen name", "moveDown": -10 }
]
```

| Point | How it works |
|---|---|
| Keys | Any `elements` key works in a style (font, color, selected, shadow, moveDown...). |
| Rule wins | The rule's own keys win over the style's. |
| Blocks merge | `selected`, `greyed`, `shadow`, `outline` and `backdrop` merge key by key: a rule with `"selected": { "color": "#FF0000" }` keeps the style's selected font. |
| Several styles | `"style": ["list row", "bold"]`: later styles win over earlier ones. |
| Unknown name | Warning in the log; the rule still applies without it. |
| Names | Capitals don't matter. Keys starting with `_` are notes. |
| Live | Saving the file applies style changes to every rule using them. |

---

## Add-on files: `MenuLayout.<name>.json`

Extra rule files that sit **next to `MenuLayout.json`**, e.g. `MenuLayout.battle.json` or `MenuLayout.reina.json`. Use them to keep a set of rules apart, share it, or switch it on and off as a whole.

```json
{
  "styles":   { "red": { "color": "#FF4040" } },
  "elements": [ { "name": "main menu name", "style": "red", "path": "status/GameObject/name_text", "within": "status_info_content" } ]
}
```

| Thing to know | Details |
|---|---|
| Keys | `screens`, `elements`, `textStyles`, `styles` (and `$schema`). Switches like `log`, `perf` or `kupoTextureCache` work **only in `MenuLayout.json`**; an add-on that has one gets a log warning. |
| File name | Any `MenuLayout.<name>.json` is read as an add-on, so don't give other files that pattern (the schema is `MenuLayout_schema.json` for that reason). |
| Order | `MenuLayout.json` first, then add-ons by file name (`MenuLayout.a.json` before `MenuLayout.b.json`). |
| Same `name` | A rule with the **same name** as one in an earlier file **replaces** it, in its place. The whole rule is replaced, so copy every key you want to keep. Log: `[AddOns] elements 'x' from ... replaces the one from ...`. A new name adds a rule. |
| Styles | Every file can use styles from every file. Same style name: the later file wins. |
| Switch off | Rename it so it no longer ends in `.json`, e.g. `MenuLayout.battle.json.off`. |
| Broken add-on | Skipped with a warning (`[AddOns] ... unreadable`); `MenuLayout.json` and other add-ons still load. A bad rule skips only that rule. |
| Live | Saving, adding or renaming an add-on applies straight away. The `[MenuLayout] read ...` log line lists the files it read. |

---

## `textStyles`: every text of a font type

Style for **all** texts of one font type (`Font01`–`Font10`, the same types as KupoUI's `fontconfig.json`). KupoUI still picks the font and size; this adds the style on top.

```json
"textStyles": [
  { "fontType": ["Font01", "Font07"], "fontStyle": "Bold", "outline": { "color": "#000000", "distance": 2 } },
  { "fontType": "Font05", "shadow": false }
]
```

| Key | Meaning |
|---|---|
| `fontType` | One type, or a list. Which text has which type: F8 dump, `fontType=` |
| `fontStyle`, `lineSpacing`, `alignment`, `shadow`, `outline` | As in `elements` (above) |

- If an `elements` rule sets the same thing on a text, the `elements` rule wins. `textStyles` never changes the font, so an element's `font` always stays.
- A wrong value (e.g. a misspelt `fontStyle`) skips only that entry; the log names it (`[Elements] entry '…' skipped` / `[TextStyles] entry skipped`).
- Texts without a font type (F8 dump shows no `fontType=`) aren't affected; use `elements` for them.
- Saves apply live; deleting a style puts the game's values back.

---

## `renderHeights`: battle resolution

The game draws battle into a small image (384×216) and enlarges it, which makes high-res sprites blocky. This sets the height of that image.

| Camera | What it draws | Game's own size | Value in use |
|---|---|---|---|
| `CameraBattle` | Battle characters, enemies | 216 | `1440` |
| `CameraBattlebackGround` | Battle background | 256 | `1707` |
| `CameraFieldMain`, `CameraTileMap` | Field / overworld | 180 | `0` (off) |

- `0` = the game's own size.
- Values are px at 1440p. `1440` = one image pixel per screen pixel.

---

## `battle`: the battle screen

```json
"battle": { "fitBackground": true, "spritesDown": 170, "depthScale": 90,
            "groundShadow": { "azimuth": 110, "elevation": 40, "opacity": 0.75 } }
```

All keys are live (save the file, the next frame uses them), except where noted.

**Background and placement**

| Key | Meaning |
|---|---|
| `fitBackground` | `true` = show the background's full height (the game cuts off its top and bottom) |
| `spritesDown` | Move party, enemies and effects down by this many px; the background stays. `0` = off. Cursors over the sprites: give their `elements` rules `"addSpritesDown": true` |
| `spriteCameras` | Which cameras `spritesDown` moves. Default `CameraBattle`, `CameraEffect1`–`4` (see the F8 dump's camera rows) |
| `partyRight` / `partyDown` | Move every party member (sprite and shadow) by this many px; negative = left / up. Adds to `spritesDown`. Needs `hdPixelsPerUnit` on |
| `partySlots` | One `[right, down]` pair per party slot, px, added to `partyRight` / `partyDown`. Slot 1 = top of the screen (order at battle start; the log prints `[Battle] party slot n`). Example `[[0, 0], [10, -5], [0, 0], [0, 0]]`. Changing it mid-battle can misplace the shadows until the next battle |
| `backRowShift` | Extra px added to the game's back-row distance; negative = closer to the front row. Eases in with the game's own slide. `0` = game's |
| `enemyOnTop` | Enemy texture names (prefix, e.g. `["MN_FF5_127"]`) drawn above the other enemies where they overlap |
| `resolutionScale` | Test setting: leave at `0` |

> Cursors that follow a party member (active-character arrow, target hand) move with that member's `partyRight` / `partyDown` / `partySlots` / `backRowShift` offset when their rule has `addSpritesDown`.

**HD sprite size and depth look**

| Key | Meaning |
|---|---|
| `customScale` | On-screen size of replaced party sprites that are drawn at the game's pixel density (texture up to 2x the game's, e.g. 64 px PSP-style sets): `1.5` = 50% larger, feet kept. Default `1` = game size. Keep the files at their real size; this setting does the enlarging. HD art keeps `hdPixelsPerUnit`. Needs `hdPixelsPerUnit` on |
| `miniScale` | **Unverified, not yet tested in-game.** Mini status: the member keeps its normal job sprites, shown this much smaller (`0.5` = half size, feet kept). No Mini art needed. Default `0.5`; `0` = off (the game's own Mini sprites). The log prints `[Mini]` lines. Needs `hdPixelsPerUnit` on |
| `hdPixelsPerUnit` | Size of HD battle art: texture px per battle unit. Default `6.667` = a 248 px image shows 248 px tall at 1440p, feet kept in place. `0` = off. Only textures much larger than the game's are resized, so original-art poses keep their size. HD enemies get the same sizing (feet and centre kept). See `ART_GUIDE.md` |
| `depthScale` | % smaller a sprite is with its feet at the top of the battle view than at the bottom, linear in between (party, enemies, shadows; feet stay put). `0` = off, max `90`. The log prints each sprite's factor (`[Battle] depthScale`) |

> Weapons follow their member (size, place, offsets). The active-character arrow and the target hand follow each sprite's real size (HD art × depth): the arrow sits above the head, the hand points at the figure's left edge. Fine-tune with the rules' `moveRight` / `moveDown`.

**`groundShadow`: 3D shadows on the ground**

Each party member and enemy casts a shadow onto the ground, computed on the GPU from the sprite's shape and (optional) depth map. Needs `shaders\ff5shadow.bundle` beside `MenuLayout.json`.

| Key | Meaning | Default in the pack |
|---|---|---|
| `on` | `false` = off | `true` |
| `azimuth` | Screen direction the shadow falls, degrees: 0 right, 90 up/away, 180 left, 270 down/toward the camera | `110` |
| `elevation` | Light height, degrees: low = long shadows, 90 = overhead | `40` |
| `opacity` | Darkness, 0–1 | `0.75` |
| `groundTilt` | Angle the ground is seen at; also sets how deep bodies are. `0.5` matches the HD sprites | `0.5` |
| `thickness` | Rounded depth of each part from its outline, share of the sprite width. Sprites without a depth map get only this | `0.15` |
| `depthRange` | Fallback depth scale (share of the width) when the sole fit fails, and the depth used for flyers | `0.6` |
| `blur` | Soft edge, px at 1440p | `4` |
| `falloff` | 0–1: how much lighter the far end is than at the feet | `0.75` |
| `hideGameShadows` | `true` = hide the game's round shadows, party and enemies, in every PR game (flyers' round shadows are always hidden) | `false` |
| `debug` | `true` = show the shadow buffer over the battle (bodies grey by depth, red = shadow) | `false` |

- **Depth maps** (optional, much better shadows): `<sprite>.depth.png` beside the sprite's PNG. How to make them: `ART_GUIDE.md`.
- The depth scale is solved per sprite so **both soles touch the ground**.
- **Flyers and Float:** the empty space down to the base of the game's round shadow counts as height. The shadow gap is 2 × that gap, so the shadow lands 2 × gap ÷ tan(elevation) away.
- Log: each sprite's depth map, and once per battle its ground fit (`[GpuShadow] ground fit`).
- Tuning without the game: the shadow simulator (`source\tools\shadow_sim` in the pack).

---

## Battle backgrounds

| Step | Details |
|---|---|
| **Put the image in** | `Modules\00-Mods\SpriteMod HD\FF5\battle_backgrounds\` |
| **Name** | Same as the game's: `BG_FF5_07.png` (KupoUI loads it by name) |
| **Size** | Any size larger than the original 512×256, same 2:1 shape (e.g. 2048×1024). The plugin scales it to the game's background size. |
| **When it applies** | Next battle that uses it (KupoUI reads the folder at startup, so restart for new files) |

- `battle.fitBackground` shows the full height instead of the game's cropped view.
- Log: `[Render] background 'BG_FF5_07...': ... scale ...` when the plugin resizes one.
- Animated parts (torches, water): HD frames go in `battle_backgrounds\animtex\`; guide in that folder's `README.md`.
- More on art sizes and file names: `ART_GUIDE.md`.

---

## Character sprites

| Step | Details |
|---|---|
| **Put the image in** | `Modules\00-Mods\SpriteMod HD\FF5\GameAssets\Serial\Res\Chara\Battle\BC_FF5_P001_J00\Default_00.png` (folder = character + job, as in the game) |
| **Size file** | `Default_00.json` beside it: `{ "pixelsPerUnit": 6.667 }` = a 248 px image shows 248 px tall at 1440p |
| **When it applies** | After a restart (KupoUI reads the folder at startup) |

- KupoUI matches these **by folder only while at least two characters share the file name** `Default_00.png`. With just one, it matches by name and changes every character.
- Sizes, depth maps and enemies: `ART_GUIDE.md`.

---

## Map files

Files beside `MenuLayout.json` that the map features read. Any PR game; the map tile tools (`source\tools\map_tiles\README.md` in the download) write most of them. `<Map>` = the map name from an F8 dump, e.g. `Map_20061_2`. New or changed files apply on the next map load.

| File | What it is | Made by |
|---|---|---|
| `map_tiles\<sheet>.hd.png` | HD tile sheet, same layout as the game's sheet (any whole multiple of its size) | map tile tools, or by hand (`ART_GUIDE.md`) |
| `map_tiles\anim\anim.json` | tile animations: `[{ "sheet", "tid", "frames": "<png>", "count", "ms" }]`; the PNG holds `count` frames of one tile side by side, stored upside down like the sheet. Speed: `fieldLavaAnimSpeed` | by hand |
| `GameAssets\Serial\Res\Map\<map package>\<Map>\tilemap.json` | edited map layout, the game's own format; replaces the tile cells (walking, events and colours stay the game's) | map tile tools (Tiled import) |
| `map_objects\<Map>.json` + atlas PNG | tile objects placed in Tiled, drawn at their exact position, animated, with the layer's blend mode and opacity | map tile tools (Tiled import) |
| `map_shadows\<Map>.png` | shadow mask: black, alpha = shadow, 32 px per map cell (map width x 32 by height x 32 px), top row = map row 0; leave it empty where objects, upper or ceiling tiles cover the floor. Tiled's File > Export As Image gives a base image of the same size. `<Map>.decor.png` (optional) = the same shadow on see-through floor tiles (stools, rug edges), drawn after them. `fieldMapShadows` | by hand |
| `map_shadows\<Map>.lava.json` | lava layer: `{ "width", "height" (cells), "frames": "<png>", "count", "ms", "cells": [[x, y], ...], "edge": "lava_edge.png" }`; frames are 32 px, side by side. `fieldLavaOpacity`, `fieldLavaBlend`, `fieldLavaEdge` | by hand |
| `effects\<effect>\flame_*.png` | deprecated: upright flame frames for a map fire effect (`EF_...` name from the F8 dump), 2 px per game pixel. `fieldFlames` | by hand |
| `effects\<effect>\flame.json` | optional: `"frameMs"` (default 133), `"offset": [x, y]` (game pixels, y up), `"scale"`, `"hideFire": true` (no frames: only hides the game's fire), `"quadMode"` (blend mode of the effect on the map, e.g. `"overlay"`) | by hand |

---

## Loading animation

### Add your frames

1. Open `Modules\00-Mods\SpriteMod HD\FF5\loading_icon\`.
2. Copy your frames in, named in play order: `00.png`, `01.png`, `02.png`, ... Square images, any size (e.g. 256x256).
3. Wait 2 seconds. The next loading screen uses them. No restart.

To go back to the game's chocobo: delete the PNGs from the folder.

### Change the speed

In `MenuLayout.json`, find `"loadingIcon"` and change the number after `"frameTime":`. It is the seconds each frame shows.

| Want | Type |
|---|---|
| 8 frames per second | `"frameTime": 0.125` |
| 4 frames per second (game's speed) | `"frameTime": 0.25` |
| 12 frames per second | `"frameTime": 0.083` |

### Change the size on screen

In `MenuLayout.json`, find `"loadingIcon"` and change the number after `"size":`.

| Want | Type |
|---|---|
| Same size as the game's icon (133) | `"size": 0` |
| Icon 200 pixels tall on a 2560x1440 screen | `"size": 200` |

- The number is the height in pixels **as on a 2560x1440 screen**. On other screens it is scaled to match, so `200` is 150 pixels on a 1920x1080 screen.
- The game's icon is **133** pixels tall on a 2560x1440 screen. A bigger number makes it bigger, e.g. `"size": 256` is almost twice as tall.
- Save the file. The change shows on the next loading screen.

### Smoother animation while loading

The animation stutters when the game spends long frames loading. `"priority"` in `"loadingIcon"` limits how much background loading the game does per frame **while the icon shows** (Unity's `backgroundLoadingPriority`); the game's own value comes back afterwards.

| `"priority"` | Loading work per frame | Effect |
|---|---|---|
| `"High"` | up to ~50 ms | fastest loading, most stutter |
| `"Normal"` | ~10 ms | |
| `"BelowNormal"` | ~4 ms | smoother, loads a bit longer |
| `"Low"` | ~2 ms | smoothest, longest loading |
| `""` or no key | — | game's own setting |

- The log says once which value the game uses (`[Loading] background loading priority: game …`).
- Only pauses from *background* loading get shorter; loading the game does all at once still freezes the screen. *(How much it helps: untested.)*

---

## Title menu highlight

The selected title-menu item (main list, Extras, Options) fades to a colour and grows a little. Selected = the item under the game's cursor, or the item the mouse moves onto; the last one you used wins.

1. Open `MenuLayout.json`.
2. Find `"titleHighlight"`.
3. Change a value from the table. Save; it shows at once.

| Key | Type | Example effect |
|---|---|---|
| `color` | `"#RRGGBB"` | `"#FFE08A"` = pale gold, `"#8AD8FF"` = pale blue |
| `scale` | number | `1.1` = 10% larger, `1` = no growth |
| `fadeTime` | seconds | `0.15` = quick fade, `0` = instant |

- To turn it off: delete the whole `"titleHighlight": { ... },` block.
- The cursor arrow keeps its size: it is shrunk by the same amount.

## Screen dump (F8)

Writes `_dumps\screen_<date>_<time>.tsv` next to `MenuLayout.json`: one row per visible object.

| Column | What it shows |
|---|---|
| `path` | Full object path. Use the end of it for `path`, a middle part for `within`. |
| `components` | What the object is: `Text`, `Image`, `HorizontalLayoutGroup`, ... `(off)` = switched off |
| `box1440` | Where it is on a 1440p screen: `x left-right y top-bottom` |
| `details` | Text content, font, size, style, colour, `fontType`, shadow/outline, `scale` (when not 1), sprite name, list spacing; for a canvas: its scale and the `scaler` line (reference resolution, match mode) |

- `RULE='name'` in `details` = the element rule that owns this object. No `RULE` on the object you meant = the rule didn't match it.
- Cameras get their own rows (`Camera`): position, orthographic size, render target.
- The key only works while the game window has focus.
- Battle scene sprites (characters, enemies) show their world position instead of a screen box.

---

## Typos: key warnings and the schema

A key the plugin doesn't know is ignored, so a typo used to do nothing without a trace. Now:

| Where | What you get |
|---|---|
| **Log** | One warning per unknown key, every time the file is saved: `[Keys] MenuLayout.json element 'item names': unknown key 'fontsclae' (did you mean 'fontScale'?); ignored`. Wrong capitals: `(capitals matter: 'fontScale')`. Checked: top level, `screens`, `elements`, `styles`, `textStyles` and their blocks (`selected`, `greyed`, `shadow`, `outline`, `backdrop`, `hide`, `battle`, `loadingIcon`, `titleHighlight`, `cleanup`). |
| **VS Code** | `MenuLayout_schema.json` sits next to this file, and `"$schema"` at the top of `MenuLayout.json` points to it. VS Code then suggests keys as you type (Ctrl+Space), shows a short description on hover, and underlines unknown keys, wrong types and bad colours in red. For an add-on, put the same `"$schema": "./MenuLayout_schema.json"` line at its top. |
| **Without the game** | `python check_menulayout.py` (pack: `source\tools\`) checks `MenuLayout.json` and every add-on against the schema. |

> Keys starting with `_` are notes and are never warned about, so headings and comments are fine.
> The warning doesn't block anything: the rest of the rule still applies.

---

## A rule does nothing?

| Check | How |
|---|---|
| Did the file load? | Log: `[MenuLayout] read N rule(s), M element rule(s)`. A `rules file unreadable` line names the broken line. |
| Typo in a key? | Log: `[Keys] ... unknown key`. It names the closest real key. |
| Did the rule match? | Press F8 and look for `RULE='your rule name'` on the object. Or set `"log": true`: Each matched object logs `[Elements] 'your rule name': ...`. No line = the path or sprite name doesn't fit. |
| Matches too much? | Add `within` with a part of the screen's path. |
| Right path? | Press F8 on that screen and search the dump. |
