"""Check MenuLayout.json (and add-ons) against docs/MenuLayout_schema.json, without the jsonschema package.

Usage: python tools/check_menulayout.py [file ...]
Default: the game's MenuLayout.json plus every MenuLayout.<name>.json beside it.
Supports the schema subset the file uses: type, enum, pattern, minimum, minItems/maxItems, properties,
patternProperties, additionalProperties, items, required, anyOf, $ref. Read-only; prints one line per problem.
"""
import json
import re
import os
import sys
from pathlib import Path
sys.path.insert(0, str(__import__('pathlib').Path(__file__).resolve().parent))  # tools folder: games.py
from games import GAME  # --game FF6 picks the game (tools\games.json)

HERE = Path(__file__).resolve().parent
# Game: --game FF6 (tools\games.json; FF5_GAME still overrides the FF5 folder).
GAME_DIR = GAME.mod
# Schema: the project's docs copy, else the one installed beside MenuLayout.json.
SCHEMA = next((p for p in (HERE.parent / "docs" / "MenuLayout_schema.json", GAME_DIR / "MenuLayout_schema.json") if p.exists()),
              GAME_DIR / "MenuLayout_schema.json")

TYPES = {
    "object": lambda v: isinstance(v, dict),
    "array": lambda v: isinstance(v, list),
    "string": lambda v: isinstance(v, str),
    "boolean": lambda v: isinstance(v, bool),
    "number": lambda v: isinstance(v, (int, float)) and not isinstance(v, bool),
    "integer": lambda v: isinstance(v, int) and not isinstance(v, bool),
}


def resolve(root, ref):
    node = root
    for part in ref.lstrip("#/").split("/"):
        node = node[part]
    return node


def validate(v, s, root, path, out):
    if "$ref" in s:
        s = {**resolve(root, s["$ref"]), **{k: x for k, x in s.items() if k != "$ref"}}
        if "$ref" in s:
            return validate(v, s, root, path, out)
    if "anyOf" in s:
        if not any(not validate(v, sub, root, path, []) for sub in s["anyOf"]):
            out.append(f"{path}: value {json.dumps(v)[:60]} fits none of the allowed forms")
        return out
    t = s.get("type")
    if t:
        ts = t if isinstance(t, list) else [t]
        if not any(TYPES[x](v) for x in ts):
            out.append(f"{path}: expected {'/'.join(ts)}, got {json.dumps(v)[:60]}")
            return out
    if "enum" in s and v not in s["enum"]:
        out.append(f"{path}: {json.dumps(v)} is not one of {s['enum']}")
    if isinstance(v, str) and "pattern" in s and not re.search(s["pattern"], v):
        out.append(f"{path}: {json.dumps(v)} does not match {s['pattern']}")
    if TYPES["number"](v) and "minimum" in s and v < s["minimum"]:
        out.append(f"{path}: {v} is below {s['minimum']}")
    if isinstance(v, list):
        if "minItems" in s and len(v) < s["minItems"] or "maxItems" in s and len(v) > s["maxItems"]:
            out.append(f"{path}: wrong number of items ({len(v)})")
        if "items" in s:
            for i, x in enumerate(v):
                validate(x, s["items"], root, f"{path}[{i}]", out)
    if isinstance(v, dict):
        props = s.get("properties", {})
        pats = s.get("patternProperties", {})
        for r in s.get("required", []):
            if r not in v:
                out.append(f"{path}: missing '{r}'")
        for k, x in v.items():
            label = f"{path}.{k}"
            if k in props:
                validate(x, props[k], root, label, out)
                continue
            pat = next((p for p in pats if re.search(p, k)), None)
            if pat is not None:
                validate(x, pats[pat], root, label, out)
                continue
            ap = s.get("additionalProperties", True)
            if ap is False:
                close = [p for p in props if p.lower() == k.lower()]
                hint = f" (capitals matter: '{close[0]}')" if close else ""
                out.append(f"{path}: unknown key '{k}'{hint}")
            elif isinstance(ap, dict):
                validate(x, ap, root, label, out)
    return out


def name_of(doc, path):
    """'elements[12]' -> 'elements[12] "rule name"' for readable output."""
    m = re.match(r"\$\.(elements|screens|textStyles)\[(\d+)\]", path)
    if m:
        item = doc.get(m.group(1), [])[int(m.group(2))]
        if isinstance(item, dict) and isinstance(item.get("name"), str):
            return path.replace(m.group(0), f'{m.group(0)} "{item["name"]}"', 1)
    return path


def main():
    files = [Path(a) for a in sys.argv[1:]]
    if not files:
        files = [GAME_DIR / "MenuLayout.json"] + sorted(
            p for p in GAME_DIR.glob("MenuLayout.*.json") if p.name.lower() != "menulayout.json")
    # a schema beside the first file wins (an installed copy matches its plugin)
    beside = files[0].resolve().parent / "MenuLayout_schema.json"
    schema_path = beside if beside.exists() else SCHEMA
    if not schema_path.exists():
        print(f"schema not found: {schema_path} (pick the game with --game, or pass a file that has the schema beside it)")
        return 2
    schema = json.loads(schema_path.read_text(encoding="utf-8"))
    total = 0
    for f in files:
        try:
            doc = json.loads(f.read_text(encoding="utf-8-sig"))
        except Exception as e:
            print(f"{f.name}: unreadable: {e}")
            total += 1
            continue
        problems = validate(doc, schema, schema, "$", [])
        if f.name.lower() != "menulayout.json" and isinstance(doc, dict):
            main_only = [k for k in doc if not k.startswith("_") and k not in ("$schema", "screens", "elements", "textStyles", "styles")
                         and k in schema["properties"]]
            problems += [f"$: '{k}' only works in MenuLayout.json" for k in main_only]
        for p in problems:
            print(f"{f.name}: {name_of(doc, p)}")
        print(f"{f.name}: {len(problems)} problem(s)")
        total += len(problems)
    return 1 if total else 0


if __name__ == "__main__":
    sys.exit(main())
