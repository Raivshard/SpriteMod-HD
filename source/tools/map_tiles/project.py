r"""Map project: one folder per area or mod with a project.json; every map tool reads its paths and lists from it.

Which project: --project <folder> (removed from sys.argv), else the folder holding project.json above any .tmj
argument (the Tiled commands pass the map file), else the Karnak project (Working\tilesets\Karnak).
The project's "game" picks the game for every tool in the run (sets SPRITEMOD_GAME unless --game is given), so
import this module before games / layouts / pair_maps.

project.json (paths relative to the project folder; all optional except "game"):
  "game"        FF1 / FF5 / FF6 ... (tools\games.json)
  "name"        shown in messages
  "maps"        the maps worked on, e.g. ["Map_20060", "Map_20061_2"] (new_project.py exports them to Tiled)
  "sheets"      the sheets the build installs (default: the sheets those maps use)
  "scale"       HD tile size = 16 x scale px (default 2 = 32 px, the Tiled tile size)
  "mobile"      true = the FF5 mobile-art pipeline (votes, decisions, write_mobile_replacements); Karnak only
  "shadow_maps" maps that get map shadows (mobile pipeline only)
  "paths"       {"build", "catalog", "choices", "added", "layouts", "objects", "tiled", "installed"}
Defaults (a new project): inside the project folder: choices.csv, "my tiles"\, layouts\, objects\, tiled\,
installed\, build output = the project folder (overrides\, pack\). The game's tile catalogue is shared:
data\catalogs\FF5 (FF5), data\catalogs\<game> (other games).
"""
import json, os, sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
PROJECT = HERE.parents[1]                     # the mod's project folder (tools\, data\)
sys.path.insert(0, str(HERE.parent))
import paths                                  # project folder layout (tools\paths.py)
KARNAK = paths.KARNAK
DEFAULTS = {"build": ".", "catalog": "catalog", "choices": "choices.csv", "added": "my tiles", "layouts": "layouts",
            "objects": "objects", "tiled": "tiled", "installed": "installed", "shadow_masks": "shadow_masks"}


class Project:
    def __init__(self, folder):
        self.folder = Path(folder).resolve()
        f = self.folder / "project.json"
        if f.exists():
            j = json.loads(f.read_text(encoding="utf-8"))
        elif self.folder == KARNAK.resolve():
            # no Karnak project here (e.g. the release's tools): the default game, default folders
            g = json.loads((HERE.parent / "games.json").read_text(encoding="utf-8"))["default"]
            j = {"game": os.environ.get("SPRITEMOD_GAME") or g, "name": "default"}
        else:
            sys.exit(f"no project.json in {self.folder} (make one with new_project.py)")
        self.json = j
        self.game = j["game"].upper()
        self.name = j.get("name", self.folder.name)
        self.maps = j.get("maps", [])
        self.scale = int(j.get("scale", 2))
        self.mobile = bool(j.get("mobile"))
        self.shadow_maps = j.get("shadow_maps", [])
        p = dict(DEFAULTS, **j.get("paths", {}))
        for k, v in p.items():
            setattr(self, k, (self.folder / v).resolve())
        if "catalog" not in j.get("paths", {}):
            # the game's tile catalogue is shared by all projects of a game (map_tile_catalog.py builds it)
            self.catalog = paths.catalog(self.game)
        self._sheets = j.get("sheets")

    @property
    def sheets(self):
        """sheets the build installs: project.json "sheets", else every sheet the project's maps use"""
        if self._sheets:
            return list(self._sheets)
        from layouts import cell_layer_tiles
        return sorted({s for m in self.maps for d in cell_layer_tiles(m).values() for s, _ in d.values() if s})

    def __repr__(self):
        return f"{self.name} ({self.game}, {self.folder})"


def _find():
    a = sys.argv
    if "--project" in a:
        i = a.index("--project")
        if i + 1 >= len(a):
            sys.exit("--project needs a folder")
        folder = a[i + 1]
        del a[i:i + 2]
        return Project(folder)
    for x in a[1:]:
        if x.lower().endswith(".tmj"):
            for d in Path(x).resolve().parents:
                if (d / "project.json").exists():
                    return Project(d)
    return Project(os.environ.get("SPRITEMOD_PROJECT") or KARNAK)


P = _find()
if "--game" not in sys.argv and not os.environ.get("SPRITEMOD_GAME"):
    os.environ["SPRITEMOD_GAME"] = P.game     # games.py reads it on import; subprocesses inherit it
os.environ["SPRITEMOD_PROJECT"] = str(P.folder)   # subprocesses of build_install use the same project
