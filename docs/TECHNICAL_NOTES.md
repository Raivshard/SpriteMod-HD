# Technical notes: FF5 Pixel Remaster internals

What the plugin relies on, for developers. Labels: **[verified]** = seen in game data, the interop dump, logs or screenshots; **[inferred]** = deduced, not directly confirmed. Pixel figures are at 2560x1440; the game is resolution-independent, so work in units and ratios.

## Engine and loaders

- Unity 2019.4.26f1, IL2CPP, Direct3D 11. BepInEx 6 IL2CPP with Il2CppInterop. [verified]
- Game types live mostly in `Assembly-CSharp` under `Last.*` (`Last.UI`, `Last.Battle`, `Last.Systems`). Look them up in `BepInEx\interop` with `tools\il2cpp_dump`. [verified]
- **KupoUI** reads `Modules\` in the game root and swaps game images for PNGs **by file name**. When two files share a name (e.g. every character's `Default_00.png`), it matches by the asset path under `GameAssets\...` instead. Two mods with the same texture name conflict and neither loads (`Texture conflict detected`). [verified]
- KupoUI reads `<texture>.json` anywhere under `Modules\` as that texture's metadata (`pixelsPerUnit`, `filterMode`, …). Don't name other JSON files after a texture. [verified]
- KupoUI's `ObjectConfig.json` is read only at game start. [verified]
- Magicite packs (`StreamingAssets\Magicite\<pack>`): KupoUI wins over Magicite for sprites; between Magicite packs the folder name sorting first wins. [verified]

## Display metrics

- 1 unit = 1 original pixel; sprites are PPU 1. [verified]
- **Battle:** 216 units tall → 6.67 screen px per unit at 1440p. A 32x32 character cell = 213 px. Party layout (units): front row x 68, back row x 84, base y 43, rows 25 apart. [verified at 16:9]
- **Overworld:** 8 px per unit at 1440p (16 px frame = 128 px). [verified]
- **Menus:** each character is a UI `Image` in a fixed 160x160 canvas-unit box, bottom-centre pivot, stretched to fill it. Canvas scale = screen height / 1080 (CanvasScaler ScaleWithScreenSize, reference 1920x1080, match height). [verified]
- **Loading icon:** `Last.Systems.Indicator.SystemIndicator.loadingIcon`, 100x100 units = 133 px at 1440p, 4 frames at 0.25 s. [verified]

## Battle rendering

- Battle is drawn into small render textures and enlarged: `CameraBattle` → 'Battle' 384x216, `CameraBattlebackGround` → 512x256, `CameraEffect1-4` → 640x320, all Point-filtered. Field: `CameraTileMap` / `CameraFieldMain` → 320x180. That is why HD sprites look blocky. Resizing these textures in place fixes it (`renderHeights`). [verified]
- The game draws every texture at half size (`QualitySettings.masterTextureLimit` = 1). [verified]
- The background is shown through material `Last/LastSpriteMaster` with `_MosaicWidth/_MosaicHeight/_MosaicBlock` = 512/256/512, a mosaic grid at the original size; the plugin scales them with the render size. Don't scale the shared `LastSpriteMaterial` asset, only `(Instance)` copies. [verified]
- Party members are MeshRenderers (`RootObject/Entity/PlayerParty/BattlePlayer(Clone)/CharaObject/Mesh`) on a 32x33-unit mesh. KupoUI's sidecar size doesn't apply to them, so HD art is squeezed into 32 units until the plugin rescales the mesh each frame (the game resets scale to 1 every frame before LateUpdate). [verified]
- With `fitBackground`, `CameraBattlebackGround` (depth -5) renders 3414x1707 straight to the screen; `pixelRect` reports 0,0 but the screen shows the **middle** of that picture. `CameraBattle` (depth 0) renders to 'Battle', clearing depth only. [verified]
- Drawing into `CameraBattle`'s target from its own command buffers before `AfterEverything` gets overwritten. What works: run passes on `CameraBattlebackGround` at `AfterEverything` with `CameraBattle`'s matrices (`SetViewProjectionMatrices`). [verified]
- The game unloads unused assets after each battle: runtime Materials, RenderTextures and Texture2Ds held only by plugin code are destroyed. Set `HideFlags.DontUnloadUnusedAsset` on them. [verified]

## Broken or missing in this game's interop

Some managed wrappers call methods stripped from the game and throw `MissingMethodException` on first call. The dump doesn't show this: treat constructors, implicit operators and argument-checking wrappers as suspect, and prefer the native `Internal_*` / `*_Impl` method. [verified, each cost a test]

| API | Problem | Work-around |
|---|---|---|
| `RenderTargetIdentifier` constructors, `op_Implicit(Texture)` | Stripped | Fill the public fields by hand (`GpuShadow.Rt`) |
| `CommandBuffer.DrawRenderer` | Throws (String.Format missing) | Call `Internal_DrawRenderer(Renderer, Material, int, int)` through a delegate (`GpuShadow.Draw`) |
| `CommandBuffer.SetGlobalTexture` | No public overload | One material per draw with its own texture |

Also: shader pass index = the pass's order in the `.shader` file, not its label.

## Battle backgrounds

- Original files are 512x256 with the painted area at x 20-492, y 12-244 inside a black frame. [verified]
- Animation is data in each background prefab: Animators whose clips swap the sprite on fixed SpriteRenderers (sheet `<BG>_Animtex`, or `BG_FF5_18_A`). No clip moves a piece. Prefab units = original px, background centre = (0,0). Background 31 scrolls a material on a mesh instead. [verified, prefab dumps]
- A larger `_Animtex` sheet through KupoUI is copied into the original texture, but the pieces keep their original rects, so they show the wrong areas. Hence per-frame HD files. [verified]

## Field maps and tiles

- Maps are **Tiled** JSON (`tilemap.json`, Tiled 1.3.5): a group with tile layers `BottomLayer`, `GroundLayer`, `UpperLayer`, `CeilLayer`, one tile number per cell; embedded tilesets with `firstgid`; 16 px tiles on 512x512 sheets `map_chip_NNN.png` (32 x 32 tiles). Tile animations are per tile in the tileset's `tiles` list. [verified, export]
- Walkability is **per map cell** in `collision.json` (`GroundCollision`, `UpperCollision`, `BottomCollision`), not per tile; new tile art never changes where you can walk. [verified]
- Each map layer is a mesh (`MapLayerMesh`) whose material draws one sheet; UVs come from `MapTileGroupData.TexUVTable`. The same sheet is drawn by `MainViewMapRenderer`, `BirdViewMapRenderer`, `WaterViewMapRenderer` and `MinimapViewMapRenderer` under `RootObject/RootFiled/MapRenderer`. [verified, interop dump + log]
- A larger sheet loaded by the game (Magicite, or KupoUI by file name) does not display correctly; the plugin lets the game load its own sheet and then sets the material texture, so any size with the same layout works. KupoUI loads any `<sheet>.png` under `Modules\` by name, hence the plugin's `.hd.png` names. [verified]
- 23 sheet names have different art in different map packages (e.g. `map_chip_063`, 8 versions). The current package is the `Map_NNNNN` child of the `RootObject` that holds `RootFiled/MapRenderer`; `GameObject.Find("RootObject")` can return a different object. [verified]
- 439 sheet versions hold 31,648 distinct tile images; 12,875 are placed on maps or are animation frames. [verified, counted]

## Field characters

- Field characters are `SpriteRenderer`s whose frames (`Default_00`, `WalkL_01`, ...) are cut from one atlas per character (`sactx-...-MO_FF5_P001_C00-...`). KupoUI looks sprites up by name only, and battle sets use the same names, so it cannot swap them; the plugin hooks `SpriteRenderer.sprite` and finds the folder from the atlas name. [verified]
- Walks are 2 frames (`_01` then `_00`), 0.10 s each in every direction; `_00` is also the frame shown at rest. Right-facing walks are the left frames flipped. [verified, `anim_info.json` + log]
- `anim_info.json` frames carry `offsetx`/`offsety` in game pixels; the only walk offset is `WalkL_01` `offsety` 1.0 (all five party members). The game applies them in `SpriteAnimLastFieldEntity.ReflectFrameData(Int32)`; the plugin reads them there and cancels them for replaced frames. [verified]
- The game cannot make one walk frame longer without shortening the next, so for walks listed in `fieldStrideDelay` the plugin runs its own frame clock and uses the game's frame changes only as "still walking" and for the direction. [verified in-game]
- Draw order on field maps: character body queue 2455, head 2457 (its shader paints the ceiling layer's picture over the head), shadow 2452; map tiles opaque 2450 (write depth), transparent 2456 (no depth write); effect quads (fire) 2458 and write depth. Layer depths: ceiling 50, upper 150, ground 250, bottom 350, plus 2 units per map row; a character sits 1-1.5 units nearer than its own row's ground. [verified, F8 + shader state]
- Map tile meshes are a fixed grid of slots that follows the camera; the game gives slots other tile pictures as the view scrolls (empty slots use uv 0,0), so a slot is not a map cell. Changes to a tile must follow its picture, not its slot. [verified]
- `tilemap.json` (Tiled) numbers a sheet's rows from the bottom. [verified]
- Fire over taller heads: the game sets the effect's depth back every frame, so the plugin moves it behind the characters right before drawing (`Canvas.willRenderCanvases`), only while a character stands in front of it. [verified in-game]
- Ceiling signs: Karnak has two sign boards in the ceiling layer. `fieldCeilingSigns` blanks their slots (uv set to the transparent corner texel the game uses for empty slots) and draws them from its own mesh at ground depth - 2.25, on layer 23 (drawn by the tile camera, not by the ceiling-picture camera). [verified in-game]

## Save list

- Each save slot stores a full-screen screenshot as base64 PNG text; `Last.UI.KeyInput.SaveContentController.SetScreenShot(String)` decodes it on the main thread (~60 ms each at 2560x1440). `fastSaveList` holds that call back, decodes on a worker thread, then replays the call and hands the ready pixels to its `LoadImage`. [verified]

## UI

- Element paths: the F8 dump prints them. `(Clone)` suffixes vary between screens; the plugin ignores them. [verified]
- Text font types (`Last.UI.FontSelector.type`, `Font01`-`Font10`) are KupoUI's font slots: Font01 main UI text, Font03 names and headers, Font05 numbers, Font06 button-glyph letters. [verified from UI prefabs]
- Layout changes must be applied after Unity's layout pass: the plugin hooks `Canvas.willRenderCanvases` after `CanvasUpdateRegistry` subscribes, and rebuilds a text's mesh (`Graphic.Rebuild(PreRender)`) after resizing it. [verified]
- KupoUI patches the Text `fontSize` and `color` setters for texts in its fonts; the plugin uses its own copy of the font where it needs a different size. [inferred from KupoUI's code and tests]
- `blocksRaycasts` doesn't stop menu selection: the game reads the pointer itself. [verified]
