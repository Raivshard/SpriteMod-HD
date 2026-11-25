r"""Your saved files are never changed without your consent (user, 2026-11-24: "When I save a file, that is the output.
It cannot be changed without my direct and explicit consent"; "when you or a tool encounter a file that requires consent,
you MUST immediately ask me").

The guard is there to stop the AI assistant, not you ("it's to stop YOU from making changes without my knowledge"):
  - Run by you (Tiled commands, your own terminal): never stops. What you run is what you asked for.
  - Run by Claude Code (its shells set CLAUDECODE): before changing an existing file it checks that the file is exactly
    what these tools last wrote (build\tool records\written_files.json keeps a SHA-256 per file). A file that differs (you saved it,
    painted it, edited it, or it predates this record) STOPS the tool at once: it prints the files and exits with code 3,
    before that file is touched. The assistant must then ask you; --overwrite-saved only after your explicit yes.
Every map tool writes, copies and deletes through this module, so the record stays current either way. New files
(that do not exist yet) need no consent.
"""
import hashlib, json, os, shutil, sys
from pathlib import Path
sys.path.insert(0, str(__import__('pathlib').Path(__file__).resolve().parents[1]))  # tools folder
import paths   # project folder layout (tools\paths.py)

FLAG = "--overwrite-saved"
CONSENT = FLAG in sys.argv
if CONSENT:
    sys.argv.remove(FLAG)
ASSISTANT = bool(os.environ.get("CLAUDECODE"))   # set in Claude Code's shells; not in Tiled or your own terminal
RECORD = paths.TOOL_RECORDS / "written_files.json"
_rec = None


def _load():
    global _rec
    if _rec is None:
        _rec = json.loads(RECORD.read_text(encoding="utf-8")) if RECORD.exists() else {}
    return _rec


def _key(p):
    return os.path.normcase(str(Path(p).resolve()))


def _sha(p):
    return hashlib.sha256(Path(p).read_bytes()).hexdigest()


def mine(p):
    """True if the tools may change p: it does not exist, or it is byte for byte what the tools last wrote"""
    p = Path(p)
    return not p.exists() or _load().get(_key(p)) == _sha(p)


def need(paths, what="change"):
    """Stop (exit 3) before anything is changed if any of paths needs the user's consent"""
    bad = [Path(p) for p in paths if not mine(p)]
    if not bad or CONSENT or not ASSISTANT:
        return
    print(f"\nSTOPPED: NEEDS YOUR CONSENT. This would {what} {len(bad)} file(s) that were saved or edited outside these "
          f"tools since the tools last wrote them (or that the tools never wrote):", flush=True)
    for p in bad[:60]:
        print(f"   {p}")
    if len(bad) > 60:
        print(f"   ... and {len(bad) - 60} more")
    print(f"Nothing in these files was changed. To replace them anyway, run the same command again with {FLAG}.", flush=True)
    sys.exit(3)


def _record(p):
    r = _load()
    r[_key(p)] = _sha(p)
    RECORD.parent.mkdir(parents=True, exist_ok=True)
    RECORD.write_text(json.dumps(r, indent=0, sort_keys=True), encoding="utf-8")


def _forget(p):
    r = _load()
    if r.pop(_key(p), None) is not None:
        RECORD.write_text(json.dumps(r, indent=0, sort_keys=True), encoding="utf-8")


def write_text(p, text):
    p = Path(p)
    need([p])
    p.write_text(text, encoding="utf-8")
    _record(p)


def write_bytes(p, data):
    p = Path(p)
    need([p])
    p.write_bytes(data)
    _record(p)


def save_image(im, p):
    p = Path(p)
    need([p])
    im.save(p)
    _record(p)


def copy(src, dst):
    dst = Path(dst)
    need([dst])
    shutil.copy2(src, dst)
    _record(dst)


def remove(p):
    p = Path(p)
    need([p], "delete")
    p.unlink()
    _forget(p)
