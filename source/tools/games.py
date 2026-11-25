r"""Which Pixel Remaster game a tool works on, and its paths (from tools\games.json).

Choose the game: --game FF6 on the command line (removed from sys.argv on import, so a tool's own arguments are
unaffected), else the environment variable SPRITEMOD_GAME, else "default" in games.json. FF5_GAME (an older
variable holding the FF5 install folder) still overrides FF5's folder.

    import sys; sys.path.insert(0, <tools folder>)
    from games import GAME
    GAME.root, GAME.streaming, GAME.export, GAME.mod, GAME.exe, GAME.code
"""
import json, os, sys
from pathlib import Path

_CFG = json.loads((Path(__file__).resolve().parent / "games.json").read_text(encoding="utf-8"))


class Game:
    def __init__(self, code):
        code = code.upper()
        if code not in _CFG["games"]:
            sys.exit(f"unknown game '{code}'; known: {', '.join(_CFG['games'])} (add it to tools\\games.json)")
        g = _CFG["games"][code]
        self.code = code
        self.exe = g["exe"]                                   # process name without .exe
        root = os.environ.get("FF5_GAME") if code == "FF5" else None
        self.root = Path(root or Path(_CFG["steam"]) / g["folder"])
        self.data = self.root / (self.exe + "_Data")
        self.streaming = self.data / "StreamingAssets"
        self.export = self.streaming / "MagiciteExport"       # vanilla export (read-only)
        self.magicite = self.streaming / "Magicite"           # Magicite packs
        self.aa = self.streaming / "aa" / "StandaloneWindows64"
        self.modules = self.root / "Modules"
        self.mod = self.modules / "00-Mods" / "SpriteMod HD" / code   # the plugin's folder (MenuLayout.json)
        self.plugins = self.root / "BepInEx" / "plugins"
        self.log = self.root / "BepInEx" / "LogOutput.log"

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
        print(f"{c}: {g.root}  exists={g.root.exists()}  export={g.export.exists()}  mod={g.mod.exists()}")
