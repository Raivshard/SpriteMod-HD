# Building from source

## Folders

| Folder | What | Needs |
|---|---|---|
| `plugin\` | The BepInEx plugin `FF5MenuSprites.dll` (C#, net6.0) | .NET SDK 6 or later; the game with BepInEx 6 IL2CPP installed and started once |
| `shaders\UnityProject\` | Ground-shadow shaders → `ff5shadow.bundle` | Unity **2019.4.26f1** exactly (the game's version) |
| `tools\` | Helper tools (below) | Python 3 with Pillow; `UnityPy` for the bundle readers |
| `tools\il2cpp_dump\` | Lists types, methods and fields of the game's interop DLLs | .NET SDK |

## Plugin

```
dotnet build plugin -c Release -p:Game="C:\Program Files (x86)\Steam\steamapps\common\FINAL FANTASY V PR"
```

- `Game` = your game folder. The project references `BepInEx\core\*.dll` and `BepInEx\interop\*.dll` from it (not copied, not shipped).
- Output: `plugin\bin\Release\net6.0\FF5MenuSprites.dll` → copy to `<game>\BepInEx\plugins\` with the game closed.
- The version is `Plugin.Version` in `Plugin.cs`.

## Shader bundle

```
powershell -File shaders\build_bundle.ps1 -Unity "C:\Program Files\Unity\Hub\Editor\2019.4.26f1\Editor\Unity.exe"
```

Builds `shaders\out\ff5shadow.bundle` in batch mode (log: `shaders\build_bundle.log`). Copy it to `<game>\Modules\00-Mods\SpriteMod HD\FF5\shaders\`.
**Shader pass index = the pass's order in the file.** `ShadowCast.shader`: 0 cast, 1 composite, 2 fit, 3 blur. Add new passes at the end.

## Tools

Set the game folder once per terminal if it isn't the Steam default: `set FF5_GAME=D:\Games\FINAL FANTASY V PR`.

| Tool | Use |
|---|---|
| `check_menulayout.py [files]` | Check `MenuLayout.json` and add-ons against the schema without starting the game |
| `json_fold_format.py [--write] files` | Reformat hand-edited JSON so editor folding shows each rule's name line; refuses if data would change |
| `shadow_sim\shadow_sim.py` | Ground-shadow simulator: same model as the shaders, on a sprite and its depth map. README in its folder. Output: `SHADOW_SIM_OUT` or `data\shadow_sim\` |
| `animtex_fit.py` | Place HD frames of animated backgrounds: cuts frames from an upscaled sheet (crop only), finds each on the HD background, writes `<BG>_place.json` |
| `animtex_frames.py` | Cut the game's original frames from a background sheet (templates for artists) |
| `bg_prefab_dump.py` | Print a battle background prefab's pieces, animators and effects from the game's bundle (UnityPy, read-only) |
| `rename_font.py` | Make a font copy with its own family name (see `FONTS.md`) |
| `il2cpp_dump` | `dotnet run --project tools\il2cpp_dump -- <interop dll> <Type> [--member name] [--strings regex]` |

All tools read game files only; none write into the game's own data.

## Working on the plugin

- Read `docs\TECHNICAL_NOTES.md` first, especially "Broken or missing in this game's interop".
- Check every game type and method against the interop dump before using it.
- Patch bodies are wrapped in try/catch and log with a `[Prefix]`; a bad file must log a warning, never crash the game.
- Config keys: the parser (`Plugin.cs`), the key lists (`Keys.cs`, for typo warnings) and `MenuLayout_schema.json` must stay in step.
