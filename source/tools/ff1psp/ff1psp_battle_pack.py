r"""Build a KupoUI pack that replaces FF1 PR's party battle sprites with the PSP versions (personal use only:
Square Enix art from your own PSP copy; never share the output).

Input: the decoded frames `<ordered>\frames\JOB_ALL\JOBnn_<Class>\NN_<pose>.png` (tools\ff1psp_frames.py) and
their `frames.csv` (anchor). PR: one 32x32 texture per pose, Pivot [0.5, 0] (feet, bottom centre), PPU 1,
`Serial/Res/Chara/Battle/BC_FF1_P0nn/<Pose>_00` [verified, MagiciteExport]. PSP bodies are 48 px tall with the
feet on the bottom row = 2x PR's 24 px [measured], so each frame goes bottom-centred on a 64x64 canvas
(wider, symmetric, only when a pose is wider) with pixelsPerUnit 2 -> the same on-screen size as PR.
P001-P012 = JOB00-0B (same class order, checked by eye). LeftAttack_* is empty in vanilla FF1: not replaced.

Usage: python ff1psp_battle_pack.py <ordered folder> <out folder> [--vanilla <MagiciteExport folder>] [--force]
  writes <out>\GameAssets\Serial\Res\Chara\Battle\BC_FF1_P0nn\<Pose>_00.png + .json (KupoUI sidecar) and
  <out>\preview.png (vanilla PR above, PSP below, per class and pose; needs --vanilla <MagiciteExport folder>).
"""
import csv
import json
import shutil
import sys
from pathlib import Path

from PIL import Image, ImageDraw

# PR pose <- PSP frame (decided by look; see BATTLE_POSES in ff1psp_frames.py)
POSES = {"Default_00": "00_stand", "Ready_00": "01_ready", "RightAttack_00": "01_ready",
         "RightAttack_01": "04_attack", "SkillReady_00": "05_chant1", "SkillReady_01": "06_chant2",
         "Win_00": "07_arm_up", "Dying_00": "08_kneel", "Damage_00": "09_hit", "Down_00": "10_ko"}
SCALE = 2  # PSP pixels per PR pixel


def main():
    a = sys.argv[1:]
    if len(a) < 2 or a[0] in ("-h", "--help"):
        raise SystemExit(__doc__)
    ordered, out = Path(a[0]), Path(a[1])
    vanilla = Path(a[a.index("--vanilla") + 1]) if "--vanilla" in a else None
    if out.exists():
        if "--force" not in a:
            raise SystemExit(f"{out} exists; pass --force")
        if not (out / "GameAssets").exists():
            raise SystemExit(f"{out} is not a folder this tool made, refusing")
        shutil.rmtree(out)
    frames = ordered / "frames"
    anchors = {r["sprite"]: (int(r["anchor x"]), int(r["anchor y"]), int(r["frame height"]))
               for r in csv.DictReader(open(frames / "frames.csv", encoding="utf-8")) if r["folder"] == "JOB_ALL"}
    classes = sorted(p for p in (frames / "JOB_ALL").iterdir() if p.is_dir() and "_S_" not in p.name)
    n = 0
    prev = []
    for k, d in enumerate(classes):
        ax, ay, fh = anchors[d.name]
        dst = out / "GameAssets/Serial/Res/Chara/Battle" / f"BC_FF1_P{k + 1:03d}"
        dst.mkdir(parents=True, exist_ok=True)
        row = []
        for pose, src in POSES.items():
            im = Image.open(d / f"{src}.png").convert("RGBA")
            half = max(32, ax, im.width - ax)  # canvas half-width around the feet
            c = Image.new("RGBA", (2 * half, 32 * SCALE), (0, 0, 0, 0))
            c.alpha_composite(im, (half - ax, c.height - (ay + (fh - ay))))
            c.save(dst / f"{pose}.png")
            (dst / f"{pose}.json").write_text(json.dumps({"pixelsPerUnit": SCALE, "filterMode": "Point"}, indent=2) + "\n")
            n += 1
            row.append((pose, c))
        prev.append((k + 1, row))
    if vanilla:
        cw = 84
        W = Image.new("RGBA", (cw * len(POSES), len(prev) * 140), (90, 90, 110, 255))
        dr = ImageDraw.Draw(W)
        for r, (p, row) in enumerate(prev):
            for j, (pose, c) in enumerate(row):
                v = vanilla / f"bc_ff1_p{p:03d}/Assets/GameAssets/Serial/Res/Chara/Battle/BC_FF1_P{p:03d}/{pose}.png"
                x, y = j * cw + (cw - c.width) // 2, r * 140
                if v.exists():
                    vi = Image.open(v).convert("RGBA").resize((64, 64), Image.NEAREST)
                    W.alpha_composite(vi, (j * cw + (cw - 64) // 2, y))
                W.alpha_composite(c, (x, y + 66))
                if r == 0:
                    dr.text((j * cw + 2, y + 2), pose.replace("_00", "").replace("Attack", "Atk"), fill=(255, 255, 255))
        W.save(out / "preview.png")
    print(f"classes: {len(prev)}; textures: {n}; written to {out}")


if __name__ == "__main__":
    main()
