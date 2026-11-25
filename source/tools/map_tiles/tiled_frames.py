r"""Add a hidden layer group "animation frames" to the Tiled working maps (door open frames).
Tile animations (doors: 2 frames of 1 ms, flipped when the door opens; torches, clock, fireplace) show other tile numbers
than the one placed on the map. Per map layer that has animated cells, one layer per extra frame ("<Layer> frame 2",
...; frames in order of first appearance, repeats skipped) holds that frame's built art at each animated cell, from
a separate tileset "frames (current)" (atlas\<map>_frames.png) added after all others. Stamping there sets that frame
tile (tiled_import.py): built exactly as stamped, same tile number (the animation needs it).
The user's files are only appended to, as text: the group goes at the end of "layers", the tileset at the end of
"tilesets", layer ids from 21 (below the file's nextlayerid: 100 from tiled_export.py, 30 in older maps); nothing else in the file changes (checked).
Usage: python tiled_frames.py <Map_...> [...] [--dry]
"""
import json, sys
from pathlib import Path
from PIL import Image
sys.path.insert(0, str(Path(__file__).resolve().parent))
import guard   # your saved files are never changed without consent
from layouts import EXPORT, flip, load_tilemap, walk
from tiled_export import tile_art, atlas

PROJECT = Path(__file__).resolve().parents[2]
from project import P
TD = P.tiled
PACK = P.build / "pack"
LAYERS = ("BottomLayer", "GroundLayer", "UpperLayer", "CeilLayer")


def frames_of(mapname):
    """(layer, cell index) -> [frame 2 tid, frame 3 tid, ...] (project tile ids); sheet; width"""
    f = next(Path(EXPORT).glob(f"map_*/Assets/GameAssets/Serial/Res/Map/**/{mapname}/tilemap.json"))
    j = load_tilemap(f)
    ts = j["tilesets"][0]
    anim = {t["id"]: [a["tileid"] for a in t["animation"]] for t in ts.get("tiles", []) if "animation" in t}
    out = {}
    for l in walk(j["layers"]):
        if l["name"] not in LAYERS:
            continue
        for i, g in enumerate(l["data"]):
            if g and (g - 1) in anim:
                seen = []
                for x in anim[g - 1]:
                    if x != g - 1 and x not in seen:
                        seen.append(x)
                if seen:
                    out[(l["name"].replace("Layer", ""), i)] = [flip(x) for x in seen]
    game = Image.open(f.parent.parent / Path(ts["image"]).name).convert("RGBA")
    return out, Path(ts["image"]).stem, j["width"], j["height"], game


def main():
    a = sys.argv[1:]
    dry = "--dry" in a
    for m in [x for x in a if x.startswith("Map_")]:
        tmj = TD / f"{m}.tmj"
        text = tmj.read_text(encoding="utf-8")
        if '"name":"animation frames"' in text:
            print(f"{m}: already has the frames group, skipped"); continue
        doc = json.loads(text)
        fr, sheet, w, h, game = frames_of(m)
        if not fr:
            print(f"{m}: no animated cells, nothing added"); continue
        keys = sorted({t for v in fr.values() for t in v})
        idx = {t: n for n, t in enumerate(keys)}
        first = max(t["firstgid"] + t["tilecount"] for t in doc["tilesets"])
        (TD / "atlas").mkdir(exist_ok=True)
        if not dry:
            guard.need([tmj, TD / "atlas" / f"{m}_frames.png"])   # before anything is written
        size, cols = atlas([tile_art(PACK, sheet, t, game) for t in keys], TD / "atlas" / f"{m}_frames.png") if not dry else ((0, 0), 1)
        ts = {"columns": cols, "firstgid": first, "image": f"atlas/{m}_frames.png", "imageheight": size[1], "imagewidth": size[0],
              "margin": 0, "name": "frames (current)", "spacing": 0, "tilecount": len(keys), "tileheight": 32, "tilewidth": 32,
              "tiles": [{"id": n, "properties": [{"name": "tile", "type": "string", "value": f"{sheet.replace('map_chip_', '')}:{t}"}]}
                        for n, t in enumerate(keys)]}
        layers, lid = [], 22
        for L in ("Bottom", "Ground", "Upper", "Ceil"):
            depth = max((len(v) for (l2, _), v in fr.items() if l2 == L), default=0)
            for k in range(depth):
                data = [0] * (w * h)
                for (l2, i), v in fr.items():
                    if l2 == L and k < len(v):
                        data[i] = first + idx[v[k]]
                layers.append({"data": data, "height": h, "id": lid, "name": f"{L} frame {k + 2}", "opacity": 1,
                               "type": "tilelayer", "visible": True, "width": w, "x": 0, "y": 0})
                lid += 1
        if lid > doc["nextlayerid"]:   # ids 21.. stay below it (tiled_export.py: 100; Karnak's older maps: 30)
            sys.exit(f"{m}: {lid - 22} frame layers do not fit below nextlayerid {doc['nextlayerid']}")
        group = {"id": 21, "layers": layers, "name": "animation frames", "opacity": 1, "type": "group", "visible": False, "x": 0, "y": 0}
        # append as text: end of "layers" = the "}]," right before "nextlayerid"; end of "tilesets" = "}]," before "tilewidth"
        a1, a2 = '\n        }],\n "nextlayerid"', '\n        }],\n "tilewidth"'
        if text.count(a1) == 1 and text.count(a2) == 1:
            # saved by Tiled (its own layout)
            new = text.replace(a1, "\n        }, " + json.dumps(group) + '],\n "nextlayerid"').replace(a2, "\n        }, " + json.dumps(ts) + '],\n "tilewidth"')
            back = new.replace("\n        }, " + json.dumps(group) + "]", "\n        }]").replace("\n        }, " + json.dumps(ts) + "]", "\n        }]")
        elif json.dumps(doc) == text:
            # never saved in Tiled: tiled_export.py's one-line layout, which json.dumps reproduces exactly
            nd2 = dict(doc, layers=doc["layers"] + [group], tilesets=doc["tilesets"] + [ts])
            new = json.dumps(nd2)
            back = json.dumps(dict(json.loads(new), layers=json.loads(new)["layers"][:-1], tilesets=json.loads(new)["tilesets"][:-1]))
        else:
            sys.exit(f"{m}: unexpected file layout, nothing written")
        # check: the old document is the new one minus exactly the appended group and tileset, text included
        nd = json.loads(new)
        assert nd["layers"][:-1] == doc["layers"] and nd["tilesets"][:-1] == doc["tilesets"], m
        assert {k: v for k, v in nd.items() if k not in ("layers", "tilesets")} == {k: v for k, v in doc.items() if k not in ("layers", "tilesets")}, m
        assert back == text, m
        print(f"{m}: {len(fr)} animated cell(s), {len(layers)} frame layer(s) ({', '.join(l['name'] for l in layers)}), {len(keys)} frame tile(s)"
              + (" (dry)" if dry else ""))
        if not dry:
            guard.write_text(tmj, new)


if __name__ == "__main__":
    main()
