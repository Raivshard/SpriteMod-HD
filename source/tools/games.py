r"""Which Pixel Remaster game a tool works on, and its paths (from tools\games.json).

Choose the game: --game FF6 on the command line (removed from sys.argv on import, so a tool's own arguments are
unaffected), else the environment variable SPRITEMOD_GAME, else "default" in games.json. FF5_GAME (an older
variable holding the FF5 install folder) still overrides FF5's folder.

Finding the game folder (first that holds <exe>.exe): FF5_GAME (FF5 only); "steam" in games.json as the folder
holding the game folders; "steam" as the game folder itself; every Steam library (Steam's own list,
steamapps\libraryfolders.vdf). So "steam" may stay as it is when the games are in a Steam library.
The export folder follows Magicite's own setting ("Export Directory" in BepInEx\config\silvris.magicite.cfg).

    import sys; sys.path.insert(0, <tools folder>)
    from games import GAME
    GAME.root, GAME.streaming, GAME.export, GAME.mod, GAME.exe, GAME.code
    GAME.require_export()   # stops with a message that says what is missing and how to fix it

Check the paths: python tools\games.py
"""
import json, os, re, sys
from pathlib import Path

_JSON = Path(__file__).resolve().parent / "games.json"


def _load():
    t = _JSON.read_text(encoding="utf-8-sig")
    # a Windows path typed with single backslashes ("D:\Games") is not valid JSON: double them first
    # (only backslashes that are not already doubled and do not escape a quote)
    fixed = re.sub(r'(?<!\\)\\(?![\\"])', r"\\\\", t)
    try:
        return json.loads(fixed)
    except json.JSONDecodeError as e:
        sys.exit(f"tools\\games.json is not valid JSON: {e.msg}, line {e.lineno} column {e.colno}.\n"
                 f"Check the quotes and commas on that line (compare with the file in the download).")


_CFG = _load()


def _steam_libraries():
    """every Steam library's steamapps\\common folder (from the registry and libraryfolders.vdf); [] when not found"""
    roots = []
    try:
        import winreg
        for hive, key, name in ((winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam", "SteamPath"),
                                (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath")):
            try:
                with winreg.OpenKey(hive, key) as k:
                    roots.append(Path(winreg.QueryValueEx(k, name)[0]))
            except OSError:
                pass
    except ImportError:
        pass
    libs = []
    for r in roots:
        libs.append(r)
        vdf = r / "steamapps" / "libraryfolders.vdf"
        try:
            for m in re.finditer(r'"path"\s+"([^"]+)"', vdf.read_text(encoding="utf-8", errors="replace")):
                libs.append(Path(m.group(1).replace("\\\\", "\\")))
        except OSError:
            pass
    out = []
    for l in libs:
        c = l / "steamapps" / "common"
        if c not in out:
            out.append(c)
    return out


def _expand(value, g):
    """Magicite's path placeholders"""
    v = value.strip().strip('"')
    v = v.replace("%StreamingAssets%", str(g.streaming)).replace("%DataPath%", str(g.data))
    # %PersistentData%: Unity's LocalLow folder; the product folder named like the exe is unverified
    v = v.replace("%PersistentData%", str(Path(os.environ.get("USERPROFILE", "~")) / "AppData" / "LocalLow"
                                          / "SQUARE ENIX, Inc_" / g.exe))
    return Path(v)


class Game:
    def __init__(self, code):
        code = code.upper()
        if code not in _CFG["games"]:
            sys.exit(f"unknown game '{code}'; known: {', '.join(_CFG['games'])} (add it to tools\\games.json)")
        g = _CFG["games"][code]
        self.code = code
        self.exe = g["exe"]                                   # process name without .exe
        steam = Path(_CFG.get("steam", "."))
        self.tried = []                                       # folders looked in (for messages)
        cands = []
        if code == "FF5" and os.environ.get("FF5_GAME"):
            cands.append(Path(os.environ["FF5_GAME"]))
        cands += [steam / g["folder"], steam]
        self.root = None
        for c in cands:
            self.tried.append(c)
            if (c / f"{self.exe}.exe").is_file():
                self.root = c
                break
        if self.root is None:
            for lib in _steam_libraries():
                c = lib / g["folder"]
                self.tried.append(c)
                if (c / f"{self.exe}.exe").is_file():
                    self.root = c
                    break
        self.found = self.root is not None
        if not self.found:
            self.root = cands[0]                              # not found: the configured folder (for messages)
        self.data = self.root / (self.exe + "_Data")
        self.streaming = self.data / "StreamingAssets"
        self.magicite_cfg = self.root / "BepInEx" / "config" / "silvris.magicite.cfg"
        self.export = self.streaming / "MagiciteExport"       # vanilla export (read-only); Magicite's setting below
        try:
            m = re.search(r"^\s*Export Directory\s*=\s*(.+?)\s*$", self.magicite_cfg.read_text(encoding="utf-8-sig"), re.M)
            if m and m.group(1):
                self.export = _expand(m.group(1), self)
        except OSError:
            pass
        self.magicite = self.streaming / "Magicite"           # Magicite packs
        self.aa = self.streaming / "aa" / "StandaloneWindows64"
        self.modules = self.root / "Modules"
        self.mod = self.modules / "00-Mods" / "SpriteMod HD" / code   # the plugin's folder (MenuLayout.json)
        self.plugins = self.root / "BepInEx" / "plugins"
        self.log = self.root / "BepInEx" / "LogOutput.log"

    def has_export(self):
        return self.export.is_dir() and any(self.export.glob("map_*"))

    def require_export(self):
        """stop with a message that names what is missing (game folder, Magicite, the export) and how to fix it"""
        if not self.found:
            sys.exit(f"{self.code}: game not found ({self.exe}.exe). Looked in:\n  " + "\n  ".join(map(str, self.tried))
                     + f"\nSet \"steam\" in tools\\games.json to the folder that holds \"{_CFG['games'][self.code]['folder']}\""
                     f" (or to that game folder itself).")
        if not self.has_export():
            how = (f"To make it:\n  1. In {self.magicite_cfg} set  Export Enabled = true\n"
                   f"  2. Start the game and wait until the export is finished (the setting switches itself back to false).\n"
                   f"  3. Run this again.")
            if not self.magicite_cfg.exists():
                how = (f"Magicite is not installed in {self.root} (no {self.magicite_cfg.name}).\n"
                       f"Install BepInEx and Magicite and start the game once. " + how)
            state = "is empty (no map_ folders)" if self.export.is_dir() else "does not exist"
            sys.exit(f"{self.code}: the game's exported files are missing: {self.export} {state}.\n"
                     f"The map tools read the game's maps from Magicite's export. {how}")

    def running(self):
        """True while this game's exe runs (Windows tasklist)."""
        import subprocess
        try:
            out = subprocess.run(["tasklist", "/FI", f"IMAGENAME eq {self.exe}.exe"], capture_output=True, text=True).stdout
            return f"{self.exe}.exe".lower() in out.lower()
        except OSError:
            return False

    def __repr__(self):
        return f"{self.code} ({self.root})"


def _pick():
    code = os.environ.get("SPRITEMOD_GAME") or _CFG["default"]
    if "--game" in sys.argv:
        i = sys.argv.index("--game")
        if i + 1 >= len(sys.argv):
            sys.exit("--game needs a code, e.g. --game FF6")
        code = sys.argv[i + 1]
        del sys.argv[i:i + 2]
    return Game(code)


GAME = _pick()

if __name__ == "__main__":
    for c in _CFG["games"]:
        g = Game(c)
        if not g.found:
            print(f"{c}: not found (looked in {len(g.tried)} folders; first: {g.tried[0]})")
        else:
            print(f"{c}: {g.root}\n    export: {g.export}  {'ok' if g.has_export() else 'MISSING (see the map tools README)'}"
                  f"\n    mod folder: {'ok' if g.mod.exists() else 'missing (install the mod)'}")
